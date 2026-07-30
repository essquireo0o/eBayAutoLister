using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// One result from either a cache hit or a real Terapeak scrape, plus how much weight it should
// carry given its age — full weight inside 30 days, reduced 31-90 days, progressively less beyond.
public class TerapeakMarketResult
{
    public SoldCompsResult Data { get; set; } = new();
    public DateTime ScrapedAtUtc { get; set; }
    public bool FromCache { get; set; }
    public double FreshnessWeight { get; set; }
}

/// <summary>
/// Why a Terapeak lookup produced what it produced. The distinction that matters is the last three
/// against <see cref="NoData"/>: those mean "Terapeak had nothing to say about this item", which is
/// real information, and the rest mean "Terapeak did not answer", which is not information at all.
/// </summary>
public enum TerapeakOutcome
{
    /// <summary>Served from the shared cache. No browser was launched.</summary>
    Cached,

    /// <summary>A real scrape ran and parsed.</summary>
    Scraped,

    /// <summary>Cache miss and the caller did not authorise a scrape. Says nothing about the item.</summary>
    NotAttempted,

    /// <summary>Terapeak has never been connected on this machine.</summary>
    NotConnected,

    /// <summary>eBay bounced the saved session to sign-in or a subscription wall. Needs the seller.</summary>
    SessionExpired,

    /// <summary>eBay's bot check answered instead of the page. Usually clears on its own.</summary>
    Blocked,

    /// <summary>The lookup broke — no browser, a crash, a timeout.</summary>
    Failed,

    /// <summary>Terapeak answered, and genuinely has no sold history for this product.</summary>
    NoData,
}

/// <summary>
/// A Terapeak lookup and the reason behind it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GetAsync"/> returns only the result and answers every failure with <c>null</c>, which
/// is the right shape for the pricing maths — a source that did not answer must contribute nothing
/// rather than contribute a zero. But <c>null</c> alone is why a dead session was invisible for so
/// long: the estimator saw "no Terapeak data", weighted the local comps at 100%, and produced a
/// confident number with nothing anywhere saying the second source had been silently dropped.
/// </para>
/// <para>
/// So the reason travels beside the result, and callers that show a price to a person read it to say
/// which source the number actually came from.
/// </para>
/// </remarks>
public sealed record TerapeakLookup(TerapeakMarketResult? Result, TerapeakOutcome Outcome, string Reason = "")
{
    /// <summary>True when the numbers are real Terapeak sold data (from a scrape or the cache).</summary>
    public bool HasData => Result is not null;

    /// <summary>
    /// True when the absence of data is about the connection rather than about the item. A caller
    /// must not report "no recent sales" on the strength of one of these.
    /// </summary>
    public bool IsConnectionProblem =>
        Outcome is TerapeakOutcome.NotConnected or TerapeakOutcome.SessionExpired
                or TerapeakOutcome.Blocked or TerapeakOutcome.Failed;

    /// <summary>True when the fix is the seller reconnecting Terapeak, and nothing else will do.</summary>
    public bool NeedsReconnect => Outcome == TerapeakOutcome.SessionExpired;
}

// Thin orchestration over the existing TerapeakService (browser scrape) + TerapeakPriceCache
// (SQLite persistence) pair — centralizes what used to be duplicated inline in Program.cs
// (GetOrScrapePricingAsync) so both the Opportunity Finder search and the Supplier File Analyzer
// share one implementation, and adds a normalized cache-key signature plus freshness weighting.
//
// SAFETY: Terapeak is a real logged-in browser scrape, not an API — this class NEVER initiates a
// scrape on its own. GetAsync only scrapes when the caller explicitly passes allowRealScrape=true,
// and even then only on a cache miss. Callers are responsible for rationing how many times per
// search they set allowRealScrape=true (see Program.cs's terapeakRecheckLimit) — this class must
// never be changed to pre-fetch, bulk-scan, or background-refresh Terapeak data.
public sealed class TerapeakMarketService(TerapeakService terapeak, TerapeakPriceCache cache, ActionLog log)
{
    // A normalized search signature (brand+model+major-spec+condition+category), not the raw
    // query string — two searches that mean the same product but differ in wording/word order
    // hit the same cache entry instead of each paying for their own scrape.
    public static string BuildCacheKey(NormalizedProduct product)
    {
        var parts = new[] { product.Brand, product.Model, product.Capacity, product.Generation, product.Condition, product.Category }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim().ToLowerInvariant())
            .ToList();

        return parts.Count > 0 ? string.Join('|', parts) : MarketplaceMatcher.Normalize(product.RawText);
    }

    /// <summary>
    /// The lookup, with nothing but the numbers. Every failure is <c>null</c>, so a source that did
    /// not answer contributes nothing to a blend rather than contributing a zero.
    /// </summary>
    public async Task<TerapeakMarketResult?> GetAsync(
        NormalizedProduct product, string rawQueryForScrape, bool allowRealScrape,
        TimeSpan? maxAge = null, CancellationToken ct = default) =>
        (await LookupAsync(product, rawQueryForScrape, allowRealScrape, maxAge, ct)).Result;

    /// <summary>
    /// The same lookup, with the reason attached. Callers that put a price in front of a person use
    /// this one, so the screen can name the source the number came from — and can tell a dead
    /// session apart from an item that genuinely has no sold history.
    /// </summary>
    public async Task<TerapeakLookup> LookupAsync(
        NormalizedProduct product, string rawQueryForScrape, bool allowRealScrape,
        TimeSpan? maxAge = null, CancellationToken ct = default)
    {
        var key = BuildCacheKey(product);
        var age = maxAge ?? TimeSpan.FromHours(48);
        var now = DateTime.UtcNow;

        var cached = cache.TryGet(key, age);
        if (cached is not null)
        {
            return new TerapeakLookup(new TerapeakMarketResult
            {
                Data = new SoldCompsResult
                {
                    Query = rawQueryForScrape, Average = cached.Average, Median = cached.Median,
                    AvgShipping = cached.AvgShipping, SellThroughPercent = cached.SellThroughPercent,
                },
                ScrapedAtUtc = cached.ScrapedAtUtc,
                FromCache = true,
                FreshnessWeight = FreshnessWeight(cached.ScrapedAtUtc, now),
            }, TerapeakOutcome.Cached);
        }

        // Tested first because it is free. Every scan in this app runs a cache-only pre-pass over
        // every product before it spends a single scrape, and inspecting the session file for each
        // of several hundred products just to reach the same "no" is work nobody asked for.
        if (!allowRealScrape)
            return new TerapeakLookup(null, TerapeakOutcome.NotAttempted,
                "Terapeak wasn't checked for this one — a real lookup drives a browser, so they're rationed per search.");

        // Read as a state machine, not as File.Exists: a session eBay has stopped accepting is a
        // different answer from one that was never made, and only one of them has a Reconnect button.
        var session = terapeak.Session;
        if (session.NeedsReconnect)
            return new TerapeakLookup(null, TerapeakOutcome.SessionExpired,
                $"{session.Reason} Reconnect Terapeak in Settings to use its sold data again.");

        if (!session.CanSearch)
            return new TerapeakLookup(null, TerapeakOutcome.NotConnected,
                "Terapeak isn't connected, so its sold data wasn't part of this price.");

        // Randomized delay so request timing doesn't look like a metronome — preserved exactly
        // from the prior GetOrScrapePricingAsync anti-bot-detection behavior. A real scrape only
        // ever happens here, once, for a product actually being analyzed right now.
        await Task.Delay(TimeSpan.FromSeconds(Random.Shared.Next(2, 10)), ct);

        TerapeakScrapeResult scrape;
        try
        {
            scrape = await terapeak.ScrapeAsync(rawQueryForScrape, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Terapeak scrape failed", ex.Message);
            return new TerapeakLookup(null, TerapeakOutcome.Failed,
                "Terapeak couldn't be reached for this one, so its sold data isn't part of this price.");
        }

        // Nothing below may cache, and nothing below may be reported as "no sold history". A page
        // this app was bounced off has no comps on it by definition, and treating that as a real
        // sample of zero is exactly how a dead session became a confident price.
        switch (scrape.Status)
        {
            case "ok":
                break;

            case "session_expired":
                return new TerapeakLookup(null, TerapeakOutcome.SessionExpired, scrape.Reason);

            case "challenged":
                return new TerapeakLookup(null, TerapeakOutcome.Blocked, scrape.Reason);

            case "not_connected":
                return new TerapeakLookup(null, TerapeakOutcome.NotConnected, scrape.Reason);

            default:
                log.Add("Warning", "Terapeak lookup failed", scrape.Error ?? scrape.Reason);
                return new TerapeakLookup(null, TerapeakOutcome.Failed,
                    "Terapeak couldn't be reached for this one, so its sold data isn't part of this price.");
        }

        var parsed = ParseTerapeakBodyText(scrape.BodyText, rawQueryForScrape);
        if (parsed is null)
            return new TerapeakLookup(null, TerapeakOutcome.NoData,
                "Terapeak has no recent sold history for this one.");

        cache.Set(key, parsed.Average, parsed.Median, parsed.AvgShipping, parsed.SellThroughPercent);
        return new TerapeakLookup(
            new TerapeakMarketResult { Data = parsed, ScrapedAtUtc = now, FromCache = false, FreshnessWeight = 1.0 },
            TerapeakOutcome.Scraped);
    }

    private static double FreshnessWeight(DateTime scrapedAtUtc, DateTime nowUtc)
    {
        var ageDays = (nowUtc - scrapedAtUtc).TotalDays;
        return ageDays switch
        {
            <= 30 => 1.0,
            <= 90 => 0.7,
            <= 180 => 0.4,
            _ => 0.2,
        };
    }

    // Text parse of the Seller Hub Research page, tuned against a real logged-in capture (see
    // /api/terapeak/debug-scrape). innerText renders each stat tile as "<value>\n<label>", e.g.
    // "$64.31\nAvg sold price", and each result-table row as "...\n$14.90\nFixed price\n...".
    // Moved here (was Program.cs's ParseTerapeakBodyText local function) so both the Opportunity
    // Finder search and the Supplier File Analyzer share one parser instead of each having a copy.
    // Public so the standalone /api/sold-comps and /api/terapeak/debug-scrape endpoints (outside
    // the Opportunity Finder pipeline) can still parse a raw scrape without duplicating this.
    public static SoldCompsResult? ParseTerapeakBodyText(string text, string query)
    {
        var result = new SoldCompsResult { Query = query };

        var avgMatch = Regex.Match(text, @"\$\s*([\d,]+\.\d{2})\s*\n\s*Avg sold price", RegexOptions.IgnoreCase);
        if (avgMatch.Success && decimal.TryParse(avgMatch.Groups[1].Value.Replace(",", ""), out var avg))
            result.Average = avg;

        var rangeMatch = Regex.Match(text, @"\$\s*([\d,]+\.\d{2})\s*-\s*\$\s*([\d,]+\.\d{2})\s*\n\s*Sold price range", RegexOptions.IgnoreCase);
        if (rangeMatch.Success)
        {
            if (decimal.TryParse(rangeMatch.Groups[1].Value.Replace(",", ""), out var min)) result.Min = min;
            if (decimal.TryParse(rangeMatch.Groups[2].Value.Replace(",", ""), out var max)) result.Max = max;
        }

        // "72%\nSell-through" — Terapeak shows "-" instead of a percentage when it can't compute
        // one for this query, which TryParse below just leaves as null (no data) rather than 0%.
        var sellThroughMatch = Regex.Match(text, @"([\d.]+)%\s*\n\s*Sell-through", RegexOptions.IgnoreCase);
        if (sellThroughMatch.Success && decimal.TryParse(sellThroughMatch.Groups[1].Value, out var sellThrough))
            result.SellThroughPercent = sellThrough;

        var shippingMatch = Regex.Match(text, @"\$\s*([\d,]+\.\d{2})\s*\n\s*Avg shipping", RegexOptions.IgnoreCase);
        if (shippingMatch.Success && decimal.TryParse(shippingMatch.Groups[1].Value.Replace(",", ""), out var avgShip))
            result.AvgShipping = avgShip;

        // Each matching product row shows its own avg sold price — collect them into a real
        // per-listing distribution so we get an actual median (the page-level summary above only
        // gives a single blended average, which the profit calc doesn't want).
        var rowPrices = Regex.Matches(text, @"\$\s*([\d,]+\.\d{2})\s*\n\s*(?:Fixed price|Auction|Best Offer)", RegexOptions.IgnoreCase)
            .Select(m => decimal.TryParse(m.Groups[1].Value.Replace(",", ""), out var p) ? p : (decimal?)null)
            .Where(p => p.HasValue)
            .Select(p => p!.Value)
            .OrderBy(p => p)
            .ToList();

        if (rowPrices.Count > 0)
        {
            result.Count = rowPrices.Count;
            result.Median = rowPrices.Count % 2 == 1
                ? rowPrices[rowPrices.Count / 2]
                : (rowPrices[rowPrices.Count / 2 - 1] + rowPrices[rowPrices.Count / 2]) / 2m;
        }

        return result.Count > 0 || result.Average > 0 ? result : null;
    }
}
