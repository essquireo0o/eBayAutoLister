using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// One null in one row's stored blob threw away every comp for the whole search.
/// </summary>
/// <remarks>
/// <para>
/// Caught in the running app on 2026-08-23, eleven times in eighty log lines, as
/// <c>Marketplace lookup failed: The requested operation requires an element of type 'Number',
/// but the target element has type 'Null'.</c>
/// </para>
/// <para>
/// <see cref="System.Text.Json.JsonElement.TryGetInt32"/> is not the safe reader its name
/// suggests. It returns false when a NUMBER will not fit an int; when the element is a null it
/// throws <see cref="InvalidOperationException"/>. The enrichment that reads it caught
/// <c>JsonException</c> only, so the throw escaped, and the search's own catch-all turned a whole
/// result set into an empty list. The blob is optional per-row colour — seller feedback — and it
/// was taking the prices down with it.
/// </para>
/// <para>
/// It mattered because of what else was broken that week: the live sold-comps API had been
/// answering HTTP 503 since 2026-08-20, so the stored database was the only working price source
/// the Opportunity Finder had, and eBay sellers with hidden feedback are ordinary.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public sealed class MarketplaceRawJsonNullTests : IDisposable
{
    private readonly string _dbPath;

    // Exactly the shape the OpenWebNinja rows carry, with the two fields the reader wants set to
    // null — which is what eBay sends for a seller who does not publish feedback.
    private const string BlobWithNulls = """
        {"epid":"12345","buying_format":"buy_it_now",
         "seller_feedback_count":null,"seller_feedback_percentage":null}
        """;

    // The older SerpApi shape, same problem one level down.
    private const string BlobWithNullSellerFields = """
        {"seller":{"reviews":null,"positive_feedback_in_percentage":null}}
        """;

    // And the shape that always worked, so the fix is not "ignore the blob".
    private const string BlobWithRealNumbers = """
        {"seller_feedback_count":4213,"seller_feedback_percentage":99.4}
        """;

    public MarketplaceRawJsonNullTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"marketplace_nulls_{Guid.NewGuid():N}.db");

        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();

        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE SoldListings (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ItemId TEXT UNIQUE, Title TEXT NOT NULL, Price REAL, Shipping REAL,
                    Condition TEXT, Seller TEXT, SoldDate TEXT, Category TEXT, Brand TEXT,
                    Model TEXT, ItemUrl TEXT, ImageUrl TEXT, SearchKeyword TEXT,
                    DateCollected TEXT, RawJson TEXT
                );
                """;
            create.ExecuteNonQuery();
        }

        void Insert(string id, string title, decimal price, string? rawJson)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO SoldListings (ItemId, Title, Price, Shipping, Condition, Seller, SoldDate, ItemUrl, ImageUrl, RawJson)
                VALUES (@id, @title, @price, 0, 'Pre-Owned', 'testseller', 'Aug 1, 2026', @url, '', @raw);
                """;
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@title", title);
            insert.Parameters.AddWithValue("@price", price);
            insert.Parameters.AddWithValue("@url", $"https://www.ebay.com/itm/{id}");
            insert.Parameters.AddWithValue("@raw", (object?)rawJson ?? DBNull.Value);
            insert.ExecuteNonQuery();
        }

        Insert("2001", "Bitmain Antminer S19j Pro 104TH/s Bitcoin Miner Tested Working", 950m, BlobWithNulls);
        Insert("2002", "Bitmain Antminer S19j Pro 104TH/s ASIC Miner Working", 900m, BlobWithNullSellerFields);
        Insert("2003", "Bitmain Antminer S19j Pro 104TH/s Bitcoin Miner Good Condition", 975m, BlobWithRealNumbers);
    }

    private MarketplaceRepository Repository()
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        return new(new ExternalMarketplaceDb(_dbPath), new ActionLog(),
                   new LiquidityScoringService(), normalizer, new ComparableMatcher(normalizer));
    }

    [Fact]
    public async Task A_null_seller_feedback_does_not_throw_away_the_whole_search()
    {
        var results = await Repository().SearchByModelAsync("Antminer S19j Pro");

        // Before the fix this came back empty — all three rows lost to one unreadable field on
        // one of them, and the board above it said "no sold data" about a product it had comps for.
        Assert.Equal(3, results.Count);
    }

    [Fact]
    public async Task The_prices_survive_the_field_that_could_not_be_read()
    {
        var results = await Repository().SearchByModelAsync("Antminer S19j Pro");

        // The blob is optional colour. The price is the entire point of the row, and it is right
        // there in its own column — it must never depend on the blob parsing.
        Assert.Contains(results, r => r.ItemId == "2001" && r.SoldPrice == 950m);
        Assert.Contains(results, r => r.ItemId == "2002" && r.SoldPrice == 900m);
    }

    [Fact]
    public async Task An_unreadable_field_is_left_unset_rather_than_guessed()
    {
        var results = await Repository().SearchByModelAsync("Antminer S19j Pro");
        var nulls = results.Single(r => r.ItemId == "2001");

        // Not zero. "This seller has no feedback" and "we could not read their feedback" are
        // different claims, and a zero would be the app inventing the first one.
        Assert.Null(nulls.SellerFeedbackCount);
        Assert.Null(nulls.SellerPositiveFeedbackPercent);
    }

    [Fact]
    public async Task A_blob_that_does_carry_the_numbers_is_still_read()
    {
        var results = await Repository().SearchByModelAsync("Antminer S19j Pro");
        var good = results.Single(r => r.ItemId == "2003");

        // The fix is guarding the read, not abandoning it.
        Assert.Equal(4213, good.SellerFeedbackCount);
        Assert.Equal(99.4, good.SellerPositiveFeedbackPercent!.Value, 3);
    }

    [Fact]
    public async Task A_blob_that_is_not_json_at_all_still_costs_nothing()
    {
        using (var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO SoldListings (ItemId, Title, Price, Shipping, Condition, Seller, SoldDate, ItemUrl, ImageUrl, RawJson)
                VALUES ('2004', 'Bitmain Antminer S19j Pro 104TH/s Miner', 925, 0, 'Pre-Owned', 's', 'Aug 2, 2026', 'u', '', '<html>not json</html>');
                """;
            insert.ExecuteNonQuery();
        }

        var results = await Repository().SearchByModelAsync("Antminer S19j Pro");
        Assert.Contains(results, r => r.ItemId == "2004" && r.SoldPrice == 925m);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { File.Delete(_dbPath); } catch { /* best-effort cleanup */ }
    }
}
