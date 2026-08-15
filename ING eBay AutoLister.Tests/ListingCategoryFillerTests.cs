using System.Collections.Concurrent;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

// The filler is what turns "every listing reports category unknown" into a card that runs on real
// data — but it spends one real eBay call per listing, so every one of these guards its cost: it
// only fetches what is genuinely blank, reads the cache before eBay, populates the cache after,
// stays under a concurrency cap, honours a per-scan lookup cap, and never lets one bad fetch fail
// the scan.
public class ListingCategoryFillerTests
{
    private static EbayListingSummary Listing(string id, string category = "", string categoryId = "") =>
        new() { ListingId = id, Title = "Item " + id, Category = category, CategoryId = categoryId };

    private static Func<string, CancellationToken, Task<(string, string)>> FetchReturning(
        string category, string categoryId, Action<string>? seen = null) =>
        (id, _) =>
        {
            seen?.Invoke(id);
            return Task.FromResult((category, categoryId));
        };

    [Fact]
    public async Task A_blank_category_is_filled_from_the_per_item_lookup()
    {
        var listings = new List<EbayListingSummary> { Listing("1") };
        var cache = new ListingCategoryCache();

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache, FetchReturning("Cameras & Photo > Digital Cameras", "31388"));

        Assert.Equal("Cameras & Photo > Digital Cameras", listings[0].Category);
        Assert.Equal("31388", listings[0].CategoryId);
    }

    [Fact]
    public async Task A_listing_that_already_has_a_category_is_never_looked_up()
    {
        var listings = new List<EbayListingSummary> { Listing("1", "Already Here", "999") };
        var cache = new ListingCategoryCache();
        var fetched = new List<string>();

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache, FetchReturning("X", "1", fetched.Add));

        Assert.Empty(fetched);
        Assert.Equal("Already Here", listings[0].Category);
    }

    [Fact]
    public async Task A_cached_category_is_used_instead_of_a_fresh_ebay_call()
    {
        var cache = new ListingCategoryCache();
        cache.Set("1", "Cached Name", "555");
        var listings = new List<EbayListingSummary> { Listing("1") };
        var fetched = new List<string>();

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache, FetchReturning("Fresh", "111", fetched.Add));

        Assert.Empty(fetched);                       // the re-scan did NOT re-hit eBay
        Assert.Equal("Cached Name", listings[0].Category);
        Assert.Equal("555", listings[0].CategoryId);
    }

    [Fact]
    public async Task A_freshly_fetched_category_is_written_back_to_the_cache()
    {
        var cache = new ListingCategoryCache();
        var listings = new List<EbayListingSummary> { Listing("1") };

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache, FetchReturning("Name", "777"));

        var hit = cache.TryGet("1");
        Assert.NotNull(hit);
        Assert.Equal("777", hit!.Value.CategoryId);
    }

    [Fact]
    public async Task One_failing_lookup_leaves_that_listing_unknown_and_does_not_fail_the_scan()
    {
        var listings = new List<EbayListingSummary> { Listing("bad"), Listing("good") };
        var cache = new ListingCategoryCache();
        var errors = new List<string>();

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache,
            fetch: (id, _) => id == "bad"
                ? throw new Exception("eBay said no")
                : Task.FromResult(("Good Name", "222")),
            onError: (id, _, _) => errors.Add(id));

        Assert.Equal("", listings.Single(l => l.ListingId == "bad").Category);
        Assert.Equal("Good Name", listings.Single(l => l.ListingId == "good").Category);
        Assert.Contains("bad", errors);
    }

    [Fact]
    public async Task The_lookup_count_never_exceeds_the_per_scan_cap()
    {
        var listings = Enumerable.Range(0, ListingCategoryFiller.MaxLookupsPerScan + 25)
            .Select(i => Listing("id" + i)).ToList();
        var cache = new ListingCategoryCache();
        var calls = 0;

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache,
            fetch: (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(("N", "1")); });

        Assert.Equal(ListingCategoryFiller.MaxLookupsPerScan, calls);
        // The overflow past the cap keeps a blank category and is reported honestly, not fetched.
        Assert.Contains(listings, l => string.IsNullOrEmpty(l.Category));
    }

    [Fact]
    public async Task No_more_than_the_concurrency_cap_of_lookups_run_at_once()
    {
        var listings = Enumerable.Range(0, 40).Select(i => Listing("id" + i)).ToList();
        var cache = new ListingCategoryCache();
        var inFlight = 0;
        var peak = 0;
        var padlock = new object();

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache,
            fetch: async (_, _) =>
            {
                var now = Interlocked.Increment(ref inFlight);
                lock (padlock) peak = Math.Max(peak, now);
                await Task.Delay(15);
                Interlocked.Decrement(ref inFlight);
                return ("N", "1");
            });

        Assert.True(peak <= ListingCategoryFiller.MaxConcurrency,
            $"peak concurrency {peak} exceeded the cap of {ListingCategoryFiller.MaxConcurrency}");
    }

    [Fact]
    public async Task An_empty_account_does_no_work_and_does_not_throw()
    {
        var listings = new List<EbayListingSummary>();
        var cache = new ListingCategoryCache();
        var calls = 0;

        await ListingCategoryFiller.FillCategoriesAsync(
            listings, cache,
            fetch: (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(("N", "1")); });

        Assert.Equal(0, calls);
    }
}
