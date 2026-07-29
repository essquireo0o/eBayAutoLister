using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The workspace tab bar is the app's only router: a sidebar click opens or focuses a tab, and
/// the tab's registry entry says which section to show. Nothing type-checks that wiring, and the
/// failure it produces is silent — a sidebar entry with no registry entry does not error, it just
/// falls back to the Dashboard, so the new feature appears to do nothing when clicked.
///
/// These lock the three lists that have to agree: the sidebar's data-page values, the
/// WORKSPACE_PAGES registry in app.js, and the section ids in index.html.
/// </summary>
public class WorkspaceTabsAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void EverySidebarEntryOpensAWorkspaceTab()
    {
        var missing = SidebarPages().Except(RegisteredPages()).ToList();

        Assert.True(missing.Count == 0,
            "sidebar entries with no WORKSPACE_PAGES entry (clicking them falls back to the " +
            $"Dashboard instead of opening the feature): {string.Join(", ", missing)}");
    }

    /// <summary>
    /// Pages deliberately taken out of the sidebar at the seller's request, which still work on
    /// their own URL. Only the nav buttons were removed — the screens, routes and endpoints are
    /// untouched, so a deep link still opens them; the tab just falls back to its route key for a
    /// title. Put a button back and the entry comes out of this list.
    /// </summary>
    private static readonly string[] HiddenByRequest =
        ["inventory", "lots", "relist", "trends", "wheretosell", "snipe"];

    [Fact]
    public void EveryWorkspaceTabCanLabelItself()
    {
        // Title and icon normally come off the sidebar button, which is why renaming a feature in
        // the nav renames its tab with nothing to keep in sync. A page deliberately kept OUT of the
        // sidebar has to carry its own, or it opens as a tab labelled with the raw route key
        // ("wheretosell") wearing the Dashboard glyph. Either source is fine; neither is not.
        var orphans = RegisteredPages()
            .Except(SidebarPages())
            .Except(PagesWithTheirOwnTitle())
            .ToList();

        Assert.True(orphans.Count == 0,
            "registered pages with neither a sidebar entry nor a title of their own, so their tab " +
            $"would be labelled with the route key: {string.Join(", ", orphans)}");
    }

    [Fact]
    public void APageHiddenFromTheSidebarBringsItsOwnTitleAndIcon()
    {
        // The specific promise made when these were taken out of the nav: still reachable, and
        // still properly labelled when reached.
        var titled = PagesWithTheirOwnTitle().ToHashSet();
        var unlabelled = HiddenByRequest.Where(p => !titled.Contains(p)).ToList();
        Assert.True(unlabelled.Count == 0,
            $"hidden pages with no title of their own: {string.Join(", ", unlabelled)}");

        var iconless = HiddenByRequest.Where(p => !HasOwnIcon(p)).ToList();
        Assert.True(iconless.Count == 0,
            $"hidden pages whose tab would show the Dashboard glyph: {string.Join(", ", iconless)}");
    }

    [Fact]
    public void ThePagesHiddenOnPurposeAreStillRegisteredAndStillReachable()
    {
        // The other half of the bargain: hiding a nav button must not quietly delete the feature.
        // If one is ever really removed, this fails and the allowlist gets trimmed with it, rather
        // than silently permitting an orphan that no longer exists.
        var registered = RegisteredPages().ToHashSet();
        var vanished = HiddenByRequest.Where(p => !registered.Contains(p)).ToList();

        Assert.True(vanished.Count == 0,
            "pages on the hidden-by-request list are no longer registered at all, so they are not "
            + $"reachable by URL either — drop them from the list: {string.Join(", ", vanished)}");
    }

    [Fact]
    public void EveryRegisteredSectionExistsInTheMarkup()
    {
        var missing = RegisteredSections()
            .Where(id => !Html.Contains($"id=\"{id}\"", StringComparison.Ordinal))
            .ToList();

        Assert.True(missing.Count == 0,
            $"WORKSPACE_PAGES points at section ids that are not in index.html: {string.Join(", ", missing)}");
    }

    [Fact]
    public void EveryOverlaySectionIsAWorkspaceSection()
    {
        // hideOverlaySections() is what clears the screen before another one shows. A section it
        // hides but the registry doesn't know about can be hidden and never shown again.
        var stray = OverlaySections().Except(RegisteredSections()).ToList();

        Assert.True(stray.Count == 0,
            $"OVERLAY_SECTIONS entries missing from WORKSPACE_PAGES: {string.Join(", ", stray)}");
    }

    [Fact]
    public void TheAiListingModalIsHiddenWithTheOtherScreens()
    {
        // It is a screen like any other now. Left out of the list, a draft stays on top of
        // whatever the seller opens next.
        Assert.Contains("new-listing-overlay", OverlaySections());
    }

    [Fact]
    public void TheTabBarIsAKeyboardTablist()
    {
        var bar = Regex.Match(Html, @"<div id=""ws-tab-bar""[^>]*>");
        Assert.True(bar.Success, "the workspace tab bar is gone from index.html");
        Assert.Contains(@"role=""tablist""", bar.Value);
        Assert.Contains("aria-label=", bar.Value);

        // Arrow-key movement and per-tab close are the whole keyboard story; without these the
        // bar is a mouse-only control sitting in front of every feature in the app.
        Assert.Contains("'ArrowRight'", Js);
        Assert.Contains("'ArrowLeft'", Js);
        Assert.Contains("aria-selected", Js);
    }

    [Fact]
    public void TheBarOnlyTakesRoomWhenSomethingIsOpenBesideTheDashboard()
    {
        var css = ReadAsset("style.css");

        Assert.Contains("--ws-tabbar-h: 0px", css);
        Assert.Contains("body.ws-tabs-open", css);
        // Every fixed, full-viewport screen has to move down by the bar's height, or the bar
        // covers that screen's own header.
        Assert.Matches(@"body\.ws-tabs-open[^{]*\.opportunity-overlay", css);
        Assert.Contains("ws-tabs-open", Js);
    }

    private static List<string> SidebarPages() =>
        Regex.Matches(Html, @"class=""nav-item[^""]*""[^>]*data-page=""(\w+)""")
             .Select(m => m.Groups[1].Value)
             .Distinct()
             .OrderBy(p => p, StringComparer.Ordinal)
             .ToList();

    private static List<string> RegisteredPages() =>
        RegistryBody()
            .Split('\n')
            .Select(line => Regex.Match(line, @"^\s*(\w+):\s*\{"))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

    /// <summary>Pages carrying a <c>title:</c> on their own registry entry, rather than borrowing the nav's.</summary>
    private static List<string> PagesWithTheirOwnTitle() =>
        RegistryEntries()
            .Where(e => Regex.IsMatch(e.Body, @"\btitle:\s*'"))
            .Select(e => e.Page)
            .ToList();

    private static bool HasOwnIcon(string page) =>
        RegistryEntries().Any(e => e.Page == page && Regex.IsMatch(e.Body, @"\bicon:\s*'#"));

    /// <summary>
    /// Each top-level entry in WORKSPACE_PAGES and its body. Split on the entry key rather than on
    /// braces, because an entry spans several lines and contains function references.
    /// </summary>
    private static List<(string Page, string Body)> RegistryEntries()
    {
        var body = RegistryBody();
        var starts = Regex.Matches(body, @"^\s{4}(\w+):\s*\{", RegexOptions.Multiline).ToList();

        return starts.Select((m, i) =>
        {
            var from = m.Index;
            var to = i + 1 < starts.Count ? starts[i + 1].Index : body.Length;
            return (m.Groups[1].Value, body[from..to]);
        }).ToList();
    }

    private static List<string> RegisteredSections() =>
        Regex.Matches(RegistryBody(), @"section:\s*'([\w-]+)'")
             .Select(m => m.Groups[1].Value)
             .Distinct()
             .ToList();

    private static string RegistryBody()
    {
        var start = Js.IndexOf("const WORKSPACE_PAGES = {", StringComparison.Ordinal);
        Assert.True(start >= 0, "WORKSPACE_PAGES is no longer a literal object in app.js");
        var end = Js.IndexOf("\n  };", start, StringComparison.Ordinal);
        Assert.True(end > start, "WORKSPACE_PAGES is no longer closed at the top level");
        return Js[start..end];
    }

    private static List<string> OverlaySections()
    {
        var match = Regex.Match(Js, @"const OVERLAY_SECTIONS = \[([^\]]*)\]");
        Assert.True(match.Success, "OVERLAY_SECTIONS is no longer a literal array");
        return Regex.Matches(match.Groups[1].Value, @"'([\w-]+)'")
                    .Select(m => m.Groups[1].Value)
                    .ToList();
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
