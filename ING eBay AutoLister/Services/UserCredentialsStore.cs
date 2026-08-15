using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One user's stored credentials, and whether the row they came from could be read at all.
/// </summary>
/// <param name="Data">The record, or empty defaults when there is nothing stored yet.</param>
/// <param name="Unreadable">
/// True when a row exists but did not decrypt — a rotated encryption key, a restored backup from a
/// deployment with a different one, or a tampered row. The caller must not write over it: see
/// <see cref="UserCredentialsStore"/>.
/// </param>
public sealed record StoredCredentials(Credentials Data, bool Unreadable);

/// <summary>
/// Every user's own eBay account, in the app's SQLite database, one encrypted row each.
/// </summary>
/// <remarks>
/// <para>
/// This is the hosted counterpart of the single <c>credentials.json</c> the desktop app has always
/// had. On one seller's own machine that file is the product's configuration; on a server it would
/// be one eBay account, one refresh token and one set of business policies shared by everybody who
/// signs up — the first user to connect eBay would be publishing on the second user's behalf, and
/// the second would be reading the first's tokens out of the Settings screen.
/// </para>
/// <para>
/// The whole record is stored as one encrypted blob (see <see cref="CredentialCipher"/>) rather
/// than a column per field. Nothing queries these by value — they are always fetched by user id —
/// so columns would buy nothing and would leave whichever fields nobody remembered to encrypt
/// sitting in the clear.
/// </para>
/// <para>
/// A row that will not decrypt is reported rather than swallowed, and the caller stops writing for
/// that user. This is the same rule <see cref="CredentialsStore"/> applies to an unreadable
/// credentials.json, and for the same reason: writing empty defaults over the row would turn "we
/// could not read your eBay connection" into "you no longer have one", and the refresh token it
/// destroyed can only be replaced by the user going back to eBay in a browser.
/// </para>
/// </remarks>
public sealed class UserCredentialsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string _databasePath;
    private readonly CredentialCipher _cipher;
    private readonly object _gate = new();

    public UserCredentialsStore(ListingDatabase database, CredentialCipher cipher)
        : this(database.DatabasePath, cipher) { }

    public UserCredentialsStore(string databasePath, CredentialCipher cipher)
    {
        _databasePath = databasePath;
        _cipher       = cipher;
        Initialize();
    }

    /// <summary>
    /// What is stored for this user. A user who has configured nothing yet gets empty defaults,
    /// which is the same thing a first run of the desktop app gets.
    /// </summary>
    public StoredCredentials Load(long userId)
    {
        var stored = ReadStoredText(userId);
        if (stored is null) return new StoredCredentials(new Credentials(), Unreadable: false);

        if (!_cipher.TryUnprotect(stored, out var json))
            return new StoredCredentials(new Credentials(), Unreadable: true);

        try
        {
            var loaded = JsonSerializer.Deserialize<Credentials>(json, JsonOptions);
            if (loaded is not null) return new StoredCredentials(loaded, Unreadable: false);
        }
        catch (JsonException) { /* falls through — decrypted but not a record we can use */ }

        return new StoredCredentials(new Credentials(), Unreadable: true);
    }

    /// <summary>Replaces this user's record. Encrypted before it reaches the database.</summary>
    public void Save(long userId, Credentials credentials)
    {
        var payload = _cipher.Protect(JsonSerializer.Serialize(credentials, JsonOptions));

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var upsert = connection.CreateCommand();
            upsert.CommandText = """
                INSERT INTO user_credentials (user_id, credentials, updated_at)
                VALUES (@user_id, @credentials, @updated_at)
                ON CONFLICT(user_id) DO UPDATE SET
                    credentials = excluded.credentials,
                    updated_at  = excluded.updated_at;
                """;
            upsert.Parameters.AddWithValue("@user_id", userId);
            upsert.Parameters.AddWithValue("@credentials", payload);
            upsert.Parameters.AddWithValue("@updated_at", DateTimeOffset.UtcNow.ToString("O"));
            upsert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Exactly the text held for this user, ciphertext and all, or null when there is no row.
    /// Public so a test can assert that a refresh token is not sitting in the database in the
    /// clear — the one claim about encryption that cannot be made by calling <see cref="Load"/>.
    /// </summary>
    public string? ReadStoredText(long userId)
    {
        using var connection = OpenConnection();
        using var select = connection.CreateCommand();
        select.CommandText = "SELECT credentials FROM user_credentials WHERE user_id = @user_id LIMIT 1;";
        select.Parameters.AddWithValue("@user_id", userId);
        return select.ExecuteScalar() as string;
    }

    private void Initialize()
    {
        var folder = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

        using var connection = OpenConnection();
        using var create = connection.CreateCommand();
        create.CommandText = """
            CREATE TABLE IF NOT EXISTS user_credentials (
                user_id     INTEGER PRIMARY KEY,
                credentials TEXT NOT NULL,
                updated_at  TEXT NOT NULL
            );
            """;
        create.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
