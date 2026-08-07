namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A lot that is three things reaches the live card through six links, and four of them are outside
/// C#: the reader is called by the advisor, the request carries the seller's own answer, the browser
/// posts it, the browser renders what came back, the stylesheet tells the three outcomes apart, and
/// the count is dropped when the lot changes. Break any one and the screen silently goes back to
/// pricing every lot as a single item — which looks exactly like a show that only sold single items.
/// </summary>
/// <remarks>
/// Five of these are decisions rather than plumbing, and every one of them is the sort of thing a
/// later tidy-up undoes without reading why: the browser computes no money of its own, the count is
/// never carried from one lot to the next, the quantity box outranks the name, the market
/// statistics are not multiplied, and the sold-comps path this whole screen stands on is untouched.
/// </remarks>
public class WhatsNotLotUnitsAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Reader = ReadSource("Services/LiveLotSize.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");

    // ── From the request to the ceiling ──────────────────────────────────────────────────────

    [Fact]
    public void The_advisor_asks_the_reader_how_many_and_prices_that_many()
    {
        Assert.Contains("LiveLotSize.Read(item, request.Quantity)", Advisor, StringComparison.Ordinal);
        Assert.Contains("breakEvenPerUnit * count", Advisor, StringComparison.Ordinal);
        Assert.Contains("AuctionSniperAnalyzer.MaxBidDetail(breakEvenAllIn, shipping, target, feePercent)",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// One ceiling function in the app, still. The lot's break-even is the per-unit break-even
    /// multiplied — nothing here re-derives what a bid is worth, or a live lot and the same three
    /// items bought one at a time would be two opinions about the same money.
    /// </summary>
    [Fact]
    public void The_lot_ceiling_is_the_same_ceiling_function_as_every_other_screens()
    {
        Assert.Contains("hunter.BreakEvenBuyPrice(resale, fees)", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("SolidProfit * count", Advisor, StringComparison.Ordinal);
        Assert.DoesNotContain("SolidProfit * units", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reader reads and never prices. A count is a claim about what is in the box; the ceiling
    /// is made of sales that happened, and the two live in different files so that a change to one
    /// cannot quietly become a change to the other.
    /// </summary>
    [Fact]
    public void The_reader_carries_no_money_at_all()
    {
        foreach (var money in new[] { "decimal MaxBid", "BreakEven", "ProfitCalculator", "ResalePricing", "FeeProfile" })
            Assert.DoesNotContain(money, Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reader_refuses_the_vocabulary_that_names_products_on_a_live_show()
    {
        // "36 pack", "24 ct", "50 count" are counts on a pallet and product names on a live show.
        // The divergence from LiquidationSelectors.CountUnits is the point of this file existing.
        var pieces = Section(Reader, "CountPiecesRegex", "SpecXContextRegex");

        foreach (var word in new[] { "pack", "ct", "count", "box", "case" })
            Assert.DoesNotContain($"|{word}", pieces, StringComparison.Ordinal);

        Assert.Contains("LiquidationSelectors.LeadingCount", Reader, StringComparison.Ordinal);
    }

    // ── The request, the endpoints and the sheet ─────────────────────────────────────────────

    [Fact]
    public void Every_endpoint_that_prices_a_card_carries_the_count()
    {
        // The re-price and the win both run the SAME Build as the fresh price, so all three have to
        // be asked the same question — a win recorded as one item would put a third of the night's
        // best lot on the sheet.
        Assert.Contains("public int? Quantity { get; set; }", ReadSource("Models/LiveBidModels.cs"),
            StringComparison.Ordinal);
        Assert.Contains("Quantity = Quantity,", ReadSource("Models/LiveBuyModels.cs"), StringComparison.Ordinal);

        foreach (var route in new[] { "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won" })
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_sends_the_quantity_everywhere_it_sends_the_bid()
    {
        foreach (var call in new[] { "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won" })
        {
            var post = Section(Js, $"safePost('{call}', {{", "});");
            Assert.Contains("quantity: wnQty()", post, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_blank_quantity_box_hands_the_question_back_to_the_lots_name()
    {
        // Blank is null and means "read it off the name" — a different answer from 1, which means
        // "I looked, and it is one" and is the undo for a count read wrong.
        var qty = Section(Js, "function wnQty() {", "\n  }");

        Assert.Contains("if (n == null) return null;", qty, StringComparison.Ordinal);
        Assert.Contains("Math.floor(n)", qty, StringComparison.Ordinal);
        Assert.Contains("whole >= 1 ? whole : null", qty, StringComparison.Ordinal);
    }

    // ── The count belongs to the lot it was counted on ───────────────────────────────────────

    /// <summary>
    /// The most expensive mistake this feature could make: a 3 left in the box from the last lot,
    /// tripling the ceiling on the next one, on a screen being read in two seconds.
    /// </summary>
    [Fact]
    public void The_count_is_dropped_whenever_the_lot_changes()
    {
        Assert.Contains("function wnResetQty()", Js, StringComparison.Ordinal);

        // Typed over, read off the show, and opened from the lot list — every way the item on
        // screen can become a different item.
        var typing = Section(Js, "$('wn-item')?.addEventListener('input', () => {", "});");
        Assert.Contains("wnResetQty();", typing, StringComparison.Ordinal);

        var read = Section(Js, "setVal('wn-item', read.title);", "wnPriceItem();");
        Assert.Contains("wnResetQty();", read, StringComparison.Ordinal);

        var openLot = Section(Js, "function wnOpenLot(index) {", "wnScheduleRebid();");
        Assert.Contains("wnResetQty();", openLot, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the one case where it must NOT be dropped: taking the photo's better name for a lot the
    /// typed name had counted. A product name carries no count, so the count moves to the box
    /// rather than evaporating and quietly dividing the ceiling by three.
    /// </summary>
    [Fact]
    public void A_better_name_for_the_same_lot_keeps_the_count()
    {
        var use = Section(Js, "function wnUsePhotoTitle(title) {", "wnPriceItem();");

        Assert.Contains("wnLastUnits.isLot", use, StringComparison.Ordinal);
        Assert.Contains("setVal('wn-qty', wnLastUnits.count)", use, StringComparison.Ordinal);
        Assert.DoesNotContain("wnResetQty();", use, StringComparison.Ordinal);
    }

    [Fact]
    public void Changing_the_quantity_re_answers_without_reading_ebay_again()
    {
        // The host says "you're getting three" while the bidding is running. That has to cost a
        // keystroke and no round trip past this machine.
        var start = Js.IndexOf("['wn-bid',", StringComparison.Ordinal);
        Assert.True(start >= 0, "the boxes that re-price off held comps are no longer a list");

        Assert.Contains("'wn-qty'", Js[start..Js.IndexOf("].forEach", start, StringComparison.Ordinal)],
            StringComparison.Ordinal);
        Assert.Contains("$(id)?.addEventListener('input', wnScheduleRebid);", Js, StringComparison.Ordinal);
    }

    // ── What the screen shows ────────────────────────────────────────────────────────────────

    [Fact]
    public void The_quantity_box_is_on_the_screen_next_to_the_bid()
    {
        Assert.Contains("id=\"wn-qty\"", Html, StringComparison.Ordinal);
        Assert.Contains("wn-field-qty", Html, StringComparison.Ordinal);
        Assert.Contains(".wn-field-qty", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_units_strip_says_the_servers_sentence_and_computes_nothing()
    {
        var strip = Section(Js, "const unitsStrip =", "</div>` : '';");

        // Every sentence on it is the server's. A browser that assembled "priced as 3 units" out of
        // a count and a template would be a second account of what the money means.
        Assert.Contains("esc(u.countUnstated ? (u.unstatedNote || u.note) : u.note)", strip, StringComparison.Ordinal);
        Assert.Contains("money2(u.maxBidPerUnit)", strip, StringComparison.Ordinal);
        Assert.Contains("money2(u.resalePerUnit)", strip, StringComparison.Ordinal);
        Assert.Contains("money2(u.profitPerUnit)", strip, StringComparison.Ordinal);

        // And no arithmetic: no division, no multiplication of anything the server sent.
        Assert.DoesNotContain("/ u.count", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("* u.count", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_outcomes_are_told_apart_by_the_stylesheet()
    {
        // A lot that was counted, a lot whose size nobody stated, and a count that was found and
        // deliberately not used are three different things to do next.
        foreach (var rule in new[] { ".wn-units", ".wn-units-ask", ".wn-units-refused", ".wn-units-each" })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        Assert.Contains("wn-units-${u.isLot ? 'lot' : (u.countUnstated ? 'ask' : 'refused')}", Js,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_resale_tile_says_which_scale_it_is_on()
    {
        // A "$600" resale tile on a lot of three and on a single item look identical, and one of
        // them is wrong by 200%.
        Assert.Contains("u.isLot ? `eBay resale · all ${u.count}` : 'eBay resale'", Js, StringComparison.Ordinal);
        Assert.Contains("${u.count} × ${money2(u.resalePerUnit)} each", Js, StringComparison.Ordinal);
        Assert.Contains("'per unit — where one of these lands'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spoken_line_says_how_many_before_it_says_any_other_number()
    {
        Assert.Contains("Join(Headline(card), HowMany(card)", Speech, StringComparison.Ordinal);
        Assert.Contains("For all {units.Count}", Speech, StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_is_not_a_second_live_region()
    {
        // One live region on this screen (#wn-say). Two announcements during a live sale is a
        // screen reader saying nothing usable.
        var strip = Section(Js, "const unitsStrip =", "</div>` : '';");

        Assert.DoesNotContain("aria-live", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_and_the_script_were_both_re_stamped()
    {
        Assert.True(AssetVersion(Html, "app.js?v=") >= 127, "app.js changed, so its stamp must move");
        Assert.True(AssetVersion(Html, "style.css?v=") >= 110, "style.css changed, so its stamp must move");
    }

    [Fact]
    public void The_screen_still_fits_a_window_down_the_side_of_a_stream()
    {
        // The WhatsNot one specifically — the stylesheet has more than one 620px block, and the
        // screen this feature lives on is the one that is a window down the side of a stream.
        var narrow = Section(Css.Replace("\r\n", "\n"), "@media (max-width: 620px) {\n  .wn-field,", "\n}");

        Assert.Contains(".wn-field-qty", narrow, StringComparison.Ordinal);
        Assert.Contains(".wn-units-each", narrow, StringComparison.Ordinal);
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
                     "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void A_single_item_is_priced_exactly_as_it_always_was()
    {
        // The compatibility property, on the screen side: with no count anywhere, the strip is not
        // rendered at all and every tile keeps the label it has always had.
        Assert.Contains("(u.isLot || u.countUnstated || u.refused) ?", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-price', 'click', wnPriceItem);", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-item\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lot_list_still_prices_every_line_through_the_same_endpoint()
    {
        // A pasted show list gets counts for free: each line goes through /api/whatsnot/bid, which
        // reads the count off that line's own name. Nothing about the list needed changing, and
        // nothing about it may quietly stop working either.
        Assert.Contains("safePost('/api/whatsnot/bid', {", Js, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/lots\"", Program, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

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
