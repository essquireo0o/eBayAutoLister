using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

// Connection management for the existing, externally-maintained Marketplace.db at
// C:\INGListing\Data\Marketplace.db (populated by a separate collector process outside this
// project — see the RawJson column's serpapi.com references). This class never creates, drops,
// renames, or overwrites the database or the SoldListings table, and never writes/deletes a row.
// The one schema operation it performs is adding missing, non-destructive indexes (EnsureIndexes)
// — additive only, guarded so a locked or read-only file just skips it rather than failing startup.
public sealed class ExternalMarketplaceDb(string? databasePathOverride = null)
{
    // Real production path — the "existing configured database path". Overridable only so tests
    // can point this at a throwaway SQLite fixture; DI always resolves the parameterless default.
    public const string DefaultDatabasePath = @"C:\INGListing\Data\Marketplace.db";
    public const string SoldListingsTable = "SoldListings";

    /// <summary>
    /// Where sold comps live when the collector's own database is not on this machine — a server,
    /// or any copy of the desktop app that is not the owner's.
    /// </summary>
    /// <remarks>
    /// Before live lookups worked without a browser this fallback would have been an empty file, so
    /// the local path simply reported "unavailable" and every price came from the hosted API. Now
    /// <see cref="LiveCompsStore"/> creates and fills it on the first lookup, which is how a
    /// deployment with no collector gets a sold-comps table of its own at all — and why the rows a
    /// seller waited for are readable by the pricing path a moment later.
    /// </remarks>
    public const string FallbackFileName = "live-comps.db";

    public string DatabasePath { get; } = databasePathOverride ?? ResolveDefault();

    /// <summary>
    /// The collector's database when it is there, and a writable file under the app's own data home
    /// when it is not. Resolved once, at construction: a path that changed the moment a file
    /// appeared would have the reader and the writer looking at two different databases.
    /// </summary>
    public static string ResolveDefault() =>
        File.Exists(DefaultDatabasePath)
            ? DefaultDatabasePath
            : Path.Combine(AppPaths.DataHome, "App_Data", FallbackFileName);

    public bool DatabaseFileExists => File.Exists(DatabasePath);

    private string ReadOnlyConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadOnly
    }.ToString();

    private string ReadWriteConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        Mode = SqliteOpenMode.ReadWrite
    }.ToString();

    // Every query goes through here — read-only mode means SQLite itself refuses any INSERT/
    // UPDATE/DELETE/DDL on this connection, so a bug in a future query can't accidentally write.
    public async Task<SqliteConnection> OpenReadOnlyAsync(CancellationToken ct = default)
    {
        var connection = new SqliteConnection(ReadOnlyConnectionString);
        await connection.OpenAsync(ct);
        // Busy timeout matters even for reads: the external collector writes to this file
        // continuously, and a reader can otherwise hit "database is locked" during its commits.
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 5000;";
        await pragma.ExecuteNonQueryAsync(ct);
        return connection;
    }

    public bool SoldListingsTableExists()
    {
        using var connection = new SqliteConnection(ReadOnlyConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name;";
        command.Parameters.AddWithValue("@name", SoldListingsTable);
        return command.ExecuteScalar() is not null;
    }

    public long GetSoldListingsCount()
    {
        using var connection = new SqliteConnection(ReadOnlyConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {SoldListingsTable};";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    // Adds indexes the spec calls out as useful (Title, SearchKeyword, SoldDate, Price) — but
    // only the ones that don't already exist. ItemId already has an automatic unique index from
    // its UNIQUE column constraint, so it's deliberately not duplicated here.
    // Best-effort: a locked file, a read-only mount, or any other failure here just reports back
    // and moves on — this must never block the app from reading the data that's already there.
    public (bool Attempted, string? Error, List<string> Created) EnsureIndexes()
    {
        var created = new List<string>();
        try
        {
            using var connection = new SqliteConnection(ReadWriteConnectionString);
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                pragma.ExecuteNonQuery();
            }

            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var list = connection.CreateCommand())
            {
                list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = @tbl;";
                list.Parameters.AddWithValue("@tbl", SoldListingsTable);
                using var reader = list.ExecuteReader();
                while (reader.Read()) existing.Add(reader.GetString(0));
            }

            (string Name, string Column)[] wanted =
            [
                ("ix_soldlistings_title", "Title"),
                ("ix_soldlistings_solddate", "SoldDate"),
                ("ix_soldlistings_price", "Price"),
                ("ix_soldlistings_searchkeyword", "SearchKeyword"),
            ];

            foreach (var (name, column) in wanted)
            {
                if (existing.Contains(name)) continue;
                using var create = connection.CreateCommand();
                create.CommandText = $"CREATE INDEX IF NOT EXISTS {name} ON {SoldListingsTable}({column});";
                create.ExecuteNonQuery();
                created.Add(name);
            }

            return (true, null, created);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, created);
        }
    }
}
