using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The rule that decides which passwords this app will hold. Length and a blocklist, and — just as
/// deliberately — no character-class rule, which is a thing a well-meaning change could add back.
/// </summary>
public class PasswordPolicyTests
{
    private const string Email = "seller@example.com";

    [Fact]
    public void Eleven_characters_is_short_and_twelve_is_not()
    {
        Assert.Equal(PasswordVerdict.TooShort,   PasswordPolicy.Check("elevenchars",  Email));
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("elevencharsX", Email));
    }

    [Fact]
    public void The_floor_is_twelve()
    {
        // Written out, so that lowering the constant fails here rather than quietly weakening
        // every test that derives its input from it.
        Assert.Equal(12, PasswordPolicy.MinimumLength);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_at_all_is_too_short(string? password)
    {
        Assert.Equal(PasswordVerdict.TooShort, PasswordPolicy.Check(password, Email));
    }

    [Fact]
    public void A_passphrase_of_ordinary_words_is_fine()
    {
        // The whole point of dropping character classes. None of these has a digit, a capital or a
        // symbol in it, and every one of them is stronger than Passw0rd!.
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("correct horse battery staple", Email));
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("the wrong trousers", Email));
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("mypasswordisaboat", Email));
    }

    [Fact]
    public void Nothing_demands_a_digit_or_a_capital_or_a_symbol()
    {
        // Twelve lowercase letters that are not on the list. If somebody adds "must contain a
        // number" this is the test that says no, and the comment above it says why.
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("verdantmarsh", Email));
    }

    [Fact]
    public void The_password_may_not_be_the_email_address()
    {
        Assert.Equal(PasswordVerdict.SameAsEmail, PasswordPolicy.Check("seller@example.com", Email));
        // However either of them was capitalised, and whatever whitespace the form left on the address.
        Assert.Equal(PasswordVerdict.SameAsEmail, PasswordPolicy.Check("Seller@Example.COM", "  seller@example.com "));
    }

    [Fact]
    public void The_password_may_not_be_the_part_of_the_address_before_the_at_sign()
    {
        Assert.Equal(PasswordVerdict.SameAsEmail,
                     PasswordPolicy.Check("longsellername", "longsellername@example.com"));
    }

    [Fact]
    public void An_address_that_merely_appears_inside_a_passphrase_is_not_the_address()
    {
        // Exact match, not a substring search. Otherwise the rule quietly refuses good passwords.
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("seller@example.com is not it", Email));
    }

    [Theory]
    [InlineData("passwordpassword")]
    [InlineData("PASSWORDPASSWORD")]
    [InlineData("PasswordPassword")]
    [InlineData("123456789012")]
    [InlineData("111111111111")]
    [InlineData("qwertyuiop123")]
    [InlineData("1qaz2wsx3edc")]
    [InlineData("administrator")]
    [InlineData("thisismypassword")]
    [InlineData("iloveyou1234")]
    public void The_ones_that_get_tried_first_are_refused(string common)
    {
        Assert.Equal(PasswordVerdict.TooCommon, PasswordPolicy.Check(common, Email));
    }

    [Fact]
    public void The_blocklist_is_an_exact_match_and_not_a_substring_ban()
    {
        // "password" is on the list. A passphrase that contains the word is still a passphrase,
        // and banning the substring is how a policy ends up refusing "my password is a boat".
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("my password is a boat", Email));
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("qwerty is a silly word", Email));
    }

    [Fact]
    public void Every_entry_on_the_list_is_actually_refused_by_something()
    {
        // The short half of the list is caught by the length rule and the long half by the list
        // itself. Either way, no entry may be acceptable — an entry that is is a typo in the list.
        foreach (var entry in PasswordPolicy.Blocklist)
            Assert.NotEqual(PasswordVerdict.Acceptable, PasswordPolicy.Check(entry, Email));
    }

    [Fact]
    public void Every_refusal_has_a_sentence_that_does_not_quote_the_password()
    {
        foreach (var verdict in Enum.GetValues<PasswordVerdict>())
        {
            var sentence = PasswordPolicy.Explain(verdict);
            Assert.False(string.IsNullOrWhiteSpace(sentence));
        }

        // And the one for a too-short password says what the length actually is, rather than
        // leaving somebody to guess by adding a character at a time.
        Assert.Contains(PasswordPolicy.MinimumLength.ToString(), PasswordPolicy.Explain(PasswordVerdict.TooShort),
                        StringComparison.Ordinal);
    }

    [Fact]
    public void A_password_that_is_only_spaces_at_the_ends_is_kept_as_typed()
    {
        // Not trimmed. Trimming here would store a different password than the one the person will
        // type back at the sign-in page, and they would be locked out by a helpful tidy-up.
        Assert.Equal(PasswordVerdict.Acceptable, PasswordPolicy.Check("  spaced  out  ", Email));
    }
}
