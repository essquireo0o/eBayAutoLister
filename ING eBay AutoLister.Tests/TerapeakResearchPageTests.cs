using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Telling "this item has no recent sales" apart from "your Terapeak login is dead", on the only
/// evidence there is: the URL a headless load ended on and what the page said.
/// </summary>
/// <remarks>
/// <para>
/// Until this existed the two were identical. A lookup replayed a session eBay no longer accepted,
/// ran the sold-comp regexes over the sign-in page it was handed, matched none of them, and returned
/// an empty result — and the pricing code cannot tell an empty result from a genuine absence of
/// sales. The seller got a confident number built on a sample of zero, and nothing anywhere said
/// "reconnect".
/// </para>
/// <para>
/// The HTML and innerText below are representative of what eBay actually serves each of these
/// sessions, cut down to the parts a detector can see. Both forms are tested because both reach the
/// detector: the scrape reads <c>document.body.innerText</c>, and a caller holding raw HTML uses the
/// same call.
/// </para>
/// <para>
/// The false-positive cases are not padding. A research page is a table of listing titles the seller
/// searched for, and those titles are arbitrary text — a marker loose enough to match one throws
/// away a working session and sends the seller through a six-minute browser sign-in to fix nothing.
/// </para>
/// </remarks>
public class TerapeakResearchPageTests
{
    private const string ResearchUrl =
        "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords=yaskawa+servo+amplifier";

    /// <summary>What a working lookup gets: the rendered research page, stat tiles and a results table.</summary>
    private const string RenderedResearchPage = """
        Product research
        Yaskawa servo amplifier
        Last 60 days · Sold listings
        Avg sold price $412.55
        Sold price range $180.00 - $899.00
        Avg shipping $18.40
        Total sold 47
        Sell-through 82%
        YASKAWA SGDV-2R8A01A SERVOPACK AC Servo Amplifier 400W  $389.00  Sold Jul 12, 2026
        YASKAWA SGDH-10AE Servo Amplifier 1.0kW 200V TESTED     $455.00  Sold Jul 09, 2026
        """;

    /// <summary>eBay's sign-in form, served in place on a Seller Hub URL — the URL never changes.</summary>
    private const string SignInFormHtml = """
        <html><head><title>Sign in to your eBay account</title></head><body>
          <form name="SignInForm" method="post">
            <input type="text" id="userid" name="userid" autocomplete="username" />
            <input type="password" id="pass" name="pass" autocomplete="current-password" />
            <input type="checkbox" name="keepMeSignInOption" /> Stay signed in
            <button type="submit">Sign in</button>
          </form>
        </body></html>
        """;

    /// <summary>The same wall as the seller's eyes see it — no markup, just the words on screen.</summary>
    private const string SignInInnerText = """
        Hello
        Sign in to continue to Seller Hub
        Email or username
        Password
        Continue
        """;

    /// <summary>The subscription wall. Served with a 200 ON /sh/research, so only the words give it away.</summary>
    private const string SubscriptionWallText = """
        Product research
        Terapeak product research requires a Store subscription.
        Full sold and completed listing data going back 365 days is available to Basic Store
        subscription or above. Choose a Store plan to unlock the full research tools.
        Compare Store plans
        """;

    /// <summary>eBay's automated-traffic splash. Note that it carries its own sign-in wording.</summary>
    private const string ChallengeHtml = """
        <html><body>
          <h1>Pardon Our Interruption</h1>
          <p>As you were browsing, something about your browser made us think you were a bot.</p>
          <p>Please verify yourself to continue, or sign in to your account to keep shopping.</p>
          <div id="px-captcha"></div>
        </body></html>
        """;

    // ── The sign-in wall ─────────────────────────────────────────────────────

    // The unambiguous case: eBay moved the browser to its own sign-in host. Nothing else needs
    // reading — eBay has stated outright that this session is not signed in.
    [Fact]
    public void A_redirect_to_the_sign_in_host_is_SignIn()
    {
        var url = "https://signin.ebay.com/ws/eBayISAPI.dll?SignIn&ru=https%3A%2F%2Fwww.ebay.com%2Fsh%2Fresearch";

        Assert.Equal(TerapeakPageWall.SignIn, TerapeakResearchPage.DetectWall(url, SignInFormHtml));
    }

    // The one the URL alone misses, and the reason the page text is read at all: eBay serves the
    // login form on the Seller Hub URL itself, so a check that only looked at where it landed saw
    // /sh/research and called it a working page with no comps on it.
    [Fact]
    public void The_sign_in_form_served_in_place_on_the_research_URL_is_SignIn()
    {
        Assert.Equal(TerapeakPageWall.SignIn, TerapeakResearchPage.DetectWall(ResearchUrl, SignInFormHtml));
    }

    [Fact]
    public void The_sign_in_wall_is_detected_from_rendered_text_as_well_as_HTML()
    {
        // The scrape reads innerText, so the form-field markers above are not present at all — the
        // wording has to be enough on its own.
        Assert.Equal(TerapeakPageWall.SignIn, TerapeakResearchPage.DetectWall(ResearchUrl, SignInInnerText));
    }

    // ── The subscription wall ────────────────────────────────────────────────

    // Invisible to every check except reading the page: 200 OK, right URL, no redirect, no login
    // form. The session is fine and will never work, which is why it is its own wall rather than
    // being filed as an expired login.
    [Fact]
    public void A_subscription_wall_served_on_the_research_URL_is_Subscription()
    {
        Assert.Equal(TerapeakPageWall.Subscription,
            TerapeakResearchPage.DetectWall(ResearchUrl, SubscriptionWallText));
    }

    [Fact]
    public void A_lapsed_subscription_is_also_Subscription()
    {
        const string lapsed = """
            Product research
            Your subscription has expired. Renew your eBay Store to get back to full sold data.
            """;

        Assert.Equal(TerapeakPageWall.Subscription, TerapeakResearchPage.DetectWall(ResearchUrl, lapsed));
    }

    // ── The bot check ────────────────────────────────────────────────────────

    // Ordered ahead of the sign-in text markers on purpose. The splash carries its own "sign in to
    // your account" wording, so testing sign-in first files a challenge as an expired login — and
    // reconnecting does not clear a challenge, so that sends the seller through a six-minute sign-in
    // to fix nothing.
    [Fact]
    public void The_bot_check_is_a_Challenge_even_though_it_says_sign_in()
    {
        Assert.Equal(TerapeakPageWall.Challenge, TerapeakResearchPage.DetectWall(ResearchUrl, ChallengeHtml));
    }

    [Fact]
    public void The_bot_check_splash_URL_is_a_Challenge()
    {
        Assert.Equal(TerapeakPageWall.Challenge,
            TerapeakResearchPage.DetectWall("https://www.ebay.com/splashui/captcha?ru=%2Fsh%2Fresearch", ""));
    }

    // A challenge drawn on top of a genuine sign-in redirect is still a dead session: eBay only
    // bounces to signin.ebay.com for a browser it does not consider signed in, and that fact
    // outranks whatever it drew on the way.
    [Fact]
    public void A_challenge_on_the_sign_in_host_is_still_SignIn()
    {
        Assert.Equal(TerapeakPageWall.SignIn,
            TerapeakResearchPage.DetectWall("https://signin.ebay.com/ws/eBayISAPI.dll?SignIn", ChallengeHtml));
    }

    // ── The page that is fine ────────────────────────────────────────────────

    [Fact]
    public void The_rendered_research_page_is_not_a_wall()
    {
        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(ResearchUrl, RenderedResearchPage));
        Assert.True(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, RenderedResearchPage));
    }

    // The answer this whole file exists to protect. Terapeak reached, Terapeak answered, and the
    // answer is that nothing like this has sold — which is real information about the item and must
    // never be reported as a connection problem.
    [Fact]
    public void A_research_page_with_no_sales_on_it_is_a_working_page_not_a_wall()
    {
        const string empty = """
            Product research
            fanuc a06b-6079-h209 servo amplifier
            No results found for your search. Try fewer or different keywords.
            """;

        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(ResearchUrl, empty));
        Assert.True(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, empty));
    }

    // ── The false positives that would cost a working session ────────────────

    // Listing titles are arbitrary seller-written text, and they land in the page the detector
    // reads. Nothing may match on a single common word: "store", "subscription" and "sign" all
    // appear here in a perfectly ordinary results table.
    [Fact]
    public void Listing_titles_using_the_wall_words_do_not_trip_a_wall()
    {
        const string awkwardTitles = """
            Product research
            Avg sold price $28.10
            Total sold 63
            NEON OPEN SIGN Store Front Display Light Working              $34.00  Sold Jul 18, 2026
            Monthly Subscription Box Store Lot of 12 Sealed Mystery Items $22.50  Sold Jul 11, 2026
            Vintage Metal Store Sign In Original Paint 24in              $41.00  Sold Jul 02, 2026
            """;

        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(ResearchUrl, awkwardTitles));
        Assert.True(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, awkwardTitles));
    }

    // The research URL carries the seller's own keywords. Matching URL markers against the whole
    // thing lets a search for "captcha" or "signin" classify its own results page as a wall — so the
    // query string is dropped before anything is matched.
    [Theory]
    [InlineData("https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&keywords=captcha+book")]
    [InlineData("https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&keywords=signin+neon+sign")]
    [InlineData("https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&keywords=store%2Fsubscription")]
    public void The_sellers_own_search_terms_cannot_classify_their_results_as_a_wall(string url)
    {
        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(url, RenderedResearchPage));
    }

    // A load that failed before it fetched anything reports its own error. Reading it as an expired
    // session as well would throw away a perfectly good login over a dropped connection.
    [Fact]
    public void Nothing_loaded_at_all_is_not_a_verdict()
    {
        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall("", ""));
        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(null, null));
    }

    // ── What each wall means to the seller ───────────────────────────────────

    // Challenge is deliberately not in this set: it is eBay's bot detection talking about this
    // request, not about the account, and a reconnect does not clear one.
    [Theory]
    [InlineData(TerapeakPageWall.SignIn,       true)]
    [InlineData(TerapeakPageWall.Subscription, true)]
    [InlineData(TerapeakPageWall.Challenge,    false)]
    [InlineData(TerapeakPageWall.None,         false)]
    public void Only_the_walls_a_sign_in_actually_fixes_ask_for_a_reconnect(TerapeakPageWall wall, bool needsReconnect)
    {
        Assert.Equal(needsReconnect, TerapeakResearchPage.NeedsReconnect(wall));
    }

    // Both reconnectable walls end in the same button, but they are not the same event — telling
    // someone whose Store subscription lapsed that their "session expired" sends them through a
    // sign-in that lands on the same wall again.
    [Fact]
    public void Each_wall_is_described_in_its_own_words()
    {
        var signIn = TerapeakResearchPage.Describe(TerapeakPageWall.SignIn);
        var subscription = TerapeakResearchPage.Describe(TerapeakPageWall.Subscription);
        var challenge = TerapeakResearchPage.Describe(TerapeakPageWall.Challenge);

        Assert.Contains("Reconnect", signIn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("subscription", subscription, StringComparison.OrdinalIgnoreCase);
        // The one the seller must NOT be sent to a sign-in for: it clears on its own.
        Assert.Contains("try again", challenge, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(signIn, subscription);
        Assert.Equal("", TerapeakResearchPage.Describe(TerapeakPageWall.None));
    }

    // ── Confirming a fresh login for real ────────────────────────────────────

    // "No wall was detected" is not the same claim as "eBay served the research page", and the gap
    // between them is where "Terapeak says connected but every lookup is empty" lived. A blank page,
    // a loading shell and an eBay error page all clear every wall check — and every one of them used
    // to be declared a working connection because a file had been written.
    [Theory]
    [InlineData("")]
    [InlineData("Loading…")]
    [InlineData("Something went wrong. Please try again later.")]
    public void A_page_that_is_merely_not_a_wall_does_not_confirm_a_login(string text)
    {
        Assert.Equal(TerapeakPageWall.None, TerapeakResearchPage.DetectWall(ResearchUrl, text));
        Assert.False(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, text));
    }

    // The tiles are the same strings the parser matches on, so this confirms the page the lookup
    // actually needs rather than merely an eBay page that answered.
    [Fact]
    public void The_research_page_is_confirmed_by_the_tiles_the_parser_reads()
    {
        Assert.False(TerapeakResearchPage.LooksLikeResearchPage(
            "https://www.ebay.com/mys/home", RenderedResearchPage));

        Assert.False(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, SignInInnerText));
        Assert.False(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, SubscriptionWallText));
        Assert.False(TerapeakResearchPage.LooksLikeResearchPage(ResearchUrl, ChallengeHtml));
    }
}
