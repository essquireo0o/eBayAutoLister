using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>One signed-up person. Never carries the password hash out of <see cref="UserStore"/>.</summary>
/// <param name="Name">
/// What they are called, as they typed it. Empty only for an account that predates the column —
/// see <see cref="UserStore.Initialize"/>; every account created since is required to have one.
/// </param>
public sealed record UserAccount(long Id, string Email, string Name,
                                 DateTimeOffset CreatedAt, DateTimeOffset? LastSignInAt)
{
    /// <summary>The name when there is one, the address when there is not. What the UI should show.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Email : Name;
}

/// <summary>Why a sign-up did or did not produce an account.</summary>
public enum SignUpOutcome
{
    Created,
    NameMissing,
    EmailInvalid,
    EmailAlreadyRegistered,
    PasswordTooShort,
    PasswordSameAsEmail,
    PasswordTooCommon,
}

/// <summary>The account, when one was made, and a sentence the sign-up page can show when one wasn't.</summary>
public sealed record SignUpResult(SignUpOutcome Outcome, UserAccount? User, string Message)
{
    public bool Succeeded => Outcome == SignUpOutcome.Created;
}

/// <summary>
/// The people who may use a hosted deployment, in the app's own SQLite database alongside
/// <see cref="EarningsStore"/> and <see cref="OnboardingStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// The desktop build never constructs this. It is one seller on their own machine behind a
/// loopback port, and asking them to invent a password to reach their own computer would be
/// security theatre. A hosted deployment is the opposite: the eBay refresh token and the owner's
/// Anthropic key are on the server, so "who is asking" has to have an answer before any endpoint
/// runs. See <see cref="HostedAuth"/>.
/// </para>
/// <para>
/// Addresses are matched on a normalised copy — trimmed and lowercased — held in its own column
/// with the unique index on it, while <c>email</c> keeps what the person actually typed. Matching
/// on the typed form would let Seller@x.com and seller@x.com sign up twice and then wonder which
/// account their listings went to.
/// </para>
/// <para>
/// Passwords reach this class as plaintext and leave it as PBKDF2 (see <see cref="PasswordHash"/>).
/// Nothing here logs one, returns one, or puts one in an exception message.
/// </para>
/// </remarks>
public sealed class UserStore
{
    /// <summary>
    /// The longest name that will be stored. Not a rejection: a name longer than this is kept up to
    /// here rather than refused, because a person with a long name has done nothing wrong and a
    /// form that will not accept their name is the kind of thing they remember about a product.
    /// The cap exists so a script cannot post a megabyte into every row.
    /// </summary>
    public const int MaximumNameLength = 100;

    private readonly string _databasePath;
    private readonly object _gate = new();

    public UserStore(ListingDatabase database) : this(database.DatabasePath) { }

    public UserStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>
    /// Creates an account. Returns why it refused rather than throwing: every one of these
    /// outcomes is something the person typed, and the sign-up page has to show it back to them.
    /// </summary>
    /// <param name="name">
    /// What to call them. Required — trimmed, capped at <see cref="MaximumNameLength"/>, and
    /// refused outright when it is empty or nothing but whitespace. Checked first because it is the
    /// first field on the form, and a page that reports the last problem first makes people fix
    /// their password only to be told about their name.
    /// </param>
    public SignUpResult Create(string? email, string? password, string? name)
    {
        var cleanName = CleanName(name);
        if (cleanName is null)
            return new SignUpResult(SignUpOutcome.NameMissing, null, "Enter your name.");

        var normalized = Normalize(email);
        if (normalized is null)
            return new SignUpResult(SignUpOutcome.EmailInvalid, null, "Enter an email address, like you@example.com.");

        // Length and a blocklist, and no character-class rules — see PasswordPolicy for why.
        var verdict = PasswordPolicy.Check(password, email);
        if (verdict != PasswordVerdict.Acceptable)
            return new SignUpResult(Outcome(verdict), null, PasswordPolicy.Explain(verdict));

        var hash      = PasswordHash.Create(password!);
        var createdAt = DateTimeOffset.UtcNow;

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO users (email, email_normalized, name, password_hash, created_at)
                VALUES (@email, @email_normalized, @name, @password_hash, @created_at)
                ON CONFLICT(email_normalized) DO NOTHING;

                SELECT changes();
                """;
            insert.Parameters.AddWithValue("@email", email!.Trim());
            insert.Parameters.AddWithValue("@email_normalized", normalized);
            insert.Parameters.AddWithValue("@name", cleanName);
            insert.Parameters.AddWithValue("@password_hash", hash);
            insert.Parameters.AddWithValue("@created_at", createdAt.ToString("O"));

            // The unique index decides, not a SELECT before the INSERT — two sign-ups for one
            // address arriving together would both pass that check and one would win by accident.
            //
            // The message is what the caller may show if it decides to; the hosted sign-up endpoint
            // deliberately does not, because showing it is a way to ask which addresses are
            // registered. See MapAccountEndpoints. The outcome stays distinct so the caller can
            // tell — the desktop build and the tests both need to — but this is the one refusal
            // whose text must never reach a stranger.
            if (Convert.ToInt32(insert.ExecuteScalar()) == 0)
                return new SignUpResult(SignUpOutcome.EmailAlreadyRegistered, null,
                    "That email address already has an account. Sign in instead.");
        }

        return new SignUpResult(SignUpOutcome.Created, Find(normalized), "Account created.");
    }

    /// <summary>
    /// The account when the password is right, null when it is not — and null in exactly the same
    /// shape, and after the same work, whether the address is unregistered or the password is
    /// wrong. The caller must not tell those two apart to the person signing in either.
    /// </summary>
    public UserAccount? Verify(string? email, string? password)
    {
        var normalized = Normalize(email);
        if (normalized is null) { PasswordHash.VerifyNothing(password); return null; }

        string? hash;
        using (var connection = OpenConnection())
        using (var select = connection.CreateCommand())
        {
            select.CommandText = "SELECT password_hash FROM users WHERE email_normalized = @email LIMIT 1;";
            select.Parameters.AddWithValue("@email", normalized);
            hash = select.ExecuteScalar() as string;
        }

        if (hash is null) { PasswordHash.VerifyNothing(password); return null; }
        if (!PasswordHash.Verify(password, hash)) return null;

        RecordSignIn(normalized);
        return Find(normalized);
    }

    /// <summary>The account behind a cookie's user id, or null if the row has since been deleted.</summary>
    public UserAccount? Find(long id)
    {
        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, email, name, created_at, last_sign_in_at FROM users WHERE id = @id LIMIT 1;
            """;
        select.Parameters.AddWithValue("@id", id);

        using var reader = select.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    /// <summary>The account for a typed address, matched case- and whitespace-insensitively.</summary>
    public UserAccount? FindByEmail(string? email)
    {
        var normalized = Normalize(email);
        return normalized is null ? null : Find(normalized);
    }

    /// <summary>How many people have signed up. Used by the sign-up page to say whether it is the first.</summary>
    public int Count()
    {
        using var connection = OpenConnection();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM users;";
        return Convert.ToInt32(count.ExecuteScalar());
    }

    private UserAccount? Find(string normalizedEmail)
    {
        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, email, name, created_at, last_sign_in_at
            FROM users WHERE email_normalized = @email LIMIT 1;
            """;
        select.Parameters.AddWithValue("@email", normalizedEmail);

        using var reader = select.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    private void RecordSignIn(string normalizedEmail)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var update = connection.CreateCommand();
            update.CommandText = "UPDATE users SET last_sign_in_at = @at WHERE email_normalized = @email;";
            update.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("@email", normalizedEmail);
            update.ExecuteNonQuery();
        }
    }

    private static UserAccount Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
        DateTimeOffset.Parse(reader.GetString(3), System.Globalization.CultureInfo.InvariantCulture),
        reader.IsDBNull(4) ? null
            : DateTimeOffset.Parse(reader.GetString(4), System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Which refusal a password verdict is. One enum for the page, one for the policy.</summary>
    private static SignUpOutcome Outcome(PasswordVerdict verdict) => verdict switch
    {
        PasswordVerdict.SameAsEmail => SignUpOutcome.PasswordSameAsEmail,
        PasswordVerdict.TooCommon   => SignUpOutcome.PasswordTooCommon,
        _                           => SignUpOutcome.PasswordTooShort,
    };

    /// <summary>
    /// The name as it will be stored, or null when there is not one. Whitespace-only is not a name:
    /// a space bar is what gets typed to move past a field somebody does not want to fill in, and
    /// storing that leaves a blank where the app says who is signed in.
    /// </summary>
    /// <remarks>
    /// Inner whitespace is left exactly as typed. Names have spaces, hyphens, apostrophes and
    /// letters from every alphabet in them, and the shortest way to insult somebody is to tell them
    /// their own name is invalid. The only rules are "not empty" and "not longer than the column".
    /// </remarks>
    internal static string? CleanName(string? name)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        return trimmed.Length <= MaximumNameLength ? trimmed : trimmed[..MaximumNameLength].TrimEnd();
    }

    /// <summary>
    /// The form an address is stored and compared in, or null when it is not an address at all.
    /// Deliberately not a regular expression: the only thing worth rejecting here is a value that
    /// cannot be an email, and the real check is whether the person can read what is sent to it.
    /// </summary>
    internal static string? Normalize(string? email)
    {
        var trimmed = email?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 254) return null;
        if (trimmed.Any(char.IsWhiteSpace)) return null;

        var at = trimmed.IndexOf('@');
        if (at <= 0 || at != trimmed.LastIndexOf('@') || at == trimmed.Length - 1) return null;
        if (!trimmed[(at + 1)..].Contains('.')) return null;

        return trimmed.ToLowerInvariant();
    }

    private void Initialize()
    {
        var folder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var connection = OpenConnection();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                id               INTEGER PRIMARY KEY AUTOINCREMENT,
                email            TEXT NOT NULL,
                email_normalized TEXT NOT NULL,
                name             TEXT NOT NULL DEFAULT '',
                password_hash    TEXT NOT NULL,
                created_at       TEXT NOT NULL,
                last_sign_in_at  TEXT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_users_email_normalized ON users (email_normalized);
            """;
        create.ExecuteNonQuery();

        // For the deployments that already have the table. The column is required at sign-up but
        // NOT NULL DEFAULT '' in the schema, because the accounts that exist on the live box were
        // created before it and there is no name to invent for them — they read back as empty and
        // UserAccount.DisplayName falls through to the address, which is what they showed before.
        UserOwnedTable.AddColumnIfMissing(connection, "users", "name", "TEXT NOT NULL DEFAULT ''");
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
