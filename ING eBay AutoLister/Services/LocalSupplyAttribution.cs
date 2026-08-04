using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Traces each site's listings from the search through to the money, so a source chip can say what
/// its site was actually worth rather than only how many rows it returned.
/// </summary>
/// <remarks>
/// <para>
/// The multi-source scan reports per-source counts from the <b>search</b> and then builds the board
/// through four filters that all drop rows silently and unevenly across sites: listings with no
/// price, the not-the-item screen, the shared analysis cap, and pricing that finds no comps. A
/// seller reading "Craigslist · 48 results · Facebook · 6 results" over a table of eleven rows has
/// no way to tell which site those eleven came from, and every wrong guess costs them the same
/// thing — they untick the site that was actually paying.
/// </para>
/// <para>
/// This is deliberately arithmetic over lists the caller already has rather than a counter threaded
/// through the pipeline: nothing upstream has to remember to increment anything, and every rule is
/// checkable without a search, a browser or a comps database.
/// </para>
/// </remarks>
public static class LocalSupplyAttribution
{
    /// <summary>
    /// Fills in each outcome's post-search figures. Sites that never answered are left alone —
    /// zeroes against a disconnected Facebook are not a judgement on Facebook.
    /// </summary>
    /// <param name="outcomes">The per-source outcomes to annotate, in place.</param>
    /// <param name="usable">
    /// Everything that survived the screening and was eligible for the cap — the denominator for
    /// <see cref="LocalSupplySourceOutcome.Capped"/>.
    /// </param>
    /// <param name="analyzed">What the cap actually let through, and so what was priced.</param>
    /// <param name="ranked">The finished board.</param>
    public static void Apply(
        IEnumerable<LocalSupplySourceOutcome> outcomes,
        IEnumerable<LocalSupplyListing> usable,
        IEnumerable<LocalSupplyListing> analyzed,
        IEnumerable<LocalArbitrageOpportunity> ranked)
    {
        var usableBySource = Tally(usable.Select(l => l.Source));
        var analyzedBySource = Tally(analyzed.Select(l => l.Source));
        var rows = ranked.ToList();

        foreach (var outcome in outcomes)
        {
            var mine = rows.Where(r => Same(r.Source, outcome.Id)).ToList();
            var profitable = mine.Where(r => r.NetProfit is > 0).ToList();

            outcome.Analyzed = Count(analyzedBySource, outcome.Id);
            outcome.Ranked = mine.Count;
            outcome.ProfitableCount = profitable.Count;
            outcome.PotentialProfit = Math.Round(profitable.Sum(r => r.NetProfit!.Value), 2);
            // Only ever true when the site had more to offer than the scan looked at. A site whose
            // every usable listing was priced was not capped however many the cap was.
            outcome.Capped = Count(usableBySource, outcome.Id) > outcome.Analyzed;
        }
    }

    /// <summary>
    /// The site that made the seller the most money on this board, or null when none of them did.
    /// </summary>
    /// <remarks>
    /// Ranked on money rather than on row count, and it is not the same answer: a site with one
    /// $340 flip beats a site with nine $6 ones, and the seller's Saturday is finite. Null rather
    /// than "the least bad site" when nothing on the board is profitable — naming a winner among
    /// losers would read as a recommendation to go and buy from it.
    /// </remarks>
    public static LocalSupplySourceOutcome? BestEarner(IEnumerable<LocalSupplySourceOutcome> outcomes) =>
        outcomes
            .Where(o => o.PotentialProfit > 0)
            .OrderByDescending(o => o.PotentialProfit)
            .ThenByDescending(o => o.ProfitableCount)
            .ThenBy(o => o.Label, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

    private static Dictionary<string, int> Tally(IEnumerable<string> sources)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in sources)
        {
            var key = source ?? "";
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    private static int Count(Dictionary<string, int> counts, string id) =>
        counts.TryGetValue(id ?? "", out var n) ? n : 0;

    // Source ids are matched the same way everywhere else in this stack matches them — a listing
    // carrying "Craigslist" and an outcome carrying "craigslist" are the same site.
    private static bool Same(string a, string b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
}
