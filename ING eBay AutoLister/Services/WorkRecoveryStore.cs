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

/// <summary>What an autosave did, and why, when it did not simply save.</summary>
public enum WorkSaveOutcome
{
    /// <summary>Written.</summary>
    Saved,

    /// <summary>Nothing in it worth offering back — a blank tab, not a failure.</summary>
    Empty,

    /// <summary>Over <see cref="WorkRecoveryStore.MaxPayloadBytes"/>. Nothing was written.</summary>
    TooLarge,

    /// <summary>
    /// The row under this key is the record that a listing went live, and an autosave is not allowed
    /// to write over it. See the remarks on <see cref="WorkRecoveryStore.SaveWithOutcome"/>.
    /// </summary>
    PublishJournal,
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
/// <para>
/// Owned per seller, like every other table that holds somebody's work — see <see cref="UserScope"/>.
/// This one was the last without an owner column, and it was the one that mattered most once
/// <see cref="LatestResumable"/> existed: the recovery list needed a click before it put anything on
/// screen, but auto-restore does not, so an unscoped table would have opened the AI Listing screen on
/// a stranger's half-written listing — their title, their price, their photos — with nobody asking
/// for it.
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
    private readonly UserScope _scope;
    private readonly object _writeLock = new();

    public WorkRecoveryStore(ListingDatabase database, UserScope? scope = null) : this(database.DatabasePath, scope) { }

    public WorkRecoveryStore(string databasePath, UserScope? scope = null)
    {
        _databasePath = databasePath;
        _scope        = scope ?? UserScope.Desktop;
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
    public bool Save(WorkSnapshot snapshot) => SaveWithOutcome(snapshot) == WorkSaveOutcome.Saved;

    /// <inheritdoc cref="Save(WorkSnapshot)"/>
    /// <remarks>
    /// <para>
    /// <b>An autosave saves work. It never moves a listing's publish stage.</b> That rule is the
    /// whole of the difference between this and what it replaced, and it was not a theoretical
    /// concern: the client presses Publish, the browser flushes the current text one last time, and
    /// those two requests race. The old <c>ON CONFLICT DO UPDATE</c> wrote <c>stage = @stage</c>, and
    /// the client always sends <c>editing</c> — so an autosave landing a moment after
    /// <see cref="MarkPublishing"/> quietly reset the row to <c>editing</c>, and one landing after
    /// <see cref="RecordPublished"/> reset it from <c>published</c>.
    /// </para>
    /// <para>
    /// Both losses are silent and both are expensive. A reset <c>publishing</c> row is no longer an
    /// unresolved publish, so an app that dies mid-publish comes back with nothing telling the
    /// seller the outcome is unknown. A reset <c>published</c> row is no longer found by
    /// <see cref="FindPublished"/>, so <see cref="PublishGuard"/>'s duplicate brake is gone and the
    /// seller's next attempt puts a second live listing up for the same physical item.
    /// </para>
    /// <para>
    /// So: stage is written on the insert, where it is this row's first and only claim about itself,
    /// and never on the update. A <c>published</c> row is refused outright rather than merely having
    /// its stage preserved — its <c>updated_at</c> is what the duplicate window is measured from,
    /// and an autosave bumping that would silently extend the window past the moment it should end.
    /// </para>
    /// </remarks>
    public WorkSaveOutcome SaveWithOutcome(WorkSnapshot snapshot)
    {
        if (string.IsNullOrWhiteSpace(snapshot.Key)) return WorkSaveOutcome.Empty;
        if (System.Text.Encoding.UTF8.GetByteCount(snapshot.Payload ?? "") > MaxPayloadBytes) return WorkSaveOutcome.TooLarge;
        if (!IsWorthRecovering(snapshot)) return WorkSaveOutcome.Empty;
        // Nobody signed in on a hosted deployment: nothing is written. See UserScope.
        if (_scope.OwnerId is not { } owner) return WorkSaveOutcome.Empty;

        var now = DateTimeOffset.UtcNow;
        snapshot.UpdatedUtc = now;

        int written;
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO work_in_progress
                    (user_id, key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@user_id, @key, @label, @stage, @payload, @fingerprint, @listing_id, @last_error,
                     @attempt_count, @created_at, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    label = @label,
                    payload = @payload,
                    updated_at = @updated_at
                WHERE work_in_progress.stage <> 'published'
                  AND work_in_progress.user_id = @user_id;
                """;
            command.Parameters.AddWithValue("@user_id", owner);
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
            written = command.ExecuteNonQuery();
        }

        // Nothing changed means the conflict update was filtered out: the key belongs to the publish
        // journal now, or — impossible in practice, since keys are random per browser — to somebody
        // else. Reported rather than swallowed, so the client can start a fresh draft instead of
        // autosaving into a row that will refuse it for the rest of the session.
        if (written == 0) return WorkSaveOutcome.PublishJournal;

        Prune();
        return WorkSaveOutcome.Saved;
    }

    public WorkSnapshot? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        if (_scope.OwnerId is not { } owner) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE key = @key AND user_id = @user_id;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@user_id", owner);
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
        if (_scope.OwnerId is not { } owner) return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Fetched up to the table's own cap rather than to `wanted`, so drafts dropped by the
        // worth-recovering filter do not leave the banner short of rows it could have shown.
        command.CommandText = Select + """
             WHERE stage <> 'published' AND user_id = @user_id
             ORDER BY updated_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", MaxRecoverableRows);
        command.Parameters.AddWithValue("@user_id", owner);

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

    /// <summary>
    /// The one draft the AI Listing screen puts back on screen when it opens onto an empty form, or
    /// null when there is nothing to resume.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Autosave already kept every field of an unfinished listing. Getting it back was the half that
    /// was missing: the seller had to notice a <em>Recover</em> button, open it, and pick a row. A
    /// safety net nobody knows to reach for catches nobody — the work was saved and lost anyway.
    /// </para>
    /// <para>
    /// Deliberately narrower than <see cref="Recoverable"/> on three counts, and each one is the
    /// difference between a helpful restore and a harmful one:
    /// </para>
    /// <list type="bullet">
    /// <item><b>Editing only.</b> A <c>publishing</c> or <c>failed</c> row is a listing that may be
    /// live on eBay right now, and it has its own urgent dashboard banner saying so. Quietly opening
    /// one in the editor invites the seller to press Publish on an item that already sold — the exact
    /// double-listing this app's publish guard exists to prevent. Those keep the banner and the
    /// deliberate click.</item>
    /// <item><b>Something to show.</b> A publish is journalled even when its draft never reached the
    /// store, so some rows carry an outcome and no listing. Restoring one of those means clearing the
    /// screen and putting nothing back.</item>
    /// <item><b>The newest one.</b> Not a list. The screen holds one listing, and "carry on where I
    /// left off" has exactly one answer.</item>
    /// </list>
    /// <para>
    /// The row keeps its key, and the caller adopts it — that is what makes opening the screen twice
    /// show the same draft rather than a second copy of it, and what stops a restored draft being
    /// offered back again next launch as a row of its own.
    /// </para>
    /// </remarks>
    public WorkSnapshot? LatestResumable()
    {
        if (_scope.OwnerId is not { } owner) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE stage = 'editing' AND user_id = @user_id
             ORDER BY updated_at DESC
             LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", MaxRecoverableRows);
        command.Parameters.AddWithValue("@user_id", owner);

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var row = Read(reader);
            if (!IsWorthRecovering(row)) continue;
            if (!HasRestorableForm(row.Payload)) continue;
            return row;
        }
        return null;
    }

    /// <summary>
    /// Whether this payload is a listing the form can actually be filled in from.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>hasRestorableContent</c> in app.js. Stricter than
    /// <see cref="IsWorthRecovering(WorkSnapshot)"/>, which lets a bare label through: a row named
    /// but never filled in is worth offering in a list the seller chose to open, and is not worth
    /// clearing the screen for on its own.
    /// </remarks>
    private static bool HasRestorableForm(string? payload)
    {
        var text = (payload ?? "").Trim();
        if (text.Length < 2) return false;
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.EnumerateObject().Any();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public bool Discard(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        if (_scope.OwnerId is not { } owner) return false;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM work_in_progress WHERE key = @key AND user_id = @user_id;";
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@user_id", owner);
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
        if (_scope.OwnerId is not { } owner) return 0;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM work_in_progress WHERE stage <> 'published' AND user_id = @user_id;";
            command.Parameters.AddWithValue("@user_id", owner);
            return command.ExecuteNonQuery();
        }
    }

    // ── Publish journal ─────────────────────────────────────────────────────

    /// <summary>
    /// Records that this listing has been handed to eBay and the answer is still outstanding.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Creates the row when there isn't one, which is the case that matters. This used to be an
    /// <c>UPDATE … WHERE key = @key</c> and nothing else — so a publish whose draft had never
    /// reached the store affected no rows and was journalled nowhere. That is precisely the seller
    /// whose autosave had been failing: the one publish that most needs a safety net was the one
    /// publish that had none, and an app that died mid-send came back with no record that anything
    /// had been sent at all.
    /// </para>
    /// <para>
    /// <paramref name="label"/> is what makes such a row useful rather than merely present. The
    /// recovery banner offers a <em>Check eBay</em> button on an unresolved publish, and that check
    /// matches on the listing's title — a journal row with no title is a row the seller can be told
    /// about but cannot resolve.
    /// </para>
    /// </remarks>
    public void MarkPublishing(string key, string fingerprint, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_scope.OwnerId is not { } owner) return;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO work_in_progress
                    (user_id, key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@user_id, @key, @label, 'publishing', '', @fingerprint, '', '', 1, @updated_at, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    stage = 'publishing',
                    fingerprint = @fingerprint,
                    -- A label only when this row has none: the draft's own title is the seller's
                    -- wording and outranks whatever the publish path happened to carry.
                    label = CASE WHEN work_in_progress.label = '' THEN @label ELSE work_in_progress.label END,
                    attempt_count = work_in_progress.attempt_count + 1,
                    last_error = '',
                    updated_at = @updated_at
                WHERE work_in_progress.user_id = @user_id;
                """;
            command.Parameters.AddWithValue("@user_id", owner);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@label", Clip(label, 200));
            command.Parameters.AddWithValue("@fingerprint", fingerprint ?? "");
            command.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    /// <summary>Records that the publish under this key did not go through, and why.</summary>
    /// <remarks>
    /// Creates the row when there isn't one, for the same reason as <see cref="MarkPublishing"/>: a
    /// failed publish the seller is never shown is a listing they believe they made.
    /// </remarks>
    public void MarkFailed(string key, string error, string? label = null)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        if (_scope.OwnerId is not { } owner) return;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO work_in_progress
                    (user_id, key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@user_id, @key, @label, 'failed', '', '', '', @error, 1, @updated_at, @updated_at)
                ON CONFLICT(key) DO UPDATE SET
                    stage = 'failed',
                    label = CASE WHEN work_in_progress.label = '' THEN @label ELSE work_in_progress.label END,
                    last_error = @error,
                    updated_at = @updated_at
                WHERE work_in_progress.user_id = @user_id;
                """;
            command.Parameters.AddWithValue("@user_id", owner);
            command.Parameters.AddWithValue("@key", key);
            command.Parameters.AddWithValue("@label", Clip(label, 200));
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
        if (_scope.OwnerId is not { } owner) return;

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
                    WHERE key = @key AND user_id = @user_id;
                    """;
                update.Parameters.AddWithValue("@key", key!);
                update.Parameters.AddWithValue("@user_id", owner);
                update.Parameters.AddWithValue("@listing_id", listingId ?? "");
                update.Parameters.AddWithValue("@fingerprint", fingerprint);
                update.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
                if (update.ExecuteNonQuery() > 0) return;
            }

            // The owner is in the fallback key, not just the column. Two sellers publishing the same
            // item — same title, category, price — produce the same fingerprint, and a key without
            // the owner in it would make the second publish collide with the first seller's journal
            // row instead of writing its own. The DO UPDATE would then be filtered out by the owner
            // check and the second seller would silently have no duplicate brake at all.
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO work_in_progress
                    (user_id, key, label, stage, payload, fingerprint, listing_id, last_error,
                     attempt_count, created_at, updated_at)
                VALUES
                    (@user_id, @key, '', 'published', '', @fingerprint, @listing_id, '', 1, @now, @now)
                ON CONFLICT(key) DO UPDATE SET
                    stage = 'published', listing_id = @listing_id, updated_at = @now
                WHERE work_in_progress.user_id = @user_id;
                """;
            insert.Parameters.AddWithValue("@user_id", owner);
            insert.Parameters.AddWithValue("@key", string.IsNullOrWhiteSpace(key) ? $"published-{owner}-{fingerprint}" : key!);
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
        // Per seller, and both directions matter: one seller's publish must not brake another's, and
        // it must not be the row the app points them at either — that listing ID is not theirs.
        if (_scope.OwnerId is not { } owner) return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + """
             WHERE fingerprint = @fingerprint AND stage = 'published' AND user_id = @user_id
             ORDER BY updated_at DESC LIMIT 1;
            """;
        command.Parameters.AddWithValue("@fingerprint", fingerprint);
        command.Parameters.AddWithValue("@user_id", owner);

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
        if (_scope.OwnerId is not { } owner) return [];

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = Select + " WHERE stage = 'publishing' AND user_id = @user_id ORDER BY updated_at DESC LIMIT 20;";
        command.Parameters.AddWithValue("@user_id", owner);
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
            // Retention is time-based and applies to everyone: a fortnight-old draft is stale whoever
            // wrote it. The row cap is not — counted per seller, because a global "newest 40" lets a
            // busy account push a quiet one's only unfinished listing out of the table.
            command.CommandText = """
                DELETE FROM work_in_progress
                WHERE (stage = 'published' AND updated_at < @published_before)
                   OR (stage <> 'published' AND updated_at < @draft_before)
                   OR key IN (
                        SELECT key FROM (
                            SELECT key, ROW_NUMBER() OVER (
                                PARTITION BY user_id ORDER BY updated_at DESC
                            ) AS rank_for_user
                            FROM work_in_progress
                            WHERE stage <> 'published'
                        )
                        WHERE rank_for_user > @keep
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
                -- Whose listing this is. 0 on a desktop database, where there is only the one seller.
                user_id INTEGER NOT NULL DEFAULT 0,
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
            """;
        command.ExecuteNonQuery();

        // The owner column before the indexes that use it — on an old database it is not there yet.
        UserOwnedTable.Migrate(connection, "work_in_progress");

        using var indexes = connection.CreateCommand();
        indexes.CommandText = """
            -- Superseded by the owner-first pairs below; every query now filters on the owner first.
            DROP INDEX IF EXISTS ix_work_stage_updated;
            DROP INDEX IF EXISTS ix_work_fingerprint;

            CREATE INDEX IF NOT EXISTS ix_work_user_stage_updated
                ON work_in_progress(user_id, stage, updated_at);

            CREATE INDEX IF NOT EXISTS ix_work_user_fingerprint
                ON work_in_progress(user_id, fingerprint) WHERE fingerprint <> '';
            """;
        indexes.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
