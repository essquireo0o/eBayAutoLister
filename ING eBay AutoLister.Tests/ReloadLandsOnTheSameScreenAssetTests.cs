namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Two requests that sound opposed and aren't. August: "the app opens on the dashboard, always" — a
/// hash left over from last session must not hijack a fresh open. 2026-08-20: "when I refresh it
/// goes to the homepage — have it go to the page I am on." The browser tells the two apart by
/// navigation type, and these pin that the startup code asks it.
/// </summary>
public class ReloadLandsOnTheSameScreenAssetTests
{
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void A_refresh_reopens_the_screen_that_was_on_the_page()
    {
        // 'reload' is F5 on a screen somebody was using; back/forward is the same promise.
        Assert.Contains("performance.getEntriesByType?.('navigation')?.[0]?.type", Js);
        Assert.Contains("(navType === 'reload' || navType === 'back_forward')", Js);
        // It goes through the router, so the screen opens as a proper workspace tab — never by
        // un-hiding a section, which would leave it with no tab and no way back.
        Assert.Contains("handleNav(refreshedPage);", Js);
        // And only for a page the router knows: a stale or mistyped hash cannot open anything.
        Assert.Contains("&& WORKSPACE_PAGES[refreshedPage]", Js);
    }

    [Fact]
    public void A_fresh_open_still_lands_on_the_dashboard()
    {
        // The August promise survives: anything that is not a reload clears the stale hash.
        Assert.Contains("} else if (location.hash && location.hash !== '#dashboard') {", Js);
        Assert.Contains("history.replaceState(null, '', location.pathname + location.search);", Js);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
