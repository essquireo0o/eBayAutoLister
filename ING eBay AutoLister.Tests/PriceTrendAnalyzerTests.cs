using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A price-trend tool is only worth anything if it refuses to print a trend it can't see. Most of
// these cases are about the refusals: a stale comps database, missing sold dates, a single busy
// week, a corpus that got busier on its own, and a cluster that quietly changed which product it
// was measuring. The rest pin the money rule — the trend is upside on a buy that already works at
// today's price, never the reason a buy works.
public class PriceTrendAnalyzerTests
{
    private static readonly DateTime Now = new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
    private const int Window = 45;

    private static MarketplaceComparableResult Comp(decimal price, int daysAgo, string title = "Bitmain Antminer S19j Pro", int quantity = 1) =>
        new()
        {
            ItemId = Guid.NewGuid().ToString(),
            Title = title,
            SoldPrice = price,
            TotalPrice = price,
            Quantity = quantity,
            SoldDate = Now.AddDays(-daysAgo),
        };

    private static MarketplaceComparableResult Undated(decimal price, string title = "Bitmain Antminer S19j Pro") =>
        new() { ItemId = Guid.NewGuid().ToString(), Title = title, SoldPrice = price, TotalPrice = price, Quantity = 1 };

    // n sales spread evenly through a window, all at the same price.
    private static IEnumerable<MarketplaceComparableResult> Sales(int count, decimal price, int fromDaysAgo, int toDaysAgo)
    {
        for (var i = 0; i < count; i++)
        {
            var span = Math.Max(1, toDaysAgo - fromDaysAgo);
            yield return Comp(price, fromDaysAgo + i * span / Math.Max(1, count));
        }
    }

    private static TrendCorpus ReadableCorpus(decimal? velocityChange = 0m) => new()
    {
        WindowDays = Window, IsReadable = true, TotalComps = 400, DatedComps = 400,
        DatedCoveragePercent = 100m, NewestCompAgeDays = 1,
        RecentComps = 200, PriorComps = 200, VelocityChangePercent = velocityChange,
    };

    // ── The corpus: the scan reading its own data before it reads any product ──

    [Fact]
    public void BuildCorpus_RefusesWhenTheNewestSaleIsOlderThanTheWindow()
    {
        // The collector stopped 60 days ago. Every product would show zero recent sales, and the
        // board would report a market-wide collapse that is really a gap in the data.
        var comps = Sales(40, 500m, 60, 120).ToList();

        var corpus = PriceTrendAnalyzer.BuildCorpus(comps, Now, Window);

        Assert.False(corpus.IsReadable);
        Assert.Contains("older than", corpus.Note);
        Assert.Equal(0, corpus.RecentComps);
    }

    [Fact]
    public void BuildCorpus_RefusesWhenAlmostNothingCarriesADate()
    {
        var comps = Enumerable.Range(0, 40).Select(_ => Undated(500m)).ToList();

        var corpus = PriceTrendAnalyzer.BuildCorpus(comps, Now, Window);

        Assert.False(corpus.IsReadable);
        Assert.Equal(0, corpus.DatedComps);
        Assert.Contains("no time series", corpus.Note);
    }

    [Fact]
    public void BuildCorpus_ReadsItsOwnVelocityDriftSoAProductCanBeMeasuredAgainstIt()
    {
        var comps = Sales(60, 500m, 0, Window).Concat(Sales(30, 500m, Window, Window * 2)).ToList();

        var corpus = PriceTrendAnalyzer.BuildCorpus(comps, Now, Window);

        Assert.True(corpus.IsReadable);
        Assert.Equal(60, corpus.RecentComps);
        Assert.Equal(30, corpus.PriorComps);
        Assert.Equal(100m, corpus.VelocityChangePercent);
        Assert.Contains("baseline", corpus.Note);
    }

    [Fact]
    public void BuildCorpus_IsReadableWhenTheDataIsFreshAndDated()
    {
        var comps = Sales(20, 500m, 0, Window).Concat(Sales(20, 500m, Window, Window * 2)).ToList();

        var corpus = PriceTrendAnalyzer.BuildCorpus(comps, Now, Window);

        Assert.True(corpus.IsReadable);
        Assert.Equal(100m, corpus.DatedCoveragePercent);
    }

    // ── Detrending: the database's own drift is not demand ─────────────────────

    [Fact]
    public void Detrend_RemovesTheCorpusBaseline()
    {
        // A product up 30% inside a scan that is itself up 30% has not moved at all.
        Assert.Equal(0m, PriceTrendAnalyzer.Detrend(30m, 30m));
    }

    [Fact]
    public void Detrend_LeavesTheRawFigureWhenThereIsNoBaseline()
    {
        Assert.Equal(45m, PriceTrendAnalyzer.Detrend(45m, null));
    }

    [Fact]
    public void Detrend_TurnsAGrowingProductInAFasterGrowingMarketNegative()
    {
        Assert.True(PriceTrendAnalyzer.Detrend(20m, 60m) < 0m);
    }

    [Fact]
    public void PercentChange_IsNullFromAZeroBaselineRatherThanInventingInfinity()
    {
        Assert.Null(PriceTrendAnalyzer.PercentChange(0m, 10m));
    }

    [Fact]
    public void Measure_DoesNotCallAProductRisingJustBecauseTheWholeDatabaseGotBusier()
    {
        // 8 → 16 sales looks like demand doubling, until you notice the whole scan doubled too.
        var comps = Sales(16, 500m, 0, Window).Concat(Sales(8, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus(velocityChange: 100m));

        Assert.Equal(100m, reading.VelocityChangePercent);
        Assert.Equal(0m, reading.RelativeVelocityChangePercent);
        Assert.False(reading.IsRising);
        Assert.Equal("steady", reading.Signal);
    }

    // ── The signals ────────────────────────────────────────────────────────────

    [Fact]
    public void Measure_FlagsRisingDemandWhenPriceAndVolumeBothClimb()
    {
        var comps = Sales(14, 600m, 0, Window).Concat(Sales(8, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("rising_demand", reading.Signal);
        Assert.Equal("confirmed", reading.Reliability);
        Assert.Equal(20m, reading.PriceChangePercent);
        Assert.Equal(100m, reading.PriceChangeAmount);
        Assert.True(reading.IsRising);
    }

    [Fact]
    public void Measure_SeparatesASupplySqueezeFromRealDemand()
    {
        // Dearer, but fewer changing hands: that is supply drying up, not buyers arriving, and it
        // is the hardest of these to actually buy into.
        var comps = Sales(5, 600m, 0, Window).Concat(Sales(14, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("supply_squeeze", reading.Signal);
        Assert.Contains("supply drying up", reading.Note);
    }

    [Fact]
    public void Measure_FlagsDemandBuildingWhenVolumeMovesBeforeThePriceDoes()
    {
        var comps = Sales(18, 505m, 0, Window).Concat(Sales(8, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("demand_building", reading.Signal);
        Assert.Contains("early half of a move", reading.Note);
    }

    [Fact]
    public void Measure_CallsAFallingPriceCoolingRatherThanHidingIt()
    {
        var comps = Sales(10, 400m, 0, Window).Concat(Sales(10, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("cooling", reading.Signal);
        Assert.False(reading.IsRising);
    }

    [Fact]
    public void Measure_TreatsSalesStoppingAsCoolingNotAsMissingData()
    {
        var comps = Sales(12, 500m, Window, Window * 2).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("cooling", reading.Signal);
        Assert.Equal("confirmed", reading.Reliability);
        Assert.Contains("none since", reading.Note);
    }

    [Fact]
    public void Measure_RefusesAProductWithNoEarlierWindowToRiseAbove()
    {
        // Everything sold in the last fortnight. There is no baseline, so there is no rise.
        var comps = Sales(12, 500m, 0, 14).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("unreadable", reading.Signal);
        Assert.Contains("nothing to compare", reading.Note);
    }

    // ── The refusals ───────────────────────────────────────────────────────────

    [Fact]
    public void Measure_RefusesWhenMostCompsCarryNoSoldDate()
    {
        // 6 dated rows inside 40 is a 15% sample — a trend read off that is noise with a % sign.
        var comps = Sales(3, 600m, 0, Window)
            .Concat(Sales(3, 500m, Window, Window * 2))
            .Concat(Enumerable.Range(0, 34).Select(_ => Undated(500m)))
            .ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("unreadable", reading.Signal);
        Assert.Contains("carry a date", reading.Note);
    }

    [Fact]
    public void Measure_RefusesEverythingWhenTheCorpusItselfIsUnreadable()
    {
        var comps = Sales(14, 600m, 0, Window).Concat(Sales(8, 500m, Window, Window * 2)).ToList();
        var stale = new TrendCorpus { IsReadable = false, Note = "The comps database stopped updating." };

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, stale);

        Assert.Equal("unreadable", reading.Signal);
        Assert.Equal("The comps database stopped updating.", reading.Note);
    }

    [Fact]
    public void Measure_DemotesAThinComparisonToTentative()
    {
        var comps = Sales(3, 600m, 0, Window).Concat(Sales(3, 500m, Window, Window * 2))
            .Concat(Sales(2, 550m, Window * 2, Window * 3)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("tentative", reading.Reliability);
        Assert.Contains("recent and", reading.Note);
    }

    [Fact]
    public void Measure_DemotesAWidelyDispersedClusterBecauseThatIsAMixShiftNotAPriceMove()
    {
        // The recent window quietly took in a much dearer variant. The median "rose"; the product
        // changed. Nothing here can tell those apart, so it must not claim to.
        var comps = new List<MarketplaceComparableResult>
        {
            Comp(200m, 5), Comp(240m, 10), Comp(900m, 15), Comp(1000m, 20), Comp(1100m, 25), Comp(950m, 30),
            Comp(300m, 50), Comp(310m, 55), Comp(320m, 60), Comp(305m, 65), Comp(315m, 70), Comp(300m, 80),
        };

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal("tentative", reading.Reliability);
        Assert.Contains("change of variant", reading.Note);
    }

    [Fact]
    public void Measure_PricesLotsPerUnitSoAFourPackIsNotAPriceSpike()
    {
        var comps = Sales(8, 500m, Window, Window * 2)
            .Concat(Enumerable.Range(0, 8).Select(i => Comp(2000m, i * 5, quantity: 4)))
            .ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal(500m, reading.Recent.MedianPrice);
        Assert.Equal(0m, reading.PriceChangePercent);
    }

    // ── The slope: a second opinion on the window split ────────────────────────

    [Fact]
    public void SlopePerMonth_IsPositiveWhenNewerSalesAreDearer()
    {
        var points = new List<(int, decimal)> { (0, 600m), (15, 550m), (30, 500m), (45, 450m) };

        var slope = PriceTrendAnalyzer.SlopePerMonth(points);

        Assert.NotNull(slope);
        Assert.True(slope > 0m);
        Assert.Equal(100m, slope);   // $50 per 15 days == $100 a month
    }

    [Fact]
    public void SlopePerMonth_IgnoresASingleWildComp()
    {
        // Least squares would be dragged by the $5,000 outlier; a median of pairwise slopes can't be.
        var clean = new List<(int, decimal)> { (0, 600m), (15, 550m), (30, 500m), (45, 450m) };
        var dirty = new List<(int, decimal)>(clean) { (20, 5000m) };

        Assert.Equal(PriceTrendAnalyzer.SlopePerMonth(clean), PriceTrendAnalyzer.SlopePerMonth(dirty));
    }

    [Fact]
    public void SlopePerMonth_IsNullWithoutEnoughPointsToDrawALine()
    {
        Assert.Null(PriceTrendAnalyzer.SlopePerMonth([(0, 600m), (10, 500m)]));
    }

    [Fact]
    public void Judge_DemotesARiseTheTrendLineDoesNotBack()
    {
        var reading = new PriceTrendReading
        {
            TotalCompCount = 20, DatedCompCount = 20, DatedCoveragePercent = 100m,
            Recent = new TrendWindow { SoldCount = 10, MedianPrice = 600m, LowPrice = 580m, HighPrice = 620m },
            Prior = new TrendWindow { SoldCount = 10, MedianPrice = 500m, LowPrice = 480m, HighPrice = 520m },
            PriceChangePercent = 20m,
            RelativeVelocityChangePercent = 0m,
            SlopePerMonth = -25m,
        };

        PriceTrendAnalyzer.Judge(reading, ReadableCorpus(), Window);

        Assert.Equal("price_climbing", reading.Signal);
        Assert.Equal("tentative", reading.Reliability);
        Assert.Contains("trend line", reading.Note);
    }

    // ── The projection ─────────────────────────────────────────────────────────

    [Fact]
    public void Measure_NeverProjectsAboveThePriceAnyoneActuallyPaid()
    {
        var comps = Sales(10, 600m, 0, Window).Concat(Sales(10, 400m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Equal(50m, reading.PriceChangePercent);
        // A naive extrapolation says $900. Nobody has paid more than $600, so $600 is the ceiling.
        Assert.Equal(600m, reading.ProjectedPrice);
    }

    [Fact]
    public void Measure_ProjectsNothingForAProductThatIsNotRising()
    {
        var comps = Sales(10, 400m, 0, Window).Concat(Sales(10, 500m, Window, Window * 2)).ToList();

        var reading = PriceTrendAnalyzer.Measure(comps, Now, Window, ReadableCorpus());

        Assert.Null(reading.ProjectedPrice);
        Assert.Equal(1m, PriceTrendAnalyzer.TrendMultiplier(reading));
    }

    [Fact]
    public void TrendMultiplier_IsCappedSoTheUpsideCannotRunAway()
    {
        var reading = new PriceTrendReading
        {
            Recent = new TrendWindow { MedianPrice = 100m },
            ProjectedPrice = 400m,
        };

        Assert.Equal(PriceTrendAnalyzer.MaxProjectionMultiple, PriceTrendAnalyzer.TrendMultiplier(reading));
    }

    // ── The weekly series ──────────────────────────────────────────────────────

    [Fact]
    public void WeeklySeries_KeepsTheWeeksWithNoSalesSoGapsStayVisible()
    {
        var series = PriceTrendAnalyzer.WeeklySeries([(2, 500m), (30, 400m)], 90);

        Assert.Equal(13, series.Count);
        Assert.Equal(12, series[0].WeeksAgo);      // oldest first, newest last
        Assert.Equal(0, series[^1].WeeksAgo);
        Assert.Equal(500m, series[^1].MedianPrice);
        Assert.Contains(series, p => p.SoldCount == 0);
    }

    // ── The money rule: the trend is upside, never the reason a buy works ──────

    [Fact]
    public void JudgeRow_RefusesToRecommendAnythingThatCannotClearItsOwnFees()
    {
        var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
            Rising(), compCount: 20, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: -5m, targetBuyPrice: 0m, trendHeadroom: 50m);

        Assert.Equal("pass", verdict);
        Assert.Contains("whichever way the price is moving", note);
    }

    [Fact]
    public void JudgeRow_WillNotCallAThinlyEvidencedRiseABuy()
    {
        // A 40% climb measured across three sold comps is still three sold comps.
        var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
            Rising(), compCount: 3, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 60m);

        Assert.Equal("thin", verdict);
        Assert.Contains("Only 3 sold comps", note);
    }

    [Fact]
    public void JudgeRow_DoesNotClaimThePriceIsMovingOnAThinlyEvidencedCoolingProduct()
    {
        // Found by a live scan: the thin branch is reached by cooling and unreadable rows too, and
        // "the price is moving" in front of a product whose sales stopped is exactly backwards.
        var cooling = Rising();
        cooling.Signal = "cooling";

        var (_, note) = PriceTrendAnalyzer.JudgeRow(
            cooling, compCount: 4, confidenceScore: 30, confidenceLevel: "Insufficient Evidence",
            maxBuyToday: 100m, targetBuyPrice: 30m, trendHeadroom: 0m);

        Assert.DoesNotContain("price is moving", note);
        Assert.Contains("either way", note);
    }

    [Fact]
    public void JudgeRow_WillNotCallALowConfidenceRiseABuyEither()
    {
        var (verdict, _) = PriceTrendAnalyzer.JudgeRow(
            Rising(), compCount: 30, confidenceScore: 20, confidenceLevel: "Low",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 60m);

        Assert.Equal("thin", verdict);
    }

    [Fact]
    public void JudgeRow_QuotesTheTargetPriceFromTodayAndTheHeadroomAsUpside()
    {
        var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
            Rising(), compCount: 20, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 60m);

        Assert.Equal("buy_now", verdict);
        Assert.Contains("$220", note);
        Assert.Contains("at today's price", note);
        Assert.Contains("$60 more", note);
    }

    [Fact]
    public void JudgeRow_HoldsATentativeRiseBackToWatch()
    {
        var tentative = Rising();
        tentative.Reliability = "tentative";

        var (verdict, _) = PriceTrendAnalyzer.JudgeRow(
            tentative, compCount: 20, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 60m);

        Assert.Equal("watch", verdict);
    }

    [Fact]
    public void JudgeRow_TellsAVolumeOnlyMoveApartFromAPriceMove()
    {
        var building = Rising();
        building.Signal = "demand_building";

        var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
            building, compCount: 20, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 0m);

        Assert.Equal("get_in_early", verdict);
        Assert.Contains("not worth chasing", note);
    }

    [Fact]
    public void JudgeRow_SaysOutrightThatASqueezeIsTheHardOneToBuyInto()
    {
        var squeeze = Rising();
        squeeze.Signal = "supply_squeeze";

        var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
            squeeze, compCount: 20, confidenceScore: 80, confidenceLevel: "Good",
            maxBuyToday: 400m, targetBuyPrice: 220m, trendHeadroom: 60m);

        Assert.Equal("watch", verdict);
        Assert.Contains("struggle to find", note);
    }

    // ── Ranking ────────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_PutsTheBestEvidenceAboveTheBiggestPercentage()
    {
        var thinButSpectacular = new TrendRadarRow
        {
            Product = "Thin", Verdict = "thin", TrendHeadroom = 900m, ProfitAtTarget = 900m,
            Trend = new PriceTrendReading { PriceChangePercent = 300m },
        };
        var modestButReal = new TrendRadarRow
        {
            Product = "Real", Verdict = "buy_now", TrendHeadroom = 40m, ProfitAtTarget = 90m,
            Trend = new PriceTrendReading { PriceChangePercent = 12m },
        };

        var ranked = PriceTrendAnalyzer.Rank([thinButSpectacular, modestButReal]);

        Assert.Equal("Real", ranked[0].Product);
    }

    [Fact]
    public void Rank_SortsBuyableRowsByTheMoneyOnThem()
    {
        var small = new TrendRadarRow { Product = "Small", Verdict = "buy_now", TrendHeadroom = 10m, ProfitAtTarget = 30m, Trend = new() };
        var big = new TrendRadarRow { Product = "Big", Verdict = "buy_now", TrendHeadroom = 120m, ProfitAtTarget = 200m, Trend = new() };

        var ranked = PriceTrendAnalyzer.Rank([small, big]);

        Assert.Equal("Big", ranked[0].Product);
    }

    private static PriceTrendReading Rising() => new()
    {
        Signal = "rising_demand", Reliability = "confirmed",
        PriceChangePercent = 20m,
        Recent = new TrendWindow { SoldCount = 10, MedianPrice = 600m },
        Prior = new TrendWindow { SoldCount = 8, MedianPrice = 500m },
    };
}
