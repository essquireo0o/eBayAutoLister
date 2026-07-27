using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a liquidation-auction search page into <see cref="LocalSupplyListing"/> rows the arbitrage
/// pipeline already knows how to price. Pure — no HTTP, no clock beyond the one passed in — so
/// every rule below is a unit test rather than a guess about a live site.
///
/// <para><b>It reads state, not tiles.</b> HiBid renders its search server-side and ships the data
/// that produced the page along with it, as a JSON island. So this does not match on CSS classes
/// that change with a redesign: it reads named fields — the lot, the current bid, the bid count,
/// the closing time, the auction house, the pickup city, the buyer's premium. That is the only
/// reason a buyer's premium can be charged at all, and the premium is the cost line that decides
/// whether auction arithmetic is honest.</para>
///
/// <para><b>The parser's real job is refusal</b>, exactly as it is in
/// <see cref="DealFeedParser"/> — and here the stakes are higher, because a liquidation row can be
/// eight of something. A wrong unit count does not shade a number, it multiplies it. So a count is
/// used only where the listing stated one, a lot whose contents are "assorted" is not priced at
/// all, and the site's own placeholder bid is refused by name.</para>
/// </summary>
public static class LiquidationParser
{
    /// <summary>The JSON island the search page ships its own data in.</summary>
    private static readonly Regex StateScript = new(
        @"<script[^>]*\bid=""hibid-state""[^>]*>(.*?)</script>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private const string LotBaseUrl = "https://hibid.com/lot/";
    private const string AuctionBaseUrl = "https://hibid.com/auction/";

    // ── The page ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses one search page. Returns an empty list rather than throwing on anything malformed: a
    /// site that changed shape has to degrade into "this slice found nothing" beside the slices that
    /// worked, never into a failed scan.
    /// </summary>
    public static List<LocalSupplyListing> ParsePage(string? html, LiquidationFeed feed, DateTime nowUtc)
    {
        var listings = new List<LocalSupplyListing>();

        var json = ExtractState(html);
        if (json is null) return listings;

        JsonDocument document;
        try { document = JsonDocument.Parse(json); }
        catch (JsonException) { return listings; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("apollo.state", out var state)
                || state.ValueKind != JsonValueKind.Object)
            {
                return listings;
            }

            // The state is a flat store keyed by "Type:id", with cross-references as {"__ref": key}.
            // Indexing it once is what lets a lot resolve its own auction — where the premium, the
            // pickup city and the sale's name live.
            var store = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var entry in state.EnumerateObject()) store[entry.Name] = entry.Value;

            foreach (var entry in store)
            {
                if (Text(entry.Value, "__typename") != "Lot") continue;

                var listing = ParseLot(entry.Value, store, feed, nowUtc);
                if (listing is not null) listings.Add(listing);
            }
        }

        return listings;
    }

    /// <summary>
    /// Pulls the JSON island out of the page and undoes the framework's own escaping.
    /// </summary>
    /// <remarks>
    /// The escaping is a private five-character scheme (<c>&amp;a; &amp;q; &amp;s; &amp;l; &amp;g;</c>),
    /// not HTML entities — running an HTML decoder over it corrupts the document, because a
    /// <c>&amp;quot;</c> that a seller typed inside a lot description is legitimate JSON text and
    /// decoding it produces an unterminated string. Found the hard way. The ampersand is decoded
    /// LAST for the same reason it was encoded first.
    /// </remarks>
    public static string? ExtractState(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var match = StateScript.Match(html);
        if (!match.Success) return null;

        return match.Groups[1].Value
            .Replace("&q;", "\"", StringComparison.Ordinal)
            .Replace("&s;", "'", StringComparison.Ordinal)
            .Replace("&l;", "<", StringComparison.Ordinal)
            .Replace("&g;", ">", StringComparison.Ordinal)
            .Replace("&a;", "&", StringComparison.Ordinal);
    }

    // ── One lot ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One lot, or null when it isn't something a seller could buy and flip. Null is a common and
    /// correct outcome — see the class remarks.
    /// </summary>
    public static LocalSupplyListing? ParseLot(
        JsonElement lot, IReadOnlyDictionary<string, JsonElement> store, LiquidationFeed feed, DateTime nowUtc)
    {
        var id = Text(lot, "id");
        if (id.Length == 0) return null;

        var rawTitle = Text(lot, "lead");
        if (rawTitle.Length == 0) return null;

        var lotState = Child(lot, "lotState");

        // Closed, archived, or bidding that doesn't happen online: all three are lots the seller
        // cannot act on from here, and a ranking full of things you can't buy is worse than a
        // shorter one.
        if (Flag(lotState, "isClosed") || Flag(lotState, "isArchived")) return null;
        if (Text(lotState, "status").Contains("NO_INTERNET_BIDDING", StringComparison.OrdinalIgnoreCase)) return null;

        var (price, isStartingBid) = ReadBid(lotState);
        if (price is null) return null;

        var auction = Ref(lot, "auction", store);
        var description = Text(lot, "description");
        var eventName = Text(auction, "eventName");

        // Everything the wording is judged on, in one string. The auction's own name counts: a lot
        // in a sale called "Customer Returns — Uninspected" is graded by that even when the lot
        // itself says nothing.
        var wording = $"{rawTitle} {description} {eventName}";
        if (IsNotFlippable(wording)) return null;

        var units = ReadUnits(rawTitle, description, IntOf(lot, "quantity"));
        var title = CleanTitle(rawTitle);
        if (title.Length == 0) return null;

        var details = new LiquidationLotDetails
        {
            AuctionHouse = AuctioneerName(auction, store),
            EventName = eventName,
            IsLiquidationEvent = LiquidationSelectors.LiquidationEvent.IsMatch($"{eventName} {rawTitle}"),
            EventUrl = Text(auction, "id") is { Length: > 0 } auctionId ? AuctionBaseUrl + auctionId : "",
            BuyerPremiumPercent = ReadBuyerPremium(auction, out var premiumAssumed),
            BuyerPremiumAssumed = premiumAssumed,
            BidCount = IntOf(lotState, "bidCount"),
            IsStartingBid = isStartingBid,
            TimeLeft = Tidy(Text(lotState, "timeLeft")),
            // Computed from the site's own countdown rather than from its printed close time, which
            // is written in the auction house's local zone with no offset on it. Seconds are exact
            // and have no timezone to get wrong.
            ClosesUtc = Decimal(lotState, "timeLeftSeconds") is { } seconds && seconds > 0m
                ? nowUtc.AddSeconds((double)seconds)
                : null,
            IsLot = units.IsLot,
            Units = units.Count,
            GradeId = units.IsLot ? GradeFor(wording) : "",
            ClaimedRetailTotal = ReadClaimedRetail($"{rawTitle} {description}"),
            UnpriceableReason = units.Reason ?? UnpriceableReason(rawTitle, wording, units),
        };

        return new LocalSupplyListing
        {
            Source = LiquidationCatalog.SourceId,
            // The badge names the aggregator the lot was found on, so "go and check it" means
            // something. The auction house itself is on the row as the place you actually pay.
            SourceLabel = LiquidationCatalog.Site,
            // Deliberately NOT prefixed with the feed slice: the same lot legitimately comes back
            // from the plain search and from the "lot" slice, and it is one thing to buy, not two.
            // Prefixing would defeat LocalSupplyResults.Dedupe, which keys on (Source, ItemId).
            ItemId = id,
            Url = LotBaseUrl + id,
            Title = title,
            ImageUrl = ImageOf(lot),
            Price = price,
            PriceText = isStartingBid ? $"${price:0.##} opening bid" : $"${price:0.##} current bid",
            Location = PlaceOf(auction),
            // Published only when the site computed one against the seller's zip; null is honest and
            // every distance sort in the app already reads it as unknown.
            DistanceMiles = Decimal(lot, "distanceMiles") is { } miles && miles > 0m ? (double)miles : null,
            Liquidation = details,
        };
    }

    // ── The bid ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What the lot costs right now, and whether anyone has actually bid.
    ///
    /// The live bid comes first; with no bids the opening bid is the price, and it is flagged as one
    /// because "nobody has bid yet" is the difference between a floor and a contest.
    /// </summary>
    /// <remarks>
    /// <b>The field named <c>bidAmount</c> is deliberately never read.</b> It carried the identical
    /// value 123.45 on all 801 live lots checked across eight searches — it is a client-side
    /// placeholder, not a price. Reading it would have handed every row on this board the same
    /// invented cost basis, which on the cheap lots (where the real opening bid is $1) is the
    /// difference between the best row on the board and a loss.
    /// </remarks>
    public static (decimal? Price, bool IsStartingBid) ReadBid(JsonElement lotState)
    {
        var high = Decimal(lotState, "highBid");
        if (Credible(high)) return (high, false);

        var opening = Decimal(lotState, "minBid");
        return Credible(opening) ? (opening, true) : (null, false);
    }

    private static bool Credible(decimal? value) =>
        value is >= LiquidationSelectors.MinCredibleBid and <= LiquidationSelectors.MaxCredibleBid;

    /// <summary>
    /// The auctioneer's cut on top of the hammer, as a percentage.
    ///
    /// Published as a multiplier (1.15 for 15%), which is believed when it is there. When it isn't,
    /// the printed terms are read for a percentage; when those don't state one either, a premium is
    /// ASSUMED rather than waived, and the row says so. A published zero and an unpublished premium
    /// look identical in this data, and of the two mistakes only one costs money: assuming a premium
    /// that isn't charged makes the app pass on a deal, while assuming none where 15% is charged
    /// makes it buy a loser.
    /// </summary>
    public static decimal ReadBuyerPremium(JsonElement auction, out bool assumed)
    {
        assumed = false;

        var rate = Decimal(auction, "buyerPremiumRate");
        // Anything outside this range isn't a premium multiplier — it is a different field or a
        // typo, and charging a 400% premium would blank the board.
        if (rate is > 1m and <= 2m) return Math.Round((rate.Value - 1m) * 100m, 2);

        var printed = LiquidationSelectors.PremiumPercentInText.Match(Text(auction, "buyerPremium"));
        if (printed.Success
            && decimal.TryParse(printed.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out var percent)
            && percent is > 0m and <= 40m)
        {
            return percent;
        }

        assumed = true;
        return LiquidationLotPricer.AssumedBuyerPremiumPercent;
    }

    // ── How many things is this ──────────────────────────────────────────────────────────────────

    /// <summary>The result of asking a lot title how many units it is.</summary>
    /// <param name="Count">Units, only ever from a count the listing stated.</param>
    /// <param name="IsLot">True when this is several units of one product rather than one item.</param>
    /// <param name="Reason">Set when it is bulk stock whose size was never stated, so it can't be priced.</param>
    public readonly record struct LotSize(int Count, bool IsLot, string? Reason);

    /// <summary>
    /// How many units the listing says are in the lot. <b>Stated counts only</b> — the word "pallet"
    /// never implies a quantity, and neither does anything else. An unstated count is reported as a
    /// reason the row can't be priced, because a divisor nobody wrote down is not a divisor.
    /// </summary>
    public static LotSize ReadUnits(string? title, string? description, int siteQuantity)
    {
        var text = title ?? "";

        // The site's own quantity field, when it says more than one, is the most reliable answer
        // there is — it is structured data rather than a title convention.
        var stated = siteQuantity > 1 ? siteQuantity : 0;

        foreach (var pattern in new[] {
            LiquidationSelectors.LeadingCount, LiquidationSelectors.CountOf, LiquidationSelectors.CountQtyValue })
        {
            var match = pattern.Match(text);
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count) && count > stated)
                stated = count;
        }

        // "24 pcs" is read only when nothing more explicit was found: it is the pattern most likely
        // to catch a specification instead of a count ("50 count nails" is a count; "18 in. chain"
        // is not, and only the units word keeps them apart).
        if (stated == 0)
        {
            var units = LiquidationSelectors.CountUnits.Match(text);
            if (units.Success && int.TryParse(units.Groups[1].Value, out var count)) stated = count;
        }

        if (stated > LiquidationSelectors.MaxCredibleUnits)
            return new LotSize(1, false, $"The title reads as {stated:N0} units, which is a model number or a spec rather than a quantity — this one needs pricing by hand.");

        if (stated > 1) return new LotSize(stated, true, null);

        if (!LiquidationSelectors.BulkWithoutCount.IsMatch(text)
            && !LiquidationSelectors.BulkWithoutCount.IsMatch(description ?? ""))
        {
            return new LotSize(1, false, null);
        }

        // Bulk wording. Before refusing it, check for a count stated somewhere other than the
        // front — "LOT OF (2) DeWalt Rotary Hammer Drills" says exactly how many it is. Safe only
        // here: inside bulk wording a bracketed number is a quantity, where in an arbitrary title
        // it could be anything.
        var bracketed = LiquidationSelectors.BracketedCount.Match(text);
        if (bracketed.Success && int.TryParse(bracketed.Groups[1].Value, out var inside)
            && inside is > 1 and <= LiquidationSelectors.MaxCredibleUnits)
        {
            return new LotSize(inside, true, null);
        }

        // Genuinely unpriceable: the resale value of a pallet is the per-unit price times a number
        // nobody published.
        return new LotSize(1, true,
            "This is bulk stock, but neither the title nor the description says how many units are in it — so there is no honest way to price it. Open the lot and count.");
    }

    /// <summary>
    /// Why this row can't be priced against sold comps, or null when it can.
    ///
    /// Two refusals, and both of them are about there being no single product to look up. An
    /// "assorted" lot is priced against whatever the matcher happens to find for the word, and a
    /// multi-unit lot whose title lists three different things has no per-unit comp to multiply at
    /// all — those are the rows where a confident, badged, ranked number would be pure fiction.
    /// </summary>
    public static string? UnpriceableReason(string? title, string? wording, LotSize size)
    {
        if (LiquidationSelectors.ForPartsOnly.IsMatch(title ?? ""))
            return "Sold for parts or not working — the sold comps behind any price for it are comps for items that work.";

        if (!size.IsLot) return null;

        if (LiquidationSelectors.AssortedContents.IsMatch(wording ?? title ?? ""))
            return "The contents are described as assorted, so there is no single product to price the units against.";

        // Applied only to lots: on a single item a comma is ordinary punctuation.
        var body = LiquidationSelectors.LeadingCount.Replace(title ?? "", "");
        if (LiquidationSelectors.MixedContentsList.IsMatch(body))
            return "The title lists several different items, so the units aren't all the same product — there is no one comp to multiply.";

        return null;
    }

    /// <summary>Which of <see cref="LotAnalyzer.Grades"/> the wording implies. See LiquidationSelectors.GradeCues.</summary>
    public static string GradeFor(string? wording)
    {
        foreach (var (pattern, gradeId) in LiquidationSelectors.GradeCues)
            if (pattern.IsMatch(wording ?? "")) return gradeId;

        return LiquidationSelectors.DefaultLotGradeId;
    }

    /// <summary>True when this isn't something an eBay seller can list at all — see NotFlippablePhrases.</summary>
    public static bool IsNotFlippable(string? text) =>
        !string.IsNullOrWhiteSpace(text)
        && LiquidationSelectors.NotFlippablePhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The retail value the listing claims for itself. Never a value — a cross-check, exactly as a
    /// manifest's retail column is (<see cref="LotAnalyzer.RetailSanityCheck"/>).
    /// </summary>
    public static decimal? ReadClaimedRetail(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var match = LiquidationSelectors.ClaimedRetailBefore.Match(text);
        if (!match.Success) match = LiquidationSelectors.ClaimedRetailAfter.Match(text);
        if (!match.Success) return null;

        return decimal.TryParse(match.Groups[1].Value.Replace(",", ""),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var value) && value > 0m ? value : null;
    }

    /// <summary>
    /// Strips the auction's own conventions off a lot title so the comp lookup runs on the product:
    /// the house's lot number, the leading unit count, and the "as-is" disclaimer stapled to the
    /// end. Same job <see cref="CraigslistParser.CleanTitle"/> does for a neighbourhood suffix.
    ///
    /// The count comes off only after it has been read — <see cref="ReadUnits"/> runs on the raw
    /// title, and this runs on it afterwards.
    /// </summary>
    public static string CleanTitle(string? rawTitle)
    {
        var title = (rawTitle ?? "").Trim();

        title = LiquidationSelectors.LeadingLotNumber.Replace(title, "");
        title = LiquidationSelectors.LeadingCount.Replace(title, "");

        // Twice: "… Drill, Used, As-Is" needs two passes to lose both tails, and each strip is what
        // exposes the next one as trailing.
        for (var pass = 0; pass < 2; pass++)
            title = LiquidationSelectors.TrailingConditionTail.Replace(title, "").Trim();

        return Tidy(title).Trim(' ', ',', '-', '–', '—', '|', ':');
    }

    // ── Result building ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Assembles the search result across the slices that answered. The site ran the seller's words
    /// itself on every slice, so this gets the lenient treatment Craigslist's results get
    /// (<see cref="LocalSupplyResults.FilterByRelevance"/>, which falls back to everything rather
    /// than report a false empty) — the strict filter <see cref="DealFeedParser"/> uses exists for
    /// firehoses nobody searched.
    /// </summary>
    public static LocalSupplySearchResult BuildResult(
        IEnumerable<List<LocalSupplyListing>> perFeed, string query, string zip, int radiusMiles)
    {
        // Dedupe first, then filter: the same lot comes back from several slices by design, and
        // collapsing it before anything else is measured keeps the counts meaning "things to buy".
        var items = LocalSupplyResults.FilterByRelevance(
            LocalSupplyResults.Dedupe(perFeed.SelectMany(f => f)), query);

        // Cheapest first, matching every other source. The ranking that decides what a seller acts
        // on happens in LocalArbitrageAnalyzer, against real sold comps.
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId = LiquidationCatalog.SourceId,
            SourceLabel = LiquidationCatalog.SourceLabel,
            Status = "ok",
            Query = query,
            ZipCode = zip ?? "",
            RadiusMiles = LiquidationCatalog.RadiusFor(radiusMiles),
            SearchUrl = LiquidationCatalog.SearchPageUrl(query, zip, radiusMiles),
            ScopeLabel = LiquidationCatalog.ScopeLabel(zip, radiusMiles),
            Items = items,
            Min = min, Median = median, Max = max,
        };
    }

    /// <summary>
    /// A 200 that isn't an answer. Only the head of the document is scanned, for the same reason
    /// <see cref="CraigslistParser.DetectBlock"/> gives: a real page is megabytes of lots, and
    /// somebody's listing for a door lock genuinely does say "access denied".
    /// </summary>
    public static string? DetectBlock(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return $"{LiquidationCatalog.Site} sent back an empty page — try again in a moment.";

        var head = body.Length > LiquidationSelectors.BlockScanChars
            ? body[..LiquidationSelectors.BlockScanChars]
            : body;

        return LiquidationSelectors.BlockPhrases.Any(p => head.Contains(p, StringComparison.OrdinalIgnoreCase))
            ? $"{LiquidationCatalog.Site} is blocking this connection right now — wait a minute and scan again."
            : null;
    }

    // ── Small readers ────────────────────────────────────────────────────────────────────────────

    private static string AuctioneerName(JsonElement auction, IReadOnlyDictionary<string, JsonElement> store)
    {
        var auctioneer = Ref(auction, "auctioneer", store);
        var name = Text(auctioneer, "name");
        return name.Length > 0 ? name : Text(auctioneer, "company");
    }

    // Where the seller physically collects it. Pickup is the whole logistics question on an auction
    // lot, so the city goes in the field every board renders as "where".
    private static string PlaceOf(JsonElement auction)
    {
        var city = Text(auction, "eventCity");
        var state = Text(auction, "eventState");

        if (city.Length > 0 && state.Length > 0) return $"{city}, {state.ToUpperInvariant()}";
        return city.Length > 0 ? city : state;
    }

    private static string ImageOf(JsonElement lot)
    {
        var picture = Child(lot, "featuredPicture");
        foreach (var field in new[] { "thumbnailLocation", "hdThumbnailLocation", "fullSizeLocation" })
        {
            var url = Text(picture, field);
            if (url.Length > 0) return url;
        }

        return "";
    }

    /// <summary>Follows a <c>{"__ref": "Type:id"}</c> pointer into the state store.</summary>
    private static JsonElement Ref(JsonElement parent, string name, IReadOnlyDictionary<string, JsonElement> store)
    {
        var pointer = Child(parent, name);
        var key = Text(pointer, "__ref");
        return key.Length > 0 && store.TryGetValue(key, out var target) ? target : default;
    }

    private static JsonElement Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) ? child : default;

    // Reads a field as text whatever the site chose to encode it as — ids in this document arrive
    // as numbers in some places and as strings in others.
    private static string Text(JsonElement parent, string name)
    {
        var value = Child(parent, name);
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? "",
            JsonValueKind.Number => value.ToString(),
            _ => "",
        };
    }

    private static decimal? Decimal(JsonElement parent, string name) =>
        Child(parent, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetDecimal(out var number)
            ? number : null;

    private static int IntOf(JsonElement parent, string name) =>
        Decimal(parent, name) is { } value && value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;

    private static bool Flag(JsonElement parent, string name) =>
        Child(parent, name).ValueKind == JsonValueKind.True;

    private static string Tidy(string? text) =>
        LiquidationSelectors.Whitespace.Replace((text ?? "").Replace(' ', ' '), " ").Trim();
}
