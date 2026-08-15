using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The single-row reprice behind POST /api/opportunities/reprice-row. The board prices every row
/// against whatever sold comps were in the database when the scan ran; when a seller fires a live
/// lookup for one item, the fresh sold rows land in that same database and the endpoint re-costs
/// just that row from them. These pin the two halves that make that honest: the LocalSupplyListing
/// is rebuilt faithfully from what the board carries, and the row that comes back is costed by the
/// exact same LocalArbitrageAnalyzer.Build the scan uses — never a second pricing path.
/// </summary>
public class DealRepriceTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static RepriceRowRequest Request(decimal? price = 50m, bool isFree = false) => new()
    {
        Source = "facebook", SourceLabel = "Facebook Marketplace", ItemId = "42",
        Title = "Bitmain Antminer S19j Pro", Url = "https://www.facebook.com/marketplace/item/42/",
        ImageUrl = "https://example.test/s19.jpg", Price = price, IsFree = isFree,
        Location = "Las Vegas, NV", DistanceMiles = 12, PostedAgo = "3 hours ago",
        CategoryId = ResaleCategoryCatalog.AnythingId,
    };

    // The same comps the scan's pricing pass would hand analyzer.Build — a real number off eight
    // matching sold comps, which is what a live lookup deepens an estimate into.
    private static ResalePricing Pricing(decimal expected = 200m) => new()
    {
        LookupTitle = "Bitmain Antminer S19j Pro 104TH",
        Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
        SoldCompCount = 8, PricedCompCount = 8, ConfidenceScore = 70, ConfidenceLevel = "Good",
    };

    // ── Rebuilding the listing ───────────────────────────────────────────────

    [Fact]
    public void ToListing_RebuildsTheBuySideFaithfully()
    {
        var listing = DealReprice.ToListing(Request());

        Assert.Equal("facebook", listing.Source);
        Assert.Equal("42", listing.ItemId);
        Assert.Equal("Bitmain Antminer S19j Pro", listing.Title);
        Assert.Equal("https://www.facebook.com/marketplace/item/42/", listing.Url);
        Assert.Equal(50m, listing.Price);
        Assert.False(listing.IsFree);
        Assert.Equal(12, listing.DistanceMiles);

        // Classified in place the way the scan does, so valuation and fees match — never left blank.
        Assert.False(string.IsNullOrEmpty(listing.CategoryId));
        Assert.False(string.IsNullOrEmpty(listing.CategoryLabel));
    }

    [Fact]
    public void ToListing_AFreeRowKeepsARealZeroCostBasisNotAMissingPrice()
    {
        // IsFree is the difference between "cost nothing" and "we couldn't read a price": a free row
        // must carry a null Price with IsFree set, or the reprice would treat it as unpriced.
        var listing = DealReprice.ToListing(Request(price: 0m, isFree: true));

        Assert.Null(listing.Price);
        Assert.True(listing.IsFree);
    }

    [Fact]
    public void ToListing_KeepsTheCategoryTheScanAlreadyStamped()
    {
        // A source that already knew what the row is wins over anything a title parser re-derives —
        // the same rule ResaleCategoryCatalog.Classify follows inside the scan.
        var req = Request();
        req.CategoryId = ResaleCategoryCatalog.AnythingId;

        Assert.Equal(ResaleCategoryCatalog.AnythingId, DealReprice.ToListing(req).CategoryId);
    }

    [Fact]
    public void LookupQuery_PrefersTheBrowsersQueryThenPricedAsThenTitle()
    {
        Assert.Equal("explicit query",
            DealReprice.LookupQueryFor(new RepriceRowRequest { Title = "t", PricedAs = "p", Query = "explicit query" }));
        Assert.Equal("fuller priced-as title",
            DealReprice.LookupQueryFor(new RepriceRowRequest { Title = "t", PricedAs = "fuller priced-as title" }));
        Assert.Equal("just the title",
            DealReprice.LookupQueryFor(new RepriceRowRequest { Title = "just the title" }));
    }

    // ── The repriced row ─────────────────────────────────────────────────────

    [Fact]
    public void Reprice_ReturnsAScanRowShapedRowCostedTheSameWayTheScanCostsIt()
    {
        // This is exactly what the endpoint does once the live comps have landed: rebuild the
        // listing, then hand it to the same analyzer.Build the scan's ranking pass calls, with
        // retailSalesTaxPercent 0 (eBay's tax is already inside the delivered price) and no coupons.
        var listing = DealReprice.ToListing(Request(price: 50m));
        var row = Analyzer.Build(listing, Pricing(expected: 200m), Fees, 0m, coupons: null);

        // A LocalArbitrageOpportunity — the identical type an /api/ebay/scan Items[] entry is.
        Assert.IsType<LocalArbitrageOpportunity>(row);
        Assert.Equal("facebook", row.Source);
        Assert.Equal("Bitmain Antminer S19j Pro", row.Title);
        Assert.Equal("https://www.facebook.com/marketplace/item/42/", row.Url);

        // The money is the resale half the round-trip recomputes: $200 sale, 13.25% + $0.40 = $26.90
        // in fees, leaving $123.10 over a $50 buy — the same arithmetic the board's own scan produces.
        Assert.Equal(200m, row.EbayExpectedSale);
        Assert.Equal(26.90m, row.EstimatedFees);
        Assert.Equal(123.10m, row.NetProfit);
        Assert.Equal(8, row.PricedCompCount);
        Assert.NotEqual("no_data", row.Verdict);
    }

    [Fact]
    public void Reprice_WithNoComps_IsNoDataNotAFabricatedProfit()
    {
        // A live lookup that found nothing leaves the comps database unchanged, so the reprice reads
        // an empty pricing — and the row says "no data" rather than inventing a number.
        var listing = DealReprice.ToListing(Request(price: 50m));
        var row = Analyzer.Build(listing, resale: null, Fees, 0m, coupons: null);

        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.NetProfit);
        Assert.Null(row.EbayExpectedSale);
    }
}
