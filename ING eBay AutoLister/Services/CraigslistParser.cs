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

    /// <summary>Craigslist's "for sale" board — everything with a price on it, and the default.</summary>
    public const string ForSaleCategory = "sss";

    /// <summary>
    /// Builds a craigslist search URL. <c>postal</c> + <c>search_distance</c> are craigslist's own
    /// radius filter, applied server-side, so the site only has to be the right metro — see
    /// CraigslistSites. Sorted newest-first: a sourcing search wants what appeared since the seller
    /// last looked, not the same aged posts every time.
    /// </summary>
    /// <param name="category">
    /// The board to search. Defaults to <see cref="ForSaleCategory"/>, which is what every caller
    /// wanted until the freebie finder needed craigslist's free-stuff board — see
    /// <see cref="FreebieCatalog.CraigslistFreeCategory"/>. A category code, never user input: it
    /// goes into the path, and craigslist's own vocabulary is the only thing that belongs there.
    /// </param>
    public static string BuildSearchUrl(
        string site, string query, string zip, int radiusMiles, int offset = 0, bool rss = true,
        string category = ForSaleCategory)
    {
        // Anything that isn't one of craigslist's own short lowercase codes is not a category, and
        // is certainly not something to splice into a URL path.
        if (string.IsNullOrWhiteSpace(category) || !category.All(char.IsAsciiLetterLower))
            category = ForSaleCategory;

        var url = $"https://{site}.craigslist.org/search/{category}?query={Uri.EscapeDataString(query ?? "")}&sort=date";

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
    /// <param name="freeBoard">
    /// True when every post in this feed is free by construction (craigslist's free-stuff
    /// category). A post with no price is then free rather than unpriceable — see
    /// <see cref="ParseRssItem"/>.
    /// </param>
    public static List<LocalSupplyListing> ParseRss(string xml, bool freeBoard = false)
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
            var listing = ParseRssItem(item, freeBoard);
            if (listing is not null) listings.Add(listing);
        }

        return listings;
    }

    public static LocalSupplyListing? ParseRssItem(XElement item, bool freeBoard = false)
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
        // On the free board a missing price means free, not unreadable: most of those posts never
        // say the word at all, and requiring them to would throw away the best supply on the board.
        listing.IsFree = priced.Price is null && (freeBoard || LooksFree(rawTitle));

        var (title, place) = CleanTitle(rawTitle);
        listing.Title = title;
        listing.Location = place;

        // The post's own body. Kept because it is where people say the two things a title never has
        // room for and a buyer most needs: what condition it is in, and whether it is still under
        // warranty. Truncated on the way in — see LocalSupplyListing.DetailText.
        listing.DetailText = Truncate(WebUtilityDecode(StripHtml(description)));

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
    /// <param name="freeBoard">See <see cref="ParseRss"/> — a missing price is "free", not "unreadable".</param>
    public static List<LocalSupplyListing> ParseStaticHtml(string html, bool freeBoard = false)
    {
        var listings = new List<LocalSupplyListing>();
        if (string.IsNullOrWhiteSpace(html)) return listings;

        // One result block per <a class="cl-app-anchor ..."> / <li class="cl-static-search-result">.
        // Matched with a regex rather than an HTML parser because this is the fallback path for a
        // fallback page — taking a parser dependency to read four fields isn't worth it.
        // Thumbnails come from elsewhere in this same response — see ThumbnailsByTitle. The static
        // list carries no <img> at all (measured: 0 image tags across 322 rows), which is why every
        // Craigslist row used to render the empty box.
        var thumbs = ThumbnailsByTitle(html);

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
                IsFree = price is null && (freeBoard || LooksFree(title)),
                Location = location.Length > 0 ? location : place,
                ImageUrl = LookupThumbnail(thumbs, title, cleanTitle),
            };

            if (listing.Title.Length == 0) continue;
            if (listing.Price is null && !listing.IsFree) continue;
            listings.Add(listing);
        }

        return listings;
    }

    /// <summary>
    /// Post thumbnails, keyed by post title, read out of the search page this parser already has.
    ///
    /// Craigslist's no-JavaScript results list — the one <see cref="ParseStaticHtml"/> reads — has
    /// no images in it whatsoever: measured live, 0 <c>&lt;img&gt;</c> tags and 0 image URLs across
    /// 322 rows. That is why every Craigslist and free-board row rendered the empty 📦 box.
    ///
    /// The pictures are in the SAME response, in the schema.org JSON-LD block Craigslist emits for
    /// search engines (<c>ld_searchpage_results</c>): 291 of 291 items there carried an
    /// <c>image[]</c>. So this costs no extra request, follows nobody into a post page, and keeps
    /// "one search is one request" exactly as it was — the bytes were already downloaded and
    /// thrown away.
    ///
    /// Keyed by title rather than by position on purpose. The two lists are NOT the same length
    /// (322 static rows against 291 JSON-LD entries on the same page), so joining by index would
    /// slide every row after the first gap onto somebody else's photo — and the wrong picture is
    /// worse than no picture. A title collision at worst shows another listing of the same thing.
    /// </summary>
    private static Dictionary<string, string> ThumbnailsByTitle(string html)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var block = Regex.Match(html,
            @"<script[^>]*id=""ld_searchpage_results""[^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!block.Success) return map;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(block.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("itemListElement", out var list) ||
                list.ValueKind != System.Text.Json.JsonValueKind.Array)
                return map;

            foreach (var element in list.EnumerateArray())
            {
                if (!element.TryGetProperty("item", out var item)) continue;
                if (!item.TryGetProperty("name", out var nameEl)) continue;
                if (!item.TryGetProperty("image", out var images) ||
                    images.ValueKind != System.Text.Json.JsonValueKind.Array) continue;

                var name = nameEl.GetString();
                if (string.IsNullOrWhiteSpace(name)) continue;

                // First photo only. The rest are the same item from other angles, and a sourcing
                // table shows one thumbnail per row.
                var first = images.EnumerateArray().FirstOrDefault().GetString();
                if (string.IsNullOrWhiteSpace(first)) continue;

                // First title wins, matching the dedupe rule used everywhere else.
                map.TryAdd(NormalizeTitleKey(name), first);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Craigslist changed the block's shape. Rows render with the empty box exactly as they
            // did before this existed — a missing thumbnail must never fail a search.
        }

        return map;
    }

    /// <summary>
    /// The raw feed title and the cleaned one are both tried: the JSON-LD carries the post's own
    /// title, which is what the raw one still is before <see cref="CleanTitle"/> strips the
    /// trailing place name off it.
    /// </summary>
    private static string LookupThumbnail(Dictionary<string, string> thumbs, string rawTitle, string cleanTitle)
    {
        if (thumbs.Count == 0) return "";
        if (thumbs.TryGetValue(NormalizeTitleKey(rawTitle), out var byRaw)) return byRaw;
        if (thumbs.TryGetValue(NormalizeTitleKey(cleanTitle), out var byClean)) return byClean;
        return "";
    }

    /// <summary>
    /// Collapses whitespace and drops punctuation so a title that differs only in spacing or in a
    /// stray dash still matches. Deliberately not fuzzy: a near-match here is the wrong photo.
    /// </summary>
    private static string NormalizeTitleKey(string title) =>
        Regex.Replace(WebUtilityDecode(title ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();

    /// <summary>
    /// Turns parsed posts into a search result: duplicates dropped (craigslist repeats posts
    /// across feed pages), padding filtered out, ask spread summarised.
    /// </summary>
    /// <param name="categoryId">
    /// This app's category id for the board that was searched, stamped onto every post. Craigslist
    /// is the one source that can state a category as fact rather than infer it — it searched one
    /// board — and that fact outranks anything a title parser could work out downstream. Empty when
    /// the board doesn't map to a category (the free-stuff board carries anything).
    /// </param>
    public static LocalSupplySearchResult BuildResult(
        IEnumerable<LocalSupplyListing> listings, CraigslistSite site, string query, string zip, int radiusMiles,
        string category = ForSaleCategory, string categoryId = "")
    {
        var items = LocalSupplyResults.Dedupe(listings);
        items = LocalSupplyResults.FilterByRelevance(items, query);
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        if (categoryId.Length > 0)
            foreach (var item in items) item.CategoryId = categoryId;

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId = SourceId,
            SourceLabel = SourceLabel,
            Status = "ok",
            Query = query,
            ZipCode = zip,
            RadiusMiles = radiusMiles,
            SearchUrl = BuildSearchUrl(site.Id, query, zip, radiusMiles, rss: false, category: category),
            ScopeLabel = $"{site.Label} craigslist ({site.State})",
            Items = items,
            Min = min, Median = median, Max = max,
        };
    }

    // ── Blocked / empty responses ─────────────────────────────────────────────

    // Only the top of the document is scanned. A block page says so in its first screenful; a real
    // results page is hundreds of kilobytes of posts, and somebody's ad for a door lock genuinely
    // does say "access denied" — searching the whole body would report a healthy search as blocked.
    private const int BlockScanChars = 4000;

    // Craigslist's own wording first, then the interstitials a network in front of it serves.
    // Each is specific enough that a post title can't trip it inside the scanned window.
    private static readonly string[] BlockPhrases =
    [
        "ip has been automatically blocked",
        "this ip has been blocked",
        "blocked</h1>",
        "error: blocked",
        "attention required",                     // Cloudflare interstitial
        "checking your browser before accessing",
        "verify you are a human",
        "please complete the security check",
        "enable javascript and cookies to continue",
        "too many requests",
    ];

    /// <summary>
    /// Craigslist refuses an address it doesn't like with a 200 and a block page, not with a
    /// status code — so an unchecked "success" parses to zero listings and is reported as an empty
    /// local market. That's the worst possible failure for this feature: it looks like an answer.
    ///
    /// Returns the message to show the seller, or null when the body looks like a real page.
    /// </summary>
    public static string? DetectBlock(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return "Craigslist sent back an empty page — wait a moment and search again.";

        var head = body.Length > BlockScanChars ? body[..BlockScanChars] : body;

        return BlockPhrases.Any(p => head.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? "Craigslist is blocking this connection right now — wait a minute and search again, " +
              "or open the search on craigslist.org directly."
            : null;
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

    /// <summary>
    /// Body text cut to the length anything downstream actually reads — see
    /// <see cref="WarrantySelectors.MaxDetailChars"/>. Whitespace is collapsed first, because an RSS
    /// description is mostly the newlines the poster typed and they would eat the budget.
    /// </summary>
    internal static string Truncate(string? text)
    {
        var tidied = Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        return tidied.Length > WarrantySelectors.MaxDetailChars
            ? tidied[..WarrantySelectors.MaxDetailChars]
            : tidied;
    }

    // &nbsp; decodes to U+00A0, which survives Trim() and every word comparison downstream as a
    // character that merely looks like a space — so it becomes a real one here, once.
    private static string WebUtilityDecode(string? text) =>
        System.Net.WebUtility.HtmlDecode(text ?? "").Replace(' ', ' ').Trim();
}
