using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The session has to travel in a browser profile, not a JSON file replayed into a fresh browser.
/// </summary>
/// <remarks>
/// Terapeak kept dropping. The login saved <c>storageState</c> and every scrape did
/// <c>newContext({ storageState })</c> - which hands eBay its own session cookie from a browser it
/// has never seen: new fingerprint, no history, no localStorage. That is indistinguishable from a
/// stolen cookie, so eBay challenged or killed it, and the seller was asked to log in again.
///
/// Both halves must open the same profile directory. These assert the generated script, because the
/// failure is invisible in C# - it lives inside a JavaScript string handed to node.
/// </remarks>
public class TerapeakProfileTests
{
    private static readonly string LoginJs =
        TerapeakService.BuildLoginScript("C:\\\\pw\\\\playwright", "C:\\\\app\\\\terapeak-session.json",
                                         "C:\\\\app\\\\terapeak-profile");

    [Fact]
    public void The_login_opens_a_persistent_profile()
    {
        Assert.Contains("launchPersistentContext", LoginJs);
        Assert.Contains("terapeak-profile", LoginJs);
    }

    [Fact]
    public void The_login_no_longer_starts_a_throwaway_browser()
    {
        // chromium.launch(...) + newContext() is the pattern that loses the session. If it comes
        // back, this fails rather than the seller discovering it a week later.
        Assert.DoesNotContain("chromium.launch(", LoginJs);
    }

    [Fact]
    public void The_profile_is_not_the_sellers_own_chrome()
    {
        // This app drives a browser; it must never open the profile someone browses with. A crash
        // or a stray automation flag in there is their real browsing, not ours.
        Assert.DoesNotContain("User Data", LoginJs);
        Assert.DoesNotContain("Google\\\\Chrome", LoginJs);
    }

    [Fact]
    public void The_connected_marker_is_still_written()
    {
        // IsConnected and ConnectionDoctor both read the storageState file. The cookies no longer
        // travel in it, but removing it would make the app believe it was never connected.
        Assert.Contains("terapeak-session.json", LoginJs);
        Assert.Contains("storageState", LoginJs);
    }
}
