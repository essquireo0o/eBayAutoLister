using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class PackageEstimatorTests
{
    private static readonly PackageEstimator Estimator = new();

    [Fact]
    public void Estimate_MeasuredInputWinsOutright()
    {
        var package = Estimator.Estimate("Antminer S19 Pro 110TH", weightOz: 40m,
            lengthIn: 6m, widthIn: 5m, heightIn: 4m);

        Assert.Equal("measured", package.Source);
        Assert.Equal(40m, package.WeightOz);
        Assert.Equal(6m, package.LengthIn);
        Assert.Equal("Your measurements.", package.Basis);
    }

    [Fact]
    public void Estimate_TitleAloneProducesAnEstimateFlaggedAsOne()
    {
        var package = Estimator.Estimate("Dell Latitude 7420 laptop i7 16GB");

        Assert.Equal("estimated", package.Source);
        Assert.Equal("laptop", package.Profile);
        Assert.True(package.WeightOz > 0);
        Assert.Contains("laptop", package.Basis);
    }

    // The single most specific keyword has to win, or "gaming pc" prices as a "cpu" because
    // "core i9" appeared later in the same title.
    [Fact]
    public void Estimate_PrefersTheLongestMatchingKeyword()
    {
        var specific = Estimator.Estimate("Sealed booster box trading card lot");
        Assert.Equal("trading card", specific.Profile);

        var console = Estimator.Estimate("Nintendo Switch OLED console bundle");
        Assert.Equal("game console", console.Profile);
    }

    [Fact]
    public void Estimate_UnknownItemIsFlaggedAsFallbackNotAsAnEstimate()
    {
        var package = Estimator.Estimate("Assorted mystery items from an estate");

        Assert.Equal("fallback", package.Source);
        Assert.Contains("small parcel", package.Basis);
    }

    // The whole engine exists because optimistic shipping assumptions poison every profit number
    // upstream. When the app does not know what an item is, it must not guess "envelope".
    [Fact]
    public void Estimate_UnknownItemGuessesUpwardNotDownward()
    {
        var package = Estimator.Estimate("");
        Assert.True(package.WeightOz >= 16m,
            $"An unknown item must not be priced as a light envelope; got {package.WeightOz} oz.");
    }

    [Fact]
    public void Estimate_PartialInputFillsOnlyTheMissingHalf()
    {
        var weighedOnly = Estimator.Estimate("Canon EF 70-200mm lens", weightOz: 50m);
        Assert.Equal("estimated", weighedOnly.Source);
        Assert.Equal(50m, weighedOnly.WeightOz);
        Assert.True(weighedOnly.HasDimensions);
        Assert.Contains("Your weight", weighedOnly.Basis);

        var measuredOnly = Estimator.Estimate("Canon EF 70-200mm lens", lengthIn: 9m, widthIn: 7m, heightIn: 7m);
        Assert.Equal("estimated", measuredOnly.Source);
        Assert.Equal(9m, measuredOnly.LengthIn);
        Assert.True(measuredOnly.WeightOz > 0);
        Assert.Contains("Your dimensions", measuredOnly.Basis);
    }

    [Fact]
    public void Estimate_CategoryCountsAsEvidenceWhenTheTitleIsUseless()
    {
        var package = Estimator.Estimate("Lot #4 — see photos", category: "Men's Shoes");
        Assert.Equal("shoes", package.Profile);
    }

    [Fact]
    public void EstimateFromListing_CombinesPoundsAndOunces()
    {
        var package = Estimator.EstimateFromListing(new ListingData
        {
            Title = "Vintage receiver",
            WeightLbs = 12m,
            WeightOz = 4m,
            PackageLengthIn = 22m,
            PackageWidthIn = 18m,
            PackageHeightIn = 12m,
        });

        Assert.Equal("measured", package.Source);
        Assert.Equal(196m, package.WeightOz);   // 12 lb 4 oz
    }

    [Fact]
    public void LengthPlusGirth_UsesTheLongestSideAsTheLength()
    {
        // 20 + 2*(10 + 5) = 50, regardless of which field holds which number.
        var package = new PackageSpec { LengthIn = 10m, WidthIn = 20m, HeightIn = 5m };
        Assert.Equal(50m, package.LengthPlusGirthIn);
        Assert.Equal(20m, package.LongestSideIn);
    }

    [Fact]
    public void AsicMiners_AreNeverEstimatedAsAnOrdinaryParcel()
    {
        // This app's sourcing boards surface miners constantly. A packed S19 is somewhere around
        // 40 lb — inside what USPS will carry, but nowhere near the fallback parcel, and mistaking
        // one for the other understates the label by tens of dollars on the app's own deal boards.
        var package = Estimator.Estimate("Bitmain Antminer S19j Pro 104TH miner");

        Assert.Equal("asic miner", package.Profile);
        Assert.True(package.WeightLb >= 30m,
            $"An ASIC miner must estimate as a heavy box; got {package.WeightLb} lb.");
    }
}
