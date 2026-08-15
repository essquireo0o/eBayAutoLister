using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>One issued session: which account it belongs to, when it started, and whether it still counts.</summary>
/// <param name="Id">The opaque identifier carried in the cookie's <c>sid</c> claim. 256 bits of randomness.</param>
/// <param name="RevokedAt">When it was withdrawn, or null while it is live.</param>
public sealed record SessionRecord(string Id, long UserId, DateTimeOffset IssuedAt,
                                   DateTimeOffset LastSeenAt, DateTimeOffset? RevokedAt);

/// <summary>
/// The list of sessions the server is currently prepared to honour, so that a signed cookie is a
/// claim to be checked rather than a verdict to be obeyed.
/// </summary>
/// <remarks>
/// <para>
/// Cookie authentication on its own has no such list. The ticket is encrypted with the data
/// protection key ring and the server believes whatever a ticket it can decrypt says, for the whole
/// fourteen days of <c>ExpireTimeSpan</c>. Two consequences follow, and this class exists for both.
/// </para>
/// <para>
/// Signing out could only ever delete the browser's copy. <c>SignOutAsync</c> emits a
/// <c>Set-Cookie</c> that expires <c>ing_session</c>, which is a fine answer for the seller who
/// clicked the button on their own laptop and no answer at all for the one who clicked it because
/// they had just used a shared machine — anybody who copied the cookie value before it was cleared
/// still holds a ticket the server decrypts happily, for a fortnight. Revoking the row here is what
/// makes signing out mean something on the server side: the ticket still decrypts, the session it
/// names is gone, and <see cref="IsLive"/> refuses it.
/// </para>
/// <para>
/// The other consequence is session fixation. A fresh <see cref="Issue"/> on every sign-in means the
/// identifier a session is known by is chosen by the server after the password was checked and never
/// before it, so a value planted in the browser ahead of time cannot survive into the authenticated
/// session — it names a row that does not exist. <see cref="HostedAuth"/> also revokes whatever
/// session the caller arrived holding, so signing in while already signed in retires the old one
/// rather than leaving two live tickets for one account.
/// </para>
/// <para>
/// It costs one indexed read of a single-row primary key per authenticated request. That is the
/// price of the cookie being checkable at all, and it is the same database every other per-user
/// table in this app already lives in — putting it in memory would empty the list on every deploy
/// and every reboot, which on a hosted box means "signing out is undone by the next release".
/// </para>
/// </remarks>
public sealed class SessionStore
{
    /// <summary>The claim the session id travels in. Not a standard claim type; there isn't one.</summary>
    public const string SessionIdClaim = "sid";

    /// <summary>
    /// The longest a session may live no matter how much it is used. The cookie's own sliding
    /// fourteen days can be extended forever by a browser that keeps calling; this cannot, so a
    /// stolen cookie has an end date even if the thief is careful to keep it warm.
    /// </summary>
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(30);

    private readonly string _databasePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lifetime;
    private readonly object _gate = new();

    /// <summary>When the dead rows were last swept. See <see cref="Prune"/>.</summary>
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public SessionStore(ListingDatabase database) : this(database.DatabasePath) { }

    /// <param name="clock">Overridable so a test can watch a session age out without waiting a month.</param>
    /// <param name="lifetime">Overridable for the same reason. Production uses <see cref="AbsoluteLifetime"/>.</param>
    public SessionStore(string databasePath, Func<DateTimeOffset>? clock = null, TimeSpan? lifetime = null)
    {
        _databasePath = databasePath;
        _clock        = clock ?? (() => DateTimeOffset.UtcNow);
        _lifetime     = lifetime ?? AbsoluteLifetime;
        Initialize();
    }

    /// <summary>Now, by this store's clock.</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>
    /// Starts a session for an account and returns its identifier. Called once per successful
    /// sign-in and never anywhere else — an identifier that can be minted without a password is
    /// not an authentication.
    /// </summary>
    public string Issue(long userId)
    {
        // 256 bits from the CSPRNG. Base64url so it survives a claim, a cookie and a log line
        // without escaping, and long enough that guessing one is not a strategy.
        var id  = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = _clock();

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO sessions (id, user_id, issued_at, last_seen_at, revoked_at)
                VALUES (@id, @user_id, @at, @at, NULL);
                """;
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@user_id", userId);
            insert.Parameters.AddWithValue("@at", Stamp(now));
            insert.ExecuteNonQuery();

            Prune(now);
        }

        return id;
    }

    /// <summary>
    /// Whether this session may still act for this account. False for one that was never issued,
    /// one that has been signed out, one older than <see cref="AbsoluteLifetime"/>, and one whose
    /// row belongs to a different account than the ticket claims.
    /// </summary>
    /// <remarks>
    /// The user id is checked and not merely read. Both halves come out of the same encrypted
    /// ticket, so they cannot disagree unless the ticket was forged — but the whole point of this
    /// class is to stop deciding things by trusting the ticket, and a mismatch is free to detect.
    /// </remarks>
    public bool IsLive(string? sessionId, long userId)
    {
        if (string.IsNullOrEmpty(sessionId)) return false;

        var row = Find(sessionId);
        if (row is null) return false;
        if (row.UserId != userId) return false;
        if (row.RevokedAt is not null) return false;

        return _clock() - row.IssuedAt < _lifetime;
    }

    /// <summary>The row behind an identifier, or null when there is none.</summary>
    public SessionRecord? Find(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;

        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, user_id, issued_at, last_seen_at, revoked_at
            FROM sessions WHERE id = @id LIMIT 1;
            """;
        select.Parameters.AddWithValue("@id", sessionId);

        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;

        return new SessionRecord(
            reader.GetString(0),
            reader.GetInt64(1),
            DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(4) ? null
                : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Withdraws one session. What sign-out calls, and what makes it a server-side act rather than
    /// a suggestion to the browser. Silently does nothing for an identifier that was never issued,
    /// because that is already the state being asked for.
    /// </summary>
    public void Revoke(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var update = connection.CreateCommand();
            // Marked rather than deleted, so the row stays until Prune sweeps it and a replay of
            // the old cookie meets a revoked session instead of an unknown one. Both are refused;
            // this way the refusal is on the record for as long as the ticket could still arrive.
            update.CommandText = "UPDATE sessions SET revoked_at = @at WHERE id = @id AND revoked_at IS NULL;";
            update.Parameters.AddWithValue("@at", Stamp(_clock()));
            update.Parameters.AddWithValue("@id", sessionId);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Withdraws every live session for an account. Not wired to a button yet; it is what a
    /// password change has to call to mean anything, and what the owner needs on the day an
    /// account is reported stolen.
    /// </summary>
    public int RevokeAllFor(long userId)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sessions SET revoked_at = @at WHERE user_id = @user_id AND revoked_at IS NULL;";
            update.Parameters.AddWithValue("@at", Stamp(_clock()));
            update.Parameters.AddWithValue("@user_id", userId);
            return update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Notes that a session was used. Kept so the owner can tell a session last seen this morning
    /// from one last seen in March; nothing decides anything on it.
    /// </summary>
    public void Touch(string? sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE sessions SET last_seen_at = @at WHERE id = @id;";
            update.Parameters.AddWithValue("@at", Stamp(_clock()));
            update.Parameters.AddWithValue("@id", sessionId);
            update.ExecuteNonQuery();
        }
    }

    /// <summary>How many rows are being kept. For the test that proves <see cref="Prune"/> works.</summary>
    public int CountRows()
    {
        using var connection = OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sessions;";
        return Convert.ToInt32(count.ExecuteScalar());
    }

    /// <summary>
    /// Deletes rows that can no longer authorise anything: revoked longer ago than a cookie can
    /// live, and issued longer ago than <see cref="AbsoluteLifetime"/>. Both are already refused by
    /// <see cref="IsLive"/>, so this changes no decision — it stops the table being a permanent
    /// record of every sign-in the deployment has ever seen.
    /// </summary>
    /// <remarks>
    /// Called under <c>_gate</c> from <see cref="Issue"/>, which is the only thing that adds a row,
    /// and at most once per <see cref="AbsoluteLifetime"/> because the sweep is a full-table scan.
    /// </remarks>
    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < _lifetime) return;
        _lastPrune = now;

        using var connection = OpenConnection();
        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM sessions
            WHERE issued_at < @cutoff
               OR (revoked_at IS NOT NULL AND revoked_at < @cutoff);
            """;
        delete.Parameters.AddWithValue("@cutoff", Stamp(now - _lifetime));
        delete.ExecuteNonQuery();
    }

    /// <summary>UTC always, for the same reason <see cref="SignInThrottle"/> insists on it: Prune compares these as text.</summary>
    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O");

    /// <summary>Base64 with the two URL-hostile characters swapped out and the padding dropped.</summary>
    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void Initialize()
    {
        var folder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var connection = OpenConnection();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id           TEXT PRIMARY KEY,
                user_id      INTEGER NOT NULL,
                issued_at    TEXT NOT NULL,
                last_seen_at TEXT NOT NULL,
                revoked_at   TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_user ON sessions (user_id);
            """;
        create.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
