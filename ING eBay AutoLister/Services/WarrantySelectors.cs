using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every pattern the warranty finder matches with, in one file — the same posture as
/// <see cref="FreebieSelectors"/>, <see cref="DealFeedSelectors"/> and
/// <see cref="LiquidationSelectors"/>, and for the same reason: people word this a hundred ways and
/// tuning has to mean editing a string here rather than touching the detector.
/// </summary>
/// <remarks>
/// <para>
/// The word "warranty" is not the signal. It appears on listings that have none ("no warranty",
/// "warranty expired"), on listings advertising a plan the buyer would have to purchase, and inside
/// boilerplate that means nothing at all. So <see cref="NoCover"/> is tested before anything else,
/// exactly the way <see cref="FreebieSelectors.FreeDelivery"/> is stripped before anything is asked
/// about the word "free".
/// </para>
/// <para>
/// Plain "as is" is deliberately NOT a no-cover signal on its own. It is the most common two words
/// on any classifieds board and it is boilerplate there — the same judgement
/// <see cref="FreebieSelectors.FreeBecauseBroken"/> makes. Only the forms where the seller is
/// actually disclaiming ("as-is, no returns", "all sales final") count.
/// </para>
/// </remarks>
public static class WarrantySelectors
{
    // ── Refusal: the listing says there is no cover ───────────────────────────────────────────────

    /// <summary>
    /// Stated absence of cover. Tested first, so "no longer under warranty" can never be read by the
    /// presence patterns below as "under warranty".
    /// </summary>
    public static readonly Regex NoCover = new(
        @"\bno\s+(?:warranty|guarantee)\b|\bwithout\s+warranty\b|\bwarranty\s+(?:is\s+)?" +
        @"(?:expired|void(?:ed)?|ended|over|gone|up)\b|\bout\s+of\s+warranty\b|\bpast\s+warranty\b|" +
        @"\bno\s+longer\s+(?:under|in)\s+warranty\b|\bwarranty\s+(?:has\s+)?(?:expired|run\s+out)\b|" +
        @"\bexpired\s+warranty\b|\bunwarranted\b|\bnon[\s-]?warrant(?:y|ied)\b|" +
        @"\ball\s+sales\s+(?:are\s+)?final\b|\bas[\s-]?is[\s,;]+(?:no\s+(?:returns?|refunds?|warranty|guarantee))\b|" +
        @"\bno\s+returns?\s*,?\s*no\s+refunds?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The plan is being SOLD, not included. "Add a 3-year protection plan", "warranty available for
    /// purchase" — the item does not come with it, and reading these as cover would put a warranty on
    /// every Best Buy row on the board.
    /// </summary>
    public static readonly Regex CoverForSale = new(
        @"\b(?:warranty|protection\s+plan|service\s+plan|coverage)\s+(?:is\s+)?" +
        @"(?:available|offered|optional|extra|additional|for\s+(?:purchase|sale)|can\s+be\s+(?:added|purchased))\b|" +
        @"\b(?:add|buy|purchase|get)\s+(?:a\s+|an\s+)?(?:\d+[\s-]?(?:year|yr|month)s?\s+)?" +
        @"(?:extended\s+)?(?:warranty|protection\s+plan)\b|\bwarranty\s+(?:sold|priced)\s+separately\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Presence: the listing says there IS cover ─────────────────────────────────────────────────

    /// <summary>
    /// Cover, stated. Kept broad because the refusals above have already run — what reaches here is
    /// text where "warranty" is not being denied and not being sold.
    /// </summary>
    public static readonly Regex HasCover = new(
        @"\b(?:still\s+)?(?:under|in|has|have|with|w/|includes?|including|comes?\s+with|carries|" +
        @"remaining|balance\s+of(?:\s+the)?)\s+(?:a\s+|the\s+|its\s+|full\s+|original\s+)?" +
        // Each qualifier carries its own trailing space, so "under factory warranty" matches. Folding
        // the space into the alternation instead lets the group consume "factory" and then demand
        // "warranty" with no gap, which quietly fails on the commonest phrasing there is.
        @"(?:(?:factory|manufacturer'?s?|mfg|mfr|oem|limited|\d+[\s-]?(?:year|yr|month|mo|day)s?)\s+)*warranty\b|" +
        // A term standing on its own in front of the word — "90 day warranty", "1 year factory
        // warranty". The commonest way a shop states cover, and it has no verb in front of it for
        // the alternation above to hook onto.
        @"\b\d{1,3}\s*[-\s]?\s*(?:year|yr|yrs|month|months|mo|mos|day|days)\s+" +
        @"(?:manufacturer'?s?\s+|factory\s+|seller\s+|limited\s+|parts\s+|full\s+)?warranty\b|" +
        @"\bwarrant(?:y|ied|ies)\s+(?:until|thru|through|till|to|good|valid|active|remaining|left|" +
        @"expires?|ends?|runs?)\b|\bunder\s+warranty\b|\bwarrantied\b|\bstill\s+covered\b|" +
        @"\bwarranty\s*:?\s*(?:yes|active|valid|current)\b|\bfull\s+warranty\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Whose warranty it is: the factory's rather than the person selling it.</summary>
    public static readonly Regex ManufacturerBacked = new(
        @"\b(?:factory|manufacturer'?s?|mfg|mfr|oem|original|apple|dell|hp|lenovo|samsung|lg|sony)\s*" +
        @"(?:limited\s+)?warranty\b|\bwarranty\s+(?:from|by|through)\s+(?:the\s+)?(?:manufacturer|factory|maker)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A bought plan, named. These outlast the factory term and are usually tied to the device, which
    /// is why they get their own kind rather than being folded into the manufacturer's.
    /// </summary>
    public static readonly Regex ExtendedPlan = new(
        @"\bapple\s?care\+?\b|\bsquare\s?trade\b|\bgeek\s*squad\s+(?:protection|plan)\b|" +
        @"\basurion\b|\ballstate\s+protection\b|\bextended\s+warranty\b|\bprotection\s+plan\b|" +
        @"\bservice\s+(?:plan|contract)\b|\bcare\s?pack\b|\bpremium\s+care\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The person selling it is the warranty. Real cover for the reseller, worth nothing to whoever
    /// they sell it to — which is the whole reason this is told apart from the factory's.
    /// </summary>
    public static readonly Regex SellerBacked = new(
        @"\b(?:my|our|seller|shop|store|in[\s-]?house|personal)\s+(?:own\s+)?(?:warranty|guarantee)\b|" +
        @"\bseller\s+warrant(?:y|ied)\b|\b(?:\d{1,3})[\s-]?day\s+(?:warranty|guarantee|money\s+back)\b|" +
        @"\bguaranteed?\s+(?:working|not\s+(?:doa|dead)|to\s+work)\b|\bwe\s+(?:warranty|guarantee)\b|" +
        @"\btested\s+and\s+guaranteed\b|\breturn\s+it\s+if\s+it\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The cover is explicitly said to survive a resale — or explicitly said not to.</summary>
    public static readonly Regex Transferable = new(
        @"\b(?:fully\s+)?transfer(?:able|rable|s)\b|\bwarranty\s+transfers?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Regex NonTransferable = new(
        @"\bnon[\s-]?transfer(?:able|rable)\b|\bnot\s+transfer(?:able|rable)\b|" +
        @"\boriginal\s+(?:purchaser|owner|buyer)\s+only\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A receipt, invoice or proof of purchase. Never worth money on its own and always worth saying:
    /// it is what turns a warranty claim from an argument into a form.
    /// </summary>
    public static readonly Regex ProofOfPurchase = new(
        @"\b(?:original\s+)?receipt\b|\bproof\s+of\s+purchase\b|\binvoice\b|\border\s+confirmation\b|" +
        @"\bregistered\s+(?:to|with)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── How long ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stated term: "1 year warranty", "warranty: 90 days", "3-yr manufacturer warranty",
    /// "6 months left on the warranty".
    /// </summary>
    public static readonly Regex Term = new(
        @"\b(\d{1,3})\s*[-\s]?\s*(year|yr|yrs|month|months|mo|mos|day|days)\b" +
        @"[^.\n]{0,28}?\bwarrant(?:y|ied)\b|" +
        @"\bwarrant(?:y|ied)\b[^.\n]{0,28}?\b(\d{1,3})\s*[-\s]?\s*(year|yr|yrs|month|months|mo|mos|day|days)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A stated end date on the cover: "warranty until 3/2027", "covered through March 2027",
    /// "warranty expires 12/25/26". Group 1 is a numeric date, groups 2/3 a named month and year.
    /// </summary>
    public static readonly Regex CoverUntil = new(
        @"\b(?:warrant(?:y|ied)|coverage|covered|applecare\+?|protection)\b[^.\n]{0,30}?" +
        @"\b(?:until|thru|through|till|to|expires?(?:\s+on)?|ends?(?:\s+on)?|good\s+(?:until|thru|through)|" +
        @"valid\s+(?:until|thru|through))\s*:?\s*" +
        // The month/year form is tried first on purpose: against "3/2027" a day-first alternation
        // matches "3/20" and silently turns a 2027 expiry into a date three weeks from now.
        @"(?:(\d{1,2}[/\-]\d{4}|\d{1,2}[/\-]\d{1,2}(?:[/\-]\d{2,4})?)|" +
        @"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+(\d{4}))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// When it was bought, stated as a date: "bought in March 2025", "purchased 06/2024",
    /// "ordered Jan 2026".
    /// </summary>
    public static readonly Regex PurchasedOn = new(
        @"\b(?:bought|purchased|ordered|got\s+it|picked\s+(?:it\s+)?up|delivered)\b[^.\n]{0,20}?" +
        @"(?:(\d{1,2}[/\-]\d{4}|\d{1,2}[/\-]\d{1,2}[/\-]\d{2,4})|" +
        @"(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\.?,?\s+(\d{4}))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>How long ago it was bought, stated in words: "bought 3 months ago", "purchased last week".</summary>
    public static readonly Regex PurchasedAgo = new(
        @"\b(?:bought|purchased|ordered|got\s+it|owned\s+(?:it\s+)?for|had\s+(?:it\s+)?(?:for|about))\b" +
        @"[^.\n]{0,16}?\b(?:(\d{1,2})|(a|an|last|this))\s*(week|weeks|month|months|year|years)\b(?:\s+ago)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Condition ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Refurbished / renewed / open-box wording. The supply this whole board exists for: items sold
    /// as used that carry cover anyway.
    /// </summary>
    public static readonly Regex RefurbCondition = new(
        @"\b(certified\s+refurbished|manufacturer\s+refurbished|factory\s+refurbished|" +
        @"seller\s+refurbished|refurbished|refurb(?:'?d)?|renewed|recertified|re-?certified|" +
        @"open[\s-]?box|openbox|scratch\s*(?:&|and)\s*dent|b[\s-]?stock)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Never opened. Its cover has not started running, which makes it the one used-listing shape
    /// where a full factory term is a reasonable inference rather than a guess.
    /// </summary>
    public static readonly Regex Sealed = new(
        @"\b(brand\s+new(?:\s+in\s+box)?|new\s+in\s+(?:box|sealed\s+box)|nib|bnib|nwt|" +
        @"factory\s+sealed|sealed\s+(?:in\s+)?box|still\s+sealed|never\s+(?:opened|used)|unopened)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Credibility bounds ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most cover this app will ever credit, in months. Long factory terms exist — five-year tool
    /// batteries, ten-year grills — and a resale premium does not keep scaling with them: nobody pays
    /// twice as much for eight years of cover as for four. Three years is where the argument stops
    /// being about the warranty and starts being about the item.
    /// </summary>
    public const int MaxCreditedMonths = 36;

    /// <summary>
    /// Cover shorter than this is not a selling point. A fortnight of factory warranty left is a fact
    /// about the item, not a reason anybody pays more for it.
    /// </summary>
    public const int MinCreditedMonths = 1;

    /// <summary>
    /// The ceiling on what a warranty may add to a resale estimate, as a percentage. Deliberately
    /// modest: the sold comps behind the estimate already contain covered units, so this is a premium
    /// over a blended average and not over an uncovered one.
    /// </summary>
    public const decimal MaxUpliftPercent = 10m;

    /// <summary>
    /// The ceiling in dollars, applied after the percentage. Without it a 10% uplift on a $2,400 miner
    /// would put $240 of unverified prose into the profit ranking, which is exactly the kind of number
    /// this app refuses to print. A warranty is worth a real but bounded amount.
    /// </summary>
    public const decimal MaxUpliftDollars = 75m;

    /// <summary>
    /// Sold comps below this count, or confidence below <see cref="MinUpliftConfidence"/>, and no
    /// uplift is applied at all. The same bar the goldmine badge is held to — a premium computed on
    /// top of a price nobody believes is two guesses stacked on each other.
    /// </summary>
    public const int MinUpliftComps = 3;

    /// <summary>See <see cref="MinUpliftComps"/>.</summary>
    public const int MinUpliftConfidence = 50;

    /// <summary>
    /// The buy price above which a stated "as-is, no returns" is worth holding a verdict down for.
    /// Under it the whole outlay is smaller than one bad flip's shipping, and the warning would fire
    /// on nearly every row on a classifieds board.
    /// </summary>
    public const decimal AsIsRiskThreshold = 150m;

    /// <summary>
    /// How much of a listing's body text is read. The condition, the purchase date and the warranty
    /// wording are in the opening paragraph of any listing that has them; past that it is shipping
    /// boilerplate and forum chatter. Same cut, and the same reason, as
    /// <see cref="FreebieClassifier"/>'s opening-line rule.
    /// </summary>
    public const int MaxDetailChars = 600;
}
