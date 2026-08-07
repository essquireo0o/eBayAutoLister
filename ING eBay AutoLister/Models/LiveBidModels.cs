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

    // ── What was priced ──────────────────────────────────────────────────────
    public string Item { get; set; } = "";
    /// <summary>The title the comp lookup actually ran against, when it differs from Item.</summary>
    public string PricedAs { get; set; } = "";
    public string CategoryLabel { get; set; } = "";

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

    /// <summary>eBay's own sold-and-completed search for this item. The bidder's own eyes are the
    /// last check, and on a thin card they are the only one worth having.</summary>
    public string SoldSearchUrl { get; set; } = "";
    public List<LiveBidComp> Comps { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public long ElapsedMs { get; set; }
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
