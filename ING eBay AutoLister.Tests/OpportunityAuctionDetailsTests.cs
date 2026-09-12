using System.Reflection;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
namespace ING_eBay_AutoLister.Tests;
public class OpportunityAuctionDetailsTests
{
    private static LocalSupplyListing Map(EbayOpportunityItem item) =>
        (LocalSupplyListing)typeof(EbaySupplySource).GetMethod("ToListing", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(new EbaySupplySource(null!, null!, null!), [item])!;

    [Fact]
    public void AuctionDetailsAndZeroFeedbackSurvivePricing()
    {
        var end = DateTime.UtcNow.AddHours(2);
        var listing = Map(new() { Title = "Antminer S19", Price = 45m, BuyingOption = "AUCTION",
            BidCount = 1, EndDate = end, SellerFeedbackScore = 0, ShippingStated = true, ShippingCost = 20m });
        var analyzer = new LocalArbitrageAnalyzer(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));
        var row = analyzer.Build(listing, new ResalePricing { ExpectedSale = 150m }, new FeeProfile());
        Assert.Equal("AUCTION", row.BuyingOption);
        Assert.Equal(1, row.BidCount);
        Assert.Equal(end, row.AuctionEndUtc);
        Assert.Equal(0, row.SellerFeedbackScore);
        Assert.Equal(20m, row.PurchaseShippingCost);
    }

    [Fact]
    public void FixedPriceDoesNotPretendToBeAZeroBidAuction()
    {
        var listing = Map(new() { Price = 45m, BuyingOption = "FIXED_PRICE", BidCount = 0 });
        Assert.Null(listing.BidCount);
        Assert.Null(listing.AuctionEndUtc);
        Assert.Null(listing.PurchaseShippingCost);
        Assert.False(listing.FreeShipping);
    }
}

