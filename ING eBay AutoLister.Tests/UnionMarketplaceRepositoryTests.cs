using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The merge is what makes a live scrape able to change a price at all: the scraper writes into the
// LOCAL comps table while the shipped configuration priced from the HOSTED one, so until these two
// sets were read together, a three-minute lookup landed its rows somewhere nothing read them.
public class UnionMarketplaceRepositoryTests
{
    private static MarketplaceComparableResult Row(
        string itemId, decimal price, int score, DateTime? sold = null) =>
        new() { ItemId = itemId, Title = $"item {itemId}", SoldPrice = price, TotalPrice = price,
                MatchScore = score, SoldDate = sold };

    [Fact]
    public void Merge_KeepsRowsOnlyOneSourceHas()
    {
        var hosted = new[] { Row("A", 100m, 90), Row("B", 110m, 80) };
        var local  = new[] { Row("C", 120m, 70) };   // e.g. just scraped, not yet synced up

        var merged = UnionMarketplaceRepository.Merge(hosted, local, 20);

        Assert.Equal(3, merged.Count);
        Assert.Contains(merged, r => r.ItemId == "C");
    }

    [Fact]
    public void Merge_DedupesTheSameSalePresentInBoth()
    {
        // The same eBay sale legitimately exists in both tables. Counting it twice would inflate
        // the comp count every evidence gate in the app reads.
        var hosted = new[] { Row("A", 100m, 90), Row("B", 110m, 80) };
        var local  = new[] { Row("A", 100m, 90), Row("C", 120m, 70) };

        var merged = UnionMarketplaceRepository.Merge(hosted, local, 20);

        Assert.Equal(3, merged.Count);
        Assert.Single(merged, r => r.ItemId == "A");
    }

    [Fact]
    public void Merge_IsCaseInsensitiveOnItemId()
    {
        var merged = UnionMarketplaceRepository.Merge(
            [Row("abc123", 100m, 90)], [Row("ABC123", 100m, 90)], 20);

        Assert.Single(merged);
    }

    [Fact]
    public void Merge_RanksByMatchQualityThenRecency()
    {
        var older = new DateTime(2026, 1, 1);
        var newer = new DateTime(2026, 8, 1);
        var merged = UnionMarketplaceRepository.Merge(
            [Row("A", 100m, 50, older), Row("B", 100m, 90, older)],
            [Row("C", 100m, 90, newer)], 20);

        Assert.Equal("C", merged[0].ItemId);   // same score as B, but newer
        Assert.Equal("B", merged[1].ItemId);
        Assert.Equal("A", merged[2].ItemId);   // weakest match last
    }

    [Fact]
    public void Merge_RespectsTheLimit()
    {
        var hosted = Enumerable.Range(0, 30).Select(i => Row($"h{i}", 100m, 90 - i)).ToArray();
        var local  = Enumerable.Range(0, 30).Select(i => Row($"l{i}", 100m, 89 - i)).ToArray();

        var merged = UnionMarketplaceRepository.Merge(hosted, local, 20);

        Assert.Equal(20, merged.Count);
    }

    [Fact]
    public void Merge_KeepsRowsWithNoItemIdRatherThanDroppingRealSales()
    {
        // A blank id cannot be dedupe-keyed. Dropping the row would be silent data loss; keeping a
        // possible duplicate is the milder failure.
        var merged = UnionMarketplaceRepository.Merge(
            [Row("", 100m, 90), Row("", 110m, 85)], [Row("", 120m, 80)], 20);

        Assert.Equal(3, merged.Count);
    }

    [Fact]
    public void Merge_EmptyOnBothSides_ReturnsEmptyRatherThanThrowing()
    {
        Assert.Empty(UnionMarketplaceRepository.Merge([], [], 20));
    }

    [Fact]
    public void Merge_OneSideEmpty_ReturnsTheOtherUnchanged()
    {
        var local = new[] { Row("A", 100m, 90), Row("B", 110m, 80) };

        Assert.Equal(2, UnionMarketplaceRepository.Merge([], local, 20).Count);
        Assert.Equal(2, UnionMarketplaceRepository.Merge(local, [], 20).Count);
    }
}
