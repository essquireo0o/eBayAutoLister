using ING_eBay_AutoLister.Services;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Tests;

public class ShippingZonesTests
{
    [Theory]
    [InlineData(10, 1)]
    [InlineData(50, 1)]
    [InlineData(51, 2)]
    [InlineData(300, 3)]
    [InlineData(301, 4)]
    [InlineData(1000, 5)]
    [InlineData(1401, 7)]
    [InlineData(2500, 8)]
    [InlineData(9000, 8)]
    public void ZoneForDistance_FollowsUspsBands(double miles, int expectedZone) =>
        Assert.Equal(expectedZone, ShippingZones.ZoneForDistance(miles));

    [Fact]
    public void MixFor_AlwaysSumsToOneHundredPercent()
    {
        foreach (var zip in new[] { "43004", "90210", "10001", "33101", "99501" })
        {
            var mix = ShippingZones.MixFor(zip);
            Assert.Equal(100m, mix.Sum(z => z.SharePercent), 1);
        }
    }

    // A coastal seller reaches most of the country the long way round; a midwestern one does not.
    // If this ever inverts, every free-shipping recommendation in the app is backwards.
    [Fact]
    public void MixFor_CoastalOriginIsFartherFromBuyersThanCentralOrigin()
    {
        decimal AverageZone(string zip) =>
            ShippingZones.MixFor(zip).Sum(z => z.Zone * z.SharePercent) / 100m;

        var california = AverageZone("90210");
        var ohio = AverageZone("43004");

        Assert.True(california > ohio,
            $"Shipping from CA should average a higher zone than from OH; got {california} vs {ohio}.");
    }

    [Fact]
    public void MixFor_NoOriginFallsBackToTheNationalAverageRatherThanThrowing()
    {
        foreach (var zip in new string?[] { null, "", "   ", "902" })
        {
            var mix = ShippingZones.MixFor(zip);
            Assert.NotEmpty(mix);
            Assert.Equal(100m, mix.Sum(z => z.SharePercent), 1);
        }
    }

    // The origin's own region must not price as zone 1, or a tenth of every seller's sales are
    // quoted at a rate no real buyer pays.
    [Fact]
    public void MixFor_OwnRegionDoesNotCollapseToZoneOne()
    {
        var mix = ShippingZones.MixFor("90210");
        Assert.DoesNotContain(mix, z => z.Zone == 1);
    }

    [Fact]
    public void ExpectedOver_WeightsByPopulationShareNotByZoneCount()
    {
        List<ZoneShare> mix =
        [
            new() { Zone = 3, SharePercent = 90m },
            new() { Zone = 8, SharePercent = 10m },
        ];

        // 0.9 * 10 + 0.1 * 100 = 19, not the unweighted mean of 55.
        Assert.Equal(19m, ShippingZones.ExpectedOver(mix, z => z == 3 ? 10m : 100m));
    }

    [Fact]
    public void ShareAboveCost_ReportsTheSliceOfBuyersAFreeShippingPriceLosesOn()
    {
        List<ZoneShare> mix =
        [
            new() { Zone = 3, SharePercent = 60m },
            new() { Zone = 6, SharePercent = 25m },
            new() { Zone = 8, SharePercent = 15m },
        ];

        decimal Cost(int zone) => zone switch { 3 => 5m, 6 => 9m, _ => 14m };

        // Collecting $9 covers zones 3 and 6 exactly; only zone 8 is underwater.
        Assert.Equal(15m, ShippingZones.ShareAboveCost(mix, Cost, 9m));
        // Collecting $4 covers nobody.
        Assert.Equal(100m, ShippingZones.ShareAboveCost(mix, Cost, 4m));
        // Collecting $20 covers everybody.
        Assert.Equal(0m, ShippingZones.ShareAboveCost(mix, Cost, 20m));
    }

    [Fact]
    public void NearestAndFarthestZone_BoundTheMix()
    {
        var mix = ShippingZones.MixFor("10001");
        var near = ShippingZones.NearestZone(mix);
        var far = ShippingZones.FarthestZone(mix);

        Assert.True(near <= far);
        Assert.All(mix, z => Assert.InRange(z.Zone, near, far));
    }
}
