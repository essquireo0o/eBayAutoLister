namespace ING_eBay_AutoLister.Models;

// ── The live-auction arbitrage card (see Services/LiveBidAdvisor.cs) ──────────────────────────
// An item is on screen in a live-selling feed, the bidding is running, and the seller has seconds
// to decide. Every other sourcing screen in this app answers that question with a table and a
// desk; this one answers it with one ceiling and the statistics behind it, before the hammer.
//
// The resale side is not new and deliberately so: it is the same AnalyzeProductAsync → eBay sold
// comps → sell-through pipeline the Opportunity Finder, Local Deals and the auction sniper run. A
// live card that disagreed with the board about the same item would mean the app has two opinions
// and the bidder has none. What is different is that the price is a BID — it moves, a buyer's
// premium sits on top of it, and shipping has to come out of it rather than out of the profit.

/// <summary>One "should I bid on this?" question, asked about the thing currently on screen.</summary>
public sealed class LiveBidRequest
{
    /// <summary>What it is, typed or pasted off the feed. The comp lookup runs against this, so it
    /// is the one field that has to be right.</summary>
    public string? Title { get; set; }

    /// <summary>Where the bidding is right now. Null/zero means it hasn't started — the answer is
    /// then the ceiling alone, which is the useful output before the first bid anyway.</summary>
    public decimal? CurrentBid { get; set; }

    /// <summary>What it costs to get it delivered to you. Part of what winning costs, so it comes
    /// out of the bid rather than out of the profit afterwards.</summary>
    public decimal? ShippingCost { get; set; }

    /// <summary>The live platform's buyer premium, as a percentage of the winning bid. Whatnot's is
    /// currently 8% + shipping; it is a field rather than a constant because it varies by platform
    /// and changes without notice.</summary>
    public decimal? BuyerFeePercent { get; set; }

    /// <summary>The return the seller wants on the money, as a percentage. Null uses the app's own
    /// "worth doing" bar (<see cref="Services.LocalArbitrageAnalyzer.SolidRoiPercent"/>).</summary>
    public decimal? TargetRoiPercent { get; set; }

    /// <summary>Optional category override, same ids as the Local Deals board.</summary>
    public string? CategoryId { get; set; }

    /// <summary>
    /// How many of the thing are in the lot, when the seller has said. Null lets the lot's own name
    /// answer (<see cref="Services.LiveLotSize"/>), which is the usual case; a number typed here
    /// outranks the name, including a 1 — that is the undo for a count read wrong.
    /// </summary>
    /// <remarks>
    /// Sold comps are per unit everywhere in this app, so the whole card is a per-unit answer until
    /// this says otherwise. On a live show that is wrong often enough to matter: "3x Antminer S9" is
    /// three miners and one hammer price.
    /// </remarks>
    public int? Quantity { get; set; }

    /// <summary>
    /// The handle on comps already read for this lot (<see cref="Services.LiveBidBoard"/>). Present
    /// on a re-price, absent on a first price. It buys speed and nothing else: the ceiling is
    /// recomputed in full, against the same sold history, by the same function.
    /// </summary>
    public string? Token { get; set; }
}

/// <summary>The show's upcoming lots, pasted. See <see cref="Services.LiveLotList"/>.</summary>
public sealed class LiveLotListRequest
{
    /// <summary>The lot list exactly as it was copied — lot numbers, asking prices and all. What
    /// the app made of each line comes back in the plan, so the cleaning is visible rather than
    /// something that happened to the seller's paste on the way past.</summary>
    public string? Text { get; set; }
}

/// <summary>One sold comp, flattened for the card — the sale, what it went for, and when.</summary>
public sealed class LiveBidComp
{
    public string Title { get; set; } = "";
    public decimal SoldPrice { get; set; }
    public decimal Shipping { get; set; }
    public decimal TotalPrice { get; set; }
    public string Condition { get; set; } = "";
    public DateTime? SoldDate { get; set; }
    /// <summary>How many days ago that sale happened. Null when the row carried no date, which is
    /// itself worth showing — an undated comp is evidence of a price, not of a market.</summary>
    public int? AgeDays { get; set; }
    public string Url { get; set; } = "";
}

/// <summary>
/// The answer: one ceiling, the eBay resale statistics it was derived from, and how much of it to
/// believe.
/// </summary>
public sealed class LiveBidCard
{
    public string Status { get; set; } = "ok";     // ok | error
    public string Error { get; set; } = "";

    // ── The call ─────────────────────────────────────────────────────────────
    /// <summary>bid | risky | stop | no_data. See <see cref="Services.LiveBidAdvisor"/>.</summary>
    public string Call { get; set; } = LiveBidCalls.NoData;
    /// <summary>The badge: BID UP TO $40, RISKY — UP TO $40, STOP, CAN'T PRICE IT.</summary>
    public string CallLabel { get; set; } = "";
    /// <summary>The one line under it, in this item's own numbers. Never generic.</summary>
    public string Reason { get; set; } = "";

    /// <summary>
    /// The whole card in one sentence — the call, the ceiling, where the bidding is against it and
    /// what the thing resells for. See <see cref="Services.LiveBidSpeech"/> for what it refuses to
    /// claim. The screen paints this verbatim into its one live region and uses it as the label a
    /// lot row is read out as; nothing in the browser assembles it, so the list and the card cannot
    /// say different things about the same lot.
    /// </summary>
    public string Say { get; set; } = "";

    // ── What was priced ──────────────────────────────────────────────────────
    public string Item { get; set; } = "";
    /// <summary>The title the comp lookup actually ran against, when it differs from Item.</summary>
    public string PricedAs { get; set; } = "";
    public string CategoryLabel { get; set; } = "";

    /// <summary>
    /// How many things this lot is. Never null — a single item carries a block that says so. When
    /// <see cref="LiveLotUnits.IsLot"/>, every dollar figure on this card is for the WHOLE lot and
    /// the per-unit versions live inside this block.
    /// </summary>
    public LiveLotUnits Units { get; set; } = new();

    // ── The bid ──────────────────────────────────────────────────────────────
    public decimal CurrentBid { get; set; }
    public bool BidWasKnown { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal BuyerFeePercent { get; set; }
    /// <summary>The premium on the CURRENT bid, in cash. Zero when no premium was stated.</summary>
    public decimal BuyerFee { get; set; }
    /// <summary>Bid + premium + shipping — what winning at the current bid actually costs.</summary>
    public decimal LandedCostNow { get; set; }

    /// <summary>The highest bid that still clears the target. The number to stop at.</summary>
    public decimal MaxBid { get; set; }
    /// <summary>The highest bid that breaks even. Above this the flip loses money — a walk-away
    /// line, never a target.</summary>
    public decimal BreakEvenBid { get; set; }
    /// <summary>MaxBid − CurrentBid. Negative once the bidding has passed the ceiling.</summary>
    public decimal Headroom { get; set; }
    public decimal ProfitAtMaxBid { get; set; }
    /// <summary>roi | cash — which of the two bars set the ceiling. A percentage has no size, so
    /// the cash floor binds on cheap items and the return binds on expensive ones.</summary>
    public string CeilingBoundBy { get; set; } = "";
    /// <summary>That, in words. Written here rather than in the browser so the bar's own dollar
    /// figure has exactly one definition in the app.</summary>
    public string CeilingNote { get; set; } = "";
    public decimal TargetRoiPercent { get; set; }

    /// <summary>Money and return at the bid on screen right now. Null before the first bid.</summary>
    public decimal? ProfitNow { get; set; }
    public decimal? RoiNow { get; set; }
    public decimal? MarginNow { get; set; }
    public decimal? EstimatedFees { get; set; }
    public decimal? EstimatedShipCost { get; set; }

    // ── What it resells for ──────────────────────────────────────────────────
    public decimal? ResalePrice { get; set; }
    public decimal? MedianPrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    /// <summary>The middle half of the sold prices — 25th and 75th percentile. The spread is the
    /// honest version of "it sells for $X": a $40–$300 middle half is not a price, it is a range
    /// the item lands somewhere in depending on what is wrong with it.</summary>
    public decimal? PriceLow { get; set; }
    public decimal? PriceHigh { get; set; }
    public decimal? PriceFloor { get; set; }
    public decimal? PriceCeiling { get; set; }

    // ── Whether it moves ─────────────────────────────────────────────────────
    public decimal? SellThroughRate { get; set; }
    /// <summary>Excellent | Very Strong | Good | Moderate | Weak | Poor | Unknown.</summary>
    public string SellThroughLabel { get; set; } = "Unknown";
    public int SellThroughScore { get; set; }
    /// <summary>True when sold comps exist but nothing is currently listed, so the rate has no
    /// denominator. The card shows "—" rather than inventing a 100%.</summary>
    public bool SellThroughUnbounded { get; set; }
    public int ActiveCompCount { get; set; }
    public decimal EstimatedMonthlySales { get; set; }
    public int? DaysToSell { get; set; }
    public int? DaysToCash { get; set; }
    public string SpeedLabel { get; set; } = "";
    public string LiquidityLevel { get; set; } = "";

    // ── How much to believe it ───────────────────────────────────────────────
    public int CompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "";
    /// <summary>confident | low | none — the same grading every other board dims its percentages
    /// on (<see cref="Services.LocalArbitrageAnalyzer.GradeEvidence"/>).</summary>
    public string EvidenceTier { get; set; } = LocalArbitrageEvidence.None;
    public string EvidenceNote { get; set; } = "";
    public bool IdentityVerified { get; set; } = true;

    /// <summary>How old the evidence is, in words. A price is a claim about now made out of sales
    /// that happened in the past, and how far in the past is the difference between evidence and an
    /// anecdote — said out loud rather than left to the comp list.</summary>
    public string FreshnessNote { get; set; } = "";
    public DateTime? NewestCompUtc { get; set; }
    public DateTime? OldestCompUtc { get; set; }
    public int? NewestCompAgeDays { get; set; }

    /// <summary>
    /// What THIS seller has done with this product before — their own completed sales of it and the
    /// units of it they are already sitting on, priced at this auction's terms. Null when nothing
    /// read their book; see <see cref="Services.OwnTrackRecord"/> for what it refuses to claim.
    /// It never moves the badge: the call above is the market's, and this is the seller's.
    /// </summary>
    public LiveOwnHistory? OwnHistory { get; set; }

    /// <summary>eBay's own sold-and-completed search for this item. The bidder's own eyes are the
    /// last check, and on a thin card they are the only one worth having.</summary>
    public string SoldSearchUrl { get; set; } = "";
    public List<LiveBidComp> Comps { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public long ElapsedMs { get; set; }

    // ── Moving the bid without re-reading eBay ────────────────────────────────
    // The bid climbs every few seconds; the sold history behind it does not change between one bid
    // and the next. So the comps are held (Services/LiveBidBoard.cs) and the ceiling is recomputed
    // against them — the same Build, the same arithmetic, no comp lookup. See RepricedFromHeldComps.

    /// <summary>The handle that re-prices this same lot at a new bid. Empty when nothing was held,
    /// which is the client's signal that a change of bid costs a fresh read of eBay.</summary>
    public string Token { get; set; } = "";

    /// <summary>When the sold comps behind this card were read.</summary>
    public DateTime? PricedAtUtc { get; set; }

    /// <summary>How long ago that was, in seconds. Zero on a fresh price; printed on every re-price,
    /// because "instant" is only honest while the thing it skipped is still true.</summary>
    public int CompsAgeSeconds { get; set; }

    /// <summary>
    /// True when this card was computed against comps already in hand rather than a fresh eBay read.
    /// Every other number on it means exactly what it means on a fresh card — this says only that
    /// the sold history is <see cref="CompsAgeSeconds"/> old, and the screen says so out loud.
    /// </summary>
    public bool RepricedFromHeldComps { get; set; }

    /// <summary>
    /// Where this lot belongs among the others on a show's list — higher is worth being there for.
    /// See <see cref="Services.LiveBidAdvisor.RankLot"/> for what it is made of and why it is made
    /// on this side. On a single typed card it is computed and ignored, which costs nothing.
    /// </summary>
    public decimal LotRank { get; set; }
}

/// <summary>
/// How many things the lot is, and what that does to the money. See
/// <see cref="Services.LiveLotSize"/> for what it refuses to guess.
/// </summary>
/// <remarks>
/// Present on every card, including single items — a block that only appears when the app thinks
/// something is a lot is a block whose silence is indistinguishable from not having looked. On a
/// single item it says "priced as a single item" and every per-unit figure equals its lot figure.
/// </remarks>
public sealed class LiveLotUnits
{
    /// <summary>Units the ceiling above was computed for. Never below 1.</summary>
    public int Count { get; set; } = 1;

    /// <summary>single | title | seller — where <see cref="Count"/> came from.</summary>
    public string Source { get; set; } = Services.LiveLotSize.SourceSingle;

    /// <summary>The text in the lot's name that stated the count — "3x", "(4)", "lot of 5". Empty
    /// when the seller typed it or when there is no count.</summary>
    public string Evidence { get; set; } = "";

    /// <summary>What was priced, in one sentence. Said on every card.</summary>
    public string Note { get; set; } = "";

    /// <summary>True when this is more than one item, so the money below is lot money.</summary>
    public bool IsLot => Count > 1;

    /// <summary>
    /// The name says "several" and never says how many, so it was priced as ONE. The seller can fix
    /// this in a keystroke and nothing else on the screen can fix it at all.
    /// </summary>
    public bool CountUnstated { get; set; }
    public string UnstatedNote { get; set; } = "";

    /// <summary>Set when a count WAS found and deliberately not used — a spec caught by a count
    /// pattern, or a quantity past what this screen will price in one lot.</summary>
    public string Refused { get; set; } = "";

    // ── The same money, per unit ─────────────────────────────────────────────
    // The card's headline figures are the lot's. These are what one of them is worth, because that
    // is the number the seller compares against the single unit they priced ten minutes ago.
    public decimal? ResalePerUnit { get; set; }
    public decimal MaxBidPerUnit { get; set; }
    public decimal ProfitPerUnit { get; set; }

    // ── What it costs in time ────────────────────────────────────────────────
    /// <summary>Units divided by how many of them sell a month. Null with no dated sold history.</summary>
    public decimal? MonthsToClear { get; set; }
    /// <summary>How long until the LAST one is sold — the first sells at the market's ordinary
    /// speed and each one after it waits for the demand the one before it used up.</summary>
    public int? DaysToSellAll { get; set; }
    public string AbsorptionNote { get; set; } = "";
}

/// <summary>The four answers. Spelled once so the badge, the tests and the CSS agree.</summary>
public static class LiveBidCalls
{
    /// <summary>Priced on evidence worth bidding against. The ceiling holds.</summary>
    public const string Bid = "bid";
    /// <summary>There is money in it and the sold history behind that money is thin.</summary>
    public const string Risky = "risky";
    /// <summary>The bidding has passed the ceiling, or no bid makes this work at all.</summary>
    public const string Stop = "stop";
    /// <summary>No sold history matched, so there is nothing to bid against.</summary>
    public const string NoData = "no_data";
}
