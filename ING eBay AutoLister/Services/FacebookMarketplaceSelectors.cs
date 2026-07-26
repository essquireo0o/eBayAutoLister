using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every Facebook-specific string in one place: URL shape, DOM selectors, and the radius
/// values Facebook's own dropdown offers.
///
/// Facebook rewrites its markup (and its obfuscated class names) constantly, so the scrape
/// in FacebookMarketplaceService deliberately holds no selectors of its own — it reads this
/// config, and every selector is a CANDIDATE LIST tried in order until one matches. When
/// Facebook changes something, the fix is editing a string here rather than touching the
/// browser plumbing or the parser. Nothing here is aria-label-only by accident either: the
/// accessibility labels survive Facebook's class-name churn far better than the classes do.
/// </summary>
public static class FacebookMarketplaceSelectors
{
    // Sorted newest-first: a sourcing search wants what appeared since the seller last looked,
    // not the same aged listings every time.
    public const string SearchUrlTemplate =
        "https://www.facebook.com/marketplace/search/?query={query}&radius_in_km={radiusKm}&sortBy=creation_time_descend&exact=false";

    // Where the one-time login lands. Reaching a /marketplace URL that isn't a login or a
    // checkpoint is what the login script treats as "signed in".
    public const string LoginLandingUrl = "https://www.facebook.com/marketplace/you/selling";

    // Result tiles. Every Marketplace tile is an anchor to /marketplace/item/<id>/ regardless
    // of which grid layout is being A/B tested, which makes the href the most durable hook
    // on the page.
    public static readonly string[] CardSelectors =
    [
        "a[href*='/marketplace/item/']",
    ];

    // "Sign in", checkpoint and account-recovery interstitials — any of these means the saved
    // session is no longer good and the user has to reconnect from Settings.
    public const string LoggedOutUrlPattern = @"facebook\.com/(login|checkpoint|recover|two_step_verification)";
    public static readonly string[] LoggedOutSelectors =
    [
        "input[name='pass']",
        "form[action*='login']",
    ];

    // ── Location dialog ───────────────────────────────────────────────────────
    // Facebook keys Marketplace results off the account's saved location, not off a URL
    // parameter, so a zip search means driving the real dialog: open it, type the zip, take
    // the first suggestion, pick the radius, apply.
    public static readonly string[] LocationOpenSelectors =
    [
        "div[aria-label='Change location']",
        "span[aria-label='Change location']",
        "div[role='button']:has-text('Within ')",
        "div[role='button'][aria-label*='miles']",
    ];

    public static readonly string[] LocationInputSelectors =
    [
        "input[aria-label='Location']",
        "input[placeholder='Location']",
        "div[role='dialog'] input[type='text']",
    ];

    public static readonly string[] LocationSuggestionSelectors =
    [
        "ul[role='listbox'] li:first-child",
        "div[role='listbox'] div[role='option']",
        "div[role='dialog'] ul li:first-child",
    ];

    public static readonly string[] RadiusOpenSelectors =
    [
        "div[role='dialog'] label[aria-label='Radius']",
        "div[role='dialog'] div[aria-label='Radius']",
        "div[role='dialog'] select",
    ];

    public static readonly string[] ApplySelectors =
    [
        "div[role='dialog'] div[aria-label='Apply']",
        "div[role='dialog'] div[aria-label='Save']",
        "div[role='dialog'] span:has-text('Apply')",
    ];

    // The exact radii Facebook's dropdown offers, in miles. A request for anything else is
    // snapped to the nearest of these rather than silently ignored — see
    // FacebookMarketplaceParser.NearestSupportedRadius.
    public static readonly int[] SupportedRadiiMiles = [1, 2, 5, 10, 20, 40, 60, 80, 100, 250, 500];

    // How many wheel scrolls to spend loading more of the virtualised result grid. Facebook
    // renders roughly 8-12 tiles per screen; each scroll costs ~1.2s of wall clock.
    public const int ScrollPasses = 6;

    /// <summary>Config object handed to the node scrape script, so the script itself stays selector-free.</summary>
    public static string ToJson(string query, int radiusMiles, string zip) => JsonSerializer.Serialize(new
    {
        searchUrl = BuildSearchUrl(query, radiusMiles),
        zip,
        radiusMiles = FacebookMarketplaceParser.NearestSupportedRadius(radiusMiles),
        cardSelectors        = CardSelectors,
        loggedOutUrlPattern  = LoggedOutUrlPattern,
        loggedOutSelectors   = LoggedOutSelectors,
        locationOpen         = LocationOpenSelectors,
        locationInput        = LocationInputSelectors,
        locationSuggestion   = LocationSuggestionSelectors,
        radiusOpen           = RadiusOpenSelectors,
        apply                = ApplySelectors,
        scrollPasses         = ScrollPasses,
    });

    public static string BuildSearchUrl(string query, int radiusMiles)
    {
        var miles = FacebookMarketplaceParser.NearestSupportedRadius(radiusMiles);
        // Facebook's URL parameter is kilometres even on a US account showing miles.
        var km = (int)Math.Round(miles * 1.60934);
        return SearchUrlTemplate
            .Replace("{query}", Uri.EscapeDataString(query))
            .Replace("{radiusKm}", km.ToString());
    }
}
