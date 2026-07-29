using System.Globalization;
using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Craigslist's own search endpoint — the one craigslist.org itself calls to fill the results grid.
///
/// <b>Why this exists at all: the no-JavaScript page has no photographs in it.</b> Not "sometimes",
/// not "fewer" — none. Measured live on the free-stuff board within 40 miles of 02341: 358 results,
/// 0 <c>&lt;img&gt;</c> tags, 0 occurrences of the string <c>images.craigslist.org</c>. The
/// schema.org block <see cref="CraigslistParser.ParseStaticHtml"/> reads instead
/// (<c>ld_searchpage_results</c>) is emitted on that board too, but empty — a 79-byte document
/// whose <c>itemListElement</c> is <c>[]</c>. So there was never a thumbnail to key, by title or by
/// anything else, and every free-board row rendered the 📦 box no matter how the lookup was written.
/// The RSS feed, which did carry thumbnails, now answers "Your request has been blocked" to any
/// address on any user agent.
///
/// The photos are real and craigslist will hand them over — the site's own grid shows them. This is
/// the request it makes to get them: one GET, returning 360 posts with the image id on 331 of them
/// (91%) on that same free-board search, and 347 of 360 (96%) on the priced board.
///
/// <b>The cost model is unchanged.</b> One search is still one request per source: this REPLACES the
/// results-page GET rather than adding to it, and <see cref="CraigslistParser.ParseStaticHtml"/>
/// stays behind it as the fallback for the day this endpoint moves. Nothing here follows a post into
/// its own page, and nothing pages — a thumbnail is never worth a request per row.
///
/// <b>On reading an endpoint craigslist doesn't document.</b> The response is positional JSON, so
/// the risk worth engineering against is a silent shift in field order rather than an outright
/// failure. Everything that matters is therefore read by its tag code (see <see cref="Tag"/>) or by
/// its shape, not by its index — the one exception is the numeric price, checked below against the
/// display price on 695 live posts across both boards with zero disagreements. A shape this parser
/// doesn't recognise yields zero listings, which is exactly the signal CraigslistService already
/// treats as "fall back to the HTML page".
/// </summary>
public static class CraigslistSearchApi
{
    /// <summary>
    /// Posts per request. Craigslist's own cap — asking for more returns this many anyway — and it
    /// matches what the results page carries (~360), so switching to this endpoint neither widens
    /// nor narrows what a search sees.
    /// </summary>
    public const int BatchSize = 360;

    private const string Endpoint = "https://sapi.craigslist.org/web/v8/postings/search/full";

    // Craigslist serves its photos in several renditions off one id. 600x450 is the one the grid
    // uses and the same rendition the schema.org block names on the boards that populate it, so a
    // row's picture is identical whichever path produced it.
    private const string ImageSuffix = "_600x450.jpg";

    /// <summary>
    /// The field codes craigslist tags each post's variable-length section with. Read by code rather
    /// than by position because that section genuinely varies: a post with no photo omits
    /// <see cref="Images"/> entirely, and 25 of 360 priced posts carried no <see cref="PriceText"/>.
    /// </summary>
    private static class Tag
    {
        public const int Images = 4;
        public const int Slug = 6;
        public const int PriceText = 10;
        public const int PostToken = 13;
    }

    /// <summary>
    /// Builds the search request. Deliberately the same inputs
    /// <see cref="CraigslistParser.BuildSearchUrl"/> takes, so the two paths cannot drift into
    /// searching different things.
    /// </summary>
    /// <param name="category">
    /// A craigslist board code — <c>sss</c> for-sale, <c>zip</c> free-stuff. Validated the same way
    /// and for the same reason as in <see cref="CraigslistParser.BuildSearchUrl"/>: it is craigslist's
    /// own vocabulary, never user input.
    /// </param>
    public static string BuildUrl(string query, string zip, int radiusMiles, string category = CraigslistParser.ForSaleCategory)
    {
        if (string.IsNullOrWhiteSpace(category) || !category.All(char.IsAsciiLetterLower))
            category = CraigslistParser.ForSaleCategory;

        // The leading number is craigslist's area id. It is genuinely inert here — the same search
        // sent with 1, 4 and 40 returned identical result counts — because postal + search_distance
        // below are what actually scope the search. That matters: it means this endpoint needs no
        // site-id-to-area-id table, and CraigslistSites stays the only thing that resolves a zip.
        var url = $"{Endpoint}?batch=1-0-{BatchSize}-0-0&cc=US&lang=en&searchPath={category}" +
                  $"&query={Uri.EscapeDataString(query ?? "")}&sort=date";

        // Both or neither, matching the results-page URL: craigslist ignores a radius with no
        // postal code to measure it from.
        var zipDigits = new string((zip ?? "").Where(char.IsDigit).ToArray());
        if (zipDigits.Length >= 5)
            url += $"&postal={zipDigits[..5]}&search_distance={Math.Clamp(radiusMiles, 1, 500)}";

        return url;
    }

    /// <summary>
    /// Parses one response into the same listings <see cref="CraigslistParser.ParseStaticHtml"/>
    /// produces, so everything downstream — dedupe, relevance, the analyzer, the board — is unaware
    /// this path exists.
    ///
    /// Returns an empty list rather than throwing on anything unexpected. A craigslist that changed
    /// this endpoint has to degrade into "use the HTML page", never into a failed search.
    /// </summary>
    /// <param name="freeBoard">
    /// See <see cref="CraigslistParser.ParseRss"/> — on a board where everything is free by
    /// construction, a post with no price is free rather than unreadable.
    /// </param>
    public static List<LocalSupplyListing> Parse(string json, bool freeBoard = false)
    {
        var listings = new List<LocalSupplyListing>();
        if (string.IsNullOrWhiteSpace(json)) return listings;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return listings;
            if (!data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
                return listings;

            // Craigslist sends each post's town as an index into one shared table rather than as a
            // string per post. Absent on some responses, which costs a location and nothing else.
            var places = PlaceTable(data);

            foreach (var item in items.EnumerateArray())
            {
                var listing = ParseItem(item, places, freeBoard);
                if (listing is not null) listings.Add(listing);
            }
        }
        catch (JsonException)
        {
            // Not JSON, or not this JSON. Same contract as the schema.org reader in
            // CraigslistParser: a search degrades to the other path, never to an error.
        }

        return listings;
    }

    private static LocalSupplyListing? ParseItem(JsonElement item, List<string> places, bool freeBoard)
    {
        if (item.ValueKind != JsonValueKind.Array) return null;

        string token = "", slug = "", priceText = "", imageId = "", title = "", geo = "";
        decimal? numericPrice = null;
        var index = 0;

        foreach (var field in item.EnumerateArray())
        {
            switch (field.ValueKind)
            {
                // The tagged section: [code, value, value...]. Read by code — a post with no photo
                // simply has no Images entry, so counting positions here would misread every one.
                case JsonValueKind.Array:
                    var tag = TagCode(field);
                    var value = TagValue(field);
                    switch (tag)
                    {
                        case Tag.PostToken: token = value; break;
                        case Tag.Slug: slug = value; break;
                        case Tag.PriceText: priceText = value; break;
                        case Tag.Images when imageId.Length == 0: imageId = value; break;
                    }
                    break;

                // Two kinds of string live in the fixed section: the geo blob, which always starts
                // "<place>:<area>~", and the post's title, which is always last. Told apart by shape
                // so neither depends on how many scalars craigslist put in front of them.
                case JsonValueKind.String:
                    var scalar = field.GetString() ?? "";
                    if (IsGeo(scalar)) geo = scalar;
                    else title = scalar;
                    break;

                // The asking price in cents-free whole units, with -1 for "seller left it blank".
                // The one field read positionally, because craigslist gives it no tag: checked
                // against the tagged display price on 695 live posts across the for-sale and
                // free boards, agreeing on all 695 and disagreeing on none.
                case JsonValueKind.Number when index == 3:
                    if (field.TryGetInt64(out var raw) && raw > 0) numericPrice = raw;
                    break;
            }

            index++;
        }

        if (title.Length == 0 || token.Length == 0) return null;

        // The display price is authoritative where craigslist states one, because it is the string
        // the seller sees; the numeric field covers the posts that carry no display price at all
        // (25 of 360 on the priced board). Parsed through the same helper the other two paths use,
        // so "$0" stays "seller didn't say" here exactly as it does there.
        var (price, text) = CraigslistParser.ExtractPrice(priceText);
        if (price is null && numericPrice is > 0m)
        {
            price = numericPrice;
            text = numericPrice.Value.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
        }

        var (cleanTitle, place) = CraigslistParser.CleanTitle(title);
        if (cleanTitle.Length == 0) return null;

        var listing = new LocalSupplyListing
        {
            Source = CraigslistParser.SourceId,
            SourceLabel = CraigslistParser.SourceLabel,
            // The post's own token, which is also the last segment of its permalink — so a post read
            // through this path and the same post read off the HTML page dedupe against each other
            // rather than showing twice.
            ItemId = token,
            Url = PostUrl(slug, token),
            Title = cleanTitle,
            Price = price,
            PriceText = text,
            IsFree = price is null && (freeBoard || CraigslistParser.LooksFree(title)),
            Location = PlaceOf(geo, places) is { Length: > 0 } named ? named : place,
            ImageUrl = ImageUrlOf(imageId),
        };

        // The same bar the other two paths hold: no price and not free is no cost basis, and a
        // sourcing row without one has nothing to say.
        return listing.Price is null && !listing.IsFree ? null : listing;
    }

    /// <summary>
    /// Craigslist's permalink for a post: the slug is cosmetic and the token identifies it. Built
    /// rather than read because the endpoint sends the two halves separately — and built to the
    /// same shape the results page links to, so both paths produce the same URL for the same post.
    /// </summary>
    private static string PostUrl(string slug, string token) =>
        slug.Length > 0
            ? $"https://www.craigslist.org/view/d/{slug}/{token}"
            : $"https://www.craigslist.org/view/d/{token}";

    /// <summary>
    /// The photo. Craigslist sends "&lt;count&gt;:&lt;id&gt;" — the count of pictures on the post,
    /// then the id of the first — and the id plus a rendition suffix is the URL.
    ///
    /// First photo only, matching the schema.org path: the rest are the same item from other angles
    /// and a sourcing table shows one thumbnail per row.
    /// </summary>
    private static string ImageUrlOf(string imageId)
    {
        if (imageId.Length == 0) return "";

        var id = imageId;
        var colon = id.IndexOf(':');
        if (colon >= 0) id = id[(colon + 1)..];

        // An id is craigslist's own alphanumeric-and-underscore token. Anything else is a shape
        // this parser doesn't understand, and a guessed URL would render as a broken image.
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) return "";

        return $"https://images.craigslist.org/{id}{ImageSuffix}";
    }

    // "<areaIndex>:<placeIndex>~<lat>~<lon>" — the only string in a post that looks like this.
    private static bool IsGeo(string text)
    {
        var tilde = text.IndexOf('~');
        if (tilde <= 0) return false;

        var head = text[..tilde];
        var colon = head.IndexOf(':');
        return colon > 0
               && head[..colon].All(char.IsAsciiDigit)
               && head[(colon + 1)..].All(char.IsAsciiDigit);
    }

    /// <summary>
    /// The town craigslist filed the post under, or empty when it named none.
    ///
    /// The index is the number AFTER the colon, not before it. The two are equal often enough to
    /// look interchangeable on a spot check, so this was settled by measurement rather than by
    /// reading: across 120 live posts, the town named by the second index matched the post's own
    /// URL slug 80 times and the one named by the first index matched twice.
    /// </summary>
    private static string PlaceOf(string geo, List<string> places)
    {
        if (geo.Length == 0 || places.Count == 0) return "";

        var tilde = geo.IndexOf('~');
        if (tilde <= 0) return "";

        var colon = geo.LastIndexOf(':', tilde - 1);
        if (colon <= 0) return "";
        if (!int.TryParse(geo[(colon + 1)..tilde], out var slot)) return "";

        // Slot 0 is craigslist's "no town on this post" placeholder — the table's first entry is a
        // literal 0 rather than a name.
        return slot > 0 && slot < places.Count ? places[slot] : "";
    }

    /// <summary>
    /// The shared table of town names. Entry 0 is a placeholder rather than a string, so the list is
    /// built with a blank in that slot to keep craigslist's own indexes valid.
    /// </summary>
    private static List<string> PlaceTable(JsonElement data)
    {
        var places = new List<string>();
        if (!data.TryGetProperty("decode", out var decode) ||
            !decode.TryGetProperty("locationDescriptions", out var descriptions) ||
            descriptions.ValueKind != JsonValueKind.Array)
            return places;

        foreach (var entry in descriptions.EnumerateArray())
            places.Add(entry.ValueKind == JsonValueKind.String ? entry.GetString() ?? "" : "");

        return places;
    }

    private static int TagCode(JsonElement field)
    {
        foreach (var part in field.EnumerateArray())
            return part.ValueKind == JsonValueKind.Number && part.TryGetInt32(out var code) ? code : -1;
        return -1;
    }

    private static string TagValue(JsonElement field)
    {
        var first = true;
        foreach (var part in field.EnumerateArray())
        {
            if (first) { first = false; continue; }
            return part.ValueKind == JsonValueKind.String ? part.GetString() ?? "" : "";
        }
        return "";
    }

    /// <summary>
    /// How many of these listings a seller can actually look at, which is the number worth logging:
    /// a board that comes back with rows but no pictures is a broken board, and it used to say so
    /// nowhere. See CraigslistService, which reports it.
    /// </summary>
    public static int WithPhotos(IEnumerable<LocalSupplyListing> listings) =>
        listings.Count(l => !string.IsNullOrWhiteSpace(l.ImageUrl));
}
