using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// "Facebook served a login page" versus "there is nothing for sale near you".
///
/// These two produced the identical result for as long as the scrape only counted tiles: a session
/// Facebook no longer accepted got handed a login form, found no <c>/marketplace/item/</c> anchors
/// on it, and reported zero listings. The seller ran it again, got zero again, and concluded local
/// sourcing did not work — while the one thing they could have fixed in a single click was never
/// mentioned anywhere.
///
/// The HTML below is the shape of what those pages actually return: the title, the password field,
/// and the interstitial wording. The scrape sends a few hundred bytes of exactly that (see
/// FacebookMarketplaceService's page signature) rather than a megabyte of obfuscated markup.
/// </summary>
public class FacebookLoginWallTests
{
    private const string MarketplaceUrl = "https://www.facebook.com/marketplace/search/?query=antminer";

    // What a logged-in Marketplace page's signature looks like: a normal title and tile text.
    private const string MarketplaceSignature = """
        <title>Marketplace - Antminer S19 | Facebook</title>
        $1,200
        Las Vegas, NV
        Antminer S19 95TH miner with PSU
        $850
        Henderson, NV
        Bitmain S19j Pro — works, quiet
        """;

    private const string LoginSignature = """
        <title>Log into Facebook</title>
        name="pass"
        id="login_form"
        Log into Facebook
        You must log in to continue.
        Forgot account?
        """;

    private const string CheckpointSignature = """
        <title>Facebook</title>
        We need to confirm it's you
        We've detected unusual activity on your account. Please confirm your identity to continue.
        Continue
        """;

    private const string TwoFactorSignature = """
        <title>Facebook</title>
        Two-factor authentication required
        Enter the 6-digit code from your authentication app.
        Continue
        Try another way
        """;

    // ── A working session must never be thrown away ──────────────────────────

    // The expensive direction of a mistake: a false positive here marks a live login as dead and
    // sends the seller through a browser sign-in to fix nothing. Marketplace pages are full of
    // ordinary words, so nothing matches on a phrase like "log in" on its own.
    [Fact]
    public void A_real_Marketplace_page_is_not_a_login_wall()
    {
        Assert.Equal(FacebookLoginWall.None,
            FacebookMarketplaceParser.DetectLoginWall(MarketplaceUrl, MarketplaceSignature));
    }

    [Fact]
    public void The_picks_feed_is_not_a_login_wall()
    {
        Assert.Equal(FacebookLoginWall.None, FacebookMarketplaceParser.DetectLoginWall(
            "https://www.facebook.com/marketplace/?radius_in_km=64",
            "<title>Marketplace | Facebook</title>\nToday's picks\n$40\nFree\nLas Vegas, NV"));
    }

    [Fact]
    public void Nothing_at_all_is_not_a_login_wall()
    {
        // A scrape that failed before it loaded anything reports its own error; it must not also be
        // reported as an expired session, which would delete a perfectly good login's standing.
        Assert.Equal(FacebookLoginWall.None, FacebookMarketplaceParser.DetectLoginWall(null, null));
        Assert.Equal(FacebookLoginWall.None, FacebookMarketplaceParser.DetectLoginWall("", ""));
    }

    // ── The three walls ──────────────────────────────────────────────────────

    [Fact]
    public void A_redirect_to_the_login_page_is_a_login_wall()
    {
        Assert.Equal(FacebookLoginWall.Login, FacebookMarketplaceParser.DetectLoginWall(
            "https://www.facebook.com/login/?next=%2Fmarketplace%2Fsearch%2F", LoginSignature));
    }

    // Facebook does not always redirect: it will serve the login form on the Marketplace URL itself,
    // which is why the URL alone was never enough to detect this.
    [Fact]
    public void A_login_form_served_on_the_Marketplace_URL_is_still_a_login_wall()
    {
        Assert.Equal(FacebookLoginWall.Login,
            FacebookMarketplaceParser.DetectLoginWall(MarketplaceUrl, LoginSignature));
    }

    [Fact]
    public void A_checkpoint_is_reported_as_a_checkpoint()
    {
        Assert.Equal(FacebookLoginWall.Checkpoint, FacebookMarketplaceParser.DetectLoginWall(
            "https://www.facebook.com/checkpoint/1501092823525282/", CheckpointSignature));
    }

    // A two-factor prompt is served under /checkpoint/ and carries a form of its own, so checking
    // the broader cases first would file it as an ordinary expired session — and tell the seller
    // nothing about the code they are about to be asked for.
    [Fact]
    public void A_two_factor_prompt_outranks_the_checkpoint_it_is_served_under()
    {
        Assert.Equal(FacebookLoginWall.TwoFactor, FacebookMarketplaceParser.DetectLoginWall(
            "https://www.facebook.com/checkpoint/?next=https%3A%2F%2Fwww.facebook.com%2Fmarketplace%2F",
            TwoFactorSignature));
    }

    [Fact]
    public void The_two_step_verification_URL_is_a_two_factor_wall()
    {
        Assert.Equal(FacebookLoginWall.TwoFactor, FacebookMarketplaceParser.DetectLoginWall(
            "https://www.facebook.com/two_step_verification/authentication/?next=x", ""));
    }

    // ── What the seller is told ──────────────────────────────────────────────

    // All three end in "reconnect", but they are not the same event: telling someone with a security
    // checkpoint that their session "expired" sends them through a sign-in Facebook is going to
    // interrupt again, for a reason nothing warned them about.
    [Theory]
    [InlineData(FacebookLoginWall.Login)]
    [InlineData(FacebookLoginWall.Checkpoint)]
    [InlineData(FacebookLoginWall.TwoFactor)]
    public void Every_wall_gets_a_sentence_that_names_the_next_action(FacebookLoginWall wall)
    {
        var sentence = FacebookMarketplaceParser.DescribeLoginWall(wall);

        Assert.NotEqual("", sentence);
        Assert.Contains("econnect", sentence);   // Reconnect / reconnect
    }

    [Fact]
    public void The_three_sentences_are_not_the_same_sentence()
    {
        var login = FacebookMarketplaceParser.DescribeLoginWall(FacebookLoginWall.Login);
        var check = FacebookMarketplaceParser.DescribeLoginWall(FacebookLoginWall.Checkpoint);
        var twofa = FacebookMarketplaceParser.DescribeLoginWall(FacebookLoginWall.TwoFactor);

        Assert.NotEqual(login, check);
        Assert.NotEqual(check, twofa);
        Assert.Contains("checkpoint", check, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two-factor", twofa, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_wall_has_nothing_to_say()
    {
        Assert.Equal("", FacebookMarketplaceParser.DescribeLoginWall(FacebookLoginWall.None));
    }
}
