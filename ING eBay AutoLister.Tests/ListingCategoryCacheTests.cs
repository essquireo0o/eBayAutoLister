using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

// The cache exists for one reason: to stop a re-scan of the same account from re-spending an eBay
// GetItem call on a category it already read. So these pin that it round-trips what it was told,
// and that it lets go of an entry once it is older than its life — the two properties the scan
// relies on to be both cheap and not stale.
public class ListingCategoryCacheTests
{
    private static readonly DateTime T0 = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_stored_category_round_trips()
    {
        var cache = new ListingCategoryCache();
        cache.Set("110000000001", "Cameras & Photo > Digital Cameras", "31388", T0);

        var hit = cache.TryGet("110000000001", T0);

        Assert.NotNull(hit);
        Assert.Equal("Cameras & Photo > Digital Cameras", hit!.Value.Category);
        Assert.Equal("31388", hit.Value.CategoryId);
    }

    [Fact]
    public void An_unknown_listing_returns_nothing_rather_than_an_empty_entry()
    {
        var cache = new ListingCategoryCache();
        Assert.Null(cache.TryGet("nope", T0));
    }

    [Fact]
    public void A_category_read_just_inside_the_ttl_is_still_served()
    {
        var cache = new ListingCategoryCache();
        cache.Set("id", "Name", "123", T0);

        var justInside = T0 + cache.TimeToLive - TimeSpan.FromMinutes(1);

        Assert.NotNull(cache.TryGet("id", justInside));
    }

    [Fact]
    public void A_category_older_than_the_ttl_is_dropped_so_a_moved_listing_is_re_read()
    {
        var cache = new ListingCategoryCache();
        cache.Set("id", "Name", "123", T0);

        var pastLife = T0 + cache.TimeToLive + TimeSpan.FromMinutes(1);

        // Miss means the scan will fetch it again — which is exactly what should happen once the
        // cached answer is old enough that the listing might have been re-categorised.
        Assert.Null(cache.TryGet("id", pastLife));
    }

    [Fact]
    public void A_blank_listing_id_is_never_stored_or_served()
    {
        var cache = new ListingCategoryCache();
        cache.Set("", "Name", "123", T0);
        Assert.Null(cache.TryGet("", T0));
    }
}
