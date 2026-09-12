namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The photo editor opens in an iframe, and the iframe used to load a bare "/editor.html". Every
/// other asset on the page is versioned (style.css?v=NNN), so only the editor could go stale: after
/// the app updated itself, a browser could keep serving the editor it had cached, and clicking
/// Rotate, Adjust or any tool in that old copy did nothing — reported 2026-09-12 as "rotate does not
/// work, none of these features work". The fix stamps the iframe src with the running build so it
/// refetches across an update; this keeps the stamp from being dropped again.
/// </summary>
public class EditorCacheBustTests
{
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void The_editor_iframe_is_cache_busted_so_an_update_cannot_leave_a_stale_editor()
    {
        // The bare, un-versioned src is the bug; it must not come back.
        Assert.DoesNotContain("iframe.src = '/editor.html';", Js);
        // Stamped with the build the page is running, which changes on every update.
        Assert.Contains("iframe.src = '/editor.html?v=' + (seenBuild || Date.now());", Js);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing asset: " + path);
        return File.ReadAllText(path);
    }
}
