using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The three earned milestones behind <see cref="OnboardingProgress"/>, and the two one-time flags
/// beside them.
/// </summary>
/// <remarks>
/// <para>
/// These have to survive a restart or the panel lies. <see cref="ActionLog"/> — the only other
/// record that a comp lookup or a publish ever happened — lives in memory and holds a hundred
/// entries, so a seller who priced something on Monday and reopened the app on Tuesday would be
/// told to go and do it again. So: one row per milestone in the app's own database, alongside the
/// fee profile and the cost basis, migrated with them by <see cref="AppPaths"/>.
/// </para>
/// <para>
/// The first time wins. <see cref="Reach"/> is an insert that ignores a conflict, so the stored
/// timestamp is the date the seller first did the thing rather than the last time they did it —
/// "you priced your first item on Aug 3" stays true after the hundredth.
/// </para>
/// </remarks>
public sealed class OnboardingStore
{
    private const string DismissedKey = "flag:dismissed";
    private const string WelcomeSeenKey = "flag:welcome_seen";
    private const string KeyCheckKey = "flag:ai_key_check";

    private readonly string _databasePath;
    private readonly object _gate = new();

    /// <summary>
    /// Whether this database has the <c>note</c> column yet. Installs created before the key check
    /// existed do not, and the migration that adds it is allowed to fail — a store that can still
    /// record the three milestones and both flags is worth far more than one that throws on
    /// construction and takes the whole app down with it.
    /// </summary>
    private bool _hasNote;

    public OnboardingStore(ListingDatabase database) : this(database.DatabasePath) { }

    public OnboardingStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>
    /// Records that a milestone has been reached, keeping the earliest time it ever was.
    /// </summary>
    /// <returns>
    /// True when this call is what recorded it — the moment worth celebrating in the UI. False for
    /// an unknown id and for the second and every later time.
    /// </returns>
    /// <remarks>
    /// Called from inside endpoints that have already done their real work, so it must not be able
    /// to fail them: a locked database is not a reason to fail a publish that eBay accepted.
    /// </remarks>
    public bool Reach(string milestone, DateTimeOffset? at = null)
    {
        var id = OnboardingProgress.Milestones.Normalize(milestone);
        if (id is null) return false;

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO onboarding (key, reached_at) VALUES (@key, @reached_at)
                    ON CONFLICT(key) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("@key", id);
                command.Parameters.AddWithValue("@reached_at", (at ?? DateTimeOffset.UtcNow).ToString("O"));
                return command.ExecuteNonQuery() > 0;
            }
        }
        catch
        {
            // A missed tick shows the seller one extra row on a checklist. Nothing else is lost.
            return false;
        }
    }

    /// <summary>When the milestone was first reached, or null if it never was.</summary>
    public DateTimeOffset? ReachedAt(string milestone) =>
        OnboardingProgress.Milestones.Normalize(milestone) is { } id && ReadAll().TryGetValue(id, out var row)
            ? row.When
            : null;

    /// <summary>
    /// Records what Anthropic said about the saved key, replacing whatever it said last time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Last write wins here, unlike <see cref="Reach"/> — this is the current state of one key, not
    /// a milestone that happened once. A seller who fixes a rejected key and re-tests has to be able
    /// to turn the panel green again.
    /// </para>
    /// <para>
    /// <see cref="AiKeyCheck.Untested"/> clears the row rather than storing the word, which is what
    /// saving a new key does: the previous verdict was about a different key and saying anything
    /// about the new one would be a guess.
    /// </para>
    /// </remarks>
    public void RecordKeyCheck(string? state, DateTimeOffset? at = null)
    {
        var normalized = AiKeyCheck.Normalize(state);

        // Nothing to say, or nowhere to say it: an install whose migration never landed keeps
        // working, it just never shows a checked key.
        if (normalized == AiKeyCheck.Untested || !_hasNote)
        {
            if (normalized == AiKeyCheck.Untested) SetFlag(KeyCheckKey, false);
            return;
        }

        try
        {
            lock (_gate)
            {
                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO onboarding (key, reached_at, note) VALUES (@key, @reached_at, @note)
                    ON CONFLICT(key) DO UPDATE SET reached_at = @reached_at, note = @note;
                    """;
                command.Parameters.AddWithValue("@key", KeyCheckKey);
                command.Parameters.AddWithValue("@reached_at", (at ?? DateTimeOffset.UtcNow).ToString("O"));
                command.Parameters.AddWithValue("@note", normalized);
                command.ExecuteNonQuery();
            }
        }
        catch
        {
            // Same bargain as Reach: a lost verdict costs one line of explanation on a checklist.
        }
    }

    /// <summary>The last thing Anthropic said about the saved key, and when it said it.</summary>
    public (string State, DateTimeOffset? At) KeyCheck()
    {
        var rows = ReadAll();
        return rows.TryGetValue(KeyCheckKey, out var row)
            ? (AiKeyCheck.Normalize(row.Note), row.When)
            : (AiKeyCheck.Untested, null);
    }

    /// <summary>The seller closed the panel, or asked for it back.</summary>
    public void SetDismissed(bool dismissed) => SetFlag(DismissedKey, dismissed);

    /// <summary>The one-time first-run screen has been shown. Set the moment it opens, not when it closes.</summary>
    public void MarkWelcomeSeen() => SetFlag(WelcomeSeenKey, true);

    /// <summary>Everything the plan needs, in one read.</summary>
    public OnboardingProgress.Facts Facts(bool hasAiKey, bool ebayConnected)
    {
        var rows = ReadAll();
        var keyCheck = rows.TryGetValue(KeyCheckKey, out var checkRow)
            ? (State: AiKeyCheck.Normalize(checkRow.Note), At: (DateTimeOffset?)checkRow.When)
            : (State: AiKeyCheck.Untested, At: (DateTimeOffset?)null);

        return new OnboardingProgress.Facts(
            HasAiKey: hasAiKey,
            EbayConnected: ebayConnected,
            PricedAt: Get(OnboardingProgress.Milestones.Priced),
            WrittenAt: Get(OnboardingProgress.Milestones.Written),
            PublishedAt: Get(OnboardingProgress.Milestones.Published),
            Dismissed: rows.ContainsKey(DismissedKey),
            WelcomeSeen: rows.ContainsKey(WelcomeSeenKey),
            AiKeyState: keyCheck.State,
            AiKeyCheckedAt: keyCheck.At);

        DateTimeOffset? Get(string key) => rows.TryGetValue(key, out var row) ? row.When : null;
    }

    /// <summary>
    /// Forgets the three milestones and both flags, putting the panel back to its first-run state.
    /// </summary>
    /// <remarks>
    /// This is for a beta tester who wants to see the first five minutes again — the one thing
    /// nobody testing onboarding can otherwise do without deleting the database. It clears the
    /// onboarding table and nothing else, so no listing, cost, sale or credential is touched.
    /// </remarks>
    public void Reset()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM onboarding;";
            command.ExecuteNonQuery();
        }
    }

    private void SetFlag(string key, bool on)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (on)
            {
                command.CommandText = """
                    INSERT INTO onboarding (key, reached_at) VALUES (@key, @reached_at)
                    ON CONFLICT(key) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("@reached_at", DateTimeOffset.UtcNow.ToString("O"));
            }
            else
            {
                command.CommandText = "DELETE FROM onboarding WHERE key = @key;";
            }
            command.Parameters.AddWithValue("@key", key);
            command.ExecuteNonQuery();
        }
    }

    /// <param name="When">When the row was written.</param>
    /// <param name="Note">What it says, for the rows that say something. Empty for the rest.</param>
    private readonly record struct Row(DateTimeOffset When, string Note);

    private Dictionary<string, Row> ReadAll()
    {
        var rows = new Dictionary<string, Row>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = _hasNote
                ? "SELECT key, reached_at, note FROM onboarding;"
                : "SELECT key, reached_at, '' FROM onboarding;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (DateTimeOffset.TryParse(reader.GetString(1), null,
                        System.Globalization.DateTimeStyles.RoundtripKind, out var when))
                    rows[reader.GetString(0)] = new Row(when, reader.IsDBNull(2) ? "" : reader.GetString(2));
            }
        }
        catch
        {
            // Unreadable reads as "nothing done yet", which shows the full checklist. That is the
            // safe direction to be wrong in: it offers work, it never claims work was finished.
        }
        return rows;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE IF NOT EXISTS onboarding (
                    key TEXT PRIMARY KEY,
                    reached_at TEXT NOT NULL DEFAULT ''
                );
                """;
            create.ExecuteNonQuery();
        }

        // Everything here was a date until the key check, which is the first row that also has to
        // say *what* happened. One nullable text column carries it; a second table for one string
        // would be a schema for a sentence.
        if (!HasNoteColumn(connection))
        {
            try
            {
                using var alter = connection.CreateCommand();
                alter.CommandText = "ALTER TABLE onboarding ADD COLUMN note TEXT NOT NULL DEFAULT '';";
                alter.ExecuteNonQuery();
            }
            catch
            {
                // Re-read below rather than assume either outcome: another instance may have added
                // it a millisecond ago, in which case this failed and the column is still there.
            }
        }

        _hasNote = HasNoteColumn(connection);
    }

    private static bool HasNoteColumn(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(onboarding);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "note", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch
        {
            // Unreadable schema reads as "no note column", which costs the key-check state and
            // nothing else.
        }
        return false;
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
