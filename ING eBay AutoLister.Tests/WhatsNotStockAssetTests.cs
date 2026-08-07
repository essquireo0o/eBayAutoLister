namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// How many of these the seller would then own reaches the live card through six links, and five of
/// them are the sort of thing a later tidy-up removes without reading why: the endpoint asks the buy
/// sheet, the sheet writes down how many units each won lot was, the advisor reads the pile, the
/// strip is rendered on every card, the browser computes none of it, and nothing anywhere takes a
/// dollar off a price for it. Break any one and the feature silently does nothing on every card
/// forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// Two of these are decisions rather than plumbing. Tonight's count is deliberately NOT held with
/// the comps — it is the one input the seller changes mid-lot by pressing Won it. And the constraint
/// every WhatsNot session has worked under: the sold-comps path this whole screen stands on is
/// untouched and additive.
/// </remarks>
public class WhatsNotStockAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string StockModels = ReadSource("Models/LiveStockModels.cs");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Stock = ReadSource("Services/LiveStockDepth.cs");
    private static readonly string Sheet = ReadSource("Services/LiveBuySheet.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");

    // ── The count reaches the card ───────────────────────────────────────────────────────────

    /// <summary>
    /// All three endpoints that build a card ask the sheet. A fresh price, a re-price and a recorded
    /// win are the same card by design; one of them counting a different pile would put two answers
    /// about one night's stock on one screen.
    /// </summary>
    [Fact]
    public void Every_endpoint_that_builds_a_card_counts_tonights_lots()
    {
        Assert.Contains("var tonight = sheet.UnitsWonOf(title);", Program, StringComparison.Ordinal);
        Assert.Contains("tonight: tonight", Program, StringComparison.Ordinal);

        // The re-price and the win both count off the item the comps were held for, never off a
        // title typed since — the same guard the token itself enforces.
        Assert.Equal(2, CountOf(Program, "sheet.UnitsWonOf(quote.Item)"));
    }

    /// <summary>
    /// Tonight's count is deliberately re-read rather than held beside the comps. The shelf cannot
    /// change while a lot is on screen; this can, because the seller changes it themselves by
    /// winning the previous lot of the same product thirty seconds ago.
    /// </summary>
    [Fact]
    public void Tonights_count_is_re_read_on_a_reprice_and_never_held()
    {
        var board = ReadSource("Services/LiveBidBoard.cs");
        Assert.DoesNotContain("LiveStockTonight", board, StringComparison.Ordinal);
        Assert.DoesNotContain("UnitsWonOf", board, StringComparison.Ordinal);

        // And the re-price still does no eBay read: the count comes off a list already in memory.
        Assert.Contains("Held comps, fresh count — no eBay read either way.", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// A won lot writes down how many things it bought. One hammer price on a lot of three is three
    /// units of stock, and a sheet that recorded it as one would tell the next card it is clear when
    /// it is not.
    /// </summary>
    [Fact]
    public void A_won_row_records_its_unit_count()
    {
        Assert.Contains("Units = Math.Max(1, card.Units?.Count ?? 1),", Sheet, StringComparison.Ordinal);
        Assert.Contains("Math.Max(1, l.Units)", Sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Listed rows are left out of tonight's count because listing writes a Deal Pipeline card, and
    /// the pipeline is where the shelf count comes from. Counting them in both places would report
    /// a stack of four as a stack of eight.
    /// </summary>
    [Fact]
    public void Listed_rows_are_left_out_so_nothing_is_counted_twice()
    {
        Assert.Contains("string.IsNullOrEmpty(l.ListedDraftFile)", Sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// Products are grouped by the same key the seller's own record and the Restock board use, so
    /// all three screens agree about which items are "these".
    /// </summary>
    [Fact]
    public void The_sheet_groups_products_the_way_the_rest_of_the_app_does()
    {
        Assert.Contains("JackpotHunter.ProductSignature", Sheet, StringComparison.Ordinal);
        Assert.Contains("JackpotHunter.ProductSignature", ReadSource("Services/OwnTrackRecord.cs"), StringComparison.Ordinal);
    }

    // ── It never touches a price ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole design. <c>LiveTrend</c> and <c>LiveCondition</c> cut the ceiling because they are
    /// about what the object fetches; this is about what a calendar does to money. The fourth one
    /// still resells for what the comps say — it just sells in April.
    /// </summary>
    [Fact]
    public void The_stock_read_is_attached_after_the_ceiling_and_changes_no_figure()
    {
        // Composed into no price. The two discounts that ARE allowed are still exactly two.
        Assert.Contains("LiveCondition.Discount(LiveTrend.Discount(resale, trend), condition)",
            Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveStockDepth.Discount", Advisor, StringComparison.Ordinal);

        // And the only thing it is allowed to do is say something.
        Assert.Contains("if (card.Stock.Warning.Length > 0) card.Warnings.Add(card.Stock.Warning);",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>Read on the unpriceable card too. "Nothing priced this AND you are holding four" is
    /// the most useful thing a card with no ceiling can say.</summary>
    [Fact]
    public void The_unpriceable_card_still_counts_the_pile()
    {
        Assert.Equal(2, CountOf(Advisor, "ApplyStock(card, own, tonight,"));
        Assert.Contains("monthlySales: 0m, daysToSellOne: null", Advisor, StringComparison.Ordinal);
    }

    /// <summary>The card carries the block on every answer, so its silence can never mean "nothing
    /// looked".</summary>
    [Fact]
    public void The_card_always_carries_a_stock_block()
    {
        Assert.Contains("public LiveStockRead Stock { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rendered under the unit count and above the ladder — the same count carried one step further,
    /// and above everything on the card that is money.
    /// </summary>
    [Fact]
    public void The_strip_sits_between_the_unit_count_and_the_ladder()
    {
        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${unitsStrip}\n      ${stockStrip}", template, StringComparison.Ordinal);
        // The freight strip was added between the pile and the ladder — the last thing said about
        // what winning costs, immediately before the numbers it costed. See WhatsNotShipAssetTests.
        Assert.Contains("${stockStrip}\n      ${shipStrip}\n      ${ladder}", template, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every sentence on the strip is the server's. A headline assembled in the browser out of a
    /// unit count and a sell-through rate would be a second opinion about the same shelf, and it
    /// would be the one on screen.
    /// </summary>
    [Fact]
    public void The_browser_paints_the_servers_words_and_counts_nothing()
    {
        Assert.Contains("esc(st.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(st.note)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(b.label)", Js, StringComparison.Ordinal);

        // No arithmetic anywhere in the block — the strip's own source, start to end.
        var start = Js.IndexOf("const st = c.stock || {};", StringComparison.Ordinal);
        Assert.True(start > 0);
        var block = Js[start..Js.IndexOf("// ── The press, not the price", start, StringComparison.Ordinal)];

        foreach (var arithmetic in new[] { " / ", " * ", " + ", "toFixed", "Math." })
            Assert.DoesNotContain(arithmetic, block, StringComparison.Ordinal);
    }

    /// <summary>The bars carry their own explanation, so a number three characters wide is not the
    /// whole of what the block said.</summary>
    [Fact]
    public void The_bars_carry_the_sentence_behind_them()
    {
        Assert.Contains("title=\"${esc(b.detail)}\"", Js, StringComparison.Ordinal);
        Assert.Contains("wn-stock-bar-${esc(b.kind)}", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every verdict the server can send has a rule, and the two that are news are the only two
    /// that get a colour: a strip on every card that shouted on all of them would train the eye to
    /// skip the two that matter.
    /// </summary>
    [Fact]
    public void Every_verdict_has_a_style_and_only_the_two_that_matter_are_loud()
    {
        foreach (var verdict in new[] { "single", "clear", "deep", "flooded", "blind", "none" })
            Assert.Contains($"= \"{verdict}\";", StockModels, StringComparison.Ordinal);

        Assert.Contains(".wn-stock-deep {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-stock-flooded {", Css, StringComparison.Ordinal);
        Assert.Contains("var(--warn", Css[Css.IndexOf(".wn-stock-deep {", StringComparison.Ordinal)..][..200]);

        // The quiet ones have no border colour rule of their own beyond the neutral one.
        Assert.DoesNotContain(".wn-stock-single {", Css, StringComparison.Ordinal);
        Assert.DoesNotContain(".wn-stock-clear {", Css, StringComparison.Ordinal);
    }

    /// <summary>The three bar kinds are styled, and the two that are already committed money are
    /// the ones that carry colour.</summary>
    [Fact]
    public void The_three_bar_kinds_are_styled()
    {
        Assert.Contains(".wn-stock-bar-shelf {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-stock-bar-tonight {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-stock-bars {", Css, StringComparison.Ordinal);
    }

    /// <summary>The strip folds like the four above it when the panel is narrow — the headline is
    /// the half of the line worth the width.</summary>
    [Fact]
    public void The_strip_folds_on_a_narrow_panel()
    {
        var narrow = Css[Css.IndexOf("@media (max-width: 620px) {", Css.IndexOf(".wn-stock {", StringComparison.Ordinal), StringComparison.Ordinal)..];
        Assert.Contains(".wn-stock-line {", narrow, StringComparison.Ordinal);
    }

    /// <summary>The strip's markup is escaped like everything else the server sends — a lot's name
    /// reaches this block through the shelf and the sheet.</summary>
    [Fact]
    public void Everything_on_the_strip_is_escaped()
    {
        var start = Js.IndexOf("const st = c.stock || {};", StringComparison.Ordinal);
        var block = Js[start..Js.IndexOf("// ── The press, not the price", start, StringComparison.Ordinal)];

        // Every interpolation of a server string goes through esc(); the only bare ones are the
        // integer unit counts, which cannot carry markup.
        foreach (var raw in new[] { "${st.headline}", "${st.note}", "${b.label}", "${b.detail}" })
            Assert.DoesNotContain(raw, block, StringComparison.Ordinal);
    }

    // ── The spoken line ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The one line speaks about the shelf only when the pile has crossed a bar. Silent on the first
    /// one and on a stack the market clears, which is nearly every card — a clause on every lot in
    /// exchange for a count the strip already carries would cost the line the thing it is for.
    /// </summary>
    [Fact]
    public void The_spoken_line_mentions_the_pile_only_when_it_crossed_a_bar()
    {
        Assert.Contains("HowManyYoudHave(card)", Speech, StringComparison.Ordinal);
        Assert.Contains("if (card.Stock is not { } stock || !stock.AlreadyStocked) return \"\";",
            Speech, StringComparison.Ordinal);
        Assert.Contains("LiveStockVerdicts.Flooded or LiveStockVerdicts.Deep", Speech, StringComparison.Ordinal);

        // Both exits of Build set the line, so both say it.
        Assert.Equal(2, CountOf(Speech, "HowManyYoudHave(card)"));
    }

    /// <summary>It never states a price. Nothing about a shelf changes what the thing is worth, and
    /// a second dollar figure in a spoken line is a second number to mishear.</summary>
    [Fact]
    public void The_spoken_clause_carries_no_money()
    {
        var start = Speech.IndexOf("private static string HowManyYoudHave", StringComparison.Ordinal);
        var clause = Speech[start..Speech.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal)];

        foreach (var money in new[] { "ToString(\"C", ":C0", "MaxBid", "ResalePrice" })
            Assert.DoesNotContain(money, clause, StringComparison.Ordinal);
    }

    // ── Sold comps, untouched ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The constraint every WhatsNot session has worked under. This screen stands on the sold-comps
    /// path, which is read here and never changed — every endpoint the feature has ever registered
    /// is still registered, and the live price still runs on the same pipeline.
    /// </summary>
    [Fact]
    public void Sold_comps_are_untouched_and_every_endpoint_still_exists()
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

        Assert.Contains("AnalyzeProductAsync(", Program, StringComparison.Ordinal);

        // And the pile never reaches the query. The comp lookup is asked for the item, not for how
        // many of it the seller owns.
        Assert.DoesNotContain("LiveStockDepth", ReadSource("Services/LiveSearchQuery.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("Stock", ReadSource("Services/LiveSearchQuery.cs"), StringComparison.Ordinal);
    }

    /// <summary>The assets the browser loads are the ones that were changed.</summary>
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        Assert.Contains("app.js?v=134", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=117", Html, StringComparison.Ordinal);
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
