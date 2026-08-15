using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Where one address stands with the lockout: whether it is refused right now, and until when.
/// </summary>
/// <param name="Locked">True when an attempt must be refused without checking the password at all.</param>
/// <param name="Until">When the lock lifts, or null when there is no lock.</param>
/// <param name="Failures">Consecutive failures counted so far. For the log, not for the caller.</param>
public sealed record SignInLockout(bool Locked, DateTimeOffset? Until, int Failures)
{
    /// <summary>How long is left on the lock, or zero when it has lifted or was never set.</summary>
    public TimeSpan Remaining(DateTimeOffset now) =>
        Until is null || Until <= now ? TimeSpan.Zero : Until.Value - now;

    /// <summary>The state of an address nobody has failed on.</summary>
    public static readonly SignInLockout Clear = new(false, null, 0);
}

/// <summary>
/// Counts consecutive failed sign-ins per email address and refuses further attempts once there
/// have been too many, so a password cannot be found by trying every password.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the live site before this existed: eight wrong passwords in a row for one account
/// answered 401 eight times, at full speed, with nothing between the attacker and the ninth. PBKDF2
/// at 210,000 iterations (see <see cref="PasswordHash"/>) makes each guess cost the server about a
/// tenth of a second, which is a tax on a spray and not a wall in front of one. This is the wall:
/// after <see cref="MaxFailures"/> consecutive misses the address stops being checked at all for
/// <see cref="DefaultLockout"/>, and a correct password during that window is refused too. That
/// last part is the point — a lock that a correct guess opens is not a lock.
/// </para>
/// <para>
/// The counter is keyed on the address as typed at the sign-in form, whether or not it belongs to
/// an account. Locking only real addresses would answer "locked" for a registered address and
/// "wrong password" for an invented one, which is a way to ask which addresses are registered — the
/// question <see cref="UserStore.Verify"/> and <see cref="PasswordHash.VerifyNothing"/> go to
/// trouble to refuse. An address nobody has ever signed up gets counted, locked and forgotten
/// exactly like a real one.
/// </para>
/// <para>
/// It lives in the app's SQLite database rather than in memory, because in memory it is a counter
/// that a restart clears, and a hosted deployment restarts on every deploy and every reboot. It is
/// certainly not in a cookie: the party being counted is the one holding the cookie jar.
/// </para>
/// <para>
/// The cost of this is that anyone who knows a seller's address can keep that seller locked out by
/// guessing wrong five times every quarter of an hour. That is the standard trade and it is the
/// right way round: an owner who has to wait fifteen minutes has an inconvenience, an owner whose
/// eBay account is published by a script has a business problem. The per-IP limiter in
/// <see cref="HostedAuth"/> is what makes doing it to the whole userbase from one host expensive.
/// </para>
/// </remarks>
public sealed class SignInThrottle
{
    /// <summary>Consecutive misses before the address stops being checked. The sixth is refused.</summary>
    public const int MaxFailures = 5;

    /// <summary>How long the lock lasts, and how long a failure is remembered as "consecutive".</summary>
    public static readonly TimeSpan DefaultLockout = TimeSpan.FromMinutes(15);

    private readonly string _databasePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockout;
    private readonly object _gate = new();

    /// <summary>When the spent rows were last swept up. See <see cref="Prune"/>.</summary>
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    public SignInThrottle(ListingDatabase database) : this(database.DatabasePath) { }

    /// <param name="clock">Overridable so a test can watch a lock lift without waiting a quarter of an hour.</param>
    /// <param name="lockout">Overridable for the same reason. Production uses <see cref="DefaultLockout"/>.</param>
    public SignInThrottle(string databasePath, Func<DateTimeOffset>? clock = null, TimeSpan? lockout = null)
    {
        _databasePath = databasePath;
        _clock        = clock ?? (() => DateTimeOffset.UtcNow);
        _lockout      = lockout ?? DefaultLockout;
        Initialize();
    }

    /// <summary>How long a lock lasts here. What the caller tells the person to wait.</summary>
    public TimeSpan Lockout => _lockout;

    /// <summary>Now, by this throttle's clock. The caller works out "how much longer" against it.</summary>
    public DateTimeOffset Now => _clock();

    /// <summary>
    /// Whether this address is refused right now. Asked before the password is looked at, so that a
    /// locked address costs a database read rather than a PBKDF2 verification.
    /// </summary>
    public SignInLockout Check(string? email)
    {
        var key = Key(email);
        var now = _clock();

        lock (_gate)
        {
            var row = Read(key);
            if (row is null) return SignInLockout.Clear;

            return row.LockedUntil > now
                ? new SignInLockout(true, row.LockedUntil, row.Failures)
                : new SignInLockout(false, null, row.Failures);
        }
    }

    /// <summary>
    /// Counts one miss and returns where that leaves the address — including, on the miss that
    /// reaches <see cref="MaxFailures"/>, a lock the caller can report on the next attempt.
    /// </summary>
    public SignInLockout RecordFailure(string? email)
    {
        var key = Key(email);
        var now = _clock();

        lock (_gate)
        {
            var row = Read(key);

            // An attempt that arrives while the lock is still on does not extend it. Otherwise a
            // script hammering the endpoint would hold a real seller out indefinitely, and the
            // fifteen minutes this promises would be a lie whenever it mattered.
            if (row is not null && row.LockedUntil > now)
                return new SignInLockout(true, row.LockedUntil, row.Failures);

            // "Consecutive" is bounded by the same window as the lock. Without this, five typos
            // spread over a year add up to a locked account, which is a support ticket and not a
            // defence. A lock that has since expired starts the count again from nothing.
            var failures = row is null || row.LockedUntil is not null || now - row.LastFailureAt > _lockout
                ? 0
                : row.Failures;

            failures++;
            var lockedUntil = failures >= MaxFailures ? now + _lockout : (DateTimeOffset?)null;

            Write(key, failures, now, lockedUntil);
            Prune(now);
            return new SignInLockout(lockedUntil is not null, lockedUntil, failures);
        }
    }

    /// <summary>
    /// Deletes rows the read path has already stopped believing. Called from
    /// <see cref="RecordFailure"/>, which is the only thing that ever adds one.
    /// </summary>
    /// <remarks>
    /// Every failed attempt writes a row keyed on whatever was typed in the email box, and a spray
    /// types a different address every time — so without this the table is a list of every address
    /// anybody has ever guessed at, kept forever, on the same disk as the database. The per-IP
    /// limiter caps how fast it can grow but nothing caps the total.
    /// <para>
    /// Deleting these changes no decision. A row whose last failure is older than the window is
    /// already treated as a fresh start by <see cref="RecordFailure"/>, and one whose lock has
    /// expired is already <see cref="SignInLockout.Clear"/> to <see cref="Check"/>; the row is
    /// simply the memory of something nobody is allowed to remember any more. Rows still inside
    /// the window — and every live lock — are left exactly where they are.
    /// </para>
    /// <para>
    /// Once per window, not once per attempt. The sweep is a full-table scan and the attempts that
    /// trigger it arrive fastest precisely when the server is already busy refusing an attack.
    /// </para>
    /// </remarks>
    private void Prune(DateTimeOffset now)
    {
        if (now - _lastPrune < _lockout) return;
        _lastPrune = now;

        using var connection = OpenConnection();
        using var delete = connection.CreateCommand();
        delete.CommandText = """
            DELETE FROM sign_in_failures
            WHERE last_failure_at < @cutoff
              AND (locked_until IS NULL OR locked_until < @now);
            """;
        delete.Parameters.AddWithValue("@cutoff", Stamp(now - _lockout));
        delete.Parameters.AddWithValue("@now", Stamp(now));
        delete.ExecuteNonQuery();
    }

    /// <summary>The one form a time is written in. See <see cref="Write"/> for why it must be UTC.</summary>
    private static string Stamp(DateTimeOffset at) => at.ToUniversalTime().ToString("O");

    /// <summary>
    /// How many addresses are currently being counted. Says nothing about who they are, and is
    /// here for the test that proves <see cref="Prune"/> keeps the table from growing forever.
    /// </summary>
    public int CountedAddresses()
    {
        using var connection = OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM sign_in_failures;";
        return Convert.ToInt32(count.ExecuteScalar());
    }

    /// <summary>
    /// Forgets the address. Called on a sign-in that worked, which is what makes the count
    /// consecutive rather than lifetime.
    /// </summary>
    public void RecordSuccess(string? email)
    {
        var key = Key(email);

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM sign_in_failures WHERE email_normalized = @email;";
            delete.Parameters.AddWithValue("@email", key);
            delete.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// What to tell someone who is locked out. It says plainly that they are locked and when it
    /// lifts, because the person on the other end is nearly always someone who mistyped their own
    /// password — and the one case where it is not, the attacker already knows they are locked out.
    /// Hiding it would only punish the seller.
    /// </summary>
    public static string LockedMessage(TimeSpan remaining)
    {
        var minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        return minutes <= 1
            ? "Too many failed sign-in attempts. This account is locked for about another minute — try again then."
            : $"Too many failed sign-in attempts. This account is locked for about another {minutes} minutes — try again then.";
    }

    /// <summary>
    /// The form the counter is kept under. <see cref="UserStore.Normalize"/> where the address is
    /// one, so that Seller@X.com and seller@x.com are the same five attempts rather than ten; the
    /// trimmed lowercase text where it is not, because something unparseable is still something
    /// somebody is typing over and over.
    /// </summary>
    private static string Key(string? email) =>
        UserStore.Normalize(email) ?? email?.Trim().ToLowerInvariant() ?? string.Empty;

    private sealed record Row(int Failures, DateTimeOffset LastFailureAt, DateTimeOffset? LockedUntil);

    private Row? Read(string key)
    {
        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT failures, last_failure_at, locked_until
            FROM sign_in_failures WHERE email_normalized = @email LIMIT 1;
            """;
        select.Parameters.AddWithValue("@email", key);

        using var reader = select.ExecuteReader();
        if (!reader.Read()) return null;

        return new Row(
            reader.GetInt32(0),
            DateTimeOffset.Parse(reader.GetString(1), System.Globalization.CultureInfo.InvariantCulture),
            reader.IsDBNull(2) ? null
                : DateTimeOffset.Parse(reader.GetString(2), System.Globalization.CultureInfo.InvariantCulture));
    }

    private void Write(string key, int failures, DateTimeOffset at, DateTimeOffset? lockedUntil)
    {
        using var connection = OpenConnection();
        using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO sign_in_failures (email_normalized, failures, last_failure_at, locked_until)
            VALUES (@email, @failures, @at, @until)
            ON CONFLICT(email_normalized) DO UPDATE SET
                failures        = excluded.failures,
                last_failure_at = excluded.last_failure_at,
                locked_until    = excluded.locked_until;
            """;
        upsert.Parameters.AddWithValue("@email", key);
        upsert.Parameters.AddWithValue("@failures", failures);
        // UTC, always. Prune compares these as text, and "O" sorts lexicographically only when
        // every value carries the same offset — a row written at +02:00 would sort as though it
        // happened two hours later than it did, and be swept early or kept late.
        upsert.Parameters.AddWithValue("@at", Stamp(at));
        upsert.Parameters.AddWithValue("@until", (object?)(lockedUntil is null ? null : Stamp(lockedUntil.Value)) ?? DBNull.Value);
        upsert.ExecuteNonQuery();
    }

    private void Initialize()
    {
        var folder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var connection = OpenConnection();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS sign_in_failures (
                email_normalized TEXT PRIMARY KEY,
                failures         INTEGER NOT NULL,
                last_failure_at  TEXT NOT NULL,
                locked_until     TEXT NULL
            );
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
