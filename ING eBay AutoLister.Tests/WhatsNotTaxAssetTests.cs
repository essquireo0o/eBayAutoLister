namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The sales tax the marketplace collects reaches the live card through seven links, and six of them
/// are the sort of thing a later tidy-up removes without reading why: two boxes on the form, the
/// browser sending both on every path a lot is costed on, the advisor reading them, the ceiling
/// dividing by the rate, the strip rendering it, the win carrying it onto the buy sheet, and nothing
/// anywhere assuming a rate on the seller's behalf. Break any one and the feature silently does
/// nothing on every card forever, which looks exactly like working.
/// </summary>
/// <remarks>
/// <para>
/// Two of these are decisions rather than plumbing. The certificate <b>outranks</b> the rate box,
/// because most resellers file one and a ceiling cut for a cost they do not pay loses them lots. And
/// an empty box charges nothing rather than a default: a card whose ceiling quietly fell 7% for a
/// number nobody typed is a card the seller cannot check.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched, and this is purely additive to it.
/// </para>
/// </remarks>
public class WhatsNotTaxAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Tax = ReadSource("Services/LiveSalesTax.cs");
    private static readonly string TaxModels = ReadSource("Models/LiveTaxModels.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string BuyModels = ReadSource("Models/LiveBuyModels.cs");
    private static readonly string Sniper = ReadSource("Services/AuctionSniperAnalyzer.cs");
    private static readonly string Increment = ReadSource("Services/LiveBidIncrement.cs");
    private static readonly string Record = ReadSource("Services/OwnTrackRecord.cs");

    // ── The two boxes ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A rate and a tick, on the same row as the buyer's premium — the two things the platform bills
    /// on top of the hammer belong beside each other.
    /// </summary>
    [Fact]
    public void The_form_has_a_rate_and_a_certificate()
    {
        Assert.Contains("id=\"wn-tax\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-exempt\"", Html, StringComparison.Ordinal);
        Assert.Contains("type=\"checkbox\"", Html, StringComparison.Ordinal);

        // A dash, not a figure. A placeholder of "7.5" would read as a default that is being
        // charged, and nothing here charges a rate nobody typed.
        var box = Html[Html.IndexOf("id=\"wn-tax\"", StringComparison.Ordinal)..];
        Assert.Contains("placeholder=\"—\"", box[..120], StringComparison.Ordinal);
    }

    /// <summary>
    /// Both reach every path a lot is costed on — the fresh price, the instant re-price, a recorded
    /// win and the show's lot list. A win recorded without them would report a night as costing less
    /// than the seller's bank statement will.
    /// </summary>
    [Fact]
    public void Both_reach_every_endpoint_that_costs_a_lot()
    {
        Assert.Contains(
            "salesTaxPercent: wnNumber('wn-tax'), taxExempt: $('wn-exempt')?.checked === true",
            Js, StringComparison.Ordinal);

        // One helper, spread into all four payloads — a second assembly of the same two fields is
        // how one of them ends up sending a rate the others do not.
        Assert.Equal(4, Occurrences(Js, "...wnTaxFields(),"));
    }

    /// <summary>
    /// Typing a rate re-prices off the held comps with no eBay in it, and so does ticking the
    /// certificate — which is what lets a seller fix the whole night's ceilings between two lots.
    /// A checkbox fires change and not input, so it is bound separately or it is not bound at all.
    /// </summary>
    [Fact]
    public void Both_reprice_without_reading_ebay()
    {
        var template = Js.Replace("\r\n", "\n");
        var at = template.IndexOf("$(id)?.addEventListener('input', wnScheduleRebid)", StringComparison.Ordinal);
        Assert.True(at > 0, "the re-price list is still a literal list of box ids");

        var ids = template[..at];
        ids = ids[ids.LastIndexOf("['wn-bid'", StringComparison.Ordinal)..];
        Assert.Contains("'wn-tax'", ids, StringComparison.Ordinal);

        Assert.Contains("$('wn-exempt')?.addEventListener('change', wnScheduleRebid);",
            Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Remembered between lots and between shows, like the shipping and the premium — a rate is a
    /// fact about where the seller lives and a certificate is a fact about their paperwork, and
    /// neither changes when the next lot comes up. The rate is restored even when it is "0", because
    /// a stated zero is a real answer and an empty box is a different one.
    /// </summary>
    [Fact]
    public void Both_are_remembered()
    {
        Assert.Contains("tax: $('wn-tax')?.value ?? ''", Js, StringComparison.Ordinal);
        Assert.Contains("exempt: $('wn-exempt')?.checked === true", Js, StringComparison.Ordinal);
        Assert.Contains("if (saved.tax != null) setVal('wn-tax', saved.tax);", Js, StringComparison.Ordinal);
        Assert.Contains("if (exempt) exempt.checked = saved.exempt === true;", Js, StringComparison.Ordinal);
    }

    // ── The read ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_request_carries_a_rate_and_a_certificate()
    {
        Assert.Contains("public decimal? SalesTaxPercent { get; set; }", BidModels, StringComparison.Ordinal);
        Assert.Contains("public bool? TaxExempt { get; set; }", BidModels, StringComparison.Ordinal);

        // And so does a win, so the row on the buy sheet is costed at the card's own terms.
        Assert.Contains("public decimal? SalesTaxPercent { get; set; }", BuyModels, StringComparison.Ordinal);
        Assert.Contains("public bool? TaxExempt { get; set; }", BuyModels, StringComparison.Ordinal);
        Assert.Contains("SalesTaxPercent = SalesTaxPercent,", BuyModels, StringComparison.Ordinal);
        Assert.Contains("TaxExempt = TaxExempt,", BuyModels, StringComparison.Ordinal);
    }

    [Fact]
    public void The_advisor_asks_the_read_and_prices_what_it_answers()
    {
        Assert.Contains(
            "LiveSalesTax.Read(request.SalesTaxPercent, request.TaxExempt == true, bid, feePercent)",
            Advisor, StringComparison.Ordinal);
        Assert.Contains("var taxPercent = tax.RatePercent;", Advisor, StringComparison.Ordinal);
        Assert.Contains("Tax = tax,", Advisor, StringComparison.Ordinal);
        Assert.Contains("SalesTax = tax.Charged,", Advisor, StringComparison.Ordinal);
        Assert.Contains("public LiveTaxRead Tax { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four states, spelled once, so the strip, the CSS, the log and these tests cannot drift apart.
    /// </summary>
    [Fact]
    public void The_four_states_are_spelled_once()
    {
        foreach (var state in new[] { "none", "exempt", "free", "charged" })
            Assert.Contains($"= \"{state}\";", TaxModels, StringComparison.Ordinal);
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One ceiling function in the app, still. The tax multiplies the bid exactly as the premium
    /// does, so it divides back out with it — the same <c>k = (1+premium)(1+tax)</c> the liquidation
    /// board's ceiling has always used. A second ceiling that took the tax off as a flat cost would
    /// quote a bid the arithmetic behind it never allowed.
    /// </summary>
    [Fact]
    public void The_tax_divides_out_of_the_one_ceiling_function()
    {
        Assert.Contains(
            "(1m + Math.Max(0m, buyerFeePercent) / 100m) * (1m + Math.Max(0m, salesTaxPercent) / 100m)",
            Sniper, StringComparison.Ordinal);
        Assert.Contains("breakEvenAllIn, shipping, target, feePercent, taxPercent)",
            Advisor, StringComparison.Ordinal);

        // And the same expression is the landed cost and the walk-away line, in the advisor.
        Assert.Contains("BreakEvenBid(breakEvenAllIn, feePercent, shipping, taxPercent)",
            Advisor, StringComparison.Ordinal);
        Assert.Contains("LandedCost(bid, feePercent, shipping, taxPercent)", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// The press is costed at the same tax as the price, and so is the second ceiling the seller's
    /// own record produces. Two figures on one card costed on different terms is the failure this
    /// whole file exists to catch.
    /// </summary>
    [Fact]
    public void Everything_else_on_the_card_is_costed_at_the_same_rate()
    {
        Assert.Contains("card.Tax.RatePercent)", Increment, StringComparison.Ordinal);
        Assert.Contains("card.Tax.RatePercent);", Advisor, StringComparison.Ordinal);
        Assert.Contains("proceeds, shipping, targetRoiPercent, buyerFeePercent, salesTaxPercent)",
            Record, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tax is charged on the hammer plus the premium and not on the freight — the app's one
    /// sales-tax arithmetic, which is <see cref="ING_eBay_AutoLister.Services.LotAnalyzer"/>'s.
    /// </summary>
    [Fact]
    public void The_freight_is_added_after_the_tax_rather_than_taxed()
    {
        Assert.Contains(
            "bid * (1m + Math.Max(0m, buyerFeePercent) / 100m) * (1m + Math.Max(0m, salesTaxPercent) / 100m)",
            Advisor, StringComparison.Ordinal);
        Assert.Contains("Math.Round(taxed + Math.Max(0m, shipping), 2)", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing here charges a rate the seller did not type. <c>RatePercent</c> is initialised to zero
    /// on the read and set on exactly one line — the charged state — which is what makes "a card with
    /// an empty tax box is priced as it was before this file existed" a property of the code.
    /// </summary>
    [Fact]
    public void The_rate_is_raised_above_zero_on_exactly_one_line()
    {
        Assert.Equal(1, Occurrences(Tax, "read.RatePercent = rate;"));
        Assert.Contains("RatePercent = 0m,", Tax, StringComparison.Ordinal);

        // And the US average appears only where the silence is sized — never as something charged.
        Assert.Contains("read.Exposure = TaxOn(basis, TypicalRatePercent);", Tax, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(Tax, "TaxOn(basis, TypicalRatePercent)"));
    }

    /// <summary>The certificate is read before the rate box, which is what makes it outrank it.</summary>
    [Fact]
    public void The_certificate_is_judged_first()
    {
        var judge = Tax[Tax.IndexOf("private static void Judge(", StringComparison.Ordinal)..];

        var exemptAt = judge.IndexOf("if (read.Exempt)", StringComparison.Ordinal);
        var statedAt = judge.IndexOf("if (!read.Stated)", StringComparison.Ordinal);
        var chargedAt = judge.IndexOf("read.RatePercent = rate;", StringComparison.Ordinal);

        Assert.True(exemptAt > 0 && exemptAt < statedAt && statedAt < chargedAt,
            "the certificate has to be judged before the rate box or it does not outrank it");
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>Drawn on every card, under the freight, and the browser adds nothing up: every word
    /// and every dollar on it is the server's.</summary>
    [Fact]
    public void The_strip_is_rendered_from_the_servers_own_words()
    {
        Assert.Contains("const tx = c.tax || {};", Js, StringComparison.Ordinal);
        Assert.Contains("esc(tx.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(tx.note)", Js, StringComparison.Ordinal);

        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${shipStrip}\n      ${taxStrip}\n      ${ladder}", template, StringComparison.Ordinal);

        // The three figures are carried, not multiplied here.
        Assert.Contains("moneyExact(tx.taxableBase)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(tx.charged)", Js, StringComparison.Ordinal);
        Assert.Contains("tx.cutPercent", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("tx.ratePercent /", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four states, four edges. Only the silence is drawn as an error, because the other three are
    /// all states of an accurate card — including the one that charges.
    /// </summary>
    [Fact]
    public void Every_state_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var rule in new[]
                 {
                     ".wn-tax", ".wn-tax-none", ".wn-tax-charged", ".wn-tax-exempt", ".wn-tax-free",
                     ".wn-tax-line", ".wn-tax-tag", ".wn-tax-box", ".wn-tax-cell", ".wn-tax-note",
                     ".wn-field-tax", ".wn-field-exempt",
                 })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        var none = Css[Css.IndexOf(".wn-tax-none {", StringComparison.Ordinal)..];
        Assert.Contains("var(--danger", none[..none.IndexOf('}')], StringComparison.Ordinal);

        Assert.Contains("style.css?v=119", Html, StringComparison.Ordinal);
        Assert.Contains("app.js?v=136", Html, StringComparison.Ordinal);
    }

    /// <summary>It folds on a narrow card, like every strip above it — this screen is used as a
    /// column down the side of a live stream.</summary>
    [Fact]
    public void The_strip_folds_on_a_narrow_card()
    {
        var narrow = Css[Css.IndexOf("@media", StringComparison.Ordinal)..];
        Assert.Contains(".wn-tax-line", narrow, StringComparison.Ordinal);
        Assert.Contains(".wn-field-exempt", narrow, StringComparison.Ordinal);
    }

    /// <summary>The verdict and the money reach the seller's own action log, which is where the
    /// first real "the app said stop because it charged tax my certificate covers" will show up.</summary>
    [Fact]
    public void The_verdict_and_the_money_are_logged()
    {
        Assert.Contains("tax {card.Tax.Verdict}", Program, StringComparison.Ordinal);
        Assert.Contains("card.Tax.RatePercent:0.##", Program, StringComparison.Ordinal);
        Assert.Contains("card.Tax.Charged:C", Program, StringComparison.Ordinal);
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

    /// <summary>
    /// The tax never reaches the query. What eBay is asked is what the thing IS; where the buyer
    /// lives is no part of that, and a rate in the sold search would return nothing at all.
    /// </summary>
    [Fact]
    public void The_tax_never_reaches_what_ebay_is_asked()
    {
        var query = ReadSource("Services/LiveSearchQuery.cs");

        Assert.DoesNotContain("LiveSalesTax", query, StringComparison.Ordinal);
        Assert.DoesNotContain("SalesTax", query, StringComparison.Ordinal);
        Assert.DoesNotContain("TaxExempt", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// It prices nothing about the market. The read takes a bid, a premium, a rate and a tick, and
    /// no comps at all — a tax that could see the sold history would be a tax that could re-rate it.
    /// </summary>
    [Fact]
    public void The_read_never_sees_a_comp()
    {
        Assert.DoesNotContain("Comparable", Tax, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketAnalysisResult", Tax, StringComparison.Ordinal);
        Assert.DoesNotContain("ResalePricing", Tax, StringComparison.Ordinal);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
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
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
