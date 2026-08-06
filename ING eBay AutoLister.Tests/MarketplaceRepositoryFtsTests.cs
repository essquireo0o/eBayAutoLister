using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

// The comps table is searched with `Title LIKE '%word%'`, which no index can serve: every
// lookup scanned the whole table. Measured on the real 900k-row database, a rare term — which
// is what a specific model number is — cost 3.5s, while common terms returned in 0.04s only
// because LIMIT filled early. BitData/build_title_fts.py adds an FTS5 index (SoldTitles) and
// MarketplaceRepository uses it when present, falling back to LIKE when it is not.
//
// These tests pin the two things that could go wrong with that: the fallback must still work
// on a database without the index, and the FTS path must return the same listings — including
// the substring case (`s19` finding `S19j`) that plain FTS word matching would have dropped.
public sealed class FtsMarketplaceFixture : IDisposable
{
    public string WithFtsPath { get; }
    public string WithoutFtsPath { get; }

    public FtsMarketplaceFixture()
    {
        WithFtsPath = Path.Combine(Path.GetTempPath(), $"marketplace_fts_{Guid.NewGuid():N}.db");
        WithoutFtsPath = Path.Combine(Path.GetTempPath(), $"marketplace_nofts_{Guid.NewGuid():N}.db");
        Build(WithFtsPath, withFts: true);
        Build(WithoutFtsPath, withFts: false);
    }

    private static void Build(string path, bool withFts)
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = path }.ToString());
        connection.Open();

        Exec(connection, """
            CREATE TABLE SoldListings (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ItemId TEXT UNIQUE, Title TEXT NOT NULL, Price REAL, Shipping REAL,
                Condition TEXT, Seller TEXT, SoldDate TEXT, Category TEXT, Brand TEXT,
                Model TEXT, ItemUrl TEXT, ImageUrl TEXT, SearchKeyword TEXT,
                DateCollected TEXT, RawJson TEXT
            );
            """);

        void Insert(string id, string title, decimal price)
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO SoldListings (ItemId, Title, Price, Shipping, Condition, Seller, SoldDate, ItemUrl, ImageUrl)
                VALUES (@id, @title, @price, 25.0, 'Pre-Owned', 'testseller', 'Jul 12, 2026', @url, '');
                """;
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@title", title);
            cmd.Parameters.AddWithValue("@price", price);
            cmd.Parameters.AddWithValue("@url", $"https://www.ebay.com/itm/{id}");
            cmd.ExecuteNonQuery();
        }

        Insert("2001", "Bitmain Antminer S19j Pro 104TH/s Bitcoin Miner ASIC", 950m);
        Insert("2002", "Bitmain Antminer S19 95TH/s SHA-256 ASIC Bitcoin Miner", 900m);
        Insert("2003", "Bitmain Antminer S21e XP Hydro 860TH Bitcoin Miner", 6293m);
        Insert("2004", "Vintage Baseball Card Collection Binder Lot 1990s", 25m);

        if (!withFts) return;

        Exec(connection, """
            CREATE VIRTUAL TABLE SoldTitles USING fts5(
                Title, content='SoldListings', content_rowid='Id',
                tokenize='unicode61 remove_diacritics 2');
            """);
        Exec(connection, "INSERT INTO SoldTitles(SoldTitles) VALUES('rebuild');");
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var p in new[] { WithFtsPath, WithoutFtsPath })
        {
            try { File.Delete(p); } catch { /* best-effort cleanup */ }
        }
    }
}

[Collection(PooledSqliteTests.Name)]
public class MarketplaceRepositoryFtsTests : IClassFixture<FtsMarketplaceFixture>
{
    private readonly FtsMarketplaceFixture _fixture;

    public MarketplaceRepositoryFtsTests(FtsMarketplaceFixture fixture) => _fixture = fixture;

    private static MarketplaceRepository Repo(string path)
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        return new(new ExternalMarketplaceDb(path), new ActionLog(),
            new LiquidityScoringService(), normalizer, new ComparableMatcher(normalizer));
    }

    [Fact]
    public async Task Search_WithFtsIndex_FindsTheSameListings()
    {
        var results = await Repo(_fixture.WithFtsPath).SearchByKeywordAsync("Antminer S19j Pro");

        Assert.Contains(results, r => r.ItemId == "2001");
        Assert.DoesNotContain(results, r => r.ItemId == "2004");   // the baseball cards
    }

    [Fact]
    public async Task Search_WithoutFtsIndex_StillWorksViaLikeFallback()
    {
        // A comps database that predates the index — or the hosted copy — must behave exactly
        // as before. This is the guarantee that makes the index safe to add.
        var results = await Repo(_fixture.WithoutFtsPath).SearchByKeywordAsync("Antminer S19j Pro");

        Assert.Contains(results, r => r.ItemId == "2001");
        Assert.DoesNotContain(results, r => r.ItemId == "2004");
    }

    [Fact]
    public async Task Search_FtsAndLike_AgreeOnAPartialModelNumber()
    {
        // The behaviour change worth pinning: LIKE '%s19%' matched "S19j" as a substring, but
        // FTS matches whole words, so "s19" alone would NOT have found "S19j" without the
        // prefix wildcard the repository appends. Both paths must still return it.
        var withFts = await Repo(_fixture.WithFtsPath).SearchByKeywordAsync("antminer s19");
        var without = await Repo(_fixture.WithoutFtsPath).SearchByKeywordAsync("antminer s19");

        Assert.Contains(withFts, r => r.ItemId == "2001");    // S19j found by prefix
        Assert.Contains(without, r => r.ItemId == "2001");
        Assert.Contains(withFts, r => r.ItemId == "2002");    // exact S19
        Assert.Contains(without, r => r.ItemId == "2002");
    }

    [Fact]
    public async Task Search_RareModel_IsFoundThroughTheIndex()
    {
        // The case that cost 3.5 seconds on the real database: a model with almost no comps.
        var results = await Repo(_fixture.WithFtsPath).SearchByKeywordAsync("S21e XP Hydro");

        Assert.Contains(results, r => r.ItemId == "2003");
    }
}
