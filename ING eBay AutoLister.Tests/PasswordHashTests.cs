using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The properties that make a stored password worth storing. Each of these fails loudly if
/// somebody ever swaps PBKDF2 for something simpler because it was in the way.
/// </summary>
public class PasswordHashTests
{
    [Fact]
    public void A_hash_verifies_the_password_it_was_made_from()
    {
        var stored = PasswordHash.Create("a-long-enough-password");

        Assert.True(PasswordHash.Verify("a-long-enough-password", stored));
        Assert.False(PasswordHash.Verify("a-long-enough-passworD", stored));
        Assert.False(PasswordHash.Verify("", stored));
    }

    [Fact]
    public void The_password_is_not_in_the_hash()
    {
        var stored = PasswordHash.Create("hunter2-and-then-some");

        Assert.DoesNotContain("hunter2", stored, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Two_people_with_the_same_password_do_not_have_the_same_row()
    {
        // Which is what a random salt per hash buys: without it, one precomputed table cracks
        // every account that chose a common password, and equal rows say who shares one.
        var first  = PasswordHash.Create("the-same-password");
        var second = PasswordHash.Create("the-same-password");

        Assert.NotEqual(first, second);
        Assert.True(PasswordHash.Verify("the-same-password", first));
        Assert.True(PasswordHash.Verify("the-same-password", second));
    }

    [Fact]
    public void The_stored_value_carries_its_own_work_factor()
    {
        // Read at verification time rather than taken from the current constant. That is what lets
        // the iteration count be raised later without locking out everyone who signed up before.
        var parts = PasswordHash.Create("a-long-enough-password").Split('$');

        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.True(int.Parse(parts[1]) >= 210_000, "PBKDF2 iterations below the OWASP floor");
    }

    [Fact]
    public void The_stored_value_really_is_PBKDF2_HMAC_SHA256_and_not_just_labelled_as_it()
    {
        // The label in the row is a string, and a string can lie. This recomputes the hash from
        // the outside — salt and iteration count read off the row, BCL PBKDF2 applied to them —
        // and requires the answer to match byte for byte. A bare SHA-256, an unsalted digest or a
        // different KDF wearing the same name all fail here.
        const string password = "a-long-enough-password";
        var parts = PasswordHash.Create(password).Split('$');

        var iterations = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var salt       = Convert.FromBase64String(parts[2]);
        var stored     = Convert.FromBase64String(parts[3]);

        var recomputed = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            password, salt, iterations, System.Security.Cryptography.HashAlgorithmName.SHA256, stored.Length);

        Assert.Equal(recomputed, stored);

        // And the parameters are the ones that make it worth anything: a salt long enough not to
        // collide, a key long enough not to be brute-forced on its own, and a work factor above
        // the 100,000 floor this codebase claims. OWASP's 2023 figure for SHA-256 is 210,000.
        Assert.True(salt.Length >= 16,   $"salt is {salt.Length} bytes");
        Assert.True(stored.Length >= 32, $"derived key is {stored.Length} bytes");
        Assert.True(iterations >= 100_000, $"PBKDF2 iterations are {iterations}, below the 100,000 floor");

        // Not a bare digest of the password. A single SHA-256 would be 32 bytes and identical
        // every time; this asserts the stored key is not that.
        var bare = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(password));
        Assert.NotEqual(bare, stored);
    }

    [Fact]
    public void A_hash_written_at_a_lower_work_factor_still_verifies()
    {
        var stored = PasswordHash.Create("a-long-enough-password");
        var parts  = stored.Split('$');
        var salt   = Convert.FromBase64String(parts[2]);
        var weaker = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(
            "a-long-enough-password", salt, 1000,
            System.Security.Cryptography.HashAlgorithmName.SHA256, 32);

        var old = string.Join('$', "pbkdf2-sha256", "1000", parts[2], Convert.ToBase64String(weaker));

        Assert.True(PasswordHash.Verify("a-long-enough-password", old));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-hash")]
    [InlineData("pbkdf2-sha256$210000$notbase64$notbase64")]
    [InlineData("pbkdf2-sha256$notanumber$c2FsdA==$aGFzaA==")]
    [InlineData("md5$210000$c2FsdA==$aGFzaA==")]
    [InlineData("pbkdf2-sha256$210000$$")]
    [InlineData("pbkdf2-sha256$-1$c2FsdA==$aGFzaA==")]
    public void A_row_that_is_not_a_hash_verifies_nothing_and_throws_nothing(string? stored)
    {
        // One corrupted row must fail one person's sign-in, not take the server down with it.
        Assert.False(PasswordHash.Verify("a-long-enough-password", stored));
    }

    [Fact]
    public void Verifying_nothing_is_false()
    {
        Assert.False(PasswordHash.VerifyNothing("a-long-enough-password"));
        Assert.False(PasswordHash.VerifyNothing(null));
    }
}
