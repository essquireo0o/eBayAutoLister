using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A promo code is the cheapest dollar in this app: it comes off the buy, so eBay takes none of it,
// nothing has to ship for it, and it lands today rather than after a sale.
//
// It is also a CLAIM rather than a price — a public code may be dead, regional, category-limited or
// new-customers-only, and nothing short of checking out can test it. So the rule these pin is that
// the coupon numbers live BESIDE the row's own and never inside them: the ranking, the verdict
// badge, the goldmine count and the board's profit total all still run off the shelf price, and one
// dead code can never promote a deal that doesn't exist.
public class CouponArbitrageTests
{
    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        DefaultShippingCost = 0m,
    };

    private static LocalArbitrageAnalyzer Analyzer() =>
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static LocalSupplyListing RetailDeal(decimal price, string retailer = "The Home Depot") => new()
    {
        Source = DealFeedCatalog.SourceId,
        SourceLabel = "Slickdeals",
        ItemId = "slickdeals:1",
        Title = "Ryobi ONE+ 18V Drill Kit",
        Price = price,
        IsRetail = true,
        Retailer = retailer,
        FreeShipping = true,
    };

    private static ResalePricing Resale(decimal expected) => new()
    {
        LookupTitle = "Ryobi ONE+ 18V Drill Kit",
        Median = expected,
        ExpectedSale = expected,
        QuickSale = expected * 0.9m,
        SoldCompCount = 12,
        ConfidenceScore = 80,
    };

    private static List<CouponOffer> Codes(decimal percent = 20m, string code = "SAVE20") =>
    [
        new()
        {
            MerchantId = "homedepot", MerchantLabel = "The Home Depot",
            Kind = CouponKinds.PercentOff, Code = code, Value = percent,
            Confidence = CouponConfidence.Medium, PublishedUtc = DateTime.UtcNow.AddDays(-2),
            Title = $"{percent}% off your order with code {code}", AppliesToOrder = true,
        },
    ];

    // ── The row keeps its own numbers ─────────────────────────────────────────

    [Fact]
    public void TheRowsOwnProfitIsTheOneItCanStandBehind()
    {
        var withCoupons = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, 7.5m, Codes());
        var without = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, 7.5m);

        // Identical in every figure the board ranks, badges and totals on.
        Assert.Equal(without.NetProfit, withCoupons.NetProfit);
        Assert.Equal(without.RoiPercent, withCoupons.RoiPercent);
        Assert.Equal(without.BuyCostAllIn, withCoupons.BuyCostAllIn);
        Assert.Equal(without.Verdict, withCoupons.Verdict);
        Assert.Equal(without.MaxBuyPrice, withCoupons.MaxBuyPrice);
    }

    [Fact]
    public void TheSavingIsTheSameFlipCostedAtTheDiscountedPrice()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, 7.5m, Codes());

        var coupons = Assert.IsType<CouponSavings>(row.Coupons);
        Assert.Equal(40m, coupons.Discount);
        Assert.Equal(160m, coupons.DiscountedSubtotal);
        Assert.Equal(12m, coupons.SalesTax);
        Assert.Equal(172m, coupons.BuyCostWithCoupons);

        // The code takes $40 off the sticker and the $3 of tax that sat on top of it — and every
        // dollar of that is profit, because nothing downstream of the buy changed.
        Assert.Equal(43m, coupons.ExtraProfit);
        Assert.Equal(row.NetProfit + 43m, coupons.NetProfitWithCoupons);
    }

    [Fact]
    public void RoiIsMeasuredAgainstWhatWasActuallySpent()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, 7.5m, Codes());

        // A smaller cost basis for the same net is a better return, and the coupon ROI has to say so
        // rather than quietly reusing the row's.
        Assert.True(row.Coupons!.RoiPercentWithCoupons > row.RoiPercent);
    }

    [Fact]
    public void ACodeThatChangesTheAnswerSaysWhatTheAnswerBecomes()
    {
        // Thin at the shelf price; a real deal with 20% off.
        var row = Analyzer().Build(RetailDeal(250m), Resale(330m), Fees, 0m, Codes());

        Assert.Equal("thin", row.Verdict);
        Assert.Equal("solid", row.Coupons!.VerdictIfItWorks);
    }

    [Fact]
    public void ACodeThatChangesNothingAboutTheVerdictSaysNothing()
    {
        var row = Analyzer().Build(RetailDeal(100m), Resale(400m), Fees, 0m, Codes());

        Assert.Equal("goldmine", row.Verdict);
        // Already the best badge on the board — repeating it would be noise beside the money.
        Assert.Null(row.Coupons!.VerdictIfItWorks);
    }

    // ── The rows that only exist because of a code ────────────────────────────

    [Fact]
    public void ADealThatOnlyWorksWithTheCodeIsFlaggedAndStillJudgedOnTheShelfPrice()
    {
        // $300 buy against a $330 sale loses money once eBay is paid; 20% off turns it around.
        var row = Analyzer().Build(RetailDeal(300m), Resale(330m), Fees, 0m, Codes());

        Assert.True(row.NetProfit <= 0);
        Assert.Equal("pass", row.Verdict);          // the board still says walk, and it is right to
        Assert.True(row.Coupons!.RescuesTheDeal);
        Assert.True(row.Coupons.NetProfitWithCoupons > 0);
    }

    // ── Where coupons don't belong ────────────────────────────────────────────

    [Fact]
    public void NobodySellingADrillOnCraigslistTakesAPromoCode()
    {
        var classified = new LocalSupplyListing
        {
            Source = "craigslist", SourceLabel = "Craigslist", ItemId = "7712345678",
            Title = "Ryobi ONE+ 18V Drill Kit", Price = 200m,
        };

        var row = Analyzer().Build(classified, Resale(320m), Fees, 7.5m, Codes());

        Assert.Null(row.Coupons);
    }

    [Fact]
    public void AFreeItemHasNothingToDiscount()
    {
        var freebie = RetailDeal(0m);
        freebie.Price = 0m;
        freebie.IsFree = true;
        freebie.Freebie = new FreebieDetails { Kind = FreebieKinds.Free, KindLabel = "Free", DeliveryCostKnown = true };

        var row = Analyzer().Build(freebie, Resale(320m), Fees, 7.5m, Codes());

        Assert.Null(row.Coupons);
    }

    [Fact]
    public void AnAuctionTakesBidsNotCodes()
    {
        var lot = RetailDeal(200m);
        lot.Liquidation = new LiquidationLotDetails { AuctionHouse = "GovDeals", BidCount = 3 };

        var row = Analyzer().Build(lot, Resale(320m), Fees, 7.5m, Codes());

        Assert.Null(row.Coupons);
    }

    [Fact]
    public void NoCodesFoundLeavesTheRowExactlyAsItWas()
    {
        var row = Analyzer().Build(RetailDeal(200m), Resale(320m), Fees, 7.5m, []);

        Assert.Null(row.Coupons);
    }

    // ── The rows nothing could price ──────────────────────────────────────────

    [Fact]
    public void ARowWithNoSoldHistoryStillGetsTheCode()
    {
        // There is no profit to recompute, but "this costs $172 instead of $215 with code SAVE20"
        // is worth more than the dash in every money column beside it.
        var row = Analyzer().Build(RetailDeal(200m), resale: null, Fees, 7.5m, Codes());

        Assert.Equal("no_data", row.Verdict);
        Assert.Equal(172m, row.Coupons!.BuyCostWithCoupons);
        Assert.Null(row.Coupons.NetProfitWithCoupons);
        Assert.Null(row.Coupons.ExtraProfit);
    }

    // ── The deal's own code ───────────────────────────────────────────────────

    [Fact]
    public void ThePriceOnTheDealAlreadyNeedsItsOwnCodeSoNothingStacksOnIt()
    {
        var deal = RetailDeal(200m);
        deal.CouponCode = "DEALCODE";

        var row = Analyzer().Build(deal, Resale(320m), Fees, 7.5m, Codes());

        Assert.Equal(0m, row.Coupons!.Discount);
        Assert.Null(row.Coupons.ExtraProfit);
        // The store-wide code is still shown — it may be worth more than the deal's own.
        Assert.Single(row.Coupons.AlsoFound);
        Assert.Contains("one code", row.Coupons.Note);
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    [Fact]
    public void ACouponCannotMoveARowUpTheTable()
    {
        // The whole point of keeping the two sets of numbers apart: a $500 code on a losing deal
        // must not outrank a deal that already makes money.
        var loser = Analyzer().Build(RetailDeal(300m), Resale(330m), Fees, 0m, Codes(percent: 40m, code: "HALF"));
        var winner = Analyzer().Build(RetailDeal(150m), Resale(330m), Fees, 0m);

        var ranked = LocalArbitrageAnalyzer.Rank([loser, winner]);

        Assert.Equal(winner.Title, ranked[0].Title);
        Assert.True(ranked[0].NetProfit > ranked[1].NetProfit);
    }
}
