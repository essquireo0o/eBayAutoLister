using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Sold/pending tiles on Facebook Marketplace.
///
/// The rule these all defend: a sold tile still shows a price, and that price is the seller's last
/// ASK. Facebook publishes no sale prices. So a sold row must never be counted as supply, must
/// never move the local ask median, and must never reach anything that prices an item.
/// </summary>
public class FacebookSoldTests
{
    private static FacebookRawCard Card(string id, string title, string price, params string[] extra) => new()
    {
        Href = $"/marketplace/item/{id}/",
        ImageUrl = "https://example.com/x.jpg",
        Lines = new List<string> { price, title }.Concat(extra).ToList(),
    };

    [Fact]
    public void A_sold_tile_is_flagged_and_kept_out_of_buyable_supply()
    {
        var result = FacebookMarketplaceParser.BuildResult(
        [
            Card("1", "Antminer S19 95th miner", "$500"),
            Card("2", "Antminer S19 95th miner", "$300", "Sold"),
        ], "antminer s19", "02341", 40);

        Assert.Single(result.Items);
        Assert.Equal("1", result.Items[0].ItemId);

        Assert.Single(result.SoldItems);
        Assert.Equal("2", result.SoldItems[0].ItemId);
        Assert.True(result.SoldItems[0].IsSold);
        Assert.Equal("Sold", result.SoldItems[0].SoldStateText);
    }

    [Theory]
    [InlineData("Sold")]
    [InlineData("sold")]
    [InlineData("Pending")]
    [InlineData("Sale pending")]
    [InlineData("Sold out")]
    [InlineData("No longer available")]
    public void Every_wording_facebook_uses_is_recognised(string badge)
    {
        var result = FacebookMarketplaceParser.BuildResult(
            [Card("9", "Dewalt drill combo kit", "$120", badge)], "dewalt drill", "02341", 40);

        Assert.Empty(result.Items);
        Assert.Single(result.SoldItems);
    }

    [Fact]
    public void A_sold_price_does_not_drag_the_local_ask_median()
    {
        // Without the split the sold $100 would pull the median of "what it costs here" down to a
        // number nobody can actually buy at.
        var withSold = FacebookMarketplaceParser.BuildResult(
        [
            Card("1", "Dewalt drill combo kit", "$300"),
            Card("2", "Dewalt drill combo kit", "$320"),
            Card("3", "Dewalt drill combo kit", "$100", "Sold"),
        ], "dewalt drill", "02341", 40);

        var withoutSold = FacebookMarketplaceParser.BuildResult(
        [
            Card("1", "Dewalt drill combo kit", "$300"),
            Card("2", "Dewalt drill combo kit", "$320"),
        ], "dewalt drill", "02341", 40);

        Assert.Equal(withoutSold.Median, withSold.Median);
        Assert.Equal(withoutSold.Min, withSold.Min);
        Assert.Equal(300m, withSold.Min);
    }

    [Fact]
    public void The_badge_does_not_become_the_location()
    {
        // Before the badge was recognised it fell through to the prose bucket, where the
        // last-non-title line becomes Location — so a sold item was "located" in Sold.
        var result = FacebookMarketplaceParser.BuildResult(
            [Card("4", "Snap-on socket set 3/8 drive", "$210", "Sold")], "snap-on", "02341", 40);

        var row = Assert.Single(result.SoldItems);
        Assert.NotEqual("Sold", row.Location);
        Assert.Equal("Snap-on socket set 3/8 drive", row.Title);
    }

    [Fact]
    public void The_recorded_price_is_the_ask_and_the_model_says_so()
    {
        var result = FacebookMarketplaceParser.BuildResult(
            [Card("7", "Milwaukee packout stack", "$275", "Sold")], "packout", "02341", 40);

        var row = new FacebookSoldRow
        {
            ItemId = result.SoldItems[0].ItemId,
            LastAskPrice = result.SoldItems[0].Price,
        };

        // The property a caller reaches for is named for what it holds. There is no SoldPrice to
        // reach for by mistake, and that absence is the point.
        Assert.Equal(275m, row.LastAskPrice);
        Assert.Null(typeof(FacebookSoldRow).GetProperty("SoldPrice"));
        Assert.Null(typeof(FacebookSoldRow).GetProperty("SalePrice"));
    }

    [Fact]
    public void An_ordinary_listing_is_untouched()
    {
        var result = FacebookMarketplaceParser.BuildResult(
            [Card("5", "Honda EU2200i generator", "$650", "2 mi away", "Quincy, MA")], "generator", "02341", 40);

        var row = Assert.Single(result.Items);
        Assert.False(row.IsSold);
        Assert.Empty(result.SoldItems);
        Assert.Equal(650m, row.Price);
        Assert.Equal("Quincy, MA", row.Location);
    }
}
