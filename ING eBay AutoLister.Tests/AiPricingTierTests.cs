using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The third pricing tier. The board reads live sold comps, then the stored comps database, and
// only then asks the model — which exists because of what the first two leave behind: a "1oz gold"
// scan on 2026-08-22 priced 120 products, got HTTP 503 from every live lookup, and put 106 of them
// on screen as a dash and a search link.
//
// These pin the three rules that make an unprovable number safe to show. It is priced by the same
// money code as everything else, it is never dressed as evidence, and nothing that speaks without
// being read — the radar's notifications — is allowed to quote it.
public class AiPricingTierTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static LocalSupplyListing Listing(string title, decimal? price, string id = "1") =>
        new()
        {
            Source = "ebay", SourceLabel = "eBay", ItemId = id, Title = title,
            Url = $"https://www.ebay.com/itm/{id}", Price = price,
            // Stated as free, because these are tests about the AI tier and not about shipping. An
            // eBay row that leaves shipping UNKNOWN has its money columns withheld on purpose (see
            // Shipping_unknown_...), and an uncosted row cannot show what the money code did with
            // an estimate.
            PurchaseShippingCost = 0m,
        };

    private static ResalePricing Comps(decimal expected, int comps = 6, int confidence = 70) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.9m,
            SoldCompCount = comps, PricedCompCount = comps, IdentityVerified = true,
            ConfidenceScore = confidence, ConfidenceLevel = "Good",
        };

    // ── It is a real price, costed by the real money code ────────────────────────────────────

    [Fact]
    public void An_estimate_is_priced_by_the_same_calculator_as_a_comp_backed_row()
    {
        var listing = Listing("1 oz Gold Buffalo Bullion Bar .999 Fine 24k", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(
            listing, ResalePricing.FromAi(listing.Title, 180m, 220m, "generic bullion round"), Fees);

        // The middle of the range is the price — and then the ordinary fee model, to the cent:
        // $200 sale, 13.25% + $0.40 = $26.90, leaving $123.10 on a $50 buy.
        Assert.Equal(200m, row.EbayExpectedSale);
        Assert.Equal(26.90m, row.EstimatedFees);
        Assert.Equal(123.10m, row.NetProfit);
    }

    [Fact]
    public void The_low_end_of_the_range_is_the_quick_sale_figure()
    {
        // Same shape a comp-priced product has, so every downstream reader — the offer ladder, the
        // max-to-pay — works on it without learning a second kind of price.
        var pricing = ResalePricing.FromAi("a thing", 180m, 220m, null);

        Assert.Equal(200m, pricing.ExpectedSale);
        Assert.Equal(200m, pricing.Median);
        Assert.Equal(180m, pricing.QuickSale);
        Assert.True(pricing.HasPrice);
    }

    // ── It is never dressed as evidence ──────────────────────────────────────────────────────

    [Fact]
    public void An_estimate_is_graded_as_itself_and_never_as_thin_comps()
    {
        var listing = Listing("1 oz Gold Buffalo Bullion Bar .999 Fine 24k", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(
            listing, ResalePricing.FromAi(listing.Title, 180m, 220m, "generic bullion round"), Fees);

        // Its own tier, not "low" and certainly not "confident": "the comps were thin" and "there
        // were no comps at all" send a seller to do completely different things next.
        Assert.Equal(LocalArbitrageEvidence.Ai, row.EvidenceTier);
        Assert.Equal(LocalArbitrageEvidence.Ai, row.Valuation!.Confidence);
        Assert.Equal(ValuationStatuses.AiEstimate, row.Valuation.Status);
        Assert.Equal("AI estimate", row.Valuation.SourceLabel);

        // And it says so in words, without claiming a comp count it does not have.
        Assert.Contains("No sold listing matched", row.EvidenceNote);
        Assert.Contains("generic bullion round", row.EvidenceNote);
        Assert.DoesNotContain("sold comps matched this item — there is no resale price",
            row.EvidenceNote);

        // Zero comps, stated as zero. A count borrowed from somewhere would be the whole failure.
        Assert.Equal(0, row.SoldCompCount);
        Assert.Equal(0, row.PricedCompCount);
    }

    [Fact]
    public void The_hand_search_link_survives_the_estimate()
    {
        // An estimate the seller can check in one click is a different thing from one they have to
        // take on faith.
        var listing = Listing("2016 Ford Fusion SEL", 6_000m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, ResalePricing.FromAi(listing.Title, 7_000m, 9_000m, null), Fees);

        Assert.Contains("LH_Sold=1", row.Valuation!.LookupUrl);
        Assert.Equal(listing.Title, row.Valuation.LookupQuery);
    }

    [Fact]
    public void A_category_guard_cannot_refuse_the_one_price_the_row_has()
    {
        // The vehicle and bulky guards exist to ask "do these sold comps describe THIS kind of
        // thing". A model estimate has no lookup behind it — it was asked about this exact item,
        // by name, because no comp matched — so there is nothing for a guard to check, and a guard
        // that ran would throw away the only answer a truck row is ever going to get.
        var listing = Listing("2011 Toyota Tundra SR5 4x4 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        var refusedByTheGuard = Analyzer.Build(listing, Comps(180m, comps: 4), Fees);
        Assert.Equal(ValuationStatuses.Manual, refusedByTheGuard.Valuation!.Status);
        Assert.Null(refusedByTheGuard.EbayExpectedSale);

        var estimated = Analyzer.Build(
            listing, ResalePricing.FromAi(listing.Title, 11_000m, 13_000m, "private-party, average condition"), Fees);
        Assert.Equal(ValuationStatuses.AiEstimate, estimated.Valuation!.Status);
        Assert.Equal(12_000m, estimated.EbayExpectedSale);
    }

    [Fact]
    public void A_comp_backed_price_is_never_replaced_by_a_guess()
    {
        // Not a rule the pass can bend: it only ever looks at products the tiers above left with no
        // price at all. Pinned here as the property that matters — a real price outranks an
        // estimate, whatever order they arrive in.
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Comps(200m), Fees);

        Assert.Equal(ValuationStatuses.Comps, row.Valuation!.Status);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, row.EvidenceTier);
    }

    // ── Nothing that speaks unasked may quote it ─────────────────────────────────────────────

    // ── An unpriceable cost does not rewrite the evidence ────────────────────────────────────

    [Fact]
    public void Shipping_unknown_withholds_the_money_without_claiming_comps_that_do_not_exist()
    {
        // Same eBay row, shipping left unstated: Browse omits it on calculated, freight and some
        // local-pickup listings, and that is not free shipping.
        var listing = new LocalSupplyListing
        {
            Source = "ebay", SourceLabel = "eBay", ItemId = "9",
            Title = "1 oz Gold Buffalo Bullion Bar .999 Fine 24k",
            Url = "https://www.ebay.com/itm/9", Price = 50m,
        };
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(
            listing, ResalePricing.FromAi(listing.Title, 180m, 220m, "generic bullion round"), Fees);

        // The money is withheld — no delivered cost, no honest profit.
        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.NetProfit);

        // But the row is still an AI estimate, and it still says so. It must NOT announce comps:
        // this row found no sold listing at all, and "sold comps were found" printed over its own
        // sentence is the app claiming evidence it has not got.
        Assert.Equal(LocalArbitrageEvidence.Ai, row.EvidenceTier);
        Assert.Contains("No sold listing matched", row.EvidenceNote);
        Assert.Contains("generic bullion round", row.EvidenceNote);
        Assert.DoesNotContain("Sold comps were found", row.EvidenceNote);

        // And the shipping fact is added, not substituted.
        Assert.Contains("Inbound shipping is unknown", row.EvidenceNote);
        Assert.Equal(0, row.SoldCompCount);
    }

    [Fact]
    public void The_radar_never_fires_on_an_estimate()
    {
        var listing = Listing("1 oz Gold Buffalo Bullion Bar .999 Fine 24k", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var estimated = Analyzer.Build(
            listing, ResalePricing.FromAi(listing.Title, 900m, 1_100m, "bullion, spot-linked"), Fees);
        var proven = Analyzer.Build(listing, Comps(1_000m), Fees);

        var watch = new DealWatch { Query = "gold", MinNetProfit = 100m, RequireConfidentEvidence = false };

        // Same item, same money, same watch. The board may show both — badged, dimmed, filterable.
        // A push notification carries none of that, so only the proven one is allowed to wake
        // somebody up.
        Assert.True(estimated.NetProfit > 100m);
        Assert.False(DealRadarMatcher.Qualifies(watch, estimated));
        Assert.True(DealRadarMatcher.Qualifies(watch, proven));
    }
}
