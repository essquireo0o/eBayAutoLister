using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

public class TerapeakCacheEntry
{
    public decimal Average { get; set; }
    public decimal Median { get; set; }
    public decimal AvgShipping { get; set; }
    public decimal? SellThroughPercent { get; set; }
    public DateTime ScrapedAtUtc { get; set; }

    /// <summary>
    /// How many sold listings the median was taken from. Zero means "not recorded" — every row
    /// written before this column existed reads back as zero, and a caller must treat that as an
    /// unknown sample size rather than as a measured zero.
    /// </summary>
    /// <remarks>
    /// Not decoration. The pricing blend weights each source by its sample size
    /// (<see cref="MarketPriceEstimator.ResolveWeights"/>), so a stored price with no count behind
    /// it is a price that gets weighted at nothing — the second opinion is computed, carried, and
    /// then multiplied by zero. That was the state of every cache hit until this column existed.
    /// </remarks>
    public int CompCount { get; set; }

    /// <summary>
    /// The sold price range behind the median. Both zero when unrecorded. The blend reads the
    /// spread to decide how much to trust the median: a source whose sales ran $200-$900 is worth
    /// less than one whose sales ran $410-$430, whatever the midpoint says.
    /// </summary>
    public decimal Min { get; set; }
    public decimal Max { get; set; }
}

// Terapeak is a real logged-in browser scrape, not an API — each call is a genuine ~5-40s hit
// against eBay, and hammering it is how a session gets flagged or logged out (see
// TerapeakService.ScrapeAsync and the "Security Measure" challenge that showed up on a fresh
// login after heavy use). The one thing this app has plenty of is time, so every scrape's
// result is kept — permanently, in the same SQLite database as everything else the app
// persists (ListingDatabase's ing_listing_engine.db) — and reused for any query that asks the
// same keyword again, whether that ask comes from a manual Opportunity Finder search or the Gem
// Radar background scanner. No eviction: the longer this runs, the more of the keyword space is
// already priced for free, and the fewer real scrapes any given search — or the background
// scanner — ever needs to spend.
public class TerapeakPriceCache
{
    private readonly string _connectionString;
    private readonly object _sync = new();

    public TerapeakPriceCache(ListingDatabase db)
    {
        _connectionString = new SqliteConnectionStringBuilder { DataSource = db.DatabasePath }.ToString();
        Initialize();
    }

    public TerapeakCacheEntry? TryGet(string query, TimeSpan maxAge)
    {
        var key = Normalize(query);
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT average, median, avg_shipping, sell_through_percent, scraped_at_utc,
                       comp_count, price_min, price_max
                FROM terapeak_price_cache WHERE query_key = @key;
                """;
            select.Parameters.AddWithValue("@key", key);

            using var reader = select.ExecuteReader();
            if (!reader.Read())
            {
                RecordLookup(connection, hit: false);
                return null;
            }

            var scrapedAt = DateTime.Parse(reader.GetString(4)).ToUniversalTime();
            if (DateTime.UtcNow - scrapedAt > maxAge)
            {
                RecordLookup(connection, hit: false);
                return null;
            }

            RecordLookup(connection, hit: true);
            return new TerapeakCacheEntry
            {
                Average            = reader.GetDecimal(0),
                Median             = reader.GetDecimal(1),
                AvgShipping        = reader.GetDecimal(2),
                SellThroughPercent = reader.IsDBNull(3) ? null : reader.GetDecimal(3),
                ScrapedAtUtc       = scrapedAt,
                CompCount          = reader.GetInt32(5),
                Min                = reader.GetDecimal(6),
                Max                = reader.GetDecimal(7),
            };
        }
    }

    /// <param name="compCount">
    /// How many sold listings the median came from. Store it: a price kept without its sample size
    /// is a price the blend cannot weigh, and it will be dropped from the estimate rather than
    /// disagreed with. Zero is written only when the caller genuinely doesn't know.
    /// </param>
    public void Set(
        string query, decimal average, decimal median, decimal avgShipping, decimal? sellThroughPercent,
        int compCount = 0, decimal min = 0m, decimal max = 0m)
    {
        var key = Normalize(query);
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO terapeak_price_cache
                    (query_key, average, median, avg_shipping, sell_through_percent, scraped_at_utc,
                     comp_count, price_min, price_max)
                VALUES (@key, @avg, @median, @ship, @sellThrough, @scrapedAt, @count, @min, @max)
                ON CONFLICT(query_key) DO UPDATE SET
                    average = excluded.average, median = excluded.median, avg_shipping = excluded.avg_shipping,
                    sell_through_percent = excluded.sell_through_percent, scraped_at_utc = excluded.scraped_at_utc,
                    comp_count = excluded.comp_count, price_min = excluded.price_min, price_max = excluded.price_max;
                """;
            upsert.Parameters.AddWithValue("@key", key);
            upsert.Parameters.AddWithValue("@avg", average);
            upsert.Parameters.AddWithValue("@median", median);
            upsert.Parameters.AddWithValue("@ship", avgShipping);
            upsert.Parameters.AddWithValue("@sellThrough", (object?)sellThroughPercent ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@scrapedAt", DateTime.UtcNow.ToString("O"));
            upsert.Parameters.AddWithValue("@count", compCount);
            upsert.Parameters.AddWithValue("@min", min);
            upsert.Parameters.AddWithValue("@max", max);
            upsert.ExecuteNonQuery();

            RecordRealScrape(connection);
        }
    }

    // Categories the app has actually mined recently, ranked by real Terapeak sell-through % —
    // powers the "High Sell-Through" and "Low Competition" insight cards without spending a
    // single additional scrape (pure read of what's already been collected).
    public List<(string Query, decimal SellThroughPercent, DateTime ScrapedAtUtc)> GetTopSellThrough(int limit, TimeSpan maxAge)
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT query_key, sell_through_percent, scraped_at_utc
                FROM terapeak_price_cache
                WHERE sell_through_percent IS NOT NULL
                ORDER BY scraped_at_utc DESC
                LIMIT 200;
                """;
            using var reader = command.ExecuteReader();
            var rows = new List<(string, decimal, DateTime)>();
            while (reader.Read())
            {
                var scrapedAt = DateTime.Parse(reader.GetString(2)).ToUniversalTime();
                if (DateTime.UtcNow - scrapedAt > maxAge) continue;
                rows.Add((reader.GetString(0), reader.GetDecimal(1), scrapedAt));
            }
            return rows.OrderByDescending(r => r.Item2).Take(limit).ToList();
        }
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM terapeak_price_cache;";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }
    }

    // Visibility into how much real eBay traffic this app is generating vs. how much it's
    // avoiding by reusing cached prices — the ratio that matters for staying under the radar.
    public (int RealScrapes, int CacheHits) GetStats()
    {
        lock (_sync)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT real_scrapes, cache_hits FROM terapeak_cache_stats WHERE id = 1;";
            using var reader = command.ExecuteReader();
            return reader.Read() ? (reader.GetInt32(0), reader.GetInt32(1)) : (0, 0);
        }
    }

    private static void RecordRealScrape(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terapeak_cache_stats (id, real_scrapes, cache_hits) VALUES (1, 1, 0)
            ON CONFLICT(id) DO UPDATE SET real_scrapes = real_scrapes + 1;
            """;
        command.ExecuteNonQuery();
    }

    private static void RecordLookup(SqliteConnection connection, bool hit)
    {
        if (!hit) return; // misses aren't "spent" against anything — only count confirmed hits
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO terapeak_cache_stats (id, real_scrapes, cache_hits) VALUES (1, 0, 1)
            ON CONFLICT(id) DO UPDATE SET cache_hits = cache_hits + 1;
            """;
        command.ExecuteNonQuery();
    }

    private static string Normalize(string query) => query.Trim().ToLowerInvariant();

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS terapeak_price_cache (
                query_key TEXT PRIMARY KEY,
                average REAL NOT NULL,
                median REAL NOT NULL,
                avg_shipping REAL NOT NULL,
                sell_through_percent REAL NULL,
                scraped_at_utc TEXT NOT NULL,
                comp_count INTEGER NOT NULL DEFAULT 0,
                price_min REAL NOT NULL DEFAULT 0,
                price_max REAL NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS terapeak_cache_stats (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                real_scrapes INTEGER NOT NULL DEFAULT 0,
                cache_hits INTEGER NOT NULL DEFAULT 0
            );
            """;
        command.ExecuteNonQuery();

        // This table never evicts, so an installed copy of the app is carrying rows written under
        // the original six-column schema and will carry them for as long as it runs. Add the three
        // columns in place rather than rebuilding: `NOT NULL DEFAULT 0` fills the existing rows,
        // and zero is exactly what those rows know about their sample size.
        AddColumnIfMissing(connection, "comp_count", "INTEGER NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "price_min", "REAL NOT NULL DEFAULT 0");
        AddColumnIfMissing(connection, "price_max", "REAL NOT NULL DEFAULT 0");
    }

    private static void AddColumnIfMissing(SqliteConnection connection, string column, string declaration)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('terapeak_price_cache') WHERE name = @name;";
        probe.Parameters.AddWithValue("@name", column);
        if (Convert.ToInt32(probe.ExecuteScalar()) > 0) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE terapeak_price_cache ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
