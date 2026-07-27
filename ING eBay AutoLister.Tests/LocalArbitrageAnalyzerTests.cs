using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The whole point of this feature is that the number on screen is what the seller actually keeps,
// so these cases pin the fee math, the break-even price they'd negotiate against, and the rule
// that a green badge has to be earned by evidence and not just by arithmetic.
public class LocalArbitrageAnalyzerTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer = new(new ProfitCalculator());
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static LocalSupplyListing Listing(
        decimal? price, string title = "Bitmain Antminer S19j Pro", string id = "1", double? miles = 12) =>
        new()
        {
            Source = "facebook", SourceLabel = "Facebook Marketplace",
            ItemId = id, Title = title, Url = $"https://www.facebook.com/marketplace/item/{id}/",
            Price = price, IsFree = price is null, DistanceMiles = miles, Location = "Las Vegas, NV",
        };

    private static ResalePricing Pricing(
        decimal? expected = 200m, int soldComps = 8, int terapeakComps = 0,
        decimal avgShipping = 0m, int confidence = 70) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, TerapeakCompCount = terapeakComps,
            AvgCompShipping = avgShipping, ConfidenceScore = confidence, ConfidenceLevel = "Good",
        };

    // ── Build: the money ───────────────────────────────────────────────────────

    [Fact]
    public void Build_SubtractsEbayFeesFromTheSpread()
    {
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 200m), Fees);

        // $200 sale -> 13.25% + $0.40 = $26.90 in fees, leaving $123.10 over a $50 local buy.
        Assert.Equal(26.90m, row.EstimatedFees);
        Assert.Equal(123.10m, row.NetProfit);
        Assert.Equal(246.2m, row.RoiPercent);
        Assert.InRange(row.MarginPercent!.Value, 61.5m, 61.6m);
    }

    [Fact]
    public void Build_MaxBuyPriceIsTheAskThatBreaksEven()
    {
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 200m), Fees);
        Assert.Equal(173.10m, row.MaxBuyPrice);

        // Paying exactly that leaves nothing — which is what "max to pay" has to mean for
        // someone standing in a driveway negotiating.
        var atCeiling = Analyzer.Build(Listing(row.MaxBuyPrice!.Value), Pricing(expected: 200m), Fees);
        Assert.Equal(0m, atCeiling.NetProfit);
    }

    [Fact]
    public void Build_CompShippingIsBookedAsBothRevenueAndCost()
    {
        var noShipping = Analyzer.Build(Listing(50m), Pricing(expected: 200m), Fees);
        var withShipping = Analyzer.Build(Listing(50m), Pricing(expected: 200m, avgShipping: 20m), Fees);

        // Buyers paid $20 and it costs $20 to ship, so the only real difference is eBay's fee on
        // that $20 ($2.65) — shipping must not show up as free money on either side.
        Assert.Equal(20m, withShipping.EstimatedShipCost);
        Assert.Equal(120.45m, withShipping.NetProfit);
        Assert.Equal(2.65m, noShipping.NetProfit - withShipping.NetProfit);
    }

    [Fact]
    public void Build_NoResaleData_IsNoDataNotZeroProfit()
    {
        var row = Analyzer.Build(Listing(50m), resale: null, Fees);

        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.NetProfit);
        Assert.Null(row.EbayExpectedSale);
        Assert.Equal("none", row.ResaleSource);
    }

    [Fact]
    public void Build_EmptyEstimate_IsAlsoNoData()
    {
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 0m), Fees);
        Assert.Equal("no_data", row.Verdict);
    }

    [Fact]
    public void Build_FallsBackToMedianWhenNoExpectedSalePrice()
    {
        var pricing = Pricing(expected: 200m);
        pricing.ExpectedSale = null;

        var row = Analyzer.Build(Listing(50m), pricing, Fees);
        Assert.Equal(123.10m, row.NetProfit);
    }

    [Fact]
    public void Build_CarriesLocalListingDetailAndEvidenceThrough()
    {
        var listing = Listing(50m, id: "778");
        listing.OriginalPrice = 90m;
        listing.PostedAgo = "2 hours ago";
        var pricing = Pricing(terapeakComps: 4);
        pricing.LiquidityLevel = "Fast Mover";
        pricing.LiquidityScore = 82;
        pricing.DisagreementMessage = "sources differ";

        var row = Analyzer.Build(listing, pricing, Fees);

        Assert.Equal("778", row.ItemId);
        // One ranked table mixes sites, so a row has to say which one to go and buy on.
        Assert.Equal("facebook", row.Source);
        Assert.Equal("Facebook Marketplace", row.SourceLabel);
        Assert.Equal(90m, row.OriginalPrice);       // price-drop tiles are motivated sellers
        Assert.Equal(12, row.DistanceMiles);
        Assert.Equal("2 hours ago", row.PostedAgo);
        Assert.Equal("hosted_comps+terapeak", row.ResaleSource);
        Assert.Equal("Fast Mover", row.LiquidityLevel);
        Assert.Equal(82, row.LiquidityScore);
        Assert.Equal("sources differ", row.DisagreementMessage);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", row.PricedAs);
    }

    [Fact]
    public void Build_FreeListing_HasNoRoiButStillProfits()
    {
        var row = Analyzer.Build(Listing(null), Pricing(expected: 200m), Fees);

        Assert.Equal(0m, row.LocalAsk);
        Assert.Null(row.RoiPercent);       // no cost basis — undefined, not zero
        Assert.Equal(173.10m, row.NetProfit);
        Assert.Equal("goldmine", row.Verdict);
    }

    // ── Judge: verdicts have to be earned ──────────────────────────────────────

    [Fact]
    public void Judge_LosesMoney_IsPass()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(-12m, -20m, localAsk: 60m, compCount: 9, confidenceScore: 80);
        Assert.Equal("pass", verdict);
        Assert.Contains("60", note);
    }

    [Fact]
    public void Judge_ExactlyBreakEven_IsPass()
    {
        var (verdict, _) = LocalArbitrageAnalyzer.Judge(0m, 0m, 60m, 9, 80);
        Assert.Equal("pass", verdict);
    }

    [Fact]
    public void Judge_StrongNumbersWithRealEvidence_IsGoldmine()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(180m, 300m, 60m, compCount: 9, confidenceScore: 80);
        Assert.Equal("goldmine", verdict);
        Assert.Contains("9 sold comps", note);
    }

    // The lesson already learned once on sell-through: a huge number on two comps is not a
    // green badge, it's an unverified guess.
    [Fact]
    public void Judge_HugeProfitOnTwoComps_IsThinNotGoldmine()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(400m, 800m, 50m, compCount: 2, confidenceScore: 90);
        Assert.Equal("thin", verdict);
        Assert.Contains("too few", note);
    }

    [Fact]
    public void Judge_GoldmineNumbersButWeakConfidence_IsNotGoldmine()
    {
        var (verdict, _) = LocalArbitrageAnalyzer.Judge(180m, 300m, 60m, compCount: 9, confidenceScore: 20);
        Assert.NotEqual("goldmine", verdict);
    }

    [Fact]
    public void Judge_BigPercentageOnPocketChange_IsNotGoldmine()
    {
        // 400% ROI on a $5 buy is $20 — a real margin and a pointless drive.
        var (verdict, _) = LocalArbitrageAnalyzer.Judge(20m, 400m, 5m, compCount: 9, confidenceScore: 80);
        Assert.Equal("thin", verdict);
    }

    [Fact]
    public void Judge_ModestButRealMargin_IsSolid()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(60m, 40m, 150m, compCount: 9, confidenceScore: 70);
        Assert.Equal("solid", verdict);
        Assert.Contains("40% ROI", note);
    }

    [Fact]
    public void Judge_FreeItem_TreatsUndefinedRoiAsUnbounded()
    {
        var (verdict, _) = LocalArbitrageAnalyzer.Judge(150m, roiPercent: null, localAsk: 0m, compCount: 9, confidenceScore: 80);
        Assert.Equal("goldmine", verdict);
    }

    [Theory]
    [InlineData(5, 0, "hosted_comps")]
    [InlineData(0, 5, "terapeak")]
    [InlineData(5, 5, "hosted_comps+terapeak")]
    [InlineData(0, 0, "none")]
    public void SourceLabel_NamesWhicheverSourcesContributed(int sold, int terapeak, string expected) =>
        Assert.Equal(expected, LocalArbitrageAnalyzer.SourceLabel(sold, terapeak));

    // ── Grouping: one comp lookup per product, not per tile ────────────────────

    [Fact]
    public void GroupByProduct_SameProductDifferentSellers_IsOneGroup()
    {
        var groups = LocalArbitrageAnalyzer.GroupByProduct(
            [Listing(400m, "S19j Pro", "1"), Listing(520m, "Antminer S19j Pro 104TH miner", "2")],
            _ => "bitmain|s19j pro");

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Listings.Count);
        // The fullest title wins — the matcher can only work with the words it's given.
        Assert.Equal("Antminer S19j Pro 104TH miner", groups[0].LookupTitle);
        Assert.Equal(400m, groups[0].LowestAsk);
    }

    [Fact]
    public void GroupByProduct_BlankKey_FallsBackToTitleInsteadOfCollapsing()
    {
        var groups = LocalArbitrageAnalyzer.GroupByProduct(
            [Listing(20m, "old lamp", "1"), Listing(30m, "wooden chair", "2")], _ => "");

        Assert.Equal(2, groups.Count);
    }

    [Fact]
    public void GroupByProduct_SkipsUntitledTiles()
    {
        var groups = LocalArbitrageAnalyzer.GroupByProduct(
            [Listing(20m, "", "1"), Listing(30m, "wooden chair", "2")], l => l.Title);

        Assert.Single(groups);
        Assert.Equal("wooden chair", groups[0].LookupTitle);
    }

    [Fact]
    public void GroupByProduct_FreeListingsStillCountAsAGroup()
    {
        var groups = LocalArbitrageAnalyzer.GroupByProduct([Listing(null, "free treadmill", "1")], l => l.Title);

        Assert.Single(groups);
        Assert.Equal(0m, groups[0].LowestAsk); // no priced listing to take a floor from
    }

    // ── Scrape budget: a Terapeak scrape is a browser page load, not an API call ─

    private static (string, decimal?, bool, decimal) Group(string key, decimal? profit, bool cached = false, decimal ask = 100m) =>
        (key, profit, cached, ask);

    [Fact]
    public void SelectScrapeTargets_ZeroBudget_SpendsNothing() =>
        Assert.Empty(LocalArbitrageAnalyzer.SelectScrapeTargets([Group("a", 500m)], budget: 0));

    [Fact]
    public void SelectScrapeTargets_SkipsProductsTerapeakAlreadyCached()
    {
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            [Group("cached", 900m, cached: true), Group("fresh", 100m)], budget: 5);

        Assert.Equal(["fresh"], targets);
    }

    [Fact]
    public void SelectScrapeTargets_CorroboratesTheBiggestProfitsFirst()
    {
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            [Group("small", 20m), Group("huge", 900m), Group("mid", 300m)], budget: 2);

        Assert.Equal(["huge", "mid"], targets);
    }

    [Fact]
    public void SelectScrapeTargets_UnpricedProductsComeAfterProfitableOnes_BiggestAskFirst()
    {
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            [Group("unpriced-cheap", null, ask: 20m), Group("unpriced-dear", null, ask: 900m), Group("profitable", 40m)],
            budget: 3);

        Assert.Equal(["profitable", "unpriced-dear", "unpriced-cheap"], targets);
    }

    [Fact]
    public void SelectScrapeTargets_KnownLosersAreNotWorthAScrape()
    {
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            [Group("loser", -80m), Group("winner", 10m)], budget: 5);

        Assert.Equal(["winner"], targets);
    }

    [Fact]
    public void SelectScrapeTargets_NeverExceedsTheBudget() =>
        Assert.Equal(3, LocalArbitrageAnalyzer.SelectScrapeTargets(
            [Group("a", 10m), Group("b", 20m), Group("c", 30m), Group("d", 40m), Group("e", 50m)], budget: 3).Count);

    // ── Ranking ────────────────────────────────────────────────────────────────

    private static LocalArbitrageOpportunity Row(string title, decimal? profit, decimal? roi = null, double? miles = null) =>
        new() { Title = title, NetProfit = profit, RoiPercent = roi, DistanceMiles = miles };

    [Fact]
    public void Rank_BestMoneyFirst()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([Row("b", 40m), Row("a", 400m), Row("c", 120m)]);
        Assert.Equal(["a", "c", "b"], ranked.Select(r => r.Title));
    }

    [Fact]
    public void Rank_UnpricedRowsSortLastButAreNeverDropped()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([Row("unknown", null), Row("loss", -50m), Row("win", 10m)]);

        Assert.Equal(["win", "loss", "unknown"], ranked.Select(r => r.Title));
        Assert.Equal(3, ranked.Count);
    }

    [Fact]
    public void Rank_EqualProfit_PrefersTheCloserDrive()
    {
        var ranked = LocalArbitrageAnalyzer.Rank(
            [Row("far", 100m, 50m, miles: 48), Row("near", 100m, 50m, miles: 3)]);

        Assert.Equal(["near", "far"], ranked.Select(r => r.Title));
    }

    // ── Days to cash: ranking by how fast the money comes back ─────────────────

    private static LocalArbitrageOpportunity SpeedRow(
        string title, decimal profit, int? daysToCash, decimal? perDay = null, string tier = "steady") =>
        new()
        {
            Title = title, NetProfit = profit, DaysToCash = daysToCash,
            ProfitPerDay = perDay ?? (daysToCash is int d && d > 0 ? Math.Round(profit / d, 2) : null),
            SpeedTier = tier,
        };

    [Fact]
    public void Build_PricesTheWaitAsWellAsTheProfit()
    {
        var resale = Pricing(expected: 200m);
        resale.EstimatedDaysToSell = 12;
        resale.EstimatedMonthlySales = 2.5m;

        var row = Analyzer.Build(Listing(50m), resale, Fees);

        Assert.Equal(12, row.DaysToSell);
        Assert.Equal(12 + DaysToCashEstimator.PipelineDays, row.DaysToCash);
        // Profit per day is this row's own profit over the wait, not the product's in general.
        Assert.Equal(Math.Round(row.NetProfit!.Value / row.DaysToCash!.Value, 2), row.ProfitPerDay);
        Assert.Equal("fast", row.SpeedTier);
        Assert.NotEqual("", row.SpeedNote);
    }

    [Fact]
    public void Build_NoVelocityEvidence_LeavesTheWaitUnknownRatherThanGuessing()
    {
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 200m), Fees);

        Assert.Null(row.DaysToCash);
        Assert.Null(row.ProfitPerDay);
        Assert.Equal("unknown", row.SpeedTier);
    }

    [Fact]
    public void Rank_FastestCash_PutsTheSoonestMoneyFirst()
    {
        var ranked = LocalArbitrageAnalyzer.Rank(
            [SpeedRow("slow", 300m, 180), SpeedRow("quick", 45m, 15), SpeedRow("middling", 90m, 60)],
            LocalArbitrageAnalyzer.SortByFastestCash);

        Assert.Equal(["quick", "middling", "slow"], ranked.Select(r => r.Title));
    }

    [Fact]
    public void Rank_ProfitPerDay_BeatsTheBiggerButStalerMargin()
    {
        // $45 in 15 days is $3/day; $300 in 180 days is $1.67/day. The small flip wins.
        var ranked = LocalArbitrageAnalyzer.Rank(
            [SpeedRow("fat-and-stale", 300m, 180), SpeedRow("small-and-quick", 45m, 15)],
            LocalArbitrageAnalyzer.SortByProfitPerDay);

        Assert.Equal(["small-and-quick", "fat-and-stale"], ranked.Select(r => r.Title));
    }

    [Fact]
    public void Rank_VelocitySorts_StillKeepLosersAndUnpricedRowsAtTheBottom()
    {
        // A one-day route to a loss is not a fast flip.
        var loser = SpeedRow("loss", -20m, 2);
        var unpriced = new LocalArbitrageOpportunity { Title = "unknown", NetProfit = null };
        var winner = SpeedRow("win", 30m, 90);

        foreach (var sort in new[] { LocalArbitrageAnalyzer.SortByFastestCash, LocalArbitrageAnalyzer.SortByProfitPerDay })
            Assert.Equal(["win", "loss", "unknown"],
                LocalArbitrageAnalyzer.Rank([loser, unpriced, winner], sort).Select(r => r.Title));
    }

    [Fact]
    public void Rank_FastestCash_AnUnmeasuredWaitIsNeverTreatedAsInstant()
    {
        var ranked = LocalArbitrageAnalyzer.Rank(
            [SpeedRow("unmeasured", 100m, null, tier: "unknown"), SpeedRow("measured", 100m, 40)],
            LocalArbitrageAnalyzer.SortByFastestCash);

        Assert.Equal(["measured", "unmeasured"], ranked.Select(r => r.Title));
    }

    [Fact]
    public void Rank_EqualMoney_PrefersTheOneThatPaysBackSooner()
    {
        var slow = SpeedRow("slow", 100m, 120);
        var fast = SpeedRow("fast", 100m, 20);
        slow.RoiPercent = fast.RoiPercent = 50m;

        // Default (money-first) ordering, where the two rows are otherwise identical.
        Assert.Equal(["fast", "slow"], LocalArbitrageAnalyzer.Rank([slow, fast]).Select(r => r.Title));
    }

    [Theory]
    [InlineData(null, "profit")]
    [InlineData("", "profit")]
    [InlineData("nonsense", "profit")]
    [InlineData("FASTEST", "fastest")]
    [InlineData(" perday ", "profit_per_day")]
    public void NormalizeSort_UnknownSortsFallBackToMoneyFirst(string? input, string expected) =>
        Assert.Equal(expected, LocalArbitrageAnalyzer.NormalizeSort(input));
}
