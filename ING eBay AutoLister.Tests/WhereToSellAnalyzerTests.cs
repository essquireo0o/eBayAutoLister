using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// This feature can move a seller off the marketplace their whole workflow lives on, so the bar for
// saying "sell it there instead" is what these cases pin: the fee math per venue, the haircut that
// stops an asking price being read as a sale, the sample and materiality bars a challenger has to
// clear before it can win, and the rule that a venue with no data is reported as having no data
// rather than quietly losing.
public class WhereToSellAnalyzerTests
{
    private static WhereToSellAnalyzer Analyzer(CrossListingFeeProfile? cross = null) =>
        new(new ProfitCalculator(), cross ?? new CrossListingFeeProfile());

    // Default profile: 13.25% + $0.40 on eBay, no promoted rate, no shipping/packaging/labor.
    private static FeeProfile Fees() => new();

    private static ResalePricing Ebay(
        decimal? expected = 200m, int soldComps = 9, decimal avgShipping = 0m, int confidence = 72) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, AvgCompShipping = avgShipping,
            ConfidenceScore = confidence, ConfidenceLevel = "Good Confidence",
            EstimatedDaysToSell = 14, EstimatedMonthlySales = 2m,
        };

    private static LocalVenueEvidence Local(
        string venue = WhereToSellAnalyzer.Facebook, string status = "ok", params decimal[] prices) =>
        new()
        {
            Venue = venue, Label = venue == WhereToSellAnalyzer.Facebook ? "Facebook Marketplace" : "Craigslist",
            Status = status, Prices = [.. prices], RawResultCount = prices.Length,
            SearchUrl = "https://example.test/search",
        };

    private static VenueOutlook Venue(WhereToSellReport report, string venue) =>
        report.Venues.Single(v => v.Venue == venue);

    // ── The eBay baseline ──────────────────────────────────────────────────────

    [Fact]
    public void Ebay_ReportsWhatIsLeftAfterItsOwnFees()
    {
        var report = Analyzer().Build("antminer s19j pro", Ebay(expected: 200m), [], Fees(), unitCost: null);
        var ebay = Venue(report, WhereToSellAnalyzer.Ebay);

        // $200 sale -> 13.25% + $0.40 = $26.90, so $173.10 reaches the seller.
        Assert.Equal(200m, ebay.ExpectedPrice);
        Assert.Equal(26.90m, ebay.Fees);
        Assert.Equal(173.10m, ebay.NetProceeds);
        Assert.Equal(173.10m, report.EbayNet);
        Assert.Equal("sold", ebay.EvidenceKind);
    }

    [Fact]
    public void Ebay_BooksBuyerPaidShippingAsBothRevenueAndCost()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m, avgShipping: 15m), [], Fees(), unitCost: null);
        var ebay = Venue(report, WhereToSellAnalyzer.Ebay);

        // Gross is $215, eBay's cut is charged on all of it, and the label costs the same $15.
        Assert.Equal(215m, ebay.GrossRevenue);
        Assert.Equal(28.89m, ebay.Fees);          // 215 * 13.25% + 0.40
        Assert.Equal(15m, ebay.FulfilmentCost);
        Assert.Equal(171.11m, ebay.NetProceeds);
    }

    [Fact]
    public void Ebay_WithNoSoldHistoryIsReportedAsUnpricedRatherThanZero()
    {
        var report = Analyzer().Build("something nobody sells", null, [], Fees(), unitCost: null);
        var ebay = Venue(report, WhereToSellAnalyzer.Ebay);

        Assert.Null(ebay.NetProceeds);
        Assert.Null(report.EbayNet);
        Assert.False(ebay.Rankable);
        Assert.Equal("no_data", ebay.Verdict);
    }

    // ── Local venues: asks, haircut, and the sample bar ────────────────────────

    [Fact]
    public void LocalAsksAreMarkedDownBeforeTheyCompeteWithSoldPrices()
    {
        var report = Analyzer().Build("antminer", Ebay(), [Local(prices: [200m, 220m, 240m])], Fees(), null);
        var facebook = Venue(report, WhereToSellAnalyzer.Facebook);

        Assert.Equal("asking", facebook.EvidenceKind);
        Assert.Equal(220m, facebook.ObservedMedian);
        Assert.Equal(0.90m, facebook.RealizationFactor);
        Assert.Equal(198m, facebook.ExpectedPrice);   // 220 * 0.90
        Assert.Contains("asks, not sales", facebook.PriceBasis);
    }

    [Fact]
    public void LocalPickupTakesNoFeeNoLabelAndNoReturnsReserve()
    {
        var fees = Fees();
        fees.DefaultShippingCost = 12m;
        fees.DefaultPackagingCost = 2m;
        fees.DefaultLaborCost = 3m;
        fees.ReturnReservePercent = 4m;

        var report = Analyzer().Build("antminer", Ebay(), [Local(prices: [200m, 200m, 200m])], fees, null);
        var facebook = Venue(report, WhereToSellAnalyzer.Facebook);

        Assert.Equal(0m, facebook.Fees);
        Assert.Equal(3m, facebook.FulfilmentCost);    // the seller's own time survives; the box does not
        Assert.Equal(0m, facebook.Costs.Single(c => c.Key == "shipping").Amount);
        Assert.Equal(0m, facebook.Costs.Single(c => c.Key == "packaging").Amount);
        Assert.Equal(0m, facebook.Costs.Single(c => c.Key == "returns").Amount);
        Assert.Equal(177m, facebook.NetProceeds);     // 200 * 0.90 - 3
        Assert.Equal(0, facebook.CashPipelineDays);
    }

    [Fact]
    public void TwoHopefulListingsCanNeverBeatEbay()
    {
        // Both asks are wildly above the eBay price, and it still isn't evidence.
        var report = Analyzer().Build("antminer", Ebay(expected: 200m), [Local(prices: [400m, 420m])], Fees(), null);
        var facebook = Venue(report, WhereToSellAnalyzer.Facebook);

        Assert.False(facebook.Rankable);
        Assert.Equal("thin", facebook.Verdict);
        Assert.Equal(WhereToSellAnalyzer.Ebay, report.BestVenue);
        Assert.Equal("stay_on_ebay", report.Verdict);
        // The number is still shown — it is exactly the row a seller should go and check themselves.
        Assert.True(facebook.NetVsEbay > 0m);
    }

    // ── The recommendation ─────────────────────────────────────────────────────

    [Fact]
    public void RecommendsTheVenueThatHandsOverTheMostMoney()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m),
            [Local(prices: [210m, 220m, 230m, 240m])], Fees(), unitCost: null);

        // Local median $225 -> $202.50 realised, all of it kept, against $173.10 after eBay's fees.
        Assert.Equal("move", report.Verdict);
        Assert.Equal(WhereToSellAnalyzer.Facebook, report.BestVenue);
        Assert.Equal(202.50m, report.BestNet);
        Assert.Equal(29.40m, report.ExtraVsEbay);
        Assert.Equal("best", Venue(report, WhereToSellAnalyzer.Facebook).Verdict);
        Assert.Contains("$29.40 more", report.Subhead);
        // Winner first: the whole screen is "where should this go".
        Assert.Equal(WhereToSellAnalyzer.Facebook, report.Venues[0].Venue);
        Assert.Equal(1, report.Venues[0].Rank);
    }

    [Fact]
    public void StaysOnEbayWhenTheLocalAsksAreLower()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m),
            [Local(prices: [140m, 150m, 160m])], Fees(), unitCost: null);

        Assert.Equal("stay_on_ebay", report.Verdict);
        Assert.Equal(WhereToSellAnalyzer.Ebay, report.BestVenue);
        Assert.Equal(0m, report.ExtraVsEbay);
        Assert.Equal("lower", Venue(report, WhereToSellAnalyzer.Facebook).Verdict);
        Assert.Equal(-38.10m, Venue(report, WhereToSellAnalyzer.Facebook).NetVsEbay); // 135.00 - 173.10
    }

    [Fact]
    public void AnImmaterialEdgeIsNotAReasonToChangeMarketplace()
    {
        // Local nets $180 against eBay's $173.10 — $6.90 ahead, which clears the dollar bar but is
        // under 4% of the eBay take, well inside the error bars of an asking-price estimate.
        var report = Analyzer().Build("antminer", Ebay(expected: 200m),
            [Local(prices: [195m, 200m, 205m])], Fees(), unitCost: null);

        Assert.Equal(180m, Venue(report, WhereToSellAnalyzer.Facebook).NetProceeds);
        Assert.Equal("too_close", report.Verdict);
        Assert.Equal("close", Venue(report, WhereToSellAnalyzer.Facebook).Verdict);
        Assert.Contains("about the same", report.Headline);
    }

    [Theory]
    [InlineData(4.99, 100, false)]   // under the flat bar
    [InlineData(5.01, 100, true)]    // clears both
    [InlineData(9.00, 600, false)]   // clears the dollars, misses the 4%
    [InlineData(25.00, 600, true)]
    public void IsMaterial_RequiresBothBars(decimal gap, decimal ebayNet, bool expected) =>
        Assert.Equal(expected, WhereToSellAnalyzer.IsMaterial(gap, ebayNet));

    // ── Profit, once the seller says what they paid ────────────────────────────

    [Fact]
    public void WithoutACostBasisItReportsProceedsAndNotProfit()
    {
        var report = Analyzer().Build("antminer", Ebay(), [], Fees(), unitCost: null);
        var ebay = Venue(report, WhereToSellAnalyzer.Ebay);

        Assert.False(report.HasCostBasis);
        Assert.Equal(173.10m, ebay.NetProceeds);
        Assert.Null(ebay.NetProfit);
        Assert.Contains("Add what you paid", report.Subhead);
    }

    [Fact]
    public void WithACostBasisEveryVenueCarriesRealProfit()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m),
            [Local(prices: [210m, 220m, 230m])], Fees(), unitCost: 60m);

        Assert.True(report.HasCostBasis);
        Assert.Equal(113.10m, Venue(report, WhereToSellAnalyzer.Ebay).NetProfit);      // 173.10 - 60
        Assert.Equal(138m, Venue(report, WhereToSellAnalyzer.Facebook).NetProfit);     // 198.00 - 60
        Assert.Contains("$138.00 of profit", report.Subhead);
    }

    [Fact]
    public void ALossIsCalledALossRatherThanNegativeProfit()
    {
        // Paid $800 for something that sells for $200 anywhere. The best venue is still a loss.
        var report = Analyzer().Build("antminer", Ebay(expected: 200m), [], Fees(), unitCost: 800m);

        Assert.Equal(-626.90m, Venue(report, WhereToSellAnalyzer.Ebay).NetProfit);   // 173.10 - 800
        Assert.Contains("still loses $626.90", report.Subhead);
        Assert.DoesNotContain("of profit", report.Subhead);
    }

    // ── The price a venue has to fetch to beat eBay ────────────────────────────

    [Fact]
    public void MercariHasNoPriceDataButStillGetsAPriceToBeat()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m), [], Fees(), unitCost: null);
        var mercari = Venue(report, WhereToSellAnalyzer.Mercari);

        Assert.Null(mercari.ExpectedPrice);
        Assert.Equal("none", mercari.EvidenceKind);
        Assert.False(mercari.Rankable);
        Assert.Contains("cannot read Mercari prices", mercari.Note);
        // No seller fee there, so matching eBay's take-home needs exactly eBay's take-home.
        Assert.Equal(173.10m, mercari.PriceToBeatEbay);
    }

    [Fact]
    public void ThePriceToBeatEbayIsSolvedThroughTheVenuesOwnFees()
    {
        var cross = new CrossListingFeeProfile { MercariFeePercent = 10m };
        var report = Analyzer(cross).Build("antminer", Ebay(expected: 200m), [], Fees(), unitCost: null);
        var mercari = Venue(report, WhereToSellAnalyzer.Mercari);

        // 173.10 / 0.90 = 192.33, and 10% of that is 19.23 — leaving eBay's take-home to the cent.
        Assert.Equal(192.33m, mercari.PriceToBeatEbay);
        Assert.Equal(173.10m, Math.Round(192.33m * 0.90m, 2));
        Assert.Contains("match eBay", mercari.PriceToBeatEbayNote);
    }

    // ── Honesty ────────────────────────────────────────────────────────────────

    [Fact]
    public void SaysNothingCanBeComparedWhenNothingCanBeComparedAtAll()
    {
        var report = Analyzer().Build("unidentifiable thing", null, [], Fees(), unitCost: null);

        Assert.Equal("no_data", report.Verdict);
        Assert.Null(report.BestVenue);
        Assert.Null(report.ExtraVsEbay);
        Assert.Contains("Not enough evidence", report.Headline);
        Assert.Contains(report.Warnings, w => w.Contains("no comparison"));
    }

    [Fact]
    public void ADisconnectedSourceIsMissingFromTheComparisonNotLosingIt()
    {
        var report = Analyzer().Build("antminer", Ebay(),
            [Local(WhereToSellAnalyzer.Facebook, "not_connected")], Fees(), unitCost: null);
        var facebook = Venue(report, WhereToSellAnalyzer.Facebook);

        Assert.Equal("unavailable", facebook.Verdict);
        Assert.Null(facebook.NetProceeds);
        Assert.Contains("connect it in Settings", facebook.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(report.Warnings, w => w.Contains("missing from this comparison"));
    }

    [Fact]
    public void EveryRankedAskBasedFigureCarriesTheAskWarning()
    {
        var report = Analyzer().Build("antminer", Ebay(), [Local(prices: [210m, 220m, 230m])], Fees(), null);

        Assert.Contains(report.Warnings, w => w.Contains("ASKING"));
    }

    [Fact]
    public void ALocalVenueClaimsNoSpeedItCannotMeasure()
    {
        var report = Analyzer().Build("antminer", Ebay(), [Local(prices: [210m, 220m, 230m])], Fees(), null);
        var facebook = Venue(report, WhereToSellAnalyzer.Facebook);
        var ebay = Venue(report, WhereToSellAnalyzer.Ebay);

        Assert.Equal("unknown", facebook.SpeedTier);
        Assert.Null(facebook.DaysToCash);
        Assert.Contains("not something this data can measure", facebook.SpeedNote);
        // eBay does have dated sold history, so it keeps a real days-to-cash.
        Assert.Equal(14 + DaysToCashEstimator.PipelineDays, ebay.DaysToCash);
    }

    [Fact]
    public void AskingEvidenceNeverScoresAsHighlyAsSoldEvidenceCan()
    {
        var (score, level) = WhereToSellAnalyzer.ScoreAskingEvidence([200m, 205m, 210m, 215m, 220m, 225m, 230m, 235m]);

        Assert.InRange(score, 40, 64);
        Assert.Equal("Limited Confidence", level);
        Assert.Equal((0, "Insufficient Evidence"), WhereToSellAnalyzer.ScoreAskingEvidence([]));
    }

    [Fact]
    public void TwoLocalVenuesAreRankedAgainstEachOtherAndAgainstEbay()
    {
        var report = Analyzer().Build("antminer", Ebay(expected: 200m),
            [
                Local(WhereToSellAnalyzer.Facebook, "ok", 210m, 220m, 230m),
                Local(WhereToSellAnalyzer.Craigslist, "ok", 250m, 260m, 270m),
            ], Fees(), unitCost: null);

        Assert.Equal(WhereToSellAnalyzer.Craigslist, report.BestVenue);
        Assert.Equal(234m, report.BestNet);                                  // 260 * 0.90
        Assert.Equal("best", Venue(report, WhereToSellAnalyzer.Craigslist).Verdict);
        // Ahead of eBay, but not the recommendation — only one venue can be that.
        Assert.Equal("close", Venue(report, WhereToSellAnalyzer.Facebook).Verdict);
        Assert.Equal(1, Venue(report, WhereToSellAnalyzer.Craigslist).Rank);
        Assert.Equal(2, Venue(report, WhereToSellAnalyzer.Facebook).Rank);
        Assert.Equal(3, Venue(report, WhereToSellAnalyzer.Ebay).Rank);
    }
}
