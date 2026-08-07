namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Which way the sold price has been going reaches the live card through five links, and two of
/// them are outside C#: the analysis carries every comp it found instead of the five best-matching
/// ones, the advisor reads them, the haircut goes into the ceiling, the browser renders the strip,
/// and the browser computes none of it. Break the first and the whole feature silently reports
/// "not enough dated sales" on every card forever, which is indistinguishable from working.
/// </summary>
/// <remarks>
/// Three of these are decisions rather than plumbing, and each is the sort of thing a later tidy-up
/// undoes without reading why: a climb never raises the ceiling, the cut needs both the window
/// medians and the trend line to agree, and comps whose newest sale predates the window are refused
/// rather than reported as a market that collapsed. The fourth is the constraint every WhatsNot
/// session has worked under — the sold-comps path this screen stands on is untouched.
/// </remarks>
public class WhatsNotTrendAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Trend = ReadSource("Services/LiveTrend.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string Models = ReadSource("Models/MarketAnalysisModels.cs");

    // ── The comps reach the reader at all ────────────────────────────────────────────────────

    /// <summary>
    /// The link that fails silently. A trend is a time series; the five best-matching comps are not
    /// one, they are five rows that happen to sit wherever their sale dates fell. If the whole set
    /// stops being carried, every card reads as unreadable and the screen looks exactly the same.
    /// </summary>
    [Fact]
    public void The_analysis_carries_every_comp_it_found_and_not_just_the_five_on_screen()
    {
        Assert.Contains("AllSoldComparables", Models, StringComparison.Ordinal);
        Assert.Contains("AllSoldComparables = localComparables,", Program, StringComparison.Ordinal);
        // Still only five on any screen — this is an addition, not a widening of every response.
        Assert.Contains("TopSoldComparables = localComparables.OrderByDescending(c => c.MatchScore).Take(5)",
            Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Server-side only. Twenty extra rows on every analysis, shipped to every board that renders
    /// five of them, is a cost paid on every screen in the app for one strip on one of them.
    /// </summary>
    [Fact]
    public void The_full_comp_set_is_never_serialized_to_the_browser()
    {
        var at = Models.IndexOf("public List<MarketplaceComparableResult> AllSoldComparables",
            StringComparison.Ordinal);
        Assert.True(at > 0, "AllSoldComparables is gone from MarketAnalysisResult");

        // The attribute sits immediately above the property.
        Assert.Contains("JsonIgnore", Models[Math.Max(0, at - 200)..at], StringComparison.Ordinal);
        Assert.DoesNotContain("allSoldComparables", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// No second lookup. The whole promise of this screen is an answer in the seconds a lot is on
    /// screen, and a trend that cost another round trip to the comps API would be paid for on every
    /// card to change the answer on a few of them.
    /// </summary>
    [Fact]
    public void The_trend_costs_the_live_path_no_extra_lookup()
    {
        Assert.Contains("LiveTrend.Read(analysis?.AllSoldComparables, now)", Advisor, StringComparison.Ordinal);

        // The endpoint reads eBay at most twice — the first search and the one widening — and the
        // trend is not a third.
        var bid = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "app.MapPost(\"/api/whatsnot/rebid\"");
        Assert.DoesNotContain("LiveTrend", bid, StringComparison.Ordinal);
        Assert.Equal(2, Occurrences(bid, "await AnalyzeProductAsync("));
    }

    /// <summary>
    /// Read inside Build rather than handed in, so a card re-priced against held comps re-runs the
    /// same reading over the same rows. A trend computed at the endpoint and carried on the quote
    /// would be a number the fresh card and the re-priced one could disagree about.
    /// </summary>
    [Fact]
    public void A_repriced_card_reads_the_trend_again_rather_than_carrying_it()
    {
        Assert.DoesNotContain("LiveTrendRead", ReadSource("Services/LiveBidBoard.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("Trend", ReadSource("Services/LiveBidBoard.cs"), StringComparison.Ordinal);

        var rebid = Section(Program, "app.MapPost(\"/api/whatsnot/rebid\"", "app.MapPost(\"/api/whatsnot/won\"");
        Assert.Contains("advisor.Build(", rebid, StringComparison.Ordinal);
    }

    // ── What the reading is allowed to do ────────────────────────────────────────────────────

    /// <summary>
    /// The asymmetry the feature is built around, pinned because it reads like an oversight. On a
    /// screen with seconds and one hammer, bidding up on a price that has not happened yet is
    /// paying for it twice — so a climb is reported and never priced.
    /// </summary>
    [Fact]
    public void A_climb_never_raises_the_ceiling()
    {
        // The radar's two upside tools are the ones this must not reach for: a projected price and
        // the multiplier built from it are exactly how a live ceiling would learn to go up.
        Assert.DoesNotContain("PriceTrendAnalyzer.TrendMultiplier", Trend, StringComparison.Ordinal);
        Assert.DoesNotContain("ProjectedPrice", Trend, StringComparison.Ordinal);

        // And the multiplier can only ever be below 1 — belt and braces, in Discount as well as in
        // the read that sets it.
        Assert.Contains("multiplier >= 1m) return resale;", Trend, StringComparison.Ordinal);
        Assert.Contains("LiveTrendDirections.Rising", Trend, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cut needs two independent readings to agree: the window medians AND the line through every
    /// dated sale. A haircut taken on the strength of where a window boundary happened to fall is a
    /// haircut that refuses a perfectly good lot.
    /// </summary>
    [Fact]
    public void A_cut_needs_the_slope_and_the_windows_to_agree_and_the_reading_to_be_confirmed()
    {
        Assert.Contains("read.Reliability != \"confirmed\"", Trend, StringComparison.Ordinal);
        Assert.Contains("reading.SlopePerMonth is decimal slope && slope > 0m", Trend, StringComparison.Ordinal);
        Assert.Contains("change > -MaterialMovePercent", Trend, StringComparison.Ordinal);
    }

    /// <summary>
    /// The most expensive lie this feature could tell. The comps database is filled by a scraper
    /// with fragile session cookies; when it stalls, every product on earth shows no recent sales.
    /// </summary>
    [Fact]
    public void Comps_older_than_the_window_are_refused_rather_than_called_a_collapse()
    {
        Assert.Contains("newest >= windowDays", Trend, StringComparison.Ordinal);
        Assert.Contains("stopped being updated", Trend, StringComparison.Ordinal);
        // And no direction exists for it to have been reported as.
        Assert.DoesNotContain("Stopped", ReadSource("Models/LiveBidModels.cs"), StringComparison.Ordinal);
    }

    /// <summary>
    /// One product's comps cannot be their own baseline. The radar divides the scan-wide change in
    /// volume out of each product's; handed one product that baseline is the product itself, and
    /// nothing could ever be selling faster than usual.
    /// </summary>
    [Fact]
    public void One_products_corpus_carries_no_velocity_baseline()
    {
        // The scan-wide corpus is deliberately NOT reused, and the baseline it exists to carry is
        // never assigned here — Detrend then returns the raw figure, and the card claims nothing
        // about how this product compares to the rest of the market.
        // Named in the prose that explains why it is not used; never called.
        Assert.DoesNotContain("PriceTrendAnalyzer.BuildCorpus(", Trend, StringComparison.Ordinal);
        Assert.DoesNotContain("corpus.VelocityChangePercent =", Trend, StringComparison.Ordinal);
        Assert.Contains("SoloCorpus(rows, nowUtc, windowDays)", Trend, StringComparison.Ordinal);
    }

    // ── The measurement is borrowed, not rewritten ───────────────────────────────────────────

    /// <summary>
    /// The trend the live card reports is the trend the Trend Radar board reports, measured by the
    /// same function. A second slope estimator in this app would be a second opinion about which
    /// way a market is going, and nothing would say which one to believe.
    /// </summary>
    [Fact]
    public void The_measurement_is_the_radars_own()
    {
        Assert.Contains("PriceTrendAnalyzer.Measure(rows, nowUtc, windowDays, corpus)", Trend,
            StringComparison.Ordinal);
        Assert.Contains("PriceTrendAnalyzer.ClimbingPricePercent", Trend, StringComparison.Ordinal);

        // Nothing here computes a median, a slope or a percentage change of its own.
        foreach (var borrowed in new[] { "Theil", "OrderBy(p => p)", "SlopePerMonth(", "PercentChange(" })
            Assert.DoesNotContain(borrowed + " {", Trend, StringComparison.Ordinal);
    }

    /// <summary>
    /// One break-even in the app, still. The haircut changes the PRICE handed to it, never the
    /// arithmetic — a live card that computed its own ceiling out of a discounted median would be a
    /// second opinion about money.
    /// </summary>
    [Fact]
    public void The_cut_changes_the_price_and_not_the_ceiling_function()
    {
        Assert.Contains("hunter.BreakEvenBuyPrice(bidAgainst, fees)", Advisor, StringComparison.Ordinal);
        Assert.Contains("AuctionSniperAnalyzer.MaxBidDetail(breakEvenAllIn, shipping, target, feePercent)",
            Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("ResaleMultiplier", Advisor, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_strip_is_rendered_on_every_card_and_sits_under_the_search_it_qualifies()
    {
        Assert.Contains("const t = c.trend || {};", Js, StringComparison.Ordinal);
        Assert.Contains("const trendStrip = t.headline ?", Js, StringComparison.Ordinal);

        // Order on the card: what was searched, then which way that search's answer is moving, then
        // how many of the thing there are. Both of the first two are facts about what the money
        // below MEANS; the third is what the money is multiplied by.
        var search = Js.IndexOf("${searchStrip}\n      ${trendStrip}\n      ${unitsStrip}".Replace("\n", "\r\n"),
            StringComparison.Ordinal);
        Assert.True(search > 0, "the trend strip is no longer between the search strip and the units strip");
    }

    /// <summary>
    /// Every sentence on this strip is the server's. A percentage computed in the browser is a
    /// second opinion about a market, printed next to a ceiling that was priced off the first one.
    /// </summary>
    [Fact]
    public void The_browser_computes_none_of_it()
    {
        var strip = Section(Js, "const t = c.trend || {};", "// ── How many things is this");

        foreach (var arithmetic in new[] { "recentMedian /", "priorMedian", "* 100", "toFixed" })
            Assert.DoesNotContain(arithmetic, strip, StringComparison.Ordinal);

        // The three sentences it paints, all of them written next to the arithmetic they describe.
        foreach (var field in new[] { "t.headline", "t.moneyNote", "t.note", "t.cutPercent" })
            Assert.Contains(field, strip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cut_is_visible_on_the_one_figure_it_moved()
    {
        Assert.Contains("cut ${t.cutPercent}% for the slide", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_is_quiet_until_it_has_something_that_changes_the_decision()
    {
        Assert.Contains(".wn-trend {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-trend-cut {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-trend-falling {", Css, StringComparison.Ordinal);
        // Steady and unknown get no edge at all — they are the common cases.
        Assert.DoesNotContain(".wn-trend-steady {", Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".wn-trend-unknown {", Css, StringComparison.Ordinal);
        // And it folds at the same width the search strip does.
        Assert.Contains(".wn-trend-line {", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assets_were_republished()
    {
        Assert.True(AssetVersion(Html, "app.js?v=") >= 129);
        Assert.True(AssetVersion(Html, "style.css?v=") >= 112);
    }

    // ── The spoken line ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one line is glanced at, not studied. It speaks about the trend in exactly one case — the
    /// one where the badge the seller just heard is lower than the comps under it suggest.
    /// </summary>
    [Fact]
    public void The_spoken_line_speaks_only_when_the_trend_moved_the_money()
    {
        Assert.Contains("card.Trend is not { Discounted: true } trend", Speech, StringComparison.Ordinal);
        Assert.Contains("WhichWayItsGoing(card)", Speech, StringComparison.Ordinal);
        // No dollar figure. The ceiling in the badge is already the cut one, and a second number in
        // a spoken line is a second number to mishear.
        var clause = Section(Speech, "private static string WhichWayItsGoing", "private static string HowMany");
        Assert.DoesNotContain(":C0", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxBid", clause, StringComparison.Ordinal);
    }

    // ── Additive, as every WhatsNot session has been ─────────────────────────────────────────

    /// <summary>
    /// The constraint the whole feature has been built under: sold comps stay fully working, and
    /// WhatsNot is something extra standing on them.
    /// </summary>
    [Fact]
    public void Sold_comps_are_untouched()
    {
        foreach (var route in new[]
        {
            "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
            "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
            "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
        })
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);

        // The live price still runs on the shared pipeline, unchanged.
        Assert.Contains("await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find \"{to}\" after \"{from}\"");
        return text[start..end];
    }

    private static int AssetVersion(string html, string prefix)
    {
        var at = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{prefix}\" is no longer in index.html");
        var digits = new string(html[(at + prefix.Length)..].TakeWhile(char.IsDigit).ToArray());
        Assert.NotEqual("", digits);
        return int.Parse(digits);
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
