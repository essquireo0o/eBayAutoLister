using System.Collections.Concurrent;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// A short-lived memory of each listing's eBay category, keyed by listing id.
/// </summary>
/// <remarks>
/// eBay's bulk listing call omits the category; the per-item GetItem call has it but costs one API
/// call each. This cache is what stops a re-scan of the same account minutes later from spending
/// that call again on a category it already read. A 24-hour life is long enough that a re-scan is
/// free and short enough that a listing genuinely moved to a new category is picked up by tomorrow.
/// Singleton, so the memory is shared across every scan for the lifetime of the process.
/// </remarks>
public sealed class ListingCategoryCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Entry> _byListingId =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct Entry(string Category, string CategoryId, DateTime FetchedUtc);

    /// <summary>How long a cached category is trusted before it is fetched again.</summary>
    public TimeSpan TimeToLive => Ttl;

    /// <summary>The cached category for a listing, or null when nothing fresh is held for it.</summary>
    public (string Category, string CategoryId)? TryGet(string listingId) =>
        TryGet(listingId, DateTime.UtcNow);

    /// <summary>Time-injected overload, so the TTL can be exercised without waiting a day.</summary>
    public (string Category, string CategoryId)? TryGet(string listingId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(listingId)) return null;
        if (!_byListingId.TryGetValue(listingId, out var e)) return null;

        // Expired entries are dropped on read rather than swept: the only thing that ever asks for
        // one is the scan, so a stale entry costs nothing until it is looked at, then removes itself.
        if (nowUtc - e.FetchedUtc > Ttl)
        {
            _byListingId.TryRemove(listingId, out _);
            return null;
        }
        return (e.Category, e.CategoryId);
    }

    /// <summary>Remembers a listing's category as of now.</summary>
    public void Set(string listingId, string category, string categoryId) =>
        Set(listingId, category, categoryId, DateTime.UtcNow);

    /// <summary>Time-injected overload, matching <see cref="TryGet(string, DateTime)"/>.</summary>
    public void Set(string listingId, string category, string categoryId, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(listingId)) return;
        _byListingId[listingId] = new Entry(category ?? "", categoryId ?? "", nowUtc);
    }
}

/// <summary>
/// Fills in the eBay category on the listings that came back from the bulk call without one, by
/// looking each up per-item — cache first, then a bounded burst of GetItem calls.
/// </summary>
/// <remarks>
/// <para>
/// Every guard here exists because this spends real eBay API calls. The concurrency cap turns 88
/// listings into a short burst instead of a serial stall or an unbounded fan-out that would trip
/// eBay's rate limits; the lookup cap stops an account with thousands of listings from launching
/// thousands of calls; the overall timeout means the scan can never hang; and the cache means a
/// re-scan does not re-fetch what it already knows.
/// </para>
/// <para>
/// A single listing's failure or timeout is swallowed — that listing simply keeps its blank
/// category and is reported honestly as "not read this pass" — because one bad GetItem must never
/// fail the whole scan. The listing summaries are mutated in place.
/// </para>
/// </remarks>
public static class ListingCategoryFiller
{
    /// <summary>Most GetItem calls in flight at once. Small enough to stay well under eBay's limits.</summary>
    public const int MaxConcurrency = 6;

    /// <summary>Most per-item lookups one scan may launch. The rest stay unknown, counted honestly.</summary>
    public const int MaxLookupsPerScan = 300;

    /// <summary>Hard ceiling on the whole fill, so the scan can never wait forever.</summary>
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Reads the cache for every blank-category listing, then fetches the remainder (up to the cap)
    /// under the concurrency limit and the overall timeout. Never throws.
    /// </summary>
    /// <param name="listings">The bulk-call summaries, mutated in place.</param>
    /// <param name="cache">Shared category memory; read before fetching and written after.</param>
    /// <param name="fetch">Per-item category lookup — in production, one GetItem call.</param>
    /// <param name="onError">Called with (listingId, what, detail) for a fetch that failed. Optional.</param>
    /// <param name="ct">Request cancellation; the timeout is layered on top of it.</param>
    public static async Task FillCategoriesAsync(
        IReadOnlyList<EbayListingSummary> listings,
        ListingCategoryCache cache,
        Func<string, CancellationToken, Task<(string Category, string CategoryId)>> fetch,
        Action<string, string, string>? onError = null,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var needing = new List<EbayListingSummary>();

        foreach (var l in listings)
        {
            // Only listings that actually lack a category and carry an id we can look up. A listing
            // the bulk call already categorised is left untouched — no call, no cache read.
            if (!string.IsNullOrWhiteSpace(l.Category) || !string.IsNullOrWhiteSpace(l.CategoryId))
                continue;
            if (string.IsNullOrWhiteSpace(l.ListingId))
                continue;

            if (cache.TryGet(l.ListingId, now) is { } hit)
            {
                l.Category = hit.Category;
                l.CategoryId = hit.CategoryId;
                continue;
            }
            needing.Add(l);
        }

        if (needing.Count == 0) return;

        // Over the cap, we fetch the cap's worth and leave the rest unknown — the card's honest
        // "still couldn't be read" count covers them.
        var toFetch = needing.Count > MaxLookupsPerScan
            ? needing.GetRange(0, MaxLookupsPerScan)
            : needing;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(OverallTimeout);
        var token = timeoutCts.Token;

        using var gate = new SemaphoreSlim(MaxConcurrency);

        var tasks = toFetch.Select(async l =>
        {
            try
            {
                await gate.WaitAsync(token);
            }
            catch (OperationCanceledException)
            {
                // Timed out before this one even started — leave it unknown.
                return;
            }

            try
            {
                var (cat, catId) = await fetch(l.ListingId, token);
                if (!string.IsNullOrWhiteSpace(cat) || !string.IsNullOrWhiteSpace(catId))
                {
                    l.Category = cat ?? "";
                    l.CategoryId = catId ?? "";
                    cache.Set(l.ListingId, l.Category, l.CategoryId, DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException)
            {
                // Overall timeout landed mid-fetch. Keep whatever else came back; leave this unknown.
            }
            catch (Exception ex)
            {
                onError?.Invoke(l.ListingId, "GetItem failed", ex.Message);
            }
            finally
            {
                gate.Release();
            }
        });

        // Each task swallows its own faults, so this only ever completes — it never throws.
        await Task.WhenAll(tasks);
    }
}
