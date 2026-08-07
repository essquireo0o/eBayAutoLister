namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The seller's own record only reaches the live card through four links, none of them in C#: the
/// endpoint reads their book, the held quote keeps it between bids, the browser renders it, and the
/// stylesheet tells the three verdicts apart. Break any one and the panel silently stops appearing —
/// which looks exactly like a seller who has never sold one of these.
///
/// Three of these are decisions rather than plumbing, and all three are the sort of thing a later
/// tidy-up reverses without reading why: the record is read from the same rows the Restock board
/// uses, a failure to read it costs the panel and never the ceiling, and the sold-comps path this
/// whole screen stands on is left completely alone.
/// </summary>
public class WhatsNotOwnRecordAssetTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Board = ReadSource("Services/LiveBidBoard.cs");

    // ── The endpoint ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_live_price_reads_the_seller_s_own_sales_and_their_unsold_stock()
    {
        Assert.Contains("static OwnSalesEvidence? ReadOwnTrackRecord(", Program, StringComparison.Ordinal);
        Assert.Contains("var own = ReadOwnTrackRecord(title, earnings, costBasis, earningsCalc, deals, feeProfile, log);",
            Program, StringComparison.Ordinal);

        // Both halves of the question: what you sold, and what you are already holding.
        Assert.Contains("OwnTrackRecord.Match(title, sales, deals.GetAll()", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_rows_are_the_ones_the_restock_board_is_built_from()
    {
        // Two screens claiming to show "your sales of this" out of two different assemblies of the
        // same table is two screens that will eventually disagree about the seller's own history.
        foreach (var piece in new[]
                 {
                     "var costs = costBasis.GetAll();",
                     "CostBasisStore.Find(costs, flip.ListingId, flip.Sku)",
                     "new RestockSale",
                     "Sale = calculator.Compute(flip, basis, feeProfile),",
                     "AcquiredUtc = basis?.AcquiredUtc,",
                 })
        {
            Assert.Contains(piece, Program, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A locked database has to cost the seller the extra panel, never the ceiling. The whole screen
    /// exists to answer in the seconds before a hammer falls.
    /// </summary>
    [Fact]
    public void Failing_to_read_the_seller_s_book_never_fails_the_price()
    {
        var helper = Between(Program, "static OwnSalesEvidence? ReadOwnTrackRecord(", "// ── WhatsNot: live-auction arbitrage");

        Assert.Contains("try", helper, StringComparison.Ordinal);
        Assert.Contains("catch (Exception ex)", helper, StringComparison.Ordinal);
        Assert.Contains("return null;", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_is_held_with_the_comps_so_a_moving_bid_keeps_it()
    {
        // The bid climbs every two or three seconds. Re-reading two SQLite tables per keystroke to
        // arrive at a history that cannot have changed is the cost this screen refuses to pay.
        Assert.Contains("board.Hold(title, analysis, category, nowUtc: null, own: own)", Program, StringComparison.Ordinal);
        Assert.Contains("public OwnSalesEvidence? Own { get; init; }", Board, StringComparison.Ordinal);

        // And both of the endpoints that price against held comps pass it back in.
        Assert.Contains("advisor.Build(quote.Item, quote.Analysis, req, feeProfile, quote.Category, now, quote.Own)",
            Program, StringComparison.Ordinal);
        Assert.Contains("advisor.Build(quote.Item, quote.Analysis, req.AsBid(), feeProfile, quote.Category, null, quote.Own)",
            Program, StringComparison.Ordinal);
    }

    // ── The advisor ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_record_is_attached_after_the_call_has_already_been_decided()
    {
        // Placement is the safety property. Judge runs, the badge is set, and only then is the
        // seller's own record priced and attached — so there is no order of statements in which it
        // could reach the verdict.
        var build = Between(Advisor, "public LiveBidCard Build(", "public const decimal LotRankTierStep");

        Assert.True(
            build.IndexOf("var (call, label, reason) = Judge(card);", StringComparison.Ordinal)
                < build.LastIndexOf("AttachOwnRecord(card, own);", StringComparison.Ordinal),
            "the seller's own record must be attached after the call is decided");

        Assert.Contains("card.OwnHistory = OwnTrackRecord.Price(", Advisor, StringComparison.Ordinal);
        Assert.Contains("card.Warnings.AddRange(OwnTrackRecord.Warnings(card.OwnHistory));", Advisor, StringComparison.Ordinal);
    }

    [Fact]
    public void There_is_still_one_function_that_turns_a_break_even_into_a_maximum_bid()
    {
        var record = ReadSource("Services/OwnTrackRecord.cs");

        Assert.Contains("AuctionSniperAnalyzer.MaxBidDetail(proceeds, shipping, targetRoiPercent, buyerFeePercent)",
            record, StringComparison.Ordinal);
        Assert.Contains("LiveBidAdvisor.BreakEvenBid(proceeds, buyerFeePercent, shipping)", record, StringComparison.Ordinal);

        // No arithmetic of its own for the ceiling — the two calls above and nothing beside them.
        Assert.DoesNotContain("/ (1m + target", record, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_for_pricing_off_the_seller_s_own_history_is_stated_once()
    {
        var record = ReadSource("Services/OwnTrackRecord.cs");

        Assert.Contains("public const int MinOrdersToTrust = RestockAnalyzer.MinOrdersToRank;", record, StringComparison.Ordinal);
        Assert.Contains("if (e.IdentityIsLoose || e.Orders < MinOrdersToTrust) return;", record, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_card_renders_the_record_the_server_sent()
    {
        Assert.Contains("function wnOwnBlock(own) {", Js, StringComparison.Ordinal);
        Assert.Contains("${wnOwnBlock(c.ownHistory)}", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_record_sits_above_the_market_statistics_rather_than_under_them()
    {
        // It is evidence for the decision, not a footnote to it: a seller who has to scroll past the
        // comps to find out they already own three of these has been told too late.
        var card = Between(Js, "card.innerHTML = `", "  // ── WhatsNot: the show's buy sheet");

        Assert.True(
            card.IndexOf("${wnOwnBlock(c.ownHistory)}", StringComparison.Ordinal)
                < card.IndexOf("<div class=\"wn-stats\">", StringComparison.Ordinal),
            "your own record belongs above the market's statistics");
    }

    [Fact]
    public void The_four_facts_that_decide_a_repeat_buy_are_all_on_the_panel()
    {
        // What you got, what you cleared, how long it took, and how many you are still sitting on.
        foreach (var tile in new[] { "\"You've sold\"", "'You got'", "'Your net each'", "'Your sell time'", "'Still holding'" })
            Assert.Contains(tile, Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_computes_none_of_the_money_on_the_panel()
    {
        // The second ceiling, the comparison and the headline are all written next to the arithmetic
        // in OwnTrackRecord. A sentence assembled here out of an average would be a second opinion
        // about money that nothing tests, and it would be the one on screen.
        Assert.Contains("esc(own.ceilingComparison)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(own.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(own.ownMaxBid)", Js, StringComparison.Ordinal);

        var block = Between(Js, "function wnOwnBlock(own) {", "  function wnRenderCard(c) {");
        foreach (var arithmetic in new[] { "* 1.3", "/ 1.3", "reduce(", ".toFixed(1)" })
            Assert.DoesNotContain(arithmetic, block, StringComparison.Ordinal);
    }

    [Fact]
    public void Never_having_sold_one_is_a_line_rather_than_a_blank_space()
    {
        // "You have never sold one of these" is the difference between a repeat buy and a punt, and
        // it is a decision statistic in its own right.
        Assert.Contains("wn-own-empty", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-own-empty {", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_verdict_the_server_can_return_has_a_colour()
    {
        Assert.Contains("wn-own wn-own-${esc(own.verdict)}", Js, StringComparison.Ordinal);
        foreach (var rule in new[] { ".wn-own {", ".wn-own-proven {", ".wn-own-holding {" })
            Assert.Contains(rule, Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seller_s_ceiling_is_marked_when_it_is_the_lower_of_the_two()
    {
        // The case that costs money: the badge is the market's number and the seller has never got
        // the market's number for one of these.
        Assert.Contains("own.ceilingIsLower ? ' wn-own-ceiling-low'", Js, StringComparison.Ordinal);
        Assert.Contains("own.ownIsTheOnlyCeiling ? ' wn-own-ceiling-only'", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-own-ceiling-low {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-own-ceiling-only {", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_folds_rather_than_overflowing_on_a_phone_sized_window()
    {
        Assert.Contains(".wn-own-table {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-own-cell-age {", Css, StringComparison.Ordinal);

        var narrow = Css[Css.LastIndexOf("@media (max-width: 620px) {", StringComparison.Ordinal)..];
        Assert.Contains(".wn-own-table", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_and_the_script_were_both_re_stamped()
    {
        // wwwroot is embedded, and a cached app.js against a new server renders a card with no panel
        // on it at all.
        Assert.Contains("app.js?v=124", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=107", Html, StringComparison.Ordinal);
    }

    // ── Additive, as every WhatsNot session has been ─────────────────────────────────────────

    [Fact]
    public void Sold_comps_and_every_endpoint_this_screen_already_had_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps",
                     "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
                     "/api/whatsnot/embed-check",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        // And the live price still runs on the app's own sold-comp pipeline rather than a new lookup.
        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_restock_board_still_reads_the_seller_s_book_its_own_way()
    {
        // This session reused the Restock board's row shape. It did not take it over: /api/restock
        // still assembles and analyses its own, including the one eBay read the live card refuses.
        Assert.Contains("app.MapGet(\"/api/restock\"", Program, StringComparison.Ordinal);
        Assert.Contains("RestockAnalyzer.Analyze(sales, active, DateTimeOffset.Now)", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_price_reads_no_network_for_the_seller_s_own_record()
    {
        // The Restock board can afford one eBay call for "what have I got listed". A live auction
        // cannot, and an await inside this helper is how that gets forgotten.
        var helper = Between(Program, "static OwnSalesEvidence? ReadOwnTrackRecord(", "// ── WhatsNot: live-auction arbitrage");

        Assert.DoesNotContain("await", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("ebay.", helper, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" is no longer in the source");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"\"{end}\" no longer follows \"{start}\"");
        return source[from..to];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
