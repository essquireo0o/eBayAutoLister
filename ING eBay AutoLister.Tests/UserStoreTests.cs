using System.Text;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The users table for the hosted build, and the one rule that has no acceptable failure: what is
/// written to disk must never be the password.
/// </summary>
public class UserStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ing-userstore-tests", Guid.NewGuid().ToString("N"));
    private readonly UserStore _users;
    private readonly string _databasePath;

    public UserStoreTests()
    {
        Directory.CreateDirectory(_folder);
        _databasePath = Path.Combine(_folder, "users.db");
        _users = new UserStore(_databasePath);
    }

    [Fact]
    public void The_password_is_nowhere_in_the_database_file()
    {
        const string password = "correct-horse-battery-staple";
        _users.Create("seller@example.com", password, "Dana Ellis");

        // Opened sharing the write handle Microsoft.Data.Sqlite's connection pool is still holding,
        // and decoded a byte to a character so the page bytes around the row survive the read.
        using var file = new FileStream(_databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        var text = Encoding.Latin1.GetString(bytes);

        // Not the password, and not a bare digest of it either — the stored value names its own
        // construction, so a later change away from PBKDF2 is visible in the row rather than
        // silently compatible with it.
        Assert.DoesNotContain(password, text, StringComparison.Ordinal);
        Assert.Contains("pbkdf2-sha256$", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_new_account_can_sign_in()
    {
        var created = _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        Assert.True(created.Succeeded);
        Assert.Equal(created.User!.Id, _users.Verify("seller@example.com", "a-long-enough-password")?.Id);
    }

    [Fact]
    public void The_wrong_password_is_nobody()
    {
        _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        Assert.Null(_users.Verify("seller@example.com", "a-long-enough-Password"));
        Assert.Null(_users.Verify("seller@example.com", ""));
        Assert.Null(_users.Verify("seller@example.com", null));
    }

    [Fact]
    public void An_address_nobody_registered_is_nobody()
    {
        Assert.Null(_users.Verify("stranger@example.com", "a-long-enough-password"));
    }

    [Theory]
    [InlineData("Seller@Example.com")]
    [InlineData("  seller@example.com  ")]
    [InlineData("SELLER@EXAMPLE.COM")]
    public void One_address_is_one_account_however_it_was_typed(string retyped)
    {
        _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        var again = _users.Create(retyped, "some-other-password", "Someone Else");

        Assert.Equal(SignUpOutcome.EmailAlreadyRegistered, again.Outcome);
        Assert.Equal(1, _users.Count());
        // And the first person's password still works — the second attempt overwrote nothing.
        Assert.NotNull(_users.Verify("seller@example.com", "a-long-enough-password"));
    }

    [Fact]
    public void Signing_in_finds_the_account_whatever_case_it_was_typed_in()
    {
        _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        Assert.NotNull(_users.Verify(" SELLER@example.COM ", "a-long-enough-password"));
    }

    [Fact]
    public void The_typed_address_is_kept_as_typed()
    {
        // Matching is case-insensitive; what is shown back to the person is not "seller@..." when
        // they signed up as "Seller@...".
        var created = _users.Create("Seller@Example.com", "a-long-enough-password", "Dana Ellis");

        Assert.Equal("Seller@Example.com", created.User!.Email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-an-address")]
    [InlineData("no@dot")]
    [InlineData("@example.com")]
    [InlineData("seller@")]
    [InlineData("two@at@example.com")]
    [InlineData("space in@example.com")]
    public void Something_that_is_not_an_address_makes_no_account(string? email)
    {
        var result = _users.Create(email, "a-long-enough-password", "Dana Ellis");

        Assert.Equal(SignUpOutcome.EmailInvalid, result.Outcome);
        Assert.Equal(0, _users.Count());
    }

    [Fact]
    public void A_password_below_the_minimum_makes_no_account()
    {
        // Eleven characters against a twelve-character floor. The acceptance case, spelled out
        // rather than derived, so that lowering the constant cannot make this test pass anyway.
        var result = _users.Create("seller@example.com", "elevenchars", "Dana Ellis");

        Assert.Equal(11, "elevenchars".Length);
        Assert.Equal(12, PasswordPolicy.MinimumLength);
        Assert.Equal(SignUpOutcome.PasswordTooShort, result.Outcome);
        Assert.Equal(0, _users.Count());
        Assert.Null(_users.Verify("seller@example.com", "elevenchars"));
    }

    [Fact]
    public void The_twelfth_character_is_what_makes_it_acceptable()
    {
        var eleven = _users.Create("seller@example.com", "unguessable", "Dana Ellis");
        var twelve = _users.Create("seller@example.com", "unguessableX", "Dana Ellis");

        Assert.Equal(SignUpOutcome.PasswordTooShort, eleven.Outcome);
        Assert.True(twelve.Succeeded);
    }

    // ── The name ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_name_is_kept_and_comes_back_with_the_account()
    {
        var created = _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        Assert.True(created.Succeeded);
        Assert.Equal("Dana Ellis", created.User!.Name);
        // And it survives being read back from the row rather than only being echoed by Create.
        Assert.Equal("Dana Ellis", _users.Find(created.User.Id)!.Name);
        Assert.Equal("Dana Ellis", _users.Verify("seller@example.com", "a-long-enough-password")!.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\u00A0")] // a non-breaking space, which is what a copy-paste from a web page leaves
    public void An_account_with_no_name_is_refused(string? name)
    {
        var result = _users.Create("seller@example.com", "a-long-enough-password", name);

        Assert.Equal(SignUpOutcome.NameMissing, result.Outcome);
        Assert.Equal(0, _users.Count());
    }

    [Fact]
    public void The_name_is_trimmed_but_not_otherwise_rewritten()
    {
        // Inner spacing, hyphens and apostrophes are somebody's actual name, not input to clean up.
        var created = _users.Create("seller@example.com", "a-long-enough-password", "  Ada  O'Neill-Vance  ");

        Assert.Equal("Ada  O'Neill-Vance", created.User!.Name);
    }

    [Fact]
    public void A_name_longer_than_the_column_is_capped_rather_than_refused()
    {
        var long_name = new string('a', UserStore.MaximumNameLength + 50);

        var created = _users.Create("seller@example.com", "a-long-enough-password", long_name);

        Assert.True(created.Succeeded);
        Assert.Equal(UserStore.MaximumNameLength, created.User!.Name.Length);
    }

    [Fact]
    public void An_account_from_before_the_name_column_reads_back_and_shows_its_address()
    {
        // The live deployment has these. Adding the column must not make them unreadable, and the
        // screen that says who is signed in must say something rather than nothing.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_databasePath}"))
        {
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = """
                DROP TABLE users;
                CREATE TABLE users (
                    id               INTEGER PRIMARY KEY AUTOINCREMENT,
                    email            TEXT NOT NULL,
                    email_normalized TEXT NOT NULL,
                    password_hash    TEXT NOT NULL,
                    created_at       TEXT NOT NULL,
                    last_sign_in_at  TEXT NULL
                );
                CREATE UNIQUE INDEX idx_users_email_normalized ON users (email_normalized);
                INSERT INTO users (email, email_normalized, password_hash, created_at)
                VALUES ('old@example.com', 'old@example.com', @hash, '2026-01-01T00:00:00.0000000+00:00');
                """;
            drop.Parameters.AddWithValue("@hash", PasswordHash.Create("a-long-enough-password"));
            drop.ExecuteNonQuery();
        }

        var migrated = new UserStore(_databasePath);
        var old = migrated.Verify("old@example.com", "a-long-enough-password");

        Assert.NotNull(old);
        Assert.Equal(string.Empty, old!.Name);
        Assert.Equal("old@example.com", old.DisplayName);
    }

    // ── The passwords that are refused for being guessable rather than short ─────────────────

    [Fact]
    public void The_password_cannot_be_the_email_address()
    {
        // Same field twice. It is long enough, and it is printed on every invoice they send.
        var result = _users.Create("seller@example.com", "seller@example.com", "Dana Ellis");

        Assert.Equal(SignUpOutcome.PasswordSameAsEmail, result.Outcome);
        Assert.Equal(0, _users.Count());
    }

    [Theory]
    [InlineData("passwordpassword")]
    [InlineData("PasswordPassword")] // the same guess with the shift key held
    [InlineData("123456789012")]
    [InlineData("qwertyuiop123")]
    [InlineData("administrator")]
    public void A_password_off_the_top_of_the_list_makes_no_account(string common)
    {
        var result = _users.Create("seller@example.com", common, "Dana Ellis");

        Assert.Equal(SignUpOutcome.PasswordTooCommon, result.Outcome);
        Assert.Equal(0, _users.Count());
    }

    [Fact]
    public void A_second_store_over_the_same_file_reads_the_same_people()
    {
        // The table has to survive a restart, and Initialize has to be safe to run over a database
        // that already has it — every store in this app is constructed on every start.
        _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        var reopened = new UserStore(_databasePath);

        Assert.Equal(1, reopened.Count());
        Assert.NotNull(reopened.Verify("seller@example.com", "a-long-enough-password"));
    }

    [Fact]
    public void The_cookies_user_id_finds_the_person_it_names()
    {
        var created = _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");

        Assert.Equal("seller@example.com", _users.Find(created.User!.Id)?.Email);
        Assert.Null(_users.Find(created.User!.Id + 1000));
    }

    [Fact]
    public void Signing_in_records_when()
    {
        var created = _users.Create("seller@example.com", "a-long-enough-password", "Dana Ellis");
        Assert.Null(created.User!.LastSignInAt);

        _users.Verify("seller@example.com", "a-long-enough-password");

        Assert.NotNull(_users.Find(created.User.Id)!.LastSignInAt);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
        GC.SuppressFinalize(this);
    }
}
