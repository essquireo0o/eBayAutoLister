namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What condition the lot is in reaches the live card through six links, and five of them are the
/// sort of thing a later tidy-up removes without reading why: the picker exists in the markup, the
/// browser sends it on every one of the three posts that price a lot, the advisor reads it off the
/// comps already in hand, the cut composes with the trend's rather than replacing it, the strip is
/// rendered on every card, and the browser computes none of it. Break any one and the feature
/// silently does nothing on every card forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// Three of these are decisions rather than plumbing. A better condition never raises the ceiling.
/// The condition box is deliberately NOT stamped across a pasted lot list, because it describes one
/// item the seller is looking at. And the constraint every WhatsNot session has worked under: the
/// sold-comps path this whole screen stands on is untouched and additive.
/// </remarks>
public class WhatsNotConditionAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Condition = ReadSource("Services/LiveCondition.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string BuyModels = ReadSource("Models/LiveBuyModels.cs");

    // ── The picker ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Every band the server can be told about has a way to say it, and the default is the empty
    /// one — "from the name" — because silence in a lot's name is not evidence of anything and a
    /// picker that defaulted to "Used" would cut a sealed lot's ceiling on nobody's say-so.
    /// </summary>
    [Fact]
    public void The_picker_offers_every_band_and_defaults_to_reading_the_name()
    {
        Assert.Contains("id=\"wn-cond\"", Html, StringComparison.Ordinal);
        Assert.Contains("<option value=\"\">from the name</option>", Html, StringComparison.Ordinal);

        foreach (var band in new[] { "new", "likenew", "used", "broken" })
            Assert.Contains($"<option value=\"{band}\"", Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// It never changes what eBay is asked. The sold lookup is a boolean AND over sold titles and
    /// eBay's condition is a field, so a query with "used" in it finds nothing — the whole design
    /// rests on splitting comps already in hand, which is also what makes it instant.
    /// </summary>
    [Fact]
    public void The_condition_never_reaches_the_search_query()
    {
        // The query builder is handed the typed name and nothing else, at both call sites.
        Assert.Contains("LiveSearchQuery.Exact(title) : LiveSearchQuery.Build(title)",
            Program, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveCondition", ReadSource("Services/LiveSearchQuery.cs"), StringComparison.Ordinal);

        // Read off the rows the analysis already carried, not from a second lookup.
        Assert.Contains("LiveCondition.Read(item, request.Condition, analysis?.AllSoldComparables)",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sent on all three posts that price a lot, so the card, the re-price and the recorded win
    /// cannot disagree about what was being bought.
    /// </summary>
    [Fact]
    public void The_condition_is_sent_on_every_post_that_prices_a_lot()
    {
        foreach (var endpoint in new[] { "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won" })
        {
            var at = Js.IndexOf($"safePost('{endpoint}'", StringComparison.Ordinal);
            Assert.True(at > 0, $"{endpoint} is no longer posted from the browser");
            Assert.Contains("condition: wnCondition()", Js[at..(at + 900)], StringComparison.Ordinal);
        }

        Assert.Contains("public string? Condition { get; set; }", BidModels, StringComparison.Ordinal);
        Assert.Contains("Condition = Condition,", BuyModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// And deliberately NOT sent on the lot list. That box describes one item the seller is looking
    /// at; the list is a dozen items nobody has seen. Stamping "Used" across all of them would cut
    /// eleven ceilings on the strength of a keystroke about the twelfth.
    /// </summary>
    [Fact]
    public void The_condition_is_not_stamped_across_a_pasted_lot_list()
    {
        var run = Section(Js, "safePost('/api/whatsnot/lots'", "row.state = WN_LOT_FAILED;");

        Assert.Contains("bidIncrement: wnNumber('wn-inc')", run, StringComparison.Ordinal);
        Assert.DoesNotContain("condition: wnCondition()", run, StringComparison.Ordinal);
        // Said out loud where the next person to add it will read it.
        Assert.Contains("deliberately NOT sent", run, StringComparison.Ordinal);
    }

    /// <summary>
    /// The count and the condition belong to the LOT, not to the show, and are emptied together
    /// every time the item changes. A stale "New / sealed" is the dangerous one — it stops the next
    /// lot's used comps cutting anything.
    /// </summary>
    [Fact]
    public void The_condition_is_dropped_whenever_the_lot_changes()
    {
        var reset = Section(Js, "function wnResetLotBoxes() {", "}");
        Assert.Contains("wnResetQty();", reset, StringComparison.Ordinal);
        Assert.Contains("wn-cond", reset, StringComparison.Ordinal);

        // And it is NOT remembered between shows the way the shipping, fee, target and step are.
        var saved = Section(Js, "function wnSaveSettings() {", "}");
        Assert.DoesNotContain("wn-cond", saved, StringComparison.Ordinal);
    }

    /// <summary>
    /// A select fires <c>change</c>, not <c>input</c>. Bound to the wrong event the box would look
    /// like it worked and re-price nothing until the next keystroke somewhere else.
    /// </summary>
    [Fact]
    public void Picking_a_condition_re_prices_off_the_comps_already_held()
    {
        Assert.Contains("$('wn-cond')?.addEventListener('change', wnScheduleRebid);", Js, StringComparison.Ordinal);
        // No eBay in the re-price path — the same guarantee the bid, the step and the quantity have.
        var rebid = Section(Js, "async function wnRebid() {", "wnRenderCard(body);");
        Assert.Contains("/api/whatsnot/rebid", rebid, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/whatsnot/bid'", rebid, StringComparison.Ordinal);
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The asymmetry, in the code rather than only in the prose: the cut is taken when the matching
    /// band sold for LESS, and the other direction is an explicit refusal.
    /// </summary>
    [Fact]
    public void A_better_condition_than_the_comps_never_raises_the_ceiling()
    {
        Assert.Contains("if (read.MatchedMedian >= read.AllMedian)", Condition, StringComparison.Ordinal);
        Assert.Contains("never raises it", Condition, StringComparison.Ordinal);
        // The multiplier is only ever assigned on the falling side.
        var raising = Condition.IndexOf("read.MatchedMedian >= read.AllMedian", StringComparison.Ordinal);
        var assignment = Condition.IndexOf("read.ResaleMultiplier = Math.Round", StringComparison.Ordinal);
        Assert.True(assignment > raising, "the multiplier is assigned before the no-raise refusal");
    }

    /// <summary>
    /// Two ratios, stacked. One is what these fetch lately and the other is what they fetch in this
    /// shape; both are measured off the same rows and both only ever cut. Replacing the composition
    /// with either one alone silently un-prices half the evidence.
    /// </summary>
    [Fact]
    public void The_two_cuts_compose_rather_than_compete()
    {
        Assert.Contains("LiveCondition.Discount(LiveTrend.Discount(resale, trend), condition)",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The gates. Each is a refusal that costs a seller a lot they could have won rather than cash
    /// they cannot get back, which is the direction every rounding on this card goes.
    /// </summary>
    [Fact]
    public void The_gates_on_the_cut_are_all_still_there()
    {
        Assert.Contains("public const int MinBandComps = AuctionSniperAnalyzer.MinCompsToBid;",
            Condition, StringComparison.Ordinal);
        Assert.Contains("public const decimal MinCoveragePercent", Condition, StringComparison.Ordinal);
        Assert.Contains("public const int MinClassifiedComps", Condition, StringComparison.Ordinal);
        Assert.Contains("public const decimal MaxHaircutPercent", Condition, StringComparison.Ordinal);

        Assert.Contains("if (read.MatchedComps < MinBandComps)", Condition, StringComparison.Ordinal);
        Assert.Contains("read.Readable = read.CoveragePercent >= MinCoveragePercent", Condition, StringComparison.Ordinal);
    }

    /// <summary>
    /// It costs no lookup and no clock. This runs while a lot is on the block; a reading that
    /// reached the network would spend the seconds the whole feature exists to save, and one that
    /// read the clock could not be reproduced from the rows that produced it.
    /// </summary>
    [Fact]
    public void The_read_costs_nothing_and_is_reproducible()
    {
        foreach (var forbidden in new[] { "DateTime.UtcNow", "DateTime.Now", "await ", "HttpClient", "Task<" })
            Assert.DoesNotContain(forbidden, Condition, StringComparison.Ordinal);
    }

    /// <summary>
    /// The medians on the strip and the median the price estimator works from are the same function
    /// over the same field. Two ways of averaging would make the ratio between two bands a fact
    /// about arithmetic rather than about condition.
    /// </summary>
    [Fact]
    public void The_band_medians_use_the_apps_own_median_over_the_apps_own_price_field()
    {
        Assert.Contains("MarketplacePricingCalculator.Median", Condition, StringComparison.Ordinal);
        Assert.Contains("row.SoldPrice", Condition, StringComparison.Ordinal);
        Assert.Contains("c.SoldPrice", ReadSource("Services/MarketPriceEstimator.cs"), StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_strip_sits_under_the_trend_it_qualifies_and_above_the_count()
    {
        Assert.Contains("const cond = c.condition || {};", Js, StringComparison.Ordinal);
        Assert.Contains("const condStrip = (priced && cond.headline) ?", Js, StringComparison.Ordinal);

        var at = Js.IndexOf(
            "${trendStrip}\n      ${condStrip}\n      ${unitsStrip}".Replace("\n", "\r\n"),
            StringComparison.Ordinal);
        Assert.True(at > 0, "the condition strip is no longer between the trend strip and the units strip");
    }

    /// <summary>
    /// Every sentence on this strip is the server's. A percentage or a median computed in the
    /// browser is a second opinion about a market, printed next to a ceiling priced off the first.
    /// </summary>
    [Fact]
    public void The_browser_computes_none_of_it()
    {
        var strip = Section(Js, "const cond = c.condition || {};", "// ── How many things is this");

        foreach (var arithmetic in new[] { "matchedMedian /", "allMedian", "* 100", "toFixed", "reduce(" })
            Assert.DoesNotContain(arithmetic, strip, StringComparison.Ordinal);

        foreach (var fromTheServer in new[] { "cond.headline", "cond.moneyNote", "cond.cutPercent", "b.label" })
            Assert.Contains(fromTheServer, strip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The band the lot is in is the only one with a colour, because it is the only one being asked
    /// a question — and the two edges that appear on the strip are the two states that change the
    /// decision.
    /// </summary>
    [Fact]
    public void Every_state_of_the_strip_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var rule in new[]
                 {
                     ".wn-cond-strip", ".wn-cond-cut", ".wn-cond-warn", ".wn-cond-bands",
                     ".wn-cond-band-mine", ".wn-cond-money", ".wn-field-cond",
                 })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        Assert.Contains("style.css?v=120", Html, StringComparison.Ordinal);
        Assert.Contains("app.js?v=137", Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// This screen is used as a column down the side of a live stream, so the strip and the picker
    /// both fold rather than overflowing at the narrow width every other WhatsNot block folds at.
    /// </summary>
    [Fact]
    public void The_strip_folds_when_the_window_is_a_column()
    {
        var narrow = Css.IndexOf(".wn-cond-line {\r\n    flex-direction: column;", StringComparison.Ordinal);
        Assert.True(narrow > 0, "the condition strip no longer folds at the narrow width");
        Assert.Contains(".wn-field-cond {\r\n    width: 100%;", Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spoken line speaks in exactly two states, and neither of them is the common one. A
    /// clause on every lot would cost the line the only thing it is for.
    /// </summary>
    [Fact]
    public void The_spoken_line_only_speaks_where_it_changes_what_the_hand_does()
    {
        Assert.Contains("private static string WhatKindOfOne(LiveBidCard card)", Speech, StringComparison.Ordinal);
        Assert.Contains("if (cond.Band == LiveConditionBands.Unstated) return \"\";", Speech, StringComparison.Ordinal);
        Assert.Contains("WhichWayItsGoing(card),\r\n            WhatKindOfOne(card)", Speech, StringComparison.Ordinal);
    }

    /// <summary>
    /// The action log is where the first real "the comps were all sealed and the lot was not" will
    /// show up, on a real show, in the seller's own record.
    /// </summary>
    [Fact]
    public void The_fresh_price_logs_what_was_bid_on_against_what_the_comps_were()
    {
        Assert.Contains("condition {card.Condition.Band} ({card.Condition.Source})",
            Program, StringComparison.Ordinal);
        Assert.Contains("card.Condition.MatchedComps}/{card.Condition.ClassifiedComps}",
            Program, StringComparison.Ordinal);
    }

    // ── The constraint every WhatsNot session works under ────────────────────────────────────

    /// <summary>
    /// Sold comps stay fully working and this is purely additive to them. The live price still runs
    /// on the same pipeline, and every endpoint the screen had before is still registered.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched()
    {
        Assert.Contains("app.MapGet(\"/api/sold-comps\"", Program, StringComparison.Ordinal);
        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);

        foreach (var endpoint in new[]
                 {
                     "/api/sold-comps",
                     "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won", "/api/whatsnot/sheet",
                     "/api/whatsnot/lots", "/api/whatsnot/list", "/api/whatsnot/embed-check",
                     "/api/whatsnot/read", "/api/whatsnot/photo",
                 })
            Assert.Contains($"\"{endpoint}\"", Program, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"section start is gone: {from}");
        var end = text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"section end is gone: {to}");
        return text[start..end];
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
