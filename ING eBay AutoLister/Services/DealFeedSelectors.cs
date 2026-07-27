using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every pattern the deal-feed parser matches with, in one file — the same posture as
/// <see cref="FacebookMarketplaceSelectors"/>, and for the same reason: these sites reword their
/// titles constantly, so tuning the scanner has to mean editing a string here rather than touching
/// the parser.
///
/// Almost all of this exists to answer one question honestly: <b>is that dollar figure the price of
/// the thing, or something else?</b> Deal titles are written to sell, and they are full of dollar
/// amounts that are not prices — "$50 off", "$25 back in credit", "free $10 gift card w/ $75". Read
/// any one of those as a cost basis and the scan invents a goldmine out of a full-price item, which
/// is the worst thing this feature could do. So a figure is only a price when nothing around it
/// says otherwise, and an entry with no clean price is dropped rather than guessed at.
/// </summary>
public static class DealFeedSelectors
{
    // "$1,299.99" / "$59" — the thousands-separated form first so "$1,250" is never read as $1.
    // Same shape as CraigslistParser's, deliberately: one idea of what a written price looks like.
    public static readonly Regex PriceToken =
        new(@"\$\s?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)", RegexOptions.Compiled);

    /// <summary>
    /// Words immediately BEFORE a figure that mean it isn't what the item costs — a saving, a
    /// threshold, a credit, a gift card. "Save $50", "orders over $35", "free $10 gift card".
    /// </summary>
    /// <remarks>
    /// A bare <c>+</c> is in here for a reason found live: "…Laptop $499.99 +$14.99 Shipping" reads
    /// its LAST figure as $14.99, and a $500 laptop with a $15 cost basis is a fabricated goldmine
    /// at the very top of the ranking. Anything added to a price is not the price.
    /// </remarks>
    public static readonly Regex NotAPriceBefore = new(
        @"(save|saves|saving|savings|extra|additional|up\s+to|as\s+much\s+as|coupon|promo|code|gift\s*card|" +
        @"egift|e-?gift|voucher|w/|with|spend|spending|orders?\s+(of|over)|purchases?\s+(of|over)|over|" +
        @"earn|bonus|credit|rebate|cash\s*back|off|discount|worth|free|\+)\W{0,8}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Words immediately AFTER a figure that mean the same thing — "$50 off", "$25 back",
    /// "$14.99 shipping". Checked as well as the words before, because deal titles put the
    /// qualifier on whichever side reads better, and because shipping is quoted both ways.
    /// </summary>
    public static readonly Regex NotAPriceAfter = new(
        @"^\s*(off|back|credit|rebate|value|gift\s*card|egift|e-?gift|voucher|toward[s]?|savings?|" +
        @"discount|bonus|in\s+(store\s+)?credit|cash\s*back|shipping|shipped|shipping\s+fee|s&h|" +
        @"delivery|handling|minimum|min\.|threshold)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Words before a figure that make it the price it USED to be. Captured rather than discarded:
    /// a struck-through retail price is exactly the signal <c>LocalSupplyListing.OriginalPrice</c>
    /// already carries for a local seller who cut their own price.
    /// </summary>
    public static readonly Regex WasPriceBefore = new(
        @"(was|reg\.?|regularly|regular|list(\s+price)?|orig(\.|inally|inal)?|msrp|retail(\s+price)?|" +
        @"normally|down\s+from|compared?\s+to|price\s+of)\W{0,8}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Entries that are not one buyable object, whatever price they quote. A gift card, a
    /// subscription, a cruise or a "spend $75 get $15" promotion has no resale comp and no honest
    /// cost basis, and letting any of them into a profit ranking produces a number that is not
    /// wrong so much as meaningless.
    ///
    /// Each phrase is specific enough that a real product title can't trip it. "Refurbished" and
    /// "open box" are deliberately NOT here — those are the best margins on the board.
    /// </summary>
    public static readonly string[] NotAProductPhrases =
    [
        "gift card", "giftcard", "egift", "e-gift", "gift certificate",
        "subscription", "membership", "free trial", "streaming",
        "credit card", "cash back", "cashback", "statement credit",
        "insurance", "protection plan", "service plan", "extended warranty",
        "cruise", "flight", "hotel", "airfare", "rental car", "theme park",
        "concert", "tickets to", "sweepstakes", "giveaway", "donation",
        "trade-in", "trade in credit", "price match", "printable coupon",
        "meal kit", "grocery delivery", "food delivery",
        "no purchase necessary", "with purchase", "w/ purchase", "w/ qualifying",
    ];

    /// <summary>
    /// Headline patterns for a whole category sale rather than an item — "Up to 60% off tools",
    /// "Nike Sale: extra 25% off". Any price these mention belongs to something unnamed, so the
    /// entry is dropped whether or not a figure was parsed.
    /// </summary>
    public static readonly Regex CategorySaleHeadline = new(
        @"(\bup\s+to\s+\d+%\s*off|\bup\s+to\s+\$|^\s*save\s+up\s+to\b|\bextra\s+\d+%\s*off\b|" +
        @"\b(sale|deals|savings|clearance|event)\s*:|\bstarting\s+(at|from)\s+\$?\d+%|\bshop\s+(all|the)\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Shipping is free far more often than not on these, and it is worth saying so.</summary>
    public static readonly Regex FreeShipping = new(
        @"(free\s+(shipping|delivery|ship\b)|ships?\s+free|free\s+2-?day|free\s+store\s+pickup)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A promo code the seller has to actually type at checkout. Carried onto the row because a
    /// price that only exists with a code, shown without the code, is a price the seller can't get.
    /// Slickdeals marks these up; everything else states them in prose.
    /// </summary>
    public static readonly Regex CouponCodeMarkup = new(
        @"<span[^>]*class=['""][^'""]*\bcode\b[^'""]*['""][^>]*>\s*(?:<strong>)?\s*([A-Za-z0-9_\-]{4,24})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Regex CouponCodeProse = new(
        @"\b(?:promo|coupon|discount)?\s*code:?\s*""?([A-Z0-9][A-Z0-9_\-]{3,23})""?\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Slickdeals writes the store into the body: <c>Amazon [amazon.com] has *the thing* for *$X*</c>.
    /// The bracketed domain is the durable half — the visible name is a link whose text changes.
    /// </summary>
    public static readonly Regex StoreLead = new(
        @"^\s*([A-Za-z][A-Za-z0-9&'’.\- ]{1,34}?)\s*\[\s*(?:https?://)?(?:www\.)?([a-z0-9][a-z0-9\-]*\.[a-z.]{2,})\s*\]",
        RegexOptions.Compiled);

    /// <summary>Slickdeals' own machine-readable store slug, when the prose form isn't there.</summary>
    public static readonly Regex StoreSlug =
        new(@"data-store-slug=['""]([a-z0-9\-]{2,30})['""]", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Posting conventions that wrap the product name. Slickdeals tags threads ("[Lightning Deal]"),
    /// leads with the price ("$19.99* | 24L Backpack …"), and appends the store as prose or as a
    /// bare domain ("… Laptop $449.99 Bestbuy.com"). Every one of those reaches ProductNormalizer as
    /// part of the product identity unless it comes off here.
    /// </summary>
    public static readonly Regex LeadingTag =
        new(@"^\s*\[[^\]]{1,40}\]\s*", RegexOptions.Compiled);

    public static readonly Regex LeadingPrice =
        new(@"^\s*\$\s?[\d,.]+\*?\s*(?:[|:\-–—]\s*)?", RegexOptions.Compiled);

    // The same convention with a channel note in front of it: "Woot! App: $155.99 | Dyson V11 …".
    // The trailing pipe is what makes this safe to strip — it is a posting separator, not something
    // that appears inside a product name.
    public static readonly Regex LeadingPricePrefix =
        new(@"^\s*[^|$]{0,30}?\$\s?[\d,.]+\*?\s*\|\s*", RegexOptions.Compiled);

    // "at Amazon", "via Woot!" — deliberately NOT "from", which is how a brand is named
    // ("Ryzen 5 from AMD") as often as a store is.
    public static readonly Regex TrailingStore = new(
        @"\s*(?:[-–—|,]\s*)?\b(?:at|via)\s+[A-Z][A-Za-z0-9&'’.\- ]{1,28}!?\s*$",
        RegexOptions.Compiled);

    public static readonly Regex TrailingDomain = new(
        @"\s*[A-Za-z0-9][A-Za-z0-9\-]{1,30}\.(?:com|net|org|co|io|us)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Deal titles end with the price and the shipping promise; the comp matcher wants neither.</summary>
    public static readonly Regex TrailingPriceTail = new(
        @"\s*(?:[-–—,:]\s*)?(?:for\s+|only\s+|now\s+|just\s+)?\$\s?[\d,.]+\s*" +
        @"(?:\+\s*free\s+(?:shipping|delivery|ship)|\+\s*\$[\d,.]+\s+shipping|,?\s*free\s+shipping|\s*shipped)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Everything after the price that is about the transaction rather than the product.</summary>
    public static readonly Regex TrailingOfferTail = new(
        @"\s*(?:[-–—|,]\s*)?(?:\+\s*)?(?:free\s+(?:shipping|delivery|store\s+pickup|s\s*&\s*h)|" +
        @"w/\s*(?:free\s+)?(?:shipping|prime|code\s+\S+)|shipped\s+free|" +
        @"free\s+shipping\s+w/?\s*\$?\d*|\bshipped\b|\bplus\s+free\s+shipping\b)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Block / interstitial wording. These sites answer an address they don't like with a 200 and a
    /// challenge page, which parses to zero deals and would otherwise be reported as "no deals
    /// match" — an answer that looks like an answer, and the one failure mode worth detecting.
    /// </summary>
    public static readonly string[] BlockPhrases =
    [
        "attention required",
        "checking your browser before accessing",
        "verify you are a human",
        "please complete the security check",
        "enable javascript and cookies to continue",
        "access denied",
        "too many requests",
        "rate limit exceeded",
    ];

    /// <summary>Only the head of a document is scanned for the above — see CraigslistParser.DetectBlock.</summary>
    public const int BlockScanChars = 4000;

    // Above this a parsed "price" is a parsing accident, not a clearance deal. Below it, a figure
    // that small is almost always a shipping threshold or a coupon value rather than a resale item.
    public const decimal MaxCrediblePrice = 50_000m;
    public const decimal MinCrediblePrice = 1m;

    public static readonly Regex NonWord = new(@"[^a-z0-9]+", RegexOptions.Compiled);
    public static readonly Regex HtmlTag = new(@"<[^>]+>", RegexOptions.Compiled);
    public static readonly Regex ImgSrc =
        new(@"<img[^>]+src=[""']([^""']+)[""']", RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
