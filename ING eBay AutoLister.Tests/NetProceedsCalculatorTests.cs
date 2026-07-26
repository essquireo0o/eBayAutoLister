using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class NetProceedsCalculatorTests
{
    private static NetProceedsCalculator Calc() => new(new ProfitCalculator());

    // A seller who has actually configured their costs — the case the whole feature exists for.
    private static FeeProfile RealisticFees() => new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        PromotedListingRatePercent = 2m,
        DefaultShippingCost = 9m,
        DefaultPackagingCost = 1.25m,
        DefaultLaborCost = 3m,
        ReturnReservePercent = 3m,
    };

    [Fact]
    public void Quote_ItemisedDeductions_SumToTheDifferenceBetweenGrossAndNetProceeds()
    {
        var quote = Calc().Quote(askPrice: 120m, unitCost: 40m, RealisticFees());

        var lineTotal = quote.Lines.Sum(l => l.Amount);
        Assert.Equal(quote.TotalDeductions, Math.Round(lineTotal, 2));
        Assert.Equal(Math.Round(quote.GrossRevenue - quote.TotalDeductions, 2), quote.NetProceeds);
        Assert.Equal(Math.Round(quote.NetProceeds - quote.UnitCost, 2), quote.NetProfit);
    }

    // The headline claim: the number shown is the number that arrives, not the price minus eBay's
    // cut. With shipping, packaging, handling and a return reserve configured, that gap is large.
    [Fact]
    public void Quote_HiddenCosts_ReduceNetFarBelowPriceMinusEbayFee()
    {
        var fees = RealisticFees();
        var quote = Calc().Quote(askPrice: 120m, unitCost: 40m, fees);

        var naive = 120m - 40m - Math.Round(120m * 0.1325m + 0.40m, 2); // what the old editor implied
        Assert.True(quote.NetProfit < naive - 12m,
            $"expected the configured costs to cost real money; naive {naive}, actual {quote.NetProfit}");
        Assert.True(quote.NetProfit > 0m);
    }

    [Fact]
    public void Quote_AtBreakEvenPrice_NetProfitIsZero()
    {
        var fees = RealisticFees();
        var calc = Calc();

        var initial = calc.Quote(150m, 60m, fees);
        var atBreakEven = calc.Quote(initial.BreakEvenPrice, 60m, fees);

        Assert.True(Math.Abs(atBreakEven.NetProfit) < 0.05m,
            $"expected ~0 net profit at the break-even price, got {atBreakEven.NetProfit}");
    }

    // The identity every floor in the app is derived from — if it stops holding, the offer floors
    // on the watchers screen and in the editor both quietly become wrong.
    [Fact]
    public void NetProfitAt_MatchesAFullQuoteAtTheSamePrice()
    {
        var fees = RealisticFees();
        var calc = Calc();
        var reference = calc.Quote(150m, 60m, fees);

        var shorthand = NetProceedsCalculator.NetProfitAt(175m, reference.BreakEvenPrice, fees);
        var full = calc.Quote(175m, 60m, fees);

        Assert.NotNull(shorthand);
        Assert.True(Math.Abs(shorthand!.Value - full.NetProfit) < 0.05m,
            $"shorthand {shorthand} vs full quote {full.NetProfit}");
    }

    [Fact]
    public void Quote_BelowBreakEven_IsFlaggedAsALoss()
    {
        var quote = Calc().Quote(askPrice: 45m, unitCost: 40m, RealisticFees());

        Assert.True(quote.BelowBreakEven);
        Assert.True(quote.NetProfit < 0m);
        Assert.Equal("loss", quote.Verdict);
    }

    [Fact]
    public void Quote_WithNoMinimums_FloorIsExactlyBreakEven()
    {
        var quote = Calc().Quote(200m, 60m, RealisticFees());

        Assert.Equal(quote.BreakEvenPrice, quote.MinimumOfferPrice);
        Assert.Equal("break_even", quote.MinimumOfferBasis);
    }

    // Buying back $15 of profit costs more than $15 of price, because the fees scale with the sale.
    [Fact]
    public void Quote_MinimumNetProfit_RaisesTheFloorByMoreThanTheTargetItself()
    {
        var fees = RealisticFees();
        fees.MinimumNetProfit = 15m;

        var quote = Calc().Quote(200m, 60m, fees);

        Assert.Equal("profit_target", quote.MinimumOfferBasis);
        Assert.True(quote.MinimumOfferPrice > quote.BreakEvenPrice + 15m,
            $"floor {quote.MinimumOfferPrice} should exceed break-even {quote.BreakEvenPrice} + $15");
        Assert.True(Math.Abs(quote.NetProfitAtMinimumOffer - 15m) < 0.05m,
            $"the floor should net exactly the target, got {quote.NetProfitAtMinimumOffer}");
    }

    [Fact]
    public void Quote_MinimumMargin_SetsTheFloorWhenItBitesHarderThanTheDollarTarget()
    {
        var fees = RealisticFees();
        fees.MinimumNetProfit = 1m;
        fees.MinimumMarginPercent = 20m;

        var quote = Calc().Quote(500m, 200m, fees);

        Assert.Equal("margin_target", quote.MinimumOfferBasis);
        var atFloor = Calc().Quote(quote.MinimumOfferPrice, 200m, fees);
        Assert.NotNull(atFloor.MarginPercent);
        Assert.True(Math.Abs(atFloor.MarginPercent!.Value - 20m) < 0.5m,
            $"the margin floor should sit at ~20% margin, got {atFloor.MarginPercent}");
    }

    // An unreachable margin target must not silently become an infinite floor that blocks every
    // sale — the dollar target is left to do the work alone.
    [Fact]
    public void MarginFloorPrice_TargetAboveWhatFeesLeaveBehind_IsUnavailable()
    {
        var fees = RealisticFees();   // ~18.25% of revenue goes to percentage-based fees

        var floor = NetProceedsCalculator.MarginFloorPrice(100m, minMarginPercent: 90m, buyerPaidShipping: 0m, fees);

        Assert.Null(floor);
    }

    [Fact]
    public void Quote_PriceBetweenBreakEvenAndFloor_ReportsBelowFloorNotLoss()
    {
        var fees = RealisticFees();
        fees.MinimumNetProfit = 25m;
        var calc = Calc();

        var reference = calc.Quote(300m, 100m, fees);
        var between = calc.Quote(reference.BreakEvenPrice + 5m, 100m, fees);

        Assert.False(between.BelowBreakEven);
        Assert.True(between.BelowMinimumOffer);
        Assert.Equal("below_floor", between.Verdict);
        Assert.True(between.NetProfit > 0m);
    }

    // Without a cost basis the fee side is still exact; only the profit side is an assumption, and
    // the quote has to say so rather than reporting the whole ask as profit.
    [Fact]
    public void Quote_NoCostBasis_ReportsProceedsAndSaysProfitIsUnknown()
    {
        var quote = Calc().Quote(askPrice: 120m, unitCost: null, RealisticFees());

        Assert.False(quote.HasCostBasis);
        Assert.Equal("no_cost_basis", quote.Verdict);
        Assert.True(quote.NetProceeds < 120m);
        Assert.Contains("what you paid", quote.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Quote_BuyerPaidShipping_IsRevenueAndIsFeeBearing()
    {
        var fees = RealisticFees();
        var calc = Calc();

        var noShipping = calc.Quote(120m, 40m, fees, buyerPaidShipping: 0m);
        var withShipping = calc.Quote(120m, 40m, fees, buyerPaidShipping: 12m);

        Assert.Equal(132m, withShipping.GrossRevenue);
        // The buyer's $12 does not arrive as $12 — eBay charges its fee on it too.
        Assert.True(withShipping.NetProfit - noShipping.NetProfit < 12m);
        Assert.True(withShipping.NetProfit > noShipping.NetProfit);
    }

    [Fact]
    public void Quote_Quantity_ScalesTotalProfitButNotPerUnit()
    {
        var perUnit = Calc().Quote(120m, 40m, RealisticFees(), quantity: 1);
        var run = Calc().Quote(120m, 40m, RealisticFees(), quantity: 6);

        Assert.Equal(perUnit.NetProfit, run.NetProfit);
        Assert.Equal(Math.Round(perUnit.NetProfit * 6m, 2), run.TotalNetProfit);
    }

    // Payment processing used to be treated as a fixed cost inside the break-even, which understated
    // the price a seller had to reach whenever they billed it separately.
    [Fact]
    public void Quote_PaymentProcessing_ScalesWithTheSaleAndRaisesBreakEven()
    {
        var without = Calc().Quote(200m, 80m, new FeeProfile());
        var with = Calc().Quote(200m, 80m, new FeeProfile { PaymentProcessingPercent = 3m });

        Assert.True(with.PaymentProcessingFee > 0m);
        Assert.True(with.BreakEvenPrice > without.BreakEvenPrice);
        // At break-even the net must still be zero — the proof the fee was modelled, not bolted on.
        var atBreakEven = Calc().Quote(with.BreakEvenPrice, 80m, new FeeProfile { PaymentProcessingPercent = 3m });
        Assert.True(Math.Abs(atBreakEven.NetProfit) < 0.05m, $"got {atBreakEven.NetProfit}");
    }

    [Fact]
    public void Quote_ZeroPrice_AsksForOneInsteadOfClaimingALoss()
    {
        var quote = Calc().Quote(0m, 40m, RealisticFees());

        Assert.Equal("no_price", quote.Verdict);
        Assert.False(quote.BelowBreakEven);
        Assert.False(quote.BelowMinimumOffer);
    }
}

public class FeeProfileTests
{
    [Fact]
    public void RevenueFeeFraction_IncludesEveryPercentageBasedDeduction()
    {
        var fees = new FeeProfile
        {
            EbayFinalValueFeePercent = 13m,
            PromotedListingRatePercent = 2m,
            PaymentProcessingPercent = 3m,
            ReturnReservePercent = 4m,
            TestingReservePercent = 1m,
        };

        Assert.Equal(0.23m, fees.RevenueFeeFraction);
        Assert.Equal(0.77m, fees.KeepFraction);
    }

    [Fact]
    public void Sanitize_NegativeValues_AreClampedToZero()
    {
        var fees = new FeeProfile { PromotedListingRatePercent = -5m, DefaultShippingCost = -10m, MinimumNetProfit = -1m };

        fees.Sanitize();

        Assert.Equal(0m, fees.PromotedListingRatePercent);
        Assert.Equal(0m, fees.DefaultShippingCost);
        Assert.Equal(0m, fees.MinimumNetProfit);
    }

    // A rate typed as 1325 instead of 13.25 must not make the break-even unsolvable.
    [Fact]
    public void Sanitize_RatesSummingPastOneHundred_AreScaledBackToASolvableProfile()
    {
        var fees = new FeeProfile
        {
            EbayFinalValueFeePercent = 1325m,
            PromotedListingRatePercent = 50m,
            ReturnReservePercent = 40m,
        };

        fees.Sanitize();

        Assert.True(fees.KeepFraction > 0m, $"KeepFraction was {fees.KeepFraction}");
        Assert.True(fees.RevenueFeeFraction <= 0.951m);
        // Still recognisably the seller's own profile — the largest rate stays the largest.
        Assert.True(fees.EbayFinalValueFeePercent > fees.PromotedListingRatePercent);
    }

    [Fact]
    public void CopyFrom_OverwritesEveryFieldInPlace()
    {
        var live = new FeeProfile();
        var incoming = new FeeProfile
        {
            EbayFinalValueFeePercent = 12m, EbayFinalValueFeeFixed = 0.30m,
            PromotedListingRatePercent = 4m, PaymentProcessingPercent = 2.9m,
            DefaultShippingCost = 8m, DefaultPackagingCost = 2m, DefaultLaborCost = 5m,
            ReturnReservePercent = 3m, TestingReservePercent = 1m,
            MinimumNetProfit = 12m, MinimumMarginPercent = 15m,
        };

        live.CopyFrom(incoming);

        Assert.Equal(incoming.RevenueFeeFraction, live.RevenueFeeFraction);
        Assert.Equal(8m, live.DefaultShippingCost);
        Assert.Equal(12m, live.MinimumNetProfit);
        Assert.Equal(15m, live.MinimumMarginPercent);
    }
}
