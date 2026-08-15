using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The app's own SQLite database, and the locally-edited listings in it.
/// </summary>
/// <remarks>
/// Every other store in the app is handed this one's <see cref="DatabasePath"/> and keeps its own
/// table in the same file. The listings here belong to a user like the rest of them do — a title, a
/// price and a description are the seller's work, and on a shared deployment the second person to
/// sign up must not open the editor onto the first person's drafts. See <see cref="UserScope"/>.
/// </remarks>
public sealed class ListingDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly UserScope _scope;

    public string DatabasePath { get; }

    public ListingDatabase(IWebHostEnvironment env, UserScope? scope = null)
    {
        var dataDir = Path.Combine(env.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDir);

        _scope = scope ?? UserScope.Desktop;
        DatabasePath = Path.Combine(dataDir, "ing_listing_engine.db");
        Initialize();
    }

    public LocalListingDatabaseStatus GetStatus()
    {
        // The seller's own count, not the deployment's. "127 listings saved locally" is a claim
        // about their work, and counting everybody's would be both wrong and a headcount of
        // somebody else's inventory.
        if (_scope.OwnerId is not { } owner)
            return new LocalListingDatabaseStatus(DatabasePath, 0, DateTimeOffset.UtcNow);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM local_listings WHERE user_id = @user_id;";
        command.Parameters.AddWithValue("@user_id", owner);
        var count = Convert.ToInt32(command.ExecuteScalar());

        return new LocalListingDatabaseStatus(DatabasePath, count, DateTimeOffset.UtcNow);
    }

    public LocalListingSaveResult SaveEdit(UpdateListingRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Sku) && string.IsNullOrWhiteSpace(req.OfferId) && string.IsNullOrWhiteSpace(req.ListingId))
            throw new InvalidOperationException("A SKU, offer ID, or listing ID is required to save a local edit.");
        // Nobody signed in on a hosted deployment: nothing is written, and the result says so with
        // a local id of 0 rather than pointing at a row that belongs to somebody else.
        if (_scope.OwnerId is not { } owner)
            return new LocalListingSaveResult(0, req.ListingId, req.OfferId, req.Sku, DateTimeOffset.UtcNow);

        var savedAt = DateTimeOffset.UtcNow;
        var savedAtText = savedAt.ToString("O");
        var thumbnail = req.ImageUrls.FirstOrDefault() ?? "";

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = """
                DELETE FROM local_listings
                WHERE user_id = @user_id
                  AND ((@listing_id <> '' AND listing_id = @listing_id)
                    OR (@offer_id <> '' AND offer_id = @offer_id)
                    OR (@sku <> '' AND sku = @sku));
                """;
            delete.Parameters.AddWithValue("@user_id", owner);
            delete.Parameters.AddWithValue("@listing_id", req.ListingId);
            delete.Parameters.AddWithValue("@offer_id", req.OfferId);
            delete.Parameters.AddWithValue("@sku", req.Sku);
            delete.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO local_listings (
                user_id,
                listing_id,
                offer_id,
                sku,
                title,
                price,
                quantity,
                status,
                category,
                thumbnail_url,
                condition,
                condition_description,
                description,
                item_specifics_json,
                photo_urls_json,
                raw_json,
                last_updated,
                saved_at
            ) VALUES (
                @user_id,
                @listing_id,
                @offer_id,
                @sku,
                @title,
                @price,
                @quantity,
                @status,
                @category,
                @thumbnail_url,
                @condition,
                @condition_description,
                @description,
                @item_specifics_json,
                @photo_urls_json,
                @raw_json,
                @last_updated,
                @saved_at
            );

            SELECT last_insert_rowid();
            """;
        insert.Parameters.AddWithValue("@user_id", owner);
        insert.Parameters.AddWithValue("@listing_id", req.ListingId);
        insert.Parameters.AddWithValue("@offer_id", req.OfferId);
        insert.Parameters.AddWithValue("@sku", req.Sku);
        insert.Parameters.AddWithValue("@title", req.Title);
        insert.Parameters.AddWithValue("@price", req.Price);
        insert.Parameters.AddWithValue("@quantity", req.Quantity);
        insert.Parameters.AddWithValue("@status", "LOCAL_EDIT");
        insert.Parameters.AddWithValue("@category", string.IsNullOrWhiteSpace(req.Category) ? req.CategoryId : req.Category);
        insert.Parameters.AddWithValue("@thumbnail_url", thumbnail);
        insert.Parameters.AddWithValue("@condition", req.Condition);
        insert.Parameters.AddWithValue("@condition_description", req.ConditionDescription);
        insert.Parameters.AddWithValue("@description", req.Description);
        insert.Parameters.AddWithValue("@item_specifics_json", JsonSerializer.Serialize(req.ItemSpecifics, JsonOptions));
        insert.Parameters.AddWithValue("@photo_urls_json", JsonSerializer.Serialize(req.ImageUrls, JsonOptions));
        insert.Parameters.AddWithValue("@raw_json", JsonSerializer.Serialize(req, JsonOptions));
        insert.Parameters.AddWithValue("@last_updated", savedAtText);
        insert.Parameters.AddWithValue("@saved_at", savedAtText);

        var localId = Convert.ToInt64(insert.ExecuteScalar());
        transaction.Commit();

        return new LocalListingSaveResult(localId, req.ListingId, req.OfferId, req.Sku, savedAt);
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS local_listings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                -- Whose listing this is. 0 on a desktop database, where there is one seller.
                user_id INTEGER NOT NULL DEFAULT 0,
                listing_id TEXT NOT NULL DEFAULT '',
                offer_id TEXT NOT NULL DEFAULT '',
                sku TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                price NUMERIC NOT NULL DEFAULT 0,
                quantity INTEGER NOT NULL DEFAULT 0,
                status TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '',
                thumbnail_url TEXT NOT NULL DEFAULT '',
                condition TEXT NOT NULL DEFAULT '',
                condition_description TEXT NOT NULL DEFAULT '',
                description TEXT NOT NULL DEFAULT '',
                item_specifics_json TEXT NOT NULL DEFAULT '{}',
                photo_urls_json TEXT NOT NULL DEFAULT '[]',
                raw_json TEXT NOT NULL DEFAULT '{}',
                last_updated TEXT NOT NULL DEFAULT '',
                saved_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE INDEX IF NOT EXISTS ix_local_listings_listing_id
                ON local_listings(listing_id);

            CREATE INDEX IF NOT EXISTS ix_local_listings_sku
                ON local_listings(sku);
            """;
        command.ExecuteNonQuery();

        // Adds user_id to a database written before this change. Its rows default to the desktop
        // owner, which is who saved every one of them.
        UserOwnedTable.Migrate(connection, "local_listings");
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}

public sealed record LocalListingDatabaseStatus(
    string DatabasePath,
    int ListingCount,
    DateTimeOffset CheckedAt);

public sealed record LocalListingSaveResult(
    long LocalId,
    string ListingId,
    string OfferId,
    string Sku,
    DateTimeOffset SavedAt);
