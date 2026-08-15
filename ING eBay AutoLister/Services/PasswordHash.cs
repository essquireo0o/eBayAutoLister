using System.Security.Cryptography;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a password into something safe to keep in the database, and checks one against what was
/// kept. PBKDF2-HMAC-SHA256, from the BCL — no invented construction anywhere in this file.
/// </summary>
/// <remarks>
/// <para>
/// Three rules, and every one of them is a rule because the obvious shortcut is a breach:
/// the password is never stored, not even encrypted (a key that can decrypt is a key that can be
/// stolen); every hash gets its own random salt, so two people who chose the same password do not
/// have the same row and a precomputed table is worth nothing; and the work factor is high enough
/// that a stolen table is expensive to grind rather than free. <see cref="Iterations"/> is OWASP's
/// 2023 floor for PBKDF2-HMAC-SHA256.
/// </para>
/// <para>
/// The iteration count is written into every hash rather than being read from this constant at
/// verification time. That is what lets the number be raised later without locking out everyone
/// who signed up before the change: their rows keep verifying at the count they were written with.
/// </para>
/// <para>
/// The comparison is <see cref="CryptographicOperations.FixedTimeEquals"/> and not <c>==</c>. A
/// byte-by-byte compare that returns early leaks, in how long it took, how much of the hash was
/// guessed right.
/// </para>
/// </remarks>
public static class PasswordHash
{
    /// <summary>Names the construction inside the stored string, so a future change is detectable.</summary>
    private const string Scheme = "pbkdf2-sha256";

    private const int SaltBytes = 16;
    private const int KeyBytes  = 32;

    /// <summary>OWASP's recommended minimum for PBKDF2-HMAC-SHA256. Raise it, never lower it.</summary>
    private const int Iterations = 210_000;

    /// <summary>
    /// A throwaway hash, used to spend the same time on a sign-in attempt for an address that has
    /// never signed up as one for an address that has. See <see cref="VerifyNothing"/>.
    /// </summary>
    private static readonly string DecoyHash = Create("password-that-belongs-to-nobody");

    /// <summary>Hashes a password for storage. Never returns the same string twice for one password.</summary>
    public static string Create(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var key  = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeyBytes);

        return string.Join('$', Scheme, Iterations.ToString(System.Globalization.CultureInfo.InvariantCulture),
                                Convert.ToBase64String(salt), Convert.ToBase64String(key));
    }

    /// <summary>
    /// True when <paramref name="password"/> is the one <paramref name="stored"/> was made from.
    /// A malformed, empty or unrecognised stored value is false, never an exception — a corrupted
    /// row must fail one person's sign-in, not take the server down.
    /// </summary>
    public static bool Verify(string? password, string? stored)
    {
        if (password is null || string.IsNullOrWhiteSpace(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != Scheme) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1 || iterations > 10_000_000) return false;

        byte[] salt, expected;
        try
        {
            salt     = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }

        if (salt.Length == 0 || expected.Length == 0) return false;

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Spends a verification's worth of work and returns false. Called when the email address is
    /// not registered: without it, "no such account" answers measurably faster than "wrong
    /// password", and that difference is a list of which addresses have signed up.
    /// </summary>
    public static bool VerifyNothing(string? password)
    {
        Verify(password ?? string.Empty, DecoyHash);
        return false;
    }
}
