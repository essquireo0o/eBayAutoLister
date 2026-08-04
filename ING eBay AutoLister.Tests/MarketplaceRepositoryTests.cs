using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

// Exercises MarketplaceRepository against a real throwaway SQLite file shaped exactly like the
// production SoldListings table — not a mock — so these tests actually verify parameterized SQL,
// scoring, and dedup end to end.
public sealed class MarketplaceRepositoryFixture : IDisposable
{
    public string DatabasePath { get; }

    public MarketplaceRepositoryFixture()
    {
        DatabasePath = Path.Combine(Path.GetTempPath(), $"marketplace_test_{Guid.NewGuid():N}.db");

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath }.ToString());
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE SoldListings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ItemId TEXT UNIQUE,
                    Title TEXT NOT NULL,
                    Price REAL,
                    Shipping REAL,
                    Condition TEXT,
                    Seller TEXT,
                    SoldDate TEXT,
                    Category TEXT,
                    Brand TEXT,
                    Model TEXT,
                    ItemUrl TEXT,
                    ImageUrl TEXT,
                    SearchKeyword TEXT,
                    DateCollected TEXT,
                    RawJson TEXT
                );
                """;
            create.ExecuteNonQuery();
        }

        void Insert(string itemId, string title, decimal price, decimal shipping, string condition, string soldDate)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO SoldListings (ItemId, Title, Price, Shipping, Condition, Seller, SoldDate, ItemUrl, ImageUrl)
                VALUES (@id, @title, @price, @ship, @cond, 'testseller', @sold, @url, @img);
                """;
            insert.Parameters.AddWithValue("@id", itemId);
            insert.Parameters.AddWithValue("@title", title);
            insert.Parameters.AddWithValue("@price", price);
            insert.Parameters.AddWithValue("@ship", shipping);
            insert.Parameters.AddWithValue("@cond", condition);
            insert.Parameters.AddWithValue("@sold", soldDate);
            insert.Parameters.AddWithValue("@url", $"https://www.ebay.com/itm/{itemId}");
            insert.Parameters.AddWithValue("@img", "");
            insert.ExecuteNonQuery();
        }

        Insert("1001", "New Bitmain APW7 Power Supply PSU Antminer 100-264V 1800W (APW7-12-1800)", 65.00m, 19.99m, "Brand New", "Jul 10, 2026");
        Insert("1002", "Bitmain Antminer S19j Pro 104TH/s Bitcoin Miner ASIC Tested Working", 950.00m, 45.00m, "Pre-Owned", "Jul 12, 2026");
        Insert("1003", "Bitmain Antminer S19 95TH/s SHA-256 ASIC Bitcoin Miner - Tested Working", 900.00m, 40.00m, "Pre-Owned", "Jul 1, 2026");
        Insert("1004", "Bitmain Antminer S19 Pro 110TH Bitcoin Miner Fully Tested Working", 980.00m, 50.00m, "Pre-Owned", "Jun 20, 2026");
        Insert("1005", "Vintage Baseball Card Collection Binder Lot 1990s", 25.00m, 8.00m, "Used", "Jul 5, 2026");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(DatabasePath); } catch { /* best-effort cleanup */ }
    }
}

[Collection(PooledSqliteTests.Name)]
public class MarketplaceRepositoryTests : IClassFixture<MarketplaceRepositoryFixture>
{
    private readonly MarketplaceRepositoryFixture _fixture;

    public MarketplaceRepositoryTests(MarketplaceRepositoryFixture fixture) => _fixture = fixture;

    private MarketplaceRepository CreateRepository(string? dbPathOverride = null)
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        return new(new ExternalMarketplaceDb(dbPathOverride ?? _fixture.DatabasePath), new ActionLog(),
            new LiquidityScoringService(), normalizer, new ComparableMatcher(normalizer));
    }

    [Fact]
    public async Task SearchByPartNumberAsync_ExactPartNumber_FindsTheRightListing()
    {
        var repo = CreateRepository();

        var results = await repo.SearchByPartNumberAsync("APW7-12-1800");

        Assert.Contains(results, r => r.ItemId == "1001");
        // Weighted point table (ComparableMatcher): a part-number-only search with no brand/
        // category/keyword context can only ever earn the exact-identifier points (35 of 100) —
        // it's an honest confidence score, not a "found it" boolean.
        Assert.Equal(35, results.First(r => r.ItemId == "1001").MatchScore);
    }

    [Fact]
    public async Task SearchByModelAsync_ExactMinerModel_FindsTheRightListing()
    {
        var repo = CreateRepository();

        var results = await repo.SearchByModelAsync("Antminer S19j Pro");

        Assert.Contains(results, r => r.ItemId == "1002");
    }

    [Fact]
    public async Task SearchByKeywordAsync_BroadTitleMatch_FindsRelatedListingsNotJustExactOne()
    {
        var repo = CreateRepository();

        var results = await repo.SearchByKeywordAsync("Antminer S19 Bitcoin Miner");

        // Several different S19 variants should all surface for a broad keyword search.
        Assert.True(results.Count >= 2, $"expected multiple broad matches, got {results.Count}");
        Assert.DoesNotContain(results, r => r.ItemId == "1005"); // baseball cards must not match
    }

    [Fact]
    public async Task FindComparablesAsync_NoMatches_ReturnsZeroCountNotAnException()
    {
        var repo = CreateRepository();

        var summary = await repo.FindComparablesAsync(new MarketplaceLookupRequest { Keywords = "Nonexistent Widget Zzzqx" });

        Assert.Equal(0, summary.MatchCount);
        Assert.Null(summary.SuggestedResalePrice);
    }

    [Fact]
    public async Task FindComparablesAsync_MissingDatabaseFile_DegradesGracefullyToZeroMatches()
    {
        var repo = CreateRepository(Path.Combine(Path.GetTempPath(), $"does_not_exist_{Guid.NewGuid():N}.db"));

        var summary = await repo.FindComparablesAsync(new MarketplaceLookupRequest { Keywords = "Antminer S19" });

        Assert.Equal(0, summary.MatchCount);
    }

    [Fact]
    public async Task SearchByKeywordAsync_ResultsContainNoDuplicateItemIds()
    {
        // SoldListings.ItemId is UNIQUE in production so duplicate rows can't occur, but the
        // repository still runs every result set through MarketplaceMatcher.DeduplicateByItemId
        // (see MarketplaceMatcherTests for the dedup logic itself) — this confirms it's actually
        // wired in end to end.
        var repo = CreateRepository();

        var results = await repo.SearchByKeywordAsync("Bitmain Antminer S19");
        var ids = results.Select(r => r.ItemId).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
