namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The AI analyser gets a right-sized copy of the photograph; the listing keeps the photograph.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24, "how do we speed this up — ai analizer". Measured on the owner's own capture: a phone
/// photo is 3024x4032 and 2,009,014 bytes, which base64 inflates to 2.7 MB. Their uplink is ~20 Mbps
/// with the Google Drive backup holding 12 of it, so that was ~2.7 seconds of pure upload before the
/// model saw a pixel — the single largest cost in the round trip.
/// </para>
/// <para>
/// And two thirds of it was discarded on arrival. Claude reads an image in 28x28 patches with a
/// per-tier cap; Fable 5 is high-resolution tier (2576 px long edge, 4784 patches) and anything
/// larger is downscaled server-side before processing. A 3024x4032 frame asks for 15,552 patches
/// against a cap of 4,784. Scaling client-side to the largest size that still saturates the cap
/// keeps every pixel the model can actually use and drops ~70% of the bytes.
/// </para>
/// <para>
/// The invariant these tests exist for is not the speed, it is the COPY. <c>nlImageBase64</c> is
/// also what renders Picture 1 of the listing when no library photo claims that slot, so shrinking
/// the variable rather than the request would quietly turn the seller's lead photo into a re-encode
/// — the same failure c2af147 removed when auto-cutout was overwriting Picture 1, arriving through
/// a different door.
/// </para>
/// </remarks>
public class AnalyzerUploadSizeTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void The_request_carries_a_copy_and_the_variable_keeps_the_photograph()
    {
        // The whole safety property in one line: what is sent is `sending`, not nlImageBase64.
        var call = Slice(Js, "const sending = await nlAnalysisCopy(", "timeoutMs: AI_TIMEOUT_MS");
        Assert.Contains("imageBase64: sending.base64", call, StringComparison.Ordinal);
        Assert.DoesNotContain("imageBase64: nlImageBase64", call, StringComparison.Ordinal);

        // And the resizer never writes the variable back.
        var body = Slice(Js, "async function nlAnalysisCopy(", "  async function nlAnalyze()");
        Assert.DoesNotContain("nlImageBase64 =", body, StringComparison.Ordinal);
    }

    [Fact]
    public void It_sizes_to_the_tier_the_model_actually_reads()
    {
        // Both caps, because the patch budget binds before the long edge on a tall phone frame:
        // 2576 px alone would leave a 1932x2576 image asking 6,348 patches against a cap of 4,784.
        Assert.Contains("AI_MAX_LONG_EDGE = 2576", Js, StringComparison.Ordinal);
        Assert.Contains("AI_MAX_PATCHES   = 4784", Js, StringComparison.Ordinal);
        Assert.Contains("AI_PATCH         = 28", Js, StringComparison.Ordinal);

        var body = Slice(Js, "async function nlAnalysisCopy(", "  async function nlAnalyze()");
        Assert.Contains("AI_MAX_LONG_EDGE", body, StringComparison.Ordinal);
        Assert.Contains("AI_MAX_PATCHES", body, StringComparison.Ordinal);
    }

    [Fact]
    public void A_photo_already_inside_the_caps_is_sent_untouched()
    {
        // Re-encoding a small photo would spend a JPEG generation to save nothing, and the vision
        // guidance is explicit that repeated compression passes damage text legibility — which is
        // the model number this app is trying to read.
        var body = Slice(Js, "async function nlAnalysisCopy(", "  async function nlAnalyze()");
        Assert.Contains("<= AI_MAX_LONG_EDGE && aiPatchesFor(w, h) <= AI_MAX_PATCHES", body, StringComparison.Ordinal);
        Assert.Contains("return { base64, mime };", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Quality_stays_high_enough_to_read_a_model_number()
    {
        // 0.92, deliberately. These frames are already one JPEG generation deep from the phone, and
        // a cheaper setting saves bytes by blurring exactly the small text the analysis depends on.
        var body = Slice(Js, "async function nlAnalysisCopy(", "  async function nlAnalyze()");
        Assert.Contains("toDataURL('image/jpeg', 0.92)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Resizing_can_never_be_the_reason_an_analysis_does_not_happen()
    {
        // Every failure path hands back the original bytes: a decode error, a canvas that is not
        // available, or a re-encode that came out larger than what it replaced.
        var body = Slice(Js, "async function nlAnalysisCopy(", "  async function nlAnalyze()");
        Assert.Contains("catch", body, StringComparison.Ordinal);
        Assert.Contains("out.length >= base64.length", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 163);
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in app.js.");
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\" in app.js.");
        return text[a..b];
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository root above {AppContext.BaseDirectory}");
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
