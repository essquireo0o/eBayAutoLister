namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A photograph is one file per version, not one file per attempt.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24: "AI enhance is fucked up and its saving tons of pictures." Measured on the owner's
/// library — 73 files in <c>photo-box</c>, five of them the same 227,936 bytes, and rows of
/// near-identical thumbnails they were deleting by hand one at a time.
/// </para>
/// <para>
/// Every treatment (AI Enhance, Cut out, Portrait) POSTs the picture and gets a NEW library file
/// back, then swaps it into the filmstrip. The file it swapped OUT was never removed, so each
/// retouch left its predecessor behind: enhance one shot five times and five files land, identical
/// to the byte whenever the treatment is deterministic. Nothing was wrong with the saving — every
/// call did exactly what it was told; nothing was ever told to clean up.
/// </para>
/// <para>
/// The one thing these tests exist to protect is the exception: an ORIGINAL capture is never
/// superseded. It is the photograph the seller actually took, it is what "Save to my computer"
/// exports, and the first enhance of an untouched photo has to keep both. A tidy-up that eats the
/// original would be a far worse bug than the mess it cleans.
/// </para>
/// </remarks>
public class PhotoTreatmentsDoNotPileUpTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void Superseding_is_defined_and_deletes_only_a_derived_file()
    {
        Assert.Contains("function pbSupersede(", Js, StringComparison.Ordinal);

        // The guard IS the safety property. Without it the call deletes whatever it was handed,
        // which on a first enhance is the seller's untouched photograph.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        Assert.Contains("oldUrl !== original", body, StringComparison.Ordinal);
        Assert.Contains("pbForgetLibraryFile(oldUrl)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_treatment_that_replaces_a_picture_supersedes_the_one_it_replaced()
    {
        // Four sites produce a replacement picture: the desktop shutter's auto-enhance, the phone
        // shutter's auto-enhance, the per-photo Enhance button, and pbRework (Cut out / Portrait).
        // A fifth treatment added later without this call is a fifth way to fill the library up.
        var calls = Occurrences(Js, "pbSupersede(");
        Assert.True(calls >= 5, // one definition + four call sites
            $"expected pbSupersede to be called at all four treatment sites; found {calls - 1}.");
    }

    [Fact]
    public void The_original_capture_stays_reachable_behind_whatever_replaced_it()
    {
        // Save to my computer exports the untouched frame first, and it finds it through this map.
        // If superseding dropped the chain, Save would start exporting only the studio crop --
        // which is the failure that would have shipped with the Save button in the first place.
        var body = Slice(Js, "function pbSupersede(", "async function pbForgetLibraryFile");
        Assert.Contains("pbOriginalOf.get(oldUrl) || oldUrl", body, StringComparison.Ordinal);
        Assert.Contains("pbOriginalOf.set(newUrl, original)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_tidy_up_can_never_fail_a_treatment_the_seller_can_already_see()
    {
        // Fire-and-forget. The picture is on screen and in the library before this runs; a 404 on
        // the cleanup must not surface as "your enhancement failed".
        var body = Slice(Js, "async function pbForgetLibraryFile", "// ── Getting the photograph out");
        Assert.Contains("try {", body, StringComparison.Ordinal);
        Assert.Contains("catch", body, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 161);
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in app.js.");
        var b = text.IndexOf(to, a, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\" in app.js.");
        return text[a..b];
    }

    private static int Occurrences(string text, string needle)
    {
        int n = 0, i = 0;
        while ((i = text.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
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
