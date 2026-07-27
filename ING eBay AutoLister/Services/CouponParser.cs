using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a public coupon list's RSS into <see cref="CouponOffer"/> rows. Pure — no HTTP, and the
/// clock is passed in — so every rule here is a unit test rather than a guess about a live feed.
///
/// It reads the same three dialects <see cref="DealFeedParser"/> reads, off the same sites, by
/// element local name; a fourth list is a URL in <see cref="CouponCatalog"/> rather than a new code
/// path.
/// </summary>
/// <remarks>
/// <para>
/// Like the deal parser, the real job is refusal — but the stakes are the other way round. A missed
/// deal is a deal the seller never sees; a fabricated <b>discount</b> lowers the cost basis under a
/// profit figure that is already on screen with a green badge on it. So an entry only becomes a
/// bankable offer when all four of these are true, and becomes a lead worth reading otherwise:
/// </para>
/// <list type="number">
///   <item>it actually names <b>this store</b> — a store search returns threads that merely mention it;</item>
///   <item>it carries a <b>code</b>, because a discount needing no code is already in the shelf price;</item>
///   <item>the discount is a <b>number</b>, not a range — "up to 40% off" is an advertisement, not a price;</item>
///   <item>nothing in it says it has <b>already ended</b>.</item>
/// </list>
/// </remarks>
public static class CouponParser
{
    /// <summary>
    /// Parses one list. Returns an empty list rather than throwing on malformed XML: a feed that
    /// changed shape degrades into "this one found nothing" beside the lists that worked.
    /// </summary>
    public static List<CouponOffer> ParseFeed(
        string? xml, CouponFeed feed, CouponMerchant merchant, DateTime? nowUtc = null)
    {
        var offers = new List<CouponOffer>();
        if (string.IsNullOrWhiteSpace(xml)) return offers;

        XDocument doc;
        try
        {
            // Same posture as DealFeedParser: DTD processing off and no resolver, because this
            // parses a document fetched over the network.
            using var reader = XmlReader.Create(new StringReader(xml),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreWhitespace = true });
            doc = XDocument.Load(reader);
        }
        catch (XmlException) { return offers; }

        var now = nowUtc ?? DateTime.UtcNow;
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
            offers.AddRange(ParseItem(item, feed, merchant, now));

        return offers;
    }

    /// <summary>
    /// One entry. Usually nothing, sometimes one offer, occasionally two — a thread can carry both a
    /// store code and a cashback rate, and those are different offers that genuinely stack.
    /// </summary>
    public static List<CouponOffer> ParseItem(
        XElement item, CouponFeed feed, CouponMerchant merchant, DateTime nowUtc)
    {
        string Child(string name) =>
            item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value?.Trim() ?? "";

        var title = DealFeedParser.Decode(Child("title"));
        var url = Child("link");
        if (url.Length == 0) url = Child("guid");
        if (title.Length == 0 || url.Length == 0) return [];

        var description = Child("description");
        var body = Child("encoded");
        var prose = DealFeedParser.StripHtml(
            $"{DealFeedParser.Decode(description)} {DealFeedParser.Decode(body)}");
        var text = $"{title} — {prose}";

        // Said outright to be over. These lists never delete anything, so this is the only place a
        // dead code announces itself.
        if (CouponSelectors.Expired.IsMatch(text)) return [];

        // A store search returns whatever mentions the store, including threads about a rival's
        // sale. An offer attributed to the wrong retailer is a code the seller cannot use, typed
        // into a checkout that has never heard of it.
        if (!SoldByMerchant(item, description, body, text, merchant)) return [];

        // A discount is not a coupon. The store searches these lists run come back full of ordinary
        // deals — "75% off the 430 Series water cooler" is a clearance price, already on the board
        // one screen over, and it has nothing to say about the price of anything else. Verified
        // against the live Slickdeals feed, where it is most of what comes back.
        var code = ReadCode(title, prose);
        if (code.Length == 0 && !CouponSelectors.CouponWording.IsMatch(text)) return [];

        var published = DealFeedParser.ReadDate(Child("pubDate"), Child("date")) ?? nowUtc;
        var (expiresUtc, expiresText) = ReadExpiry(text, published, nowUtc);

        // A deadline that has already passed is the single most common thing on a coupon list.
        if (expiresUtc is not null && expiresUtc < nowUtc.Date) return [];

        var exclusions = ReadExclusions(text);
        // Whether the code is against the basket or against the one item this entry is about — the
        // difference between a discount and a fabrication. See CouponSelectors.OrderWide.
        var appliesToOrder = CouponSelectors.OrderWide.IsMatch(text)
            || CouponSelectors.AmountOffThreshold.IsMatch(text);
        var offers = new List<CouponOffer>();

        CouponOffer Base(string kind) => new()
        {
            MerchantId = merchant.Id,
            MerchantLabel = merchant.Label,
            Kind = kind,
            Title = Trim(title),
            Url = url,
            SourceLabel = feed.Site,
            PublishedUtc = published,
            ExpiresUtc = expiresUtc,
            ExpiresText = expiresText,
            ExclusionsNote = exclusions,
            // Cashback is order-wide by construction: the portal pays a percentage of what the order
            // came to, whatever was in it.
            AppliesToOrder = appliesToOrder || kind == CouponKinds.Cashback,
        };

        var discount = ReadDiscount(text);
        if (discount is not null)
        {
            var offer = Base(discount.Value.Kind);
            offer.Value = discount.Value.Value;
            offer.Code = code;
            offer.MinSpend = ReadMinSpend(text);
            offer.MaxDiscount = discount.Value.Cap;
            Grade(offer, nowUtc, discount.Value.IsRange);
            offers.Add(offer);
        }
        else if (CouponSelectors.FreeShipping.IsMatch(text) && CouponSelectors.CouponWording.IsMatch(text))
        {
            // Real money, of an amount nobody here knows. Surfaced so the seller can use it; never
            // counted, because a saving of an unknown size cannot be added to a profit figure.
            var offer = Base(CouponKinds.FreeShipping);
            offer.Code = code;
            offer.MinSpend = ReadMinSpend(text);
            Grade(offer, nowUtc, isRange: false);
            offers.Add(offer);
        }

        var cashback = CouponSelectors.Cashback.Match(text);
        if (cashback.Success && decimal.TryParse(cashback.Groups[1].Value, out var rate))
        {
            var offer = Base(CouponKinds.Cashback);
            offer.Value = rate;
            // Whoever pays it, when the entry says. It is never the retailer, which is exactly why
            // this one stacks with a code at the retailer's own checkout.
            var portal = CouponSelectors.CashbackPortal.Match(text);
            offer.MerchantLabel = portal.Success
                ? $"{merchant.Label} via {Titleize(portal.Groups[1].Value)}"
                : merchant.Label;
            Grade(offer, nowUtc, isRange: CouponSelectors.UpToRange.IsMatch(Before(text, cashback.Index)));
            offers.Add(offer);
        }

        return offers;
    }

    // ── What the offer is worth ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The discount, and whether it is a real one. <c>IsRange</c> is the important half: "up to 40%
    /// off" is one clearance item in one department, and reading it as 40% off the seller's item
    /// invents a discount on something they are not buying.
    /// </summary>
    public static (string Kind, decimal Value, decimal Cap, bool IsRange)? ReadDiscount(string text)
    {
        var percent = CouponSelectors.PercentOff.Match(text);
        if (percent.Success && decimal.TryParse(percent.Groups[1].Value, out var pct) && pct > 0)
        {
            var isRange = CouponSelectors.UpToRange.IsMatch(Before(text, percent.Index));
            // "20% off, up to $50" is a ceiling on a real discount; the same words in front of the
            // percentage are a range. Only what comes AFTER the percentage can be a cap.
            var capMatch = CouponSelectors.MaxDiscount.Match(text, percent.Index + percent.Length);
            var cap = capMatch.Success && decimal.TryParse(capMatch.Groups[1].Value, out var c) ? c : 0m;

            return (CouponKinds.PercentOff, isRange ? 0m : pct, cap, isRange);
        }

        var amount = CouponSelectors.AmountOff.Match(text);
        if (amount.Success && decimal.TryParse(amount.Groups[1].Value, out var dollars) && dollars > 0)
        {
            var isRange = CouponSelectors.UpToRange.IsMatch(Before(text, amount.Index));
            return (CouponKinds.AmountOff, isRange ? 0m : dollars, 0m, isRange);
        }

        return null;
    }

    /// <summary>
    /// The spend the offer is gated behind. Zero means none was stated — which is not the same as
    /// none existing, and is why an ungated amount-off code is capped by share of price in
    /// <see cref="CouponStacker"/> rather than trusted.
    /// </summary>
    public static decimal ReadMinSpend(string text)
    {
        var match = CouponSelectors.MinSpend.Match(text);
        if (!match.Success) return 0m;

        // Two ways of writing it, two groups: "orders over $99" and "$50 off $250".
        var raw = match.Groups[2].Success && match.Groups[2].Value.Length > 0
            ? match.Groups[2].Value
            : match.Groups[1].Value;

        return decimal.TryParse(raw, out var value) && value > 0 ? value : 0m;
    }

    // ── The code ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The string to type at checkout, or empty when the entry doesn't state one.
    /// </summary>
    /// <remarks>
    /// A wrong code is worse than no code: it sends the seller to a checkout that rejects it and
    /// leaves them thinking the price was a lie. So a candidate has to look like a code (shouted, or
    /// letters mixed with digits) and must not be one of the words that merely shout — see
    /// <see cref="CouponSelectors.NotACode"/>.
    /// </remarks>
    public static string ReadCode(params string?[] sources)
    {
        foreach (var text in sources)
        {
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (var pattern in new[] { CouponSelectors.CodeProse, CouponSelectors.CodeTrailing })
            {
                foreach (Match match in pattern.Matches(text))
                {
                    var code = match.Groups[1].Value.Trim();
                    if (LooksLikeCode(code)) return code;
                }
            }
        }

        return "";
    }

    public static bool LooksLikeCode(string code)
    {
        if (code.Length < 4 || code.Length > 24) return false;
        if (CouponSelectors.NotACode.Contains(code, StringComparer.OrdinalIgnoreCase)) return false;
        // All digits is a price, a year or a model number, never a promo code worth printing.
        if (code.All(char.IsDigit)) return false;

        return code.Any(char.IsDigit) || code.All(c => !char.IsLower(c));
    }

    // ── The deadline ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the offer stops, as the list wrote it. The year is inferred rather than assumed: coupon
    /// lists write "exp 1/15" in December, and reading that as January just gone would drop a code
    /// that is live for another six weeks.
    /// </summary>
    public static (DateTime? ExpiresUtc, string ExpiresText) ReadExpiry(
        string text, DateTime publishedUtc, DateTime nowUtc)
    {
        var today = CouponSelectors.ExpiresToday.Match(text);
        if (today.Success) return (nowUtc.Date.AddDays(1).AddSeconds(-1), today.Groups[1].Value.Trim());

        var match = CouponSelectors.ExpiryDate.Match(text);
        if (!match.Success) return (null, "");

        if (!int.TryParse(match.Groups[1].Value, out var month) || month is < 1 or > 12) return (null, "");
        if (!int.TryParse(match.Groups[2].Value, out var day) || day is < 1 or > 31) return (null, "");

        int year;
        if (match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var stated))
        {
            year = stated < 100 ? 2000 + stated : stated;
        }
        else
        {
            // No year written. It belongs to the publication date unless that would put the deadline
            // in the past by more than a rollover's worth, in which case it is next year's.
            year = publishedUtc.Year;
            if (new DateTime(year, month, DaysIn(year, month, day)) < publishedUtc.Date.AddDays(-1)) year++;
        }

        try
        {
            var date = new DateTime(year, month, DaysIn(year, month, day), 23, 59, 59, DateTimeKind.Utc);
            return (date, match.Value.Trim());
        }
        catch (ArgumentOutOfRangeException) { return (null, ""); }
    }

    // "exp 2/30" is a typo, not a reason to lose the offer — the month's last day is the honest read.
    private static int DaysIn(int year, int month, int day) => Math.Min(day, DateTime.DaysInMonth(year, month));

    // ── Confidence ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// How much this offer is worth believing, and why in one clause.
    /// </summary>
    /// <remarks>
    /// Nothing public is graded <see cref="CouponConfidence.High"/> without earning it, because the
    /// grade travels all the way to a cost basis. Exclusions are the common reason to hold one back:
    /// "20% off select tools" may well be 20% off the seller's tool, and this app has no way to know
    /// which half of the catalogue it is in.
    /// </remarks>
    public static void Grade(CouponOffer offer, DateTime nowUtc, bool isRange)
    {
        var age = (nowUtc - offer.PublishedUtc).TotalDays;
        var hasCode = offer.Code.Length > 0;
        var dated = offer.ExpiresUtc is not null;

        if (isRange)
        {
            offer.Confidence = CouponConfidence.Low;
            offer.ConfidenceNote = "\"Up to\" is a range, not a discount — the top of it is one item in one department.";
            return;
        }

        if (offer.ExclusionsNote.Length > 0)
        {
            offer.Confidence = CouponConfidence.Low;
            offer.ConfidenceNote = $"Conditions attached: {offer.ExclusionsNote}. It may still work on your item — check before you count on it.";
            return;
        }

        if (age > CouponSelectors.StaleAfterDays || (!dated && age > CouponSelectors.UndatedStaleAfterDays))
        {
            offer.Confidence = CouponConfidence.Low;
            offer.ConfidenceNote = $"Published {(int)age} days ago with {(dated ? "a deadline" : "no deadline")} — old codes are usually spent codes.";
            return;
        }

        if (offer.Kind != CouponKinds.Cashback && !hasCode)
        {
            // A discount that needs no code is a sale, and a sale is already in the price on the
            // page. Counting it would take the discount off twice.
            offer.Confidence = CouponConfidence.Low;
            offer.ConfidenceNote = "No code stated — if this is a sale price, it is already in the price you see.";
            return;
        }

        if (!offer.AppliesToOrder)
        {
            // Real, usable, and only on the item it was posted against. Worth showing to a seller
            // buying that item; worth nothing to the price of anything else at the same store.
            offer.Confidence = CouponConfidence.Low;
            offer.ConfidenceNote = "Attached to one specific deal rather than to your order — it won't discount a different item.";
            return;
        }

        if (dated && age <= CouponSelectors.UndatedStaleAfterDays)
        {
            offer.Confidence = CouponConfidence.High;
            offer.ConfidenceNote = $"Posted {(age < 1 ? "today" : $"{(int)age}d ago")}, with a stated deadline and no conditions attached.";
            return;
        }

        offer.Confidence = CouponConfidence.Medium;
        offer.ConfidenceNote = dated
            ? $"Posted {(int)age}d ago with a stated deadline."
            : "No deadline stated, which is normal on these lists and means nobody has confirmed it recently.";
    }

    // ── Small readers ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The conditions that stop a store-wide code being store-wide, in the words the seller can act
    /// on. Empty when the entry named none.
    /// </summary>
    public static string ReadExclusions(string text)
    {
        var found = CouponSelectors.Exclusions
            .Where(e => text.Contains(e.Phrase, StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Says)
            .Distinct()
            .Take(3)
            .ToList();

        return string.Join(", ", found);
    }

    /// <summary>
    /// Whether the entry is an offer AT this store, rather than one that merely says its name.
    /// </summary>
    /// <remarks>
    /// The entry's own declared store wins whenever it has one, because a mention is not a source:
    /// a search for Lenovo codes returns Amazon listings for "140W laptop power bank for Lenovo",
    /// each with a working Amazon code on it. Attributing those to Lenovo would put codes on a
    /// Lenovo row that Lenovo's checkout has never heard of. Verified against the live feed, where
    /// it was every result for that store.
    ///
    /// Read through <see cref="DealFeedParser.ReadRetailer"/> — the same reader the deal board uses
    /// to decide which shop a row is bought from, so the two can never disagree about it.
    /// </remarks>
    public static bool SoldByMerchant(
        XElement item, string? description, string? body, string text, CouponMerchant merchant)
    {
        var declared = DealFeedParser.ReadRetailer(item, description, body);
        if (declared.Length > 0)
            return CouponCatalog.Resolve(declared)?.Id.Equals(merchant.Id, StringComparison.OrdinalIgnoreCase) == true;

        // Nothing declared — TechBargains and the shorter DealNews entries often say nothing about
        // the shop. Falling back to the name in the text is looser, but the alternative is dropping
        // every entry from the lists that don't publish a store field.
        return MentionsMerchant(text, merchant);
    }

    /// <summary>
    /// Whether the entry names this store at all. Compared on letters and digits alone, so
    /// "Home Depot", "homedepot.com" and "The Home Depot" all meet — see CouponCatalog.Normalize.
    /// </summary>
    public static bool MentionsMerchant(string text, CouponMerchant merchant)
    {
        var haystack = CouponCatalog.Normalize(text);
        var lower = text.ToLowerInvariant();

        var needles = new[] { merchant.Label, merchant.Id, merchant.Domain }
            .Concat(merchant.Aliases)
            .Select(CouponCatalog.Normalize)
            .Where(n => n.Length > 0)
            .Distinct();

        foreach (var needle in needles)
        {
            // A short name ("hp", "rei") is a substring of half the words in English, so it has to
            // appear as a word rather than anywhere in the letters.
            if (needle.Length < 4)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(needle)}\b")) return true;
                continue;
            }

            if (haystack.Contains(needle, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    // Only what is immediately in front of a figure decides what the figure means — "up to" three
    // sentences earlier is about something else entirely.
    private const int LookBehindChars = 24;

    private static string Before(string text, int index) =>
        text[Math.Max(0, index - LookBehindChars)..index];

    private const int MaxTitleChars = 140;

    private static string Trim(string title) =>
        title.Length > MaxTitleChars ? title[..MaxTitleChars].TrimEnd() + "…" : title;

    private static string Titleize(string value) =>
        string.Join(' ', value.Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant()));
}
