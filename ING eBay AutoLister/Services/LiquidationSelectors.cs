using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every pattern the liquidation parser matches with, in one file — the same posture as
/// <see cref="DealFeedSelectors"/> and <see cref="FacebookMarketplaceSelectors"/>, and for the same
/// reason: thousands of independent auction houses each write their lot titles their own way, so
/// tuning this scanner has to mean editing a string here rather than touching the parser.
///
/// <para>Two questions are being answered, and both of them are about refusing:</para>
/// <list type="bullet">
///   <item><b>How many things is this?</b> "Lot of 8 Rorsou Corded Headphones" priced against one
///   headphone comp is wrong by 8x in the direction that invents a goldmine. A count is used only
///   when the listing STATED one.</item>
///   <item><b>Is it one identifiable thing at all?</b> "PALLET OF VARIOUS NEW USED PARTS REPAIR AS
///   IS" has no product to look up, and any comp the matcher finds for it is an accident.</item>
/// </list>
///
/// <para>The thresholds below are not guesses. They were measured against 801 live auction lots
/// pulled across several queries while this was being built, and the counts that decided each rule
/// are recorded next to it.</para>
/// </summary>
public static class LiquidationSelectors
{
    // ── How many units ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The dominant multi-unit convention at auction: a bracketed count in front of the product.
    /// "(4) DeWalt Grinders", "(12) liq fert coulters".
    /// </summary>
    /// <remarks>
    /// Measured: 62 of 801 live lots (7.7%) led with this, versus 9 (1.1%) written as "Lot of N".
    /// Reading only the wordier form would have missed seven eighths of the multi-unit stock — and
    /// missing a count is not a harmless omission, it prices eight units as one.
    /// </remarks>
    public static readonly Regex LeadingCount =
        new(@"^\s*\(\s*(\d{1,4})\s*\)\s*", RegexOptions.Compiled);

    /// <summary>
    /// A bracketed count anywhere in the title, not just at the front: "LOT OF (2) DeWalt Rotary
    /// Hammer Drills".
    /// </summary>
    /// <remarks>
    /// Read ONLY when the title already says "lot of" / "pallet of" — see
    /// <see cref="LiquidationParser.ReadUnits"/>. That context is what makes it safe: a bare "(2)"
    /// in the middle of an arbitrary title could be anything, but inside bulk wording it is a
    /// quantity. Found by running the parser over 661 real lots, where this exact title was being
    /// refused as "no count stated" while stating its count perfectly clearly.
    /// </remarks>
    public static readonly Regex BracketedCount =
        new(@"\(\s*(\d{1,3})\s*\)", RegexOptions.Compiled);

    /// <summary>"Lot of 8 …", "Lots of 3 …", "Case of 12 …", "Box of 24 …".</summary>
    public static readonly Regex CountOf = new(
        @"\b(?:lots?|cases?|boxes?|box|sets?|bundles?|pallets?)\s+of\s+(\d{1,4})\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"24 pcs", "8 units", "50 count", "12-pack".</summary>
    public static readonly Regex CountUnits = new(
        @"\b(\d{1,4})\s*[-\s]?(?:pcs?|pieces?|units?|ct|count|packs?|pks?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>"Qty 40", "QTY: 12", "Quantity 6".</summary>
    public static readonly Regex CountQty =
        new(@"\bqty|\bquantity\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Regex CountQtyValue =
        new(@"\b(?:qty|quantity)\.?\s*:?\s*(\d{1,4})\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Bulk wording that says "this is more than one" without saying how many — "pallet of",
    /// "truckload", "case of" with no figure.
    /// </summary>
    /// <remarks>
    /// Note this requires "pallet OF". A bare "pallet" is emphatically NOT a bulk signal: of the 37
    /// live lots whose titles contained the word, nearly all of them were pallet <i>jacks</i>,
    /// pallet <i>forks</i>, pallet <i>racks</i> and a pallet <i>shed</i> — single products that
    /// happen to be named after the thing. Treating those as pallets of stock would have multiplied
    /// a pallet jack's resale by an invented unit count.
    /// </remarks>
    public static readonly Regex BulkWithoutCount = new(
        @"\b(?:pallets?\s+of|truck\s?loads?|skids?\s+of|crates?\s+of|bulk\s+lot|wholesale\s+lot|" +
        @"lots?\s+of|cases?\s+of|boxes\s+of)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The largest unit count worth believing off a title. Above this the figure is a model number,
    /// a wattage or a year that the count patterns caught by accident, and multiplying a resale
    /// price by it would put a fictional five-figure row at the top of the ranking.
    /// </summary>
    public const int MaxCredibleUnits = 500;

    // ── Whether there is a product here at all ───────────────────────────────────────────────────

    /// <summary>
    /// Wording that says the lot's contents are not one identifiable product. Measured at 78 of 801
    /// live lots (9.7%) — nearly a tenth of the board, and every one of them would otherwise be
    /// priced against whatever comp the matcher happened to find for the word "assorted".
    /// </summary>
    public static readonly Regex AssortedContents = new(
        @"\b(?:assorted|assortment|various|variety|miscellaneous|misc\.?|mixed|sundry|" +
        @"grab\s?bag|mystery\s?box|as\s+pictured|see\s+photos?|contents\s+of|" +
        @"and\s+more|&\s+more|etc\.?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A title that lists several different things rather than naming one — "(3) NASCAR Headphones,
    /// New Glue Gun, Small Tripod". Real, and common enough to matter: a comma or ampersand joining
    /// two noun phrases inside a multi-unit lot means the units are not all the same product, so
    /// there is no single per-unit comp to multiply.
    /// </summary>
    /// <remarks>
    /// Applied ONLY to multi-unit lots. On a single item a comma is ordinary punctuation
    /// ("Sony WH-1000XM4, Black") and refusing on it would empty the board for no gain.
    /// </remarks>
    public static readonly Regex MixedContentsList = new(
        @"[A-Za-z]{3,}\s*(?:,|\s&\s|\sand\s)\s*(?:new\s+|used\s+)?[A-Za-z]{3,}\s+[A-Za-z]{3,}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Broken, incomplete or salvage stock. There is no honest comp for it: the sold history it
    /// would be priced against is history for items that work.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. "As-is" is NOT in here even though it reads like it belongs: it appeared
    /// in 56 of 801 live lots (7%), because it is boilerplate half the auction houses staple to
    /// every lot they list, and refusing on it would delete a large slice of a board where most of
    /// the items are perfectly fine. "For parts" and "not working", by contrast, appeared once each
    /// — they are said only when they are meant.
    /// </remarks>
    public static readonly Regex ForPartsOnly = new(
        @"\b(?:for\s+parts|parts\s+only|not\s+working|does\s+not\s+work|non[\s-]?working|" +
        @"doesn'?t\s+work|for\s+repair|needs?\s+repair|salvage|scrap|junk|broken|incomplete)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Things an eBay seller cannot flip, whatever the price. Firearms, ammunition and alcohol are
    /// prohibited outright; a vehicle, a trailer, real estate or livestock is not an eBay listing at
    /// all. Auction catalogues are full of all of them, and a scan that ranks a $200 rifle as a
    /// goldmine has produced a number nobody can act on.
    /// </summary>
    public static readonly string[] NotFlippablePhrases =
    [
        "firearm", "rifle", "shotgun", "handgun", "pistol", "revolver", "ammunition", "ammo",
        "gun safe" /* sold with contents at these sales more often than not */,
        "alcohol", "whiskey", "bourbon", "vodka", "tequila", "wine lot", "case of wine", "liquor",
        "tobacco", "cigarette", "vape", "e-liquid",
        "real estate", "acreage", "parcel", "land auction", "mobile home", "storage unit",
        "vehicle", "automobile", "pickup truck", "trailer vin", "atv", "utv", "tractor",
        "livestock", "cattle", "horses", "poultry",
        "prescription", "controlled substance", "hazmat", "hazardous",
        "gift card", "gift certificate", "lottery",
    ];

    // ── Condition grade ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Wording mapped to one of <see cref="LotAnalyzer.Grades"/>, most specific first. Only ever
    /// applied to a multi-unit lot, where the grade answers "how many of these N are dead" — the
    /// question a single item does not have.
    /// </summary>
    /// <remarks>
    /// A bare "new" maps to <c>shelf_pull</c> rather than <c>new</c>, on purpose. The word appeared
    /// in 122 of 801 live lots (15%) and at an auction it is a description of the packaging far more
    /// often than a guarantee of a factory seal. The stronger cues — sealed, NIB, NOS, "brand new" —
    /// get the full <c>new</c> grade. Both directions cost money, and understating recovery costs a
    /// missed lot while overstating it costs a bought one.
    /// </remarks>
    public static readonly (Regex Pattern, string GradeId)[] GradeCues =
    [
        (new(@"\b(?:salvage|scrap|damaged|for\s+parts|parts\s+only|broken)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "salvage"),
        (new(@"\b(?:uninspected|unmanifested|unsorted|unprocessed|raw\s+returns|untested)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "uninspected_returns"),
        (new(@"\b(?:customer\s+returns?|returns?\s+pallet|rma|tested\s+returns?)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "customer_returns"),
        (new(@"\b(?:open\s?box|opened|box\s+damage|damaged\s+packaging)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "open_box"),
        (new(@"\b(?:brand\s+new|new\s+in\s+box|nib|nos|factory\s+sealed|sealed|unopened)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "new"),
        (new(@"\b(?:shelf\s+pulls?|overstock|closeout|liquidation|surplus|store\s+stock|new)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "shelf_pull"),
    ];

    /// <summary>
    /// What a multi-unit lot is graded as when the wording says nothing. "Mixed / as-is / untested"
    /// — 60% sellable, 82% of the comp price — because an auction lot whose condition nobody stated
    /// is exactly that, and the alternative is assuming the best about stock sold sight-unseen.
    /// </summary>
    public const string DefaultLotGradeId = "mixed";

    // ── The retail-value claim ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// "$4,200 retail value", "MSRP $199", "Retail: $1,899". Appeared in 45 of 801 live lots (5.6%).
    /// Never used as a value — used exactly as a manifest's retail column is
    /// (<see cref="LotAnalyzer.RetailSanityCheck"/>), as a cross-check that the comp matcher found
    /// the right product rather than an accessory or an unrelated item.
    /// </summary>
    public static readonly Regex ClaimedRetailBefore = new(
        @"\b(?:retail(?:\s+value|\s+price)?|msrp|list\s+price|value)\s*(?:of|:|is|at)?\s*" +
        @"\$\s?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Regex ClaimedRetailAfter = new(
        @"\$\s?(\d{1,3}(?:,\d{3})+(?:\.\d{1,2})?|\d+(?:\.\d{1,2})?)\s*" +
        @"(?:\+\s*)?(?:in\s+)?(?:retail|msrp|value)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The premium as the auctioneer printed it — "15% Buyers Premium", "18.00 % Auctioneer's fees",
    /// "13% BP + $1 handling fee per won lot". Read only when the machine-readable rate is missing,
    /// and never past the first percentage: the second figure in "15% BP + 3% CC surcharge" is a
    /// different charge on a different base.
    /// </summary>
    public static readonly Regex PremiumPercentInText =
        new(@"(\d{1,2}(?:\.\d{1,2})?)\s*%", RegexOptions.Compiled);

    // ── The event ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An auction that exists because a business is emptying itself, as opposed to an ordinary
    /// weekly consignment sale. This is what the feature is named after — it does not change a
    /// single number, it decides which rows are worth pointing at.
    /// </summary>
    public static readonly Regex LiquidationEvent = new(
        @"\b(?:liquidation|liquidating|closeout|close[\s-]?out|going\s+out\s+of\s+business|" +
        @"\bgob\b|store\s+clos(?:ing|ure)|business\s+clos(?:ing|ure)|final\s+sale|" +
        @"everything\s+must\s+go|overstock|surplus|customer\s+returns?|shelf\s+pulls?|" +
        @"bankrupt(?:cy)?|receivership|dispersal|forced\s+sale|inventory\s+reduction|" +
        @"warehouse\s+clear(?:ance|out)|retail\s+returns?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Title tidying ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The auction house's own numbering, when it leaks into the lot title: "Lot 79 |", "#412 -",
    /// "Item 22:". Noise to the comp matcher, and a number it would otherwise take for a model.
    /// </summary>
    public static readonly Regex LeadingLotNumber = new(
        @"^\s*(?:lot|item|#)\s*#?\s*\d{1,5}\s*[|:.\-–—]\s*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Condition and disclaimer wording stapled to the end of a title. Comes off so the comp lookup
    /// runs on the product — the grade has already been read from it by the time this is applied.
    /// </summary>
    public static readonly Regex TrailingConditionTail = new(
        @"\s*(?:[-–—|,(]\s*)?(?:as[\s-]?is(?:,?\s+where[\s-]?is)?|no\s+returns?|sold\s+as\s+is|" +
        @"untested|unte[s]?ted|preowned|pre[\s-]?owned|used|new)\s*\)?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Collapses the runs of spaces that stripping leaves behind.</summary>
    public static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);

    public static readonly Regex NonWord = new(@"[^a-z0-9]+", RegexOptions.Compiled);

    // ── Price credibility ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The placeholder every lot on the feed carries in its <c>bidAmount</c> field.
    ///
    /// Verified live across 801 lots on eight separate searches: <b>every single one</b> reported
    /// <c>bidAmount: 123.45</c>. It is a sentinel the site fills in client-side, not a price, and
    /// reading it as one would have given every row on this board the same fabricated $123.45 cost
    /// basis — profitable-looking on the expensive items and catastrophic on the cheap ones. The
    /// real money is in <c>lotState.highBid</c> and <c>lotState.minBid</c>, and this constant exists
    /// so the parser can say out loud what it is refusing.
    /// </summary>
    public const decimal SentinelBidAmount = 123.45m;

    /// <summary>
    /// Opening bids of $1 are completely normal at these sales — often the whole point — so the
    /// floor is well below the deal feeds'. The ceiling is where a "bid" stopped being a bid and
    /// became a parsing accident.
    /// </summary>
    public const decimal MinCredibleBid = 0.5m;
    public const decimal MaxCredibleBid = 250_000m;

    // ── Block detection ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A 200 that isn't an answer. Same rule and same reason as <see cref="DealFeedParser.DetectBlock"/>:
    /// a challenge page parses to zero lots, which reads as "no liquidation stock matches" — an
    /// answer that looks like an answer, and the one failure mode worth detecting.
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
        "just a moment...",
    ];

    public const int BlockScanChars = 4000;
}
