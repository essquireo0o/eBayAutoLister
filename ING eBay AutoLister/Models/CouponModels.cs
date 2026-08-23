namespace ING_eBay_AutoLister.Models;

// ── Coupons, promo codes and cashback ────────────────────────────────────────
//
// Every other money screen in this app works on the sell side: what it resells for, what eBay
// takes, how fast the cash comes back. This one works on the buy side, which is where the
// cheaper dollar is — a dollar taken off the purchase price is worth more than a dollar added to
// the sale price, because eBay takes none of it, nothing has to ship for it, and it lands today.
//
// The supply is public and always has been: RetailMeNot-style code lists, the coupon threads on
// the deal aggregators this app already reads, and the cashback portals. What none of them do is
// answer the only question that matters here — "does this code make the flip work?" That needs the
// item's resale comps, its fees and its shipping, all of which are already computed one row over.
//
// The whole design rests on one refusal: a public code is a CLAIM, not a price. It may be dead,
// regional, category-limited or new-customers-only, and there is no way to test it without
// checking out. So the row's own profit figure is never quietly recomputed at the discounted cost.
// The saving lands beside it, as its own number, labelled as conditional — exactly the way the
// negotiation upside does on a classified row. See Services/CouponStacker.cs, which is mostly
// rules about what NOT to bank.

/// <summary>What the offer does to the bill — which decides whether it can be banked at all.</summary>
public static class CouponKinds
{
    /// <summary>"20% off with code SAVE20". Scales with the price, so it is worth most on the big buys.</summary>
    public const string PercentOff = "percent_off";

    /// <summary>"$30 off orders over $199". Fixed, and usually gated behind a minimum spend.</summary>
    public const string AmountOff = "amount_off";

    /// <summary>
    /// A shipping charge waived. Never priced into a number here: this app doesn't know what the
    /// retailer was going to charge to ship it, and a saving of an unknown amount is not a saving
    /// anyone can spend. Surfaced so the seller can use it, never banked.
    /// </summary>
    public const string FreeShipping = "free_shipping";

    /// <summary>
    /// A portal (Rakuten, TopCashback) paying a percentage back after the fact. The one kind that
    /// genuinely stacks with a code, because it isn't applied at the retailer's checkout at all —
    /// and the one kind that is a claim rather than a discount, so it is reserved and waited for
    /// exactly the way a mail-in rebate is. See Services/CouponStacker.cs.
    /// </summary>
    public const string Cashback = "cashback";
}

/// <summary>
/// How much this offer is worth believing. Nothing found on a public code list is ever
/// <see cref="High"/> by default — that grade is earned by an offer that names no exclusions,
/// states a deadline that hasn't passed, and was published for the whole store rather than for a
/// department the seller's item may not be in.
/// </summary>
public static class CouponConfidence
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string Low = "low";

    public static int Rank(string? level) => level switch { High => 2, Medium => 1, _ => 0 };

    /// <summary>A stack is only as trustworthy as its weakest part.</summary>
    public static string Min(string? a, string? b) => Rank(a) <= Rank(b) ? (a ?? Low) : (b ?? Low);
}

/// <summary>One published code or cashback rate, as found on a public list.</summary>
public class CouponOffer
{
    /// <summary>Canonical store id from <see cref="Services.CouponCatalog"/>: homedepot | newegg | …</summary>
    public string MerchantId { get; set; } = "";
    public string MerchantLabel { get; set; } = "";

    /// <summary>One of <see cref="CouponKinds"/>.</summary>
    public string Kind { get; set; } = CouponKinds.AmountOff;

    /// <summary>
    /// The code to type. Empty on a cashback offer (there is nothing to type) and on an
    /// automatically-applied sale — and an empty code on a discount offer is exactly why that offer
    /// is never banked: a sale that needs no code is already in the price on the page.
    /// </summary>
    public string Code { get; set; } = "";

    /// <summary>Percent for <see cref="CouponKinds.PercentOff"/> and cashback; dollars for amount-off.</summary>
    public decimal Value { get; set; }

    /// <summary>The order total the offer needs before it does anything. Zero when none was stated.</summary>
    public decimal MinSpend { get; set; }

    /// <summary>The ceiling on a percentage discount ("20% off, up to $50"). Zero when uncapped.</summary>
    public decimal MaxDiscount { get; set; }

    /// <summary>
    /// True when the code applies to the ORDER — sitewide, "your purchase", or a spend threshold —
    /// rather than to the one item the entry is about.
    /// </summary>
    /// <remarks>
    /// The single most important field on this type, and the one the live feeds taught. Most codes
    /// published on a deal aggregator are bound to a specific listing: "$40 off when you apply promo
    /// code LUSF2737 at checkout = $189.99" is $40 off that motherboard and nothing else. Typing it
    /// against a different item at the same store buys nothing, so a false value here is the
    /// difference between a discount and a fabrication. Only an order-wide code may cut a cost
    /// basis — see <see cref="Services.CouponStacker.BankableDiscount"/>.
    /// </remarks>
    public bool AppliesToOrder { get; set; }

    /// <summary>The offer as the list worded it — what the seller reads to decide whether to trust it.</summary>
    public string Title { get; set; } = "";

    /// <summary>Where to go and check it. Always populated: an unverifiable code is worth less than a link.</summary>
    public string Url { get; set; } = "";

    /// <summary>Which public list this came off — "Slickdeals", "DealNews".</summary>
    public string SourceLabel { get; set; } = "";

    public DateTime? ExpiresUtc { get; set; }

    /// <summary>The deadline as published: "exp 7/31", "today only", "ends Sunday".</summary>
    public string ExpiresText { get; set; } = "";

    /// <summary>When the list was read. A code published three months ago is a code that has been used.</summary>
    public DateTime PublishedUtc { get; set; }

    /// <summary>
    /// The strings that stop a code being a discount on THIS item: "select styles", "new customers
    /// only", "excludes clearance", "in-store only". Kept as published rather than summarised,
    /// because the seller is the only one who knows whether their item is in the excluded half.
    /// </summary>
    public string ExclusionsNote { get; set; } = "";

    /// <summary>One of <see cref="CouponConfidence"/>.</summary>
    public string Confidence { get; set; } = CouponConfidence.Low;

    /// <summary>Why it is graded that way, in one clause. Shown on hover, not folded into a number.</summary>
    public string ConfidenceNote { get; set; } = "";

    /// <summary>What this offer takes off a given subtotal — see Services/CouponStacker.cs.</summary>
    public decimal DiscountOn(decimal subtotal)
    {
        if (subtotal <= 0 || Value <= 0) return 0m;

        var raw = Kind switch
        {
            CouponKinds.PercentOff => Math.Round(subtotal * Value / 100m, 2),
            CouponKinds.AmountOff => Value,
            _ => 0m,   // free shipping and cashback are never a discount at the till
        };

        if (MaxDiscount > 0 && raw > MaxDiscount) raw = MaxDiscount;
        return Math.Min(raw, subtotal);
    }
}

/// <summary>
/// The best legal combination of offers at one price, and what it leaves the buy costing. Pure —
/// see <see cref="Services.CouponStacker"/>. No resale, no fees, no profit: this is the cost basis
/// only, so it can be tested against a price without constructing a whole flip.
/// </summary>
public class CouponStack
{
    /// <summary>What is actually counted in the money below. At most one code, plus cashback.</summary>
    public List<CouponOffer> Applied { get; set; } = [];

    /// <summary>
    /// Everything else found for this store: the other codes, the free-shipping offers, the sales
    /// that need no code. Surfaced deliberately — a code this app declined to bank may still be the
    /// right one for the seller's actual basket, and hiding it would be the app deciding for them.
    /// </summary>
    public List<CouponOffer> AlsoFound { get; set; } = [];

    /// <summary>The shelf price the stack was computed against.</summary>
    public decimal Subtotal { get; set; }

    /// <summary>Taken off at the checkout, today.</summary>
    public decimal Discount { get; set; }

    public decimal DiscountedSubtotal { get; set; }

    /// <summary>
    /// Tax on the DISCOUNTED subtotal, which is what a register charges on a retailer's own promo
    /// code — so a code saves its face value plus the tax that would have sat on top of it.
    /// </summary>
    public decimal SalesTax { get; set; }

    /// <summary>The portal's percentage of what was actually spent. Paid later, by somebody else.</summary>
    public decimal CashbackExpected { get; set; }

    /// <summary>The slice of that deliberately not counted as money — see Services/CouponStacker.cs.</summary>
    public decimal CashbackReserve { get; set; }

    /// <summary>How long the cashback takes to arrive. Zero when no cashback is in the stack.</summary>
    public int CashbackWaitDays { get; set; }

    /// <summary>The cost basis the profit maths is re-run against: paid at the till, less what comes back.</summary>
    public decimal NetCost { get; set; }

    /// <summary>One of <see cref="CouponConfidence"/> — the weakest grade among the applied offers.</summary>
    public string Confidence { get; set; } = CouponConfidence.Low;

    /// <summary>What the stack does and what it assumes, in the row's own numbers.</summary>
    public string Note { get; set; } = "";

    public bool HasSaving => Discount > 0 || CashbackExpected > 0;
}

/// <summary>
/// A stack, priced against the row it belongs to. Lives on its own nullable property of
/// <see cref="LocalArbitrageOpportunity"/> for the same reason the liquidation and freebie blocks
/// do: a Craigslist pickup has no promo code and should not carry a dozen blank coupon columns.
/// </summary>
/// <remarks>
/// The profit figures here are DELIBERATELY not the row's own. The row keeps the number it can
/// stand behind — full shelf price, tax included — and this carries what the same flip makes if
/// the code works. Ranking, verdicts and the board's totals all still run off the row's figure, so
/// a dead code can never move a deal up the table.
/// </remarks>
public class CouponSavings
{
    public string MerchantId { get; set; } = "";
    public string MerchantLabel { get; set; } = "";

    public List<CouponOffer> Applied { get; set; } = [];
    public List<CouponOffer> AlsoFound { get; set; } = [];

    public decimal Discount { get; set; }
    public decimal DiscountedSubtotal { get; set; }
    public decimal SalesTax { get; set; }
    public decimal CashbackExpected { get; set; }
    public decimal CashbackReserve { get; set; }
    public int CashbackWaitDays { get; set; }

    /// <summary>What the buy costs all-in once the code and the cashback are counted.</summary>
    public decimal BuyCostWithCoupons { get; set; }

    /// <summary>What the seller keeps, and the same figures the row shows — recomputed at that cost.</summary>
    public decimal? NetProfitWithCoupons { get; set; }
    public decimal? RoiPercentWithCoupons { get; set; }

    /// <summary>The difference the code makes to the money kept. The headline number of the feature.</summary>
    public decimal? ExtraProfit { get; set; }

    /// <summary>
    /// The verdict this row would earn at the discounted cost, when that is better than the one it
    /// has. Null when the code changes nothing about how good the deal is.
    /// </summary>
    public string? VerdictIfItWorks { get; set; }

    /// <summary>
    /// True when the row makes no money at the shelf price and does make money with the code — the
    /// one case where a coupon isn't an improvement to a deal but the entire reason there is one.
    /// </summary>
    public bool RescuesTheDeal { get; set; }

    public string Confidence { get; set; } = CouponConfidence.Low;
    public string Note { get; set; } = "";
}

/// <summary>One coupon list, and whether it could be read. Same shape and posture as the deal feeds.</summary>
public class CouponSourceOutcome
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>ok | error</summary>
    public string Status { get; set; } = "";
    public int Count { get; set; }
    public string? Error { get; set; }
    public bool Retryable { get; set; }
}

/// <summary>
/// Everything public that could be found for one store, plus the links for the lists that refuse
/// to be read by a program. Returned whole by the standalone lookup, and summarised per store on a
/// scan — see <see cref="CouponStoreOutcome"/>.
/// </summary>
public class CouponLookupResult
{
    /// <summary>ok | no_codes | error</summary>
    public string Status { get; set; } = "";

    /// <summary>What the seller typed, or the retailer read off the deal.</summary>
    public string Query { get; set; } = "";

    public string MerchantId { get; set; } = "";
    public string MerchantLabel { get; set; } = "";

    /// <summary>
    /// False when the store isn't one this app knows. The lists are still searched under the name
    /// given — a seller buying from a store nobody catalogued still deserves an answer — but the
    /// store-specific links and the "this store doesn't do codes" note can't be offered.
    /// </summary>
    public bool MerchantKnown { get; set; }

    /// <summary>
    /// Set for the stores where a code list is the wrong place to look, and why. Amazon is the one
    /// that matters: its discounts are the clip-the-coupon box on the item page, not a code, and a
    /// scan that quietly found nothing would read as "no discount available".
    /// </summary>
    public string? MerchantNote { get; set; }

    public List<CouponOffer> Offers { get; set; } = [];
    public int Count => Offers.Count;

    /// <summary>Which lists answered, and what the ones that didn't said. Never empty on a real run.</summary>
    public List<CouponSourceOutcome> Sources { get; set; } = [];

    /// <summary>
    /// The code lists and cashback portals this app deliberately does not scrape, as prefilled
    /// links for this store. RetailMeNot and the portals answer an automated request with a block
    /// page; a link the seller can open is worth more than a source chip that is red every time.
    /// </summary>
    public List<LocalSupplyManualSite> ManualSites { get; set; } = [];

    /// <summary>The best stack at the price the caller asked about. Null when no price was given.</summary>
    public CouponStack? Stack { get; set; }

    public string? Error { get; set; }
    public bool Retryable { get; set; }

    /// <summary>When the lists were actually read — a cached answer says so rather than implying it's live.</summary>
    public DateTime CheckedUtc { get; set; }
}

/// <summary>
/// What one store contributed to a scan: the counts and the links, without the offers themselves,
/// which are already attached to the rows they discount.
/// </summary>
public class CouponStoreOutcome
{
    public string MerchantId { get; set; } = "";
    public string MerchantLabel { get; set; } = "";
    /// <summary>ok | no_codes | error</summary>
    public string Status { get; set; } = "";
    public int OfferCount { get; set; }
    /// <summary>Rows on this board bought from this store.</summary>
    public int RowCount { get; set; }
    public string? Note { get; set; }
    public string? Error { get; set; }
    public List<LocalSupplyManualSite> ManualSites { get; set; } = [];
}

/// <summary>
/// One unusually large public discount worth investigating as a possible resale buy. It is a lead,
/// not claimed profit: the coupon is verified at checkout and the product is priced against sold
/// comps before the seller spends money.
/// </summary>
public class CouponOpportunity
{
    public CouponOffer Offer { get; set; } = new();
    public string ProductQuery { get; set; } = "";
    public decimal EffectiveDiscountPercent { get; set; }
    public string DiscountLabel { get; set; } = "";
    public int OpportunityScore { get; set; }
    public bool ProductSpecific => !Offer.AppliesToOrder;
}

/// <summary>Cross-store coupon discovery for the Opportunity Finder.</summary>
public class CouponDiscoveryResult
{
    /// <summary>ok | partial | no_deals | error</summary>
    public string Status { get; set; } = "";
    public int MinimumDiscountPercent { get; set; }
    public int StoresScanned { get; set; }
    public int StoresAnswered { get; set; }
    public int OffersExamined { get; set; }
    public List<CouponOpportunity> Opportunities { get; set; } = [];
    public List<CouponStoreOutcome> Stores { get; set; } = [];
    public DateTime CheckedUtc { get; set; }
    public string? Error { get; set; }
}
