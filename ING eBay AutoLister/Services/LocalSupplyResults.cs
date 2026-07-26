using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The result handling every local-supply source needs and none of them should own: drop the
/// duplicates a site's own paging produces, drop the loosely-related padding a site adds when a
/// search is thin, and summarise the local ask-price spread.
///
/// Extracted from FacebookMarketplaceParser when Craigslist became the second source — both sites
/// pad thin results with "you might also like" items, and a price median that quietly includes
/// them is worse than no median at all.
/// </summary>
public static class LocalSupplyResults
{
    /// <summary>
    /// Keeps posts whose title carries at least one real word from the query — but if that would
    /// empty the result, returns everything and lets the seller judge, rather than reporting a
    /// false "no local supply" for an item that simply doesn't word-match.
    /// </summary>
    public static List<T> FilterByRelevance<T>(List<T> items, string query) where T : LocalSupplyListing
    {
        var tokens = Regex.Split(query?.ToLowerInvariant() ?? "", @"[^a-z0-9]+")
            .Where(t => t.Length >= 3)
            .ToList();

        if (tokens.Count == 0 || items.Count == 0) return items;

        var matched = items
            .Where(i => tokens.Any(t => i.Title.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return matched.Count > 0 ? matched : items;
    }

    /// <summary>
    /// First post wins per id. A virtualised grid re-renders tiles as it scrolls and a paged feed
    /// repeats posts between pages, so the same item arrives more than once from both sources.
    /// </summary>
    public static List<T> Dedupe<T>(IEnumerable<T> items) where T : LocalSupplyListing
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.Where(i => seen.Add($"{i.Source}{i.ItemId}")).ToList();
    }

    /// <summary>
    /// Min / median / max of the asks. Free items are excluded: they're real listings but a local
    /// floor of $0 describes nothing, and it would drag the median for every priced post with it.
    /// </summary>
    public static (decimal? Min, decimal? Median, decimal? Max) Summarize(IEnumerable<LocalSupplyListing> items)
    {
        var priced = items.Where(i => i.Price is > 0m).Select(i => i.Price!.Value).OrderBy(p => p).ToList();
        if (priced.Count == 0) return (null, null, null);

        var median = priced.Count % 2 == 1
            ? priced[priced.Count / 2]
            : (priced[priced.Count / 2 - 1] + priced[priced.Count / 2]) / 2m;

        return (priced[0], median, priced[^1]);
    }
}
