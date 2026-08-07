namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What the queue costs reaches the live card through six links, and five of them are the sort of
/// thing a later tidy-up removes without reading why: the read taken at bid time, the ratio actually
/// applied to the price the ceiling is built from, the block on the card, the warning on the list,
/// the strip drawn on every card, and the clause in the spoken line. Break any one and the feature
/// silently does nothing on every card forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// <para>
/// Two of these are decisions rather than plumbing, and both are about what this is NOT. It is not a
/// duplicate haircut: the pile is never the charge, the measured slide across the wait is, so a deep
/// shelf of a flat product is priced as the first one. And it is not the trend cut in disguise: that
/// one re-bases the ninety-day median to today, this one carries today forward to the month the
/// seller's own unit reaches the front of the queue.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched and additive.
/// </para>
/// </remarks>
public class WhatsNotHoldAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Hold = ReadSource("Services/LiveHoldCost.cs");
    private static readonly string Stock = ReadSource("Services/LiveStockDepth.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string HoldModels = ReadSource("Models/LiveHoldModels.cs");

    // ── The read is taken, and its ratio is actually spent ────────────────────────────────────

    /// <summary>
    /// Read at bid time off figures already on the card, and hung on the card so the browser can
    /// draw it. A read taken and never attached is a strip that never appears.
    /// </summary>
    [Fact]
    public void The_wait_is_read_on_every_priced_card_and_carried_on_it()
    {
        Assert.Contains("LiveHoldCost.Read(", Advisor, StringComparison.Ordinal);
        Assert.Contains("card.Hold = hold;", Advisor, StringComparison.Ordinal);
        Assert.Contains("public LiveHoldRead Hold { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ratio is applied to the price the ceiling is built from — not merely reported beside it.
    /// A read that computes a haircut and hands it to nobody is the failure this whole file guards.
    /// </summary>
    [Fact]
    public void The_ratio_reaches_the_price_the_ceiling_is_built_from()
    {
        Assert.Contains("var bidAgainst = LiveHoldCost.Discount(priceToday, hold);", Advisor, StringComparison.Ordinal);

        // And it is the LAST of the three, applied to the output of the other two rather than to the
        // raw comps — the three are one price arrived at in the order the facts were measured.
        Assert.Contains("var priceToday = LiveCondition.Discount(LiveTrend.Discount(resale, trend), condition);",
            Advisor, StringComparison.Ordinal);
        Assert.True(
            Advisor.IndexOf("var priceToday =", StringComparison.Ordinal)
                < Advisor.IndexOf("var bidAgainst = LiveHoldCost.Discount", StringComparison.Ordinal),
            "the wait is priced off what the object fetches today, which is what the other two produce");
    }

    /// <summary>
    /// It is read from the price AFTER the other two cuts, so the erosion is measured against the
    /// figure the ceiling is really built on rather than against a median nothing is bid at.
    /// </summary>
    [Fact]
    public void The_wait_is_measured_against_todays_price_not_the_raw_median()
    {
        Assert.Contains("priceToday.ExpectedSale ?? priceToday.Median", Advisor, StringComparison.Ordinal);
        Assert.Contains("priceToday.EstimatedMonthlySales", Advisor, StringComparison.Ordinal);
    }

    /// <summary>The warning reaches the card's list, so a seller who never looks at the strip still
    /// hears why this ceiling is lower than the first one's.</summary>
    [Fact]
    public void The_warning_reaches_the_cards_list()
    {
        Assert.Contains("if (hold.Warning.Length > 0) card.Warnings.Add(hold.Warning);",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>Even the card nothing could price gets the block, so the strip renders the honest
    /// "nothing said how long the last one waits" rather than vanishing — which on that one card
    /// would read as "there is no queue".</summary>
    [Fact]
    public void The_unpriceable_card_still_gets_the_block()
    {
        Assert.Contains("card.Hold = LiveHoldCost.Read(", Advisor, StringComparison.Ordinal);
        Assert.Equal(2, CountOf(Advisor, "LiveHoldCost.Read("));
    }

    // ── What it refuses to be ────────────────────────────────────────────────────────────────

    /// <summary>
    /// <b>It is not a duplicate haircut.</b> There is exactly one assignment in the file that lowers
    /// the multiplier, and it sits behind the slide. Nothing may take money off for the size of the
    /// pile alone — that is the number nobody measured, and refusing it is why
    /// <see cref="Services.LiveStockDepth"/> still prices nothing.
    /// </summary>
    [Fact]
    public void Only_one_line_in_the_file_lowers_the_price()
    {
        Assert.Equal(1, CountOf(Hold, "read.ResaleMultiplier = "));
        Assert.Equal(1, CountOf(Hold, "read.Discounted = true"));

        // Initialised to "no cut" on the model, so every path that never reaches that line leaves
        // the price exactly as it found it.
        Assert.Contains("public decimal ResaleMultiplier { get; set; } = 1m;", HoldModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// The pile strip still prices nothing, and says so. If this ever fails, two blocks on one card
    /// are charging for the same shelf.
    /// </summary>
    [Fact]
    public void The_pile_strip_still_takes_nothing_off_anything()
    {
        Assert.DoesNotContain("ResaleMultiplier", Stock, StringComparison.Ordinal);
        Assert.DoesNotContain("Discounted", Stock, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxBid", Stock, StringComparison.Ordinal);
    }

    /// <summary>
    /// It never touches the ceiling, the break-even or the resale figure directly — it contributes a
    /// ratio and the shared arithmetic does the rest, exactly as the trend and condition reads do.
    /// </summary>
    [Fact]
    public void It_contributes_a_ratio_and_never_a_price()
    {
        Assert.DoesNotContain("MaxBid", Hold, StringComparison.Ordinal);
        Assert.DoesNotContain("BreakEven", Hold, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfitCalculator", Hold, StringComparison.Ordinal);
    }

    /// <summary>
    /// A projection must never take more off a ceiling than a measurement of sales that really
    /// happened, and the projection must never run further than the sales the line was fitted to.
    /// Both bars are tied to the trend read's own figures rather than being numbers of their own.
    /// </summary>
    [Fact]
    public void The_two_bars_are_tied_to_the_evidence_behind_them()
    {
        Assert.Contains("MaxProjectedMonths => LiveTrend.WindowDays * 2m / 30m", Hold, StringComparison.Ordinal);
        Assert.True(Services.LiveHoldCost.MaxHaircutPercent < Services.LiveTrend.MaxHaircutPercent);
    }

    /// <summary>
    /// It costs no lookup and no clock, so it re-answers on a held-comps re-price exactly as it does
    /// on a fresh one — in the milliseconds a climbing bid leaves.
    /// </summary>
    [Fact]
    public void It_reads_no_clock_and_makes_no_call()
    {
        var code = Hold[Hold.IndexOf("public static class LiveHoldCost", StringComparison.Ordinal)..];

        Assert.DoesNotContain("DateTime", code, StringComparison.Ordinal);
        Assert.DoesNotContain("await", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", code, StringComparison.Ordinal);
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drawn between the pile and the freight: it prices the wait the pile above it causes, and its
    /// first sentence only makes sense after that count. Both are above the ladder, which is where
    /// the money they moved is printed.
    /// </summary>
    [Fact]
    public void The_strip_sits_between_the_pile_and_the_freight()
    {
        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${stockStrip}\n      ${holdStrip}\n      ${shipStrip}\n      ${taxStrip}\n      ${budgetStrip}",
            template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every sentence on the strip is the server's. A headline assembled in the browser out of a
    /// unit count and a slope would be a second opinion about money, and it would be the one on
    /// screen.
    /// </summary>
    [Fact]
    public void The_strip_is_rendered_from_the_servers_own_words()
    {
        Assert.Contains("const hd = c.hold || {};", Js, StringComparison.Ordinal);
        Assert.Contains("esc(hd.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(hd.note)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(hd.moneyNote)", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The three cells are the whole argument for the cut — how long yours waits, how fast these are
    /// falling, what that is worth. The browser multiplies none of them together.
    /// </summary>
    [Fact]
    public void The_three_figures_are_carried_rather_than_multiplied_in_the_browser()
    {
        Assert.Contains("${hd.waitMonths} mo", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(hd.declinePerMonth)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(hd.erosionPerUnit)", Js, StringComparison.Ordinal);

        var strip = Js[Js.IndexOf("const hd = c.hold || {};", StringComparison.Ordinal)..];
        strip = strip[..strip.IndexOf("// ── What this one costs to get delivered", StringComparison.Ordinal)];
        Assert.DoesNotContain("*", strip, StringComparison.Ordinal);
    }

    /// <summary>
    /// The cells only appear when money actually moved. A wait with no cut behind it showing "gone
    /// by then −$0.00" would be a charge the card did not make.
    /// </summary>
    [Fact]
    public void The_cells_are_drawn_only_when_the_ceiling_really_moved()
    {
        Assert.Contains("${hd.discounted ? `", Js, StringComparison.Ordinal);
        Assert.Contains("hd.discounted ? `<span class=\"wn-hold-tag\">", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The resale tile names this cut too. A tile quietly lower than the comp table under it, naming
    /// two of the three haircuts that made it so, is exactly the sort of thing a seller re-reads and
    /// then distrusts the whole card over.
    /// </summary>
    [Fact]
    public void The_resale_tile_names_the_wait_alongside_the_other_two_cuts()
    {
        Assert.Contains("cut ${hd.cutPercent}% for the ${hd.waitMonths}-month wait", Js, StringComparison.Ordinal);

        // In the order the three were applied.
        var tile = Js[Js.IndexOf("cut ${t.cutPercent}% for the slide", StringComparison.Ordinal)..];
        Assert.True(
            tile.IndexOf("cond.cutPercent", StringComparison.Ordinal)
                < tile.IndexOf("hd.cutPercent", StringComparison.Ordinal),
            "the wait is named last, because it is applied last");
    }

    /// <summary>Only the state that moved money is coloured like a cut. A deep pile of a flat product
    /// has to be visibly free at a glance.</summary>
    [Fact]
    public void Only_the_state_that_charged_gets_the_colour()
    {
        Assert.Contains(".wn-hold-priced {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-hold-solo,", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-hold-steady,", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-hold-line {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-hold-cell-this {", Css, StringComparison.Ordinal);
    }

    /// <summary>The label and the headline stop sharing a line on a narrow panel — this screen is
    /// meant to sit beside a live feed, so it has to survive being half a window wide.</summary>
    [Fact]
    public void The_strip_folds_on_a_narrow_panel()
    {
        var narrow = Css[Css.LastIndexOf(".wn-hold-line {", StringComparison.Ordinal)..];
        Assert.Contains("flex-direction: column;", narrow[..80], StringComparison.Ordinal);
    }

    // ── The spoken line ──────────────────────────────────────────────────────────────────────

    /// <summary>One clause, last, and only in the state that took money off. The ceiling the seller
    /// heard at the start of the line already has this inside it.</summary>
    [Fact]
    public void The_line_says_it_only_when_the_ceiling_was_cut()
    {
        Assert.Contains("WhatTheWaitCosts(card)", Speech, StringComparison.Ordinal);
        Assert.Contains("if (card.Hold is not { Discounted: true } hold) return \"\";",
            Speech, StringComparison.Ordinal);

        // Last of all, immediately after the count that causes it.
        Assert.Contains("HowManyYoudHave(card), WhatTheWaitCosts(card));", Speech, StringComparison.Ordinal);
    }

    // ── The log ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller's own log prints the verdict and all three figures the cut was worked out from, so
    /// the first real "the app cut 12% on the eighth one" can be checked afterwards rather than
    /// taken on trust.
    /// </summary>
    [Fact]
    public void The_action_log_prints_the_working()
    {
        Assert.Contains("$\"hold {card.Hold.Verdict}\"", Program, StringComparison.Ordinal);
        Assert.Contains("card.Hold.WaitMonths", Program, StringComparison.Ordinal);
        Assert.Contains("card.Hold.DeclinePerMonth", Program, StringComparison.Ordinal);
        Assert.Contains("card.Hold.CutPercent", Program, StringComparison.Ordinal);
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

        // The live price still runs on the shared market pipeline, not on anything of its own.
        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
    }

    /// <summary>The wait never reaches the sold search. What eBay is asked is what the thing is, and
    /// how many the seller has of it is no part of that.</summary>
    [Fact]
    public void The_queue_never_reaches_the_question_ebay_is_asked()
    {
        var query = ReadSource("Services/LiveSearchQuery.cs");

        Assert.DoesNotContain("LiveHoldCost", query, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitsHeld", query, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitMonths", query, StringComparison.Ordinal);
    }

    /// <summary>The assets the browser is served are the ones these assertions were written
    /// against.</summary>
    [Fact]
    public void The_asset_versions_are_bumped()
    {
        Assert.Contains("app.js?v=138", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=121", Html, StringComparison.Ordinal);
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
