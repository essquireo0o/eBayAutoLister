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
    /// <remarks>
    /// <c>skipped</c> loses to every other reason, and to <c>error</c> in particular. A scan that
    /// spent its whole budget on two sites that then failed has an error to report and a source it
    /// never reached; the error is what the seller can act on, and "wasn't searched" as the headline
    /// would bury it. See <see cref="LocalSupplyScanBudget"/>.
    /// </remarks>
    public static string RollUpStatus(IEnumerable<LocalSupplySearchResult> results)
    {
        var list = results.ToList();
        if (list.Count == 0) return "no_sources";
        if (list.Any(r => r.Status == "ok")) return "ok";
        if (list.Any(r => r.Status == "session_expired")) return "session_expired";
        if (list.Any(r => r.Status == "not_connected")) return "not_connected";
        if (list.Any(r => r.Status == "error")) return "error";
        return list.All(r => r.Status == LocalSupplyGuard.SkippedStatus)
            ? LocalSupplyGuard.SkippedStatus
            : "error";
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
    /// <param name="productKey">
    /// The product signature two differently-worded posts for the same thing share — the same key
    /// <see cref="LocalArbitrageAnalyzer.GroupByProduct"/> is about to group on. Optional, and null
    /// means every listing counts as its own product, which is the behaviour this method had before
    /// the parameter existed.
    ///
    /// <para>
    /// It matters because the cap is spent on listings while the money is spent per <b>product</b>:
    /// one sold-comps lookup and at most one Terapeak scrape, however many posts are selling that
    /// thing. Cheapest-first with no notion of a product hands a search for "iphone 13" thirty slots
    /// and lets ten near-identical posts of the cheapest one take a third of them — thirty listings
    /// in, six products priced, and the $400 post that flips for $900 never looked at because it was
    /// thirty-first cheapest. Rotating products within each site spends every slot on something the
    /// board doesn't already know the price of.
    /// </para>
    /// </param>
    public static List<LocalSupplyListing> TakeBalanced(
        IEnumerable<LocalSupplyListing> items, int max, Func<LocalSupplyListing, string>? productKey = null)
    {
        if (max <= 0) return [];

        var bySource = items
            .GroupBy(i => i.Source, StringComparer.OrdinalIgnoreCase)
            .Select(g => ProductQueues(g, productKey))
            .ToList();

        var taken = new List<LocalSupplyListing>();
        while (taken.Count < max && bySource.Any(s => s.Any(q => q.Count > 0)))
        {
            foreach (var source in bySource)
            {
                // One listing per product per pass, so a site with one product and forty posts of it
                // contributes its cheapest post now and the other thirty-nine only once every other
                // product on that site has had a turn.
                foreach (var product in source)
                {
                    if (product.Count == 0 || taken.Count >= max) continue;
                    taken.Add(product.Dequeue());
                }
                if (taken.Count >= max) break;
            }
        }

        return taken;
    }

    /// <summary>
    /// One site's listings as a queue per product, products in cheapest-first order and each
    /// product's own posts likewise — so the first pass over a site is its cheapest copy of every
    /// distinct thing it is selling, cheapest thing first.
    /// </summary>
    private static List<Queue<LocalSupplyListing>> ProductQueues(
        IEnumerable<LocalSupplyListing> listings, Func<LocalSupplyListing, string>? productKey)
    {
        static decimal Cost(LocalSupplyListing l) => l.IsFree ? 0m : l.Price ?? decimal.MaxValue;

        var ordered = listings.OrderBy(Cost).ToList();
        if (productKey is null)
            return [new Queue<LocalSupplyListing>(ordered)];

        // Built by walking `ordered` and appending, so the queues come out cheapest-product-first
        // as well as cheapest-listing-first within each.
        var queues = new List<Queue<LocalSupplyListing>>();
        var byKey = new Dictionary<string, Queue<LocalSupplyListing>>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in ordered)
        {
            // A key the selector couldn't build is not evidence that two listings are the same
            // thing, so each of those rows gets its own queue rather than collapsing into one that
            // would then be rationed as if it were a single product — which would ration exactly
            // the listings the app understands least. Same rule, and the same reason, as
            // GroupByProduct's own fallback.
            if (productKey(listing) is not { Length: > 0 } key)
            {
                queues.Add(new Queue<LocalSupplyListing>([listing]));
                continue;
            }

            if (!byKey.TryGetValue(key, out var queue))
            {
                queue = new Queue<LocalSupplyListing>();
                byKey[key] = queue;
                queues.Add(queue);
            }
            queue.Enqueue(listing);
        }

        return queues;
    }
}
