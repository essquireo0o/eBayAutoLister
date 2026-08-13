using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads sold comps from the hosted database AND the local one, and answers with both.
/// </summary>
/// <remarks>
/// <para>
/// Until 2026-08-11 the app picked ONE of them: hosted when <c>MarketCompsApiUrl</c> was set,
/// local otherwise. That quietly broke the whole point of the on-demand scraper. A live lookup
/// writes its rows into the LOCAL <c>SoldListings</c> table, so with a hosted URL configured —
/// which is the shipped configuration — every freshly scraped comp landed in a database the
/// pricing path was not reading. The seller waited up to three minutes for eBay to answer, the
/// rows arrived, and the price did not move, because the number was still coming from a hosted
/// snapshot that knew nothing about them.
/// </para>
/// <para>
/// Merging is the honest fix rather than switching to local-only: the hosted table carries the
/// breadth (the full historical corpus) and the local one carries the recency (whatever was just
/// scraped, plus anything the collector has that has not been synced up yet). Neither is a
/// superset of the other, so reading one is always leaving evidence on the table.
/// </para>
/// <para>
/// Both sides score with the same <see cref="ComparableMatcher"/>, so their MatchScores are
/// directly comparable and a merged set can be ranked as one. Deduplication is by ItemId: the
/// same eBay sale can legitimately exist in both, and counting it twice would inflate the comp
/// count that every evidence gate in the app reads.
/// </para>
/// <para>
/// Neither source may fail the lookup. A hosted API that is down or a local file that is missing
/// degrades to whatever the other one has, which is precisely the behaviour each repository had
/// on its own.
/// </para>
/// </remarks>
public sealed class UnionMarketplaceRepository(
    MarketplaceRepository local, HostedMarketplaceRepository hosted, ActionLog log) : IMarketplaceRepository
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => await Safe(() => hosted.IsAvailableAsync(ct), false, "availability")
        || await Safe(() => local.IsAvailableAsync(ct), false, "availability");

    public async Task<IReadOnlyList<MarketplaceComparableResult>> SearchByPartNumberAsync(
        string partNumber, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default)
        => await MergeAsync(
            () => hosted.SearchByPartNumberAsync(partNumber, filters, limit, ct),
            () => local.SearchByPartNumberAsync(partNumber, filters, limit, ct), limit, "part number");

    public async Task<IReadOnlyList<MarketplaceComparableResult>> SearchByModelAsync(
        string model, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default)
        => await MergeAsync(
            () => hosted.SearchByModelAsync(model, filters, limit, ct),
            () => local.SearchByModelAsync(model, filters, limit, ct), limit, "model");

    public async Task<IReadOnlyList<MarketplaceComparableResult>> SearchByBrandAsync(
        string brand, string? additionalKeywords = null, MarketplaceSearchFilters? filters = null,
        int limit = 12, CancellationToken ct = default)
        => await MergeAsync(
            () => hosted.SearchByBrandAsync(brand, additionalKeywords, filters, limit, ct),
            () => local.SearchByBrandAsync(brand, additionalKeywords, filters, limit, ct), limit, "brand");

    public async Task<IReadOnlyList<MarketplaceComparableResult>> SearchByKeywordAsync(
        string keywords, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default)
        => await MergeAsync(
            () => hosted.SearchByKeywordAsync(keywords, filters, limit, ct),
            () => local.SearchByKeywordAsync(keywords, filters, limit, ct), limit, "keyword");

    public async Task<MarketplacePricingSummary> FindComparablesAsync(
        MarketplaceLookupRequest request, CancellationToken ct = default)
    {
        var hostedSummary = await Safe(() => hosted.FindComparablesAsync(request, ct), null, "comparables");
        var localSummary  = await Safe(() => local.FindComparablesAsync(request, ct), null, "comparables");

        if (hostedSummary is null) return localSummary ?? new MarketplacePricingSummary { Query = request.Keywords ?? "" };
        if (localSummary  is null) return hostedSummary;

        // The richer side is the base, so its price stats and liquidity assessment — both computed
        // from a whole tier of comps, not re-derivable from a merged list without recomputing the
        // tiering itself — stay internally consistent. Only the comparables are merged, and those
        // are what the price estimator actually re-reads.
        var (baseSummary, other) = localSummary.ComparableListings.Count > hostedSummary.ComparableListings.Count
            ? (localSummary, hostedSummary)
            : (hostedSummary, localSummary);

        var merged = Merge(baseSummary.ComparableListings, other.ComparableListings,
                           Math.Max(1, request.MaxComparables));

        var added = merged.Count - baseSummary.ComparableListings.Count;
        if (added > 0)
            log.Add("Info", "Sold comps merged from both databases",
                $"\"{request.Keywords}\": {hostedSummary.ComparableListings.Count} hosted + " +
                $"{localSummary.ComparableListings.Count} local -> {merged.Count} after dedupe " +
                $"({added} the other source alone would have missed).");

        baseSummary.ComparableListings = merged;
        baseSummary.MatchCount = merged.Count;
        return baseSummary;
    }

    private async Task<IReadOnlyList<MarketplaceComparableResult>> MergeAsync(
        Func<Task<IReadOnlyList<MarketplaceComparableResult>>> fromHosted,
        Func<Task<IReadOnlyList<MarketplaceComparableResult>>> fromLocal,
        int limit, string what)
    {
        var h = await Safe(fromHosted, Array.Empty<MarketplaceComparableResult>(), what);
        var l = await Safe(fromLocal,  Array.Empty<MarketplaceComparableResult>(), what);
        return Merge(h, l, Math.Max(1, limit));
    }

    /// <summary>
    /// Dedupes by ItemId and ranks the survivors as one set. Pure and testable.
    /// </summary>
    /// <remarks>
    /// Ranked by match quality first and recency second — the same order a single repository
    /// returns — so merging changes what evidence is available without changing what "best comp"
    /// means. A row missing an ItemId cannot be dedupe-keyed, so it is kept: dropping real sales
    /// because the id column was blank would be a silent data loss, and a duplicate is the milder
    /// failure of the two.
    /// </remarks>
    public static List<MarketplaceComparableResult> Merge(
        IReadOnlyList<MarketplaceComparableResult> first,
        IReadOnlyList<MarketplaceComparableResult> second,
        int limit)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<MarketplaceComparableResult>(first.Count + second.Count);

        foreach (var row in first.Concat(second))
        {
            if (row is null) continue;
            var id = row.ItemId;
            if (!string.IsNullOrWhiteSpace(id) && !seen.Add(id)) continue;
            all.Add(row);
        }

        return all
            .OrderByDescending(r => r.MatchScore)
            .ThenByDescending(r => r.SoldDate ?? DateTime.MinValue)
            .Take(limit)
            .ToList();
    }

    // One source being down is not the lookup failing — it is the other source's answer.
    private async Task<T> Safe<T>(Func<Task<T>> call, T fallback, string what)
    {
        try { return await call(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", $"One sold-comps source failed ({what})", ex.Message);
            return fallback;
        }
    }
}
