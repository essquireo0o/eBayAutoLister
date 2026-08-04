using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// "Watch for one" is the only thing on the Roll the Dice board that survives the next roll, and
/// every part of it that can break is HTML and JavaScript, where nothing in C# notices: the button
/// vanishing, the ceiling quoting the wrong price, the watched state forgetting itself on a re-sort,
/// or an eBay-derived product title reaching innerHTML unescaped.
///
/// Two of these lock decisions rather than plumbing — the board asks the SERVER whether a play can
/// be watched instead of re-deriving the evidence bar, and the ceiling is the target price rather
/// than the break-even. Both are the sort of thing a later tidy-up reverses without knowing why.
/// </summary>
public class WatchPlayAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    private static string PlayHtml => Section(Js, "function dicePlayHtml(play, index)", "function huntPlayLocally");

    [Fact]
    public void The_button_appears_on_a_play_only_when_the_server_says_it_can_be_watched()
    {
        // Not a client-side copy of "five comps and 50 confidence". Two places deciding the same
        // thing drift, and the drift shows up as a button that the endpoint then refuses.
        Assert.Contains("play.canWatch", PlayHtml, StringComparison.Ordinal);
        Assert.Contains("dice-watch-btn", PlayHtml, StringComparison.Ordinal);

        // The evidence thresholds must not be re-stated in the browser at all.
        Assert.DoesNotContain("soldCompCount >=", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("confidenceScore >=", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ceiling_it_offers_is_the_target_price_not_the_break_even()
    {
        // Paying the break-even earns nothing. A button reading "Watch for one at $693" against a
        // play whose target is $396 sets a watch that fires on deals worth zero.
        var button = Section(PlayHtml, "const watchBtn", "const watchRefusal");

        Assert.Contains("play.targetBuyPrice", button, StringComparison.Ordinal);
        Assert.DoesNotContain("play.maxBuyPrice", button, StringComparison.Ordinal);
    }

    [Fact]
    public void Pressing_it_posts_the_play_to_the_watch_endpoint_with_the_rolls_own_search_settings()
    {
        var handler = Section(Js, "async function watchPlay(btn)", "// Opening Settings from the top bar");

        Assert.Contains("'/api/opportunities/watch-play'", handler, StringComparison.Ordinal);
        // The watch has to look where the roll looked, not somewhere the seller never chose.
        Assert.Contains("dice-zip-input", handler, StringComparison.Ordinal);
        Assert.Contains("dice-radius-select", handler, StringComparison.Ordinal);
        Assert.Contains("selectedSourceIds()", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void A_watched_play_stays_watched_when_the_board_is_re_sorted()
    {
        // Sorting redraws every row from scratch. A button that only re-labels itself in the DOM
        // offers to create the same watch again the moment the seller changes the sort.
        var handler = Section(Js, "async function watchPlay(btn)", "// Opening Settings from the top bar");
        Assert.Contains("diceWatchedSearches.add(searchKey(play.searchQuery))", handler, StringComparison.Ordinal);

        Assert.Contains("diceWatchedSearches.has(searchKey(play.searchQuery))", PlayHtml, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_opens_knowing_what_the_radar_is_already_watching()
    {
        // Otherwise re-rolling into a product already on the radar offers to spend a second of the
        // twelve slots on it, and the seller finds out only from a toast after the click.
        Assert.Contains("loadDiceWatchedSearches();", Js, StringComparison.Ordinal);

        var loader = Section(Js, "async function loadDiceWatchedSearches()", "// Sorting and filtering are pure views");
        Assert.Contains("'/api/radar/status'", loader, StringComparison.Ordinal);
        // Its own failure: a status call that never answers must not stop the board rendering.
        Assert.Contains(".catch(() => null)", loader, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rows_actions_reach_the_whole_play_and_not_a_stale_position()
    {
        // Filtering and sorting mean row 3 on screen is not plays[3]. The rows actually drawn are
        // kept, and the handlers are attached to them on every render.
        var render = Section(Js, "function renderDiceBoard()", "async function watchPlay(btn)");

        Assert.Contains("diceRows = rows;", render, StringComparison.Ordinal);
        Assert.Matches(@"diceRows = rows;[\s\S]{0,400}board\.innerHTML", render);
        Assert.Contains("board.querySelectorAll('.dice-watch-btn')", render, StringComparison.Ordinal);
    }

    [Fact]
    public void Why_a_play_cannot_be_kept_is_shown_only_where_keeping_it_was_the_last_thing_left()
    {
        // A row with live supply has somewhere to click, and a thin row's tier note already says the
        // evidence is thin. Repeating it in the actions is noise on exactly the rows that need it
        // least — so the refusal is gated on there being no supply at all.
        var refusal = Section(PlayHtml, "const watchRefusal", "return `");

        Assert.Contains("!play.canWatch", refusal, StringComparison.Ordinal);
        Assert.Contains("!(play.sources && play.sources.length)", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_is_escaped_because_it_names_the_product_the_seller_never_typed()
    {
        Assert.DoesNotContain("${play.watchRefusal}", Js, StringComparison.Ordinal);
        Assert.Contains("${esc(play.watchRefusal)}", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_save_puts_the_button_back_rather_than_leaving_it_dead()
    {
        var handler = Section(Js, "async function watchPlay(btn)", "// Opening Settings from the top bar");

        // The server's refusals are sentences. Shown as they are, with the button usable again.
        Assert.Contains("btn.disabled = false;", handler, StringComparison.Ordinal);
        Assert.Contains("btn.textContent = label;", handler, StringComparison.Ordinal);
        Assert.Contains("body?.error", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void Saving_a_watch_while_the_background_watcher_is_off_says_so()
    {
        // A saved watch that never runs is the worst outcome here: the seller believes something is
        // looking for them and nothing is.
        var handler = Section(Js, "async function watchPlay(btn)", "// Opening Settings from the top bar");

        Assert.Contains("body.radarRunning", handler, StringComparison.Ordinal);
        Assert.Contains("Watch in the background", handler, StringComparison.Ordinal);
        Assert.Contains("body.alreadyWatching", handler, StringComparison.Ordinal);
        // And every one of the three outcomes offers the way to go and look at it.
        Assert.Contains("showRadarSection()", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void The_empty_board_points_at_the_thing_it_can_now_actually_do()
    {
        // It used to say the target prices were "worth watching for" and offer no way to watch.
        Assert.DoesNotContain("still worth watching for", Js, StringComparison.Ordinal);
        Assert.Contains("Watch for one", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_footnote_explains_where_a_kept_play_goes()
    {
        var footnote = Section(Html, "<p class=\"fb-arb-footnote\">", "</p>");

        Assert.Contains("Watch for one", footnote, StringComparison.Ordinal);
        Assert.Contains("Deal Radar", footnote, StringComparison.Ordinal);
    }

    [Fact]
    public void The_watching_state_is_styled_as_a_state_rather_than_a_broken_button()
    {
        Assert.Contains(".dice-watch-btn.is-watching", Css, StringComparison.Ordinal);
        Assert.Contains(".dice-watch-refusal", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cached_assets_are_versioned_past_the_build_that_shipped_without_this()
    {
        Assert.True(AssetVersion("app.js") >= 106, "app.js changed, so index.html's ?v= must move past 105");
        Assert.True(AssetVersion("style.css") >= 94, "style.css changed, so index.html's ?v= must move past 93");
    }

    private static int AssetVersion(string file)
    {
        var match = Regex.Match(Html, Regex.Escape(file) + @"\?v=(\d+)");
        Assert.True(match.Success, $"index.html no longer versions {file}");
        return int.Parse(match.Groups[1].Value);
    }

    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
