namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A phone that is sending photographs is reported as sending, not as absent.
/// </summary>
/// <remarks>
/// <para>
/// 2026-08-24, owner: "using my phone at all no longer works — this has been an ongoing issue."
/// Measured on the running build: two real photographs POSTed to <c>/p/{token}/photo</c> both
/// returned 200 and saved, and <c>/api/photobox/phone/status</c> still answered
/// <c>phoneConnected: false</c>. The panel read "Waiting for the phone to open the page" while the
/// phone was actively uploading.
/// </para>
/// <para>
/// The cause was structural rather than a fault: <c>PhoneConnected</c> requires
/// <c>_phoneEverConnected</c>, which only the live HTTPS camera page sets — through its page load,
/// its poll, its capability report and its preview stream. The certificate-free <c>/c</c> page has
/// none of those; it is a file input, by design, with no live channel at all. And that page is now
/// the default the QR leads to, so the ordinary path was the one that could never report itself.
/// </para>
/// <para>
/// The fix is NOT to set the connected flag from the upload handler, and this file exists partly to
/// stop that being done later. <c>PhoneConnected</c> is what enables the desktop shutter, and the
/// shutter works by setting a command the phone collects by polling — which the <c>/c</c> page
/// never does. Flipping the flag would light up a Snap button that then times out on "the phone
/// didn't send a photo in time". A button that lies is worse than a panel that under-reports, so
/// there is a separate <c>PhoneSending</c> state and Snap stays disabled with the reason on screen.
/// </para>
/// </remarks>
public class PhonePanelSendingStateTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void A_sending_phone_is_not_described_as_one_that_never_arrived()
    {
        var block = Slice(Js, "state.textContent = st.phoneConnected", "state.className = 'pb-phone-state");
        Assert.Contains("st.phoneSending", block, StringComparison.Ordinal);
        Assert.Contains("Your phone is sending photos", block, StringComparison.Ordinal);
    }

    [Fact]
    public void Sending_reads_as_working_rather_than_as_a_problem()
    {
        // The class drives the colour. Amber "busy" beside photographs landing in the strip is the
        // panel disagreeing with the evidence next to it.
        var line = Slice(Js, "state.className = 'pb-phone-state", ";");
        Assert.Contains("st.phoneSending", line, StringComparison.Ordinal);

        var dot = Slice(Js, "$('pb-connect-dot')?.classList.toggle('is-live'", ";");
        Assert.Contains("st.phoneSending", dot, StringComparison.Ordinal);
    }

    [Fact]
    public void The_shutter_stays_disabled_and_the_panel_says_why()
    {
        // Snap is only ever enabled inside the phoneConnected branch. If a later change enables it
        // for a sending phone, the button will sit and then time out — the failure this whole
        // state exists to avoid.
        var enable = Slice(Js, "if (st.phoneConnected) {", "} else {");
        Assert.Contains("['pb-snap', 'pb-burst'].forEach(id => $(id)?.removeAttribute('disabled'))",
                        enable, StringComparison.Ordinal);

        var elseBranch = Slice(Js, "if (status) status.textContent = st.phoneSending", "async function pbPhoneRefresh");
        Assert.DoesNotContain("removeAttribute('disabled')", elseBranch, StringComparison.Ordinal);
        // And it explains itself rather than leaving two grey buttons to be interpreted.
        Assert.Contains("need the one-time iPhone setup", elseBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_made_to_fetch_the_changed_script()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 165);
    }

    private static string Slice(string text, string from, string to)
    {
        var a = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(a >= 0, $"\"{from}\" is no longer in app.js.");
        var b = text.IndexOf(to, a + from.Length, StringComparison.Ordinal);
        Assert.True(b > a, $"\"{to}\" no longer follows \"{from}\".");
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
