using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A clearance item bought from Amazon and a drill bought off a stranger for cash go through the
// same analyzer, but they do not cost the same. These pin the two differences that move money:
// the register adds sales tax, and there is nobody to haggle with.
//
// The rule underneath all of them is that a retail row must never be flattering. Leaving tax out
// overstates every profit figure on the board, and it overstates them most on the rows nearest
// break-even — exactly the rows a verdict flips on and a seller acts on.
public class RetailArbitrageTests
{
    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        DefaultShippingCost = 0m,
    };

    private static LocalArbitrageAnalyzer Analyzer() => new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static LocalSupplyListing RetailDeal(decimal price) => new()
    {
        Source = DealFeedCatalog.SourceId,
        SourceLabel = "Slickdeals",
        ItemId = "slickdeals:1",
        Title = "Sony WH-1000XM5 Headphones",
        Price = price,
        IsRetail = true,
        Retailer = "Amazon",
        FreeShipping = true,
    };

    private static LocalSupplyListing LocalDeal(decimal price) => new()
    {
        Source = "craigslist",
        SourceLabel = "Craigslist",
        ItemId = "7712345678",
        Title = "Sony WH-1000XM5 Headphones",
        Price = price,
    };

    private static ResalePricing Resale(decimal expected) => new()
    {
        LookupTitle = "Sony WH-1000XM5 Headphones",
        Median = expected,
        ExpectedSale = expected,
        QuickSale = expected * 0.9m,
        SoldCompCount = 12,
        ConfidenceScore = 80,
    };

    // ── Sales tax ─────────────────────────────────────────────────────────────

    [Fact]
    public void ARetailBuyIsCostedOnWhatActuallyLeavesTheWallet()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 7.5m);

        Assert.True(row.IsRetail);
        Assert.Equal(200m, row.LocalAsk);      // the shelf price, unchanged
        Assert.Equal(15m, row.SalesTax);
        Assert.Equal(215m, row.BuyCostAllIn);
    }

    // The whole reason the field exists: without it the board promises money that isn't there.
    [Fact]
    public void IgnoringSalesTaxWouldOverstateTheProfitByExactlyTheTax()
    {
        var taxed = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 7.5m);
        var untaxed = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 0m);

        Assert.Equal(15m, untaxed.NetProfit - taxed.NetProfit);
    }

    // ROI is measured against the money spent, not against the sticker. A dollar paid to the state
    // is a dollar that could have bought the next flip.
    [Fact]
    public void RoiIsMeasuredAgainstTheAllInCost()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 7.5m);

        var expected = Math.Round(row.NetProfit!.Value / 215m * 100m, 1);
        Assert.Equal(expected, row.RoiPercent);
    }

    // A private-party buy is cash. Every existing board had to keep its numbers to the cent.
    [Fact]
    public void APrivatePartyBuyIsNeverTaxedWhateverTheRate()
    {
        var local = Analyzer().Build(LocalDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 9m);
        var untaxed = Analyzer().Build(LocalDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 0m);

        Assert.Null(local.SalesTax);
        Assert.Null(local.BuyCostAllIn);
        Assert.Equal(untaxed.NetProfit, local.NetProfit);
        Assert.Equal(untaxed.MaxBuyPrice, local.MaxBuyPrice);
    }

    // "Max to pay" is a shelf price the seller compares against a shelf price. Quoting the untaxed
    // break-even would name a number at which they actually lose money.
    [Fact]
    public void MaxToPayIsTheHighestSHELFPriceThatStillBreaksEven()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, retailSalesTaxPercent: 7.5m);

        // Paying exactly that shelf price must leave nothing — and nothing negative either.
        var atCeiling = Analyzer().Build(RetailDeal(row.MaxBuyPrice!.Value), Resale(320m), Fees, retailSalesTaxPercent: 7.5m);
        Assert.InRange(atCeiling.NetProfit!.Value, -0.02m, 0.02m);

        // And it is strictly tighter than the untaxed identity (ask + profit) it replaces.
        Assert.True(row.MaxBuyPrice < row.LocalAsk + row.NetProfit);
    }

    [Fact]
    public void MaxToPayKeepsTheOneDollarPerDollarIdentityWhenThereIsNoTax()
    {
        var row = Analyzer().Build(LocalDeal(200m), Resale(320m), Fees);

        Assert.Equal(Math.Round(200m + row.NetProfit!.Value, 2), row.MaxBuyPrice);
    }

    // A typo of 75 for 7.5 would otherwise report every real deal on the board as a loss.
    [Theory]
    [InlineData(75, 15)]
    [InlineData(-4, 0)]
    [InlineData(8.25, 8.25)]
    public void AnImpossibleTaxRateIsClampedRatherThanBelieved(double given, double expected)
    {
        Assert.Equal((decimal)expected, RetailBuyCosts.Sanitize((decimal)given));
    }

    [Fact]
    public void AMissingRateFallsBackToTheStatedDefaultRatherThanToZero()
    {
        Assert.Equal(RetailBuyCosts.DefaultSalesTaxPercent, RetailBuyCosts.Sanitize(null));
        Assert.True(RetailBuyCosts.DefaultSalesTaxPercent > 0m,
            "Defaulting to zero tax would overstate every retail row for every seller who never set a rate.");
    }

    // ── No haggling with a retailer ───────────────────────────────────────────

    [Fact]
    public void ARetailRowCarriesNoNegotiationPlan()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees);

        Assert.Null(row.Negotiation);
    }

    [Fact]
    public void ALocalRowStillGetsItsNegotiationPlan()
    {
        var row = Analyzer().Build(LocalDeal(200m), Resale(320m), Fees);

        Assert.NotNull(row.Negotiation);
    }

    // The buy-side headline on the board sums drafted offers. Counting a retail row in it would put
    // money on screen that cannot be won, because there is nobody to send the offer to.
    [Fact]
    public void RetailRowsContributeNothingToTheNegotiationUpside()
    {
        var rows = new[]
        {
            Analyzer().Build(RetailDeal(200m), Resale(320m), Fees),
            Analyzer().Build(LocalDeal(200m), Resale(320m), Fees),
        };

        var negotiable = rows
            .Where(r => r.Negotiation is { Verdict: "buy_now" or "negotiate" or "must_negotiate" } n && n.Upside > 0)
            .ToList();

        Assert.DoesNotContain(negotiable, r => r.IsRetail);
    }

    // ── It is still one pipeline ──────────────────────────────────────────────

    [Fact]
    public void RetailRowsCarryTheirStoreAndCouponThroughToTheBoard()
    {
        var deal = RetailDeal(200m);
        deal.CouponCode = "BESTOFPC";
        deal.OriginalPrice = 349m;

        var row = Analyzer().Build(deal, Resale(320m), Fees);

        Assert.Equal("Amazon", row.Retailer);
        Assert.Equal("BESTOFPC", row.CouponCode);
        Assert.True(row.FreeShipping);
        Assert.Equal(349m, row.OriginalPrice);
        Assert.Equal("Slickdeals", row.SourceLabel);
    }

    // The point of the pluggable design: retail and local rank against each other on one axis.
    [Fact]
    public void RetailAndLocalRowsRankAgainstEachOtherOnNetProfit()
    {
        var rows = new[]
        {
            Analyzer().Build(LocalDeal(250m), Resale(320m), Fees),
            Analyzer().Build(RetailDeal(150m), Resale(320m), Fees),
        };

        var ranked = LocalArbitrageAnalyzer.Rank(rows);

        Assert.Equal(DealFeedCatalog.SourceId, ranked[0].Source);
        Assert.True(ranked[0].NetProfit > ranked[1].NetProfit);
    }

    // A retail deal with no sold history is still reported, not hidden — same rule as every source.
    [Fact]
    public void AnUnpricedRetailDealStillReportsItselfRatherThanVanishing()
    {
        var row = Analyzer().Build(RetailDeal(200m), resale: null, Fees);

        Assert.Equal("no_data", row.Verdict);
        Assert.Equal(200m, row.LocalAsk);
        Assert.True(row.IsRetail);
    }

    // ── The source, as the picker sees it ─────────────────────────────────────

    [Fact]
    public void TheDealFeedSourceNeedsNoLoginAndIsNotLocationBased()
    {
        var source = new DealFeedService(new StubHttpClientFactory(), new ActionLog());

        Assert.Equal(DealFeedCatalog.SourceId, source.Id);
        Assert.False(source.RequiresConnection);
        Assert.True(source.IsAvailable);
        Assert.False(source.IsLocationBased);
    }

    // The default has to stay true for every source that predates the online one, or the UI starts
    // hiding the zip field on searches that need it.
    [Fact]
    public void TheLocalSourcesStayLocationBasedByDefault()
    {
        // Through the interface, because IsLocationBased is a default member — which is the point:
        // no existing source had to be edited to gain it.
        ILocalSupplySource craigslist = new CraigslistService(new StubHttpClientFactory(), new ActionLog());

        Assert.True(craigslist.IsLocationBased);
    }

    [Fact]
    public async Task AnEmptyQueryIsAnsweredWithoutTouchingTheNetwork()
    {
        var source = new DealFeedService(new StubHttpClientFactory(), new ActionLog());

        var result = await source.SearchAsync("", zip: "89101", radiusMiles: 40);

        Assert.Equal("error", result.Status);
        Assert.NotNull(result.Error);
    }

    // A factory whose client would fail if it were ever used — these tests must never reach out.
    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new FailingHandler());

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new InvalidOperationException("These tests must not make network calls.");
        }
    }
}
