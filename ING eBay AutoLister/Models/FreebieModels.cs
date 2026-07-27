namespace ING_eBay_AutoLister.Models;

// ── Free / free-after-coupon supply ──────────────────────────────────────────
//
// Every other sourcing board in this app answers "what can I buy under resale value?". This one
// answers the question with the best possible answer: what can I get for nothing. A $0 cost basis
// is not a better deal on the same axis — it is a different arithmetic, because ROI stops being a
// number and the only question left is whether the thing is worth the trip and the listing effort.
//
// Two very different kinds of supply land here, and they are told apart rather than blended:
//
//   1. LOCAL FREE. Craigslist's free-stuff board is full of televisions, treadmills, tool chests
//      and appliances people want gone today. Cash cost: nothing. Real cost: the drive, and the
//      fact that a free post is claimed in hours.
//   2. FREE AFTER COUPON / REBATE. A retail item whose price becomes $0 at checkout with a code,
//      or which is refunded after the fact. These are NOT the same trade, and the difference is
//      money — see FreebieEconomics below.
//
// The one thing this board must never do is call something free that isn't. "Free shipping" is
// the single most common phrase on every deal feed in existence and it means the item costs full
// price; "free gift with purchase" costs whatever the purchase costs. A board that reads either
// one as a $0 cost basis would put fabricated goldmines at the very top of a profit ranking, which
// is the most expensive mistake this app can make. See Services/FreebieClassifier.cs, whose entire
// job is refusal.

/// <summary>How the item gets to $0 — which decides what it actually costs. See FreebieEconomics.</summary>
public static class FreebieKinds
{
    /// <summary>$0, now, no conditions. A curb pickup, or a genuine giveaway.</summary>
    public const string Free = "free";

    /// <summary>$0 at checkout, but only if a code is typed. Free if it works, full price if it doesn't.</summary>
    public const string FreeAfterCoupon = "free_after_coupon";

    /// <summary>
    /// Paid in full now, refunded later. The seller fronts real money, waits weeks, and may never
    /// be paid — and the sales tax is never refunded at all.
    /// </summary>
    public const string FreeAfterRebate = "free_after_rebate";

    /// <summary>
    /// A real price, but small enough that the trade is the same one — see
    /// <see cref="Services.FreebieSelectors.NearFreeCeiling"/>.
    /// </summary>
    public const string NearFree = "near_free";
}

/// <summary>
/// How much time is left, which on this board is the whole difference between a deal and a story
/// about one. Free things are claimed within hours and coupon prices are pulled without notice.
/// </summary>
public static class FreebieUrgency
{
    /// <summary>Stated to end today, or already inside the last day of a stated expiry.</summary>
    public const string Today = "today";

    /// <summary>A stated expiry inside the week.</summary>
    public const string ThisWeek = "this_week";

    /// <summary>
    /// No clock, but no queue either: whoever turns up first takes it. What a local free post is,
    /// and the reason its age matters more than its price.
    /// </summary>
    public const string FirstCome = "first_come";

    public const string Unknown = "unknown";
}

/// <summary>
/// What a free listing is, beyond being an item at no price — read off the source by
/// <see cref="Services.FreebieClassifier"/> and costed by <see cref="Services.FreebiePricer"/>.
/// </summary>
/// <remarks>
/// Lives on its own nullable property rather than as ten more fields on
/// <see cref="LocalSupplyListing"/>, for the same reason <see cref="LiquidationLotDetails"/> does:
/// a clearance vacuum is not a freebie and should not carry ten blank freebie columns to say so.
/// </remarks>
public class FreebieDetails
{
    /// <summary>One of <see cref="FreebieKinds"/>.</summary>
    public string Kind { get; set; } = FreebieKinds.Free;

    /// <summary>How the row says it: "Free", "Free after coupon", "Free after $50 rebate".</summary>
    public string KindLabel { get; set; } = "";

    /// <summary>
    /// The sticker price before the coupon or rebate comes off — what the seller pays at the till on
    /// a rebate deal, and $0 on an outright freebie. Not a cost by itself; see
    /// <see cref="FreebieEconomics.OutOfPocketNow"/>.
    /// </summary>
    public decimal ListPrice { get; set; }

    /// <summary>What is expected back on a rebate. Zero on every other kind.</summary>
    public decimal RebateAmount { get; set; }

    /// <summary>"mail-in rebate", "Venmo rebate" — how the money comes back, when the listing said.</summary>
    public string RebateVia { get; set; } = "";

    /// <summary>True when the $0 only exists with a code typed at checkout.</summary>
    public bool RequiresCoupon { get; set; }

    /// <summary>
    /// True when the listing actually STATED that getting it costs nothing — free shipping, free
    /// store pickup, or a local collection. False means the delivery cost is unknown, which on a $0
    /// item is not a detail: it is the entire cost basis.
    /// </summary>
    public bool DeliveryCostKnown { get; set; }

    /// <summary>True when the item is collected in person, so there is no shipping cost to know.</summary>
    public bool IsPickup { get; set; }

    /// <summary>
    /// Furniture, appliances, exercise equipment, pianos — free all day long, and priced by eBay
    /// comps that quietly assume the thing fits in a box. Flagged rather than refused, because a
    /// treadmill someone gives away is real money to a seller who can move it.
    /// </summary>
    public bool IsBulky { get; set; }

    /// <summary>When the offer stops, if the listing stated a date.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>The deadline as the listing worded it: "today only", "exp 7/19", "ends tonight".</summary>
    public string ExpiresText { get; set; } = "";

    /// <summary>One of <see cref="FreebieUrgency"/>.</summary>
    public string Urgency { get; set; } = FreebieUrgency.Unknown;
}

/// <summary>
/// What a free thing actually costs — which is never quite nothing, and on a rebate is not close.
/// </summary>
/// <remarks>
/// <para>
/// Three costs survive the word "free", and all three are real money:
/// </para>
/// <list type="bullet">
///   <item><b>Sales tax is not refunded.</b> A $49.99 item with a $49.99 mail-in rebate is not free:
///   the register still charged tax on $49.99, and the rebate cheque only ever covers the price. At
///   7.5% that is $3.75 of genuine cost against an item the board would otherwise show at $0.</item>
///   <item><b>A rebate is a claim, not a discount.</b> The money leaves now and comes back — if it
///   comes back. See <see cref="Services.FreebiePricer.RebateReservePercent"/>.</item>
///   <item><b>Getting it is not free.</b> Shipping on an online $0 item, or the drive on a local one.
///   Where the listing didn't state it, the row says so instead of assuming zero.</item>
/// </list>
/// </remarks>
public class FreebieEconomics
{
    public string Kind { get; set; } = FreebieKinds.Free;
    public string KindLabel { get; set; } = "";

    /// <summary>
    /// What leaves the wallet at the till, today. Zero on an outright freebie or a coupon that takes
    /// the price to $0; the full taxed price on a rebate deal, where the seller is the one lending
    /// the money.
    /// </summary>
    public decimal OutOfPocketNow { get; set; }

    /// <summary>The rebate expected back. Zero on every other kind.</summary>
    public decimal RefundExpected { get; set; }

    /// <summary>
    /// Sales tax paid, and — on a rebate — never seen again. Zero on a curb pickup, which is cash
    /// between two people, and zero on a coupon that takes the till price to $0, because a register
    /// charges tax on what it actually rings up.
    /// </summary>
    public decimal SalesTax { get; set; }

    /// <summary>
    /// The slice of the rebate deliberately NOT counted as money — see
    /// <see cref="Services.FreebiePricer.RebateReservePercent"/>. A reserve, not a forecast.
    /// </summary>
    public decimal RebateReserve { get; set; }

    /// <summary>
    /// The cost basis every profit figure on the row is built from: what the seller is still out of
    /// pocket once the refund has (mostly) come back. Zero on a true freebie, and the sales tax plus
    /// the reserve on a rebate.
    /// </summary>
    public decimal NetCost { get; set; }

    /// <summary>One sentence saying where <see cref="NetCost"/> came from, in the row's own numbers.</summary>
    public string CostNote { get; set; } = "";

    /// <summary>How long there is to act — one of <see cref="FreebieUrgency"/>.</summary>
    public string Urgency { get; set; } = FreebieUrgency.Unknown;

    /// <summary>The deadline said plainly, because a profit read after the offer ended is not one.</summary>
    public string UrgencyNote { get; set; } = "";
    public DateTime? ExpiresUtc { get; set; }
    public string ExpiresText { get; set; } = "";

    public bool RequiresCoupon { get; set; }
    public bool IsPickup { get; set; }
    public bool IsBulky { get; set; }

    /// <summary>
    /// Set when the row's verdict was deliberately held below what its arithmetic earned, and why —
    /// an unknown delivery cost on a $0 item, or a freight-sized thing priced by parcel comps.
    /// Null when nothing was held back.
    /// </summary>
    public string? CappedReason { get; set; }

    /// <summary>
    /// Days between paying and being refunded, folded into the days-to-cash figure. A rebate ties up
    /// the money exactly the way inventory does, and a board that ignored it would rank a rebate flip
    /// alongside a curb pickup as if the cash came back at the same speed.
    /// </summary>
    public int RefundWaitDays { get; set; }
}
