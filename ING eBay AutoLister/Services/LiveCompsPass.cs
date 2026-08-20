using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One product group on a scan, as the live sold-comps pass sees it: what stored comps made of it,
/// and whether a title-based live lookup could honestly change that.
/// </summary>
/// <param name="Key">The product-group key (the same key the pricing dictionary uses).</param>
/// <param name="Query">The fullest title in the group — what the lookup is run against.</param>
/// <param name="Tier">The evidence tier stored comps earned: confident | low | none.</param>
/// <param name="PreliminaryProfit">The group's best buy, costed on stored comps; null when unpriced.</param>
/// <param name="LocalAsk">The cheapest ask in the group — how much money is at stake with no opinion.</param>
/// <param name="Repriceable">
/// False for the rows a title lookup cannot reprice: a liquidation lot and a freebie are priced by
/// their own arithmetic, and a category the app refuses to value (cars, boats) would only be refused
/// again. The same rule the board's own "Get real sold price" button follows (app.js canReprice).
/// A row the eBay-comps provider looked up and found nothing for is NOT one of these — its
/// valuation is stamped "manual" too, but that is "no sold history", the very thing a live lookup
/// fixes, not a refusal.
/// </param>
public sealed record LiveCompsCandidate(
    string Key, string Query, string Tier, decimal? PreliminaryProfit, decimal LocalAsk, bool Repriceable);

/// <summary>What a live pass did, so the result can say it and the UI can show it.</summary>
public sealed class LiveCompsPassResult
{
    /// <summary>API calls actually made — the ones that cost a call and a slot of the allowance.</summary>
    public int LookupsUsed { get; set; }

    /// <summary>Group keys whose comps were deepened just now and must be re-priced.</summary>
    public List<string> Refreshed { get; } = [];

    /// <summary>Every lookup's outcome by group key, in the order they ran.</summary>
    public Dictionary<string, string> Outcomes { get; } = new(StringComparer.Ordinal);

    /// <summary>True when the pass ended before its targets did — allowance spent, another lookup in flight, the source down.</summary>
    public bool Stopped { get; set; }

    /// <summary>The sentence the seller reads when it stopped: what, why, and when it comes back. Empty on a clean run.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// The live half of the sold-comps path, for a whole scan at once.
/// </summary>
/// <remarks>
/// <para>
/// Every row on the deals boards is priced against sold comps in two steps: the stored database
/// first, then — for the rows that leaves thin — a live eBay lookup that files fresh sold rows into
/// that same database and a re-read. The eBay scanner got both halves: the browser fetches live
/// comps for the search term before the scan, then deepens the top estimates row by row. A
/// Facebook Marketplace feed got only the first. It has no single search term to look up first,
/// its cards had no per-row path, and on the hosted build there is no browser to run one from —
/// so a drill, a couch or a golf cart was priced against whatever the stored database happened to
/// hold, which for ordinary local goods is nothing, and the card said "no sold data" about an
/// item eBay sells every day.
/// </para>
/// <para>
/// This runs that second half inside the scan, for every source, off the same
/// <see cref="LiveCompsLookup"/> the browser uses — one call per product, the same daily allowance,
/// the same 24-hour cache — and then the caller re-reads stored comps exactly as it did in pass 1.
/// A row refreshed here is therefore costed by the identical code a browser-refreshed row is;
/// there is no second pricing path and nothing here touches a price.
/// </para>
/// <para>
/// It is bounded the way the board's own automatic pass is: a few products per scan
/// (<see cref="DefaultBudget"/>, the same three the board deepens after a scan), the ones where a
/// lookup most changes a decision first, and it stops the moment the lookup refuses — an
/// allowance that is spent for the day is spent for every row after it. Both halves are pure
/// except for the lookup delegate, which is what makes them testable without an API in front.
/// </para>
/// </remarks>
public static class LiveCompsPass
{
    /// <summary>
    /// Products per scan. Three, like the board's own auto-deepen pass: the default daily allowance
    /// is ten, and a single feed of forty cards must not be able to spend it before the seller has
    /// looked at anything.
    /// </summary>
    public const int DefaultBudget = 3;

    /// <summary>The most a caller may ask for — a whole default day's allowance, never more.</summary>
    public const int MaxBudget = 10;

    /// <summary>Outcomes after which asking again would get the same answer: stop, and say why.</summary>
    private static readonly HashSet<string> Refusals =
        new(StringComparer.Ordinal) { "busy", "rate_limited", "unavailable", "error", "timeout" };

    /// <summary>A candidate built from the row stored comps produced — the very row the board would show.</summary>
    public static LiveCompsCandidate Candidate(string key, string query, LocalArbitrageOpportunity preliminary, decimal localAsk) =>
        new(key, query,
            string.IsNullOrWhiteSpace(preliminary.EvidenceTier) ? LocalArbitrageEvidence.None : preliminary.EvidenceTier,
            preliminary.NetProfit, localAsk,
            Repriceable: preliminary.Liquidation is null && preliminary.Freebie is null
                         && !ValuationRefused(preliminary.Valuation));

    /// <summary>
    /// Whether a valuation was refused by a provider that does not price off title comps — as
    /// opposed to priced by the comps provider, or looked up by it and found empty. Only the first
    /// would refuse a live lookup's rows again; the last is exactly what the lookup is for.
    /// </summary>
    public static bool ValuationRefused(ResaleValuation? valuation) =>
        valuation is { Status: ValuationStatuses.Manual }
        && !string.Equals(valuation.ProviderId, ResaleValuationProviders.EbayComps, StringComparison.Ordinal);

    /// <summary>
    /// Which product groups get a live lookup, in order, within <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// Never a confident group — it already has the comps — and never one a title lookup can't
    /// reprice. Among the rest, the order is where a fresh answer is worth most: an estimate that
    /// already looks profitable (the row a seller is about to act on, and the one most worth being
    /// sure of), then the groups with no comps at all by how much money is asked for them (no
    /// opinion on a $900 item is a bigger gap than no opinion on a $9 one), then the estimates
    /// that look like losses. Pure: the same input always picks the same products.
    /// </remarks>
    public static List<string> SelectTargets(IEnumerable<LiveCompsCandidate> candidates, int budget)
    {
        if (budget <= 0) return [];

        var pool = candidates
            .Where(c => c.Repriceable && !string.IsNullOrWhiteSpace(c.Query))
            .Where(c => !string.Equals(c.Tier, LocalArbitrageEvidence.Confident, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var promising = pool
            .Where(c => c.PreliminaryProfit is > 0)
            .OrderByDescending(c => c.PreliminaryProfit!.Value);

        var unpriced = pool
            .Where(c => string.Equals(c.Tier, LocalArbitrageEvidence.None, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LocalAsk);

        var doubtful = pool
            .Where(c => c.PreliminaryProfit is not > 0
                        && !string.Equals(c.Tier, LocalArbitrageEvidence.None, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.LocalAsk);

        return promising.Concat(unpriced).Concat(doubtful)
            .Select(c => c.Key)
            .Distinct(StringComparer.Ordinal)
            .Take(budget)
            .ToList();
    }

    /// <summary>
    /// Runs the lookups one after another and reports which groups now have fresher comps.
    /// </summary>
    /// <remarks>
    /// Sequential because the lookup itself is: a second call while one is in flight is answered
    /// "busy", not queued. A run that found rows (<c>ok</c>) marks its group for re-pricing. One
    /// that found none (<c>empty</c>) spent a call and proved a negative, which is worth knowing
    /// and worth nothing to re-read. One fetched earlier today (<c>fresh</c>) was already in the
    /// database when pass 1 ran. Any refusal or failure ends the pass with the lookup's own
    /// sentence: the allowance is per day, so the next product would only be told the same thing,
    /// and a source that just failed is not one to spend the rest of a tiny budget on.
    /// </remarks>
    public static async Task<LiveCompsPassResult> RunAsync(
        IReadOnlyList<(string Key, string Query)> targets,
        Func<string, CancellationToken, Task<LiveCompsRun>> fetch,
        CancellationToken ct = default)
    {
        var result = new LiveCompsPassResult();

        foreach (var (key, query) in targets)
        {
            ct.ThrowIfCancellationRequested();

            var run = await fetch(query, ct).ConfigureAwait(false);
            var outcome = run?.Outcome ?? "";
            result.Outcomes[key] = outcome;

            switch (outcome)
            {
                case "ok":
                    result.LookupsUsed++;
                    result.Refreshed.Add(key);
                    break;
                case "empty":
                case "error":
                case "timeout":
                    // The API was asked and billed the attempt, whatever it answered.
                    result.LookupsUsed++;
                    break;
            }

            if (Refusals.Contains(outcome))
            {
                result.Stopped = true;
                result.Note = run?.Message ?? "";
                return result;
            }
        }

        return result;
    }
}
