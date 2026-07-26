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
        var items = LocalSupplyResults.Dedupe(parsed);

        items = FilterByRelevance(items, query);
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
}
