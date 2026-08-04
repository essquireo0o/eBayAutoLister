using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The permanent store of every Terapeak price this app has ever paid for.
///
/// Terapeak is a logged-in browser scrape, not an API: each miss costs 5-40 seconds driving a real
/// browser against eBay under the seller's own account, and enough of them in a row is how that
/// account meets a "Security Measure" challenge. So this table is the app's only defence — it never
/// evicts, and every keyword it already holds is a scrape nobody has to spend again.
///
/// That makes two things worth pinning hard. A key that misses when it should hit costs a scrape
/// against the account. A key that hits when it should miss serves one item's price for a different
/// item, permanently, with nothing on screen saying so.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class TerapeakPriceCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-tpcache-" + Guid.NewGuid().ToString("N"));

    public TerapeakPriceCacheTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private ListingDatabase Db() => new(new StubWebHostEnvironment { ContentRootPath = _root });

    private TerapeakPriceCache NewCache() => new(Db());

    private static readonly TimeSpan Forever = TimeSpan.FromDays(3650);

    /// <summary>
    /// Ages an entry by rewriting its timestamp in place. Set() always stamps "now", so this is the
    /// only way to reach the staleness branch without a test that sleeps.
    /// </summary>
    private void Backdate(string queryKey, TimeSpan age)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db().DatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE terapeak_price_cache SET scraped_at_utc = @at WHERE query_key = @key;";
        command.Parameters.AddWithValue("@at", DateTime.UtcNow.Subtract(age).ToString("O"));
        command.Parameters.AddWithValue("@key", queryKey);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    // ── Round-tripping a price ───────────────────────────────────────────────

    [Fact]
    public void Stores_and_returns_every_field_of_a_scrape()
    {
        var cache = NewCache();
        cache.Set("Antminer S19 95TH", average: 1150.40m, median: 1099m, avgShipping: 84.25m, sellThroughPercent: 72m);

        var entry = cache.TryGet("Antminer S19 95TH", Forever);

        Assert.NotNull(entry);
        Assert.Equal(1150.40m, entry.Average);
        Assert.Equal(1099m, entry.Median);
        Assert.Equal(84.25m, entry.AvgShipping);
        Assert.Equal(72m, entry.SellThroughPercent);
        Assert.Equal(DateTimeKind.Utc, entry.ScrapedAtUtc.Kind);
        Assert.True((DateTime.UtcNow - entry.ScrapedAtUtc).Duration() < TimeSpan.FromMinutes(1));
    }

    /// <summary>
    /// The columns are declared REAL and the values are decimal money. Cents that do not survive the
    /// round trip are cents that quietly move the profit on every row priced off this entry.
    /// </summary>
    [Theory]
    [InlineData(0.01)]
    [InlineData(19.99)]
    [InlineData(1234.56)]
    [InlineData(99999.99)]
    public void Cents_survive_the_round_trip(double raw)
    {
        var money = (decimal)raw;
        var cache = NewCache();
        cache.Set("round trip", money, money, money, money);

        var entry = cache.TryGet("round trip", Forever)!;

        Assert.Equal(money, entry.Average);
        Assert.Equal(money, entry.Median);
        Assert.Equal(money, entry.AvgShipping);
        Assert.Equal(money, entry.SellThroughPercent);
    }

    /// <summary>
    /// Nothing sold is a finding. Storing "0% sell-through" as "we don't know" would let an item with
    /// a dead market keep scoring as one whose market simply wasn't measured.
    /// </summary>
    [Fact]
    public void A_sell_through_of_zero_stays_zero_and_does_not_become_unknown()
    {
        var cache = NewCache();
        cache.Set("nobody wants this", 40m, 40m, 0m, sellThroughPercent: 0m);

        Assert.Equal(0m, cache.TryGet("nobody wants this", Forever)!.SellThroughPercent);
    }

    /// <summary>
    /// And the mirror: Terapeak prints "-" rather than a percentage when it can't compute one, which
    /// arrives here as null and has to stay null.
    /// </summary>
    [Fact]
    public void An_unknown_sell_through_stays_null()
    {
        var cache = NewCache();
        cache.Set("no sell-through on the page", 40m, 40m, 0m, sellThroughPercent: null);

        Assert.Null(cache.TryGet("no sell-through on the page", Forever)!.SellThroughPercent);
    }

    // ── The sample size behind the price ─────────────────────────────────────
    // A median with no count behind it cannot be weighed against anything. The pricing blend sizes
    // each source by how many sales it saw, so a price stored without its count comes back as a
    // figure that is computed, carried, and then multiplied by a zero weight — present in the
    // database and absent from the estimate it was paid for.

    [Fact]
    public void Stores_the_sample_size_and_the_range_the_median_came_from()
    {
        var cache = NewCache();
        cache.Set("Antminer S19 95TH", 1150.40m, 1099m, 84.25m, 72m,
            compCount: 14, min: 880.00m, max: 1425.50m);

        var entry = cache.TryGet("Antminer S19 95TH", Forever)!;

        Assert.Equal(14, entry.CompCount);
        Assert.Equal(880.00m, entry.Min);
        Assert.Equal(1425.50m, entry.Max);
    }

    /// <summary>
    /// This table never evicts, so an installed copy of the app carries rows written under the
    /// original schema — which had no room for any of this — and will carry them for as long as it
    /// runs. Opening that database must migrate it in place rather than throw or drop the prices in
    /// it, and the old rows must read back as an unknown sample size rather than a made-up one.
    /// </summary>
    [Fact]
    public void A_price_stored_before_the_sample_size_had_a_column_still_opens_and_still_reads()
    {
        WriteRowUnderTheOriginalSchema("legacy keyword", average: 1150m, median: 1099m);

        var entry = new TerapeakPriceCache(Db()).TryGet("legacy keyword", Forever)!;

        Assert.Equal(1150m, entry.Average);
        Assert.Equal(1099m, entry.Median);
        Assert.Equal(0, entry.CompCount); // not recorded — and not guessed at
        Assert.Equal(0m, entry.Min);
        Assert.Equal(0m, entry.Max);
    }

    /// <summary>
    /// And the row heals the first time that keyword is scraped again: the upsert replaces the
    /// count and range along with the money, rather than leaving a fresh price sitting on the old
    /// row's zeros.
    /// </summary>
    [Fact]
    public void Re_scraping_a_keyword_replaces_its_sample_size_too()
    {
        WriteRowUnderTheOriginalSchema("legacy keyword", average: 1150m, median: 1099m);
        var cache = new TerapeakPriceCache(Db());

        cache.Set("legacy keyword", 900m, 890m, 40m, 61m, compCount: 9, min: 700m, max: 1100m);

        var entry = cache.TryGet("legacy keyword", Forever)!;
        Assert.Equal(9, entry.CompCount);
        Assert.Equal(700m, entry.Min);
        Assert.Equal(1100m, entry.Max);
        Assert.Equal(1, cache.Count); // healed in place, not duplicated
    }

    /// <summary>
    /// Creates the six-column table this cache shipped with and puts a row in it, without going
    /// through <see cref="TerapeakPriceCache"/> at all — the only honest way to test the migration
    /// is to write the database the way the previous version wrote it.
    /// </summary>
    private void WriteRowUnderTheOriginalSchema(string queryKey, decimal average, decimal median)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = Db().DatabasePath }.ToString());
        connection.Open();

        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS terapeak_price_cache (
                query_key TEXT PRIMARY KEY,
                average REAL NOT NULL,
                median REAL NOT NULL,
                avg_shipping REAL NOT NULL,
                sell_through_percent REAL NULL,
                scraped_at_utc TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO terapeak_price_cache (query_key, average, median, avg_shipping, sell_through_percent, scraped_at_utc)
            VALUES (@key, @avg, @median, 0, 72, @at);
            """;
        insert.Parameters.AddWithValue("@key", queryKey);
        insert.Parameters.AddWithValue("@avg", average);
        insert.Parameters.AddWithValue("@median", median);
        insert.Parameters.AddWithValue("@at", DateTime.UtcNow.ToString("O"));
        insert.ExecuteNonQuery();
    }

    // ── The key ──────────────────────────────────────────────────────────────

    [Fact]
    public void Case_and_surrounding_whitespace_do_not_cost_a_second_scrape()
    {
        var cache = NewCache();
        cache.Set("  Antminer S19 95TH  ", 1150m, 1099m, 84m, 72m);

        Assert.NotNull(cache.TryGet("antminer s19 95th", Forever));
        Assert.NotNull(cache.TryGet("ANTMINER S19 95TH", Forever));
        Assert.Equal(1, cache.Count);
    }

    /// <summary>
    /// Inner spacing is NOT collapsed, so "s19  95th" and "s19 95th" are two entries and two scrapes.
    /// Pinned because it is the cheap direction to be wrong in — an extra scrape costs time, whereas
    /// over-eager key collapsing would serve one item's price for another. Callers that care hand in
    /// an already-normalized signature (see TerapeakMarketService.BuildCacheKey).
    /// </summary>
    [Fact]
    public void Inner_whitespace_is_not_collapsed_so_two_spellings_are_two_entries()
    {
        var cache = NewCache();
        cache.Set("antminer s19 95th", 1150m, 1099m, 84m, 72m);

        Assert.Null(cache.TryGet("antminer  s19 95th", Forever));
        Assert.Null(cache.TryGet("antminer s19  95th", Forever));
    }

    [Fact]
    public void A_keyword_never_scraped_is_a_miss_rather_than_a_zero()
    {
        Assert.Null(NewCache().TryGet("never looked this up", Forever));
    }

    [Fact]
    public void Scraping_the_same_keyword_again_replaces_the_price_instead_of_duplicating_it()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        cache.Set("antminer s19", 890m, 875m, 79m, 61m);

        var entry = cache.TryGet("antminer s19", Forever)!;

        Assert.Equal(1, cache.Count);
        Assert.Equal(890m, entry.Average);
        Assert.Equal(875m, entry.Median);
        Assert.Equal(61m, entry.SellThroughPercent);
    }

    [Fact]
    public void Different_keywords_are_different_entries()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        cache.Set("whatsminer m30s", 700m, 690m, 80m, 55m);

        Assert.Equal(2, cache.Count);
        Assert.Equal(1150m, cache.TryGet("antminer s19", Forever)!.Average);
        Assert.Equal(700m, cache.TryGet("whatsminer m30s", Forever)!.Average);
    }

    [Fact]
    public void A_fresh_database_holds_nothing()
    {
        Assert.Equal(0, NewCache().Count);
    }

    // ── Age ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_price_older_than_the_caller_asked_for_is_not_served()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        Backdate("antminer s19", TimeSpan.FromHours(49));

        Assert.Null(cache.TryGet("antminer s19", TimeSpan.FromHours(48)));
        Assert.NotNull(cache.TryGet("antminer s19", TimeSpan.FromHours(50)));
    }

    /// <summary>
    /// A stale entry is refused, not deleted. There is no eviction anywhere in this class, and that is
    /// deliberate: a caller willing to accept a six-month-old price (see the freshness weighting in
    /// TerapeakMarketService) must still be able to get it after a caller who wasn't has asked.
    /// </summary>
    [Fact]
    public void Refusing_a_stale_price_does_not_throw_it_away()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        Backdate("antminer s19", TimeSpan.FromDays(400));

        Assert.Null(cache.TryGet("antminer s19", TimeSpan.FromHours(48)));
        Assert.Equal(1, cache.Count);
        Assert.NotNull(cache.TryGet("antminer s19", Forever));
    }

    [Fact]
    public void Re_scraping_a_stale_keyword_refreshes_it_in_place()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        Backdate("antminer s19", TimeSpan.FromDays(400));

        cache.Set("antminer s19", 890m, 875m, 79m, 61m);

        var entry = cache.TryGet("antminer s19", TimeSpan.FromHours(1))!;
        Assert.Equal(1, cache.Count);
        Assert.Equal(890m, entry.Average);
    }

    // ── What survives the process ────────────────────────────────────────────

    /// <summary>
    /// The whole point of this being SQLite rather than a dictionary: a scrape paid for in March is
    /// still paid for in July, across restarts and across app updates.
    /// </summary>
    [Fact]
    public void Prices_outlive_the_object_that_stored_them()
    {
        NewCache().Set("antminer s19", 1150m, 1099m, 84m, 72m);

        var laterRun = NewCache();

        Assert.Equal(1, laterRun.Count);
        Assert.Equal(1099m, laterRun.TryGet("antminer s19", Forever)!.Median);
    }

    // ── The scrape ledger ────────────────────────────────────────────────────

    [Fact]
    public void A_new_database_has_spent_nothing_and_saved_nothing()
    {
        Assert.Equal((0, 0), NewCache().GetStats());
    }

    [Fact]
    public void Every_stored_scrape_is_counted_as_real_traffic_against_the_account()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        cache.Set("whatsminer m30s", 700m, 690m, 80m, 55m);
        cache.Set("antminer s19", 1100m, 1050m, 84m, 70m);

        Assert.Equal(3, cache.GetStats().RealScrapes);
    }

    [Fact]
    public void Served_prices_are_counted_as_traffic_avoided()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);

        cache.TryGet("antminer s19", Forever);
        cache.TryGet("antminer s19", Forever);

        Assert.Equal((1, 2), cache.GetStats());
    }

    /// <summary>
    /// A miss is not a hit and is not a scrape. It becomes a scrape only if the caller then decides to
    /// spend one — counting it here would make the ratio this class exists to report read as though
    /// the app were hammering eBay when it had merely looked in its own table.
    /// </summary>
    [Fact]
    public void A_miss_is_charged_to_neither_column()
    {
        var cache = NewCache();

        cache.TryGet("never scraped", Forever);
        cache.TryGet("also never scraped", Forever);

        Assert.Equal((0, 0), cache.GetStats());
    }

    [Fact]
    public void Refusing_a_stale_price_counts_as_a_miss_not_as_a_saved_scrape()
    {
        var cache = NewCache();
        cache.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        Backdate("antminer s19", TimeSpan.FromDays(400));

        cache.TryGet("antminer s19", TimeSpan.FromHours(48));

        Assert.Equal((1, 0), cache.GetStats());
    }

    [Fact]
    public void The_ledger_outlives_the_process_too()
    {
        var first = NewCache();
        first.Set("antminer s19", 1150m, 1099m, 84m, 72m);
        first.TryGet("antminer s19", Forever);

        Assert.Equal((1, 1), NewCache().GetStats());
    }

    // ── The insight cards ────────────────────────────────────────────────────

    [Fact]
    public void The_best_sell_through_is_ranked_first_regardless_of_when_it_was_scraped()
    {
        var cache = NewCache();
        cache.Set("slow mover", 100m, 100m, 10m, 12m);
        cache.Set("hot category", 100m, 100m, 10m, 91m);
        cache.Set("middling", 100m, 100m, 10m, 55m);
        Backdate("hot category", TimeSpan.FromDays(20));

        var top = cache.GetTopSellThrough(limit: 3, Forever);

        Assert.Equal(["hot category", "middling", "slow mover"], top.Select(t => t.Query));
        Assert.Equal(91m, top[0].SellThroughPercent);
    }

    /// <summary>
    /// A keyword Terapeak could not compute a sell-through for has nothing to say about demand, so it
    /// must not appear on a card that ranks by demand — a null read as 0% would park real categories
    /// at the bottom of "Low Competition" forever.
    /// </summary>
    [Fact]
    public void Keywords_with_no_measured_sell_through_are_left_off_the_cards()
    {
        var cache = NewCache();
        cache.Set("measured", 100m, 100m, 10m, 44m);
        cache.Set("unmeasured", 100m, 100m, 10m, null);

        var top = cache.GetTopSellThrough(limit: 10, Forever);

        Assert.Equal(["measured"], top.Select(t => t.Query));
    }

    [Fact]
    public void Only_recently_mined_categories_reach_the_cards()
    {
        var cache = NewCache();
        cache.Set("mined last week", 100m, 100m, 10m, 40m);
        cache.Set("mined last year", 100m, 100m, 10m, 99m);
        Backdate("mined last year", TimeSpan.FromDays(365));

        var top = cache.GetTopSellThrough(limit: 10, TimeSpan.FromDays(30));

        Assert.Equal(["mined last week"], top.Select(t => t.Query));
    }

    [Fact]
    public void The_limit_is_honoured()
    {
        var cache = NewCache();
        for (var i = 0; i < 8; i++) cache.Set($"category {i}", 100m, 100m, 10m, 10m + i);

        var top = cache.GetTopSellThrough(limit: 3, Forever);

        Assert.Equal(3, top.Count);
        Assert.Equal(["category 7", "category 6", "category 5"], top.Select(t => t.Query));
    }

    [Fact]
    public void The_cards_show_the_normalized_key_and_the_time_it_was_measured()
    {
        var cache = NewCache();
        cache.Set("  Graphics Cards  ", 100m, 100m, 10m, 77m);
        Backdate("graphics cards", TimeSpan.FromDays(3));

        var row = Assert.Single(cache.GetTopSellThrough(limit: 5, Forever));

        Assert.Equal("graphics cards", row.Query);
        Assert.Equal(DateTimeKind.Utc, row.ScrapedAtUtc.Kind);
        Assert.InRange((DateTime.UtcNow - row.ScrapedAtUtc).TotalDays, 2.9, 3.1);
    }

    [Fact]
    public void Nothing_mined_yet_means_no_cards_rather_than_an_error()
    {
        Assert.Empty(NewCache().GetTopSellThrough(limit: 5, Forever));
    }

    /// <summary>
    /// The ranking looks at the 200 most recently scraped keywords, not at the whole table. Once the
    /// app has mined more than 200 categories, a record-breaking sell-through measured before those
    /// 200 stops appearing — the cards report "best of what we've looked at lately", which is the
    /// intent, but is not what "top sell-through" reads as. Pinned so a future change to the window
    /// is a deliberate one.
    /// </summary>
    [Fact]
    public void The_cards_rank_within_the_two_hundred_most_recent_keywords_only()
    {
        var cache = NewCache();
        cache.Set("the all-time best", 100m, 100m, 10m, 99m);
        Backdate("the all-time best", TimeSpan.FromDays(300));

        for (var i = 0; i < 200; i++)
        {
            cache.Set($"recent {i}", 100m, 100m, 10m, 10m);
            Backdate($"recent {i}", TimeSpan.FromDays(200 - i));
        }

        var top = cache.GetTopSellThrough(limit: 5, Forever);

        Assert.Equal(201, cache.Count);
        Assert.DoesNotContain("the all-time best", top.Select(t => t.Query));
        Assert.All(top, t => Assert.Equal(10m, t.SellThroughPercent));
    }

    // ── Two scans at once ────────────────────────────────────────────────────

    /// <summary>
    /// The Gem Radar background scanner and a manual Opportunity Finder search share one instance of
    /// this cache and can be mid-scan at the same time. SQLite hands out "database is locked" rather
    /// than blocking, so the lock inside this class is load-bearing — losing it fails a scan, not just
    /// a lookup.
    /// </summary>
    [Fact]
    public void Two_scans_writing_at_once_do_not_collide()
    {
        var cache = NewCache();

        Parallel.For(0, 60, i =>
        {
            cache.Set($"keyword {i % 20}", 100m + i, 90m + i, 5m, 50m);
            cache.TryGet($"keyword {i % 20}", Forever);
        });

        Assert.Equal(20, cache.Count);
        Assert.Equal(60, cache.GetStats().RealScrapes);
    }
}
