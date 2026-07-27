namespace ING_eBay_AutoLister.Models;

// ── Going-out-of-business / liquidation supply ───────────────────────────────
//
// Every other sourcing board in this app buys ONE object from ONE seller: a drill off Craigslist,
// a clearance vacuum off a deal feed. Liquidation supply is different in three ways that all cost
// real money, and all three are modelled here rather than flattened away:
//
//   1. It is an AUCTION. The price on screen is the current bid, not what the item costs — it is
//      the floor. So the number that matters is not "the profit at this price", it is "the highest
//      bid that still makes money", which is what Services/LotAnalyzer.MaxAsk has always computed
//      for pallets and now computes for these.
//   2. There is a BUYER'S PREMIUM on top of the hammer, and sales tax on top of that. A 15%
//      premium plus 8% tax turns a $100 bid into $124 — a quarter of the margin on a typical flip,
//      charged after the seller has already decided the deal was good.
//   3. It is often a LOT, not an item. "Lot of 8 Rorsou Corded Headphones" priced against one
//      headphone comp is off by 8x in the direction that invents a goldmine. So a lot is priced
//      per unit, through the same grade-recovery assumptions the Liquidation Lot Analyzer uses on
//      a pasted manifest (LotAnalyzer.Grades) — and where the unit count is not STATED, it is not
//      guessed.
//
// These live on their own nullable properties rather than as a dozen more fields on
// LocalSupplyListing / LocalArbitrageOpportunity: a Craigslist row is not an auction and should
// not carry twelve blank auction columns to prove it.

/// <summary>
/// What a liquidation listing is, beyond being an item at a price — read off the auction feed by
/// <see cref="Services.LiquidationParser"/> and priced by <see cref="Services.LiquidationLotPricer"/>.
/// </summary>
public class LiquidationLotDetails
{
    // ── The event ────────────────────────────────────────────────────────────
    /// <summary>The auction house running the sale — who the seller actually pays and collects from.</summary>
    public string AuctionHouse { get; set; } = "";
    /// <summary>
    /// The sale's own name: "Overstock Product Liquidation", "STORE CLOSING - EVERYTHING MUST GO".
    /// Carried because it is the single best signal of WHY the stock is cheap.
    /// </summary>
    public string EventName { get; set; } = "";
    /// <summary>
    /// True when the event name reads as a liquidation, closeout, going-out-of-business, surplus or
    /// returns sale rather than an ordinary consignment auction — see
    /// <see cref="Services.LiquidationSelectors.LiquidationEvent"/>. Not a money input; it is what
    /// puts the going-out-of-business rows in front of the seller.
    /// </summary>
    public bool IsLiquidationEvent { get; set; }
    /// <summary>The auction's own page, for checking terms, pickup and the rest of the catalogue.</summary>
    public string EventUrl { get; set; } = "";

    // ── The bid ──────────────────────────────────────────────────────────────
    /// <summary>
    /// The auctioneer's cut on top of the hammer price, as a percentage. Real money and routinely
    /// double digits; charged through <see cref="Services.LotAnalyzer.CostOf"/> exactly as it is on
    /// a pallet.
    /// </summary>
    public decimal BuyerPremiumPercent { get; set; }
    /// <summary>
    /// True when the auction published no machine-readable rate and none could be read from its
    /// printed terms, so a conservative one was charged rather than none. Said on the row: a cost
    /// the app assumed is a different claim from a cost the auctioneer stated.
    /// </summary>
    public bool BuyerPremiumAssumed { get; set; }
    public int BidCount { get; set; }
    /// <summary>
    /// True when nobody has bid yet, so the price shown is the OPENING bid rather than a live one.
    /// Worth saying on the row: an opening bid is the cheapest this will ever be, and a current bid
    /// with fourteen bidders behind it is nearly never what the item finally costs.
    /// </summary>
    public bool IsStartingBid { get; set; }
    public DateTime? ClosesUtc { get; set; }
    /// <summary>"2d 14h 12m", as the site rendered it — the deadline to act on.</summary>
    public string TimeLeft { get; set; } = "";

    // ── The contents ─────────────────────────────────────────────────────────
    /// <summary>True when this is several units of one product rather than a single item.</summary>
    public bool IsLot { get; set; }
    /// <summary>
    /// Units in the lot, only ever from a count the listing STATED — "(4) DeWalt Grinders",
    /// "Lot of 8 …", or the site's own quantity field. Never inferred from the word "pallet",
    /// because a pallet jack is not a pallet of anything (verified: of 37 live lots whose titles
    /// contained "pallet", the great majority were pallet racks, forks and jacks).
    /// </summary>
    public int Units { get; set; } = 1;
    /// <summary>
    /// Which of <see cref="Services.LotAnalyzer.Grades"/> the wording implies — how many of the
    /// units are expected to be sellable at all, and what the survivors fetch. Only meaningful on
    /// a multi-unit lot; a single item is priced like any other single item.
    /// </summary>
    public string GradeId { get; set; } = "";
    /// <summary>
    /// "$4,200 retail value" as claimed in the listing. Never used as a value — used exactly as a
    /// manifest's retail column is used, as a cross-check that the comp matcher found the right
    /// product (<see cref="Services.LotAnalyzer.RetailSanityCheck"/>).
    /// </summary>
    public decimal? ClaimedRetailTotal { get; set; }

    /// <summary>
    /// Set when this row cannot honestly be priced against sold comps at all, and why: an
    /// "assorted contents" lot has no single product to comp, a "PALLET OF" with no stated count
    /// has no divisor, and a for-parts unit has no working-item comp. Non-null means the row is
    /// shown with the reason instead of a fabricated profit.
    /// </summary>
    public string? UnpriceableReason { get; set; }
}

/// <summary>
/// The money on one liquidation row, after the buyer's premium, the sales tax and — on a lot — the
/// grade's recovery assumptions. Everything here is computed by
/// <see cref="Services.LiquidationLotPricer"/> from the same <see cref="Services.LotAnalyzer"/> and
/// <see cref="Services.ProfitCalculator"/> the manifest analyzer uses, so a pallet found by this
/// scan and the same pallet pasted into the Lot Analyzer cannot disagree.
/// </summary>
public class LiquidationLotEconomics
{
    // ── Carried through for the row to render ────────────────────────────────
    public string AuctionHouse { get; set; } = "";
    public string EventName { get; set; } = "";
    public bool IsLiquidationEvent { get; set; }
    public string EventUrl { get; set; } = "";
    public int BidCount { get; set; }
    public bool IsStartingBid { get; set; }
    public DateTime? ClosesUtc { get; set; }
    public string TimeLeft { get; set; } = "";

    // ── What the bid really costs ────────────────────────────────────────────
    public decimal BuyerPremiumPercent { get; set; }
    public decimal BuyerPremium { get; set; }
    /// <summary>True when the premium above was assumed rather than published — see the details type.</summary>
    public bool BuyerPremiumAssumed { get; set; }
    public decimal SalesTaxPercent { get; set; }
    public decimal SalesTax { get; set; }

    // ── The lot ──────────────────────────────────────────────────────────────
    public bool IsLot { get; set; }
    public int Units { get; set; } = 1;
    public string GradeId { get; set; } = "";
    public string GradeLabel { get; set; } = "";
    public string GradeNote { get; set; } = "";
    /// <summary>
    /// Units expected to be worth listing at all, at this grade's recovery rate. Deliberately
    /// fractional for the same reason it is on a manifest line: rounding 8 x 65% down to 5 throws
    /// away value that is really there, and up to 6 invents stock that isn't.
    /// </summary>
    public decimal SellableUnits { get; set; }
    /// <summary>What ONE unit is expected to fetch, after the grade's price factor.</summary>
    public decimal? UnitResale { get; set; }
    /// <summary>Net profit on one unit before any of the bid is charged to it.</summary>
    public decimal? UnitNetRecovery { get; set; }
    /// <summary>All-in cost divided by the units expected to sell — the real per-item cost basis.</summary>
    public decimal? CostPerSellableUnit { get; set; }

    // ── The cross-checks ─────────────────────────────────────────────────────
    public decimal? ClaimedRetailTotal { get; set; }
    /// <summary>
    /// What the lot actually resells for as a percentage of the retail value the listing claims.
    /// The "$12,000 retail value!" test, and it is usually a small number.
    /// </summary>
    public decimal? ResalePercentOfRetail { get; set; }

    // ── The number to walk in with ───────────────────────────────────────────
    /// <summary>
    /// The highest bid that still clears <see cref="Services.LiquidationLotPricer.TargetRoiPercent"/>,
    /// with the premium and the tax already taken out of it. The one figure worth writing on your
    /// hand before an auction closes.
    /// </summary>
    public decimal? MaxBidForTargetRoi { get; set; }
    public decimal TargetRoiPercent { get; set; }

    public string? UnpriceableReason { get; set; }
}
