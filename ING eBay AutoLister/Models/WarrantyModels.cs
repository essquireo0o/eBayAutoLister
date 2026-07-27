namespace ING_eBay_AutoLister.Models;

// ── Used, but still covered ───────────────────────────────────────────────────
//
// Every other sourcing board in this app asks "how far under resale can I buy this?". This one asks
// the question that decides whether the answer is worth acting on: "and what happens if it's dead?"
//
// A used item with time left on a manufacturer warranty is a different asset from the identical item
// without one, in two ways that are both money:
//
//   1. THE DOWNSIDE SHRINKS. A $600 laptop bought off a stranger that doesn't boot is a $600 loss.
//      The same laptop with eight months of factory cover is a repair ticket and a wait. On a board
//      that ranks by profit alone those two rows are indistinguishable, and one of them is a trap.
//   2. THE RESALE RISES. "Still under manufacturer warranty until March 2027" is a line the seller
//      can put in their own eBay listing, and buyers pay for it. The sold comps behind the price
//      estimate are a blend of covered and uncovered units, so that premium is not in the number the
//      board started from.
//
// What this must never become is a machine for inventing warranties. "Warranty" appears in listing
// text far more often than a live warranty does — expired ones, seller promises that die at resale,
// protection plans being advertised rather than included, and "no warranty" itself. So the posture
// is the same one Services/FreebieClassifier.cs takes with the word "free": refusal comes first, and
// only a warranty the LISTING ITSELF states is allowed to move a dollar. Everything inferred is
// reported and priced at zero — see Services/WarrantyPricer.cs.

/// <summary>Who is on the hook — which decides whether the cover survives the resale.</summary>
public static class WarrantyKinds
{
    /// <summary>
    /// The factory warranty that came with the item and is still running. The strongest kind: it
    /// follows the serial number rather than the receipt on most consumer electronics, so it is
    /// still there after the flip.
    /// </summary>
    public const string Manufacturer = "manufacturer";

    /// <summary>
    /// A refurbisher's own cover — Apple Certified Refurbished, Amazon Renewed, eBay Refurbished,
    /// a Best Buy open-box unit. Published terms, which is why these can be read off the programme
    /// name rather than out of the listing's prose. See <see cref="Services.WarrantyCatalog"/>.
    /// </summary>
    public const string Refurbisher = "refurbisher";

    /// <summary>
    /// A bought protection plan — AppleCare+, SquareTrade, Geek Squad Protection. Usually longer than
    /// the factory term and usually transferable, and stated by name when it exists.
    /// </summary>
    public const string Extended = "extended";

    /// <summary>
    /// The person selling it is the warranty: "30 day warranty from me", "guaranteed working or your
    /// money back". Real protection for the person buying it — and worth nothing on resale, because
    /// the promise was made to the reseller and not to their buyer.
    /// </summary>
    public const string Seller = "seller";

    /// <summary>
    /// The listing states there is no cover: expired, void, out of warranty, sold as-is with no
    /// returns. Not the absence of a signal — a signal, and one that holds a verdict down.
    /// </summary>
    public const string None = "none";
}

/// <summary>
/// How the app knows. This is the axis the money hangs off: only the first two are ever allowed to
/// change a price, and the third is always worth <c>$0</c> however good it looks.
/// </summary>
public static class WarrantyEvidence
{
    /// <summary>The listing said so, in its own words. The seller can be held to it and asked for the receipt.</summary>
    public const string Stated = "stated";

    /// <summary>
    /// A named refurbishment programme whose terms are published — the listing didn't state a length
    /// because it didn't have to. Treated as stated, because the programme's terms are a fact about
    /// the programme rather than a claim about this unit.
    /// </summary>
    public const string Program = "program";

    /// <summary>
    /// Nobody said "warranty" at all. This was worked out from a purchase date, or from an unopened
    /// box, plus what that brand's cover normally runs to. Genuinely useful to know and never worth a
    /// cent on the board — an inference about a warranty is not a warranty.
    /// </summary>
    public const string Estimated = "estimated";
}

/// <summary>
/// What one listing says about cover, read by <see cref="Services.WarrantyDetector"/> and turned into
/// money by <see cref="Services.WarrantyPricer"/>.
/// </summary>
/// <remarks>
/// Its own nullable property on <see cref="LocalSupplyListing"/> rather than a dozen more columns,
/// for the reason <see cref="FreebieDetails"/> and <see cref="LiquidationLotDetails"/> are: most rows
/// on most boards say nothing about warranty at all, and they should not carry a dozen blank fields
/// to say so.
/// </remarks>
public class WarrantyDetails
{
    /// <summary>One of <see cref="WarrantyKinds"/>.</summary>
    public string Kind { get; set; } = WarrantyKinds.Manufacturer;

    /// <summary>One of <see cref="WarrantyEvidence"/>.</summary>
    public string Evidence { get; set; } = WarrantyEvidence.Estimated;

    /// <summary>How the row says it: "Manufacturer warranty · 8 months left", "Amazon Renewed · 90 days".</summary>
    public string KindLabel { get; set; } = "";

    /// <summary>
    /// Whole months of cover left, or null when the length is genuinely unknowable from the listing —
    /// a stated term with no stated start date is exactly that, and guessing one would be inventing
    /// the whole figure. Null never earns money.
    /// </summary>
    public int? MonthsRemaining { get; set; }

    /// <summary>When the cover runs out, where the listing named a date or one could be derived.</summary>
    public DateTime? ExpiresUtc { get; set; }

    /// <summary>The full term, in months, as stated or as published by the programme. 0 when unknown.</summary>
    public int TermMonths { get; set; }

    /// <summary>
    /// Whether the cover is worth anything to the person the reseller sells to. False on a
    /// <see cref="WarrantyKinds.Seller"/> promise, which was made to the reseller, and on the brands
    /// whose terms are explicitly original-purchaser-only — most cordless power tools.
    /// Only a transferable warranty is allowed to move the resale price.
    /// </summary>
    public bool TransfersToBuyer { get; set; }

    /// <summary>"Apple Certified Refurbished", "Amazon Renewed" — named when a programme was matched.</summary>
    public string ProgramLabel { get; set; } = "";

    /// <summary>"open box", "certified refurbished", "sealed" — the condition wording that was found.</summary>
    public string ConditionLabel { get; set; } = "";

    /// <summary>
    /// The listing's own words this was read from, trimmed. Carried so the row can quote the source
    /// rather than a reformatted guess — the seller is going to ask the person about this.
    /// </summary>
    public string SourceText { get; set; } = "";

    /// <summary>
    /// True when the listing mentions a receipt, invoice or proof of purchase. Not worth money by
    /// itself; worth saying, because it is the one thing that turns a warranty claim from an argument
    /// into a form.
    /// </summary>
    public bool HasProofOfPurchase { get; set; }
}

/// <summary>
/// What the cover is worth, and what it is not allowed to be worth.
/// </summary>
/// <remarks>
/// <para>
/// The uplift here is the only place in the app where a listing's prose is permitted to raise a
/// resale estimate above what the sold comps produced, so it is fenced on every side:
/// <see cref="WarrantyEvidence.Estimated"/> earns nothing, a non-transferable warranty earns nothing,
/// thin or low-confidence sold history earns nothing, and what is left is capped both as a percentage
/// and in dollars. When any fence bites, <see cref="HeldBackReason"/> says which one and the row's
/// money is exactly what it would have been without this feature.
/// </para>
/// <para>
/// The other half is not a price at all. <see cref="CoversYourBuy"/> is about the reseller's own
/// downside: cover that never transfers is still the difference between a dead unit costing them the
/// purchase price and costing them a wait.
/// </para>
/// </remarks>
public class WarrantyEconomics
{
    public string Kind { get; set; } = WarrantyKinds.Manufacturer;
    public string Evidence { get; set; } = WarrantyEvidence.Estimated;
    public string KindLabel { get; set; } = "";
    public string ProgramLabel { get; set; } = "";
    public string ConditionLabel { get; set; } = "";
    public string SourceText { get; set; } = "";

    public int? MonthsRemaining { get; set; }
    public DateTime? ExpiresUtc { get; set; }
    /// <summary>The deadline said the way a person would: "runs to Mar 2027", "about 8 months left".</summary>
    public string ExpiresText { get; set; } = "";

    public bool TransfersToBuyer { get; set; }

    /// <summary>The percentage added to the comps' expected sale price. Zero whenever a fence bit.</summary>
    public decimal UpliftPercent { get; set; }

    /// <summary>The dollars that percentage came to, after both caps. Zero whenever a fence bit.</summary>
    public decimal ResaleUplift { get; set; }

    /// <summary>What the sold comps alone said, before the warranty was counted.</summary>
    public decimal ResaleWithoutWarranty { get; set; }

    /// <summary>What the row's profit was actually computed against — the sum of the two above.</summary>
    public decimal ResaleWithWarranty { get; set; }

    /// <summary>
    /// Why the uplift is zero, when it is. Null means the warranty was counted. Stated rather than
    /// silent: "we found a warranty and paid nothing for it" is a decision the seller should see.
    /// </summary>
    public string? HeldBackReason { get; set; }

    /// <summary>
    /// True when the cover protects the reseller's own outlay — every kind except
    /// <see cref="WarrantyKinds.None"/>. Independent of <see cref="TransfersToBuyer"/>: a seller's
    /// 30-day promise is worth nothing on resale and everything on the drive home.
    /// </summary>
    public bool CoversYourBuy { get; set; }

    /// <summary>The money that stops being at risk when it does. The buy cost, or what could be recovered of it.</summary>
    public decimal ProtectedCost { get; set; }

    /// <summary>One sentence saying what the cover is and what it did to this row's numbers.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// Set only on a row the listing states is uncovered — as-is, expired, no returns — and only
    /// when the buy is large enough for that to change the answer. Null on everything else.
    /// </summary>
    public string? RiskNote { get; set; }
}
