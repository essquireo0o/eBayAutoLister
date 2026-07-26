using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>Where an in-progress listing has got to.</summary>
public static class WorkStage
{
    /// <summary>Being written. Recoverable — this is what the restore banner offers back.</summary>
    public const string Editing = "editing";

    /// <summary>Handed to eBay; the answer has not come back yet.</summary>
    public const string Publishing = "publishing";

    /// <summary>Live on eBay. Kept briefly as the duplicate-publish record, then pruned.</summary>
    public const string Published = "published";

    /// <summary>eBay refused it, or the attempt broke. Still fully recoverable.</summary>
    public const string Failed = "failed";
}

public sealed class WorkSnapshot
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Stage { get; set; } = WorkStage.Editing;
    public string Payload { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string LastError { get; set; } = "";
    public int AttemptCount { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>
/// Keeps a listing being written alive outside the browser tab, and records what has been published.
/// </summary>
/// <remarks>
/// <para>
/// The problem this solves is the most expensive one in the app and had no handling at all: a
/// listing in progress existed only in the DOM. A Claude-written title, description and item
/// specifics cost real API spend and a minute or two of waiting, and every one of them was one
/// accidental tab close, one browser refresh, or one app crash away from being gone with no trace.
/// A seller who loses that twice does not use the app a third time.
/// </para>
/// <para>
/// Server-side rather than <c>localStorage</c>, for two reasons. It survives things the browser
/// does not — a cleared cache, a different browser, the app restarting mid-publish — and it puts the
/// recovery record in the same place as the publish journal, which is what lets
/// <see cref="PublishGuard"/> answer "did this already go live?" after a restart.
/// </para>
/// <para>
/// Bounded on purpose. Autosave fires while the seller types, so this table would grow without limit
/// if nothing capped it: payloads over <see cref="MaxPayloadBytes"/> are refused, rows are keyed so
/// repeated saves of one draft overwrite rather than accumulate, and <see cref="Prune"/> drops
/// published records once they are past any use.
/// </para>
/// </remarks>
public sealed class WorkRecoveryStore
{
    /// <summary>
    /// A listing with a long description and a dozen specifics is a few tens of kilobytes. A quarter
    /// of a megabyte is generous for that, and small enough that a runaway client cannot fill the
    /// disk one autosave at a time.
    /// </summary>
    public const int MaxPayloadBytes = 256 * 1024;

    /// <summary>How many recoverable drafts to keep. Beyond this the oldest are dropped.</summary>
    public const int MaxRecoverableRows = 40;

    /// <summary>
    /// Published records outlive the duplicate window by enough to be useful after a restart, but are
    /// not a permanent history — the eBay account is the record of what is live.
    /// </summary>
    public static readonly TimeSpan PublishedRetention = TimeSpan.FromDays(2);

    /// <summary>An abandoned draft is worth keeping for a fortnight, not forever.</summary>
    public static readonly TimeSpan DraftRetention = TimeSpan.FromDays(14);

    private readonly string _databasePath;
    private readonly object _writeLock = new();

    public WorkRecoveryStore(ListingDatabase database) : this(database.DatabasePath) { }

    public WorkRecoveryStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    // ── Autosave ────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current state of a listing being written. Overwrites the previous save for the same
    /// key.
    /// </summary>
    /// <remarks>
    /// Returns rather than throws on an oversized payload. This is called from a debounced autosave
    /// the seller never sees; surfacing a hard error there would interrupt them mid-sentence to
    /// report a problem with a background save. The rejection is logged by the endpoint instead, and
    /// the seller's on-screen work is untouched either way.
    /// </remarks>
    public bool Save(WorkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Key)) return false;
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot.Payload ?? "") > MaxPayloadBytes) return false;

        var now = DateTimeOffset.UtcNow;
        snapshot.UpdatedUtc = now;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO work_in_progress
                    (key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@key, @label, @stage, @payload, @fingerprint, @listing_id, @last_error,
                     @attempt_count, @created_at, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    label = @label,
                    stage = @stage,
                    payload = @payload,
                    updated_at = @updated_at;
                """;
            command.Parameters.AddWithValue("@key", snapshot.Key);
            command.Parameters.AddWithValue("@label", Clip(snapshot.Label, 200));
            command.Parameters.AddWithValue("@stage", string.IsNullOrWhiteSpace(snapshot.Stage) ? WorkStage.Editing : snapshot.Stage);
            command.Parameters.AddWithValue("@payload", snapshot.Payload ?? "");
            command.Parameters.AddWithValue("@fingerprint", snapshot.Fingerprint ?? "");
            command.Parameters.AddWithValue("@listing_id", snapshot.ListingId ?? "");
            command.Parameters.AddWithValue("@last_error", Clip(snapshot.LastError, 600));
            command.Parameters.AddWithValue("@attempt_count", snapshot.AttemptCount);
            command.Parameters.AddWithValue("@created_at", now.ToString("O"));
            command.Parameters.AddWithValue("@updated_at", now.ToString("O"));
            command.ExecuteNonQuery();
        }

        Prune();
        return true;
    }

    public WorkSnapshot? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>
    /// Every listing that was being written and never made it live, newest first.
    /// </summary>
    /// <remarks>
    /// <c>publishing</c> is included, and that is the important case: a row still marked publishing
    /// means the app went down between sending the listing and hearing back. The seller needs both
    /// the work back and to be told the publish outcome is unknown, rather than the row being hidden
    /// on the assumption it succeeded.
    /// </remarks>
    public List<WorkSnapshot> Recoverable(int limit = 10)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE stage <> 'published'
             ORDER BY updated_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, MaxRecoverableRows));

        var rows = new List<WorkSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    public bool Discard(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM work_in_progress WHERE key = @key;";
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteNonQuery() > 0;
        }
    }

    // ── Publish journal ─────────────────────────────────────────────────────

    public void MarkPublishing(string key, string fingerprint)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE work_in_progress
                SET stage = 'publishing',
                    fingerprint = @fingerprint,
                    attempt_count = attempt_count + 1,
                    last_error = '',
                    updated_at = @updated_at
                WHERE key = @key;
                """;
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@fingerprint", fingerprint ?? "");
            command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public void MarkFailed(string key, string error)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE work_in_progress
                SET stage = 'failed', last_error = @error, updated_at = @updated_at
                WHERE key = @key;
                """;
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@error", Clip(error, 600));
            command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Records that a fingerprint went live, so a repeat of the same publish can be refused.
    /// </summary>
    /// <remarks>
    /// Writes a row even when there is no draft key. The publish may have come from a path that
    /// never autosaved, and the duplicate guard has to work regardless of how the listing was
    /// written — that protection is the whole point, and it must not depend on the client having
    /// remembered to send a key.
    /// </remarks>
    public void RecordPublished(string? key, string fingerprint, string listingId)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return;

        lock (_writeLock)
        {
            using var connection = OpenConnection();

            if (!string.IsNullOrWhiteSpace(key))
            {
                using var update = connection.CreateCommand();
                update.CommandText = """
                    UPDATE work_in_progress
                    SET stage = 'published', listing_id = @listing_id, fingerprint = @fingerprint,
                        last_error = '', updated_at = @updated_at
                    WHERE key = @key;
                    """;
                update.Parameters.AddWithValue("@key", key!);
                update.Parameters.AddWithValue("@listing_id", listingId ?? "");
                update.Parameters.AddWithValue("@fingerprint", fingerprint);
                update.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
                if (update.ExecuteNonQuery() > 0) return;
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO work_in_progress
                    (key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@key, '', 'published', '', @fingerprint, @listing_id, '', 1, @now, @now)
                ON CONFLICT(key) DO UPDATE SET
                    stage = 'published', listing_id = @listing_id, updated_at = @now;
                """;
            insert.Parameters.AddWithValue("@key", string.IsNullOrWhiteSpace(key) ? $"published-{fingerprint}" : key!);
            insert.Parameters.AddWithValue("@fingerprint", fingerprint);
            insert.Parameters.AddWithValue("@listing_id", listingId ?? "");
            insert.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// The most recent successful publish of this fingerprint inside <paramref name="window"/>, or
    /// null if there is none.
    /// </summary>
    public WorkSnapshot? FindPublished(string fingerprint, TimeSpan window)
    {
        if (string.IsNullOrWhiteSpace(fingerprint)) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE fingerprint = @fingerprint AND stage = 'published'
             ORDER BY updated_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@fingerprint", fingerprint);

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        var row = Read(reader);
        // The row is only evidence of a duplicate for as long as the window says. Past it the
        // seller listing the same item again is listing the same item again.
        return DateTimeOffset.UtcNow - row.UpdatedUtc <= window ? row : null;
    }

    /// <summary>Rows still marked publishing — an unresolved publish the app did not see finish.</summary>
    public List<WorkSnapshot> Unresolved()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE stage = 'publishing' ORDER BY updated_at DESC LIMIT 20;";
        var rows = new List<WorkSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(Read(reader));
        return rows;
    }

    // ── Housekeeping ────────────────────────────────────────────────────────

    public int Prune()
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM work_in_progress
                WHERE (stage = 'published' AND updated_at < @published_before)
                   OR (stage <> 'published' AND updated_at < @draft_before)
                   OR key IN (
                        SELECT key FROM work_in_progress
                        WHERE stage <> 'published'
                        ORDER BY updated_at DESC
                        LIMIT -1 OFFSET @keep
                   );
                """;
            var now = DateTimeOffset.UtcNow;
            command.Parameters.AddWithValue("@published_before", (now - PublishedRetention).ToString("O"));
            command.Parameters.AddWithValue("@draft_before", (now - DraftRetention).ToString("O"));
            command.Parameters.AddWithValue("@keep", MaxRecoverableRows);
            return command.ExecuteNonQuery();
        }
    }

    private const string Select = """
        SELECT key, label, stage, payload, fingerprint, listing_id, last_error,
               attempt_count, created_at, updated_at
        FROM work_in_progress
        """;

    private static WorkSnapshot Read(SqliteDataReader reader) => new()
    {
        Key = reader.GetString(0),
        Label = reader.GetString(1),
        Stage = reader.GetString(2),
        Payload = reader.GetString(3),
        Fingerprint = reader.GetString(4),
        ListingId = reader.GetString(5),
        LastError = reader.GetString(6),
        AttemptCount = reader.GetInt32(7),
        CreatedUtc = DateTimeOffset.TryParse(reader.GetString(8), out var c) ? c : DateTimeOffset.MinValue,
        UpdatedUtc = DateTimeOffset.TryParse(reader.GetString(9), out var u) ? u : DateTimeOffset.MinValue,
    };

    private static string Clip(string? value, int max)
    {
        var text = value ?? "";
        return text.Length <= max ? text : text[..max];
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS work_in_progress (
                key TEXT PRIMARY KEY,
                label TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL DEFAULT 'editing',
                payload TEXT NOT NULL DEFAULT '',
                fingerprint TEXT NOT NULL DEFAULT '',
                listing_id TEXT NOT NULL DEFAULT '',
                last_error TEXT NOT NULL DEFAULT '',
                attempt_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );

            CREATE INDEX IF NOT EXISTS ix_work_stage_updated
                ON work_in_progress(stage, updated_at);

            CREATE INDEX IF NOT EXISTS ix_work_fingerprint
                ON work_in_progress(fingerprint) WHERE fingerprint <> '';
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
