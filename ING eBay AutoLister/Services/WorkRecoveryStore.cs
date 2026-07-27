using System.Text.Json;
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

    // ── What counts as work worth recovering ────────────────────────────────

    /// <summary>
    /// Labels that mean "this draft was never named" rather than naming it. The client sends one of
    /// these when the title box is empty, and older builds send them too, so they are recognised here
    /// rather than trusted as content.
    /// </summary>
    private static readonly HashSet<string> PlaceholderLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        "untitled listing", "untitled", "(untitled)", "new listing", "draft",
    };

    /// <summary>
    /// The fields that carry a seller's own work. Everything else in a listing payload — condition,
    /// package type, quantity, handling time, country, format, duration — is a control with a default
    /// the form fills in for itself, so an untouched blank tab already has values for all of them.
    /// Weighing the payload by size cannot tell those apart from real work; naming the fields can.
    /// </summary>
    private static readonly string[] ContentFields =
    {
        "title", "subtitle", "description", "conditionDescription",
        "brand", "mpn", "upc", "ean", "isbn", "sku",
        "category", "categoryId", "secondaryCategoryId",
        "price", "itemSpecifics", "imageUrls",
    };

    /// <summary>
    /// A payload the app does not recognise as a listing has to be judged by size instead. Small
    /// enough that `{}` and a couple of empty keys do not qualify, large enough that anything with
    /// actual text in it does.
    /// </summary>
    public const int MinimumOpaquePayloadChars = 40;

    /// <summary>
    /// Whether this row is worth offering back to the seller, as opposed to being the residue of a
    /// blank tab that was opened and closed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This exists because the banner's credibility is the whole feature. Opening the AI listing modal
    /// and closing it again used to leave a full-sized payload behind — every control already holds its
    /// default — so the next launch announced an "unfinished listing" called <c>Untitled listing</c>
    /// with nothing in it. A seller who is shown that twice stops reading the banner, and then the one
    /// launch where it is holding a real Claude-written listing goes unread too.
    /// </para>
    /// <para>
    /// Only <see cref="WorkStage.Editing"/> rows are judged. A row that reached <c>publishing</c> or
    /// <c>failed</c> means something was actually sent to eBay, and that must be surfaced whatever its
    /// payload looks like — an unresolved publish is the most important row in the table.
    /// </para>
    /// </remarks>
    public static bool IsWorthRecovering(string? stage, string? label, string? payload)
    {
        if (!string.IsNullOrWhiteSpace(stage) && stage != WorkStage.Editing) return true;

        // A name the seller typed is content in its own right, even before the rest is filled in.
        var name = (label ?? "").Trim();
        if (name.Length > 0 && !PlaceholderLabels.Contains(name)) return true;

        var text = (payload ?? "").Trim();
        if (text.Length == 0) return false;

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                var recognised = false;
                foreach (var field in ContentFields)
                {
                    if (!document.RootElement.TryGetProperty(field, out var value)) continue;
                    recognised = true;
                    if (HasContent(value)) return true;
                }

                // Recognised as a listing payload and every content field was empty: a blank tab.
                // Unrecognised means some other caller's shape, which this list cannot speak for — fall
                // through to size rather than throw that caller's work away.
                if (recognised) return false;
            }
        }
        catch (JsonException)
        {
            // Not JSON. Judged by size below, same as an unrecognised shape.
        }

        return text.Length >= MinimumOpaquePayloadChars;
    }

    /// <inheritdoc cref="IsWorthRecovering(string?, string?, string?)"/>
    public static bool IsWorthRecovering(WorkSnapshot snapshot) =>
        IsWorthRecovering(snapshot.Stage, snapshot.Label, snapshot.Payload);

    private static bool HasContent(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
        // 0 is what the form reports for an untyped price, not a price of zero.
        JsonValueKind.Number => value.TryGetDouble(out var number) && number != 0,
        JsonValueKind.Array  => value.EnumerateArray().Any(HasContent),
        JsonValueKind.Object => value.EnumerateObject().Any(p => HasContent(p.Value)),
        JsonValueKind.True   => true,
        _ => false,
    };

    // ── Autosave ────────────────────────────────────────────────────────────

    /// <summary>
    /// Saves the current state of a listing being written. Overwrites the previous save for the same
    /// key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns rather than throws on an oversized payload. This is called from a debounced autosave
    /// the seller never sees; surfacing a hard error there would interrupt them mid-sentence to
    /// report a problem with a background save. The rejection is logged by the endpoint instead, and
    /// the seller's on-screen work is untouched either way.
    /// </para>
    /// <para>
    /// An empty draft is refused for the same reason and by the same route: nothing is written, so
    /// nothing is offered back. Note that this refuses the <em>write</em>, not the existing row — a
    /// seller who clears the form keeps the last save that had something in it.
    /// </para>
    /// </remarks>
    public bool Save(WorkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Key)) return false;
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot.Payload ?? "") > MaxPayloadBytes) return false;
        if (!IsWorthRecovering(snapshot)) return false;

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
    /// <para>
    /// <c>publishing</c> is included, and that is the important case: a row still marked publishing
    /// means the app went down between sending the listing and hearing back. The seller needs both
    /// the work back and to be told the publish outcome is unknown, rather than the row being hidden
    /// on the assumption it succeeded.
    /// </para>
    /// <para>
    /// Empty drafts are left out, and the filter is applied here as well as in <see cref="Save"/>
    /// because rows written before that rule existed are still in the table. Judging them on read
    /// means the banner is clean on the next launch rather than the next save.
    /// </para>
    /// </remarks>
    public List<WorkSnapshot> Recoverable(int limit = 10)
    {
        var wanted = Math.Clamp(limit, 1, MaxRecoverableRows);

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Fetched up to the table's own cap rather than to `wanted`, so drafts dropped by the
        // worth-recovering filter do not leave the banner short of rows it could have shown.
        command.CommandText = Select + """
             WHERE stage <> 'published'
             ORDER BY updated_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", MaxRecoverableRows);

        var rows = new List<WorkSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = Read(reader);
            if (!IsWorthRecovering(row)) continue;
            rows.Add(row);
            if (rows.Count == wanted) break;
        }
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

    /// <summary>
    /// Throws away every recoverable draft at once and reports how many went. Returns the number
    /// deleted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A seller who comes back to a banner holding eight drafts they have finished with should not
    /// have to confirm eight separate discards to get their dashboard back — and the one who does that
    /// once will leave the rest sitting there forever instead.
    /// </para>
    /// <para>
    /// Deliberately scoped to the same rows <see cref="Recoverable"/> offers: <c>published</c> rows are
    /// the publish journal, and <see cref="PublishGuard"/> reads them to answer "did this already go
    /// live?". Clearing the banner must not cost the seller their duplicate protection.
    /// </para>
    /// </remarks>
    public int DiscardAll()
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM work_in_progress WHERE stage <> 'published';";
            return command.ExecuteNonQuery();
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

    public int Prune() => PruneRetention() + PruneEmptyDrafts();

    private int PruneRetention()
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

    /// <summary>
    /// Deletes drafts that hold nothing worth recovering, and reports how many went.
    /// </summary>
    /// <remarks>
    /// Reads then deletes by key, because "is there anything in this?" is a question about the shape of
    /// a JSON payload and SQL cannot answer it — a blank tab's payload is the same size as a filled
    /// one's. The scan is over drafts only, and drafts are capped at
    /// <see cref="MaxRecoverableRows"/>, so this stays a few dozen rows however long the app runs.
    /// </remarks>
    private int PruneEmptyDrafts()
    {
        var doomed = new List<string>();

        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT key, label, payload FROM work_in_progress WHERE stage = 'editing';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (!IsWorthRecovering(WorkStage.Editing, reader.GetString(1), reader.GetString(2)))
                    doomed.Add(reader.GetString(0));
            }
        }

        if (doomed.Count == 0) return 0;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var removed = 0;
            foreach (var key in doomed)
            {
                using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM work_in_progress WHERE key = @key AND stage = 'editing';";
                delete.Parameters.AddWithValue("@key", key);
                removed += delete.ExecuteNonQuery();
            }
            transaction.Commit();
            return removed;
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
