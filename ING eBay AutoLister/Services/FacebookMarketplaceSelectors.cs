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

    // Where the one-time login lands, and it must be the login form itself — NOT a Marketplace
    // URL. The login browser opens with no cookies, and Facebook answers a logged-out hit on
    // any /marketplace page with an endless www→m→www bounce (measured: 19 redirects, then
    // ERR_TOO_MANY_REDIRECTS), which leaves the window sitting on about:blank with no way to
    // sign in. /login/ serves the form directly, with zero redirects, logged out.
    //
    // Signed-in detection does not read this URL at all — it waits for Facebook's own c_user
    // cookie — so where the browser ends up after sign-in doesn't matter here.
    public const string LoginLandingUrl = "https://www.facebook.com/login/";

    // Tried if the landing URL fails to load at all. Same form, different entry point: if
    // Facebook is redirect-looping one of these, it is usually still serving the other.
    public const string LoginFallbackUrl = "https://www.facebook.com/login.php";

    // Marketplace's front page — "Today's picks". No query, no filters: it is whatever Facebook
    // has decided to show THIS account near its own saved location, which is exactly what the
    // seller sees when they open Marketplace themselves. Worth reading precisely because it is not
    // a search: it surfaces local supply the seller would never have thought to type.
    public const string PicksUrl = "https://www.facebook.com/marketplace/";

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

    // ── Every filter Marketplace itself offers ────────────────────────────────
    // Facebook refuses to be embedded — it answers every Marketplace URL with
    // "X-Frame-Options: DENY", so putting their page inside this one is not possible in any
    // browser. What IS possible is offering the same controls, because Marketplace drives its own
    // filters entirely through query-string parameters. These are those parameters.
    //
    // The values are Facebook's own strings, not this app's: sortBy=price_ascend, not "cheapest".
    // Anything else here would be a translation layer to get wrong on the day they add an option.

    public static readonly (string Value, string Label)[] SortOptions =
    [
        ("",                      "Best match"),
        ("creation_time_descend", "Newest first"),
        ("price_ascend",          "Price: low to high"),
        ("price_descend",         "Price: high to low"),
        ("distance_ascend",       "Closest first"),
    ];

    public static readonly (string Value, string Label)[] ConditionOptions =
    [
        ("",               "Any condition"),
        ("new",            "New"),
        ("used_like_new",  "Used — like new"),
        ("used_good",      "Used — good"),
        ("used_fair",      "Used — fair"),
    ];

    public static readonly (string Value, string Label)[] DateListedOptions =
    [
        ("",   "Any time"),
        ("1",  "Last 24 hours"),
        ("7",  "Last 7 days"),
        ("30", "Last 30 days"),
    ];

    public static readonly (string Value, string Label)[] DeliveryOptions =
    [
        ("",              "Any delivery"),
        ("local_pick_up", "Local pickup only"),
        ("shipping",      "Shipped to you"),
    ];

    /// <summary>
    /// Marketplace's own category boards. Slugs are what Facebook puts in the path — a category
    /// search is a different URL, not a filter on the search one.
    /// </summary>
    public static readonly (string Slug, string Label)[] CategoryOptions =
    [
        ("",              "All categories"),
        ("vehicles",      "Vehicles"),
        ("apparel",       "Apparel"),
        ("electronics",   "Electronics"),
        ("furniture",     "Home & furniture"),
        ("garden",        "Home improvement & garden"),
        ("toys",          "Toys & games"),
        ("sportinggoods", "Sporting goods"),
        ("tools",         "Tools"),
        ("musical",       "Musical instruments"),
        ("propertyrentals", "Property rentals"),
        ("free",          "Free stuff"),
    ];

    /// <summary>
    /// One Marketplace browse, expressed the way Facebook expresses it. Empty strings mean "don't
    /// send that parameter at all" rather than "send an empty one", which Marketplace treats as a
    /// filter set to nothing and returns zero results for.
    /// </summary>
    public record BrowseFilters(
        string Query = "",
        string CategorySlug = "",
        decimal? MinPrice = null,
        decimal? MaxPrice = null,
        string Condition = "",
        string DaysListed = "",
        string Delivery = "",
        string SortBy = "",
        int RadiusMiles = 40)
    {
        public bool IsPicks => string.IsNullOrWhiteSpace(Query) && string.IsNullOrWhiteSpace(CategorySlug);
    }

    /// <summary>
    /// Builds the Marketplace URL for <paramref name="f"/>. Three shapes, because Facebook uses
    /// three: the picks feed, a category board, and a keyword search.
    /// </summary>
    public static string BuildBrowseUrl(BrowseFilters f)
    {
        var km = (int)Math.Round(FacebookMarketplaceParser.NearestSupportedRadius(f.RadiusMiles) * 1.60934);

        var query = new List<string>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) query.Add($"{key}={Uri.EscapeDataString(value)}");
        }

        Add("radius_in_km", km.ToString());
        if (f.MinPrice is > 0) Add("minPrice", ((int)f.MinPrice).ToString());
        if (f.MaxPrice is > 0) Add("maxPrice", ((int)f.MaxPrice).ToString());
        Add("itemCondition", f.Condition);
        Add("daysSinceListed", f.DaysListed);
        Add("deliveryMethod", f.Delivery);
        Add("sortBy", f.SortBy);

        string basePath;
        if (!string.IsNullOrWhiteSpace(f.Query))
        {
            basePath = "https://www.facebook.com/marketplace/search/";
            query.Insert(0, $"query={Uri.EscapeDataString(f.Query)}");
            query.Add("exact=false");
        }
        else if (!string.IsNullOrWhiteSpace(f.CategorySlug))
        {
            basePath = $"https://www.facebook.com/marketplace/category/{Uri.EscapeDataString(f.CategorySlug)}/";
        }
        else
        {
            basePath = PicksUrl;
        }

        return query.Count == 0 ? basePath : $"{basePath}?{string.Join("&", query)}";
    }

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
