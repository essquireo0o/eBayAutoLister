using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A Marketplace tile carries no field labels — just a href, a thumbnail and a handful of
// short unordered strings — so every one of these cases is a shape the real grid produces.
public class FacebookMarketplaceParserTests
{
    private static FacebookRawCard Card(string href, params string[] lines) =>
        new() { Href = href, ImageUrl = "https://scontent.example/x.jpg", Lines = [.. lines] };

    private const string Href = "https://www.facebook.com/marketplace/item/1234567890/";

    // ── ParseCard ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParseCard_TypicalTile_ReadsPriceTitleAndLocation()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$1,200", "Bitmain Antminer S19j Pro 104TH", "Las Vegas, NV"));

        Assert.NotNull(listing);
        Assert.Equal("1234567890", listing!.ItemId);
        Assert.Equal(1200m, listing.Price);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", listing.Title);
        Assert.Equal("Las Vegas, NV", listing.Location);
        Assert.False(listing.IsFree);
        Assert.Null(listing.OriginalPrice);
    }

    [Fact]
    public void ParseCard_NormalizesUrlAndKeepsImage()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card("https://www.facebook.com/marketplace/item/999/?ref=search", "$40", "Dyson V11", "Henderson, NV"));

        Assert.Equal("https://www.facebook.com/marketplace/item/999/", listing!.Url);
        Assert.Equal("https://scontent.example/x.jpg", listing.ImageUrl);
    }

    [Fact]
    public void ParseCard_NonItemHref_ReturnsNull()
    {
        Assert.Null(FacebookMarketplaceParser.ParseCard(
            Card("https://www.facebook.com/marketplace/category/electronics", "$40", "Dyson V11")));
    }

    [Fact]
    public void ParseCard_PriceDrop_TakesLowerAsPriceAndHigherAsOriginal()
    {
        // Facebook renders the cut price and the struck-through original as two price lines.
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$900", "$1,200", "PS5 Digital Edition", "Reno, NV"));

        Assert.Equal(900m, listing!.Price);
        Assert.Equal(1200m, listing.OriginalPrice);
    }

    [Fact]
    public void ParseCard_Free_IsFreeNotZeroPrice()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "Free", "Moving boxes, must go", "Las Vegas, NV"));

        Assert.True(listing!.IsFree);
        Assert.Null(listing.Price);
        Assert.Equal("Free", listing.PriceText);
    }

    [Fact]
    public void ParseCard_NoPrice_ReturnsNull()
    {
        // A tile with no price at all isn't a sellable comparable — the grid also renders
        // non-listing promo tiles that would otherwise land in the results.
        Assert.Null(FacebookMarketplaceParser.ParseCard(Card(Href, "Sponsored", "Las Vegas, NV")));
    }

    [Fact]
    public void ParseCard_NoTitle_ReturnsNull()
    {
        Assert.Null(FacebookMarketplaceParser.ParseCard(Card(Href, "$1,200", "Las Vegas, NV")));
    }

    [Fact]
    public void ParseCard_Cents_ParsedExactly()
    {
        var listing = FacebookMarketplaceParser.ParseCard(Card(Href, "$1,249.99", "Antminer S21", "Las Vegas, NV"));
        Assert.Equal(1249.99m, listing!.Price);
    }

    [Fact]
    public void ParseCard_DistanceInMiles_IsRead()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$300", "Weber Genesis Grill", "Henderson, NV", "12 miles away"));

        Assert.Equal(12, listing!.DistanceMiles);
        Assert.Equal("Henderson, NV", listing.Location);
    }

    [Fact]
    public void ParseCard_DistanceInKilometers_IsConvertedToMiles()
    {
        // Non-US accounts get km; the UI's "within N miles" has to keep meaning one thing.
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$300", "Weber Genesis Grill", "Toronto, ON", "16 km away"));

        Assert.Equal(9.9, listing!.DistanceMiles);
    }

    [Fact]
    public void ParseCard_PostedTime_IsCapturedNotTreatedAsTitle()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "Just listed", "$450", "Trek Marlin 7 Mountain Bike", "Las Vegas, NV"));

        Assert.Equal("Just listed", listing!.PostedAgo);
        Assert.Equal("Trek Marlin 7 Mountain Bike", listing.Title);
    }

    [Fact]
    public void ParseCard_RelativePostedTime_IsCaptured()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$450", "Trek Marlin 7 Mountain Bike", "3 hours ago", "Las Vegas, NV"));

        Assert.Equal("3 hours ago", listing!.PostedAgo);
        Assert.Equal("Trek Marlin 7 Mountain Bike", listing.Title);
    }

    [Fact]
    public void ParseCard_NoiseLines_AreDropped()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "Sponsored", "$75", "Ryobi 18V Drill Kit", "Free shipping", "Las Vegas, NV"));

        Assert.Equal("Ryobi 18V Drill Kit", listing!.Title);
        Assert.Equal("Las Vegas, NV", listing.Location);
    }

    [Fact]
    public void ParseCard_TitleIsLongestProseLine()
    {
        var listing = FacebookMarketplaceParser.ParseCard(
            Card(Href, "$220", "Sealed", "Nintendo Switch OLED with extra dock", "Las Vegas, NV"));

        Assert.Equal("Nintendo Switch OLED with extra dock", listing!.Title);
    }

    // ── Radius snapping ────────────────────────────────────────────────────────

    [Theory]
    [InlineData(40, 40)]
    [InlineData(45, 40)]      // nearer 40 than 60
    [InlineData(0, 1)]        // below the smallest offered radius
    [InlineData(9999, 500)]   // above the largest
    [InlineData(50, 60)]      // exact tie rounds up — one extra town beats missing one
    public void NearestSupportedRadius_SnapsToFacebooksOwnOptions(int requested, int expected)
    {
        Assert.Equal(expected, FacebookMarketplaceParser.NearestSupportedRadius(requested));
    }

    // ── BuildResult ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildResult_DeduplicatesTilesTheScrollingGridRepeats()
    {
        var cards = new[]
        {
            Card("https://www.facebook.com/marketplace/item/111/", "$100", "Antminer S9 miner", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/111/?ref=x", "$100", "Antminer S9 miner", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/222/", "$150", "Antminer S9 miner unit", "Reno, NV"),
        };

        var result = FacebookMarketplaceParser.BuildResult(cards, "antminer", "89101", 40);

        Assert.Equal(2, result.Count);
        Assert.Equal(["111", "222"], result.Items.Select(i => i.ItemId).Order());
    }

    [Fact]
    public void BuildResult_ComputesAskSpreadAndSortsCheapestFirst()
    {
        var cards = new[]
        {
            Card("https://www.facebook.com/marketplace/item/1/", "$300", "Antminer S19", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/2/", "$100", "Antminer S19 unit", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/3/", "$200", "Antminer S19 spare", "Las Vegas, NV"),
        };

        var result = FacebookMarketplaceParser.BuildResult(cards, "antminer", "89101", 40);

        Assert.Equal(100m, result.Min);
        Assert.Equal(200m, result.Median);
        Assert.Equal(300m, result.Max);
        Assert.Equal([100m, 200m, 300m], result.Items.Select(i => i.Price));
    }

    [Fact]
    public void BuildResult_EvenCount_MedianIsMidpoint()
    {
        var cards = new[]
        {
            Card("https://www.facebook.com/marketplace/item/1/", "$100", "Antminer A", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/2/", "$300", "Antminer B", "Las Vegas, NV"),
        };

        Assert.Equal(200m, FacebookMarketplaceParser.BuildResult(cards, "antminer", "89101", 40).Median);
    }

    [Fact]
    public void BuildResult_FreeItemsAreListedButDoNotDragTheMedianToZero()
    {
        var cards = new[]
        {
            Card("https://www.facebook.com/marketplace/item/1/", "Free", "Antminer parts lot", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/2/", "$300", "Antminer S19", "Las Vegas, NV"),
        };

        var result = FacebookMarketplaceParser.BuildResult(cards, "antminer", "89101", 40);

        Assert.Equal(2, result.Count);
        Assert.Equal(300m, result.Min);
        Assert.Equal(300m, result.Median);
    }

    [Fact]
    public void BuildResult_NoResults_HasNullSpreadNotZero()
    {
        var result = FacebookMarketplaceParser.BuildResult([], "antminer", "89101", 40);

        Assert.Equal(0, result.Count);
        Assert.Null(result.Min);
        Assert.Null(result.Median);
        Assert.Null(result.Max);
        Assert.Equal("ok", result.Status);
    }

    [Fact]
    public void BuildResult_EchoesTheRadiusActuallySearched()
    {
        var result = FacebookMarketplaceParser.BuildResult([], "antminer", "89101", 45);
        Assert.Equal(40, result.RadiusMiles);
    }

    // ── Relevance ──────────────────────────────────────────────────────────────

    [Fact]
    public void FilterByRelevance_DropsFacebooksLooselyRelatedPadding()
    {
        var cards = new[]
        {
            Card("https://www.facebook.com/marketplace/item/1/", "$300", "Antminer S19 Pro", "Las Vegas, NV"),
            Card("https://www.facebook.com/marketplace/item/2/", "$40", "Patio furniture set", "Las Vegas, NV"),
        };

        var result = FacebookMarketplaceParser.BuildResult(cards, "antminer", "89101", 40);

        Assert.Single(result.Items);
        Assert.Equal("Antminer S19 Pro", result.Items[0].Title);
        // The padding item would otherwise have set the local floor at $40.
        Assert.Equal(300m, result.Min);
    }

    [Fact]
    public void FilterByRelevance_WhenNothingWordMatches_KeepsEverything()
    {
        // Better to show results the seller can judge than to report a false "no local supply".
        var items = new List<FacebookMarketplaceListing>
        {
            new() { ItemId = "1", Title = "Bitcoin mining rig", Price = 500m },
        };

        var kept = FacebookMarketplaceParser.FilterByRelevance(items, "antminer");

        Assert.Single(kept);
    }

    [Fact]
    public void FilterByRelevance_IgnoresShortNoiseTokens()
    {
        // "s9" is two characters; matching on it would let every title through.
        var items = new List<FacebookMarketplaceListing>
        {
            new() { ItemId = "1", Title = "Antminer S9 miner", Price = 100m },
            new() { ItemId = "2", Title = "Kids bike s9 sticker", Price = 20m },
        };

        var kept = FacebookMarketplaceParser.FilterByRelevance(items, "antminer s9");

        Assert.Single(kept);
        Assert.Equal("1", kept[0].ItemId);
    }

    // ── URL building ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildSearchUrl_EscapesQueryAndConvertsRadiusToKilometers()
    {
        var url = FacebookMarketplaceSelectors.BuildSearchUrl("antminer s19 & psu", 40);

        Assert.Contains("query=antminer%20s19%20%26%20psu", url);
        Assert.Contains("radius_in_km=64", url); // 40 mi -> 64 km
        Assert.Contains("sortBy=creation_time_descend", url);
    }
}
