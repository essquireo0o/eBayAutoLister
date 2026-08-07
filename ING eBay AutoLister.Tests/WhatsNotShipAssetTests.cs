namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What a lot really costs to get delivered reaches the live card through seven links, and six of
/// them are the sort of thing a later tidy-up removes without reading why: two boxes on the screen,
/// both rates and the show on every request the tab makes, the endpoints asking the buy sheet which
/// box is already open, the sheet writing the show down on the won row, the advisor spending the
/// marginal figure rather than the typed one, and the strip drawn on every card. Break any one and
/// the feature silently does nothing on every card forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// Three of these are decisions rather than plumbing. This is the only read on the card that can
/// RAISE a ceiling, so it fails closed to full freight on every missing gate. A typed zero is a real
/// answer and a blank box is not — free combined shipping is the commonest live-selling arrangement
/// there is. And the constraint every WhatsNot session has worked under: the sold-comps path this
/// whole screen stands on is untouched and additive.
/// </remarks>
public class WhatsNotShipAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Ship = ReadSource("Services/LiveShipShare.cs");
    private static readonly string Sheet = ReadSource("Services/LiveBuySheet.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string BuyModels = ReadSource("Models/LiveBuyModels.cs");
    private static readonly string ShipModels = ReadSource("Models/LiveShipModels.cs");

    // ── The two boxes exist and are asked for ────────────────────────────────────────────────

    /// <summary>The show and the extra-item rate are boxes on the screen, next to the shipping rate
    /// they qualify.</summary>
    [Fact]
    public void The_show_and_the_extra_item_rate_are_on_the_screen()
    {
        Assert.Contains("id=\"wn-show\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-ship-add\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-ship\"", Html, StringComparison.Ordinal);

        // Immediately before the pair they belong with — the three of them are one fact together.
        Assert.True(
            Html.IndexOf("id=\"wn-show\"", StringComparison.Ordinal)
                < Html.IndexOf("id=\"wn-ship\"", StringComparison.Ordinal),
            "the show comes before the two rates it qualifies");
        Assert.True(
            Html.IndexOf("id=\"wn-ship\"", StringComparison.Ordinal)
                < Html.IndexOf("id=\"wn-ship-add\"", StringComparison.Ordinal),
            "the pair reads as 'first one / each one after'");
    }

    /// <summary>
    /// The extra-item box is a placeholder-dash, not a zero. A box pre-filled with 0 would tell every
    /// card in the app that this show ships extras free, which is a ceiling nobody entered.
    /// </summary>
    [Fact]
    public void The_extra_item_box_starts_empty_rather_than_at_zero()
    {
        var field = Html[Html.IndexOf("id=\"wn-ship-add\"", StringComparison.Ordinal)..];
        field = field[..field.IndexOf("/>", StringComparison.Ordinal)];

        Assert.Contains("placeholder=\"—\"", field, StringComparison.Ordinal);
        Assert.DoesNotContain("value=", field, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every request the tab makes carries both. The fresh price, the re-price, the recorded win and
    /// the lot list are the same question by design; one of them costing the freight differently
    /// would put two answers about one box on one screen.
    /// </summary>
    [Fact]
    public void Every_request_the_tab_makes_carries_the_show_and_the_extra_rate()
    {
        Assert.Equal(4, CountOf(Js, "additionalItemShipping: wnNumber('wn-ship-add')"));
        Assert.Equal(4, CountOf(Js, "showName: wnShow()"));

        // One reader for the box, so no caller can normalise it differently on the way out.
        Assert.Contains("function wnShow()", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both boxes re-answer off the held comps, like every other figure that is not the item. A
    /// seller three lots into a show types one rate and watches every ceiling for the rest of the
    /// night rise by what the box is already paying for — with no eBay in it.
    /// </summary>
    [Fact]
    public void Both_boxes_reprice_without_reading_ebay()
    {
        var template = Js.Replace("\r\n", "\n");
        var at = template.IndexOf("$(id)?.addEventListener('input', wnScheduleRebid)", StringComparison.Ordinal);
        Assert.True(at > 0, "the re-price list is still a literal list of box ids");

        var ids = template[..at];
        ids = ids[ids.LastIndexOf("['wn-bid'", StringComparison.Ordinal)..];

        Assert.Contains("'wn-ship-add'", ids, StringComparison.Ordinal);
        Assert.Contains("'wn-show'", ids, StringComparison.Ordinal);
    }

    /// <summary>
    /// The show box is deliberately NOT remembered between sessions, unlike the two rates. It is the
    /// one field on that row that is only true for tonight, and a handle carried in from last week
    /// would quietly combine this lot's freight with somebody else's box.
    /// </summary>
    [Fact]
    public void The_rates_are_remembered_and_the_show_is_not()
    {
        Assert.Contains("shipAdd: $('wn-ship-add')?.value", Js, StringComparison.Ordinal);
        Assert.Contains("if (saved.shipAdd != null) setVal('wn-ship-add', saved.shipAdd);", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("setVal('wn-show', saved", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Filled in from a show that was read, and only into an EMPTY box. The seller's own wording
    /// outranks a slug off a URL, and rewriting it mid-show would silently re-point every ceiling's
    /// freight at a different box.
    /// </summary>
    [Fact]
    public void A_read_show_fills_the_box_only_when_it_is_empty()
    {
        Assert.Contains("if (!wnShow() && read.url) setVal('wn-show', read.url);", Js, StringComparison.Ordinal);
    }

    // ── The endpoints ask which box is open ──────────────────────────────────────────────────

    /// <summary>All three endpoints that build a card ask the sheet. A fresh price, a re-price and a
    /// recorded win are the same card, and one of them costing a different freight would be two
    /// answers about one parcel.</summary>
    [Fact]
    public void Every_endpoint_that_builds_a_card_asks_which_box_is_open()
    {
        Assert.Equal(3, CountOf(Program, "sheet.ShippingOnShow(req.ShowName)"));
        Assert.Contains("ship: freight", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Counted rather than held beside the comps, for the same reason tonight's stock is: it is an
    /// input the seller changes themselves, by winning the previous lot of the same show thirty
    /// seconds ago and pressing Won it.
    /// </summary>
    [Fact]
    public void The_open_box_is_re_counted_on_a_reprice_and_never_held()
    {
        var board = ReadSource("Services/LiveBidBoard.cs");

        Assert.DoesNotContain("LiveShipTonight", board, StringComparison.Ordinal);
        Assert.DoesNotContain("ShippingOnShow", board, StringComparison.Ordinal);

        // And the re-price still does no eBay read: the count comes off a list already in memory.
        Assert.Contains("no file I/O in the common case", Sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one place this differs from the stock count, and it is deliberate: a listed row is still
    /// in the box. Drafting a listing moves stock onto the Deal Pipeline; it changes nothing about
    /// the parcel the seller is waiting on.
    /// </summary>
    [Fact]
    public void A_listed_row_leaves_the_stock_count_and_stays_in_the_box()
    {
        var stock = Sheet[Sheet.IndexOf("public LiveStockTonight UnitsWonOf", StringComparison.Ordinal)..];
        stock = stock[..stock.IndexOf("public LiveShipTonight ShippingOnShow", StringComparison.Ordinal)];

        var box = Sheet[Sheet.IndexOf("public LiveShipTonight ShippingOnShow", StringComparison.Ordinal)..];
        box = box[..box.IndexOf("public WonLot? Find", StringComparison.Ordinal)];

        Assert.Contains("string.IsNullOrEmpty(l.ListedDraftFile)", stock, StringComparison.Ordinal);
        Assert.DoesNotContain("ListedDraftFile", box, StringComparison.Ordinal);
    }

    /// <summary>The show goes on the won row, because the NEXT lot asks the sheet whether that box
    /// exists at all.</summary>
    [Fact]
    public void The_won_row_records_which_show_it_came_off()
    {
        Assert.Contains("ShowName = card.Ship?.ShowName ?? \"\"", Sheet, StringComparison.Ordinal);
        Assert.Contains("public string ShowName { get; set; } = \"\";", BuyModels, StringComparison.Ordinal);

        // And the win carries both back, or the row would record the first-item rate on a lot that
        // was costed at the extra-item one.
        Assert.Contains("AdditionalItemShipping = AdditionalItemShipping,", BuyModels, StringComparison.Ordinal);
        Assert.Contains("ShowName = ShowName,", BuyModels, StringComparison.Ordinal);
    }

    // ── The ceiling spends the marginal figure ───────────────────────────────────────────────

    /// <summary>
    /// The advisor spends what the read decided, not what was typed. One substitution, at the top,
    /// so every dollar below it — the landed cost, the break-even, the ceiling, the next press — is
    /// costed on the same freight.
    /// </summary>
    [Fact]
    public void The_ceiling_is_costed_on_the_marginal_freight()
    {
        Assert.Contains("var shipping = freight.Marginal;", Advisor, StringComparison.Ordinal);
        Assert.Contains("Ship = freight,", Advisor, StringComparison.Ordinal);

        // And nothing else in the advisor reads the raw request field behind its back.
        var uses = CountOf(Advisor, "request.ShippingCost");
        Assert.Equal(1, uses);
    }

    /// <summary>
    /// It fails closed. The marginal figure starts at the full first-item rate and every path either
    /// leaves it there or lowers it, which is what makes "a card that fails a gate is priced exactly
    /// as it was before this existed" a property of the code rather than a promise.
    /// </summary>
    [Fact]
    public void The_read_starts_at_full_freight_and_only_ever_comes_down()
    {
        Assert.Contains("Marginal = first,", Ship, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(Ship, "read.Marginal ="));
        Assert.Contains("read.Marginal = read.AdditionalItemShipping;", Ship, StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero is an answer and blank is not. Free combined shipping is the commonest live-selling
    /// arrangement there is, so the read carries a separate "was it stated" flag rather than testing
    /// the rate for zero — and the browser sends null for an empty box.
    /// </summary>
    [Fact]
    public void A_blank_extra_rate_is_never_read_as_a_free_one()
    {
        Assert.Contains("var stated = additionalItemShipping is not null;", Ship, StringComparison.Ordinal);
        Assert.Contains("public bool AdditionalStated", ShipModels, StringComparison.Ordinal);
        Assert.Contains("public decimal? AdditionalItemShipping", BidModels, StringComparison.Ordinal);

        // wnNumber returns null for an empty box and a number for a typed 0 — the distinction the
        // whole feature stands on, and it already had a comment saying so.
        Assert.Contains("Empty means \"not stated\", which is a different answer from zero", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// It knows about freight and about nothing else. A resale price or a ceiling in here would be a
    /// second opinion about money — this decides one input and hands it back.
    /// </summary>
    [Fact]
    public void The_read_prices_nothing_and_costs_no_clock()
    {
        foreach (var forbidden in new[] { "MaxBid", "BreakEven", "ResalePrice", "Median", "DateTime", "await", "HttpClient" })
            Assert.DoesNotContain(forbidden, Ship, StringComparison.Ordinal);
    }

    // ── The strip and the line ───────────────────────────────────────────────────────────────

    /// <summary>Drawn on every card, between the pile and the ladder, and the browser adds nothing
    /// up: every word and every dollar on it is the server's.</summary>
    [Fact]
    public void The_strip_is_rendered_from_the_servers_own_words()
    {
        Assert.Contains("const sh = c.ship || {};", Js, StringComparison.Ordinal);
        Assert.Contains("${shipStrip}", Js, StringComparison.Ordinal);
        Assert.Contains("esc(sh.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(sh.note)", Js, StringComparison.Ordinal);

        // The shelf-time strip sits above it, between the pile and this, because it prices the wait
        // that pile causes. Below it is the other half of what winning costs — the tax the
        // marketplace collects on the hammer, then what is left of tonight's money — and then
        // the ladder they were all costed into.
        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${holdStrip}\n      ${shipStrip}\n      ${taxStrip}\n      ${budgetStrip}",
            template, StringComparison.Ordinal);
    }

    /// <summary>The three box figures are the server's too. A browser that added "so far" to "this
    /// lot" would be a second opinion about a parcel.</summary>
    [Fact]
    public void The_box_figures_are_carried_rather_than_added_up_in_the_browser()
    {
        Assert.Contains("moneyExact(sh.shippingSoFar)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(sh.marginal)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(sh.shippingWithThisLot)", Js, StringComparison.Ordinal);
        Assert.Contains("ShippingWithThisLot = Math.Round(read.ShippingSoFar + read.Marginal, 2)",
            Ship, StringComparison.Ordinal);
    }

    /// <summary>The five states have five edges, and the good one is the only positive colour on
    /// this card — everything else here either describes the market or takes money off.</summary>
    [Fact]
    public void Every_state_has_an_edge_and_only_the_good_one_is_green()
    {
        foreach (var state in new[] { "combined", "unstated", "none", "alone", "first" })
            Assert.Contains($".wn-ship-{state}", Css, StringComparison.Ordinal);

        var combined = Css[Css.IndexOf(".wn-ship-combined {", StringComparison.Ordinal)..];
        Assert.Contains("var(--ok", combined[..combined.IndexOf('}')], StringComparison.Ordinal);
    }

    /// <summary>It folds on a narrow card, like every strip above it.</summary>
    [Fact]
    public void The_strip_folds_on_a_narrow_card()
    {
        var narrow = Css[Css.IndexOf(".wn-ship-line {", StringComparison.Ordinal)..];
        Assert.Contains(".wn-ship-line", Css[Css.IndexOf("@media", StringComparison.Ordinal)..], StringComparison.Ordinal);
        Assert.Contains(".wn-field-show", Css, StringComparison.Ordinal);
        Assert.NotEmpty(narrow);
    }

    /// <summary>The spoken line carries it in two states and no more, and it never repeats what the
    /// strip already drew.</summary>
    [Fact]
    public void The_line_speaks_in_two_states_only()
    {
        Assert.Contains("WhatItShipsFor(card)", Speech, StringComparison.Ordinal);

        var clause = Speech[Speech.IndexOf("private static string WhatItShipsFor", StringComparison.Ordinal)..];
        clause = clause[..clause.IndexOf("private static string Lots(", StringComparison.Ordinal)];

        Assert.Contains("LiveShipVerdicts.Combined", clause, StringComparison.Ordinal);
        Assert.Contains("LiveShipVerdicts.Unstated", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveShipVerdicts.First", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveShipVerdicts.Alone", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveShipVerdicts.None", clause, StringComparison.Ordinal);
    }

    // ── Sold comps, untouched ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The constraint every WhatsNot session has worked under. The sold-comps path this whole screen
    /// stands on is untouched and this is purely additive to it.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched()
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

        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
    }

    /// <summary>The freight never reaches the query. What eBay is asked is about the item; this is
    /// about a parcel.</summary>
    [Fact]
    public void The_freight_never_reaches_the_sold_search()
    {
        var query = ReadSource("Services/LiveSearchQuery.cs");

        Assert.DoesNotContain("LiveShipShare", query, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowName", query, StringComparison.Ordinal);
    }

    /// <summary>The assets the browser loads are the ones that were changed.</summary>
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        Assert.Contains("app.js?v=137", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=120", Html, StringComparison.Ordinal);
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
