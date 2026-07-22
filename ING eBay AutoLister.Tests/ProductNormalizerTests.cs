using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class ProductNormalizerTests
{
    private static ProductNormalizer CreateNormalizer() => new(new ProductIdentityExtractor());

    [Fact]
    public void Normalize_LotOfTen_ExtractsQuantity()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("Lot of 10 Bitmain Antminer PSU Fans");

        Assert.Equal(10, product.Quantity);
    }

    [Fact]
    public void Normalize_LeadingParenCount_ExtractsQuantity()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("(5) Antminer Cooling Fans 12038");

        Assert.Equal(5, product.Quantity);
    }

    [Fact]
    public void Normalize_NoQuantityIndicator_DefaultsToOne()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("Bitmain Antminer S19j Pro 104TH Bitcoin Miner");

        Assert.Equal(1, product.Quantity);
    }

    [Fact]
    public void Normalize_PartsOnlyListing_IsFlaggedAsNegativeKeyword()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("Bitmain Antminer S19 Pro FOR PARTS not working");

        Assert.Contains("parts", product.NegativeKeywords);
        Assert.Contains("broken", product.NegativeKeywords);
    }

    [Fact]
    public void Normalize_EmptyBoxOrBoxOnly_IsFlaggedAsEmptyBoxKeyword()
    {
        var normalizer = CreateNormalizer();

        var boxOnly = normalizer.Normalize("RTX 4090 Founders Edition - box only");
        var emptyBox = normalizer.Normalize("RTX 4090 empty box no card included");

        Assert.Contains("empty box", boxOnly.NegativeKeywords);
        Assert.Contains("empty box", emptyBox.NegativeKeywords);
    }

    [Fact]
    public void Normalize_CaseListingWithNoModelOrPartNumber_IsFlaggedAsAccessoryListing()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("Leather Case for phone - brown");

        Assert.True(product.IsAccessoryListing);
    }

    [Fact]
    public void Normalize_MainProductThatMentionsAnAccessoryWord_IsNotFlaggedAsAccessoryListing()
    {
        var normalizer = CreateNormalizer();

        // Has a real model (extracted via ProductIdentityExtractor), so mentioning "case" in
        // passing shouldn't make this look like an accessory-only listing.
        var product = normalizer.Normalize("Bitmain Antminer S19j Pro 104TH with case and box");

        Assert.False(product.IsAccessoryListing);
    }

    [Fact]
    public void Normalize_NamedBrandAccessoryListing_IsFlaggedAsAccessoryListing()
    {
        var normalizer = CreateNormalizer();

        // Regression test: a real accessory listing that names its host product's brand right in
        // the title (very common — "iPad Screen Protector") used to slip through as NOT an
        // accessory, because the extractor correctly recognizes Brand="Apple" from "iPad" and the
        // old check required Brand to ALSO be missing. That let accessories get priced against
        // comparables for the full host product (a $57 screen protector "worth" $400+ like a real
        // iPad) instead of being hard-rejected by OpportunityScoringService as intended.
        var product = normalizer.Normalize("iPad Pro 11-inch Screen Protector - Anti-Glare, Easy Install Kit, HD Clear");

        Assert.True(product.IsAccessoryListing);
    }

    [Fact]
    public void Normalize_MergedCamelCaseWords_AreSplitBeforeExtraction()
    {
        var normalizer = CreateNormalizer();

        var product = normalizer.Normalize("Apple iPhone 15 ProMax 256GB");

        // "ProMax" -> "Pro Max" before extraction; whatever's left in Model shouldn't still
        // contain a squished "promax" token.
        Assert.DoesNotContain("promax", (product.Model ?? "").ToLowerInvariant());
    }
}
