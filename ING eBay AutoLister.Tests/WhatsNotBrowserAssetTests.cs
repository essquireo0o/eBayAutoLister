namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The other half of the WhatsNot tab: the panel that actually shows the live feed. It is HTML, CSS
/// and JavaScript, so nothing in C# notices when a navigation button loses its handler, the panel
/// forgets the address it was left on, or — the one that matters most — the screen goes back to
/// leaving a blank frame unexplained.
///
/// Two of these are decisions rather than plumbing, and both are easy to "tidy" into their
/// opposite: Back and Forward are this panel's own list of loaded addresses and must not claim to
/// be the frame's history, and a refused embed must always be reported as the site's decision with
/// a way in, never as an error in this app.
/// </summary>
public class WhatsNotBrowserAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    [Fact]
    public void The_panel_has_back_forward_reload_and_a_way_home()
    {
        foreach (var id in new[] { "wn-back", "wn-forward", "wn-reload", "wn-feed-home", "wn-url", "wn-go" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains("on('wn-back', 'click', () => wnGo(-1));", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-forward', 'click', () => wnGo(1));", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-reload', 'click'", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-feed-home', 'click'", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reload re-runs the address WITHOUT pushing it again — a reload that appended a history entry
    /// would make Back mean "reload the same page", which is the one thing Back must never do.
    /// </summary>
    [Fact]
    public void Reload_does_not_grow_the_history()
    {
        Assert.Contains("wnLoad(wnHistory[wnHistoryAt] || $('wn-url')?.value, { push: false })", Js, StringComparison.Ordinal);
        Assert.Contains("wnLoad(wnHistory[next], { push: false });", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new address truncates whatever was ahead of it, exactly like a browser. Without this the
    /// Forward button walks into a page the seller never navigated to from here.
    /// </summary>
    [Fact]
    public void A_new_address_truncates_the_forward_entries()
    {
        Assert.Contains("wnHistory = wnHistory.slice(0, wnHistoryAt + 1);", Js, StringComparison.Ordinal);
        Assert.Contains("function wnUpdateNav()", Js, StringComparison.Ordinal);
        Assert.Contains("back.disabled = wnHistoryAt <= 0", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The buttons must not claim to be the frame's own history. A page cannot read or move where a
    /// cross-origin frame has navigated, and a Back button that silently means something else is
    /// worse than no Back button.
    /// </summary>
    [Fact]
    public void The_panel_does_not_pretend_to_drive_the_frames_own_history()
    {
        Assert.Contains("the previous address this panel loaded", Html, StringComparison.Ordinal);

        // contentWindow.history / contentDocument are the cross-origin lies this would be built on.
        var panel = Section(Js, "// ── WhatsNot: embedded live-feed browser", "// ── WhatsNot: the live-auction arbitrage card");
        foreach (var forbidden in new[] { "contentWindow.history", "contentDocument", "contentWindow.location" })
            Assert.DoesNotContain(forbidden, panel, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_feed_and_the_recent_ones_are_remembered()
    {
        Assert.Contains("const WN_URL_KEY = 'whatsnotFeedUrl';", Js, StringComparison.Ordinal);
        Assert.Contains("const WN_RECENT_KEY = 'whatsnotRecentUrls';", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-recent\"", Html, StringComparison.Ordinal);
        Assert.Contains("list=\"wn-recent\"", Html, StringComparison.Ordinal);
        // Restored into the box when the tab is opened, or remembering it changes nothing.
        Assert.Contains("const remembered = wnLastUrl();", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Storage is unavailable in private mode and a throw there would take the whole panel down
    /// with it. Remembering an address is a convenience; loading the feed is the feature.
    /// </summary>
    [Fact]
    public void Losing_local_storage_never_takes_the_panel_with_it()
    {
        var save = Section(Js, "function wnSaveLastUrl(url) {", "function wnRecentUrls()");
        Assert.Contains("catch", save, StringComparison.Ordinal);
        Assert.Contains("function wnAddRecent(url) {", Js, StringComparison.Ordinal);

        var read = Section(Js, "function wnRecentUrls() {", "function wnRenderRecents()");
        Assert.Contains("catch { return []; }", read, StringComparison.Ordinal);
    }

    // ── The blank frame ───────────────────────────────────────────────────────

    /// <summary>
    /// The reason this panel is more than an iframe. A refused embed is blocked before anything
    /// renders and the page is told nothing, so the answer has to come from the site's headers,
    /// fetched by the app.
    /// </summary>
    [Fact]
    public void The_panel_asks_the_site_whether_it_allows_being_embedded()
    {
        Assert.Contains("app.MapGet(\"/api/whatsnot/embed-check\"", Program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<FrameEmbedPolicy>();", Program, StringComparison.Ordinal);
        Assert.Contains("fetch('/api/whatsnot/embed-check?url=' + encodeURIComponent(url))", Js, StringComparison.Ordinal);
        Assert.Contains("wnCheckEmbed(target);", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// A refusal is reported as the site's decision AND with the way in. A seller who reads only
    /// "blocked" concludes this app is broken and stops opening the screen that does work.
    /// </summary>
    [Fact]
    public void A_refused_embed_says_whose_decision_it_was_and_what_still_works()
    {
        Assert.Contains("id=\"wn-blocked\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-blocked-open\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('wn-blocked-open', 'click', openInBrowser);", Js, StringComparison.Ordinal);
        Assert.Contains("check.status === 'refused'", Js, StringComparison.Ordinal);
        Assert.Contains("wnShowBlocked(check);", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-blocked", Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// A blocked frame fires its load event too. Treating that as success is exactly the wrong
    /// conclusion — it is the bug this whole check exists to replace.
    /// </summary>
    [Fact]
    public void The_frames_load_event_is_never_read_as_the_embed_having_worked()
    {
        Assert.Contains("A blocked embed fires load too", Js, StringComparison.Ordinal);
        var handler = Section(Js, "$('wn-frame')?.addEventListener('load'", "const remembered = wnLastUrl();");
        Assert.DoesNotContain("wn-status-ok", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed", handler, StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdict for an address the seller has already navigated away from is noise, and worse,
    /// it can contradict what is on screen. Late answers are dropped.
    /// </summary>
    [Fact]
    public void A_late_answer_about_a_previous_address_is_dropped()
    {
        Assert.Contains("const token = ++wnCheckToken;", Js, StringComparison.Ordinal);
        Assert.Contains("if (token !== wnCheckToken) return;", Js, StringComparison.Ordinal);
    }

    /// <summary>The three verdicts all have a state on screen, or one of them renders as silence.</summary>
    [Fact]
    public void Every_verdict_has_somewhere_to_show()
    {
        foreach (var state in new[] { ".wn-status-ok", ".wn-status-refused", ".wn-status-unknown" })
            Assert.Contains(state, Css, StringComparison.Ordinal);

        Assert.Contains("id=\"wn-status\"", Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The endpoint reads headers and nothing else. A body would make the panel's little
    /// convenience check into a page fetcher pointed anywhere the address bar can reach.
    /// </summary>
    [Fact]
    public void The_check_reads_headers_and_never_a_page_body()
    {
        var service = ReadSource(Path.Combine("Services", "FrameEmbedPolicy.cs"));

        Assert.Contains("HttpCompletionOption.ResponseHeadersRead", service, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "ReadAsStringAsync", "ReadAsStreamAsync", "ReadAsByteArrayAsync" })
            Assert.DoesNotContain(forbidden, service, StringComparison.Ordinal);
    }

    /// <summary>
    /// The arbitrage card and the sold-comps path underneath it are untouched by any of this. The
    /// browser panel is the container; the card is the feature, and it still has to be wired.
    /// </summary>
    [Fact]
    public void The_card_and_the_sold_comps_path_are_left_alone()
    {
        Assert.Contains("app.MapPost(\"/api/whatsnot/bid\"", Program, StringComparison.Ordinal);
        Assert.Contains("safePost('/api/whatsnot/bid', {", Js, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/api/sold-comps\"", Program, StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find \"{to}\" after \"{from}\"");
        return text[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", relativePath));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
