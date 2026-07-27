using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class ShippingLeakScannerTests
{
    private static ShippingLeakScanner Scanner() =>
        new(new PackageEstimator(),
            new ShippingAdvisor(new PackageEstimator(), new NetProceedsCalculator(new ProfitCalculator())));

    private static FeeProfile Fees(decimal assumedShipping) =>
        new() { DefaultShippingCost = assumedShipping };

    private static EbayListingSummary Listing(
        string title, decimal price, int quantity = 1,
        decimal weightLbs = 0m, decimal l = 0m, decimal w = 0m, decimal h = 0m) => new()
        {
            ListingId = Guid.NewGuid().ToString("N")[..8],
            Title = title,
            Price = price,
            Quantity = quantity,
            Status = "ACTIVE",
            Data = new PostListingRequest
            {
                WeightLbs = weightLbs,
                PackageLengthIn = l,
                PackageWidthIn = w,
                PackageHeightIn = h,
            },
        };

    [Fact]
    public void Scan_FindsListingsWhoseRealLabelBeatsTheFlatAssumption()
    {
        // A 24 lb amplifier assumed to ship for $5. The gap is the whole point of the feature.
        var result = Scanner().Scan([Listing("Stereo receiver amplifier", 300m)], "43004", Fees(5m));

        var leak = Assert.Single(result.Leaks);
        Assert.Equal("underpriced_label", leak.Kind);
        Assert.Equal("critical", leak.Severity);
        Assert.True(leak.PerSaleImpact > 0m);
        Assert.Equal(Math.Round(leak.ExpectedLabelCost - 5m, 2), leak.PerSaleImpact);
    }

    [Fact]
    public void Scan_StaysQuietWhenTheFlatAssumptionIsAlreadyGenerous()
    {
        // A phone assumed at $25 a label: nothing to report, and no invented finding.
        var result = Scanner().Scan([Listing("iPhone 13 unlocked", 400m)], "43004", Fees(25m));

        Assert.Empty(result.Leaks);
        Assert.Equal(1, result.Summary.ListingsScanned);
        Assert.Equal(0m, result.Summary.TotalPerSaleImpact);
    }

    [Fact]
    public void Scan_WarnsWhenTheFeeProfileHasNoShippingCostAtAll()
    {
        var result = Scanner().Scan([Listing("Dell laptop i7", 500m)], "43004", Fees(0m));

        Assert.Contains("assuming shipping is free", result.DataWarning);
        // With nothing assumed, the whole label is the gap.
        var leak = Assert.Single(result.Leaks);
        Assert.Equal(leak.ExpectedLabelCost, leak.PerSaleImpact);
    }

    [Fact]
    public void Scan_ReportsOneFindingPerListingNotAllOfThem()
    {
        // This box has several things wrong with it at once; the board must still show one row.
        var listing = Listing("Lampshade", 40m, weightLbs: 3m, l: 24m, w: 20m, h: 16m);
        var result = Scanner().Scan([listing], "43004", Fees(4m));

        Assert.Single(result.Leaks);
    }

    [Fact]
    public void Scan_RanksByMoneyAndTotalsIt()
    {
        var result = Scanner().Scan(
        [
            Listing("iPhone 13 unlocked", 400m),
            Listing("Stereo receiver amplifier", 300m),
            Listing("Dell laptop i7", 500m),
        ], "43004", Fees(4m));

        var impacts = result.Leaks.Select(l => l.PerSaleImpact).ToList();
        Assert.Equal(impacts.OrderByDescending(i => i), impacts);
        Assert.Equal(Math.Round(impacts.Sum(), 2), result.Summary.TotalPerSaleImpact);
    }

    [Fact]
    public void Scan_MultipliesTheGapByTheUnitsActuallyInStock()
    {
        var result = Scanner().Scan([Listing("Stereo receiver amplifier", 300m, quantity: 4)], "43004", Fees(5m));

        var leak = Assert.Single(result.Leaks);
        Assert.Equal(Math.Round(leak.PerSaleImpact * 4m, 2), leak.AtRisk);
    }

    [Fact]
    public void Scan_SeparatesMeasuredPackagesFromGuessedOnes()
    {
        var result = Scanner().Scan(
        [
            Listing("Dell laptop i7", 500m, weightLbs: 6m, l: 18m, w: 14m, h: 5m),
            Listing("Dell laptop i7", 500m),
        ], "43004", Fees(4m));

        Assert.Equal(1, result.Summary.MeasuredPackages);
        Assert.Equal(1, result.Summary.EstimatedPackages);
        Assert.Contains(result.Leaks, l => !l.PackageEstimated);
        Assert.Contains(result.Leaks, l => l.PackageEstimated);
    }

    [Fact]
    public void Scan_FlagsAnItemNoCarrierWillTakeAsCriticalRatherThanSkippingIt()
    {
        var monster = Listing("Custom crate", 900m, weightLbs: 400m, l: 200m, w: 90m, h: 90m);
        var result = Scanner().Scan([monster], "43004", Fees(20m));

        var leak = Assert.Single(result.Leaks);
        Assert.Equal("oversize", leak.Kind);
        Assert.Equal("critical", leak.Severity);
        Assert.Contains("freight", leak.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_FlagsHeavyCheapItemsAsTheItemBeingWrongNotTheShipping()
    {
        // 20 lb in a box that fits a medium flat rate: the label is already the cheapest available,
        // costs the same everywhere, and is covered by the assumption — so there is no packing fix
        // and no zone bet left. What is left is that shipping is 35% of the asking price.
        var listing = Listing("Box of assorted parts", 50m, weightLbs: 20m, l: 10m, w: 8m, h: 5m);
        var result = Scanner().Scan([listing], "43004", Fees(20m));

        var leak = Assert.Single(result.Leaks);
        Assert.Equal("shipping_heavy", leak.Kind);
        Assert.Contains("Bundle", leak.Fix, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scan_EveryLeakCarriesAFixTheSellerCanAct0n()
    {
        var result = Scanner().Scan(
        [
            Listing("Stereo receiver amplifier", 300m),
            Listing("Lampshade", 40m, weightLbs: 3m, l: 24m, w: 20m, h: 16m),
            Listing("Box of tool parts", 120m, weightLbs: 15m, l: 12m, w: 9m, h: 6m),
        ], "43004", Fees(6m));

        Assert.NotEmpty(result.Leaks);
        Assert.All(result.Leaks, leak =>
        {
            Assert.False(string.IsNullOrWhiteSpace(leak.Headline));
            Assert.False(string.IsNullOrWhiteSpace(leak.Detail));
            Assert.False(string.IsNullOrWhiteSpace(leak.Fix));
        });
    }

    [Fact]
    public void Scan_RespectsTheItemCap()
    {
        var listings = Enumerable.Range(0, 50)
            .Select(i => Listing($"Stereo receiver amplifier {i}", 300m))
            .ToList();

        var result = Scanner().Scan(listings, "43004", Fees(5m), maxItems: 10);

        Assert.Equal(10, result.Summary.ListingsScanned);
        Assert.Equal(50, result.ActiveListings);
    }

    [Fact]
    public void Scan_OfAnEmptyInventoryIsAnEmptyResultNotAnError()
    {
        var result = Scanner().Scan([], "43004", Fees(5m));

        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Leaks);
        Assert.Equal(0, result.Summary.ListingsScanned);
        Assert.Equal(0m, result.Summary.AverageLabelCost);
    }
}
