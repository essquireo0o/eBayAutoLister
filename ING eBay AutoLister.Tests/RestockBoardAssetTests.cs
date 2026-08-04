using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Restock List's screen is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing
/// in C# notices when the sidebar button stops opening anything, the "Find one to buy" action stops
/// reaching the Opportunity Finder, or an eBay title lands in innerHTML unescaped. These lock the
/// wiring that <see cref="ING_eBay_AutoLister.Services.RestockAnalyzer"/> is useless without.
///
/// They also lock three things that are decisions rather than plumbing: the hero is money the seller
/// is NOT making, the stop list is rendered above the quiet reference lists rather than at the
/// bottom, and the cautions on a card are always visible. Each of the three is the sort of thing a
/// later tidy-up reverses without knowing why it was that way.
/// </summary>
public class RestockBoardAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void The_screen_is_reachable_from_the_sidebar_and_opens_as_a_workspace_tab()
    {
        Assert.Contains("data-page=\"restock\"", Html, StringComparison.Ordinal);
        Assert.Contains("<section id=\"restock-section\" class=\"opportunity-overlay hidden\">", Html, StringComparison.Ordinal);

        Assert.Matches(@"restock:\s*\{\s*section:\s*'restock-section',\s*open:\s*showRestockSection", Js);

        // Hidden with every other overlay, or opening a different screen leaves this one underneath.
        var overlays = Regex.Match(Js, @"const OVERLAY_SECTIONS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(overlays.Success, "OVERLAY_SECTIONS is no longer a single array literal");
        Assert.Contains("'restock-section'", overlays.Groups[1].Value, StringComparison.Ordinal);

        // And its handlers are actually attached at start-up.
        Assert.Contains("bindRestock();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void It_sits_with_the_sourcing_boards_because_it_is_one()
    {
        // It is built from the earnings data, so the tempting place for it is beside Money Made.
        // Wrong: it is not a report of what happened, it is an instruction about what to buy, and a
        // seller looking for something to go and buy looks under Grow.
        var grow = Section(Html,
            "<p class=\"nav-group-label\">Grow</p>",
            "<p class=\"nav-group-label\">Account</p>");

        Assert.Contains("data-page=\"restock\"", grow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hero_is_money_the_seller_is_not_making()
    {
        // Not "profit from repeat products", which is a report. Proven lines with nothing listed is
        // the one figure on this page that is an instruction, and it is why the screen gets opened.
        var hero = Section(Html, "<div class=\"er-hero rs-hero\">", "id=\"rs-notice\"");

        Assert.Contains("Sitting idle", hero, StringComparison.Ordinal);
        Assert.Contains("id=\"rs-hero-figure\"", hero, StringComparison.Ordinal);
        Assert.Contains("monthlyProfitOffTheShelf", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void With_ebay_down_the_hero_stops_claiming_anything_about_stock()
    {
        // "$0 sitting idle" on an account whose listings could not be read says "nothing to do
        // here", which is the opposite of what is known. The headline falls back to what the proven
        // lines earn, and the label changes with it.
        Assert.Contains("const stockKnown = r.stockStatus === 'read';", Js, StringComparison.Ordinal);
        Assert.Contains("setText('rs-hero-figure', money(stockKnown ? idle : (s.provenMonthlyProfit || 0)));", Js, StringComparison.Ordinal);
        Assert.Contains("setText('rs-hero-label', stockKnown ?", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stop_list_is_rendered_above_the_reference_lists()
    {
        // It is the most valuable block on the page — money already being spent on something that
        // loses it — and at the bottom of four lists it is the block nobody reads.
        var board = Html.IndexOf("id=\"rs-board\"", StringComparison.Ordinal);
        var stop = Html.IndexOf("id=\"rs-stop-card\"", StringComparison.Ordinal);
        var cost = Html.IndexOf("id=\"rs-cost-card\"", StringComparison.Ordinal);
        var watch = Html.IndexOf("id=\"rs-watch-card\"", StringComparison.Ordinal);

        Assert.True(board > 0 && stop > 0 && cost > 0 && watch > 0, "one of the four lists is gone from the page");
        Assert.True(stop > board, "the shopping list leads");
        Assert.True(cost > stop && watch > stop, "the stop list must be rendered before the quiet reference lists");

        // And it does not look like the shopping list with different words in it.
        Assert.Matches(@"\.rs-card-stop \{[^}]*border-left-color: var\(--danger\)", Css);
    }

    [Fact]
    public void A_row_hands_its_search_term_to_the_screen_that_finds_one_to_buy()
    {
        // The whole loop in one click: a product the seller has proved they can sell, handed to the
        // board that looks for one. Without this the board is a report they have to retype.
        Assert.Contains("function huntForRestockLine(query)", Js, StringComparison.Ordinal);
        Assert.Contains("navigateTo('opportunity');", Js, StringComparison.Ordinal);
        Assert.Contains("findOpportunities();", Js, StringComparison.Ordinal);
        Assert.Contains("rs-hunt", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_can_be_ordered_by_cash_speed_as_well_as_by_money()
    {
        // A seller with unlimited weekends wants the most money a month. A seller with $400 wants
        // the fastest cash back. They are routinely different lines, and sorting is a display
        // choice, so it never costs a second round trip.
        Assert.Contains("data-sort=\"month\"", Html, StringComparison.Ordinal);
        Assert.Contains("data-sort=\"cash\"", Html, StringComparison.Ordinal);
        Assert.Contains("data-sort=\"unit\"", Html, StringComparison.Ordinal);
        Assert.Contains("function sortedRestockLines(lines)", Js, StringComparison.Ordinal);

        // Re-orders the ranked lines, never adds one: everything sorted here came out of Restock.
        Assert.Matches(@"sortedRestockLines\(ranked\)", Js);
    }

    [Fact]
    public void A_line_with_no_return_on_cash_figure_sinks_rather_than_leading_that_order()
    {
        // Most sellers record no purchase dates, so most lines have no cash-speed figure at all.
        // Sorted as though a missing figure were zero they would tie with the worst real ones; the
        // -1 puts them behind every measured line instead of scattered through them.
        Assert.Contains("(b.annualReturnOnCashPercent ?? -1) - (a.annualReturnOnCashPercent ?? -1)", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stop_list_does_not_offer_to_go_and_buy_another_one()
    {
        // A card headed "stop buying" with a "Find one to buy" button on it is the screen arguing
        // with itself, and the button is the louder half.
        Assert.Contains("const hunt = line.verdict === 'restock' || line.verdict === 'watch';", Js, StringComparison.Ordinal);
        Assert.Contains("${hunt ? `<button class=\"btn btn-primary small rs-hunt\"", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_with_no_measurable_rate_shows_no_rate()
    {
        // The server sends zero for "no rate can be measured" — one sale has none. Rendered
        // unconditionally that becomes "0.0 a month", which reads as a product that stopped selling
        // rather than one that has never had a rate.
        Assert.Contains("if (line.salesPerMonth > 0) stats.push(['Sells'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cautions_on_a_card_are_never_folded_away()
    {
        // The seller reading this card is about to spend money on what it recommends. "One of the
        // four came back" behind a "more" link is a caution that does not exist.
        Assert.Contains("rs-cautions", Js, StringComparison.Ordinal);
        Assert.DoesNotContain(".rs-cautions { display: none", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_board_does_not_re_read_ebay_every_time_the_tab_is_opened()
    {
        // Unlike the Tax Pack, loading this costs a real eBay call for the live listings. Coming
        // back to a tab is not asking for one — the Refresh button is.
        Assert.DoesNotMatch(@"restock:\s*\{[^}]*refresh:", Js);
        Assert.Contains("id=\"rs-refresh\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('rs-refresh', 'click', () => loadRestock());", Js, StringComparison.Ordinal);
    }

    [Theory]
    // Every string on this screen that arrives from the server and lands in innerHTML. Product
    // titles come from eBay order lines, so an unescaped one is a listing name away from being script.
    [InlineData("line.title")]
    [InlineData("line.headline")]
    [InlineData("line.searchQuery")]
    [InlineData("line.verdict")]
    public void Server_supplied_text_never_reaches_innerHTML_unescaped(string field)
    {
        Assert.DoesNotContain("${" + field + "}", Js, StringComparison.Ordinal);
        Assert.Contains("${esc(" + field + ")}", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cached_assets_are_versioned_past_the_build_that_shipped_without_this_screen()
    {
        // wwwroot files are embedded resources served with far-future caching. A seller who updates
        // and gets yesterday's app.js gets a sidebar button that opens nothing at all.
        Assert.True(AssetVersion("app.js") >= 105, "app.js changed, so index.html's ?v= must move past 104");
        Assert.True(AssetVersion("style.css") >= 93, "style.css changed, so index.html's ?v= must move past 92");
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
