namespace ING_eBay_AutoLister.Models;

// ── The Undervalued-Auction Sniper ────────────────────────────────────────────────────────────
//
// Every sourcing screen in this app so far sends the seller somewhere else to buy: Craigslist,
// Facebook, a pallet auction, a supplier file. This one buys on eBay and sells on eBay — the same
// marketplace, hours apart — because eBay's auction format regularly closes items below what the
// same item's Buy It Now comps settle at, and the sold-comps database already knows what that is.
//
// The rules that govern this file, all of them about the same thing:
//
//   1. **A current bid is not a closing price.** A $12 auction with three days left is not a $12
//      item. Rows that far out are marked "too early" no matter how good the arithmetic looks, and
//      they contribute nothing to any total on the board. This is the single defect that makes
//      every naive "underpriced auction finder" useless, and refusing to score them is the whole
//      reason this feature can be believed.
//   2. **The number that matters is the ceiling, not the discount.** What a sniper needs is the
//      highest bid that still leaves a profit worth having — one number, entered once, walked away
//      from. The current price is where the bidding is, not where it ends.
//   3. **Nothing here bids.** The app never spends the seller's money. Every figure is a number to
//      type into eBay's own max-bid box by hand.
//   4. **Cheap is a symptom, not a signal.** Most underpriced auctions are underpriced because the
//      item is broken, an accessory, or not the thing the title says. Every listing passes the same
//      identity guard the rest of the app uses before a cent of profit is booked against it, and
//      what's left is flagged with why it might still be cheap.

/// <summary>One live eBay listing, priced against what the same item actually sells for.</summary>
public sealed class SnipeCandidate
{
    // ── What it is ────────────────────────────────────────────────────────────────────────────
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Condition { get; set; } = "";
    /// <summary>AUCTION or FIXED_PRICE. The two behave differently and are never mixed in a total.</summary>
    public string BuyingOption { get; set; } = "";
    public int BidCount { get; set; }
    public string SellerUsername { get; set; } = "";
    public int SellerFeedbackScore { get; set; }
    public decimal? SellerFeedbackPercent { get; set; }
    /// <summary>The watch term that surfaced it — a keyword typed in, or a product already sold.</summary>
    public string FoundBy { get; set; } = "";

    // ── What it costs right now ───────────────────────────────────────────────────────────────

    /// <summary>The current bid on an auction, or the asking price of a Buy It Now.</summary>
    public decimal CurrentPrice { get; set; }
    public decimal ShippingCost { get; set; }
    /// <summary>True when eBay stated a shipping cost. False means unknown, which is NOT free.</summary>
    public bool ShippingStated { get; set; }
    /// <summary>Price plus shipping — what acquiring it actually costs today.</summary>
    public decimal AllInCost { get; set; }

    // ── What it's worth ───────────────────────────────────────────────────────────────────────

    /// <summary>The title the sold comps were matched on. Shown, never summarised away.</summary>
    public string PricedAs { get; set; } = "";
    public decimal? MarketMedian { get; set; }
    public decimal? ExpectedSale { get; set; }
    public decimal? QuickSale { get; set; }
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public string ResaleSource { get; set; } = "none";
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public string? DisagreementMessage { get; set; }
    /// <summary>
    /// True when this row was re-priced against its own title rather than the watch term's.
    /// A number someone bids on should be priced off the item they're bidding on.
    /// </summary>
    public bool PricedPerItem { get; set; }

    /// <summary>How far under the sold median the all-in cost sits. The headline "why look at this".</summary>
    public decimal? DiscountPercent { get; set; }

    // ── The buy ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The bid at which this makes exactly nothing. Above it you are paying to work.</summary>
    public decimal? BreakEvenBid { get; set; }
    /// <summary>
    /// The most to bid: the highest bid that still leaves a profit worth the trouble
    /// (<see cref="Services.LocalArbitrageAnalyzer.SolidProfit"/> in cash and
    /// <see cref="Services.LocalArbitrageAnalyzer.SolidRoiPercent"/> on the money, whichever binds).
    /// Truncated to the cent, never rounded up — this is a number someone types into a bid box.
    /// </summary>
    public decimal? MaxBid { get; set; }
    /// <summary>Max bid minus the current price: how much room is left before walking away.</summary>
    public decimal? BidHeadroom { get; set; }

    /// <summary>Net profit if it closed at the price it's at right now, after every fee and cost.</summary>
    public decimal? ProfitAtCurrentPrice { get; set; }
    public decimal? RoiAtCurrentPrice { get; set; }
    public decimal? MarginPercent { get; set; }
    /// <summary>Net profit if you win at your ceiling — the worst case of a bid you'd actually place.</summary>
    public decimal? ProfitAtMaxBid { get; set; }

    public decimal? EstimatedFees { get; set; }
    public decimal? EstimatedShipCost { get; set; }

    // ── How long the money stays spent (DaysToCashEstimator) ──────────────────────────────────
    public int? DaysToSell { get; set; }
    public int? DaysToCash { get; set; }
    public decimal? ProfitPerDay { get; set; }
    public decimal? AnnualizedRoiPercent { get; set; }
    public string SpeedTier { get; set; } = "unknown";
    public string SpeedLabel { get; set; } = "";
    public string SpeedNote { get; set; } = "";
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";

    // ── Time, which is the whole game on an auction ───────────────────────────────────────────

    public DateTime? EndUtc { get; set; }
    /// <summary>Minutes until it closes. Null on a Buy It Now with no end date worth counting to.</summary>
    public int? MinutesToEnd { get; set; }
    /// <summary>closing | today | open | ended | none. See <see cref="Services.AuctionSniperAnalyzer"/>.</summary>
    public string TimeTier { get; set; } = "none";
    /// <summary>
    /// When to actually place the bid — seconds before the close. Bidding earlier only tells the
    /// other bidders to raise their own. Null on anything not worth bidding on.
    /// </summary>
    public DateTime? SnipeAtUtc { get; set; }
    /// <summary>
    /// True when the price on screen is the price you would pay: a Buy It Now, or an auction close
    /// enough to the end that the bidding is effectively over.
    /// </summary>
    public bool PriceIsReal { get; set; }

    // ── The call ──────────────────────────────────────────────────────────────────────────────

    /// <summary>snipe | watch | too_early | thin | pass | ended | no_data.</summary>
    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";
    /// <summary>Why this might be cheap for a reason. Never hidden, never turned into a score.</summary>
    public List<string> Risks { get; set; } = [];
}

/// <summary>One keyword the scan swept, and what it cost.</summary>
public sealed class SnipeWatchTerm
{
    public string Term { get; set; } = "";
    /// <summary>The fuller product title the comps were matched on, when there is one.</summary>
    public string LookupTitle { get; set; } = "";
    /// <summary>typed | sold — a keyword the seller typed, or a product they have already flipped.</summary>
    public string Source { get; set; } = "typed";
    /// <summary>"You've sold 4 of these" — why this term is on the list at all.</summary>
    public string Reason { get; set; } = "";
    /// <summary>Completed sales of this product behind the term. Zero for a typed keyword.</summary>
    public int SalesBehindIt { get; set; }

    public int ListingsFound { get; set; }
    public int ListingsRejected { get; set; }
    public int Kept { get; set; }
    public bool Priced { get; set; }
    public string? Error { get; set; }
}

/// <summary>The headline. Every figure here is deliberately the least flattering honest one.</summary>
public sealed class SnipeSummary
{
    public int TermsScanned { get; set; }
    public int ListingsScanned { get; set; }
    /// <summary>Listings dropped by the identity guard — accessories, parts, the wrong product.</summary>
    public int ListingsRejected { get; set; }

    /// <summary>Rows worth bidding on whose price is real: closing soon, or a Buy It Now.</summary>
    public int SnipeCount { get; set; }
    /// <summary>Auctions with real headroom that are still too far out to price. A watch list.</summary>
    public int TooEarlyCount { get; set; }
    /// <summary>Of the snipes, how many close within the hour.</summary>
    public int ClosingWithinTheHour { get; set; }

    /// <summary>
    /// Profit across every snipe-worthy row IF each were won at its ceiling. An upper bound on
    /// what's on the board right now, not a forecast — it falls every time somebody bids.
    /// </summary>
    public decimal ProfitAtCeilings { get; set; }
    /// <summary>The best single row's profit at its ceiling.</summary>
    public decimal BestProfit { get; set; }
    /// <summary>Total capital it would take to win them all at those ceilings.</summary>
    public decimal CapitalToWinAll { get; set; }

    /// <summary>The soonest close on the board — the clock the seller is actually racing.</summary>
    public DateTime? NextEndUtc { get; set; }
    public DateTime ScannedUtc { get; set; }
}

/// <summary>Everything the snipe board renders in one response.</summary>
public sealed class SnipeScanResult
{
    /// <summary>ok | no_terms | error.</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public string? DataWarning { get; set; }

    /// <summary>auctions | bins | both — what was swept.</summary>
    public string Mode { get; set; } = "auctions";
    public string Sort { get; set; } = "urgency";
    /// <summary>Hours out past which an auction's price is not treated as a price.</summary>
    public int PriceIsRealHours { get; set; }

    public bool TermsWereTyped { get; set; }
    public List<SnipeWatchTerm> Terms { get; set; } = [];
    public List<SnipeCandidate> Candidates { get; set; } = [];
    public SnipeSummary Summary { get; set; } = new();

    /// <summary>Plain statements of what these numbers do and don't mean. Rendered, not buried.</summary>
    public List<string> Honesty { get; set; } = [];
}
