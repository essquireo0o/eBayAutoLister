using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a deal aggregator's RSS into <see cref="LocalSupplyListing"/> rows the arbitrage pipeline
/// already knows how to price. Pure — no HTTP, no clock beyond the one passed in — so every rule
/// below is a unit test rather than a guess about a live feed.
///
/// Three feeds, three dialects, one reader: fields are matched by element LOCAL name, so DealNews'
/// <c>dealnews:price</c>, TechBargains' <c>vendorname</c> and Slickdeals' <c>content:encoded</c> are
/// all just names, and a fourth aggregator is a URL in <see cref="DealFeedCatalog"/> rather than a
/// new code path.
///
/// The parser's real job is refusal. A deal feed is advertising: most entries are not one buyable
/// object, and most dollar figures in them are not prices. Everything that can't be read as
/// "this specific item, for this specific amount" is dropped, because the alternative — a guessed
/// cost basis — reaches the seller as a ranked, badged, confident number that is simply false.
/// </summary>
public static class DealFeedParser
{
    /// <summary>
    /// Parses one feed. Returns an empty list rather than throwing on malformed XML: a feed that
    /// changed shape has to degrade into "this one found nothing" beside the feeds that worked,
    /// never into a failed scan.
    /// </summary>
    /// <param name="requirePrice">
    /// True everywhere except the freebie finder. A deal with no readable price has no cost basis
    /// and is dropped — unless the whole point of the scan is that the item is free, where a missing
    /// price is the normal case rather than a parse failure (see <see cref="FreebieSourceService"/>,
    /// which decides for itself whether "no price" means free).
    /// </param>
    public static List<LocalSupplyListing> ParseFeed(
        string? xml, DealFeed feed, DateTime? nowUtc = null, bool requirePrice = true)
    {
        var listings = new List<LocalSupplyListing>();
        if (string.IsNullOrWhiteSpace(xml)) return listings;

        XDocument doc;
        try
        {
            // DTD processing off and no resolver: this parses a document fetched over the network,
            // and an external entity is the one thing an XML parser must never go and fetch.
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return listings; }

        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var listing = ParseItem(item, feed, nowUtc ?? DateTime.UtcNow, requirePrice);
            if (listing is not null) listings.Add(listing);
        }

        return listings;
    }

    /// <summary>
    /// One entry, or null when it isn't a priced, buyable product. Null is the common case and the
    /// correct one — see the class remarks.
    /// </summary>
    public static LocalSupplyListing? ParseItem(
        XElement item, DealFeed feed, DateTime nowUtc, bool requirePrice = true)
    {
        string Child(string name) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

        var url = Child("link");
        if (url.Length == 0) url = Child("guid");
        if (url.Length == 0) return null;

        var rawTitle = Decode(Child("title"));
        if (rawTitle.Length == 0) return null;

        // Slickdeals puts the useful body in content:encoded and a truncated copy in description;
        // DealNews and TechBargains use description alone. Both are read — the store name, the
        // coupon code and the "was" price live in whichever one the site chose to fill in.
        var body = Child("encoded");
        var description = Child("description");
        var prose = StripHtml($"{Decode(description)} {Decode(body)}");

        // DealNews says outright whether an entry is one item or a whole category sale. Trusting
        // that beats inferring it from the wording, and it is the cheapest junk filter available.
        var dealType = Child("dealType");
        if (dealType.Equals("sale", StringComparison.OrdinalIgnoreCase)) return null;

        if (IsNotAProduct(rawTitle) || IsNotAProduct(FirstSentence(prose))) return null;

        var (price, originalPrice, priceText) = ReadPrice(item, rawTitle, prose);
        if (price is null && requirePrice) return null;

        var retailer = ReadRetailer(item, description, body);

        var listing = new LocalSupplyListing
        {
            Source = DealFeedCatalog.SourceId,
            // The badge names the aggregator, not the umbrella source: "found it on Slickdeals" is
            // what the seller needs in order to go and check it.
            SourceLabel = feed.Site,
            ItemId = ItemIdOf(feed, item, url),
            Url = url,
            Title = CleanTitle(rawTitle),
            ImageUrl = ImageOf(item, description, body),
            Price = price,
            PriceText = priceText,
            OriginalPrice = originalPrice,
            // Retail stock is not a place you drive to. The retailer goes in the location slot
            // because "where do I go buy this" is the question that field answers on every board.
            Location = retailer.Length > 0 ? retailer : "Online",
            // Nationwide by definition. Left null rather than zeroed: zero miles would read as
            // "it's right here", and every distance sort in the app treats null as unknown.
            DistanceMiles = null,
            // What makes this row cost differently from a cash pickup — see RetailBuyCosts.
            IsRetail = true,
            Retailer = retailer,
            FreeShipping = DealFeedSelectors.FreeShipping.IsMatch($"{rawTitle} {prose}"),
            CouponCode = ReadCouponCode(body, description, rawTitle, prose),
        };

        if (listing.Title.Length == 0) return null;

        var posted = ReadDate(Child("pubDate"), Child("date"));
        if (posted is not null)
        {
            listing.PostedUtc = posted;
            listing.PostedAgo = CraigslistParser.RelativeAge(posted.Value, nowUtc);
        }

        return listing;
    }

    // ── Price ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the thing costs, and what it used to cost.
    ///
    /// Order matters and is not arbitrary. A feed that publishes a structured price (DealNews) is
    /// believed outright — it is the site's own machine-readable answer, and no amount of title
    /// parsing beats it. Only when there isn't one does this read the title, and then the body.
    /// A DealNews price of <c>0.00</c> is treated as "not stated" rather than free, exactly as
    /// Craigslist's rendered <c>$0</c> is: it is what the site prints for "free with purchase" and
    /// for sale roundups, and taking it literally would hand a whole class of entries a $0 cost
    /// basis at the top of a profit ranking.
    /// </summary>
    public static (decimal? Price, decimal? OriginalPrice, string Text) ReadPrice(
        XElement item, string title, string prose)
    {
        var structured = ParseDecimal(item.Elements().FirstOrDefault(e => e.Name.LocalName == "price")?.Value);
        var (titlePrice, titleWas) = ScanPrices(title);
        var (prosePrice, proseWas) = ScanPrices(FirstSentence(prose));

        var price = IsCredible(structured) ? structured : titlePrice ?? prosePrice;
        if (!IsCredible(price)) return (null, null, "");

        // The struck-through price only means something above the price being paid; below it, the
        // parse picked up an unrelated figure and the claim "it was cheaper before" is worth
        // dropping rather than showing.
        var was = titleWas ?? proseWas;
        if (was is not null && was <= price) was = null;

        return (price, was, $"${price:0.##}");
    }

    /// <summary>
    /// Reads every dollar figure in a piece of text and decides what each one is.
    ///
    /// The current price is the LAST unqualified figure, because that is how these titles are
    /// written — the product, then the price, then the shipping promise ("Nautica Admiral Backpack
    /// for $36 + free shipping"). Anything a cue word disqualifies is skipped entirely, and
    /// anything a "was"-word introduces is captured as the old price instead.
    /// </summary>
    public static (decimal? Price, decimal? WasPrice) ScanPrices(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null);

        decimal? price = null, was = null;

        foreach (Match match in DealFeedSelectors.PriceToken.Matches(text))
        {
            var value = ParseDecimal(match.Groups[1].Value);
            if (!IsCredible(value)) continue;

            var before = text[..match.Index];
            var after = text[(match.Index + match.Length)..];

            if (DealFeedSelectors.NotAPriceAfter.IsMatch(after)) continue;

            if (DealFeedSelectors.WasPriceBefore.IsMatch(before)) { was = value; continue; }
            if (DealFeedSelectors.NotAPriceBefore.IsMatch(before)) continue;

            price = value;
        }

        return (price, was);
    }

    private static bool IsCredible(decimal? value) =>
        value is >= DealFeedSelectors.MinCrediblePrice and <= DealFeedSelectors.MaxCrediblePrice;

    private static decimal? ParseDecimal(string? raw) =>
        decimal.TryParse((raw ?? "").Replace(",", "").Replace("$", "").Trim(),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    // ── What kind of thing this is ───────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the entry is an offer rather than an object: a gift card, a subscription, a
    /// category sale, a spend-and-get promotion. None of these has a resale comp, so none of them
    /// belongs in a profit ranking at any price.
    /// </summary>
    public static bool IsNotAProduct(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        return DealFeedSelectors.NotAProductPhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase))
            || DealFeedSelectors.CategorySaleHeadline.IsMatch(text);
    }

    /// <summary>
    /// Does this deal answer what the seller actually asked for?
    ///
    /// Every real word in the query has to appear. That is far stricter than
    /// <see cref="LocalSupplyResults.FilterByRelevance"/>, which keeps everything when nothing
    /// matches — the right call for a site that already ran the search, and the wrong one here,
    /// where the input is six hundred unrelated deals and "no matches" is the honest answer.
    /// Retailer and category count as haystack: a Best Buy entry titled "Open-Box Monitors" is a
    /// match for "monitor" even when the title alone is thin.
    /// </summary>
    public static bool MatchesQuery(LocalSupplyListing listing, string? query)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0) return true;

        var haystack = $"{listing.Title} {listing.Retailer} {listing.Location}".ToLowerInvariant();
        return tokens.All(haystack.Contains);
    }

    private static List<string> Tokens(string? query) =>
        DealFeedSelectors.NonWord.Split((query ?? "").ToLowerInvariant())
            .Where(t => t.Length >= 3)
            .Distinct()
            .ToList();

    // ── Small readers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The store the deal is at. DealNews and TechBargains publish it as a field; Slickdeals writes
    /// it into the body as "Amazon [amazon.com] has …", with a machine-readable slug in the markup
    /// as a fallback.
    /// </summary>
    public static string ReadRetailer(XElement item, string? description, string? body)
    {
        string Child(string name) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

        var declared = Child("retailer");
        if (declared.Length == 0) declared = Child("vendorname");
        if (declared.Length > 0) return Decode(declared);

        var lead = DealFeedSelectors.StoreLead.Match(StripHtml(Decode(description)));
        if (!lead.Success) lead = DealFeedSelectors.StoreLead.Match(StripHtml(Decode(body)));
        if (lead.Success) return lead.Groups[1].Value.Trim();

        var slug = DealFeedSelectors.StoreSlug.Match(body ?? "");
        if (!slug.Success) slug = DealFeedSelectors.StoreSlug.Match(description ?? "");
        return slug.Success ? Titleize(slug.Groups[1].Value) : "";
    }

    /// <summary>
    /// The code the seller has to type for the price to be the price. Shown on the row because a
    /// price that only exists with a code, presented without it, is a price they can't actually get.
    /// </summary>
    public static string ReadCouponCode(params string?[] sources)
    {
        foreach (var text in sources)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            var markup = DealFeedSelectors.CouponCodeMarkup.Match(text);
            if (markup.Success) return markup.Groups[1].Value.Trim();

            var prose = DealFeedSelectors.CouponCodeProse.Match(text);
            if (!prose.Success) continue;

            // Codes are shouted: all-caps, or letters mixed with digits. Without that test the
            // prose pattern picks the next word out of a sentence like "code named Bluebird", and a
            // wrong code on screen is worse than no code — it sends the seller to a checkout that
            // rejects it and leaves them thinking the price was a lie.
            var code = prose.Groups[1].Value.Trim();
            if (code.Any(char.IsDigit) || code.All(c => !char.IsLower(c))) return code;
        }

        return "";
    }

    /// <summary>
    /// Strips the price and the shipping promise off the end of a deal title, because both are
    /// noise to the comp matcher: "Sony WH-1000XM5 Headphones for $248 + free shipping" has to
    /// reach ProductNormalizer as "Sony WH-1000XM5 Headphones" or the 248 becomes part of the
    /// product identity. Same job CraigslistParser.CleanTitle does for a neighbourhood suffix.
    /// </summary>
    public static string CleanTitle(string? rawTitle)
    {
        var title = Decode(rawTitle).Trim();

        // Front of the title: the thread tag, then a price the poster led with.
        title = DealFeedSelectors.LeadingTag.Replace(title, "");
        title = DealFeedSelectors.LeadingPricePrefix.Replace(title, "");
        title = DealFeedSelectors.LeadingPrice.Replace(title, "").Trim();

        // Back of it, twice, in this order: the store trails the price as often as it precedes it,
        // and each strip is what exposes the next one as the trailing token.
        for (var pass = 0; pass < 2; pass++)
        {
            title = DealFeedSelectors.TrailingOfferTail.Replace(title, "").Trim();
            title = DealFeedSelectors.TrailingDomain.Replace(title, "").Trim();
            title = DealFeedSelectors.TrailingPriceTail.Replace(title, "").Trim();
            title = DealFeedSelectors.TrailingStore.Replace(title, "").Trim();
        }

        return title.Trim(' ', ',', '-', '–', '—', '|', ':').Trim();
    }

    /// <summary>
    /// Unique per feed. The feed id is part of the key because two aggregators use the same
    /// destination URL for the same Amazon deal, and dedupe keys on (Source, ItemId) — without the
    /// prefix, DealNews' copy would silently suppress Slickdeals' better-described one.
    /// </summary>
    public static string ItemIdOf(DealFeed feed, XElement item, string url)
    {
        var guid = item.Elements().FirstOrDefault(e => e.Name.LocalName == "guid")?.Value?.Trim() ?? "";
        var basis = guid.Length > 0 ? guid : url;

        // Slickdeals: /f/19811007-slug. DealNews: /21933285.html. Both give a short stable id;
        // anything else falls back to the URL, which is still unique.
        var numeric = Regex.Match(basis, @"/(?:f/)?(\d{6,})(?:[-.]|$)");
        var id = numeric.Success ? numeric.Groups[1].Value : StripTracking(basis);

        return $"{feed.Id}:{id}";
    }

    // Query strings on these links are affiliate and session junk that differs between two fetches
    // of the same deal, which would defeat the dedupe the id exists for.
    private static string StripTracking(string url)
    {
        var q = url.IndexOf('?');
        return (q > 0 ? url[..q] : url).Trim();
    }

    private static string ImageOf(XElement item, string? description, string? body)
    {
        // media:content is the richest and the most common; TechBargains publishes <imagelink>;
        // Slickdeals only ever embeds an <img> in its body.
        var media = item.Elements().FirstOrDefault(e => e.Name.LocalName == "content")
            ?.Attributes().FirstOrDefault(a => a.Name.LocalName == "url")?.Value;
        if (!string.IsNullOrWhiteSpace(media)) return media;

        var enclosure = item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure")
            ?.Attributes().FirstOrDefault(a => a.Name.LocalName is "url" or "resource")?.Value;
        if (!string.IsNullOrWhiteSpace(enclosure)) return enclosure;

        var link = item.Elements().FirstOrDefault(e => e.Name.LocalName == "imagelink")?.Value?.Trim();
        if (!string.IsNullOrWhiteSpace(link)) return link;

        var img = DealFeedSelectors.ImgSrc.Match(Decode(body));
        if (!img.Success) img = DealFeedSelectors.ImgSrc.Match(Decode(description));
        return img.Success ? img.Groups[1].Value : "";
    }

    private static DateTime? ReadDate(params string[] candidates)
    {
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed.UtcDateTime;
        }

        return null;
    }

    // ── Result building ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Drops the same deal appearing twice under different words.
    ///
    /// These aggregators repost each other constantly, and the id-based dedupe every source gets
    /// can't see it: the same Amazon price is a Slickdeals thread, a DealNews page and a direct
    /// Amazon link, with three different ids. Two entries at the same price whose titles normalize
    /// to the same words are the same deal, and showing it three times would triple its weight in a
    /// ranking that is supposed to be one row per thing to buy.
    /// </summary>
    public static List<LocalSupplyListing> DedupeAcrossFeeds(IEnumerable<LocalSupplyListing> listings)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<LocalSupplyListing>();

        foreach (var listing in listings)
        {
            var words = string.Join(' ', DealFeedSelectors.NonWord
                .Split(listing.Title.ToLowerInvariant())
                .Where(t => t.Length > 0)
                .OrderBy(t => t, StringComparer.Ordinal));

            if (seen.Add($"{words}|{listing.Price:0.00}")) kept.Add(listing);
        }

        return kept;
    }

    /// <summary>
    /// Assembles the search result: the query filter applied only where the site didn't search for
    /// us, duplicates dropped both ways, and the price spread summarised the same way every other
    /// source summarises it.
    /// </summary>
    public static LocalSupplySearchResult BuildResult(
        IEnumerable<(DealFeed Feed, List<LocalSupplyListing> Listings)> perFeed, string query)
    {
        // A feed that ran the query gets the treatment Craigslist gets — its own search stands, and
        // LocalSupplyResults.FilterByRelevance only prunes the padding it added, falling back to
        // everything rather than reporting a false "nothing on sale". A firehose gets the strict
        // filter, because there the input is six hundred deals nobody asked about.
        var searched = perFeed.Where(f => f.Feed.IsKeywordSearch).SelectMany(f => f.Listings).ToList();
        var browsed = perFeed.Where(f => !f.Feed.IsKeywordSearch)
            .SelectMany(f => f.Listings.Where(l => MatchesQuery(l, query)));

        var matched = LocalSupplyResults.FilterByRelevance(searched, query).Concat(browsed).ToList();

        var items = DedupeAcrossFeeds(LocalSupplyResults.Dedupe(matched));
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId = DealFeedCatalog.SourceId,
            SourceLabel = DealFeedCatalog.SourceLabel,
            Status = "ok",
            Query = query,
            SearchUrl = DealFeedCatalog.SearchPageUrl(query),
            ScopeLabel = DealFeedCatalog.ScopeLabel,
            Items = items,
            Min = min, Median = median, Max = max,
        };
    }

    /// <summary>
    /// A 200 that isn't an answer. Same rule and same reason as CraigslistParser.DetectBlock: only
    /// the head of the document is scanned, because a real feed is hundreds of kilobytes of deals
    /// and somebody's listing for a door lock genuinely does say "access denied".
    /// </summary>
    public static string? DetectBlock(string? body, string site)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"{site} sent back an empty feed — try again in a moment.";

        var head = body.Length > DealFeedSelectors.BlockScanChars
            ? body[..DealFeedSelectors.BlockScanChars]
            : body;

        return DealFeedSelectors.BlockPhrases.Any(p => head.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? $"{site} is blocking this connection right now — wait a minute and scan again."
            : null;
    }

    // ── Text helpers ─────────────────────────────────────────────────────────────────────────────

    // The store, the item and the price are all in the opening line of these bodies; everything
    // after it is specs and forum chatter, and scanning that for prices is how a $12 accessory
    // mentioned in passing becomes the cost basis for a $900 laptop.
    //
    // Cut on the line break or on length, never on a full stop — a price is written "$56.96", so
    // splitting at the first "." reads it as $56 and understates the buy by every cent after the
    // decimal point.
    private const int OpeningLineChars = 220;

    private static string FirstSentence(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return "";

        var stop = trimmed.IndexOfAny(['\n', '\r']);
        if (stop > 20) trimmed = trimmed[..stop];

        return trimmed.Length > OpeningLineChars ? trimmed[..OpeningLineChars] : trimmed;
    }

    private static string StripHtml(string? html) =>
        DealFeedSelectors.HtmlTag.Replace(html ?? "", " ").Replace("  ", " ").Trim();

    // &nbsp; decodes to U+00A0, which survives Trim() and every word comparison downstream as a
    // character that merely looks like a space — so it becomes a real one here, once.
    private static string Decode(string? text) =>
        System.Net.WebUtility.HtmlDecode(text ?? "").Replace(' ', ' ').Trim();

    private static string Titleize(string slug) =>
        string.Join(' ', slug.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
}
