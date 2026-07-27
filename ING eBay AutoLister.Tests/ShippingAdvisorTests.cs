using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class ShippingAdvisorTests
{
    private static ShippingAdvisor Advisor() =>
        new(new PackageEstimator(), new NetProceedsCalculator(new ProfitCalculator()));

    private static FeeProfile Fees() => new();   // 13.25% + $0.40, everything else zero

    private static ShippingQuoteRequest Request(string title, decimal price = 100m, decimal? cost = 40m) => new()
    {
        Title = title,
        Price = price,
        UnitCost = cost,
        OriginZip = "43004",
    };

    [Fact]
    public void Advise_PicksTheCheapestEligibleServiceAndMarksIt()
    {
        var result = Advisor().Advise(Request("Dell laptop i7"), Fees());

        Assert.Equal("ok", result.Status);
        Assert.NotNull(result.Best);
        Assert.True(result.Best!.Recommended);
        Assert.True(result.Best.Eligible);

        var eligible = result.Services.Where(s => s.Eligible).ToList();
        Assert.Equal(result.Best.ExpectedCost, eligible.Min(s => s.ExpectedCost));
        Assert.Equal(0m, result.Best.ExtraVsBest);
    }

    [Fact]
    public void Advise_ListsIneligibleServicesRatherThanHidingThem()
    {
        // A 100 lb box: no USPS service can take it, and the seller needs to see that stated.
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Heavy rack server",
            WeightLbs = 100m,
            PackageLengthIn = 24m, PackageWidthIn = 20m, PackageHeightIn = 16m,
            Price = 400m, OriginZip = "43004",
        }, Fees());

        Assert.Contains(result.Services, s => !s.Eligible && s.IneligibleReason.Length > 0);
        Assert.Equal("UPS", result.Best!.Carrier);
    }

    [Fact]
    public void Advise_ReportsNoServiceRatherThanAFreeLabelWhenNothingWillCarryIt()
    {
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Shipping container",
            WeightLbs = 900m,
            PackageLengthIn = 240m, PackageWidthIn = 96m, PackageHeightIn = 96m,
            Price = 5000m, OriginZip = "43004",
        }, Fees());

        Assert.Equal("no_service", result.Status);
        Assert.Null(result.Best);
        Assert.Empty(result.Modes);
        Assert.Contains("No standard service", result.Headline);
    }

    // The correction this whole feature exists to make. eBay charges its final value fee on the
    // shipping the buyer pays, so at a fixed buyer outlay every way of splitting the total nets the
    // same. If this test ever fails, the app has started telling sellers a folk myth.
    [Fact]
    public void Modes_ChargingShippingSeparatelyDoesNotAvoidTheFinalValueFee()
    {
        var result = Advisor().Advise(Request("Canon EF 70-200mm lens", price: 200m), Fees());

        var free = result.Modes.Single(m => m.Mode == "free_expected");
        var flat = result.Modes.Single(m => m.Mode == "flat");

        Assert.Equal(free.BuyerOutlayNear, flat.BuyerOutlayNear);
        Assert.Equal(free.NetExpected, flat.NetExpected, 2);
    }

    [Fact]
    public void Modes_FreeShippingAtAverageCostIsUnderwaterForTheFarHalfOfTheCountry()
    {
        // A heavy item has a wide zone spread, so pricing at the average has to lose on the tail.
        var result = Advisor().Advise(Request("Stereo receiver amplifier", price: 250m), Fees());

        var free = result.Modes.Single(m => m.Mode == "free_expected");
        Assert.True(free.UnderwaterBuyerPercent > 0m,
            "Free shipping priced at the average must be reported as losing on some share of buyers.");
        Assert.True(free.NetNear > free.NetFar, "A near buyer must be worth more than a far one under free shipping.");
        Assert.True(free.ZoneRisk > 0m);
    }

    [Fact]
    public void Modes_WorstCasePricingIsNeverUnderwater()
    {
        var result = Advisor().Advise(Request("Stereo receiver amplifier", price: 250m), Fees());

        var worstCase = result.Modes.Single(m => m.Mode == "free_worst_case");
        Assert.Equal(0m, worstCase.UnderwaterBuyerPercent);
        Assert.True(worstCase.ItemPrice > result.Modes.Single(m => m.Mode == "free_expected").ItemPrice,
            "Insuring against the far zone has to cost price competitiveness.");
    }

    [Fact]
    public void Modes_CalculatedShippingMovesTheZoneSpreadOntoTheBuyer()
    {
        var result = Advisor().Advise(Request("Stereo receiver amplifier", price: 250m), Fees());

        var calculated = result.Modes.Single(m => m.Mode == "calculated");
        var free = result.Modes.Single(m => m.Mode == "free_expected");

        Assert.Equal(0m, calculated.UnderwaterBuyerPercent);
        Assert.True(calculated.ZoneRisk < free.ZoneRisk,
            "Billing the buyer their own zone has to cut the seller's exposure.");
        // The buyer, not the seller, now sees the spread.
        Assert.True(calculated.BuyerOutlayFar > calculated.BuyerOutlayNear);
    }

    // Even calculated shipping does not fully insulate the seller: eBay's final value fee is
    // charged on the shipping the buyer pays, so a dearer label still costs the seller the fee on
    // the difference. Sellers routinely believe calculated shipping is free of this. It is not.
    [Fact]
    public void Modes_CalculatedShippingStillLeavesTheFeeOnTheShippingDifference()
    {
        var result = Advisor().Advise(Request("Stereo receiver amplifier", price: 250m), Fees());

        var calculated = result.Modes.Single(m => m.Mode == "calculated");
        var spread = result.Best!.ZoneSpread;

        Assert.True(calculated.ZoneRisk > 0m,
            "Calculated shipping cannot remove the seller's exposure entirely.");

        // What is left should be the fee rate applied to the label spread, and nothing more.
        var expectedResidual = Math.Round(spread * Fees().RevenueFeeFraction, 2);
        Assert.Equal(expectedResidual, calculated.ZoneRisk, 1);
    }

    [Fact]
    public void Modes_ExactlyOneIsRecommendedAndEveryOneIsExplained()
    {
        var result = Advisor().Advise(Request("Dell laptop i7"), Fees());

        Assert.Equal(1, result.Modes.Count(m => m.Recommended));
        Assert.All(result.Modes, m => Assert.False(string.IsNullOrWhiteSpace(m.Verdict)));
    }

    [Fact]
    public void Modes_RecommendsFreeShippingOnlyWhenTheZoneBetIsSmall()
    {
        // A light item barely moves across zones, so free shipping is safe.
        var light = Advisor().Advise(Request("Pokemon trading card PSA 9", price: 60m), Fees());
        Assert.Equal("free_expected", light.Modes.Single(m => m.Recommended).Mode);

        // A heavy one swings enough that the seller should stop carrying the risk.
        var heavy = Advisor().Advise(Request("Antminer S19 Pro bitcoin miner", price: 900m), Fees());
        Assert.NotEqual("free_expected", heavy.Modes.Single(m => m.Recommended).Mode);
    }

    [Fact]
    public void Modes_AreAbsentWithoutAPriceToSplit()
    {
        var result = Advisor().Advise(Request("Dell laptop i7", price: 0m), Fees());
        Assert.Empty(result.Modes);
        Assert.NotNull(result.Best);   // the label still costs what it costs
    }

    [Fact]
    public void Tips_PointAtTheFlatRateBoxWhenItIsOnlyTheSizeBlockingIt()
    {
        // 15 lb, just too big for the medium flat rate box (11 x 8.5 x 5.5).
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Box of assorted tool parts",
            WeightLbs = 15m,
            PackageLengthIn = 12m, PackageWidthIn = 9m, PackageHeightIn = 6m,
            Price = 120m, OriginZip = "43004",
        }, Fees());

        var tip = result.Tips.FirstOrDefault(t => t.Kind == "flat_rate_within_reach");
        Assert.NotNull(tip);
        Assert.True(tip!.SavingPerSale > 0m);
        Assert.Contains("Flat Rate", tip.Headline);
    }

    // The rate book rejects a flat-rate box on a strict dimension check, so a large carton is
    // technically only "too big" for the padded envelope. Suggesting the seller repack ten inches
    // of depth into three quarters of an inch would be cheaper on paper and impossible in life.
    [Fact]
    public void Tips_DoNotSuggestAFlatRateBoxTheItemCouldNeverPhysicallyFit()
    {
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Large carton of assorted goods",
            WeightLbs = 20m,
            PackageLengthIn = 14m, PackageWidthIn = 12m, PackageHeightIn = 10m,
            Price = 150m, OriginZip = "43004",
        }, Fees());

        Assert.DoesNotContain(result.Tips, t => t.Kind == "flat_rate_within_reach");
    }

    [Fact]
    public void Tips_CallOutDimensionalWeightOnABigLightBox()
    {
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Lampshade",
            WeightLbs = 3m,
            PackageLengthIn = 24m, PackageWidthIn = 20m, PackageHeightIn = 16m,
            Price = 80m, OriginZip = "43004",
        }, Fees());

        var tip = result.Tips.FirstOrDefault(t => t.Kind == "dim_weight");
        Assert.NotNull(tip);
        Assert.Contains("air", tip!.Headline);
    }

    [Fact]
    public void Tips_AreOrderedByMoneyDescending()
    {
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Box of assorted tool parts",
            WeightLbs = 15m,
            PackageLengthIn = 12m, PackageWidthIn = 9m, PackageHeightIn = 6m,
            Price = 120m, OriginZip = "43004",
        }, Fees());

        var savings = result.Tips.Select(t => t.SavingPerSale).ToList();
        Assert.Equal(savings.OrderByDescending(s => s), savings);
    }

    [Fact]
    public void Advise_FlagsAnItemWhoseShippingHasEatenItsMargin()
    {
        // A heavy, cheap item: the classic reseller trap.
        var result = Advisor().Advise(Request("Stereo receiver amplifier", price: 45m), Fees());

        Assert.True(result.ShippingLoadPercent >= 30m);
        Assert.Contains("%", result.Headline);
        Assert.Contains("Bundle", result.Note, StringComparison.OrdinalIgnoreCase);
    }

    // The copy has to quote the BILLABLE weight. On a dimensional-weight item the actual weight is
    // an order of magnitude smaller, and quoting it is what makes a $60 label on a 3 lb lampshade
    // look like a bug in the app rather than a fact about the box.
    [Fact]
    public void Advise_QuotesTheBillableWeightNotTheActualOneWhenDimWeightApplies()
    {
        var result = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Lampshade",
            WeightLbs = 3m,
            PackageLengthIn = 24m, PackageWidthIn = 20m, PackageHeightIn = 16m,
            Price = 45m, OriginZip = "43004",
        }, Fees());

        Assert.True(result.Best!.DimWeightApplied);
        var billableLb = result.Best.BillableWeightOz / 16m;

        Assert.Contains($"{billableLb:0.##} lb billable", result.Note);
        Assert.DoesNotContain("3 lb billable", result.Note);
    }

    [Fact]
    public void Advise_SaysSoWhenThePackageWasGuessedRatherThanWeighed()
    {
        var guessed = Advisor().Advise(Request("Dell laptop i7"), Fees());
        Assert.Equal("estimated", guessed.Package.Source);
        Assert.Contains("weigh it", guessed.Note, StringComparison.OrdinalIgnoreCase);

        var measured = Advisor().Advise(new ShippingQuoteRequest
        {
            Title = "Dell laptop i7",
            WeightLbs = 6m,
            PackageLengthIn = 18m, PackageWidthIn = 14m, PackageHeightIn = 5m,
            Price = 300m, OriginZip = "43004",
        }, Fees());
        Assert.Equal("measured", measured.Package.Source);
        Assert.DoesNotContain("weigh it", measured.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Advise_OriginChangesTheExpectedCostForTheSameBox()
    {
        var package = new ShippingQuoteRequest
        {
            Title = "Vintage camera",
            WeightLbs = 4m,
            PackageLengthIn = 11m, PackageWidthIn = 9m, PackageHeightIn = 7m,
            Price = 200m,
        };

        var advisor = Advisor();
        var fromOhio = advisor.Advise(new ShippingQuoteRequest
        {
            Title = package.Title, WeightLbs = package.WeightLbs,
            PackageLengthIn = package.PackageLengthIn, PackageWidthIn = package.PackageWidthIn,
            PackageHeightIn = package.PackageHeightIn, Price = package.Price, OriginZip = "43004",
        }, Fees());

        var fromCalifornia = advisor.Advise(new ShippingQuoteRequest
        {
            Title = package.Title, WeightLbs = package.WeightLbs,
            PackageLengthIn = package.PackageLengthIn, PackageWidthIn = package.PackageWidthIn,
            PackageHeightIn = package.PackageHeightIn, Price = package.Price, OriginZip = "90210",
        }, Fees());

        Assert.True(fromCalifornia.Best!.ExpectedCost > fromOhio.Best!.ExpectedCost,
            "Shipping the same box from a coast has to cost more on average than from the middle.");
    }

    [Fact]
    public void Advise_ReturnsTheZoneMixItPricedAgainst()
    {
        var result = Advisor().Advise(Request("Dell laptop i7"), Fees());

        Assert.NotEmpty(result.ZoneMix);
        Assert.Equal(100m, result.ZoneMix.Sum(z => z.SharePercent), 1);
    }
}
