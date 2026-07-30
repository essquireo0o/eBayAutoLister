namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What eBay put in front of a replayed session instead of the Seller Hub Research page. All of
/// these mean the same thing to the lookup — no comps, and do NOT report an empty sold market — but
/// they mean different things to the seller, so they are kept apart rather than collapsed.
/// </summary>
public enum TerapeakPageWall
{
    /// <summary>The research page itself. Nothing is in the way.</summary>
    None,

    /// <summary>eBay asked the saved session to sign in again — a redirect to sign-in, or the form served in place.</summary>
    SignIn,

    /// <summary>eBay served the page but says this account's research access needs a Store subscription.</summary>
    Subscription,

    /// <summary>eBay's automated-traffic check ("verify yourself"). Says nothing about the login.</summary>
    Challenge,
}

/// <summary>
/// Reads the URL and rendered text of a Seller Hub Research page load and says whether eBay served
/// the research page or a wall.
/// </summary>
/// <remarks>
/// <para>
/// This is the difference between "this item has no recent sales" and "your Terapeak login is
/// dead", and until it existed both looked identical. A lookup replayed a session eBay no longer
/// accepted, ran the sold-comp regexes over the sign-in page it was handed, matched none of them,
/// and returned an empty result. The pricing code cannot tell an empty result from a genuine
/// absence of sales, so it priced the item off whatever else it had and lowered nothing — the
/// seller got a confident number built on a sample of zero, and nothing anywhere said "reconnect".
/// </para>
/// <para>
/// Both halves are needed. The URL alone misses the subscription wall, which eBay serves ON
/// /sh/research with a 200; the text alone misses a redirect to sign-in that renders behind a
/// loading shell. Either one is enough to answer yes.
/// </para>
/// <para>
/// Deliberately strict about which strings count. A real research page is a table of listing titles
/// the seller searched for, and any of them may contain ordinary words like "store", "sign" or
/// "subscription" — so nothing here matches on a single common word. Every marker is either a URL
/// eBay only uses for that wall, a form field that only exists on the sign-in page, or a phrase
/// long enough that a listing title cannot collide with it. A false positive throws away a working
/// session and sends the seller through a six-minute browser sign-in to fix nothing.
/// </para>
/// </remarks>
public static class TerapeakResearchPage
{
    /// <summary>
    /// Classifies one page load. <paramref name="text"/> is the page's rendered <c>innerText</c> (or
    /// its HTML — both are matched the same way, so a caller with either can use this).
    /// </summary>
    public static TerapeakPageWall DetectWall(string? url, string? text)
    {
        var u = UrlKey(url);
        var t = text ?? "";

        // Nothing loaded at all is not a verdict. A scrape that failed before it fetched anything
        // reports its own error; reporting it as an expired session as well would throw away a
        // perfectly good login over a dropped connection.
        if (u.Length == 0 && t.Length == 0) return TerapeakPageWall.None;

        // 1) A redirect to eBay's own sign-in host is unambiguous and outranks everything, including
        //    a bot check drawn on top of it: eBay has stated the session is not signed in.
        if (MentionsAny(u, "signin.ebay.com", "/signin"))
            return TerapeakPageWall.SignIn;

        // 2) The bot check, before the sign-in text markers. eBay's challenge splash carries its own
        //    form and its own "sign in" wording, so testing sign-in first files a challenge as an
        //    expired login — and reconnecting does not clear a challenge, so that sends the seller
        //    through a sign-in to fix nothing.
        if (MentionsAny(u, "/splashui", "captcha", "/challenge")
            || MentionsAny(t, "pardon our interruption", "verify yourself to continue",
                           "please verify yourself", "checking your browser",
                           "unusual traffic from your", "activity from your device"))
            return TerapeakPageWall.Challenge;

        // 3) The sign-in form served in place, without a redirect — eBay does this on Seller Hub
        //    URLs, so the URL still reads /sh/research while the page is a login.
        if (MentionsAny(t, "sign in to continue", "please sign in", "sign in to your account",
                        "sign in to view", "sign in or register", "stay signed in",
                        "name=\"pass\"", "name='pass'", "id=\"userid\"", "id='userid'",
                        "name=\"userid\"", "name='userid'"))
            return TerapeakPageWall.SignIn;

        // 4) The subscription wall. eBay serves this with a 200 on the research URL itself, so it
        //    is invisible to every check except reading what the page says. Phrases only, and long
        //    ones: "store subscription" on its own would match a listing title in the results table.
        if (MentionsAny(u, "/store/subscription", "/subscription/checkout")
            || MentionsAny(t, "requires a store subscription", "requires an ebay store subscription",
                           "subscribe to an ebay store", "sign up for a store subscription",
                           "basic store subscription or above", "your subscription has expired",
                           "your subscription has ended", "your store subscription has ended",
                           "upgrade your subscription", "subscription required",
                           "product research is available to", "don't have access to terapeak",
                           "do not have access to terapeak"))
            return TerapeakPageWall.Subscription;

        return TerapeakPageWall.None;
    }

    /// <summary>
    /// True when a wall means the saved login is finished and the seller must sign in again. A
    /// <see cref="TerapeakPageWall.Challenge"/> is not one of these — it is eBay's bot detection
    /// talking about this request, not about the account.
    /// </summary>
    public static bool NeedsReconnect(TerapeakPageWall wall) =>
        wall is TerapeakPageWall.SignIn or TerapeakPageWall.Subscription;

    /// <summary>
    /// The sentence a seller gets for a wall. Both reconnectable walls end in the same action, but
    /// they are not the same event — telling someone whose Store subscription lapsed that their
    /// "session expired" sends them through a sign-in that will land on the same wall again.
    /// </summary>
    public static string Describe(TerapeakPageWall wall) => wall switch
    {
        TerapeakPageWall.SignIn =>
            "eBay asked the saved session to sign in again instead of showing Terapeak research, so the "
            + "session has expired. Reconnect Terapeak in Settings to use its sold data again.",
        TerapeakPageWall.Subscription =>
            "eBay served a subscription page instead of Terapeak research — its full sold-data research "
            + "needs an eBay Store subscription on this account. Check the subscription on eBay, then "
            + "reconnect Terapeak in Settings.",
        TerapeakPageWall.Challenge =>
            "eBay answered with an automated-traffic check instead of the research page, so this lookup "
            + "got no sold data. That's eBay's bot detection, not your account — it usually clears on "
            + "its own, so try again in a few minutes before reconnecting.",
        _ => "",
    };

    /// <summary>
    /// True when this really is the rendered research page — the URL eBay serves it at, plus at
    /// least one of the stat tiles the parser reads.
    /// </summary>
    /// <remarks>
    /// Used to confirm a fresh login for real rather than trusting that a file got written. "No wall
    /// was detected" is not the same claim: a blank page, a loading shell and an eBay error page all
    /// clear every check above, and every one of them would have been declared a working connection.
    /// The tile labels are the same strings TerapeakMarketService.ParseTerapeakBodyText matches on,
    /// so this confirms the page the parser actually needs, not merely an eBay page.
    /// </remarks>
    public static bool LooksLikeResearchPage(string? url, string? text)
    {
        if (DetectWall(url, text) != TerapeakPageWall.None) return false;
        if (!MentionsAny(UrlKey(url), "/sh/research")) return false;

        return MentionsAny(text ?? "", "avg sold price", "sold price range", "sell-through",
                           "avg shipping", "total sold", "no results found", "no data available");
    }

    /// <summary>
    /// Host + path of a URL, lowercased. The query string is dropped on purpose: a research URL
    /// carries the seller's own keywords, so matching URL markers against the whole thing lets a
    /// search for "captcha" or "signin parts" classify its own results page as a wall.
    /// </summary>
    private static string UrlKey(string? url)
    {
        var raw = (url ?? "").Trim();
        if (raw.Length == 0) return "";
        if (Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
            return (parsed.Host + parsed.AbsolutePath).ToLowerInvariant();
        // Not an absolute URL — a relative path, or "about:blank". Take everything before the query.
        var cut = raw.IndexOf('?');
        return (cut >= 0 ? raw[..cut] : raw).ToLowerInvariant();
    }

    private static bool MentionsAny(string text, params string[] needles)
    {
        if (text.Length == 0) return false;
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
