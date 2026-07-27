using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class PromotedListingAdvisorTests
{
    private static PromotedListingAdvisor Advisor() => new(new ProfitCalculator());

    // A seller who has configured their real costs — the case the advisor exists for, since the
    // margin an ad rate is measured against is the whole point.
    private static FeeProfile Fees() => new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        PromotedListingRatePercent = 0m,
        DefaultShippingCost = 9m,
        DefaultPackagingCost = 1.25m,
        DefaultLaborCost = 3m,
    };

    private static PromotedListingAdvisor.Input Listing(
        decimal price = 200m, decimal? cost = 90m, string category = "Consumer Electronics",
        decimal currentRate = 0m, int soldComps = 8, int quantitySold = 0, int? daysListed = 45,
        decimal? salesPerMonth = null, int watchCount = 0, decimal? marketPrice = null,
        decimal? priceGap = null, bool marketComparable = true, string liquidityLevel = "Moderate",
        int liquidityScore = 50) =>
        new(Title: "Test item", ListPrice: price, UnitCost: cost, Category: category,
            CurrentRatePercent: currentRate, SoldCompCount: soldComps, QuantitySold: quantitySold,
            DaysListed: daysListed, SalesPerMonth: salesPerMonth, WatchCount: watchCount,
            MarketPrice: marketPrice, PriceGapPercent: priceGap, MarketComparable: marketComparable,
            LiquidityLevel: liquidityLevel, LiquidityScore: liquidityScore);

    // ── The lift curve ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Lift_AtTheCategoryRate_IsHalfTheCeiling()
    {
        var lift = PromotedListingAdvisor.LiftPercentAt(ratePercent: 6m, maxLiftPercent: 40m, halfLiftRatePercent: 6m);
        Assert.Equal(20m, lift);
    }

    [Fact]
    public void Lift_Saturates_SoDoublingTheRateNeverDoublesTheSales()
    {
        var single = PromotedListingAdvisor.LiftPercentAt(5m, 40m, 5m);
        var doubled = PromotedListingAdvisor.LiftPercentAt(10m, 40m, 5m);

        Assert.True(doubled > single);
        Assert.True(doubled < single * 2m,
            $"a saturating curve must return less than double for double the rate; {single} -> {doubled}");
    }

    [Fact]
    public void Lift_AtZeroRate_IsZero() =>
        Assert.Equal(0m, PromotedListingAdvisor.LiftPercentAt(0m, 40m, 6m));

    // A crowded category needs a bigger rate to buy the same placement. That is the whole reason the
    // category norm is the curve's half-saturation point rather than a recommendation of its own.
    [Fact]
    public void Lift_SameRate_BuysLessInACrowdedCategory()
    {
        var quiet = PromotedListingAdvisor.LiftPercentAt(5m, 40m, halfLiftRatePercent: 4.5m);
        var crowded = PromotedListingAdvisor.LiftPercentAt(5m, 40m, halfLiftRatePercent: 11m);
        Assert.True(crowded < quiet);
    }

    // ── Break-even lift: the model-free half ─────────────────────────────────────────────────

    [Fact]
    public void BreakEvenLift_IsExactlyTheAlgebra()
    {
        // L* = c·f / (n - f): $12 of ads against $60 of margin at 50% cannibalisation.
        var lift = PromotedListingAdvisor.BreakEvenLiftPercent(adFeePerSale: 12m, netPerSaleNoAds: 60m, cannibalizationPercent: 50m);
        Assert.Equal(12.5m, lift!.Value);
    }

    [Fact]
    public void BreakEvenLift_AtTheMarginCeiling_IsImpossible()
    {
        // The ad fee equals the entire profit: no amount of extra volume can pay for it.
        Assert.Null(PromotedListingAdvisor.BreakEvenLiftPercent(60m, 60m, 50m));
        Assert.Null(PromotedListingAdvisor.BreakEvenLiftPercent(75m, 60m, 50m));
    }

    [Fact]
    public void BreakEvenLift_WithNoAdSpend_IsZero() =>
        Assert.Equal(0m, PromotedListingAdvisor.BreakEvenLiftPercent(0m, 60m, 50m));

    // Cannibalisation is the reason a small ad rate is not close to free: it is charged on sales the
    // listing was already making.
    [Fact]
    public void BreakEvenLift_RisesWithCannibalization()
    {
        var low = PromotedListingAdvisor.BreakEvenLiftPercent(10m, 100m, 30m)!.Value;
        var high = PromotedListingAdvisor.BreakEvenLiftPercent(10m, 100m, 70m)!.Value;
        Assert.True(high > low);
    }

    // The identity the whole ladder rests on: at exactly the break-even lift, take-home is unchanged.
    [Fact]
    public void AtTheBreakEvenLift_NetPer100SalesIsUnchanged()
    {
        const decimal net = 60m, fee = 12m, cannibalization = 50m;
        var required = PromotedListingAdvisor.BreakEvenLiftPercent(fee, net, cannibalization)!.Value;

        var noAds = PromotedListingAdvisor.NetPer100Sales(net, 0m, 0m, cannibalization)!.Value;
        var atBreakEven = PromotedListingAdvisor.NetPer100Sales(net, fee, required, cannibalization)!.Value;

        Assert.True(Math.Abs(atBreakEven - noAds) < 0.5m,
            $"expected the break-even lift to leave take-home unchanged; {noAds} vs {atBreakEven}");
    }

    // ── The margin ceiling ───────────────────────────────────────────────────────────────────

    [Fact]
    public void MarginCeiling_IsTheRateAtWhichTheFeeEatsTheWholeProfit()
    {
        var ceiling = PromotedListingAdvisor.MarginCeilingRatePercent(netPerSaleNoAds: 30m, grossPerSale: 200m);
        Assert.Equal(15m, ceiling!.Value);
        Assert.Equal(30m, PromotedListingAdvisor.AdFeeAt(15m, 200m));
    }

    [Fact]
    public void MarginCeiling_OnALosingSale_DoesNotExist() =>
        Assert.Null(PromotedListingAdvisor.MarginCeilingRatePercent(-5m, 200m));

    // ── The recommendation ───────────────────────────────────────────────────────────────────

    // The headline claim of the whole feature: a healthy margin can carry ads, a thin one cannot,
    // and the difference is decided by arithmetic rather than by eBay's category average.
    [Fact]
    public void HealthyMargin_GetsARealAdRate()
    {
        var advice = Advisor().Build(Listing(price: 400m, cost: 120m), Fees());

        Assert.True(advice.RecommendedRatePercent >= PromotedRateNorms.EbayMinimumRatePercent,
            $"a fat margin should carry an ad rate, got {advice.RecommendedRatePercent}");
        Assert.True(advice.NetPerSaleAtRecommended > 0m);
    }

    [Fact]
    public void ThinMargin_IsToldNotToPromote()
    {
        // $120 sale on an $85 item: after eBay's cut, the label, packaging and handling there is a
        // few dollars left — and eBay's ad rate is charged on the whole $120, not on the few dollars.
        var advice = Advisor().Build(Listing(price: 120m, cost: 85m), Fees());

        Assert.True(advice.NetPerSaleNoAds > 0m, "this case is a thin margin, not a losing one");
        Assert.Equal(0m, advice.RecommendedRatePercent);
        Assert.Equal("dont_promote", advice.Verdict);
    }

    [Fact]
    public void SaleThatAlreadyLosesMoney_IsRefusedOutright()
    {
        var advice = Advisor().Build(Listing(price: 100m, cost: 120m), Fees());

        Assert.Equal("no_margin", advice.Verdict);
        Assert.Equal(0m, advice.RecommendedRatePercent);
        Assert.True(advice.NetPerSaleNoAds < 0m);
    }

    // eBay will not carry a Standard campaign under 2%, so a recommendation is either "don't" or a
    // rate eBay will actually accept — never an unusable 0.7%.
    [Fact]
    public void Recommendation_IsEitherZeroOrAtLeastEbaysMinimum()
    {
        foreach (var cost in new[] { 20m, 60m, 90m, 130m, 175m })
        {
            var rate = Advisor().Build(Listing(price: 200m, cost: cost), Fees()).RecommendedRatePercent ?? 0m;
            Assert.True(rate == 0m || rate >= PromotedRateNorms.EbayMinimumRatePercent, $"cost {cost} gave {rate}%");
        }
    }

    [Fact]
    public void Recommendation_NeverExceedsTheMarginCeiling()
    {
        foreach (var cost in new[] { 20m, 60m, 90m, 130m, 175m })
        {
            var advice = Advisor().Build(Listing(price: 200m, cost: cost), Fees());
            if (advice.RecommendedRatePercent is not decimal rate || rate <= 0m) continue;

            Assert.True(rate < advice.MaxSustainableRatePercent!.Value,
                $"cost {cost}: recommended {rate}% against a {advice.MaxSustainableRatePercent}% ceiling");
            Assert.True(advice.NetPerSaleAtRecommended > 0m);
        }
    }

    // The rate the search picks must genuinely be the best rung available, not merely a plausible one.
    [Fact]
    public void Recommendation_IsTheBestRungOnItsOwnLadder()
    {
        var advice = Advisor().Build(Listing(price: 400m, cost: 120m), Fees());
        var best = advice.Ladder.Where(p => p.NetPer100Sales.HasValue).MaxBy(p => p.NetPer100Sales!.Value)!;

        Assert.Equal(best.RatePercent, advice.RecommendedRatePercent);
        Assert.True(best.IsRecommended);
    }

    [Fact]
    public void Ladder_AlwaysIncludesDoingNothing_AndMarksTheCurrentRate()
    {
        var advice = Advisor().Build(Listing(price: 300m, cost: 100m, currentRate: 9m), Fees());

        Assert.Contains(advice.Ladder, p => p.RatePercent == 0m);
        Assert.Contains(advice.Ladder, p => p.RatePercent == 9m && p.IsCurrent);
        Assert.Equal(0m, advice.Ladder.Single(p => p.RatePercent == 0m).NetChangePer100);
    }

    [Fact]
    public void Ladder_FlagsRungsWhereTheFeeExceedsTheMargin()
    {
        var advice = Advisor().Build(Listing(price: 200m, cost: 150m), Fees());
        var ceiling = advice.MaxSustainableRatePercent!.Value;

        Assert.All(advice.Ladder.Where(p => p.RatePercent > ceiling), p =>
        {
            Assert.True(p.AboveCeiling);
            Assert.Null(p.BreakEvenLiftPercent);
        });
    }

    // The same ad rate does not buy the same placement everywhere: where the field pays 11%, a 6%
    // bid is mid-pack, and where it pays 4.5% the same 6% is aggressive. That is why the category
    // norm sets the curve rather than being handed back as the recommendation.
    [Fact]
    public void CrowdedCategory_BuysLessForTheSameRate()
    {
        var quiet = Advisor().Build(Listing(price: 400m, cost: 120m, category: "Business & Industrial"), Fees());
        var crowded = Advisor().Build(Listing(price: 400m, cost: 120m, category: "Trading Cards"), Fees());

        Assert.True(crowded.CategoryRatePercent > quiet.CategoryRatePercent);

        static AdRatePoint Rung(PromotedAdvice a, decimal rate) => a.Ladder.Single(p => p.RatePercent == rate);
        Assert.True(Rung(crowded, 6m).ModeledLiftPercent < Rung(quiet, 6m).ModeledLiftPercent);
        Assert.True(Rung(crowded, 6m).NetChangePer100 < Rung(quiet, 6m).NetChangePer100);
        // Identical fee either way — the difference is entirely in what it buys.
        Assert.Equal(Rung(quiet, 6m).AdFeePerSale, Rung(crowded, 6m).AdFeePerSale);
    }

    // ── What it refuses to do ────────────────────────────────────────────────────────────────

    [Fact]
    public void WithoutACostBasis_ItSizesNothing_AndSaysWhy()
    {
        var advice = Advisor().Build(Listing(cost: null), Fees());

        Assert.Equal("no_cost_basis", advice.Verdict);
        Assert.False(advice.HasRecommendation);
        Assert.Null(advice.NetPerSaleNoAds);
        // It still shows what an ad rate would cost per sale — that part needs no cost basis.
        Assert.Contains(advice.Ladder, p => p.AdFeePerSale > 0m);
    }

    // Thin evidence does not earn a bid above the field. A listing the app knows nothing about is
    // not the place to outspend the category.
    [Fact]
    public void WithNoSoldHistory_TheRateIsHeldAtTheCategoryNorm()
    {
        var advice = Advisor().Build(
            Listing(price: 600m, cost: 100m, soldComps: 0, liquidityScore: 0, liquidityLevel: ""), Fees());

        Assert.Equal("thin", advice.EvidenceLevel);
        Assert.True(advice.RecommendedRatePercent <= advice.CategoryRatePercent,
            $"thin evidence recommended {advice.RecommendedRatePercent}% over a {advice.CategoryRatePercent}% norm");
        Assert.Contains(advice.Signals, s => s.Contains("category norm"));
    }

    // Ads amplify whatever the listing already is. Amplifying a price buyers are beating is how a
    // seller pays to lose more often.
    [Fact]
    public void PricedAboveMarket_IsToldToFixThePriceFirst()
    {
        var advice = Advisor().Build(
            Listing(price: 300m, cost: 90m, marketPrice: 200m, priceGap: 50m), Fees());

        Assert.Equal("fix_price_first", advice.Verdict);
        Assert.True(advice.RecommendedRatePercent <= advice.CategoryRatePercent);
    }

    // The same floor that bounds the repricer's markdown ladder and the watcher-offer depth has to
    // bound this too: an ad rate spends margin exactly like a discount does.
    [Fact]
    public void TheSellersMinimumProfitFloor_BoundsTheAdRate()
    {
        var fees = Fees();
        var unbounded = Advisor().Build(Listing(price: 300m, cost: 100m), fees).RecommendedRatePercent!.Value;

        fees.MinimumNetProfit = 140m;
        var advice = Advisor().Build(Listing(price: 300m, cost: 100m), fees);

        Assert.True(advice.RecommendedRatePercent < unbounded,
            $"a $140 floor should hold the rate below {unbounded}%, got {advice.RecommendedRatePercent}%");
        Assert.True(advice.NetPerSaleAtRecommended >= 140m);
    }

    // A listing that already sells is being found without paying — and eBay bills the ad rate on
    // those sales too. It should be promoted less, not more, whatever eBay's suggestion says.
    [Fact]
    public void AProvenSeller_IsAdvisedALowerRateThanAStuckOne()
    {
        var selling = Advisor().Build(
            Listing(price: 300m, cost: 90m, quantitySold: 12, salesPerMonth: 3m, daysListed: 120), Fees());
        var stuck = Advisor().Build(
            Listing(price: 300m, cost: 90m, quantitySold: 0, daysListed: 120), Fees());

        Assert.True(selling.RecommendedRatePercent < stuck.RecommendedRatePercent,
            $"selling {selling.RecommendedRatePercent}% vs stuck {stuck.RecommendedRatePercent}%");
        Assert.True(selling.Assumptions.CannibalizationPercent > stuck.Assumptions.CannibalizationPercent);
    }

    [Fact]
    public void NoPrice_IsReportedRatherThanPricedAtZero()
    {
        var advice = Advisor().Build(Listing(price: 0m), Fees());

        Assert.Equal("no_price", advice.Verdict);
        Assert.False(advice.HasRecommendation);
        Assert.Empty(advice.Ladder);
    }

    // ── Where the money is measured ──────────────────────────────────────────────────────────

    [Fact]
    public void AdFee_IsChargedOnShippingToo()
    {
        var advice = Advisor().Build(
            new PromotedListingAdvisor.Input("Item", ListPrice: 100m, UnitCost: 30m, BuyerPaidShipping: 20m,
                CurrentRatePercent: 10m, SoldCompCount: 8), Fees());

        Assert.Equal(120m, advice.GrossPerSale);
        Assert.Equal(12m, advice.AdFeeAtCurrent);   // 10% of the whole sale, not of the item price
    }

    // Monthly dollars are only reported where the listing's own history supports them — a
    // projection off a listing that has never sold would be an invented number with a dollar sign.
    [Fact]
    public void MonthlyMoney_IsOnlyReportedWhenTheListingHasSalesHistory()
    {
        var withHistory = Advisor().Build(
            Listing(price: 300m, cost: 90m, quantitySold: 6, salesPerMonth: 2m, currentRate: 12m), Fees());
        var without = Advisor().Build(Listing(price: 300m, cost: 90m, currentRate: 12m), Fees());

        Assert.NotNull(withHistory.ExtraProfitPerMonth);
        Assert.NotNull(withHistory.AdSpendPerMonthAtCurrent);
        Assert.Null(without.ExtraProfitPerMonth);
        // Per-sale money is always available, though — that needs no volume estimate.
        Assert.True(without.AdFeeAtCurrent > 0m);
    }

    // Optimal and worth doing are different questions. On a cheap item the best rate can beat the
    // current one by pennies per hundred sales, and a board that lists that as a task is a board
    // nobody finishes.
    [Fact]
    public void ARateThatIsBetterByPennies_IsNotReportedAsAChangeToMake()
    {
        var advice = Advisor().Build(Listing(price: 9m, cost: 3m, currentRate: 0m), Fees());

        if (advice.RecommendedRatePercent is decimal rate && rate >= 1m)
        {
            Assert.False(advice.ChangeWorthMaking,
                $"a {advice.NetGainPer100:C} gain per 100 sales should not read as a task");
            Assert.False(advice.NeedsChange);
            Assert.Equal("on_target", advice.Verdict);
        }
    }

    [Fact]
    public void MovingOffAnOverpayingRate_IsWorthRealMoneyPer100Sales()
    {
        var advice = Advisor().Build(Listing(price: 300m, cost: 90m, currentRate: 18m), Fees());

        Assert.Equal("over_promoted", advice.Verdict);
        Assert.True(advice.NetGainPer100 > 0m);
        Assert.True(advice.AdFeeChangePerSale < 0m, "the recommendation should spend less per sale");
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Summary_CountsTheVerdicts_AndWeightsTheBlendedRateByRevenue()
    {
        var advisor = Advisor();
        var items = new List<PromotedAdvice>
        {
            advisor.Build(Listing(price: 1200m, cost: 300m, currentRate: 0m), Fees()),   // under-promoted
            advisor.Build(Listing(price: 300m, cost: 90m, currentRate: 18m), Fees()),    // over-promoted
            advisor.Build(Listing(price: 120m, cost: 95m, currentRate: 6m), Fees()),     // shouldn't promote
            advisor.Build(Listing(price: 200m, cost: null), Fees()),                     // no cost basis
        };

        var summary = PromotedListingAdvisor.Summarize(items);

        Assert.Equal(4, summary.ListingsAnalyzed);
        Assert.Equal(3, summary.WithCostBasis);
        Assert.Equal(1, summary.UnderPromoted);
        Assert.Equal(1, summary.OverPromoted);
        Assert.Equal(1, summary.ShouldNotPromote);
        Assert.True(summary.OverspendPerRound > 0m);
        Assert.NotNull(summary.BlendedRecommendedPercent);
    }

    [Fact]
    public void Rank_PutsTheListingsWorthChangingFirst()
    {
        var advisor = Advisor();
        var onTarget = advisor.Build(Listing(price: 60m, cost: 20m, currentRate: 0m), Fees());
        var wrong = advisor.Build(Listing(price: 1200m, cost: 300m, currentRate: 20m), Fees());

        var ranked = PromotedListingAdvisor.Rank([onTarget, wrong]);
        Assert.True(ranked[0].NeedsChange);
        Assert.Equal(wrong.ListPrice, ranked[0].ListPrice);
    }
}
