using System.Security.Cryptography;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What stands between a copy of the hosted database and every user's eBay account.
/// </summary>
public class CredentialCipherTests
{
    private const string KeyMaterial = "a-hosted-deployments-secret-value";

    private static CredentialCipher Cipher() => CredentialCipher.FromKeyMaterial(KeyMaterial);

    [Fact]
    public void What_goes_in_comes_back_out()
    {
        var cipher = Cipher();

        Assert.True(cipher.TryUnprotect(cipher.Protect("v1.a-refresh-token"), out var plaintext));
        Assert.Equal("v1.a-refresh-token", plaintext);
    }

    [Fact]
    public void The_secret_is_not_in_the_ciphertext()
    {
        Assert.DoesNotContain("a-refresh-token", Cipher().Protect("v1.a-refresh-token"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_same_value_never_encrypts_to_the_same_string_twice()
    {
        var cipher = Cipher();

        // A fresh nonce every time. Reusing one under AES-GCM is the mistake that hands an attacker
        // the keystream, and identical rows would also say "these two users pasted the same token".
        Assert.NotEqual(cipher.Protect("the-same-token"), cipher.Protect("the-same-token"));
    }

    [Fact]
    public void Another_deployments_key_reads_nothing()
    {
        var written = Cipher().Protect("v1.a-refresh-token");

        Assert.False(CredentialCipher.FromKeyMaterial("a-different-deployments-secret").TryUnprotect(written, out _));
    }

    [Fact]
    public void An_edited_row_is_refused_rather_than_decrypted()
    {
        var cipher = Cipher();
        var written = cipher.Protect("v1.a-refresh-token");

        // GCM's tag is what makes tampering fail loudly. Without it, a row whose ciphertext could be
        // swapped is a row where one user's id can be given another user's token.
        var parts = written.Split('$');
        var bytes = Convert.FromBase64String(parts[2]);
        bytes[0] ^= 0xFF;
        parts[2] = Convert.ToBase64String(bytes);

        Assert.False(cipher.TryUnprotect(string.Join('$', parts), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-encrypted-at-all")]
    [InlineData("aesgcm256$not$base$64")]
    [InlineData("aesgcm256$AAAA$AAAA")]
    public void A_value_that_is_not_ours_is_false_and_not_an_exception(string? stored)
    {
        // A corrupted row must cost one person a sign-in to eBay, not take the server down.
        Assert.False(Cipher().TryUnprotect(stored, out var plaintext));
        Assert.Equal("", plaintext);
    }

    [Fact]
    public void Thirty_two_bytes_of_base64_are_used_as_the_key_as_they_stand()
    {
        var raw = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        var written = CredentialCipher.FromKeyMaterial(raw).Protect("v1.a-refresh-token");

        // The same configured value has to derive the same key on every restart, or every stored
        // eBay connection is lost on a redeploy.
        Assert.True(CredentialCipher.FromKeyMaterial(raw).TryUnprotect(written, out var plaintext));
        Assert.Equal("v1.a-refresh-token", plaintext);
    }

    [Fact]
    public void A_passphrase_survives_a_restart_too()
    {
        var written = CredentialCipher.FromKeyMaterial(KeyMaterial).Protect("v1.a-refresh-token");

        Assert.True(CredentialCipher.FromKeyMaterial(KeyMaterial).TryUnprotect(written, out var plaintext));
        Assert.Equal("v1.a-refresh-token", plaintext);
    }

    [Fact]
    public void Something_too_short_to_be_a_secret_is_refused()
    {
        Assert.Throws<ArgumentException>(() => CredentialCipher.FromKeyMaterial("changeme"));
    }

    [Fact]
    public void The_key_can_arrive_as_a_plain_environment_variable()
    {
        // Which is the form every host's settings screen hands it over in — far likelier than the
        // double-underscore spelling of the configuration key.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CredentialCipher.KeyEnvironmentVariable] = KeyMaterial,
        }).Build();

        var written = CredentialCipher.FromConfiguration(configuration).Protect("v1.a-refresh-token");

        Assert.True(Cipher().TryUnprotect(written, out var plaintext));
        Assert.Equal("v1.a-refresh-token", plaintext);
    }

    [Fact]
    public void No_key_at_all_is_an_error_that_says_what_to_set()
    {
        var configuration = new ConfigurationBuilder().Build();

        var refused = Assert.Throws<InvalidOperationException>(() => CredentialCipher.FromConfiguration(configuration));

        Assert.Contains(CredentialCipher.KeySetting, refused.Message, StringComparison.Ordinal);
    }
}
