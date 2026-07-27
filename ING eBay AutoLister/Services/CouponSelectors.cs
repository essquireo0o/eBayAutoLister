using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every pattern the coupon parser matches with, in one file — the same posture as
/// <see cref="DealFeedSelectors"/> and <see cref="FreebieSelectors"/>, and for the same reason:
/// these lists reword themselves constantly, so tuning has to mean editing a string here rather
/// than touching the parser.
/// </summary>
/// <remarks>
/// <para>
/// Coupon prose is written to be clicked, not to be read literally, and almost every phrase in it
/// is a number attached to a condition. "Up to 70% off" is a range whose top end applies to one
/// item nobody wants. "$50 off $500" is not $50 off. "20% off select styles" is 20% off something
/// the seller's item probably isn't. So most of what is here exists to tell a discount apart from
/// an advertisement for one, and the parser drops whatever it can't read cleanly.
/// </para>
/// <para>
/// The expensive failure is not missing a code — it is banking one that isn't real, because that
/// lowers a cost basis the whole profit figure is built on. Everything here errs toward reading
/// less.
/// </para>
/// </remarks>
public static class CouponSelectors
{
    // ── What kind of offer this is ───────────────────────────────────────────────────────────────

    /// <summary>
    /// "20% off", "extra 15% off". Deliberately capped at two digits: a "100% off" in a coupon
    /// title is a free-after-coupon deal (which the freebie board already handles) or a misparse,
    /// and neither belongs in a discount here.
    /// </summary>
    public static readonly Regex PercentOff = new(
        @"(\d{1,2})\s*%\s*(?:off|discount|savings?)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"$30 off", "$15 off your order". The dollar equivalent of the above.</summary>
    public static readonly Regex AmountOff = new(
        @"\$\s?(\d{1,4}(?:\.\d{1,2})?)\s*(?:off|discount)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// "Up to 40% off", "as much as $200 off". A RANGE, not a discount — the top of it applies to
    /// one clearance item in one department. Any offer wearing this loses its value entirely and is
    /// surfaced as a lead rather than priced, because the alternative is inventing the seller a
    /// discount that only exists on something they aren't buying.
    /// </summary>
    public static readonly Regex UpToRange = new(
        @"\b(?:up\s+to|as\s+much\s+as|save\s+up\s+to)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The spend the offer is gated behind: "$50 off $250+", "orders over $99", "with $35 purchase".
    /// Missing this turns a threshold coupon into a discount on an item too cheap to qualify.
    /// </summary>
    public static readonly Regex MinSpend = new(
        @"(?:orders?|purchases?|spend|spending|w/|with|on)\s*(?:of|over|above|totall?ing)?\s*\$\s?(\d{1,4}(?:\.\d{1,2})?)\s*\+?" +
        @"|\$\s?\d{1,4}(?:\.\d{1,2})?\s*off\s*\$\s?(\d{1,4}(?:\.\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The ceiling on a percentage: "20% off, up to $50 off". Without it a % code is unbounded.</summary>
    public static readonly Regex MaxDiscount = new(
        @"(?:up\s+to|max(?:imum)?(?:\s+(?:of|discount))?|capped\s+at)\s*\$\s?(\d{1,4}(?:\.\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// "8% cash back at Rakuten", "6% back via TopCashback". The one offer that genuinely stacks
    /// with a store code, because it is paid by the portal rather than taken off at the till.
    /// </summary>
    public static readonly Regex Cashback = new(
        @"(\d{1,2}(?:\.\d)?)\s*%\s*(?:cash\s*back|cashback|back\s+(?:in\s+cash|via|at|from|through))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Who pays the cashback, when the offer names them. Shown, never assumed.</summary>
    public static readonly Regex CashbackPortal = new(
        @"\b(rakuten|ebates|topcashback|top\s*cashback|befrugal|be\s*frugal|honey\s*gold|mr\.?\s*rebates|" +
        @"active\s*junky|swagbucks|ibotta|fluz|capital\s*one\s*shopping)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Shipping waived. Real money, but of an amount this app does not know — see CouponKinds.</summary>
    public static readonly Regex FreeShipping = DealFeedSelectors.FreeShipping;

    // ── Whether the code applies to the ORDER or to one item ─────────────────────────────────────

    /// <summary>
    /// Wording that makes a code apply to whatever is in the basket rather than to one listed item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the most important distinction in the whole feature, and it was learned from the live
    /// feeds rather than guessed: 22 of the 25 entries a "Newegg promo code" search returns carry a
    /// code, and nearly all of them read <c>"$40 off when you apply promo code LUSF2737 at checkout
    /// = $189.99"</c> — a code bound to that one motherboard.
    /// </para>
    /// <para>
    /// Applying such a code to a different item at the same store would invent a discount that does
    /// not exist, on a row the seller is about to spend money on. So an item's code is surfaced and
    /// never counted; only an order-wide one may cut a cost basis. See <see cref="CouponStacker"/>.
    /// </para>
    /// </remarks>
    public static readonly Regex OrderWide = new(
        @"(\bsite\s?-?wide\b|\bstore\s?-?wide\b|\byour\s+(entire\s+)?(order|purchase|cart)\b|" +
        @"\bentire\s+(order|purchase|cart|site)\b|\bany\s+(order|purchase)\b|\ball\s+orders\b|" +
        @"\bfirst\s+order\b|\bwhen\s+you\s+spend\b|\bspend\s*\$|\borders?\s+(of|over|above)\s*\$|" +
        @"\bpurchases?\s+(of|over|above)\s*\$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// "$50 off $250" — a threshold coupon, which is order-wide by construction: it is stated
    /// against a basket total rather than against a product.
    /// </summary>
    public static readonly Regex AmountOffThreshold = new(
        @"\$\s?\d{1,4}(?:\.\d{1,2})?\s*off\s*\$\s?\d{1,4}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── The code itself ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// "Code: SAVE20", "promo code FALL15", "use coupon code TOOLS10". Stricter than the deal
    /// feeds' version on purpose: this file's output is a cost basis, and a code read out of a
    /// sentence sends the seller to a checkout that rejects it.
    /// </summary>
    public static readonly Regex CodeProse = new(
        @"\b(?:promo|coupon|discount|offer)?\s*code:?\s*""?([A-Z0-9][A-Z0-9_\-]{2,23})""?",
        RegexOptions.Compiled);

    /// <summary>The same thing written the other way round: "SAVE20 at checkout", "FALL15 saves 15%".</summary>
    public static readonly Regex CodeTrailing = new(
        @"\b([A-Z0-9][A-Z0-9_\-]{3,23})\b\s*(?:at\s+checkout|during\s+checkout|to\s+save|for\s+the\s+discount)",
        RegexOptions.Compiled);

    /// <summary>
    /// Words that look like codes and aren't. Every one of these has been seen shouting in a deal
    /// title, and each would print a confident checkout code the seller cannot use.
    /// </summary>
    public static readonly string[] NotACode =
    [
        "FREE", "SALE", "DEAL", "DEALS", "SAVE", "OFF", "CODE", "COUPON", "PROMO", "SHIP",
        "SHIPPING", "TODAY", "ONLY", "NEW", "HOT", "NOW", "OPEN", "BOX", "REFURB", "USED",
        "PRIME", "PLUS", "CLEARANCE", "BUNDLE", "GIFT", "CARD", "REBATE", "APP", "STORE",
        "USA", "HDMI", "USB", "SSD", "RTX", "GTX", "CPU", "GPU", "OLED", "LED", "TV",
    ];

    // ── What stops it applying to this item ──────────────────────────────────────────────────────

    /// <summary>
    /// The conditions that make a store-wide code not a store-wide code. Not a reason to drop the
    /// offer — the seller's item may well qualify — but a reason to say so on the row and to refuse
    /// it the top confidence grade, because this app cannot see which half of the catalogue an item
    /// is in.
    /// </summary>
    public static readonly (string Phrase, string Says)[] Exclusions =
    [
        ("select ", "only on selected items"),
        ("selected ", "only on selected items"),
        ("new customer", "new customers only"),
        ("first order", "first order only"),
        ("first-time", "first-time buyers only"),
        ("new account", "new accounts only"),
        ("member", "members only"),
        ("rewards", "loyalty members only"),
        ("credit card", "store card only"),
        ("in-store", "in store only"),
        ("in store only", "in store only"),
        ("app only", "in the app only"),
        ("app-only", "in the app only"),
        ("excludes", "has exclusions"),
        ("exclusions apply", "has exclusions"),
        ("excluding", "has exclusions"),
        ("not valid on", "has exclusions"),
        ("student", "students only"),
        ("military", "military only"),
        ("email sign", "needs an email signup"),
        ("newsletter", "needs an email signup"),
        ("subscribe", "needs a subscription"),
        ("clearance excluded", "not on clearance stock"),
        ("one per customer", "one per customer"),
        ("limited quantit", "limited quantities"),
    ];

    // ── The deadline ─────────────────────────────────────────────────────────────────────────────

    /// <summary>"exp 7/31", "expires 7/31/26", "ends 8/2". The form every code list writes.</summary>
    public static readonly Regex ExpiryDate = new(
        @"\b(?:exp(?:ires?|iration)?\.?|ends?|valid\s+thr(?:u|ough)|thr(?:u|ough)|until)\s*:?\s*" +
        @"(\d{1,2})[/\-](\d{1,2})(?:[/\-](\d{2,4}))?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"today only", "ends tonight", "expires today" — a deadline with no date on it.</summary>
    public static readonly Regex ExpiresToday = new(
        @"\b(today\s+only|ends\s+(?:today|tonight|at\s+midnight)|expires\s+(?:today|tonight)|" +
        @"last\s+day|final\s+hours|one\s+day\s+(?:only|sale))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Said outright to be over. Dropped rather than shown greyed out.</summary>
    public static readonly Regex Expired = new(
        @"\b(expired|no\s+longer\s+(?:valid|available|works?)|dead\s+code|code\s+(?:is\s+)?dead|" +
        @"offer\s+(?:has\s+)?ended|deal\s+is\s+(?:dead|over))\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Whether the entry is about coupons at all ────────────────────────────────────────────────

    /// <summary>
    /// What makes an entry a coupon entry rather than a deal. The browse feeds carry both, and a
    /// clearance item mentioning no code has nothing to contribute here — it is already priced on
    /// the board by <see cref="DealFeedService"/>.
    /// </summary>
    public static readonly Regex CouponWording = new(
        @"\b(coupon|promo\s*code|discount\s*code|offer\s*code|voucher|cash\s*back|cashback)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Credibility bounds ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most a store-wide code plausibly takes off. Above this the entry is a clearance headline
    /// ("70% off winter"), a category sale or a misparse — and a fabricated 70% cut to a cost basis
    /// would put an imaginary goldmine at the top of a profit ranking.
    /// </summary>
    public const decimal MaxCrediblePercentOff = 50m;

    /// <summary>
    /// The share of the item's price an amount-off code may take when the list stated no minimum
    /// spend. "$100 off" beside a $120 item is, essentially always, "$100 off $1,000" with the
    /// threshold written somewhere the parser couldn't see.
    /// </summary>
    public const decimal MaxUngatedDiscountShare = 0.5m;

    /// <summary>Cashback rates live in a narrow band; anything outside it was read off something else.</summary>
    public const decimal MinCredibleCashbackPercent = 0.5m;
    public const decimal MaxCredibleCashbackPercent = 20m;

    /// <summary>
    /// How old a code may be before it stops being evidence. Public lists never delete anything, so
    /// age is the only signal there is that a code has been sitting unused for a season.
    /// </summary>
    public const int StaleAfterDays = 45;

    /// <summary>
    /// A code with no stated deadline and this much age behind it is not offered a middle grade —
    /// see <see cref="CouponParser"/>. Kept separate from the above so the two can be tuned apart.
    /// </summary>
    public const int UndatedStaleAfterDays = 21;
}
