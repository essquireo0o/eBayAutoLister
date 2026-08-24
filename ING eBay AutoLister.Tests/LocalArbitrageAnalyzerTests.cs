using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The whole point of this feature is that the number on screen is what the seller actually keeps,
// so these cases pin the fee math, the break-even price they'd negotiate against, and the rule
// that a green badge has to be earned by evidence and not just by arithmetic.
public class LocalArbitrageAnalyzerTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer = new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static LocalSupplyListing Listing(
        decimal? price, string title = "Bitmain Antminer S19j Pro", string id = "1", double? miles = 12) =>
        new()
        {
            Source = "facebook", SourceLabel = "Facebook Marketplace",
            ItemId = id, Title = title, Url = $"https://www.facebook.com/marketplace/item/{id}/",
            Price = price, IsFree = price is null, DistanceMiles = miles, Location = "Las Vegas, NV",
        };

    // pricedComps defaults to soldComps: unless a case is specifically about thin evidence, the
    // comps the lookup returned are the comps that priced it.
    private static ResalePricing Pricing(
        decimal? expected = 200m, int soldComps = 8, int terapeakComps = 0,
        decimal avgShipping = 0m, int confidence = 70, int? pricedComps = null,
        bool identityVerified = true) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, TerapeakCompCount = terapeakComps,
            PricedCompCount = pricedComps ?? soldComps, IdentityVerified = identityVerified,
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
    public void Build_InboundShippingIsAddedToWhatTheBuyerActuallyPays()
    {
        var shipped = Listing(100m);
        shipped.Source = EbaySupplySource.SourceId;
        shipped.PurchaseShippingCost = 47m;

        var row = Analyzer.Build(shipped, Pricing(expected: 300m), Fees, retailSalesTaxPercent: 0m);
        var noInbound = Analyzer.Build(Listing(100m), Pricing(expected: 300m), Fees);

        Assert.Equal(100m, row.LocalAsk);
        Assert.Equal(47m, row.PurchaseShippingCost);
        Assert.Equal(147m, row.BuyCostAllIn);
        Assert.Equal(noInbound.NetProfit - 47m, row.NetProfit);
        Assert.Equal(Math.Round(row.NetProfit!.Value / 147m * 100m, 1), row.RoiPercent);
    }

    [Fact]
    public void Build_UnknownEbayInboundShippingIsNeverAssumedFree()
    {
        var shipped = Listing(100m);
        shipped.Source = EbaySupplySource.SourceId;
        shipped.PurchaseShippingCost = null;

        var row = Analyzer.Build(shipped, Pricing(expected: 300m), Fees, retailSalesTaxPercent: 0m);

        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.NetProfit);
        Assert.Null(row.RoiPercent);
        Assert.Contains("shipping", row.VerdictNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not treated as free", row.EvidenceNote, StringComparison.OrdinalIgnoreCase);
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
        listing.SellerUsername = "new_seller";
        listing.SellerFeedbackScore = 4;
        listing.SellerFeedbackPercent = 100m;
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
        Assert.Equal("new_seller", row.SellerUsername);
        Assert.Equal(4, row.SellerFeedbackScore);
        Assert.Equal(100m, row.SellerFeedbackPercent);
        Assert.Equal("hosted_comps+terapeak", row.ResaleSource);
        Assert.Equal("Fast Mover", row.LiquidityLevel);
        Assert.Equal(82, row.LiquidityScore);
        Assert.Equal("sources differ", row.DisagreementMessage);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", row.PricedAs);
    }

    [Fact]
    public void Build_FreeListing_HasNoRoiButStillProfits()
    {
        var row = Analyzer.Build(Listing(null), Pricing(expected: 600m), Fees);

        Assert.Equal(0m, row.LocalAsk);
        Assert.Null(row.RoiPercent);       // no cost basis — undefined, not zero
        Assert.Equal(520.10m, row.NetProfit);
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
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(300m, 300m, 100m, compCount: 9, confidenceScore: 80);
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
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(160m, 40m, 400m, compCount: 9, confidenceScore: 70);
        Assert.Equal("solid", verdict);
        Assert.Contains("40% ROI", note);
    }

    /// <summary>
    /// The bar this whole tier exists for: real money, not a real percentage. $60 on a $150 buy is
    /// a 40% return and it is still $60, which does not pay for the finding, listing and packing.
    /// </summary>
    [Fact]
    public void Judge_GoodPercentageButUnderTheCashBar_IsThin()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(60m, 40m, 150m, compCount: 9, confidenceScore: 70);
        Assert.Equal("thin", verdict);
        Assert.Contains($"${LocalArbitrageAnalyzer.SolidProfit:0}", note);
    }

    /// <summary>
    /// The other half of the same principle, and the reason the cash-alone tier exists: a big buy
    /// at a modest multiple is a good day's work. $900 on a $3,600 buy is 25% — under the goldmine
    /// ROI bar and nowhere near "not worth doing".
    /// </summary>
    [Fact]
    public void Judge_BigCashAtAModestMultiple_IsStillAGoldmine()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(900m, 25m, 3600m, compCount: 9, confidenceScore: 80);
        Assert.Equal("goldmine", verdict);
        Assert.Contains("900", note);
    }

    [Fact]
    public void Judge_FreeItem_TreatsUndefinedRoiAsUnbounded()
    {
        var (verdict, _) = LocalArbitrageAnalyzer.Judge(300m, roiPercent: null, localAsk: 0m, compCount: 9, confidenceScore: 80);
        Assert.Equal("goldmine", verdict);
    }

    /// <summary>
    /// Profit is half a return; how long it takes to come back is the other half. Money that sits
    /// for most of a year isn't buying the next deal, however good the figure on the row.
    /// </summary>
    [Fact]
    public void Judge_MoneyThatSits_IsHeldBackWhateverTheProfit()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(
            900m, 120m, 750m, compCount: 9, confidenceScore: 80, speedTier: "dead_money");

        Assert.Equal("thin", verdict);
        Assert.Contains("sits", note);
    }

    // ── Confidence gating: a percentage has to be backed before it's shown as one ──────────────
    // The failure these pin: a $60 "toyota trailer hitch" matched to ONE loose sold comp at $554,
    // published as a 698% return in the same typeface as a return backed by twenty sales. Nothing
    // here changes the arithmetic — it changes what the board is allowed to CLAIM about it.

    [Fact]
    public void GradeEvidence_EnoughMatchingComps_IsConfident()
    {
        var (tier, note) = LocalArbitrageAnalyzer.GradeEvidence(
            pricedCompCount: 6, terapeakCompCount: 0, identityVerified: true, confidenceScore: 70);

        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, tier);
        Assert.Contains("6 sold comps", note);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    public void GradeEvidence_UnderThreeComps_IsLowAndSaysWhy(int priced, int terapeak)
    {
        var (tier, note) = LocalArbitrageAnalyzer.GradeEvidence(priced, terapeak, identityVerified: true, confidenceScore: 90);

        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, tier);
        Assert.Contains("Estimate", note);
        Assert.Contains("too few", note);
    }

    [Fact]
    public void GradeEvidence_ThreeCompsIsTheFloor_NotTwo()
    {
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow,
            LocalArbitrageAnalyzer.GradeEvidence(2, 0, true, 90).Tier);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident,
            LocalArbitrageAnalyzer.GradeEvidence(LocalArbitrageAnalyzer.ThinCompCount, 0, true, 90).Tier);
    }

    [Fact]
    public void GradeEvidence_TerapeakMakesUpTheNumbers_CountsTowardsConfidence()
    {
        // Two hosted comps plus two Terapeak sales is four real sales of the same product. The
        // gate is about how much evidence there is, not which database it came out of.
        var (tier, _) = LocalArbitrageAnalyzer.GradeEvidence(2, 2, identityVerified: true, confidenceScore: 70);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, tier);
    }

    [Fact]
    public void GradeEvidence_IdentityUnverified_IsLowNoMatterHowManyComps()
    {
        // Twenty comps for a different product is worse evidence than two for this one, so the
        // count never rescues a mismatch.
        var (tier, note) = LocalArbitrageAnalyzer.GradeEvidence(
            pricedCompCount: 20, terapeakCompCount: 5, identityVerified: false, confidenceScore: 95);

        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, tier);
        Assert.Contains("model or part number", note);
    }

    [Fact]
    public void GradeEvidence_EnoughCompsButScatteredHistory_IsLow()
    {
        var (tier, note) = LocalArbitrageAnalyzer.GradeEvidence(8, 0, identityVerified: true, confidenceScore: 20);

        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, tier);
        Assert.Contains("scattered", note);
    }

    [Fact]
    public void GradeEvidence_NothingMatched_IsNone()
    {
        var (tier, _) = LocalArbitrageAnalyzer.GradeEvidence(0, 0, identityVerified: true, confidenceScore: 0);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceNone, tier);
    }

    [Fact]
    public void Build_OneLooseComp_KeepsTheMoneyButRefusesToCallItWorthIt()
    {
        // The reported bug, end to end: $60 ask, one comp at $554.
        var row = Analyzer.Build(Listing(60m), Pricing(expected: 554m, soldComps: 1, confidence: 90), Fees);

        // The arithmetic is untouched — the lead is still worth chasing by hand.
        Assert.Equal(420.20m, row.NetProfit);
        Assert.InRange(row.RoiPercent!.Value, 700m, 701m);
        // What changed is the claim attached to it.
        Assert.Equal("thin", row.Verdict);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, row.EvidenceTier);
        Assert.Contains("1 sold comp", row.EvidenceNote);
        Assert.Equal(1, row.PricedCompCount);
    }

    [Fact]
    public void Build_TwelveCompsFoundButOnePricedIt_IsJudgedOnTheOne()
    {
        // The quiet version of the same failure: the search returned twelve rows, the identity
        // guard and the outlier filter threw eleven away, and the row used to show "12 sold comps"
        // beside a percentage that rested on one of them.
        var row = Analyzer.Build(Listing(60m), Pricing(expected: 554m, soldComps: 12, pricedComps: 1, confidence: 90), Fees);

        Assert.Equal("thin", row.Verdict);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, row.EvidenceTier);
        // Both counts survive onto the row, so the UI can show the gap rather than the flattering half.
        Assert.Equal(12, row.SoldCompCount);
        Assert.Equal(1, row.PricedCompCount);
    }

    [Fact]
    public void Build_NoCompCarriesTheModelNumber_IsNeverWorthIt()
    {
        var row = Analyzer.Build(
            Listing(150m), Pricing(expected: 400m, soldComps: 20, confidence: 95, identityVerified: false), Fees);

        Assert.NotEqual("goldmine", row.Verdict);
        Assert.NotEqual("solid", row.Verdict);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, row.EvidenceTier);
        Assert.False(row.IdentityVerified);
        Assert.Contains("model or part number", row.VerdictNote);
    }

    [Fact]
    public void Build_EnoughMatchingComps_StillEarnsItsBadgeAndItsPercentages()
    {
        // The gate must not swallow the good rows: real evidence, real badge, undimmed ROI.
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 600m, soldComps: 9, confidence: 80), Fees);

        Assert.Equal("goldmine", row.Verdict);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, row.EvidenceTier);
        Assert.Equal(940.2m, row.RoiPercent);
        Assert.NotNull(row.MarginPercent);
    }

    [Fact]
    public void Judge_IdentityUnverified_IsThinWhateverTheNumbersSay()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(
            180m, 300m, 60m, compCount: 20, confidenceScore: 90, identityVerified: false);

        Assert.Equal("thin", verdict);
        Assert.Contains("model or part number", note);
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

    [Fact]
    public void Rank_Balanced_LiftsAnOtherwiseEqualLowFeedbackEbayOpportunity()
    {
        var established = Row("established", 100m, 50m);
        established.Source = EbaySupplySource.SourceId;
        established.SellerFeedbackScore = 5000;
        var overlooked = Row("overlooked", 100m, 50m);
        overlooked.Source = EbaySupplySource.SourceId;
        overlooked.SellerFeedbackScore = 3;

        var ranked = LocalArbitrageAnalyzer.Rank([established, overlooked], LocalArbitrageAnalyzer.SortByBalanced);

        Assert.Equal(["overlooked", "established"], ranked.Select(r => r.Title));
        Assert.Equal(1.20, LocalArbitrageAnalyzer.SellerOpportunityFactor(overlooked));
        Assert.Equal(1.0, LocalArbitrageAnalyzer.SellerOpportunityFactor(established));
    }

    [Fact]
    public void SellerOpportunityFactor_DoesNotRewardUnknownFeedbackOrNonEbayRows()
    {
        Assert.Equal(1.0, LocalArbitrageAnalyzer.SellerOpportunityFactor(Row("unknown", 100m, 50m)));
        var local = Row("local", 100m, 50m);
        local.SellerFeedbackScore = 0;
        Assert.Equal(1.0, LocalArbitrageAnalyzer.SellerOpportunityFactor(local));
    }

    // ── Ranking by what the board can stand behind ─────────────────────────────

    private static LocalArbitrageOpportunity JudgedRow(
        string title, decimal profit, string verdict, int? daysToCash = null) =>
        new()
        {
            Title = title, NetProfit = profit, Verdict = verdict, DaysToCash = daysToCash,
            ProfitPerDay = daysToCash is int d && d > 0 ? Math.Round(profit / d, 2) : null,
        };

    // The one that matters. Judge refuses to go above "thin" when no sold comp carries the item's
    // model number — so a $2,000 row can be arithmetic on a different product's price. Ranked on
    // money alone it led the board, and the top row is the one a seller drives across town for,
    // whatever the dimmed percentages beside it say.
    [Fact]
    public void Rank_ABiggerButUnsupportedNumber_DoesNotOutrankACompBackedDeal()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([
            JudgedRow("priced-off-another-product", 2000m, "thin"),
            JudgedRow("backed-by-comps", 140m, "solid"),
        ]);

        Assert.Equal(["backed-by-comps", "priced-off-another-product"], ranked.Select(r => r.Title));
    }

    // Demoted, never dropped. The estimate keeps its place on the board and its figures — the claim
    // is only that it isn't the first thing the seller reads.
    [Fact]
    public void Rank_TheDemotedEstimateIsStillOnTheBoard()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([
            JudgedRow("estimate", 2000m, "thin"), JudgedRow("goldmine", 300m, "goldmine"),
            JudgedRow("solid", 140m, "solid"),
        ]);

        Assert.Equal(["goldmine", "solid", "estimate"], ranked.Select(r => r.Title));
        Assert.Equal(3, ranked.Count);
    }

    // Within one band nothing changed: the money still decides, which is what keeps a $2,000
    // estimate above an $80 one instead of collapsing every unsupported row into one heap.
    [Fact]
    public void Rank_WithinTheSameVerdict_TheMoneyStillDecides()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([
            JudgedRow("small-estimate", 80m, "thin"), JudgedRow("big-estimate", 2000m, "thin"),
        ]);

        Assert.Equal(["big-estimate", "small-estimate"], ranked.Select(r => r.Title));
    }

    // Evidence gates every mode, not just the money one. A fast route to a number nothing supports
    // is not a fast flip, and "fastest cash" is where a seller with one pot of money shops.
    [Fact]
    public void Rank_VelocitySortsAreGatedOnTheVerdictToo()
    {
        var unsupported = JudgedRow("unsupported-but-quick", 900m, "thin", daysToCash: 10);
        var backed = JudgedRow("backed-but-slower", 200m, "solid", daysToCash: 45);

        foreach (var sort in new[] { LocalArbitrageAnalyzer.SortByFastestCash, LocalArbitrageAnalyzer.SortByProfitPerDay })
            Assert.Equal(["backed-but-slower", "unsupported-but-quick"],
                LocalArbitrageAnalyzer.Rank([unsupported, backed], sort).Select(r => r.Title));
    }

    // The verdict may reorder winners among themselves; it must never lift a loser or an unpriced
    // row over one that makes money. Those two keys run first and stay first.
    [Fact]
    public void Rank_TheVerdictNeverLiftsALoserOverAWinner()
    {
        var ranked = LocalArbitrageAnalyzer.Rank([
            JudgedRow("loss", -50m, "pass"),
            new LocalArbitrageOpportunity { Title = "unpriced", NetProfit = null, Verdict = "no_data" },
            JudgedRow("thin-win", 20m, "thin"),
        ]);

        Assert.Equal(["thin-win", "loss", "unpriced"], ranked.Select(r => r.Title));
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
    // The default is now Balanced (net dollars weighed against ROI), not raw money-first: pure ROI
    // floated a $4 flip over a deal that netted far more, and pure profit floated a big razor-margin
    // row — the money is the blend. Explicit "profit" still means profit.
    [InlineData(null, "balanced")]
    [InlineData("", "balanced")]
    [InlineData("nonsense", "balanced")]
    [InlineData("profit", "profit")]
    [InlineData("FASTEST", "fastest")]
    [InlineData(" perday ", "profit_per_day")]
    public void NormalizeSort_UnknownSortsFallBackToBalanced(string? input, string expected) =>
        Assert.Equal(expected, LocalArbitrageAnalyzer.NormalizeSort(input));

    // ── The buy side ───────────────────────────────────────────────────────────
    // Paying less is the cheapest margin there is — no fee, no shipping, no wait — so every priced
    // row carries the plan for asking. See NegotiationAdvisorTests for the advice itself.

    [Fact]
    public void Build_EveryPricedRowCarriesANegotiationPlanBuiltOnItsOwnBreakEven()
    {
        var row = Analyzer.Build(Listing(50m), Pricing(expected: 200m), Fees);

        Assert.NotNull(row.Negotiation);
        // The plan negotiates against the row's own max-buy price, not a second opinion of it.
        Assert.Equal(row.MaxBuyPrice, row.Negotiation!.BreakEvenPrice);
        Assert.Equal(row.LocalAsk, row.Negotiation.AskPrice);
        Assert.Equal(row.NetProfit, row.Negotiation.NetAtAsk);
    }

    [Fact]
    public void Build_AnUnpricedRowHasNothingToNegotiateAgainst()
    {
        var row = Analyzer.Build(Listing(50m), resale: null, Fees);

        // An offer with no sold history behind it is a guess with a dollar sign on it.
        Assert.Null(row.Negotiation);
    }

    [Fact]
    public void Build_ThePlanSeesTheSellersOwnPriceCutAndTheDistance()
    {
        var listing = Listing(180m, miles: 8);
        listing.OriginalPrice = 260m;

        var row = Analyzer.Build(listing, Pricing(expected: 400m, soldComps: 12), Fees);

        Assert.Contains(row.Negotiation!.Signals, s => s.Contains("$260"));
        Assert.Contains("8 miles away", row.Negotiation.Messages[0].Text);
    }

    [Fact]
    public void Build_AListingWithNoPublishedDateMakesNoStalenessClaim()
    {
        // Facebook doesn't publish a post date. A missing date has to mean "no argument made",
        // never "this listing is fresh".
        var row = Analyzer.Build(Listing(180m), Pricing(expected: 400m, soldComps: 12), Fees);

        Assert.DoesNotContain(row.Negotiation!.Signals, s => s.Contains("Listed"));
    }
}
