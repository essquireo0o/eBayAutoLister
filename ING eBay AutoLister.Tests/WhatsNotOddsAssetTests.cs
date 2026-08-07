namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The count of the sold rows that would actually have covered the win reaches the live card
/// through seven links, and most of them are the sort of thing a later tidy-up removes without
/// reading why: the whole sold set being carried on the analysis rather than the five the table
/// shows, the advisor reading it at the bid on screen, the break-even it is counted against coming
/// from the profit calculator rather than from a second arithmetic, the comps being re-priced by the
/// ratio the ceiling was built with, the strip drawing it under the meter, the one warning, and the
/// spoken line. Break any one and the feature silently does nothing on every card forever, which
/// looks exactly like working.
/// </summary>
/// <remarks>
/// <para>
/// Three of these are decisions rather than plumbing. It counts and it <b>never charges</b> — no
/// ceiling, resale price, median, spread or call moves for anything found here, because the scatter
/// is already reported by the middle-half warning and charging for it twice would make the ceiling
/// uncheckable against the comps printed under it. A percentage is <b>refused</b> under five sales,
/// where one row moves it twenty-five points, and the count is still shown. And only the worst of
/// the three rated states <b>interrupts</b>: a coin flip the seller can read on the strip is a
/// decision, not a warning.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched, and this is purely additive to it.
/// </para>
/// </remarks>
public class WhatsNotOddsAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Odds = ReadSource("Services/LiveOdds.cs");
    private static readonly string OddsModels = ReadSource("Models/LiveOddsModels.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string Estimator = ReadSource("Services/MarketPriceEstimator.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");

    // ── The evidence it counts ───────────────────────────────────────────────────────────────

    /// <summary>
    /// It counts the WHOLE sold set, not the five rows the comp table shows. Five rows chosen by
    /// match score are not a distribution — they are five rows that happen to sit wherever their
    /// match scores fell, and a count of them would be a count of the app's own sorting.
    /// </summary>
    [Fact]
    public void It_counts_every_sold_row_and_not_the_five_the_table_shows()
    {
        Assert.Contains("analysis?.AllSoldComparables", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("TopSoldComparables", Odds, StringComparison.Ordinal);
    }

    /// <summary>
    /// A comp row is whatever that seller listed, and some of them were boxes of ten. The division
    /// is the pricing path's own, called rather than restated — two "what did one of them go for"
    /// rules is how a strip ends up disagreeing with the price above it.
    /// </summary>
    [Fact]
    public void A_multipack_comp_is_divided_by_the_pricing_paths_own_rule()
    {
        Assert.Contains("MarketPriceEstimator.UnitSoldPrice(c)", Odds, StringComparison.Ordinal);
        Assert.Contains("public static decimal UnitSoldPrice(", Estimator, StringComparison.Ordinal);

        // And the pricing path uses the same one, or there would be two of them again.
        Assert.Contains("SoldPrice = UnitSoldPrice(c),", Estimator, StringComparison.Ordinal);
    }

    // ── The bar it counts against ────────────────────────────────────────────────────────────

    /// <summary>
    /// The line each sale is measured against is the app's own "what does this have to sell for" —
    /// <see cref="ING_eBay_AutoLister.Models.ProfitBreakdown.BreakEvenSalePrice"/>, solved by the
    /// shared calculator. A break-even inverted a second time here is a second break-even.
    /// </summary>
    [Fact]
    public void The_bar_is_the_shared_calculators_break_even_and_not_a_second_arithmetic()
    {
        Assert.Contains("BreakEvenSalePrice", Odds, StringComparison.Ordinal);
        Assert.Contains("calc.Calculate(", Odds, StringComparison.Ordinal);
        Assert.Contains("LiveOdds.NeedPerUnit(profitCalc,", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counted at the bid on screen when there is one and at the card's own ceiling when there is
    /// not — the two are different promises, and the read carries which of them it made.
    /// </summary>
    [Fact]
    public void It_counts_at_the_bid_on_screen_or_at_the_ceiling_and_says_which()
    {
        Assert.Contains("var oddsBid = card.BidWasKnown ? bid : card.MaxBid;", Advisor, StringComparison.Ordinal);
        Assert.Contains("atCeiling: !card.BidWasKnown", Advisor, StringComparison.Ordinal);
        Assert.Contains("a win at your {read.AtBid:C} ceiling", Odds, StringComparison.Ordinal);
    }

    /// <summary>
    /// The landed cost is divided across the units, because the sold rows are sales of one of the
    /// thing. One hammer, one premium, one shipment, spread across what is in the box.
    /// </summary>
    [Fact]
    public void The_cost_of_the_win_is_divided_across_the_lot()
    {
        Assert.Contains("/ count, 2);", Advisor, StringComparison.Ordinal);
        Assert.Contains("LotUnits", OddsModels, StringComparison.Ordinal);
    }

    // ── The re-pricing ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every past sale is counted as what YOURS would fetch, at the ratio the ceiling above it was
    /// built with — read off the two prices rather than re-multiplying the three cuts, so the first
    /// time one of those reads changes its guards the count cannot quietly stop matching.
    /// </summary>
    [Fact]
    public void The_comps_are_repriced_by_the_ratio_the_ceiling_was_built_with()
    {
        Assert.Contains("LiveOdds.RepriceRatio(resale, bidAgainst)", Advisor, StringComparison.Ordinal);
        Assert.Contains("return Math.Min(1m, to / from);", Odds, StringComparison.Ordinal);

        // The three cuts are named in the file that borrows their answer, so the next one added has
        // somewhere obvious to be accounted for.
        foreach (var read in new[] { "LiveTrend", "LiveCondition", "LiveHoldCost" })
            Assert.Contains(read, Odds, StringComparison.Ordinal);
    }

    // ── What it refuses to do ────────────────────────────────────────────────────────────────

    /// <summary>
    /// It counts and it never charges. Nothing in the file that reads it assigns a price, a ceiling
    /// or a multiplier — the scatter is already reported by the middle-half warning, and charging
    /// for it twice would make the ceiling uncheckable against the comps printed under it.
    /// </summary>
    [Fact]
    public void Nothing_it_finds_moves_a_price()
    {
        // The read is attached and never fed back into anything.
        Assert.Contains("card.Odds = LiveOdds.Read(", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("card.MaxBid = card.Odds", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("card.ResalePrice = card.Odds", Advisor, StringComparison.Ordinal);

        // And the read itself produces no multiplier of its own to be applied by anybody.
        Assert.DoesNotContain("Discount(", Odds, StringComparison.Ordinal);
    }

    /// <summary>
    /// A percentage is refused under five sales, where one row moves it twenty-five points. The
    /// count itself is still shown — "three of four" is a true sentence — and the claim that it is
    /// a rate is what is withheld.
    /// </summary>
    [Fact]
    public void Too_few_sales_is_a_count_and_never_a_rate()
    {
        Assert.Contains("public const int MinSalesToRate = 5;", Odds, StringComparison.Ordinal);
        Assert.Contains("read.Stated = prices.Count >= MinSalesToRate;", Odds, StringComparison.Ordinal);

        // The strip drops the percentage with it rather than printing a number the read refused.
        Assert.Contains("${od.stated ? `<span class=\"wn-odds-tag\">${od.percent}%</span>` : ''}",
            Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Exactly one of the five states reaches the card's warning list, and the sentence there is the
    /// read's own — the strip and the warnings cannot describe one count in two ways.
    /// </summary>
    [Fact]
    public void Only_the_long_state_interrupts_and_the_sentence_is_the_reads_own()
    {
        Assert.Contains("if (read.Verdict != LiveOddsVerdicts.Long) return;", Odds, StringComparison.Ordinal);
        Assert.Contains("if (card.Odds is { Warning.Length: > 0 } odds) warnings.Add(odds.Warning);",
            Advisor, StringComparison.Ordinal);
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drawn directly under the meter, because it is the answer to the question the meter raises:
    /// the ceiling on that track is the bid that keeps the MIDDLE of the sold prices clearing a
    /// target, and half the sales came in under the middle.
    /// </summary>
    [Fact]
    public void The_strip_is_drawn_immediately_under_the_meter()
    {
        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${ladder}\n      ${meter}\n      ${oddsStrip}\n", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// The browser counts nothing. Every word, every dollar and the count itself are the server's;
    /// the only arithmetic here is clamping a percentage into a width.
    /// </summary>
    [Fact]
    public void The_browser_paints_the_count_and_never_makes_one()
    {
        var at = Js.IndexOf("const oddsStrip = od.headline", StringComparison.Ordinal);
        Assert.True(at > 0, "the strip is built from the server's own headline");
        var block = Js[at..(at + 1_800)];

        Assert.Contains("esc(od.headline)", block, StringComparison.Ordinal);
        Assert.Contains("esc(od.note)", block, StringComparison.Ordinal);
        Assert.Contains("Math.max(0, Math.min(100, od.percent))", block, StringComparison.Ordinal);

        // No division, no comparison against a threshold, no re-counting of anything.
        Assert.DoesNotContain("od.covered /", block, StringComparison.Ordinal);
        Assert.DoesNotContain("filter(", block, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every state the read can produce has an edge, a headline colour and a bar to land on. A
    /// verdict with no rule for it is a strip that silently draws the neutral one, which is the
    /// state that means "nothing to worry about".
    /// </summary>
    [Fact]
    public void Every_state_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var state in new[] { "strong", "even", "long", "thin" })
            Assert.Contains($".wn-odds-{state} {{", Css, StringComparison.Ordinal);

        foreach (var part in new[]
        {
            ".wn-odds {", ".wn-odds-line {", ".wn-odds-label {", ".wn-odds-headline {",
            ".wn-odds-tag {", ".wn-odds-src {", ".wn-odds-bar {", ".wn-odds-bar-covered {",
            ".wn-odds-box {", ".wn-odds-cell {", ".wn-odds-cell-this {", ".wn-odds-note {",
        })
        {
            Assert.Contains(part, Css, StringComparison.Ordinal);
        }

        // And it folds at the narrow width like every other strip on this card.
        Assert.Contains(".wn-odds-line {\n    flex-direction: column;", Css.Replace("\r\n", "\n"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar has a name a screen reader can say. It is the part of this block read at a glance,
    /// and a bare div would make the one number that moves with the bidding invisible to anybody
    /// not looking at it.
    /// </summary>
    [Fact]
    public void The_bar_says_what_it_is()
    {
        Assert.Contains("aria-label=\"${esc(`${od.covered} of ${od.total} past sales cover it`)}\"",
            Js, StringComparison.Ordinal);
    }

    /// <summary>The browser was told to reload both assets.</summary>
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        Assert.Contains("app.js?v=142", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=125", Html, StringComparison.Ordinal);
    }

    // ── The line ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The spoken line says it in exactly one state, immediately after the resale price it
    /// qualifies. A listener who hears "resells around $200" on a lot whose sales ran $40 to $320
    /// has heard the one number that hides the risk.
    /// </summary>
    [Fact]
    public void The_line_says_it_only_when_most_of_the_evidence_is_under_the_bid()
    {
        Assert.Contains("WhatItResellsFor(card), HowOftenItPays(card),", Speech, StringComparison.Ordinal);
        Assert.Contains("if (card.Odds is not { Verdict: LiveOddsVerdicts.Long } odds) return \"\";",
            Speech, StringComparison.Ordinal);
    }

    // ── The log ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's own log prints the count, the bar it was made against and the spread it came out
    /// of, so the first real "the app said BID and only 3 of 14 comps covered it" can be checked
    /// after the show rather than taken on trust.
    /// </summary>
    [Fact]
    public void The_action_log_prints_the_count()
    {
        Assert.Contains("$\"odds {card.Odds.Verdict}\"", Program, StringComparison.Ordinal);
        Assert.Contains("card.Odds.Covered", Program, StringComparison.Ordinal);
        Assert.Contains("card.Odds.NeedPerUnit", Program, StringComparison.Ordinal);
        Assert.Contains("card.Odds.LowSale", Program, StringComparison.Ordinal);
    }

    // ── The card carries it ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Present on every priced card, never null. A block that only appeared once the count was bad
    /// would be a block whose absence means both "the evidence is behind you" and "nothing counted",
    /// and the second of those is the expensive reading.
    /// </summary>
    [Fact]
    public void The_card_always_carries_the_read()
    {
        Assert.Contains("public LiveOddsRead Odds { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    // ── Sold comps are untouched ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The constraint every WhatsNot session has worked under. This screen stands on the sold-comps
    /// path, and every endpoint it stands on is still registered.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched_and_still_registered()
    {
        foreach (var route in new[]
        {
            "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
            "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
            "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
        })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        // And the live price still runs on the shared market pipeline, not on anything of its own.
        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
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
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
