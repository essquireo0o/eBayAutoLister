using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Files the rows a live lookup came back with into the same <c>SoldListings</c> table the pricing
/// path reads, so a lookup somebody waited for actually moves the price.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the app that writes to the comps database.
/// <see cref="ExternalMarketplaceDb"/> stays read-only and keeps its guarantees; what is added here
/// is a second, deliberately narrow connection that does exactly one thing — insert sold listings
/// that were just fetched, keyed on <c>ItemId</c> so the same sale can never be counted twice.
/// </para>
/// <para>
/// <b>Why it writes into that file rather than a private one.</b> The rows are only worth fetching
/// if the pricing path can see them, and the pricing path reads <c>SoldListings</c> through
/// <see cref="MarketplaceRepository"/>. On the owner's PC that is the collector's own 919k-row
/// <c>Marketplace.db</c> and this deepens it, exactly as the retired scraper did. On a server there
/// is no such file, so <see cref="ExternalMarketplaceDb"/> resolves to one under the app's own data
/// folder and this creates it on the first lookup — which is how the hosted build gets a local
/// sold-comps table at all.
/// </para>
/// <para>
/// <b>On the FTS index.</b> The collector's database carries an FTS5 index over Title with
/// <c>AFTER INSERT</c> triggers, so rows written here are searchable immediately without this class
/// knowing the index exists. A database without one falls back to the LIKE path, which is what
/// <see cref="MarketplaceRepository"/> already does.
/// </para>
/// </remarks>
public sealed class LiveCompsStore(ExternalMarketplaceDb db, ActionLog log)
{
    private readonly object _schemaGate = new();
    private bool _schemaChecked;

    /// <summary>The file rows are written to — the same one the local repository reads.</summary>
    public string DatabasePath => db.DatabasePath;

    /// <summary>
    /// Stores <paramref name="rows"/> against <paramref name="keyword"/> and reports how many of
    /// them the database had never seen.
    /// </summary>
    /// <remarks>
    /// Never throws. A comps database that is locked, missing or read-only must degrade to "the
    /// lookup found rows and could not keep them", which still prices this item correctly from what
    /// the caller is holding — not to a failed lookup.
    /// </remarks>
    public (int Stored, int New) Save(string keyword, IReadOnlyList<LiveCompRow> rows, DateTimeOffset now)
    {
        if (rows.Count == 0) return (0, 0);

        try
        {
            EnsureSchema();

            using var connection = OpenWritable();
            using var transaction = connection.BeginTransaction();

            var known = ExistingIds(connection, transaction, rows);
            var collectedAt = now.UtcDateTime.ToString("O");

            foreach (var row in rows)
            {
                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                // Idempotent on ItemId. The same sold listing turns up under many keywords and on
                // repeat lookups, and a comps table that counts one sale five times reports a market
                // that does not exist. The price and date are refreshed rather than left, because a
                // later observation of the same item is the better one.
                insert.CommandText = """
                    INSERT INTO SoldListings
                        (ItemId, Title, Price, Shipping, Condition, Seller, SoldDate,
                         Category, Brand, Model, ItemUrl, ImageUrl, SearchKeyword, DateCollected, RawJson)
                    VALUES
                        (@itemId, @title, @price, @shipping, @condition, @seller, @soldDate,
                         '', '', '', @itemUrl, @imageUrl, @keyword, @collectedAt, @rawJson)
                    ON CONFLICT(ItemId) DO UPDATE SET
                        Price         = excluded.Price,
                        Shipping      = excluded.Shipping,
                        SoldDate      = excluded.SoldDate,
                        DateCollected = excluded.DateCollected,
                        RawJson       = excluded.RawJson;
                    """;
                insert.Parameters.AddWithValue("@itemId", row.ItemId);
                insert.Parameters.AddWithValue("@title", row.Title);
                insert.Parameters.AddWithValue("@price", (object?)row.Price ?? DBNull.Value);
                insert.Parameters.AddWithValue("@shipping", (object?)row.Shipping ?? DBNull.Value);
                insert.Parameters.AddWithValue("@condition", row.Condition);
                insert.Parameters.AddWithValue("@seller", row.Seller);
                insert.Parameters.AddWithValue("@soldDate", row.SoldDate);
                insert.Parameters.AddWithValue("@itemUrl", row.ItemUrl);
                insert.Parameters.AddWithValue("@imageUrl", row.ImageUrl);
                insert.Parameters.AddWithValue("@keyword", keyword);
                insert.Parameters.AddWithValue("@collectedAt", collectedAt);
                insert.Parameters.AddWithValue("@rawJson", row.RawJson);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();

            var added = rows.Count(r => !known.Contains(r.ItemId));
            return (rows.Count, added);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Live sold comps could not be stored",
                $"\"{keyword}\": {ex.Message}. The prices from this lookup are still being used; "
                + "what was lost is having them next time.");
            return (0, 0);
        }
    }

    /// <summary>How many rows the database already holds for <paramref name="keyword"/>.</summary>
    /// <remarks>
    /// What the cached answer reports instead of a row count from a call it deliberately did not
    /// make. Zero on any failure, which reads as "nothing stored" rather than as an error.
    /// </remarks>
    public int StoredRowCount(string keyword)
    {
        try
        {
            if (!File.Exists(DatabasePath)) return 0;
            using var connection = OpenWritable();
            using var command = connection.CreateCommand();
            // NOCASE, because the cache this answers for is case-folded: the seller who gets the
            // stored rows is usually not the one whose typing wrote them.
            command.CommandText =
                "SELECT COUNT(*) FROM SoldListings WHERE SearchKeyword = @keyword COLLATE NOCASE;";
            command.Parameters.AddWithValue("@keyword", keyword);
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>Which of these items the database already has. One indexed lookup, not one per row.</summary>
    private static HashSet<string> ExistingIds(
        SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<LiveCompRow> rows)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var names = new List<string>(rows.Count);
        for (var i = 0; i < rows.Count; i++)
        {
            names.Add($"@id{i}");
            command.Parameters.AddWithValue($"@id{i}", rows[i].ItemId);
        }
        command.CommandText = $"SELECT ItemId FROM SoldListings WHERE ItemId IN ({string.Join(",", names)});";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(0)) known.Add(reader.GetString(0));

        return known;
    }

    /// <summary>
    /// Creates the table on a database that does not have one yet, and nothing else.
    /// </summary>
    /// <remarks>
    /// <c>IF NOT EXISTS</c> throughout, so on the collector's real database — which has all of this
    /// plus an FTS index, quarantine table and demand signals — every statement here is a no-op.
    /// The column list is copied from that database exactly: a second, subtly different
    /// <c>SoldListings</c> would read fine and price wrong.
    /// </remarks>
    private void EnsureSchema()
    {
        lock (_schemaGate)
        {
            if (_schemaChecked) return;

            var folder = Path.GetDirectoryName(DatabasePath);
            if (!string.IsNullOrWhiteSpace(folder)) Directory.CreateDirectory(folder);

            using var connection = OpenWritable();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS SoldListings (
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

                CREATE UNIQUE INDEX IF NOT EXISTS ux_sold_itemid ON SoldListings(ItemId);
                CREATE INDEX IF NOT EXISTS ix_soldlistings_title ON SoldListings(Title);
                CREATE INDEX IF NOT EXISTS ix_soldlistings_solddate ON SoldListings(SoldDate);
                CREATE INDEX IF NOT EXISTS ix_soldlistings_searchkeyword ON SoldListings(SearchKeyword);
                """;
            command.ExecuteNonQuery();

            _schemaChecked = true;
        }
    }

    /// <summary>
    /// A read-write connection with a busy timeout, because the bulk collector writes to the same
    /// file on the owner's machine and a commit that collides must wait rather than fail.
    /// </summary>
    private SqliteConnection OpenWritable()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 10000;";
        pragma.ExecuteNonQuery();

        return connection;
    }
}
