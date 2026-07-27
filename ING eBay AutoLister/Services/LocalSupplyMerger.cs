using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Folds several sources' results into one search. Pure — the searching itself is the caller's
/// job, so every rule here (which status wins, how a cap is shared out) is unit-testable.
/// </summary>
public static class LocalSupplyMerger
{
    /// <summary>
    /// Rolls up per-source statuses. Any source that answered makes the whole search <c>ok</c>:
    /// a disconnected Facebook must never blank out results Craigslist just returned. Only when
    /// nothing answered does the reason matter, and then the most actionable one wins — a missing
    /// or expired session is something the seller can fix in one click, a site error isn't.
    /// </summary>
    public static string RollUpStatus(IEnumerable<LocalSupplySearchResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0) return "no_sources";
        if (list.Any(r => r.Status == "ok")) return "ok";
        if (list.Any(r => r.Status == "session_expired")) return "session_expired";
        if (list.Any(r => r.Status == "not_connected")) return "not_connected";
        return "error";
    }

    public static LocalSupplyMultiResult Merge(
        IEnumerable<LocalSupplySearchResult> results, string query, string zip, int radiusMiles)
    {
        var list = results.ToList();
        var items = DedupeByUrl(LocalSupplyResults.Dedupe(list.SelectMany(r => r.Items)));
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplyMultiResult
        {
            Status = RollUpStatus(list),
            Query = query,
            ZipCode = zip,
            RadiusMiles = radiusMiles,
            Items = items,
            Sources = list.Select(LocalSupplySourceOutcome.From).ToList(),
            Min = min, Median = median, Max = max,
            // Only surfaced when nothing worked at all; otherwise per-source errors carry it,
            // and a whole-search error message over a table of real results reads as a failure.
            Error = list.Any(r => r.Status == "ok") ? null : list.Select(r => r.Error).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
        };
    }

    /// <summary>
    /// Drops the same post found by two different sources.
    ///
    /// <see cref="LocalSupplyResults.Dedupe"/> keys on (source, id) and cannot see this: the same
    /// craigslist post is one row from the for-sale board and another from the free-stuff board, and
    /// the two arrive under different source ids. One post at one address is one thing to go and
    /// collect, and showing it twice would double its weight in a ranking that is meant to be one
    /// row per thing to buy. First source wins, which is registration order — see Program.cs.
    /// </summary>
    /// <remarks>
    /// Keyed on the URL alone and nothing softer. Two listings that merely look alike are usually
    /// two real items, and collapsing those would hide supply rather than tidy it.
    /// </remarks>
    public static List<LocalSupplyListing> DedupeByUrl(IEnumerable<LocalSupplyListing> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return items
            .Where(i => string.IsNullOrWhiteSpace(i.Url) || seen.Add(i.Url.Trim()))
            .ToList();
    }

    /// <summary>
    /// Applies the analysis cap across sources round-robin (cheapest first within each source)
    /// instead of to one flat list.
    ///
    /// A flat "take 30" is quietly biased: Craigslist returns dozens of results in one cheap HTTP
    /// call and Facebook a handful from an expensive page load, so cheap-first over the merged
    /// list would spend the whole budget on one site and report the other as having no local
    /// supply. Round-robin gives every site the seller picked a fair share of the same cap.
    /// </summary>
    public static List<LocalSupplyListing> TakeBalanced(IEnumerable<LocalSupplyListing> items, int max)
    {
        if (max <= 0) return [];

        var bySource = items
            .GroupBy(i => i.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Queue<LocalSupplyListing>(g.OrderBy(i => i.IsFree ? 0m : i.Price ?? decimal.MaxValue)))
            .ToList();

        var taken = new List<LocalSupplyListing>();
        while (taken.Count < max && bySource.Any(q => q.Count > 0))
        {
            foreach (var queue in bySource)
            {
                if (queue.Count == 0 || taken.Count >= max) continue;
                taken.Add(queue.Dequeue());
            }
        }

        return taken;
    }
}
