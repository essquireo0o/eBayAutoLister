using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Remembers which eBay category the seller published each listing under, so the next listing like
/// it does not have to ask.
/// </summary>
/// <remarks>
/// <para>
/// Only successful publishes and accepted drafts are written here, and that is the point: a
/// category typed into a draft that was then abandoned is not a decision. What is stored is the
/// seller's own choice on a listing that went the whole way and came out live — the one version of
/// the answer that has been tested against eBay rather than merely typed.
/// </para>
/// <para>
/// Rows collapse on <see cref="CategorySuggester.Key"/> — the significant words of the title,
/// sorted — so listing the same model forty times is one row with a count of forty rather than
/// forty rows that all say the same thing. The table is capped and pruned oldest-first: this is a
/// convenience, and it is not allowed to grow into a liability on the seller's disk.
/// </para>
/// </remarks>
public sealed class CategoryMemoryStore
{
    /// <summary>Rows kept. Beyond this the oldest are dropped — a category the seller has not used
    /// in five hundred distinct listings is not the default they are looking for.</summary>
    public const int MaxRows = 500;

    /// <summary>How many rows the suggester is handed. Ranking is linear in this.</summary>
    public const int DefaultReadLimit = 250;

    private readonly string _databasePath;
    private readonly object _writeLock = new();

    public CategoryMemoryStore(ListingDatabase database) : this(database.DatabasePath) { }

    public CategoryMemoryStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>
    /// Records that this title was published under this category. Silently does nothing when there
    /// is nothing worth remembering.
    /// </summary>
    /// <remarks>
    /// Never throws. This is called on the success path of a publish, and a listing that went live
    /// must not be reported as a failure because a convenience table could not be written.
    /// </remarks>
    public void Remember(string? title, string? categoryId, string? categoryName)
    {
        var id = (categoryId ?? "").Trim();
        // eBay category IDs are numeric. A display name that has landed in the ID field is a bug
        // upstream, and storing it would teach the app to suggest something unpublishable.
        if (id.Length == 0 || !id.All(char.IsDigit)) return;

        var key = CategorySuggester.Key(title);
        if (key.Length == 0) return;   // a title with no significant words teaches nothing

        try
        {
            lock (_writeLock)
            {
                using var connection = OpenConnection();
                using var upsert = connection.CreateCommand();
                upsert.CommandText = """
                    INSERT INTO listing_category_memory
                        (title_key, category_id, title, category_name, use_count, last_used_at)
                    VALUES
                        (@key, @id, @title, @name, 1, @now)
                    ON CONFLICT(title_key, category_id) DO UPDATE SET
                        use_count    = use_count + 1,
                        title        = excluded.title,
                        -- A blank name never overwrites one the app already knew.
                        category_name = CASE WHEN excluded.category_name <> ''
                                             THEN excluded.category_name ELSE category_name END,
                        last_used_at = excluded.last_used_at;
                    """;
                upsert.Parameters.AddWithValue("@key", key);
                upsert.Parameters.AddWithValue("@id", id);
                upsert.Parameters.AddWithValue("@title", (title ?? "").Trim());
                upsert.Parameters.AddWithValue("@name", (categoryName ?? "").Trim());
                upsert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
                upsert.ExecuteNonQuery();

                using var prune = connection.CreateCommand();
                prune.CommandText = """
                    DELETE FROM listing_category_memory
                    WHERE rowid NOT IN (
                        SELECT rowid FROM listing_category_memory
                        ORDER BY last_used_at DESC, rowid DESC
                        LIMIT @keep
                    );
                    """;
                prune.Parameters.AddWithValue("@keep", MaxRows);
                prune.ExecuteNonQuery();
            }
        }
        // Deliberately every exception, not just SQLite's. This runs on the success path of a
        // publish that has already gone live, and a convenience table is never a reason to tell
        // the seller their listing failed.
        catch (Exception) { }
    }

    /// <summary>What the seller has published, most recently used first.</summary>
    public List<CategoryUse> Recent(int limit = DefaultReadLimit)
    {
        var uses = new List<CategoryUse>();
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT category_id, category_name, title, use_count, last_used_at
                FROM listing_category_memory
                ORDER BY last_used_at DESC, rowid DESC
                LIMIT @limit;
                """;
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, MaxRows));

            using var reader = command.ExecuteReader();
            while (reader.Read())
                uses.Add(new CategoryUse
                {
                    CategoryId   = reader.GetString(0),
                    CategoryName = reader.GetString(1),
                    Title        = reader.GetString(2),
                    UseCount     = reader.GetInt32(3),
                    LastUsedUtc  = DateTimeOffset.TryParse(reader.GetString(4), out var t) ? t : DateTimeOffset.MinValue,
                });
        }
        catch (SqliteException) { /* no memory is the same as an empty memory */ }
        return uses;
    }

    public int Count()
    {
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM listing_category_memory;";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch (SqliteException) { return 0; }
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS listing_category_memory (
                title_key TEXT NOT NULL,
                category_id TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                category_name TEXT NOT NULL DEFAULT '',
                use_count INTEGER NOT NULL DEFAULT 1,
                last_used_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (title_key, category_id)
            );

            CREATE INDEX IF NOT EXISTS ix_category_memory_last_used
                ON listing_category_memory(last_used_at DESC);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
