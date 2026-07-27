using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Every pattern the freebie finder matches with, in one file — the same posture as
/// <see cref="DealFeedSelectors"/> and <see cref="LiquidationSelectors"/>, and for the same reason:
/// these sites reword constantly, so tuning has to mean editing a string here rather than touching
/// the parser.
/// </summary>
/// <remarks>
/// <para>
/// Almost all of this exists to answer one question honestly: <b>is this thing actually free?</b>
/// The word "free" is the most abused token on the internet and it is nearly always attached to
/// something that is not the item. Of the deal-feed entries whose titles contain it, the
/// overwhelming majority are <i>free shipping</i> on a full-price product. Reading any of those as
/// a $0 cost basis puts a fabricated 100%-margin goldmine at the very top of a profit ranking,
/// which is the single most expensive thing this app could do.
/// </para>
/// <para>
/// So the rule is inverted from every other board: a listing is <b>not</b> free until something
/// says it is, and every one of the many phrases that only look like they say it is refused first.
/// </para>
/// </remarks>
public static class FreebieSelectors
{
    // ── What "free" is attached to, when it isn't the item ────────────────────────────────────────

    /// <summary>
    /// The free thing is the DELIVERY, not the product. Far and away the most common use of the
    /// word on any deal feed — a live pull of six Slickdeals "free" searches this session returned
    /// 81 distinct titles, and the great majority of the ones containing "free" were this.
    /// </summary>
    /// <remarks>
    /// Stripped out of the text before any free-signal test runs, rather than used as a refusal:
    /// "Free 65in TV + free shipping" is a genuine freebie whose title happens to contain both.
    /// </remarks>
    public static readonly Regex FreeDelivery = new(
        NotHyphenated + @"\bfree\s+(?:2-?day\s+)?(?:shipping|ship(?:ping)?\s+to\s+store|ship\b|shipped|delivery|" +
        @"store\s+pickup|pickup\s+(?:at|on)|s\s*&\s*h|returns?|installation|assembly)\b|" +
        @"\bship(?:s|ped)?\s+free\b|" + NotHyphenated + @"\bfree\s+ship\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Guards every "free" pattern in this file against the hyphenated compounds, where the word is
    /// part of a product name and nothing is being given away: <b>BPA-free</b> jugs, <b>oil-free</b>
    /// face wash, <b>aluminum-free</b> deodorant, sugar-free, gluten-free, fragrance-free,
    /// hands-free. All four of the first ones came back in a live scan this session, and each was
    /// read as a free item and had the word cut out of its own title — "2 1 gallon square BPA- jugs"
    /// is not a product any comp lookup will ever match.
    /// </summary>
    private const string NotHyphenated = @"(?<![A-Za-z]-)";

    /// <summary>
    /// The free thing is a BONUS on a purchase — "free gift w/ $75", "buy one get one free",
    /// "+ Free Bonus Gift". The item still costs whatever the qualifying purchase costs, so there
    /// is no $0 cost basis anywhere in it. All four of these forms were in the live sample.
    /// </summary>
    /// <remarks>
    /// The buy-one-get-one alternative allows words between "buy one" and "get one" because that is
    /// how the offers are actually written — "Buy One <b>Footlong</b> Get One Free" came back live
    /// and slipped through a pattern that required the two halves to be adjacent.
    /// </remarks>
    public static readonly Regex FreeWithPurchase = new(
        NotHyphenated + @"\bfree\s+(?:gift|bonus|item|sample|drink|coffee|meal|entree|appetizer)\b|" +
        NotHyphenated + @"\bfree\s+w/?\s*(?:purchase|qualifying|order|sign\s*up|subscription|trial)|" +
        @"\bbonus\s+gift\b|\bwyb\b|\bwith\s+purchase\b|\bw/\s*purchase\b|" +
        @"\bbuy\s+(?:one|1|two|2)\b[^,.]{0,24}?\bget\s+(?:one|1|two|2)?\s*free\b|\bb\d ?g\d\b|\bbogo\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The free thing is time, access or a subscription rather than an object. Nothing here has a
    /// sold comp, and most of it cannot be transferred to a buyer at all.
    /// </summary>
    public static readonly Regex FreeButNotAnObject = new(
        NotHyphenated + @"\bfree\s+(?:trial|month|months|year|shipping\s+club|membership|delivery\s+pass|" +
        @"credit|cash|money|quote|estimate|consultation|admission|entry|parking|checked\s+bags?)\b|" +
        // "daily free deals free $$" — live. Free money is not a free object, and it has no comp.
        NotHyphenated + @"\bfree\s+\$" +
        @"|\bmonth\s+free\b|\bdays?\s+free\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// What is left after the three patterns above have been stripped: the item itself is free.
    /// "Free cabinets", "COUCH - BIG AND COMFORTABLE AND FREE", "Curb Alert — Help Yourself".
    /// </summary>
    public static readonly Regex FreeItemSignal = new(
        NotHyphenated + @"\bfree(?:bie)?\b|\bgiving\s+away\b|\bgive\s?away\b|\bgratis\b|\bno\s+charge\b|" +
        @"\bhelp\s+yourself\b|\bcurb\s*alert\b|\byours\s+if\s+you\s+(?:want|haul|pick)\b|" +
        @"\b100%\s*off\b|\bfor\s+\$?0(?:\.00)?\b|\bzero\s+cost\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Free, but only after something happens ───────────────────────────────────────────────────

    /// <summary>
    /// Free once a rebate is claimed and paid. The seller fronts the money — see
    /// <see cref="FreebiePricer"/>, which refuses to treat this as the same trade as a curb pickup.
    /// </summary>
    /// <remarks>
    /// <c>AR</c> is Slickdeals' own abbreviation ("2 for $0.38 AC + AR", live in this session's
    /// sample) and is matched only as a standalone token, because it is two of the commonest
    /// letters in English.
    /// </remarks>
    public static readonly Regex AfterRebate = new(
        @"\bafter\s+(?:\$?[\d,.]+\s+)?(?:mail[\s-]?in\s+)?rebate\b|\bfree\s+after\s+rebate\b|" +
        @"\bmail[\s-]?in\s+rebate\b|\bF\.?A\.?R\.?\b|(?<![A-Za-z])AR(?![A-Za-z])|\brebate\b",
        RegexOptions.Compiled);

    /// <summary>Free only if a code is typed at checkout — free if it works, full price if it doesn't.</summary>
    public static readonly Regex AfterCoupon = new(
        @"\bafter\s+(?:\$?[\d,.]+\s+)?(?:coupon|code|promo|clip(?:ped)?\s+coupon)\b|" +
        @"\bfree\s+(?:w/?|with)\s+code\b|(?<![A-Za-z])AC(?![A-Za-z])",
        RegexOptions.Compiled);

    /// <summary>The rebate's size, when the listing stated it: "$50 Rebate", "after $10 Rebate".</summary>
    public static readonly Regex RebateAmount = new(
        @"\$\s?(\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?)\s*(?:mail[\s-]?in\s+)?rebate\b|" +
        @"\brebate\s*(?:of|:)?\s*\$\s?(\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>How the money comes back, when it is named. Venmo/PayPal rebates were live in the sample.</summary>
    public static readonly Regex RebateVia = new(
        @"\b(venmo|paypal|ibotta|fetch|checkout\s?51|rakuten)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Things that are free but are not an item to flip ─────────────────────────────────────────

    /// <summary>
    /// Free, genuinely, and worth nothing on eBay. Measured against 186 live Craigslist free-stuff
    /// posts pulled this session: bulk materials and haul-away offers were the largest single slice
    /// of the board — firewood, dirt, granite, drywall, scrap metal, pallets, moving boxes,
    /// railroad ties, used tires. Every one has no sold comp and costs more to ship than it fetches.
    /// </summary>
    public static readonly string[] NoResaleValuePhrases =
    [
        // Bulk materials and haul-away. All observed live.
        "firewood", "fire wood", "wood chips", "mulch", "topsoil", "top soil", "free dirt", "fill dirt",
        "gravel", "sand pile", "concrete", "drywall", "sheetrock", "lumber scrap", "scrap metal",
        "scrap steel", "scrap wood", "railroad tie", "used tires", "brush pile", "yard waste",
        "pallets", "wooden pallet", "moving boxes", "cardboard boxes", "packing peanuts", "egg carton",
        "shipping boxes", "manure", "horse manure", "compost", "rocks", "boulders", "pavers",
        "haul away", "haul it away", "must take all", "you haul", "u-haul it", "tear down", "demo",
        // Consumables and perishables.
        "free food", "free coffee", "free donut", "free pizza", "free sample", "samples",
        "hand sanitizer", "produce", "groceries", "leftover", "expired",
        // Living things.
        "rooster", "chickens", "kittens", "puppies", "free to good home", "rehome", "aquarium fish",
        // Not an object at all.
        "vending machine placed", "placed on your business", "free estimate", "free quote",
        "free consultation", "free class", "free lesson", "free workshop", "free seminar",
        // Digital and non-transferable. "kindle book" rather than "kindle ebook" because the live
        // sample said "Free Kindle Books", and eBay does not let anyone resell either one.
        "kindle book", "ebook", "e-book", "audiobook", "free game", "steam key", "epic games",
        "ps plus", "xbox game pass", "free app", "free download", "digital code", "streaming",
        "free movie", "printable", "digital title", "digital copy",
        // Groceries and toiletries. Near-free by the caseload and worth nothing on eBay: a live
        // scan this session came back with mustard, deodorant, face wash, cat litter liners and
        // peanuts, every one of which is a real deal and none of which is a flip.
        "mustard", "ketchup", "mayonnaise", "cereal", "candy", "snack", "soda", "coffee pods",
        "deodorant", "antiperspirant", "shampoo", "conditioner", "body wash", "face wash",
        "toothpaste", "mouthwash", "razor", "shave gel", "lotion", "body oil", "serum",
        "diapers", "wipes", "paper towel", "toilet paper", "detergent", "dish soap",
        "cleaner", "degreaser", "disinfectant", "litter box liner", "cat litter",
        "vitamins", "supplement", "protein bar", "medicine", "caplets", "tablets 24",
        "peanut", "smoothie", "fruit snack", "juice box", "granola",
        // Restaurant and chain promotions: a day of discounted burgers is not stock.
        "day deals", "participating locations", "participating restaurants", "restaurants)",
        "digital movie", "movies anywhere", "vudu", "fandango",
        // Entering something is not receiving something.
        "sweepstakes", "enter to win", "chance to win", "raffle", "contest entry",
        // Gift cards and store credit are money, not stock.
        "gift card", "giftcard", "egift", "e-gift", "gift certificate", "store credit",
    ];

    /// <summary>
    /// A post that is a pile rather than a product. Observed live: "Cleaning out garage and have a
    /// lot of free stuff", "Free stuff pile - delwood st", "FREE Curb Alert — Help Yourself!".
    /// Genuinely free, genuinely worth going to, and impossible to price — there is no single item
    /// to look up a comp for, so the row would be a confident number about nothing.
    /// </summary>
    public static readonly Regex PileNotAProduct = new(
        @"\b(?:free\s+stuff|stuff\s+pile|curb\s*alert|cleaning\s+out|garage\s+clean|estate\s+clean|" +
        @"everything\s+must\s+go|whatever\s+is\s+left|lots?\s+of\s+free|misc\s+free|" +
        @"multiple\s+items|various\s+items|odds\s+and\s+ends)\b|^\s*free\s*[.!]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Why the thing is free: it is broken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately wider than <see cref="LiquidationSelectors.ForPartsOnly"/>, which this also
    /// applies. That one was tuned narrow against auction catalogues, where "for parts" is said only
    /// when it is meant and refusing on softer words would gut the board. A free board is the
    /// opposite: damage is the single commonest reason something is being given away, and people say
    /// so plainly in the title. Four of the 186 live free posts pulled this session did exactly that
    /// — "Damaged panel/screen", "good for parts", "Repair or Project", "Scrap Metal/Parts" — and the
    /// first of those is a 75" television that would otherwise be priced against sold comps for
    /// televisions that work.
    /// </para>
    /// <para>
    /// "As-is" is still excluded, for the reason it was excluded there: it is boilerplate.
    /// </para>
    /// </remarks>
    public static readonly Regex FreeBecauseBroken = new(
        @"\b(?:damaged?|cracked|shattered|smashed|water\s+damage|fire\s+damage|" +
        @"needs?\s+(?:work|repair|fixing|a\s+new)|repair\s+or\s+project|project\s+(?:piece|item|car)|" +
        @"missing\s+(?:parts|pieces|a\s+)|torn|ripped|stained|moldy|mildew|rust(?:ed|y)\s+out|" +
        @"doesn'?t\s+turn\s+on|won'?t\s+turn\s+on|no\s+power|dead\s+battery|leaks?\b|" +
        @"good\s+for\s+parts|part\s+out|as\s+is\s+broken)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Counterfeits. eBay prohibits replicas outright, and free ones turn up on classifieds — "Free
    /// knock off yeezy" was in the live sample. Priced against genuine sold comps it would read as
    /// a spectacular flip.
    /// </summary>
    public static readonly Regex Counterfeit = new(
        @"\b(?:knock[\s-]?off|knockoff|replica|fake|faux\s+designer|inspired\s+by|" +
        @"unauthorized|unauthentic|dupe)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Big, heavy and awkward. Not refused — a free treadmill or tool chest is real money — but a
    /// row that carries this is priced by eBay comps whose shipping assumptions do not survive
    /// contact with a 300-pound object, so its verdict is capped and the reason is said out loud.
    /// Every one of these was in the live Craigslist free sample.
    /// </summary>
    public static readonly Regex Bulky = new(
        @"\b(?:sofa|couch|loveseat|sectional|recliner|armoire|dresser|wardrobe|hutch|" +
        @"mattress|box\s*spring|bed\s*frame|bunk\s*bed|loft\s*bed|headboard|" +
        @"dining\s+table|coffee\s+table|desk|bookcase|wall\s+unit|entertainment\s+center|" +
        @"refrigerator|fridge|freezer|washer|dryer|dishwasher|stove|range|oven|water\s+heater|" +
        @"treadmill|elliptical|weight\s+bench|home\s+gym|hot\s+tub|piano|organ|pool\s+table|" +
        @"shed|swing\s+set|trampoline|filing\s+cabinet|safe\b|toilet|bathtub|vanity)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── The clock ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stated deadline in the title. All three shapes were live in this session's sample:
    /// "exp 7/19/26", "ex 7/11", "(Valid 7/10 Only)".
    /// </summary>
    public static readonly Regex ExpiryDate = new(
        @"\b(?:exp|expires?|ex|valid\s+(?:thru|through|until|till)?|thru|through|ends?|until|by)\s*:?\s*" +
        @"(\d{1,2})[/\-](\d{1,2})(?:[/\-](\d{2,4}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A deadline stated in words rather than a date. "MAY 4th ONLY" was live in the sample.</summary>
    public static readonly Regex ExpiryToday = new(
        @"\b(?:today\s+only|tonight\s+only|ends\s+(?:today|tonight)|one\s+day\s+(?:only|sale)|" +
        @"\d{1,2}\s*(?:st|nd|rd|th)?\s+only|flash\s+(?:sale|deal)|lightning\s+deal|" +
        @"while\s+supplies\s+last|limited\s+quantity|going\s+fast|first\s+come)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Credibility bounds ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most a "near free" item can cost and still be the same trade. Above this it is an
    /// ordinary cheap deal, which the Deal Scanner already ranks — this board exists for the ones
    /// where the cost basis rounds to nothing.
    /// </summary>
    public const decimal NearFreeCeiling = 5m;

    /// <summary>
    /// The most a rebate deal can put on the seller's card before this board stops calling it free.
    /// Fronting $40 for six weeks is a freebie with a wait; fronting $600 is a loan, and it belongs
    /// on a board that ranks by capital at risk rather than on this one.
    /// </summary>
    public const decimal MaxFrontedOutlay = 100m;

    /// <summary>
    /// Below this a free item cannot clear the cost of listing, packing and shipping it, whatever
    /// its comp says. A $4 resale is not a flip; it is an afternoon spent for a coffee.
    /// </summary>
    public const decimal MinWorthCollecting = 15m;

    /// <summary>
    /// Everything in a deal title that is about the offer rather than the product: the rebate
    /// clause, the coupon clause, the price. Stripped before the title reaches
    /// <see cref="ProductNormalizer"/>, or "$49.99 after $10 Rebate" becomes part of the product
    /// identity and the comp lookup goes hunting for sold listings of a rebate.
    /// </summary>
    /// <remarks>
    /// A bare price is only stripped when nothing in front of it makes the figure part of the
    /// product's own description. "32 Degrees Underwear <b>Under $10</b> Sale" came back live as
    /// "32 Degrees Under Sale" from a pattern that deleted every dollar figure it saw — the price
    /// was load-bearing, and removing it left a title that reads as a different product.
    /// </remarks>
    public static readonly Regex OfferNoise = new(
        @"\bafter\s+\$?[\d,.]+\s*(?:mail[\s-]?in\s+)?(?:rebate|coupon|code|promo|discount)\b|" +
        @"\bafter\s+(?:mail[\s-]?in\s+)?(?:rebate|coupon|code|promo)\b|" +
        @"\bw/?\s*(?:promo\s+)?code\s+\S+|" +
        // "2 for $0.38 AC + AR" — the whole offer clause, abbreviations included.
        @"\b(?:\d+\s+for\s+)?\$\s?[\d,.]+\s*(?:\+?\s*(?:AC|AR)\b)+|" +
        // A price standing on its own, unless a word in front of it makes it part of the product's
        // own description rather than what it happens to cost today.
        @"(?<!\b(?:under|over|up\s+to|from|starting\s+at|below|above|worth)\s)" +
        @"\$\s?\d{1,3}(?:,\d{3})*(?:\.\d{1,2})?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static readonly Regex NonWord = new(@"[^a-z0-9]+", RegexOptions.Compiled);
}
