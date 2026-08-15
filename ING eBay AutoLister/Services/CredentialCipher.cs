using System.Security.Cryptography;
using System.Text;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Encrypts what a hosted deployment keeps for each user — in practice their eBay refresh token,
/// which is an eighteen-month grant on somebody else's selling account — with a key that lives in
/// the host's configuration and nowhere in this repository.
/// </summary>
/// <remarks>
/// <para>
/// AES-256-GCM, from the BCL. Authenticated encryption rather than plain AES because the threat
/// here is not only reading the database: a row that can be edited undetected is a row where one
/// person's user id can be given another person's ciphertext. GCM's tag is what makes that fail
/// loudly instead of decrypting into a working token — see <see cref="TryUnprotect"/>.
/// </para>
/// <para>
/// The nonce is fresh random bytes for every single call and is stored beside the ciphertext. It
/// is not a secret; it only has to be unique for the key, and reusing one under AES-GCM is the
/// mistake that hands an attacker the keystream. Nothing here ever writes a fixed one.
/// </para>
/// <para>
/// Why this and not <c>IDataProtector</c>, which the app already has for the session cookie: the
/// data protection key ring is a file in the deployment's own storage, so a database backup plus
/// the container's disk is enough to read every token. The task asks for a key from configuration
/// precisely so that the thing which decrypts is somewhere the database is not, and can be rotated
/// by the owner without redeploying.
/// </para>
/// <para>
/// What is encrypted is the whole per-user record, not a hand-picked list of secret fields. A list
/// is a thing to forget to add to — the next credential someone adds to <see cref="Credentials"/>
/// is covered by having been added.
/// </para>
/// </remarks>
public sealed class CredentialCipher
{
    /// <summary>Where the key is read from. Set it in the host's environment settings; never commit it.</summary>
    public const string KeySetting = "Credentials:EncryptionKey";

    /// <summary>
    /// The flat environment variable name, accepted as well as <see cref="KeySetting"/>. Hosts that
    /// take a plain <c>NAME=value</c> list (Fly, Render, App Service) are far likelier to be handed
    /// this than the double-underscore form of the configuration key.
    /// </summary>
    public const string KeyEnvironmentVariable = "CREDENTIALS_ENCRYPTION_KEY";

    /// <summary>Names the construction inside every stored value, so a future change is detectable.</summary>
    private const string Scheme = "aesgcm256";

    private const int KeyBytes   = 32;
    private const int NonceBytes = 12;  // AesGcm's own maximum, and the size GCM is specified around.
    private const int TagBytes   = 16;

    /// <summary>
    /// The shortest passphrase this will accept when the configured value is not already 32 raw
    /// bytes. Not a password policy — a server secret nobody types — but a floor under "changeme".
    /// </summary>
    public const int MinimumKeyMaterialLength = 16;

    /// <summary>
    /// Fixed on purpose, unlike <see cref="PasswordHash"/>'s per-hash salt. The same configured
    /// value has to derive the same key on every restart or every stored token becomes unreadable,
    /// which rules out a random salt; and the input is a high-entropy server secret rather than a
    /// human password, so the salt is not doing the work it does over there.
    /// </summary>
    private static readonly byte[] DerivationSalt =
        Encoding.UTF8.GetBytes("ING Listing Engine credential encryption v1");

    private const int DerivationIterations = 210_000;

    private readonly byte[] _key;

    private CredentialCipher(byte[] key) => _key = key;

    /// <summary>
    /// The cipher a hosted deployment runs on, or an exception naming the setting that is missing.
    /// Called while the app is starting: a hosted build with nowhere to get the key must fail here,
    /// where the message is read, rather than at the first token write with the tokens in the clear.
    /// </summary>
    public static CredentialCipher FromConfiguration(IConfiguration configuration)
    {
        var configured = configuration[KeySetting] ?? configuration[KeyEnvironmentVariable];

        if (string.IsNullOrWhiteSpace(configured))
            throw new InvalidOperationException(
                $"The hosted build stores each user's eBay tokens encrypted and has no key to do it with. " +
                $"Set {KeySetting} (or the {KeyEnvironmentVariable} environment variable) in the host's " +
                $"configuration to a secret of at least {MinimumKeyMaterialLength} characters, or 32 bytes of " +
                $"base64. It must not be committed, and changing it makes every stored eBay connection " +
                $"unreadable — every user would have to sign in to eBay again.");

        return FromKeyMaterial(configured);
    }

    /// <summary>
    /// A cipher from whatever the owner configured: 32 bytes of base64 used as the key directly, or
    /// anything else stretched into one. Both are accepted because a deployment secret arrives as
    /// whichever of the two the person generating it reached for.
    /// </summary>
    public static CredentialCipher FromKeyMaterial(string material)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(material);
        var trimmed = material.Trim();

        if (TryDecodeRawKey(trimmed, out var raw)) return new CredentialCipher(raw);

        if (trimmed.Length < MinimumKeyMaterialLength)
            throw new ArgumentException(
                $"The credential encryption key must be at least {MinimumKeyMaterialLength} characters, " +
                $"or 32 bytes of base64.", nameof(material));

        return new CredentialCipher(
            Rfc2898DeriveBytes.Pbkdf2(trimmed, DerivationSalt, DerivationIterations, HashAlgorithmName.SHA256, KeyBytes));
    }

    /// <summary>Ciphertext for <paramref name="plaintext"/>. Never returns the same string twice.</summary>
    public string Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);

        var nonce      = RandomNumberGenerator.GetBytes(NonceBytes);
        var bytes      = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[bytes.Length];
        var tag        = new byte[TagBytes];

        using var aes = new AesGcm(_key, TagBytes);
        aes.Encrypt(nonce, bytes, ciphertext, tag);

        return string.Join('$', Scheme, Convert.ToBase64String(nonce),
                                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
    }

    /// <summary>
    /// What <see cref="Protect"/> was given, or false — for a value written with a different key, a
    /// value that has been tampered with, and a value that is not one of ours at all. False rather
    /// than an exception because the caller's answer to all three is the same and is not a crash:
    /// see <see cref="UserCredentialsStore"/>, which stops writing to a row it cannot read rather
    /// than overwriting the only copy of somebody's eBay connection.
    /// </summary>
    public bool TryUnprotect(string? stored, out string plaintext)
    {
        plaintext = "";
        if (string.IsNullOrWhiteSpace(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;

        byte[] nonce, ciphertext, tag;
        try
        {
            nonce      = Convert.FromBase64String(parts[1]);
            ciphertext = Convert.FromBase64String(parts[2]);
            tag        = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        if (nonce.Length != NonceBytes || tag.Length != TagBytes) return false;

        var bytes = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(_key, TagBytes);
            aes.Decrypt(nonce, ciphertext, tag, bytes);
        }
        catch (CryptographicException) { return false; }

        plaintext = Encoding.UTF8.GetString(bytes);
        return true;
    }

    /// <summary>True when the value is 32 bytes of base64 and can be the key as it stands.</summary>
    private static bool TryDecodeRawKey(string material, out byte[] key)
    {
        key = new byte[KeyBytes];
        return Convert.TryFromBase64String(material, key, out var written) && written == KeyBytes;
    }
}
