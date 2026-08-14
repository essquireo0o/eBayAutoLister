namespace ING_eBay_AutoLister.Services;

/// <summary>What a password was refused for, or <see cref="PasswordVerdict.Acceptable"/>.</summary>
public enum PasswordVerdict
{
    Acceptable,
    TooShort,
    SameAsEmail,
    TooCommon,
}

/// <summary>
/// The only two questions worth asking about a password: is it long enough, and is it one of the
/// ones an attacker tries first.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately no character-class rule — no "must contain a digit, an uppercase and a symbol".
/// Those rules do not make people pick harder passwords; they make people pick
/// <c>Passw0rd!</c>, and every cracking dictionary has known that for a decade. What actually
/// costs an attacker is length, which is why the floor here is <see cref="MinimumLength"/> and not
/// eight-with-decorations: a twelve-character passphrase a person can remember beats an
/// eight-character line-noise password they write on a sticky note, both in entropy and in whether
/// it survives contact with a human being.
/// </para>
/// <para>
/// Length alone still lets through the passwords everyone reaches for when told to make one longer
/// — <c>passwordpassword</c>, <c>123456789012</c>, the email address itself. Those are not guessed,
/// they are looked up, and no iteration count in <see cref="PasswordHash"/> slows down a guess that
/// is right on the first try. Hence <see cref="Blocklist"/>: small, exact, and aimed at the head of
/// the distribution rather than pretending to be a breach corpus.
/// </para>
/// <para>
/// Enforced here, on the server, and reached from <see cref="UserStore.Create"/>. The sign-up page
/// says the same thing in a <c>minlength</c> attribute, but that is a courtesy to save a round
/// trip — anyone can post to the endpoint without the page, and a rule that lives only in the
/// browser is a suggestion.
/// </para>
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>
    /// The floor, in characters. Twelve rather than eight because eight is now hours of offline
    /// grinding against a stolen table, whatever it is hashed with. Raise it, never lower it.
    /// </summary>
    public const int MinimumLength = 12;

    /// <summary>
    /// The passwords that get tried first. Compared case-insensitively and in full — this is a
    /// blocklist of exact choices, not a substring filter, so a passphrase that happens to contain
    /// the word "password" is still a perfectly good passphrase.
    /// </summary>
    /// <remarks>
    /// Two halves. The short classics are already refused by <see cref="MinimumLength"/> and are
    /// here so that the list stays right if that ever changes. The rest are the twelve-plus
    /// character ones, which are the entries actually doing work: they are what a person types
    /// when a form tells them their password is too short and they pad it out.
    /// </remarks>
    public static readonly IReadOnlySet<string> Blocklist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Short classics — below the minimum already, kept so the list survives a change to it.
        "password", "123456", "1234567", "12345678", "123456789", "qwerty", "letmein", "welcome",
        "admin", "iloveyou", "monkey", "dragon", "abc123", "football", "baseball", "111111",
        "passw0rd", "trustno1", "sunshine", "princess", "master", "shadow", "superman", "starwars",
        "whatever", "changeme", "secret", "freedom", "computer", "qwertyuiop",

        // Twelve or more, which is where the padding lands.
        "123456789012", "1234567890123", "12345678901234", "123456789101", "1234567890ab",
        "111111111111", "000000000000", "aaaaaaaaaaaa", "abcdefghijkl", "abc123abc123",
        "password1234", "password12345", "password123456", "passwordpassword", "passw0rd1234",
        "p@ssw0rd1234", "passwort1234", "mypassword123", "thisismypassword", "newpassword123",
        "qwertyuiop12", "qwertyuiop123", "qwerty12345678", "qwertyuiopasdfgh", "asdfghjkl123",
        "1qaz2wsx3edc", "zaq12wsxcde3", "qazwsxedcrfv", "1q2w3e4r5t6y", "q1w2e3r4t5y6",
        "iloveyou1234", "letmein12345", "welcome123456", "trustno1trustno1", "monkey123456",
        "dragon123456", "princess1234", "sunshine1234", "superman1234", "batman123456",
        "master123456", "shadow123456", "football1234", "baseball1234", "changeme1234",
        "secret123456", "computer1234", "internet1234", "whatever1234", "adminadmin12",
        "administrator", "administrator1", "letmein123456", "loveyou123456",

        // This deployment's own obvious ones. A seller who names the product they just signed up
        // for is picking the first thing anyone would try against this particular site.
        "inglisting123", "inglistingengine", "ingmining123", "ebaypassword", "ebay12345678",
        "listingengine123",
    };

    /// <summary>
    /// Whether <paramref name="password"/> may be used, for someone signing up as
    /// <paramref name="email"/>. Null and empty are <see cref="PasswordVerdict.TooShort"/>, which is
    /// the true answer and the one the sign-up page can show.
    /// </summary>
    public static PasswordVerdict Check(string? password, string? email)
    {
        // Not trimmed. Leading and trailing spaces are characters a person deliberately typed, and
        // trimming them here would silently store a different password than the one they will type
        // back at the sign-in page.
        if (password is null || password.Length < MinimumLength) return PasswordVerdict.TooShort;

        // The address is on the sign-in form directly above the password box, so a password equal
        // to it is one field of information, not two — and it is public, printed on every invoice.
        // The local part counts as well: seller@example.com and "seller" are the same guess.
        var address = email?.Trim();
        if (!string.IsNullOrEmpty(address))
        {
            if (string.Equals(password, address, StringComparison.OrdinalIgnoreCase))
                return PasswordVerdict.SameAsEmail;

            var at = address.IndexOf('@');
            if (at > 0 && string.Equals(password, address[..at], StringComparison.OrdinalIgnoreCase))
                return PasswordVerdict.SameAsEmail;
        }

        return Blocklist.Contains(password) ? PasswordVerdict.TooCommon : PasswordVerdict.Acceptable;
    }

    /// <summary>The sentence the sign-up page shows for a verdict. Never quotes the password back.</summary>
    public static string Explain(PasswordVerdict verdict) => verdict switch
    {
        PasswordVerdict.TooShort =>
            $"Choose a password of at least {MinimumLength} characters. A few words you will remember "
            + "beats a short one with a symbol in it.",
        PasswordVerdict.SameAsEmail =>
            "Your password cannot be your email address — that is the other half of the same form.",
        PasswordVerdict.TooCommon =>
            "That password is one of the most commonly used ones, so it is guessed first. Choose another.",
        _ => "Account created.",
    };
}
