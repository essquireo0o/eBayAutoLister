using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Everything craigslist-shaped that isn't an HTTP call: the search URL, the RSS feed, the
/// no-JavaScript HTML fallback, and the small amount of interpretation a craigslist post needs.
///
/// Craigslist is the easy source, and this file is the reason: the search results are public and
/// there is no login, no session and no browser anywhere in the path. Compare
/// FacebookMarketplaceService, which needs a saved logged-in browser session to read the same kind
/// of data.
///
/// Two shapes are parsed because craigslist serves two:
///   • The static results list in the search page (<c>li.cl-static-search-result</c>), rendered
///     server-side for clients that don't run JavaScript. This is the primary source — see
///     CraigslistService for why.
///   • RSS — RDF/RSS 1.0 with dc:date and enc:enclosure, and richer where it's still served
///     (real timestamps, thumbnails). Kept as the fallback.
/// </summary>
public static class CraigslistParser
{
    public const string SourceId = "craigslist";
    public const string SourceLabel = "Craigslist";

    // Craigslist pages the RSS feed 25 posts at a time (the static results list isn't paged —
    // it carries roughly a hundred posts in one response).
    public const int PageSize = 25;

    private static readonly Regex PostIdRx = new(@"/(\d{7,})\.html", RegexOptions.Compiled);
    // The current shape: craigslist.org/view/d/las-vegas-iphone-15/dgvCZzozi9U4HqEj5VQpif
    private static readonly Regex ViewPostIdRx = new(@"/view/d/[^/]+/([A-Za-z0-9]{10,})", RegexOptions.Compiled);
    // "$1,250" anywhere in a title or body. The thousands-separated form is matched first and
    // requires an actual comma group, so a plain "$1250.50" falls through to the second branch and
    // is read whole — first-three-digits-then-stop would silently price a $1,250 miner at $125.
    private static readonly Regex PriceRx = new(@"\$\s?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)", RegexOptions.Compiled);
    // Craigslist appends the neighbourhood the poster typed, in parentheses, at the end of a title.
    private static readonly Regex TrailingPlaceRx = new(@"\s*\(([^()]{1,60})\)\s*$", RegexOptions.Compiled);
    // "... - $1,250" — the price craigslist splices into a feed title.
    private static readonly Regex TrailingPriceRx = new(@"\s*[-–]\s*\$\s?[\d,.]+\s*$", RegexOptions.Compiled);
    private static readonly Regex ImgSrcRx = new(@"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TagRx = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// Builds a craigslist search URL. <c>postal</c> + <c>search_distance</c> are craigslist's own
    /// radius filter, applied server-side, so the site only has to be the right metro — see
    /// CraigslistSites. Sorted newest-first: a sourcing search wants what appeared since the seller
    /// last looked, not the same aged posts every time.
    /// </summary>
    public static string BuildSearchUrl(string site, string query, string zip, int radiusMiles, int offset = 0, bool rss = true)
    {
        var url = $"https://{site}.craigslist.org/search/sss?query={Uri.EscapeDataString(query ?? "")}&sort=date";

        // Radius is only meaningful with a postal code to measure from; craigslist ignores one
        // without the other, so both are sent or neither is.
        var zipDigits = new string((zip ?? "").Where(char.IsDigit).ToArray());
        if (zipDigits.Length >= 5)
            url += $"&postal={zipDigits[..5]}&search_distance={Math.Clamp(radiusMiles, 1, 500)}";

        if (offset > 0) url += $"&s={offset}";
        if (rss) url += "&format=rss";
        return url;
    }

    /// <summary>
    /// Parses a craigslist RSS/RDF feed. Returns an empty list rather than throwing on malformed
    /// XML — a feed that changed shape must degrade into "try the HTML page", not into a 500.
    /// </summary>
    public static List<LocalSupplyListing> ParseRss(string xml)
    {
        var listings = new List<LocalSupplyListing>();
        if (string.IsNullOrWhiteSpace(xml)) return listings;

        XDocument doc;
        try
        {
            // DTD processing off and no external resolver: this parses a document fetched over the
            // network, and an external entity is the one thing an XML parser must never fetch.
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return listings; }

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var listing = ParseRssItem(item);
            if (listing is not null) listings.Add(listing);
        }

        return listings;
    }

    public static LocalSupplyListing? ParseRssItem(XElement item)
    {
        string Child(string name) => item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

        // RSS 2.0 puts the URL in <link>; RDF also carries it as the item's rdf:about attribute,
        // which survives feeds where <link> is namespaced oddly.
        var url = Child("link");
        if (url.Length == 0)
            url = item.Attributes().FirstOrDefault(a => a.Name.LocalName == "about")?.Value ?? "";
        if (url.Length == 0) return null;

        var rawTitle = WebUtilityDecode(Child("title"));
        if (rawTitle.Length == 0) return null;

        var description = Child("description");
        if (description.Length == 0) description = Child("encoded");

        var listing = new LocalSupplyListing
        {
            Source = SourceId,
            SourceLabel = SourceLabel,
            ItemId = PostIdOf(url),
            Url = url,
            ImageUrl = ImageOf(item, description),
        };

        // Price: the feed title first ("Antminer S19j Pro - $2,500"), then the body. Anything found
        // in the body is a weaker signal — a body can quote a retail price or a second item — so
        // the title always wins when both have one.
        var priced = ExtractPrice(rawTitle);
        if (priced.Price is null) priced = ExtractPrice(StripHtml(description));
        listing.Price = priced.Price;
        listing.PriceText = priced.Text;
        listing.IsFree = priced.Price is null && LooksFree(rawTitle);

        var (title, place) = CleanTitle(rawTitle);
        listing.Title = title;
        listing.Location = place;

        var date = Child("date");
        if (date.Length == 0) date = Child("pubDate");
        if (DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var posted))
        {
            listing.PostedUtc = posted.UtcDateTime;
            listing.PostedAgo = RelativeAge(posted.UtcDateTime, DateTime.UtcNow);
        }

        if (listing.Title.Length == 0) return null;
        // Same rule as the Facebook parser: no price and not free means no cost basis, and a
        // sourcing row without a cost basis has nothing to say.
        if (listing.Price is null && !listing.IsFree) return null;

        return listing;
    }

    /// <summary>
    /// Parses the results list craigslist renders server-side for clients that don't run
    /// JavaScript. This is the main path: it's complete, it needs no browser, and it honours the
    /// postal/distance filter. A post here carries a title, a price and often a location — no
    /// timestamp and no thumbnail, which the RSS path has and this one doesn't.
    /// </summary>
    public static List<LocalSupplyListing> ParseStaticHtml(string html)
    {
        var listings = new List<LocalSupplyListing>();
        if (string.IsNullOrWhiteSpace(html)) return listings;

        // One result block per <a class="cl-app-anchor ..."> / <li class="cl-static-search-result">.
        // Matched with a regex rather than an HTML parser because this is the fallback path for a
        // fallback page — taking a parser dependency to read four fields isn't worth it.
        var blocks = Regex.Matches(html,
            @"<li[^>]*class=""[^""]*cl-static-search-result[^""]*""[^>]*>(.*?)</li>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match block in blocks)
        {
            var inner = block.Groups[1].Value;
            var href = Regex.Match(inner, @"href=""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value;
            if (href.Length == 0) continue;

            var title = WebUtilityDecode(StripHtml(
                Regex.Match(inner, @"<div[^>]*class=""[^""]*title[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value));
            if (title.Length == 0)
                title = WebUtilityDecode(Regex.Match(block.Value, @"title=""([^""]+)""", RegexOptions.IgnoreCase).Groups[1].Value);
            if (title.Length == 0) continue;

            var priceText = StripHtml(
                Regex.Match(inner, @"<div[^>]*class=""[^""]*price[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
            var location = WebUtilityDecode(StripHtml(
                Regex.Match(inner, @"<div[^>]*class=""[^""]*location[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value)).Trim();

            var (price, text) = ExtractPrice(priceText);
            var (cleanTitle, place) = CleanTitle(title);

            var listing = new LocalSupplyListing
            {
                Source = SourceId,
                SourceLabel = SourceLabel,
                ItemId = PostIdOf(href),
                Url = href,
                Title = cleanTitle,
                Price = price,
                PriceText = text,
                IsFree = price is null && LooksFree(title),
                Location = location.Length > 0 ? location : place,
            };

            if (listing.Title.Length == 0) continue;
            if (listing.Price is null && !listing.IsFree) continue;
            listings.Add(listing);
        }

        return listings;
    }

    /// <summary>
    /// Turns parsed posts into a search result: duplicates dropped (craigslist repeats posts
    /// across feed pages), padding filtered out, ask spread summarised.
    /// </summary>
    public static LocalSupplySearchResult BuildResult(
        IEnumerable<LocalSupplyListing> listings, CraigslistSite site, string query, string zip, int radiusMiles)
    {
        var items = LocalSupplyResults.Dedupe(listings);
        items = LocalSupplyResults.FilterByRelevance(items, query);
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId = SourceId,
            SourceLabel = SourceLabel,
            Status = "ok",
            Query = query,
            ZipCode = zip,
            RadiusMiles = radiusMiles,
            SearchUrl = BuildSearchUrl(site.Id, query, zip, radiusMiles, rss: false),
            ScopeLabel = $"{site.Label} craigslist ({site.State})",
            Items = items,
            Min = min, Median = median, Max = max,
        };
    }

    // ── Small pure helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Strips the price and the neighbourhood craigslist splices onto a feed title, because both
    /// are noise to the comp matcher: "Antminer S19j Pro 104TH - $2,500 (Henderson)" has to reach
    /// ProductNormalizer as "Antminer S19j Pro 104TH" or the $2,500 becomes part of the product.
    /// </summary>
    public static (string Title, string Place) CleanTitle(string rawTitle)
    {
        var title = (rawTitle ?? "").Trim();
        var place = "";

        var placeMatch = TrailingPlaceRx.Match(title);
        if (placeMatch.Success)
        {
            place = placeMatch.Groups[1].Value.Trim();
            title = TrailingPlaceRx.Replace(title, "").Trim();
        }

        title = TrailingPriceRx.Replace(title, "").Trim();
        return (title, place);
    }

    /// <summary>
    /// The asking price, or null when there isn't one.
    ///
    /// A rendered <c>$0</c> is treated as "no price stated", not as a free item: craigslist prints
    /// $0 for every post whose seller left the price field empty, and they are common. Taking that
    /// at face value would give a whole class of posts a $0 cost basis — which is exactly what
    /// turns "seller didn't say" into an unbounded-ROI goldmine at the top of the ranking.
    /// Genuinely free items are recognised from the wording instead (see <see cref="LooksFree"/>).
    /// </summary>
    public static (decimal? Price, string Text) ExtractPrice(string? text)
    {
        var match = PriceRx.Match(text ?? "");
        if (!match.Success) return (null, "");
        return decimal.TryParse(match.Groups[1].Value.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var price)
               && price > 0m
            ? (price, match.Value.Trim())
            : (null, "");
    }

    // Craigslist has a whole "free" category and sellers say so in the title; a free item is the
    // best possible cost basis for a flip, not a missing price.
    public static bool LooksFree(string? title) =>
        Regex.IsMatch(title ?? "", @"\bfree\b", RegexOptions.IgnoreCase);

    /// <summary>
    /// The post's own id. Craigslist has two permalink shapes live at once: the classic
    /// <c>.../7712345678.html</c> and the current <c>craigslist.org/view/d/&lt;slug&gt;/&lt;id&gt;</c>
    /// the static results list emits. Both are handled, and anything else falls back to the URL —
    /// which is still unique, so an unrecognised shape costs a tidy id, never a dropped listing.
    /// </summary>
    public static string PostIdOf(string url)
    {
        var classic = PostIdRx.Match(url ?? "");
        if (classic.Success) return classic.Groups[1].Value;

        var modern = ViewPostIdRx.Match(url ?? "");
        if (modern.Success) return modern.Groups[1].Value;

        return (url ?? "").Trim();
    }

    private static string ImageOf(XElement item, string description)
    {
        // RDF feeds carry <enc:enclosure resource="..."/>; RSS 2.0 uses <enclosure url="..."/>.
        var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure");
        var fromEnclosure = enclosure?.Attributes()
            .FirstOrDefault(a => a.Name.LocalName is "resource" or "url")?.Value;
        if (!string.IsNullOrWhiteSpace(fromEnclosure)) return fromEnclosure;

        var img = ImgSrcRx.Match(description ?? "");
        return img.Success ? img.Groups[1].Value : "";
    }

    public static string RelativeAge(DateTime postedUtc, DateTime nowUtc)
    {
        var age = nowUtc - postedUtc;
        if (age < TimeSpan.Zero) return "just posted";
        if (age.TotalMinutes < 60) return $"{Math.Max(1, (int)age.TotalMinutes)} min ago";
        if (age.TotalHours < 24) return $"{(int)age.TotalHours} hour{((int)age.TotalHours == 1 ? "" : "s")} ago";
        return $"{(int)age.TotalDays} day{((int)age.TotalDays == 1 ? "" : "s")} ago";
    }

    private static string StripHtml(string? html) => TagRx.Replace(html ?? "", " ").Trim();

    // &nbsp; decodes to U+00A0, which survives Trim() and every word comparison downstream as a
    // character that merely looks like a space — so it becomes a real one here, once.
    private static string WebUtilityDecode(string? text) =>
        System.Net.WebUtility.HtmlDecode(text ?? "").Replace(' ', ' ').Trim();
}
