using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

[Collection(PooledSqliteTests.Name)]
public class MarketPriceEstimatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-estimator-" + Guid.NewGuid().ToString("N"));

    public MarketPriceEstimatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static MarketplaceComparableResult Comp(string itemId, string title, decimal price) =>
        new() { ItemId = itemId, Title = title, SoldPrice = price, TotalPrice = price, MatchScore = 60 };

    // ── Weight resolution ────────────────────────────────────────────────────────

    [Fact]
    public void ResolveWeights_NoLocalMedian_TrustsTerapeakEntirely()
    {
        var (local, terapeak) = MarketPriceEstimator.ResolveWeights(
            hasLocalMedian: false, localStrongCount: 0, terapeakStrongCount: 5, terapeakFreshnessWeight: 1.0);

        Assert.Equal(0m, local);
        Assert.Equal(1m, terapeak);
    }

    [Fact]
    public void ResolveWeights_NoTerapeakComps_TrustsLocalEntirely()
    {
        var (local, terapeak) = MarketPriceEstimator.ResolveWeights(
            hasLocalMedian: true, localStrongCount: 6, terapeakStrongCount: 0, terapeakFreshnessWeight: 1.0);

        Assert.Equal(1m, local);
        Assert.Equal(0m, terapeak);
    }

    [Fact]
    public void ResolveWeights_EqualEvidence_SplitsEvenly()
    {
        // Equal counts, no spread info -> pure sample-size ratio = 50/50 (no built-in bias).
        var (local, terapeak) = MarketPriceEstimator.ResolveWeights(true, 5, 5, 1.0);

        Assert.Equal(0.50m, terapeak, 2);
        Assert.Equal(0.50m, local, 2);
    }

    [Fact]
    public void ResolveWeights_MoreTerapeakComps_FavorTerapeakSmoothlyAndMonotonically()
    {
        var few  = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 5,  terapeakFreshnessWeight: 1.0);
        var some = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 10, terapeakFreshnessWeight: 1.0);
        var many = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 20, terapeakFreshnessWeight: 1.0);

        // Strictly increasing Terapeak weight as its comp count grows — no discontinuous jumps.
        Assert.True(some.TerapeakWeight > few.TerapeakWeight);
        Assert.True(many.TerapeakWeight > some.TerapeakWeight);
        Assert.Equal(0.667m, some.TerapeakWeight, 2); // 10 / (5 + 10)
    }

    [Fact]
    public void ResolveWeights_ThinTerapeakStrongLocal_FavorsLocal()
    {
        var (local, terapeak) = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 1, terapeakFreshnessWeight: 1.0);

        Assert.True(local > terapeak);
        Assert.Equal(0.167m, terapeak, 2); // 1 / (5 + 1)
    }

    [Fact]
    public void ResolveWeights_WiderSpreadLowersThatSourcesWeight()
    {
        // Same counts; give Terapeak a much wider spread relative to its median -> less reliable.
        var tight = MarketPriceEstimator.ResolveWeights(true, 5, 5, 1.0,
            localMedian: 100m, localSpread: 10m, terapeakMedian: 100m, terapeakSpread: 10m);
        var wide  = MarketPriceEstimator.ResolveWeights(true, 5, 5, 1.0,
            localMedian: 100m, localSpread: 10m, terapeakMedian: 100m, terapeakSpread: 120m);

        Assert.Equal(0.50m, tight.TerapeakWeight, 2);       // equal spread -> even split
        Assert.True(wide.TerapeakWeight < tight.TerapeakWeight); // noisy Terapeak counts for less
    }

    [Fact]
    public void ResolveWeights_StaleTerapeakData_LosesShareOfTheBlendToLocal()
    {
        var fresh = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 10, terapeakFreshnessWeight: 1.0);
        var stale = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 5, terapeakStrongCount: 10, terapeakFreshnessWeight: 0.2);

        Assert.True(stale.TerapeakWeight < fresh.TerapeakWeight);
        Assert.True(stale.LocalWeight   > fresh.LocalWeight);
    }

    [Fact]
    public void ResolveWeights_ExtremeMismatch_NeverFullyErasesEitherRealSource()
    {
        var (local, terapeak) = MarketPriceEstimator.ResolveWeights(true, localStrongCount: 100, terapeakStrongCount: 1, terapeakFreshnessWeight: 1.0);

        // Clamped so a real second source keeps at least a 15% voice.
        Assert.Equal(0.15m, terapeak, 2);
        Assert.Equal(0.85m, local, 2);
    }

    // ── Disagreement detection (unchanged behavior) ──────────────────────────────

    [Fact]
    public void DetectDisagreement_MediansWithinTwentyPercent_NoDisagreementFlagged()
    {
        var (disagree, message) = MarketPriceEstimator.DetectDisagreement(localMedian: 100m, terapeakMedian: 115m);

        Assert.False(disagree);
        Assert.Null(message);
    }

    [Fact]
    public void DetectDisagreement_MediansMoreThanTwentyPercentApart_FlagsDisagreementWithBothValues()
    {
        var (disagree, message) = MarketPriceEstimator.DetectDisagreement(localMedian: 100m, terapeakMedian: 160m);

        Assert.True(disagree);
        Assert.Contains("100.00", message);
        Assert.Contains("160.00", message);
    }

    [Fact]
    public void DetectDisagreement_OneSourceHasNoData_NeverFlagsDisagreement()
    {
        var (disagree, _) = MarketPriceEstimator.DetectDisagreement(localMedian: 0m, terapeakMedian: 160m);

        Assert.False(disagree);
    }

    // ── Identity guard ───────────────────────────────────────────────────────────
    // The failure this guards against: a cheap part gets priced off comps for a different,
    // far pricier product from the same brand, because the two only share a brand token.

    [Fact]
    public void ApplyIdentityGuard_PriceyCompsSharingOnlyTheBrand_AreDroppedFromThePricingSet()
    {
        var target = new NormalizedProduct { Brand = "FANUC", Model = "GP50" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "FANUC GP50 Fuse 5A for CNC Drive - Genuine", 74.00m),
            Comp("2", "FANUC GP50 Fuse Lot Tested Working", 68.00m),
            Comp("3", "Genuine FANUC GP50 5A Fuse New Old Stock", 82.00m),
            Comp("4", "FANUC A06B-6079-H206 Servo Drive Amplifier Tested", 1050.00m),
            Comp("5", "FANUC A06B-6079-H206 Servo Amplifier Module", 995.00m),
            Comp("6", "FANUC Servo Drive Unit Fully Tested Working", 1200.00m),
        };

        var kept = MarketPriceEstimator.ApplyIdentityGuard(target, comps);

        Assert.Equal(3, kept.Count);
        Assert.All(kept, c => Assert.Contains("GP50", c.Title));
        // The $1050 drives would have dragged the median from ~$74 to ~$500+.
        Assert.True(kept.Max(c => c.SoldPrice) < 100m);
    }

    [Fact]
    public void ApplyIdentityGuard_PartNumberPunctuatedDifferentlyInTitles_StillMatches()
    {
        // Target part number is hyphenated; the comp titles space it out. Both sides are
        // normalized to letters/digits before comparison, so they still line up.
        var target = new NormalizedProduct { Brand = "Bitmain", PartNumber = "APW7-12-1800" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "Bitmain APW7 12 1800 PSU Power Supply", 65.00m),
            Comp("2", "New Bitmain APW7-12-1800 Antminer PSU", 70.00m),
            Comp("3", "Bitmain (APW7) 12 1800 Power Supply Unit", 62.00m),
            Comp("4", "Bitmain Antminer S19j Pro 104TH Bitcoin Miner", 950.00m),
        };

        var kept = MarketPriceEstimator.ApplyIdentityGuard(target, comps);

        Assert.Equal(3, kept.Count);
        Assert.DoesNotContain(kept, c => c.ItemId == "4");
    }

    [Fact]
    public void ApplyIdentityGuard_OnlyTwoCompsCarryTheIdentifier_StillPricesOffJustThoseTwo()
    {
        // Two real sales of THIS part price it. Twenty sales of the $1050 drive beside it do not,
        // however much better twenty looks than two — so the guard narrows rather than stepping
        // aside, and the thinness of what survives is reported for the caller to gate on.
        var target = new NormalizedProduct { Brand = "FANUC", Model = "GP50" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "FANUC GP50 Fuse 5A for CNC Drive", 74.00m),
            Comp("2", "FANUC GP50 Fuse Lot Tested Working", 68.00m),
            Comp("3", "FANUC A06B-6079-H206 Servo Drive Amplifier", 1050.00m),
            Comp("4", "FANUC Servo Drive Unit Fully Tested Working", 1200.00m),
        };

        var guard = MarketPriceEstimator.GuardIdentity(target, comps);

        Assert.Equal(2, guard.Comps.Count);
        Assert.All(guard.Comps, c => Assert.Contains("GP50", c.Title));
        // Thin, but this product's — which is what the confidence gate needs to hear.
        Assert.True(guard.Verified);
        Assert.Equal(2, guard.MatchedCount);
    }

    [Fact]
    public void GuardIdentity_NoCompCarriesTheIdentifier_PricesAnywayButReportsItUnverified()
    {
        // Nothing matched. The set is handed back whole so the row still gets a figure — an
        // unpriced row helps nobody — but the estimate is flagged as not this product's, which is
        // what stops it being published as a real ROI.
        var target = new NormalizedProduct { Brand = "Toyota", Model = "13575" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "Toyota Tacoma Tow Hitch Receiver OEM", 554.00m),
            Comp("2", "Toyota Tundra Trailer Hitch Assembly", 480.00m),
        };

        var guard = MarketPriceEstimator.GuardIdentity(target, comps);

        Assert.Equal(2, guard.Comps.Count);
        Assert.False(guard.Verified);
        Assert.Equal(0, guard.MatchedCount);
    }

    [Fact]
    public void GuardIdentity_TargetHasNoIdentifier_ReportsVerifiedBecauseThereIsNothingToCheck()
    {
        // No model, no part number: the guard has nothing to say, and silence is not a warning.
        // Rows like this are held to account by the comp COUNT instead — see GradeEvidence.
        var target = new NormalizedProduct { Brand = "Toyota" };
        var comps = new List<MarketplaceComparableResult> { Comp("1", "Toyota Trailer Hitch", 554.00m) };

        var guard = MarketPriceEstimator.GuardIdentity(target, comps);

        Assert.True(guard.Verified);
        Assert.Equal(0, guard.TokenCount);
    }

    [Fact]
    public void ApplyIdentityGuard_TargetHasNoModelOrPartNumber_IsANoOp()
    {
        var target = new NormalizedProduct { Brand = "FANUC", Category = "Industrial Automation" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "FANUC GP50 Fuse 5A for CNC Drive", 74.00m),
            Comp("2", "FANUC A06B-6079-H206 Servo Drive Amplifier", 1050.00m),
            Comp("3", "FANUC Servo Drive Unit Fully Tested Working", 1200.00m),
        };

        var kept = MarketPriceEstimator.ApplyIdentityGuard(target, comps);

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void ApplyIdentityGuard_ModelIsOnlyGenericShortWords_IsANoOp()
    {
        // "Pro" is too short and carries no digit — filtering on a word like that would throw away
        // good comps for no identity signal, so the guard declines to act.
        var target = new NormalizedProduct { Brand = "Apple", Model = "Pro" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "Apple MacBook Air M2 13-inch", 700.00m),
            Comp("2", "Apple iPad Pro 11-inch 128GB", 450.00m),
            Comp("3", "Apple Watch Series 8 45mm", 220.00m),
        };

        var kept = MarketPriceEstimator.ApplyIdentityGuard(target, comps);

        Assert.Equal(3, kept.Count);
    }

    [Fact]
    public void ApplyIdentityGuard_EveryCompCarriesTheIdentifier_KeepsThemAll()
    {
        var target = new NormalizedProduct { Brand = "Bitmain", Model = "S19j Pro" };
        var comps = new List<MarketplaceComparableResult>
        {
            Comp("1", "Bitmain Antminer S19j Pro 104TH Bitcoin Miner", 950.00m),
            Comp("2", "Antminer S19J Pro 100TH Tested Working", 900.00m),
            Comp("3", "Bitmain Antminer S19j Pro 96TH ASIC Miner", 880.00m),
        };

        var kept = MarketPriceEstimator.ApplyIdentityGuard(target, comps);

        Assert.Equal(3, kept.Count);
    }

    // ── The second opinion, through the cache ────────────────────────────────────
    // Every scan answers from the Terapeak cache far more often than it scrapes — that is the whole
    // point of the cache, and it is the path nearly every priced row takes. So what a cached price
    // does to the estimate is not an edge case; it is the normal case.

    private TerapeakMarketService Market(out TerapeakPriceCache cache)
    {
        var log = new ActionLog();
        cache = new TerapeakPriceCache(new ListingDatabase(new StubWebHostEnvironment { ContentRootPath = _root }));
        return new TerapeakMarketService(
            new TerapeakService(Path.Combine(_root, "no-session.json"), Path.Combine(_root, "profile"), log),
            cache, log);
    }

    private static readonly NormalizedProduct Miner = new() { Brand = "Bitmain", Model = "S19j Pro" };

    /// <summary>Five local sales either side of $410, the two dearest being the closest matches.</summary>
    private static List<MarketplaceComparableResult> LocalComps() =>
        new[] { (380m, 60), (400m, 60), (410m, 60), (425m, 95), (440m, 95) }
            .Select((c, i) => new MarketplaceComparableResult
            {
                ItemId = $"c{i}", Title = "Bitmain Antminer S19j Pro 104TH", SoldPrice = c.Item1,
                TotalPrice = c.Item1, MatchScore = c.Item2, SoldDate = DateTime.UtcNow.AddDays(-5 - (i * 10)),
            })
            .ToList();

    /// <summary>
    /// A cached Terapeak price is a second opinion again.
    ///
    /// It was not. The cache stored the median and nothing about the sample behind it, so a served
    /// price arrived with a count of zero — and the blend gives a source with no sample size a
    /// weight of zero. The figure was looked up, computed, carried the length of the pipeline and
    /// then multiplied by nothing, on nearly every row, while the panel beside it said the price
    /// rested on one source. Both halves of that were true and neither was intended.
    /// </summary>
    [Fact]
    public async Task A_cached_terapeak_price_counts_towards_the_price_instead_of_being_multiplied_by_zero()
    {
        var market = Market(out var cache);
        cache.Set(TerapeakMarketService.BuildCacheKey(Miner), 300m, 300m, 20m, 65m,
            compCount: 12, min: 260m, max: 340m);

        var estimate = await new MarketPriceEstimator(market).EstimateAsync(
            Miner, LocalComps(), "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE",
            allowRealTerapeakScrape: false);

        Assert.True(estimate.TerapeakWeight > 0m, "a cached price with a known sample size has to carry weight");
        Assert.Equal(12, estimate.TerapeakComparableCount);
        Assert.Equal(300m, estimate.TerapeakMedianPrice);
        // Terapeak says $300 and the local comps say more, so the blend has to land between them —
        // the arithmetic proof that the second opinion actually moved the number.
        Assert.True(estimate.ExpectedSalePrice < estimate.LocalExpectedSalePrice,
            $"expected the blend to be pulled below the local {estimate.LocalExpectedSalePrice}, got {estimate.ExpectedSalePrice}");
        Assert.True(estimate.ExpectedSalePrice > 300m);
        // And it reconciles, which is the promise PriceBasis makes about these same two figures.
        Assert.Equal(
            estimate.LocalExpectedSalePrice!.Value * estimate.LocalWeight + 300m * estimate.TerapeakWeight,
            estimate.ExpectedSalePrice!.Value, 2);
    }

    /// <summary>
    /// The rows already in the seller's database were written before the count was stored. They are
    /// still left out — a price with an unknown sample size cannot be weighed against one with a
    /// known one, and assuming a size would put arbitrary weight on it. They heal on the next scrape.
    /// </summary>
    [Fact]
    public async Task A_cached_price_with_no_recorded_sample_size_is_still_left_out()
    {
        var market = Market(out var cache);
        cache.Set(TerapeakMarketService.BuildCacheKey(Miner), 300m, 300m, 20m, 65m);

        var estimate = await new MarketPriceEstimator(market).EstimateAsync(
            Miner, LocalComps(), "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE",
            allowRealTerapeakScrape: false);

        Assert.Equal(0m, estimate.TerapeakWeight);
        Assert.Equal(1m, estimate.LocalWeight);
    }

    /// <summary>
    /// And while it is left out, it must not move anything either.
    ///
    /// It did. A Terapeak median with zero weight still ran the whole blend, and the last thing the
    /// blend does is re-derive the recommended listing price at a flat 5% over the expected sale —
    /// overwriting the competition-scaled buffer the estimator had just worked out (8% when almost
    /// nobody else is listing this, 2% when the market is crowded). A source contributing nothing to
    /// the price was silently repricing what to list it at.
    /// </summary>
    [Fact]
    public async Task A_source_with_no_weight_does_not_reprice_what_to_list_at()
    {
        var market = Market(out var cache);
        cache.Set(TerapeakMarketService.BuildCacheKey(Miner), 300m, 300m, 20m, 65m); // no sample size

        var estimate = await new MarketPriceEstimator(market).EstimateAsync(
            Miner, LocalComps(), "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE",
            allowRealTerapeakScrape: false, activeCompetitionCount: 1);

        // One competing listing earns the wide 8% buffer, and nothing that carried no weight in the
        // price gets to trim it back to 5%.
        Assert.Equal(Math.Round(estimate.ExpectedSalePrice!.Value * 1.08m, 2), estimate.RecommendedListingPrice);
    }

    /// <summary>
    /// When each source saw the market, recorded on the estimate. Both weights above are already
    /// age-adjusted, so a reader given the weights and not the dates is being shown an adjustment
    /// with its reason left off screen — see <see cref="PriceBasis"/>.
    /// </summary>
    [Fact]
    public async Task The_estimate_records_when_each_source_last_saw_the_market()
    {
        var market = Market(out var cache);
        cache.Set(TerapeakMarketService.BuildCacheKey(Miner), 300m, 300m, 20m, 65m, compCount: 12);

        var estimate = await new MarketPriceEstimator(market).EstimateAsync(
            Miner, LocalComps(), "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE",
            allowRealTerapeakScrape: false);

        // The local comps span 5 to 45 days back, and the window says so.
        Assert.NotNull(estimate.LocalNewestSoldAtUtc);
        Assert.NotNull(estimate.LocalOldestSoldAtUtc);
        Assert.InRange((DateTime.UtcNow - estimate.LocalNewestSoldAtUtc!.Value).TotalDays, 4.9, 5.1);
        Assert.InRange((DateTime.UtcNow - estimate.LocalOldestSoldAtUtc!.Value).TotalDays, 44.9, 45.1);
        // Terapeak's figure is dated when it was scraped, which for a cache hit is not now.
        Assert.NotNull(estimate.TerapeakScrapedAtUtc);
        Assert.Equal(1.0, estimate.TerapeakFreshnessWeight);
    }

    /// <summary>
    /// Comps with no sale date leave the window empty rather than filling it with today. An undated
    /// comp is undated; a window invented for it would read as evidence of recency.
    /// </summary>
    [Fact]
    public async Task Undated_comps_leave_the_sold_window_empty_rather_than_inventing_one()
    {
        var estimate = await new MarketPriceEstimator(Market(out _)).EstimateAsync(
            Miner,
            [Comp("1", "Bitmain Antminer S19j Pro 104TH", 400m), Comp("2", "Bitmain Antminer S19j Pro 104TH", 420m)],
            "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE", allowRealTerapeakScrape: false);

        Assert.Null(estimate.LocalOldestSoldAtUtc);
        Assert.Null(estimate.LocalNewestSoldAtUtc);
    }
}
