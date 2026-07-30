using System.Globalization;
using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Talks to the hosted sold-comps API (comps.php on inglisting.com, backed by the
// ing_sold_listings MariaDB table) over HTTPS. Returns candidate rows for a keyword query;
// the C# scoring in HostedMarketplaceRepository narrows them exactly like the local path.
public sealed class HostedMarketplaceClient(IHttpClientFactory httpFactory, CredentialsStore creds, ActionLog log)
{
    // How many candidates to pull before C# scoring narrows them — mirrors the local repo's
    // SqlCandidateLimit so both paths give the matcher the same breadth to work with.
    private const int CandidateLimit = 500;

    // ── Not asking the same question twice ───────────────────────────────────────────────────
    // A 500-row answer is ~470KB, and an Opportunity Finder scan prices dozens of products in a
    // burst. Measured 2026-07-30: a handful of those in quick succession and the host starts
    // answering 503 to everything, for about forty minutes. The whole board then reports "the
    // comparable-sales lookup failed" and every row loses its price — the failure that reads as
    // "none of these numbers work".
    //
    // The same query inside one scan (and across a re-scan minutes later) is the same answer, so
    // holding it costs nothing and removes most of the traffic that trips the limit.
    // Per-INSTANCE, not static. The client is a singleton so this still caches for the life of the
    // app, and a cache that outlives its instance is a cache that can hand one set of credentials'
    // results to another — which is also exactly what made five tests read each other's rows.
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);
    private readonly Dictionary<string, (DateTimeOffset At, List<MarketplaceComparableResult> Rows)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    /// <summary>
    /// How long to wait out a 503 before trying once more. The host answers instantly when it is
    /// refusing, so a failed call costs nothing and a single spaced retry rescues the common case
    /// of one unlucky request in a burst.
    /// </summary>
    private static readonly TimeSpan RetryAfter503 = TimeSpan.FromSeconds(3);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(creds.Get().MarketCompsApiUrl)
                             && !string.IsNullOrWhiteSpace(creds.Get().MarketCompsApiKey);

    public async Task<List<MarketplaceComparableResult>> FetchCandidatesAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsConfigured) return [];

        var key = query.Trim();
        await _cacheGate.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheFor)
                return hit.Rows;
        }
        finally { _cacheGate.Release(); }

        var rows = await FetchFromHostAsync(key, ct);

        // Only a real answer is worth keeping. Caching an empty list would turn one throttled
        // request into fifteen minutes of "no comps" for a product that has plenty.
        if (rows.Count > 0)
        {
            await _cacheGate.WaitAsync(ct);
            try { _cache[key] = (DateTimeOffset.UtcNow, rows); }
            finally { _cacheGate.Release(); }
        }

        return rows;
    }

    private async Task<List<MarketplaceComparableResult>> FetchFromHostAsync(string query, CancellationToken ct)
    {
        var first = await TryFetchAsync(query, ct);
        if (first.Rows is not null) return first.Rows;

        // 503 is the host refusing under load, not a permanent no. One spaced retry turns most of
        // them into an answer; anything else (401, 500, a timeout) is not helped by asking again.
        if (first.Status == 503)
        {
            try { await Task.Delay(RetryAfter503, ct); } catch (OperationCanceledException) { return []; }
            var second = await TryFetchAsync(query, ct);
            if (second.Rows is not null) return second.Rows;
            log.Add("Warning", "Sold-comps host is throttling",
                $"comps.php answered 503 twice for \"{query}\". Prices on this run come from whatever "
                + "else could be found; it usually clears on its own.");
        }

        return [];
    }

    private async Task<(int Status, List<MarketplaceComparableResult>? Rows)> TryFetchAsync(string query, CancellationToken ct)
    {
        var c = creds.Get();
        var url = $"{c.MarketCompsApiUrl.TrimEnd('/')}?key={Uri.EscapeDataString(c.MarketCompsApiKey)}" +
                  $"&q={Uri.EscapeDataString(query)}&limit={CandidateLimit}";

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 503 is logged by the caller, and only after the retry also fails — one throttled
                // request in a burst is noise, not news.
                if ((int)resp.StatusCode != 503)
                    log.Add("Warning", "Hosted comps API error", $"HTTP {(int)resp.StatusCode} for \"{query}\".");
                return ((int)resp.StatusCode, null);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            // A 200 with no results array is a real "nothing matched", not a failure to ask —
            // returning an empty list rather than null stops the caller retrying it as a throttle.
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return (200, []);

            var rows = new List<MarketplaceComparableResult>(results.GetArrayLength());
            foreach (var r in results.EnumerateArray())
            {
                var price = Dec(r, "Price");
                var shipping = Dec(r, "Shipping");
                rows.Add(new MarketplaceComparableResult
                {
                    ItemId     = Str(r, "ItemId") ?? "",
                    Title      = Str(r, "Title") ?? "",
                    SoldPrice  = price,
                    Shipping   = shipping,
                    TotalPrice = price + shipping,
                    Condition  = Str(r, "Condition"),
                    SoldDate   = TryParseSoldDate(Str(r, "SoldDate")),
                    Seller     = Str(r, "Seller"),
                    ItemUrl    = Str(r, "ItemUrl"),
                    ImageUrl   = Str(r, "ImageUrl"),
                    // Epid / buying_format / seller-feedback are not carried in the slim hosted
                    // dataset (they lived in the dropped RawJson column) — left at defaults; the
                    // matcher scores primarily on Title, so this only forgoes minor enrichment.
                });
            }
            return (200, rows);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Hosted comps API unavailable", ex.Message);
            return (0, null);
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // Price/Shipping arrive as JSON strings ("274.99") from PHP/PDO.
    private static decimal Dec(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0m;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var ds)) return ds;
        return 0m;
    }

    private static DateTime? TryParseSoldDate(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var p) ? p : null;
}

// Drop-in IMarketplaceRepository that sources candidates from the hosted API instead of the
// local SQLite Marketplace.db, then runs the identical ComparableMatcher scoring + pricing
// summary the local MarketplaceRepository uses. Selected in Program.cs when MarketCompsApiUrl is
// configured; otherwise the local repository is used.
public sealed class HostedMarketplaceRepository(
    HostedMarketplaceClient client, ActionLog log, LiquidityScoringService liquidity,
    ProductNormalizer normalizer, ComparableMatcher matcher) : IMarketplaceRepository
{
    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!client.IsConfigured) return false;
        // A cheap probe query — if it returns without throwing, the endpoint is reachable.
        try { await client.FetchCandidatesAsync("antminer", ct); return true; }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByPartNumberAsync(
        string partNumber, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(partNumber, BuildTarget(partNumber: partNumber, condition: filters?.Condition), filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByModelAsync(
        string model, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(model, BuildTarget(model: model, condition: filters?.Condition), filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByBrandAsync(
        string brand, string? additionalKeywords = null, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(
            string.IsNullOrWhiteSpace(additionalKeywords) ? brand : $"{brand} {additionalKeywords}",
            BuildTarget(brand: brand, model: additionalKeywords, condition: filters?.Condition), filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByKeywordAsync(
        string keywords, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(keywords, normalizer.Normalize(keywords), filters, limit, ct);

    public async Task<MarketplacePricingSummary> FindComparablesAsync(MarketplaceLookupRequest request, CancellationToken ct = default)
    {
        var filters = string.IsNullOrWhiteSpace(request.Condition) ? null : new MarketplaceSearchFilters { Condition = request.Condition };
        var limit = request.MaxComparables > 0 ? request.MaxComparables : 12;
        var target = BuildTarget(request.Brand, request.Model, request.PartNumber, request.Category, request.Condition, request.Keywords);

        (string Tier, string Query, IReadOnlyList<MarketplaceComparableResult> Results)? hit = null;

        // Same tiered lookup as the local repo: strongest identifier first, stop at the first hit.
        if (!string.IsNullOrWhiteSpace(request.PartNumber))
        {
            var r = await SearchAsync(request.PartNumber, target, filters, limit, ct);
            if (r.Count > 0) hit = ("part number", request.PartNumber, r);
        }
        if (hit is null && !string.IsNullOrWhiteSpace(request.Model))
        {
            var r = await SearchAsync(request.Model, target, filters, limit, ct);
            if (r.Count > 0) hit = ("model", request.Model, r);
        }
        if (hit is null && !string.IsNullOrWhiteSpace(request.Brand) && !string.IsNullOrWhiteSpace(request.Model))
        {
            var query = $"{request.Brand} {request.Model}".Trim();
            var r = await SearchAsync(query, target, filters, limit, ct);
            if (r.Count > 0) hit = ("brand + model", query, r);
        }
        if (hit is null && !string.IsNullOrWhiteSpace(request.Brand) && !string.IsNullOrWhiteSpace(request.Category))
        {
            var query = $"{request.Brand} {request.Category}".Trim();
            var r = await SearchAsync(query, target, filters, limit, ct);
            if (r.Count > 0) hit = ("brand + category", query, r);
        }
        if (hit is null && !string.IsNullOrWhiteSpace(request.Keywords))
        {
            var r = await SearchAsync(request.Keywords, target, filters, limit, ct);
            if (r.Count > 0) hit = ("keyword", request.Keywords, r);
        }

        var effectiveQuery = hit?.Query ?? request.PartNumber ?? request.Model ?? request.Brand ?? request.Keywords ?? "";
        var summary = MarketplacePricingCalculator.Summarize(
            effectiveQuery, hit?.Results ?? [], request.Condition,
            liquidityService: liquidity, activeCompetitionCount: request.ActiveCompetitionCount);

        log.Add(summary.MatchCount > 0 ? "Info" : "Warning", "Marketplace pricing lookup (hosted)",
            summary.MatchCount > 0
                ? $"Tier: {hit!.Value.Tier}; Query: \"{effectiveQuery}\"; Matches: {summary.MatchCount}; " +
                  $"Suggested: {summary.SuggestedResalePrice:C}; Confidence: {summary.ConfidenceScore}"
                : $"Query: \"{effectiveQuery}\"; No reliable hosted sold-history matches found.");

        return summary;
    }

    private NormalizedProduct BuildTarget(
        string? brand = null, string? model = null, string? partNumber = null,
        string? category = null, string? condition = null, string? keywords = null)
    {
        var target = !string.IsNullOrWhiteSpace(keywords) ? normalizer.Normalize(keywords) : new NormalizedProduct();
        if (!string.IsNullOrWhiteSpace(brand)) target.Brand = brand;
        if (!string.IsNullOrWhiteSpace(model)) target.Model = model;
        if (!string.IsNullOrWhiteSpace(partNumber)) target.PartNumber = partNumber;
        if (!string.IsNullOrWhiteSpace(category)) target.Category = category;
        if (!string.IsNullOrWhiteSpace(condition)) target.Condition = condition;
        return target;
    }

    // Fetch candidates from the hosted API, then score/filter/rank with the SAME components the
    // local repository uses — ComparableMatcher for identity-aware scoring + hard exclusions,
    // MarketplaceMatcher.DeduplicateByItemId, then top-N by match score then recency.
    private async Task<IReadOnlyList<MarketplaceComparableResult>> SearchAsync(
        string queryText, NormalizedProduct target, MarketplaceSearchFilters? filters, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(queryText)) return [];

        var importantWords = MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(queryText));
        if (importantWords.Count == 0) return []; // nothing but generic words — refuse to "match" on that

        var candidates = await client.FetchCandidatesAsync(queryText, ct);
        if (candidates.Count == 0) return [];

        var matches = candidates.Select(c => matcher.Match(target, c)).ToList();
        var accepted = matches.Where(m => !m.Excluded)
            .Select(m => { m.Comparable.MatchScore = m.MatchConfidence; return m.Comparable; })
            .ToList();

        var scored = MarketplaceMatcher.DeduplicateByItemId(accepted);

        if (filters?.SoldAfter is DateTime after)
            scored = scored.Where(r => r.SoldDate is null || r.SoldDate >= after).ToList();
        if (filters?.SoldBefore is DateTime before)
            scored = scored.Where(r => r.SoldDate is null || r.SoldDate <= before).ToList();
        if (filters?.MinPrice is decimal min)
            scored = scored.Where(r => r.SoldPrice >= min).ToList();
        if (filters?.MaxPrice is decimal max)
            scored = scored.Where(r => r.SoldPrice <= max).ToList();

        var final = scored
            .OrderByDescending(r => r.MatchScore)
            .ThenByDescending(r => r.SoldDate ?? DateTime.MinValue)
            .Take(limit)
            .ToList();

        log.Add("Info", "Marketplace search executed (hosted)",
            $"Query: \"{queryText}\"; Candidates: {candidates.Count}; Accepted: {accepted.Count}; Returned: {final.Count}");

        return final;
    }
}
