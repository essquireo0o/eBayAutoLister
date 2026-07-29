using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns the raw text lines of a Marketplace result tile into a listing.
///
/// The browser side deliberately extracts nothing but href + image + innerText lines
/// (see FacebookMarketplaceSelectors), so all the interpretation lives here as ordinary
/// C# that runs without a browser and is covered by tests. A tile has no field labels —
/// it's an unordered pile of short strings — so every line is classified by shape
/// (price / distance / posted-time / place / prose) and the leftovers resolve to a title.
/// </summary>
public static class FacebookMarketplaceParser
{
    public const string SourceId = "facebook";
    public const string SourceLabel = "Facebook Marketplace";

    private static readonly Regex ItemIdRx   = new(@"/marketplace/item/(\d+)", RegexOptions.Compiled);
    private static readonly Regex PriceRx    = new(@"^\$\s*([\d,]+(?:\.\d{1,2})?)$", RegexOptions.Compiled);
    private static readonly Regex DistanceRx = new(@"^(?:about\s+)?([\d.]+)\s*(?:mi|mile|miles|km|kilometers?)\s*(?:away)?$",
                                                   RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PostedRx   = new(@"^(just listed|new listing|listed\s+.+|.+\s+ago)$",
                                                   RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // "Las Vegas, NV" / "Henderson, Nevada" — a place line, not a title.
    private static readonly Regex PlaceRx    = new(@"^[^,]{2,40},\s*[A-Za-z .]{2,20}$", RegexOptions.Compiled);

    // Chrome renders these into the tile text but they aren't listing data.
    private static readonly string[] NoiseLines =
        ["sponsored", "see more", "free shipping", "shipping available", "save", "message", "featured"];

    // Badges that mean the item is spoken for. Before this they fell through to `prose`, where a
    // four-letter word could not win the longest-line contest for Title but could still be picked
    // up as Location — so a sold item quietly became a buyable row located in "Sold".
    private static readonly string[] SoldLines =
        ["sold", "pending", "sale pending", "sold out", "no longer available"];

    public static int NearestSupportedRadius(int requestedMiles)
    {
        var options = FacebookMarketplaceSelectors.SupportedRadiiMiles;
        if (requestedMiles <= options[0]) return options[0];
        if (requestedMiles >= options[^1]) return options[^1];
        // Ties (e.g. exactly halfway between 40 and 60) round UP to the wider radius —
        // a sourcing search would rather see one extra town than miss one.
        return options.OrderBy(o => Math.Abs(o - requestedMiles)).ThenByDescending(o => o).First();
    }

    public static LocalSupplyListing? ParseCard(FacebookRawCard card)
    {
        var itemId = ItemIdRx.Match(card.Href ?? "").Groups[1].Value;
        if (string.IsNullOrEmpty(itemId)) return null;

        var listing = new LocalSupplyListing
        {
            Source      = SourceId,
            SourceLabel = SourceLabel,
            ItemId      = itemId,
            Url         = $"https://www.facebook.com/marketplace/item/{itemId}/",
            ImageUrl    = card.ImageUrl ?? "",
        };

        var prices = new List<decimal>();
        var prose  = new List<string>();

        foreach (var raw in card.Lines ?? [])
        {
            var line = (raw ?? "").Trim();
            if (line.Length == 0 || NoiseLines.Contains(line.ToLowerInvariant())) continue;

            if (SoldLines.Contains(line.ToLowerInvariant()))
            {
                listing.IsSold = true;
                if (listing.SoldStateText.Length == 0) listing.SoldStateText = line;
                continue;
            }

            if (line.Equals("free", StringComparison.OrdinalIgnoreCase))
            {
                listing.IsFree = true;
                if (listing.PriceText.Length == 0) listing.PriceText = "Free";
                continue;
            }

            var priceMatch = PriceRx.Match(line);
            if (priceMatch.Success &&
                decimal.TryParse(priceMatch.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
            {
                prices.Add(price);
                if (listing.PriceText.Length == 0) listing.PriceText = line;
                continue;
            }

            var distanceMatch = DistanceRx.Match(line);
            if (distanceMatch.Success &&
                double.TryParse(distanceMatch.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var distance))
            {
                // Facebook shows km to accounts outside the US; normalise so the UI's
                // "within N miles" always means the same thing.
                listing.DistanceMiles = line.Contains("km", StringComparison.OrdinalIgnoreCase)
                    ? Math.Round(distance / 1.60934, 1)
                    : distance;
                continue;
            }

            if (PostedRx.IsMatch(line)) { listing.PostedAgo = line; continue; }
            if (PlaceRx.IsMatch(line) && listing.Location.Length == 0) { listing.Location = line; continue; }

            prose.Add(line);
        }

        if (prices.Count > 0)
        {
            // The listed price is the lowest number shown; a higher second price is the
            // struck-through original, i.e. this seller has already cut their price.
            listing.Price = prices.Min();
            var original = prices.Max();
            if (original > listing.Price) listing.OriginalPrice = original;
        }

        // Titles are the longest line on a tile — everything else is a short badge, price or
        // place — and Facebook truncates them with an ellipsis rather than wrapping.
        listing.Title = prose.OrderByDescending(l => l.Length).FirstOrDefault() ?? "";
        if (listing.Location.Length == 0 && prose.Count > 1)
            listing.Location = prose.LastOrDefault(l => l != listing.Title) ?? "";

        if (listing.Title.Length == 0) return null;
        if (listing.Price is null && !listing.IsFree) return null;

        return listing;
    }

    /// <summary>
    /// Parses every tile, drops duplicates (the virtualised grid re-renders tiles as it
    /// scrolls, so the same item is scraped more than once), keeps only what actually
    /// relates to the query, and summarises the local ask-price spread.
    /// </summary>
    public static LocalSupplySearchResult BuildResult(
        IEnumerable<FacebookRawCard> cards, string query, string zip, int radiusMiles)
    {
        var parsed = (cards ?? []).Select(ParseCard).Where(l => l is not null).Select(l => l!);
        var all = LocalSupplyResults.Dedupe(parsed);

        all = FilterByRelevance(all, query);

        // Split before anything is summarised. A sold tile still shows a price, so leaving it in
        // would put an item nobody can buy into the ranked deals AND drag the local ask median
        // toward a number that is no longer on offer.
        var sold  = all.Where(i => i.IsSold).ToList();
        var items = all.Where(i => !i.IsSold).ToList();

        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId    = SourceId,
            SourceLabel = SourceLabel,
            Status      = "ok",
            Query       = query,
            ZipCode     = zip,
            RadiusMiles = NearestSupportedRadius(radiusMiles),
            SearchUrl   = FacebookMarketplaceSelectors.BuildSearchUrl(query, radiusMiles),
            ScopeLabel  = string.IsNullOrWhiteSpace(zip) ? "" : $"within {NearestSupportedRadius(radiusMiles)} mi of {zip}",
            Items       = items,
            SoldItems   = sold,
            Min = min, Median = median, Max = max,
        };
    }

    /// <summary>
    /// Facebook pads a thin result set with loosely-related items ("people also searched"),
    /// which would drag a local price median somewhere meaningless. Craigslist does the same
    /// thing with "few local results found", so the rule itself now lives in
    /// <see cref="LocalSupplyResults"/> and both sources share it.
    /// </summary>
    public static List<LocalSupplyListing> FilterByRelevance(List<LocalSupplyListing> items, string query) =>
        LocalSupplyResults.FilterByRelevance(items, query);

    // ── Bounced to a login page ───────────────────────────────────────────────

    /// <summary>
    /// Reads <paramref name="url"/> and <paramref name="html"/> and says whether Facebook served a
    /// wall instead of Marketplace.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between "there is nothing for sale near you" and "your login is dead",
    /// and until it existed both looked identical: a scrape replayed a session Facebook no longer
    /// accepted, found no <c>/marketplace/item/</c> tiles on the login page it was handed, and
    /// reported zero results. The seller re-ran it, got zero again, and concluded the feature did not
    /// work. Nothing anywhere said "reconnect".
    /// </para>
    /// <para>
    /// Both halves are needed. The URL alone misses the case where Facebook serves the login form on
    /// the Marketplace URL without redirecting; the HTML alone misses a redirect to a checkpoint that
    /// renders behind a loading shell. Either one is enough to answer yes.
    /// </para>
    /// <para>
    /// Deliberately strict about which strings count. A logged-in Marketplace page carries menus and
    /// footers full of ordinary words, so nothing here matches on a phrase like "log in" on its own —
    /// only on markers that belong to the wall itself: the password field, the login form's id, and
    /// Facebook's own interstitial wording. A false positive here throws away a working session.
    /// </para>
    /// </remarks>
    public static FacebookLoginWall DetectLoginWall(string? url, string? html)
    {
        var u = url ?? "";
        var h = html ?? "";

        // Most specific first: a two-factor page is served under /checkpoint/ and carries a password
        // field of its own, so checking either of the broader cases first would mislabel it.
        if (MentionsAny(u, "two_step_verification", "two_factor")
            || MentionsAny(h, "two-factor authentication", "two_step_verification",
                           "enter the 6-digit code", "login approval", "approvals_code",
                           "check your text messages", "authentication app"))
            return FacebookLoginWall.TwoFactor;

        if (MentionsAny(u, "/checkpoint", "/recover", "/confirm")
            || MentionsAny(h, "we need to confirm", "confirm your identity",
                           "your account has been temporarily", "help us confirm",
                           "we've detected unusual activity", "checkpoint/block"))
            return FacebookLoginWall.Checkpoint;

        if (MentionsAny(u, "/login", "login.php")
            || MentionsAny(h, "name=\"pass\"", "name='pass'", "id=\"login_form\"", "id=\"loginform\"",
                           "you must log in to continue", "log into facebook",
                           "action=\"/login", "action=\"https://www.facebook.com/login"))
            return FacebookLoginWall.Login;

        return FacebookLoginWall.None;
    }

    /// <summary>
    /// The sentence a seller gets for a wall. All three end in the same action — reconnect — but
    /// they are not the same event, and a checkpoint that says "expired" sends someone through a
    /// login that Facebook is going to interrupt again for a reason nothing told them about.
    /// </summary>
    public static string DescribeLoginWall(FacebookLoginWall wall) => wall switch
    {
        FacebookLoginWall.TwoFactor =>
            "Facebook asked for a two-factor code instead of showing Marketplace, which a saved session "
            + "can't answer. Reconnect and complete the code in the login window.",
        FacebookLoginWall.Checkpoint =>
            "Facebook put a security checkpoint in front of the account instead of showing Marketplace. "
            + "Reconnect and clear the checkpoint in the login window — searches stay blocked until you do.",
        FacebookLoginWall.Login =>
            "Facebook asked the saved session to log in again instead of showing Marketplace, so the "
            + "session has expired. Reconnect to search Marketplace again.",
        _ => "",
    };

    private static bool MentionsAny(string text, params string[] needles)
    {
        if (text.Length == 0) return false;
        foreach (var needle in needles)
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>
/// What Facebook put in front of a replayed session instead of Marketplace. All three mean the same
/// thing to the search — no results, do not report an empty local market — but they mean different
/// things to the seller, so they are kept apart rather than collapsed into "logged out".
/// </summary>
public enum FacebookLoginWall
{
    None,
    Login,
    Checkpoint,
    TwoFactor,
}
