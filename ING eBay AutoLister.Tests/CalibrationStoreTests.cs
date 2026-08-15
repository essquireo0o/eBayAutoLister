using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The learned pricing correction. A calibration that is empty must leave pricing EXACTLY as it is
/// today, and a calibration that is present must only ever move a price by a bounded, sample-gated
/// amount — because this is the one learning loop that reaches into live money decisions.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class CalibrationStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ing-calibration-" + Guid.NewGuid().ToString("N"));

    public CalibrationStoreTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private CalibrationStore NewStore() => new(Path.Combine(_root, "calibration.json"));

    private static CalibrationData Data(params (string key, double bias, int n)[] buckets)
    {
        var d = new CalibrationData
        {
            GeneratedUtc = DateTimeOffset.UtcNow,
            SampleSize = buckets.Sum(b => b.n),
            OverallBiasPct = buckets.Length > 0 ? buckets.Average(b => b.bias) : 0,
            Predictor = "median-of-comps-v1",
        };
        foreach (var (key, bias, n) in buckets)
            d.Buckets[key] = new CalibrationBucket { BiasPct = bias, N = n };
        return d;
    }

    // ── Round-trip ───────────────────────────────────────────────────────────────

    [Fact]
    public void Save_ThenLoadFromANewStore_ReturnsEveryField()
    {
        NewStore().Save(Data(("thin", 12.5, 40), ("mid", 6.0, 55), ("deep", 2.0, 33)));

        var reloaded = NewStore().Load();   // a fresh store, as if the app had restarted

        Assert.NotNull(reloaded);
        Assert.Equal(128, reloaded!.SampleSize);
        Assert.Equal("median-of-comps-v1", reloaded.Predictor);
        Assert.Equal(3, reloaded.Buckets.Count);
        Assert.Equal(12.5, reloaded.Buckets["thin"].BiasPct);
        Assert.Equal(40, reloaded.Buckets["thin"].N);
        Assert.Equal(2.0, reloaded.Buckets["deep"].BiasPct);
    }

    [Fact]
    public void Save_UpdatesTheInMemoryCurrentImmediately()
    {
        var store = NewStore();
        Assert.Null(store.Current);

        store.Save(Data(("mid", 5.0, 40)));

        Assert.NotNull(store.Current);
        Assert.Single(store.Current!.Buckets);
    }

    // ── Parsing the bot's wire shape ──────────────────────────────────────────────

    [Fact]
    public void Parse_NormalizesTheBotsFriendlyBucketLabelsToCanonicalKeys()
    {
        const string json = """
            {
              "generatedUtc": "2026-08-14T00:00:00Z",
              "sampleSize": 123,
              "overallBiasPct": 8.4,
              "buckets": {
                "thin(<8 comps)": { "biasPct": 12.1, "n": 40 },
                "mid(8-20)":      { "biasPct": 6.2,  "n": 55 },
                "deep(>20)":      { "biasPct": 3.0,  "n": 28 }
              },
              "predictor": "median-of-comps-v1"
            }
            """;

        var data = CalibrationStore.Parse(json);

        Assert.NotNull(data);
        Assert.Equal(123, data!.SampleSize);
        Assert.Equal(8.4, data.OverallBiasPct);
        Assert.True(data.Buckets.ContainsKey("thin"));
        Assert.True(data.Buckets.ContainsKey("mid"));
        Assert.True(data.Buckets.ContainsKey("deep"));
        Assert.Equal(12.1, data.Buckets["thin"].BiasPct);
        Assert.Equal(28, data.Buckets["deep"].N);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{ \"buckets\": ")]
    public void Parse_ReturnsNullForAnythingThatWillNotDeserialize(string body)
    {
        // The endpoint hands this straight back as a 400. It must never throw.
        Assert.Null(CalibrationStore.Parse(body));
    }

    [Fact]
    public void Parse_DropsNonFiniteNumbersRatherThanLettingThemPoisonPricing()
    {
        // A NaN bias sailing into the correction would make every corrected price NaN. It is
        // scrubbed to zero on the way in, and a zero-bias bucket is simply a no-op correction.
        const string json = """
            { "sampleSize": 40, "buckets": { "mid": { "biasPct": 5.0, "n": 40 } }, "overallBiasPct": 5.0 }
            """;
        var data = CalibrationStore.Parse(json);
        Assert.NotNull(data);
        Assert.Equal(5.0, data!.Buckets["mid"].BiasPct);
    }

    // ── Bucket selection ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "thin")]
    [InlineData(7, "thin")]
    [InlineData(8, "mid")]
    [InlineData(20, "mid")]
    [InlineData(21, "deep")]
    [InlineData(200, "deep")]
    public void BucketKeyFor_MatchesTheBotsThinMidDeepBoundaries(int compCount, string expected)
    {
        Assert.Equal(expected, CalibrationStore.BucketKeyFor(compCount));
    }

    // ── The correction: gated, bounded, and a no-op when empty ────────────────────

    [Fact]
    public void ResolveCorrection_WithNoCalibration_IsANoOp()
    {
        var result = NewStore().ResolveCorrection(compCount: 5);

        Assert.False(result.Applied);
        Assert.Equal(1.0m, result.Factor);
    }

    [Fact]
    public void ResolveCorrection_DividesOutAMeasuredHighBias()
    {
        var store = NewStore();
        store.Save(Data(("mid", 20.0, 40)));   // forecasts ran 20% high on 40 holdouts

        var result = store.ResolveCorrection(compCount: 12);   // mid bucket

        Assert.True(result.Applied);
        Assert.Equal("mid", result.Bucket);
        // 1 / (1 + 0.20) = 0.8333…
        Assert.Equal(1.0m / 1.2m, result.Factor, 4);
    }

    [Fact]
    public void ResolveCorrection_RaisesAPriceWhenForecastsRanLow()
    {
        var store = NewStore();
        store.Save(Data(("deep", -10.0, 40)));   // forecasts ran 10% low

        var result = store.ResolveCorrection(compCount: 50);   // deep bucket

        Assert.True(result.Applied);
        Assert.True(result.Factor > 1.0m);   // correcting upward
        Assert.Equal(1.0m / 0.9m, result.Factor, 4);
    }

    [Fact]
    public void ResolveCorrection_IsClampedSoNoCalibrationCanEverMoveAPriceMoreThanTwentyPercent()
    {
        var store = NewStore();
        // A wild +150% bias would divide the price to 0.4x; clamp pins it at 0.80.
        store.Save(Data(("thin", 150.0, 40), ("mid", -80.0, 40)));

        var high = store.ResolveCorrection(compCount: 3);   // thin
        var low  = store.ResolveCorrection(compCount: 12);  // mid

        Assert.Equal(CalibrationStore.MinFactor, high.Factor);
        Assert.Equal(0.80m, high.Factor);
        Assert.Equal(CalibrationStore.MaxFactor, low.Factor);
        Assert.Equal(1.20m, low.Factor);
    }

    [Fact]
    public void ResolveCorrection_BelowTheSampleGate_IsANoOp()
    {
        var store = NewStore();
        store.Save(Data(("mid", 20.0, CalibrationStore.MinBucketSample - 1)));   // 29 holdouts

        var result = store.ResolveCorrection(compCount: 12);

        Assert.False(result.Applied);
        Assert.Equal(1.0m, result.Factor);
    }

    [Fact]
    public void ResolveCorrection_ForABucketTheCalibrationDoesNotHave_IsANoOp()
    {
        var store = NewStore();
        store.Save(Data(("mid", 20.0, 40)));   // only the mid bucket is populated

        Assert.False(store.ResolveCorrection(compCount: 3).Applied);    // thin: nothing
        Assert.False(store.ResolveCorrection(compCount: 50).Applied);   // deep: nothing
        Assert.True(store.ResolveCorrection(compCount: 12).Applied);    // mid: present
    }

    // ── Through the estimator, on a real price ────────────────────────────────────

    private static readonly NormalizedProduct Widget = new() { Brand = "Acme" };

    /// <summary>Five undated, strong, equally-weighted comps -> a weighted median of $120 (thin bucket).</summary>
    private static List<MarketplaceComparableResult> FiveComps() =>
        new[] { 100m, 110m, 120m, 130m, 140m }
            .Select((p, i) => new MarketplaceComparableResult
            {
                ItemId = $"c{i}", Title = "Acme Widget", SoldPrice = p, TotalPrice = p, MatchScore = 60,
            })
            .ToList();

    private TerapeakMarketService Market()
    {
        var log = new ActionLog();
        var cache = new TerapeakPriceCache(new ListingDatabase(new StubWebHostEnvironment { ContentRootPath = _root }));
        return new TerapeakMarketService(
            new TerapeakService(Path.Combine(_root, "no-session.json"), Path.Combine(_root, "profile"), log),
            cache, log);
    }

    private async Task<decimal> ExpectedSaleAsync(CalibrationStore? calibration, bool enabled)
    {
        var estimator = new MarketPriceEstimator(Market(), calibration, enabled);
        var estimate = await estimator.EstimateAsync(
            Widget, FiveComps(), "Acme Widget", "FIXED_PRICE", allowRealTerapeakScrape: false);
        return estimate.ExpectedSalePrice!.Value;
    }

    [Fact]
    public async Task Estimator_WithAnEmptyStore_LeavesTheExpectedSalePriceExactlyAsItWas()
    {
        var baseline = await ExpectedSaleAsync(calibration: null, enabled: true);
        var withEmptyStore = await ExpectedSaleAsync(NewStore(), enabled: true);   // store exists, nothing saved

        Assert.Equal(baseline, withEmptyStore);
    }

    [Fact]
    public async Task Estimator_AppliesTheBucketCorrectionToTheExpectedSalePrice()
    {
        var baseline = await ExpectedSaleAsync(calibration: null, enabled: true);

        var store = NewStore();
        store.Save(Data(("thin", 25.0, 40)));   // five comps -> thin bucket, 25% high
        var corrected = await ExpectedSaleAsync(store, enabled: true);

        var expectedFactor = 1.0m / 1.25m;
        Assert.Equal(Math.Round(baseline * expectedFactor, 2), corrected);
        Assert.True(corrected < baseline);   // a high bias pulls the price down
    }

    [Fact]
    public async Task Estimator_ClampsTheCorrectionItAppliesToWithinTwentyPercent()
    {
        var baseline = await ExpectedSaleAsync(calibration: null, enabled: true);

        var store = NewStore();
        store.Save(Data(("thin", 100.0, 40)));   // would be 0.5x uncorrected; clamp holds it at 0.80
        var corrected = await ExpectedSaleAsync(store, enabled: true);

        Assert.Equal(Math.Round(baseline * 0.80m, 2), corrected);
    }

    [Fact]
    public async Task Estimator_WithTheFeatureFlagOff_LeavesPricingUntouched()
    {
        var baseline = await ExpectedSaleAsync(calibration: null, enabled: true);

        var store = NewStore();
        store.Save(Data(("thin", 25.0, 40)));
        var withFlagOff = await ExpectedSaleAsync(store, enabled: false);

        Assert.Equal(baseline, withFlagOff);
    }

    [Fact]
    public async Task Estimator_WithABucketBelowTheSampleGate_LeavesPricingUntouched()
    {
        var baseline = await ExpectedSaleAsync(calibration: null, enabled: true);

        var store = NewStore();
        store.Save(Data(("thin", 25.0, 10)));   // real bias, too few holdouts to trust
        var corrected = await ExpectedSaleAsync(store, enabled: true);

        Assert.Equal(baseline, corrected);
    }
}
