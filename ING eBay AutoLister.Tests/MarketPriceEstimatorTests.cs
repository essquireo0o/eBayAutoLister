using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class MarketPriceEstimatorTests
{
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
}
