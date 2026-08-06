using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

// Read-only access to the externally-maintained SoldListings table (see ExternalMarketplaceDb).
// Every method here only ever runs SELECT statements — nothing creates, renames, deletes, or
// overwrites the database, the table, or any row in it.
public interface IMarketplaceRepository
{
    Task<bool> IsAvailableAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MarketplaceComparableResult>> SearchByPartNumberAsync(
        string partNumber, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<MarketplaceComparableResult>> SearchByModelAsync(
        string model, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<MarketplaceComparableResult>> SearchByBrandAsync(
        string brand, string? additionalKeywords = null, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default);

    Task<IReadOnlyList<MarketplaceComparableResult>> SearchByKeywordAsync(
        string keywords, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default);

    // Tiered lookup, strongest identifier first: exact Part Number, then exact Model, then
    // Brand + Model, then Brand + Category, then bare Keywords — stopping at the first tier
    // with a reliable match, then returns stats + suggested price computed from that tier's
    // comparables. Never throws for "no data" conditions — a request that finds nothing just
    // comes back with MatchCount = 0. Normally fed from a ProductIdentity (see
    // ProductIdentityExtractor) rather than built by hand.
    Task<MarketplacePricingSummary> FindComparablesAsync(MarketplaceLookupRequest request, CancellationToken ct = default);
}

// NOTE on SoldListings' Brand/Model/Category columns: they are always NULL in the real,
// externally-maintained production database (confirmed against C:\INGListing\Data\Marketplace.db
// directly — 0 of ~396k rows have any of the three set). This repository has never queried those
// columns — every tier below already narrows candidates via a Title LIKE search built from the
// caller's identity fields, then scores each candidate's own Title (+ RawJson) in C#. What WAS
// missing is real identity-aware scoring: candidates were ranked with MarketplaceMatcher's coarse
// word-overlap bands, with no negative-keyword rejection, no quantity/generation/capacity conflict
// detection, and no use of RawJson (a SerpApi eBay-scrape blob with real condition/buying-format/
// seller/epid data, unlike the empty flat columns). ComparableMatcher/ProductNormalizer now do that
// scoring instead of MarketplaceMatcher.Score's simple bands.
public sealed class MarketplaceRepository(
    ExternalMarketplaceDb db, ActionLog log, LiquidityScoringService liquidity,
    ProductNormalizer normalizer, ComparableMatcher matcher) : IMarketplaceRepository
{
    // Rows pulled from SQL before C# scoring narrows them down — bounds the cost of a LIKE scan
    // against a table that "may contain millions of rows" regardless of how common the words are.
    private const int SqlCandidateLimit = 500;

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (!db.DatabaseFileExists) return false;
        try
        {
            using var connection = await db.OpenReadOnlyAsync(ct);
            return await TableExistsAsync(connection, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Marketplace database unavailable", ex.Message);
            return false;
        }
    }

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByPartNumberAsync(
        string partNumber, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(partNumber, BuildTarget(partNumber: partNumber, condition: filters?.Condition), strict: true, filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByModelAsync(
        string model, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(model, BuildTarget(model: model, condition: filters?.Condition), strict: true, filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByBrandAsync(
        string brand, string? additionalKeywords = null, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(
            string.IsNullOrWhiteSpace(additionalKeywords) ? brand : $"{brand} {additionalKeywords}",
            BuildTarget(brand: brand, model: additionalKeywords, condition: filters?.Condition),
            strict: true, filters, limit, ct);

    public Task<IReadOnlyList<MarketplaceComparableResult>> SearchByKeywordAsync(
        string keywords, MarketplaceSearchFilters? filters = null, int limit = 12, CancellationToken ct = default) =>
        SearchAsync(keywords, normalizer.Normalize(keywords), strict: false, filters, limit, ct);

    public async Task<MarketplacePricingSummary> FindComparablesAsync(MarketplaceLookupRequest request, CancellationToken ct = default)
    {
        var filters = string.IsNullOrWhiteSpace(request.Condition) ? null : new MarketplaceSearchFilters { Condition = request.Condition };
        var limit = request.MaxComparables > 0 ? request.MaxComparables : 12;
        var target = BuildTarget(request.Brand, request.Model, request.PartNumber, request.Category, request.Condition, request.Keywords);

        (string Tier, string Query, IReadOnlyList<MarketplaceComparableResult> Results)? hit = null;

        if (!db.DatabaseFileExists)
        {
            log.Add("Warning", "Marketplace lookup skipped", $"Database file not found at {db.DatabasePath}.");
        }
        else
        {
            // One connection shared across every tier this lookup tries, instead of each tier
            // opening (and PRAGMA/table-checking) its own — pricing one item that doesn't match
            // on an early tier used to open up to 5 separate SQLite connections.
            using var connection = await db.OpenReadOnlyAsync(ct);
            if (!await TableExistsAsync(connection, ct))
            {
                log.Add("Warning", "Marketplace lookup skipped", "SoldListings table not found.");
            }
            else
            {
                // 1) Exact Part Number — the single most specific identifier a product can have.
                if (!string.IsNullOrWhiteSpace(request.PartNumber))
                {
                    var results = await SearchAsync(request.PartNumber, target, strict: true, filters, limit, connection, ct);
                    if (results.Count > 0) hit = ("part number", request.PartNumber, results);
                }
                // 2) Exact Model.
                if (hit is null && !string.IsNullOrWhiteSpace(request.Model))
                {
                    var results = await SearchAsync(request.Model, target, strict: true, filters, limit, connection, ct);
                    if (results.Count > 0) hit = ("model", request.Model, results);
                }
                // 3) Brand + Model — narrower than model alone (rules out a same-numbered model
                // from a different manufacturer) when both are known.
                if (hit is null && !string.IsNullOrWhiteSpace(request.Brand) && !string.IsNullOrWhiteSpace(request.Model))
                {
                    var query = $"{request.Brand} {request.Model}".Trim();
                    var results = await SearchAsync(query, target, strict: true, filters, limit, connection, ct);
                    if (results.Count > 0) hit = ("brand + model", query, results);
                }
                // 4) Brand + Category — no model known, but knowing the brand and what kind of
                // thing it is still beats a completely open-ended keyword search.
                if (hit is null && !string.IsNullOrWhiteSpace(request.Brand) && !string.IsNullOrWhiteSpace(request.Category))
                {
                    var query = $"{request.Brand} {request.Category}".Trim();
                    var results = await SearchAsync(query, target, strict: true, filters, limit, connection, ct);
                    if (results.Count > 0) hit = ("brand + category", query, results);
                }
                // 5) Generic keywords — last resort.
                if (hit is null && !string.IsNullOrWhiteSpace(request.Keywords))
                {
                    var results = await SearchAsync(request.Keywords, target, strict: false, filters, limit, connection, ct);
                    if (results.Count > 0) hit = ("keyword", request.Keywords, results);
                }
            }
        }

        var effectiveQuery = hit?.Query ?? request.PartNumber ?? request.Model ?? request.Brand ?? request.Keywords ?? "";
        var summary = MarketplacePricingCalculator.Summarize(
            effectiveQuery, hit?.Results ?? [], request.Condition,
            liquidityService: liquidity, activeCompetitionCount: request.ActiveCompetitionCount);

        log.Add(summary.MatchCount > 0 ? "Info" : "Warning", "Marketplace pricing lookup",
            summary.MatchCount > 0
                ? $"Tier: {hit!.Value.Tier}; Query: \"{effectiveQuery}\"; Matches: {summary.MatchCount}; " +
                  $"Suggested: {summary.SuggestedResalePrice:C}; Confidence: {summary.ConfidenceScore}"
                : $"Query: \"{effectiveQuery}\"; No reliable local sold-history matches found.");

        return summary;
    }

    // Builds the NormalizedProduct ComparableMatcher scores candidates against. Prefers already-
    // extracted identity fields (typically from ProductIdentityExtractor upstream) over re-deriving
    // them from Keywords text, since the caller's explicit fields are the more authoritative source
    // when both are available.
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

    // ── Core search ──────────────────────────────────────────────────────────

    // Standalone entry point (used by the public SearchByXAsync methods, and tests) — opens and
    // tears down its own connection for a single, one-off search.
    private async Task<IReadOnlyList<MarketplaceComparableResult>> SearchAsync(
        string sqlQueryText, NormalizedProduct target, bool strict, MarketplaceSearchFilters? filters, int limit, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sqlQueryText)) return [];

        if (!db.DatabaseFileExists)
        {
            log.Add("Warning", "Marketplace lookup skipped", $"Database file not found at {db.DatabasePath}.");
            return [];
        }

        using var connection = await db.OpenReadOnlyAsync(ct);

        if (!await TableExistsAsync(connection, ct))
        {
            log.Add("Warning", "Marketplace lookup skipped", "SoldListings table not found.");
            return [];
        }

        return await SearchAsync(sqlQueryText, target, strict, filters, limit, connection, ct);
    }

    // Shared-connection core — lets FindComparablesAsync run all of its lookup tiers against one
    // already-open connection instead of each tier opening its own.
    private async Task<IReadOnlyList<MarketplaceComparableResult>> SearchAsync(
        string sqlQueryText, NormalizedProduct target, bool strict, MarketplaceSearchFilters? filters, int limit, SqliteConnection connection, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sqlQueryText)) return [];

        var importantWords = MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(sqlQueryText));
        if (importantWords.Count == 0) return []; // nothing but generic words — refuse to "match" on that

        var sw = Stopwatch.StartNew();
        try
        {
            var rows = await FetchCandidatesAsync(connection, sqlQueryText, importantWords, strict, filters, ct);

            var candidates = rows.Select(BuildResult).ToList();
            var matches = candidates.Select(c => matcher.Match(target, c)).ToList();
            var rejected = matches.Count(m => m.Excluded);
            var accepted = matches.Where(m => !m.Excluded)
                .Select(m => { m.Comparable.MatchScore = m.MatchConfidence; return m.Comparable; })
                .ToList();

            var scored = MarketplaceMatcher.DeduplicateByItemId(accepted);

            if (filters?.SoldAfter is DateTime after)
                scored = scored.Where(r => r.SoldDate is null || r.SoldDate >= after).ToList();
            if (filters?.SoldBefore is DateTime before)
                scored = scored.Where(r => r.SoldDate is null || r.SoldDate <= before).ToList();

            var final = scored
                .OrderByDescending(r => r.MatchScore)
                .ThenByDescending(r => r.SoldDate ?? DateTime.MinValue)
                .Take(limit)
                .ToList();

            sw.Stop();
            log.Add("Info", "Marketplace search executed",
                $"Query: \"{sqlQueryText}\" ({(strict ? "strict" : "broad")}); Candidates: {rows.Count}; " +
                $"Rejected by matcher: {rejected}; Accepted: {accepted.Count}; Returned: {final.Count}; Duration: {sw.ElapsedMilliseconds}ms");

            return final;
        }
        catch (OperationCanceledException)
        {
            throw; // cooperative cancellation — let the caller see it, not a "failed" log entry
        }
        catch (SqliteException ex)
        {
            // Covers "database is locked", "no such table", permission errors, etc. — all of
            // this must degrade to "no local data" rather than take down the caller.
            sw.Stop();
            log.Add("Warning", "Marketplace lookup failed", ex.Message);
            return [];
        }
        catch (Exception ex)
        {
            sw.Stop();
            log.Add("Warning", "Marketplace lookup failed", ex.Message);
            return [];
        }
    }

    private readonly record struct CandidateRow(
        string ItemId, string Title, decimal Price, decimal Shipping,
        string? Condition, string? Seller, string? SoldDate, string? ItemUrl, string? ImageUrl, string? RawJson);

    private static async Task<List<CandidateRow>> FetchCandidatesAsync(
        SqliteConnection connection, string queryText, List<string> importantWords,
        bool strict, MarketplaceSearchFilters? filters, CancellationToken ct)
    {
        using var command = connection.CreateCommand();

        var wordClauses = new List<string>();
        for (var i = 0; i < importantWords.Count; i++)
        {
            var p = $"@w{i}";
            command.Parameters.AddWithValue(p, $"%{importantWords[i]}%");
            wordClauses.Add($"Title LIKE {p}");
        }

        string matchClause;
        if (strict)
        {
            command.Parameters.AddWithValue("@phrase", $"%{MarketplaceMatcher.Normalize(queryText)}%");
            matchClause = $"(Title LIKE @phrase OR ({string.Join(" AND ", wordClauses)}))";
        }
        else
        {
            matchClause = $"({string.Join(" OR ", wordClauses)})";
        }

        var where = new List<string> { matchClause };
        if (filters?.MinPrice is decimal minPrice) { where.Add("Price >= @minPrice"); command.Parameters.AddWithValue("@minPrice", minPrice); }
        if (filters?.MaxPrice is decimal maxPrice) { where.Add("Price <= @maxPrice"); command.Parameters.AddWithValue("@maxPrice", maxPrice); }
        if (!string.IsNullOrWhiteSpace(filters?.Condition))
        {
            // Condition is free text in the real data ("Pre-Owned", "Refurbished in excellent
            // condition", "Brand New Merrell", ...) — a substring match tolerates that mess far
            // better than requiring an exact value.
            where.Add("Condition LIKE @condition");
            command.Parameters.AddWithValue("@condition", $"%{filters.Condition}%");
        }

        command.Parameters.AddWithValue("@sqlLimit", SqlCandidateLimit);

        // `Title LIKE '%word%'` cannot use an index — a leading wildcard defeats the B-tree —
        // so every lookup scanned the whole table. Common words hid it (LIMIT fills early and
        // returns in 0.04s), but a rare term scanned all 900k rows / 2 GB: measured 3.50s for
        // "s21e xp hydro" and 4.05s for "whatsminer m66s". Rare is exactly what a specific
        // model number is, so the slow path was the one that mattered most.
        //
        // SoldTitles is an FTS5 index over Title (built by BitData/build_title_fts.py and kept
        // current by triggers). The same searches return in under a millisecond through it.
        // When it is absent — an older comps DB, or the hosted copy — this falls back to the
        // LIKE query unchanged, so nothing depends on the index existing.
        if (await FtsAvailableAsync(connection, ct) && importantWords.Count > 0)
        {
            // FTS matches whole words where LIKE matched substrings, so each word gets a
            // prefix wildcard: "s19*" still finds "S19j", which "%s19%" did.
            var terms = string.Join(" ", importantWords
                .Select(w => "\"" + w.Replace("\"", "") + "\"*"));
            command.Parameters.AddWithValue("@fts", terms);
            var extra = where.Skip(1).ToList();   // keep price/condition filters, drop the LIKE
            var tail = extra.Count > 0 ? " AND " + string.Join(" AND ", extra) : "";
            command.CommandText = $"""
                SELECT l.ItemId, l.Title, l.Price, l.Shipping, l.Condition, l.Seller,
                       l.SoldDate, l.ItemUrl, l.ImageUrl, l.RawJson
                FROM {FtsTable} f
                JOIN {ExternalMarketplaceDb.SoldListingsTable} l ON l.rowid = f.rowid
                WHERE {FtsTable} MATCH @fts{tail}
                LIMIT @sqlLimit;
                """;
        }
        else
        {
            command.CommandText = $"""
                SELECT ItemId, Title, Price, Shipping, Condition, Seller, SoldDate, ItemUrl, ImageUrl, RawJson
                FROM {ExternalMarketplaceDb.SoldListingsTable}
                WHERE {string.Join(" AND ", where)}
                LIMIT @sqlLimit;
                """;
        }

        var rows = new List<CandidateRow>();
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new CandidateRow(
                ItemId:    reader.IsDBNull(0) ? "" : reader.GetString(0),
                Title:     reader.IsDBNull(1) ? "" : reader.GetString(1),
                Price:     reader.IsDBNull(2) ? 0 : SafeDecimal(reader, 2),
                Shipping:  reader.IsDBNull(3) ? 0 : SafeDecimal(reader, 3),
                Condition: reader.IsDBNull(4) ? null : reader.GetString(4),
                Seller:    reader.IsDBNull(5) ? null : reader.GetString(5),
                SoldDate:  reader.IsDBNull(6) ? null : reader.GetString(6),
                ItemUrl:   reader.IsDBNull(7) ? null : reader.GetString(7),
                ImageUrl:  reader.IsDBNull(8) ? null : reader.GetString(8),
                RawJson:   reader.IsDBNull(9) ? null : reader.GetString(9)));
        }
        return rows;
    }

    // Price/Shipping are declared REAL but SQLite is dynamically typed — a stray integer or
    // text value in the real data shouldn't blow up the whole search.
    private static decimal SafeDecimal(SqliteDataReader reader, int ordinal)
    {
        try { return Convert.ToDecimal(reader.GetValue(ordinal)); }
        catch { return 0m; }
    }

    private static MarketplaceComparableResult BuildResult(CandidateRow r)
    {
        var result = new MarketplaceComparableResult
        {
            ItemId     = r.ItemId,
            Title      = r.Title,
            SoldPrice  = r.Price,
            Shipping   = r.Shipping,
            TotalPrice = r.Price + r.Shipping,
            Condition  = r.Condition,
            SoldDate   = TryParseSoldDate(r.SoldDate),
            Seller     = r.Seller,
            ItemUrl    = r.ItemUrl,
            ImageUrl   = r.ImageUrl,
        };
        ApplyRawJson(result, r.RawJson);
        return result;
    }

    // RawJson is the SerpApi eBay-scrape blob the external collector stores per row — real per-row
    // condition/format/seller signal, unlike the always-empty Brand/Model/Category columns.
    // Best-effort: a malformed or missing blob just leaves these fields unset, never throws.
    private static void ApplyRawJson(MarketplaceComparableResult result, string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return;
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("epid", out var epid) && epid.ValueKind == JsonValueKind.String)
                result.Epid = epid.GetString();

            if (root.TryGetProperty("buying_format", out var format) && format.ValueKind == JsonValueKind.String)
                result.IsFixedPrice = string.Equals(format.GetString(), "buy_it_now", StringComparison.OrdinalIgnoreCase);

            if (root.TryGetProperty("seller", out var seller) && seller.ValueKind == JsonValueKind.Object)
            {
                if (seller.TryGetProperty("reviews", out var reviews) && reviews.TryGetInt32(out var reviewsVal))
                    result.SellerFeedbackCount = reviewsVal;
                if (seller.TryGetProperty("positive_feedback_in_percentage", out var pct) && pct.TryGetDouble(out var pctVal))
                    result.SellerPositiveFeedbackPercent = pctVal;
            }
        }
        catch (JsonException)
        {
            // Malformed blob — nothing to extract, leave the fields at their defaults.
        }
    }

    // SoldDate is stored as free-text like "Jul 16, 2026", not ISO — malformed or unexpected
    // formats just come back as null (no comparable date) rather than throwing.
    private static DateTime? TryParseSoldDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    /// <summary>The FTS5 index over Title, when the comps database has one.</summary>
    private const string FtsTable = "SoldTitles";

    // Cached per database file, not per process: asking sqlite_master on every pricing lookup
    // would put a query in front of the query we are trying to speed up, but a single shared
    // flag would be wrong the moment a second comps database is opened — the local file has the
    // index and a hosted or older copy may not, and whichever was seen first would decide for
    // both. Keyed on DataSource so each file answers for itself.
    private static readonly ConcurrentDictionary<string, bool> FtsByDatabase = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> FtsAvailableAsync(SqliteConnection connection, CancellationToken ct)
    {
        var key = connection.DataSource ?? "";
        if (FtsByDatabase.TryGetValue(key, out var cached)) return cached;

        bool available;
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name;";
            command.Parameters.AddWithValue("@name", FtsTable);
            available = await command.ExecuteScalarAsync(ct) is not null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            available = false;   // any doubt at all -> the LIKE path, which always works
        }
        FtsByDatabase[key] = available;
        return available;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, CancellationToken ct)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", ExternalMarketplaceDb.SoldListingsTable);
        var result = await command.ExecuteScalarAsync(ct);
        return result is not null;
    }
}
