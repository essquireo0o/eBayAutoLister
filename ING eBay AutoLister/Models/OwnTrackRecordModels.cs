namespace ING_eBay_AutoLister.Models;

// ── Your own record with this product, on the live card (see Services/OwnTrackRecord.cs) ───────
//
// Every number on the WhatsNot card so far is the market's: eBay's sold comps, eBay's sell-through,
// a resale price derived from what strangers got. That is the right evidence for an item the seller
// has never handled, and it is the WEAKER evidence for one they have sold four times — because
// their own sales carry the fee eBay actually charged them, the postage they actually paid, the
// condition they actually list in and the price their own listings actually get.
//
// Two facts decide a live bid and neither is in the comps:
//
//   * What YOU got for the last one. A comp median of $205 against your own three sales at $172 is
//     not a rounding difference; it is a $33-a-unit ceiling error, repeated on every lot of that
//     product for as long as nobody says it out loud.
//   * What you are already sitting on. Buying a fourth while three are listed and unsold is not
//     arbitrage, it is moving cash onto a shelf — and it is invisible at 11pm with a stream running.
//
// Both are already in this app and neither has ever been read at bid time: completed sales live in
// Money Made, capital in motion lives on the Deal Pipeline.

/// <summary>One of the seller's own past sales of this product, flattened for the card.</summary>
public sealed class OwnSaleRow
{
    public string Title { get; set; } = "";
    public DateTimeOffset SoldUtc { get; set; }
    public int DaysAgo { get; set; }
    public int Quantity { get; set; } = 1;

    /// <summary>What the buyer paid for the goods, per unit — excluding their shipping.</summary>
    public decimal SalePrice { get; set; }

    /// <summary>
    /// Per unit: everything that came in, minus every cost except the goods themselves. The highest
    /// price the seller could have paid for this one and still broken even — measured, not modelled.
    /// </summary>
    public decimal? NetProceeds { get; set; }

    /// <summary>Per unit, when the cost of goods was recorded. Null is common and never zero-filled.</summary>
    public decimal? NetProfit { get; set; }

    /// <summary>Days from paying for it to selling it, when a purchase date was recorded.</summary>
    public int? DaysHeld { get; set; }

    /// <summary>True when the postage this sale cost was never recorded, so its proceeds flatter it.</summary>
    public bool ShippingCostUnknown { get; set; }
}

/// <summary>
/// The seller's own history with one product — the facts alone, before any bid, premium or target
/// is applied to them.
/// </summary>
/// <remarks>
/// Held between bids alongside the comps (<see cref="Services.LiveBidBoard"/>), because it is
/// evidence about the seller rather than about the auction: it cannot change while a lot is on
/// screen, and re-reading two SQLite tables for every keystroke of a climbing bid would spend the
/// seconds this whole screen exists to save.
/// </remarks>
public sealed class OwnSalesEvidence
{
    /// <summary>The clustering key — <see cref="Services.JackpotHunter.ProductSignature"/>'s, the
    /// same one the Restock board groups the seller's sales by, so the two screens can never
    /// disagree about which sales are "these".</summary>
    public string Key { get; set; } = "";
    public string ModelToken { get; set; } = "";

    /// <summary>
    /// True when the live title carried no model designator at all, so the match was made on two
    /// ordinary words. Reported, and never priced off — see <see cref="Services.OwnTrackRecord"/>.
    /// </summary>
    public bool IdentityIsLoose { get; set; }

    /// <summary>How many sales were in the book to check against. Zero means "no record yet",
    /// which is a completely different sentence from "you have never sold one of these".</summary>
    public int SalesRead { get; set; }

    // ── What sold ────────────────────────────────────────────────────────────────────────────
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public decimal? AverageSalePrice { get; set; }

    /// <summary>
    /// Per unit, averaged: revenue minus every known cost except the goods. This is the seller's own
    /// break-even all-in cost, and it is the one figure here that is a <b>measurement</b> rather than
    /// a model — eBay's real fee, the real postage, the real refunds.
    /// </summary>
    public decimal? AverageNetProceeds { get; set; }

    /// <summary>How many units the proceeds average was actually taken over.</summary>
    public int UnitsPricingProceeds { get; set; }

    /// <summary>Units left out of the proceeds average because their postage was never recorded.</summary>
    public int UnitsMissingShippingCost { get; set; }

    public decimal? AverageNetProfit { get; set; }
    public decimal? AverageUnitCost { get; set; }
    public int UnitsWithKnownCost { get; set; }
    public int UnitsAwaitingCost { get; set; }

    /// <summary>Days from buying it to selling it, median, when purchase dates were recorded.</summary>
    public int? MedianDaysToSell { get; set; }

    public DateTimeOffset? LastSoldUtc { get; set; }
    public int? DaysSinceLastSale { get; set; }

    /// <summary>Refunded and cancelled units. Counted nowhere above — reported on their own.</summary>
    public int ReturnedUnits { get; set; }

    // ── What is still on the shelf ───────────────────────────────────────────────────────────
    /// <summary>Units of this product on the Deal Pipeline at Bought or Listed — bought and not yet
    /// sold. The cash that is already in this product before tonight's bid.</summary>
    public int UnitsHeld { get; set; }
    public decimal CapitalHeld { get; set; }
    public int? OldestHeldDays { get; set; }

    public List<OwnSaleRow> Sales { get; set; } = [];

    public bool HasAnything => Orders > 0 || UnitsHeld > 0;
}

/// <summary>
/// The seller's own record, priced at this auction's terms: what their sales imply the ceiling is,
/// beside the one the comps imply.
/// </summary>
public sealed class LiveOwnHistory
{
    /// <summary>proven | once | holding | none. See <see cref="OwnTrackVerdicts"/>.</summary>
    public string Verdict { get; set; } = OwnTrackVerdicts.None;

    /// <summary>One sentence: what this seller has actually done with this product.</summary>
    public string Headline { get; set; } = "";

    /// <summary>What the headline cannot say — each fact on its own, never netted off.</summary>
    public List<string> Notes { get; set; } = [];

    // ── The facts, copied off the evidence for the screen ─────────────────────────────────────
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public decimal? AverageSalePrice { get; set; }
    public decimal? AverageNetProceeds { get; set; }
    public decimal? AverageNetProfit { get; set; }
    public decimal? AverageUnitCost { get; set; }
    public int? MedianDaysToSell { get; set; }
    public int? DaysSinceLastSale { get; set; }
    public int ReturnedUnits { get; set; }
    public int UnitsHeld { get; set; }
    public decimal CapitalHeld { get; set; }
    public int? OldestHeldDays { get; set; }
    public bool IdentityIsLoose { get; set; }
    public List<OwnSaleRow> Sales { get; set; } = [];

    // ── The money, at this auction's terms ────────────────────────────────────────────────────
    /// <summary>
    /// The most to bid on the seller's OWN evidence — <see cref="AuctionSniperAnalyzer"/>'s ceiling,
    /// the same function the badge above it uses, run against their measured net proceeds instead of
    /// the comps' modelled resale. Zero when their record is too thin or too loose to price off.
    /// </summary>
    public decimal OwnMaxBid { get; set; }

    /// <summary>The highest bid that breaks even on their own numbers. A walk-away line, never a target.</summary>
    public decimal OwnBreakEvenBid { get; set; }

    /// <summary>roi | cash — which bar set <see cref="OwnMaxBid"/>.</summary>
    public string OwnCeilingBoundBy { get; set; } = "";

    /// <summary>OwnMaxBid − the comps ceiling. Negative when the seller does worse than the market.</summary>
    public decimal? CeilingGap { get; set; }

    /// <summary>True when the seller's own ceiling is materially BELOW the comps ceiling — the case
    /// that costs money, because the badge is the optimistic one.</summary>
    public bool CeilingIsLower { get; set; }

    /// <summary>True when the comps priced nothing and the seller's own record is the only ceiling
    /// on the card. Rare, and the most valuable thing on the screen when it happens.</summary>
    public bool OwnIsTheOnlyCeiling { get; set; }

    /// <summary>The two ceilings in one sentence, written here rather than in the browser so the
    /// comparison has exactly one definition in the app. Empty when there is nothing to compare.</summary>
    public string CeilingComparison { get; set; } = "";
}

/// <summary>The four answers. Spelled once so the sentence, the CSS and the tests agree.</summary>
public static class OwnTrackVerdicts
{
    /// <summary>Sold more than once. The only tier whose numbers are allowed to price a ceiling.</summary>
    public const string Proven = "proven";
    /// <summary>Sold exactly one. A data point, and reported as one.</summary>
    public const string Once = "once";
    /// <summary>Never sold one, and there is already money in this product.</summary>
    public const string Holding = "holding";
    /// <summary>No record of this product at all — including the case where there is no book yet.</summary>
    public const string None = "none";
}
