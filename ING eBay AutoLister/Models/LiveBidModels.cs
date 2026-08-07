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
    /// <remarks>
    /// This is the FIRST-item rate: what the show charges to post one win. What each additional win
    /// costs in the same box is <see cref="AdditionalItemShipping"/>, and which of the two this lot
    /// is actually charged is <see cref="Services.LiveShipShare"/>'s answer.
    /// </remarks>
    public decimal? ShippingCost { get; set; }

    /// <summary>
    /// What this show charges for each additional lot in the same box. Null is "nobody said" and
    /// leaves the ceiling on full freight; <b>zero is a real answer</b> — free combined shipping is
    /// the commonest live-selling arrangement there is.
    /// </summary>
    /// <remarks>
    /// A live seller posts one box per show, not one per lot, so charging every lot of the night the
    /// full first-item rate overstates what winning the fourth one costs — by the whole difference,
    /// on the lots where it matters most. See <see cref="Services.LiveShipShare"/> for the three
    /// facts that have to be true before the two rates are allowed to differ.
    /// </remarks>
    public decimal? AdditionalItemShipping { get; set; }

    /// <summary>
    /// Which show this is — the host's handle, or the show's own address pasted from the panel.
    /// Empty means the seller has not said, and nothing is ever combined across an unnamed show:
    /// tonight's buy sheet could be holding lots from three different sellers.
    /// </summary>
    public string? ShowName { get; set; }

    /// <summary>The live platform's buyer premium, as a percentage of the winning bid. Whatnot's is
    /// currently 8% + shipping; it is a field rather than a constant because it varies by platform
    /// and changes without notice.</summary>
    public decimal? BuyerFeePercent { get; set; }

    /// <summary>
    /// The seller's combined state + local sales tax rate, as a percentage. A live marketplace is a
    /// facilitator and collects it on the winning bid at checkout, so it is part of what winning
    /// costs — not something taken out of the profit afterwards.
    /// </summary>
    /// <remarks>
    /// Null is "nobody said" and charges nothing, with a warning saying how big that silence is; a
    /// typed <b>zero is a real answer</b> — five states levy no sales tax at all. The same
    /// distinction the extra-item shipping box draws, and for the same reason. See
    /// <see cref="Services.LiveSalesTax"/>.
    /// </remarks>
    public decimal? SalesTaxPercent { get; set; }

    /// <summary>
    /// Whether a resale certificate is on file with the platform. True charges no sales tax whatever
    /// is in <see cref="SalesTaxPercent"/> — which is the true answer for most resellers on most
    /// lots, and the reason the tax is a state rather than a rate.
    /// </summary>
    public bool? TaxExempt { get; set; }

    /// <summary>The return the seller wants on the money, as a percentage. Null uses the app's own
    /// "worth doing" bar (<see cref="Services.LocalArbitrageAnalyzer.SolidRoiPercent"/>).</summary>
    public decimal? TargetRoiPercent { get; set; }

    /// <summary>
    /// What the seller set aside to spend tonight, all in. Null or zero caps nothing at all — the
    /// ceiling stays the market's, exactly as it was before this field existed.
    /// </summary>
    /// <remarks>
    /// The only figure on this request that is not about the item. A live show puts up a defensible
    /// lot every four minutes and this card says <c>BID UP TO</c> on all of them; what it has never
    /// been able to say is that the money ran out three lots ago. Measured against what tonight's
    /// buy sheet has already committed — see <see cref="Services.LiveBudget"/>, which cuts the
    /// ceiling and never the resale price.
    /// </remarks>
    public decimal? NightBudget { get; set; }

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
    /// How much the bidding goes up by on this show, when the seller has said. Null assumes the
    /// usual live-auction ladder for the level the bid is at (<see cref="Services.LiveBidIncrement"/>).
    /// </summary>
    /// <remarks>
    /// The seller is looking at a screen that states the next bid amount and this app is not, so a
    /// typed figure is used exactly as typed. It changes no price on the card — only the count of
    /// presses left under the ceiling, which is the number the hand is actually about to act on.
    /// </remarks>
    public decimal? BidIncrement { get; set; }

    /// <summary>
    /// What condition the thing on screen is in, when the seller has said: new | likenew | used |
    /// broken. Null or anything unrecognised hands the question back to the lot's own name
    /// (<see cref="Services.LiveCondition"/>), which is the usual case.
    /// </summary>
    /// <remarks>
    /// It never changes what eBay is asked. The comp lookup is a boolean AND over sold titles and
    /// eBay's condition is a field, so putting "used" in the query would return nothing about an
    /// item with a perfectly good used market. This splits the comps already in hand by the
    /// condition they stated — which is why picking it re-answers instantly, with no second read.
    /// </remarks>
    public string? Condition { get; set; }

    /// <summary>
    /// Search eBay for the typed name exactly as written, instead of the cleaned-up version of it.
    /// The undo for a cleaning that took a word the seller wanted — see
    /// <see cref="Services.LiveSearchQuery"/> for what it takes out and why.
    /// </summary>
    public bool? SearchExact { get; set; }

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
    /// What the sold search asked eBay for, and every word of the typed name that was left out of
    /// it. Never null — a card that does not say what it searched for is a card whose comps cannot
    /// be argued with. See <see cref="Services.LiveSearchQuery"/>.
    /// </summary>
    public LiveSearchTerms Search { get; set; } = new();

    /// <summary>
    /// How many things this lot is. Never null — a single item carries a block that says so. When
    /// <see cref="LiveLotUnits.IsLot"/>, every dollar figure on this card is for the WHOLE lot and
    /// the per-unit versions live inside this block.
    /// </summary>
    public LiveLotUnits Units { get; set; } = new();

    /// <summary>
    /// What condition this lot is in, what condition the comps behind it were in, and what the gap
    /// between those two did to the ceiling. Never null — a card that only mentions condition when
    /// it found a mismatch is a card whose silence means both "the comps are the right condition"
    /// and "nothing looked". See <see cref="Services.LiveCondition"/>.
    /// </summary>
    public LiveConditionRead Condition { get; set; } = new();

    /// <summary>
    /// Which way the sold price has been moving, and whether that moved the ceiling. Never null —
    /// a card that only mentions the trend when it found one is a card whose silence means both
    /// "steady" and "never looked". See <see cref="Services.LiveTrend"/>.
    /// </summary>
    public LiveTrendRead Trend { get; set; } = new();

    /// <summary>
    /// How many of this the seller would own if they won it — shelf, tonight's buy sheet and this
    /// lot — and how long eBay takes to absorb that many. Never null, and it changes no price on
    /// this card: saturation is a claim about time, not about what the thing fetches. See
    /// <see cref="Services.LiveStockDepth"/>.
    /// </summary>
    public LiveStockRead Stock { get; set; } = new();

    /// <summary>
    /// How long this lot's units wait behind the stock above before they sell, and what the measured
    /// price slide takes off them across that wait. Never null. The third and last thing on this card
    /// allowed to CUT the ceiling, and the only one that is about the calendar rather than the object
    /// — it is what <see cref="Stock"/> deliberately refuses to price. See
    /// <see cref="Services.LiveHoldCost"/> for the four gates it has to clear first.
    /// </summary>
    public LiveHoldRead Hold { get; set; } = new();

    /// <summary>
    /// What winning this lot really adds to the shipping bill, as opposed to what one lot from this
    /// show costs. Never null — a block that only appears once a saving was found is a block whose
    /// silence means both "you are paying full freight" and "nothing looked". It is the only read on
    /// this card that can RAISE the ceiling, and <see cref="Services.LiveShipShare"/> gates it on
    /// three facts the seller can see.
    /// </summary>
    public LiveShipRead Ship { get; set; } = new();

    /// <summary>
    /// What sales tax adds to winning this lot. Never null — a block that only appeared once a rate
    /// was entered would be a block whose silence meant both "you are exempt" and "nobody ever
    /// asked". It is the fourth part of the landed cost and the only one that was missing from it.
    /// See <see cref="Services.LiveSalesTax"/>.
    /// </summary>
    public LiveTaxRead Tax { get; set; } = new();

    /// <summary>
    /// What the seller has left to spend tonight, and whether the ceiling above is the market's or
    /// the wallet's. Never null, for the same reason as the block above it: silence would mean both
    /// "you have plenty" and "nobody is counting". The only read on this card that can lower the
    /// ceiling without touching the resale price — the item is worth what it is worth; the money is
    /// a separate fact. See <see cref="Services.LiveBudget"/>.
    /// </summary>
    public LiveBudgetRead Budget { get; set; } = new();

    /// <summary>
    /// How many of the sold comps behind this card would actually have covered what winning it
    /// costs. Never null. It moves no price, no ceiling and no call — every other block here answers
    /// "what is this worth" and this one answers "how much of the evidence agrees at the bid on
    /// screen", which is the question a middle cannot answer. See <see cref="Services.LiveOdds"/>.
    /// </summary>
    public LiveOddsRead Odds { get; set; } = new();

    // ── The bid ──────────────────────────────────────────────────────────────
    public decimal CurrentBid { get; set; }
    public bool BidWasKnown { get; set; }

    /// <summary>
    /// What this lot was charged to get delivered — the MARGINAL cost, so on a lot riding along in a
    /// box that is already going out it is the show's extra-item rate rather than its first-item
    /// rate. What was typed in the Shipping box is <see cref="LiveShipRead.FirstItemShipping"/>.
    /// </summary>
    public decimal ShippingCost { get; set; }
    public decimal BuyerFeePercent { get; set; }
    /// <summary>The premium on the CURRENT bid, in cash. Zero when no premium was stated.</summary>
    public decimal BuyerFee { get; set; }
    /// <summary>The sales tax on the CURRENT bid, in cash — charged on the hammer plus the premium.
    /// Zero in three of the four states <see cref="Tax"/> can be in.</summary>
    public decimal SalesTax { get; set; }
    /// <summary>Bid + premium + sales tax + shipping — what winning at the current bid actually
    /// costs.</summary>
    public decimal LandedCostNow { get; set; }

    /// <summary>The highest bid that still clears the target. The number to stop at.</summary>
    public decimal MaxBid { get; set; }
    /// <summary>The highest bid that breaks even. Above this the flip loses money — a walk-away
    /// line, never a target.</summary>
    public decimal BreakEvenBid { get; set; }
    /// <summary>MaxBid − CurrentBid. Negative once the bidding has passed the ceiling.</summary>
    public decimal Headroom { get; set; }

    /// <summary>
    /// What pressing bid actually costs, and how many more presses fit under the ceiling. Never
    /// null — a card that only mentions the next bid when it has bad news about it is a card whose
    /// silence means both "press away" and "nothing looked". See
    /// <see cref="Services.LiveBidIncrement"/> for why the room above the bid and the presses left
    /// inside it are different questions.
    /// </summary>
    public LiveNextBid NextBid { get; set; } = new();
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

/// <summary>
/// What the sold search actually ran on. See <see cref="Services.LiveSearchQuery"/> for the rule
/// that decides what comes out of a live lot's name and what may never be touched.
/// </summary>
/// <remarks>
/// Present on every card, including the ones where nothing was changed. The seller is trusting five
/// statistics to a keyword search they cannot see; a screen that only mentions the search when it
/// edited something is a screen whose silence means two different things.
/// </remarks>
public sealed class LiveSearchTerms
{
    /// <summary>The lot's name exactly as it was typed or pasted.</summary>
    public string Typed { get; set; } = "";

    /// <summary>What eBay's sold history was actually asked for.</summary>
    public string Query { get; set; } = "";

    /// <summary>True when the two differ — the card then offers to search the typed name instead.</summary>
    public bool Changed => !string.Equals(Typed.Trim(), Query.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Every word taken out, with the reason. Shown struck through on the card.</summary>
    public List<LiveSearchDrop> Dropped { get; set; } = [];

    /// <summary>Why the search looks like that, in one sentence.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// Set when the cleaning was refused whole because it would have left nothing to search on.
    /// The typed name was used exactly as written.
    /// </summary>
    public string Refused { get; set; } = "";

    /// <summary>
    /// True when the whole name matched no sold history and the search was cut back to its first
    /// few words to find any at all. The comps are then a ballpark for the model rather than for
    /// the exact lot, and both the card and <see cref="LiveBidCard.Warnings"/> say so.
    /// </summary>
    public bool Widened { get; set; }
    public string WidenedNote { get; set; } = "";

    /// <summary>True when the seller asked for their exact words and nothing was cleaned.</summary>
    public bool AskedForExactly { get; set; }
}

/// <summary>One piece of a live lot's name that the sold search did not ask for.</summary>
public sealed class LiveSearchDrop
{
    /// <summary>The words themselves, as typed.</summary>
    public string Text { get; set; } = "";
    /// <summary>count | chatter | logistics | decoration | widened.</summary>
    public string Kind { get; set; } = "";
    /// <summary>Why it is not part of the search. Shown on the chip.</summary>
    public string Why { get; set; } = "";
}

/// <summary>The five reasons a word is not part of the search. Spelled once.</summary>
public static class LiveSearchDropKinds
{
    /// <summary>How many of them there are — priced by <see cref="Services.LiveLotSize"/> instead.</summary>
    public const string Count = "count";
    /// <summary>Auction and show talk: "no reserve", "going once", a price, a handle.</summary>
    public const string Chatter = "chatter";
    /// <summary>Shipping, returns and payment terms.</summary>
    public const string Logistics = "logistics";
    /// <summary>Emoji and punctuation.</summary>
    public const string Decoration = "decoration";
    /// <summary>Cut to find any sold history at all. See <see cref="Services.LiveSearchQuery.Widen"/>.</summary>
    public const string Widened = "widened";
}

/// <summary>
/// Which way this item's sold price has been moving, and what that was allowed to do to the
/// ceiling. See <see cref="Services.LiveTrend"/> for the measurement and the two guards on it.
/// </summary>
/// <remarks>
/// Present on every card, including the ones the comps could not support a reading for. The price
/// on this card is a claim about tonight built out of sales from the last two months, and a screen
/// that only mentioned the direction of travel when it happened to find one would leave the seller
/// unable to tell "these are holding their price" from "nobody looked".
/// </remarks>
public sealed class LiveTrendRead
{
    /// <summary>False when the comps could not support a reading at all. Every figure below is then
    /// zero and the ceiling is the untouched one.</summary>
    public bool Readable { get; set; }

    /// <summary>falling | rising | steady | stopped | unknown — what the card colours and labels
    /// off. Coarser than <see cref="Signal"/> on purpose: this is read in about a second.</summary>
    public string Direction { get; set; } = LiveTrendDirections.Unknown;

    /// <summary>The measurement's own vocabulary, unchanged: rising_demand | price_climbing |
    /// supply_squeeze | demand_building | cooling | steady | unreadable.</summary>
    public string Signal { get; set; } = "unreadable";

    /// <summary>confirmed | tentative | unreadable. A cut needs confirmed.</summary>
    public string Reliability { get; set; } = "unreadable";

    /// <summary>The move in one glanceable phrase — "Sold prices down 22% — $180 → $140 median".</summary>
    public string Headline { get; set; } = "";

    /// <summary>The measurement's own sentence, including its caveats. Never rewritten here.</summary>
    public string Note { get; set; } = "";

    /// <summary>What this read did or did not do to the ceiling, said on every readable card.</summary>
    public string MoneyNote { get; set; } = "";

    /// <summary>Set only when the direction of travel belongs above the money on the card, in
    /// <see cref="LiveBidCard.Warnings"/>. Empty on a steady or unreadable item.</summary>
    public string Warning { get; set; } = "";

    // ── The measurement ──────────────────────────────────────────────────────
    /// <summary>Length of each of the two windows compared. The read spans twice this.</summary>
    public int WindowDays { get; set; }
    public decimal? PriceChangePercent { get; set; }
    public decimal RecentMedian { get; set; }
    public decimal PriorMedian { get; set; }
    public int RecentSold { get; set; }
    public int PriorSold { get; set; }
    public int TotalCompCount { get; set; }
    public int DatedCompCount { get; set; }
    /// <summary>Theil–Sen slope in dollars a month across every dated sale — the second opinion
    /// that can refuse a cut the two window medians asked for.</summary>
    public decimal? SlopePerMonth { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    /// <summary>What the ceiling's resale price was multiplied by. Exactly 1 unless a confirmed,
    /// material fall survived both guards — and never above 1, whatever the climb.</summary>
    public decimal ResaleMultiplier { get; set; } = 1m;

    /// <summary>True when that multiplier is below 1 and the ceiling really was cut.</summary>
    public bool Discounted { get; set; }

    /// <summary>The cut as a percentage, for the badge on the strip.</summary>
    public decimal CutPercent { get; set; }

    /// <summary>True when the measured fall was deeper than
    /// <see cref="Services.LiveTrend.MaxHaircutPercent"/> and the cut was held at it.</summary>
    public bool Floored { get; set; }
}

/// <summary>The four directions a live card reports. Spelled once so the strip, the tests and the
/// CSS agree.</summary>
/// <remarks>
/// There is deliberately no "stopped selling". It is the reading a bidder would most want, and it
/// is the one thing a single comp lookup cannot honestly produce: an item whose sales have dried up
/// and a comps database that has stopped being ingested look identical from inside one product.
/// <see cref="Services.LiveTrend.SoloCorpus"/> refuses both rather than guessing which.
/// </remarks>
public static class LiveTrendDirections
{
    /// <summary>Selling for materially less than a window ago.</summary>
    public const string Falling = "falling";
    /// <summary>Selling for materially more. Never raises the ceiling.</summary>
    public const string Rising = "rising";
    /// <summary>No material move in either direction.</summary>
    public const string Steady = "steady";
    /// <summary>Not enough dated sold history on both sides of the window to say anything.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// The next press: what it costs, and how many more of them the ceiling has room for. See
/// <see cref="Services.LiveBidIncrement"/>.
/// </summary>
/// <remarks>
/// Present on every card. The distinction it carries is the one the rest of the card cannot make:
/// the ceiling is a price and a bid is a press, and a lot with a dollar of room above the bid can
/// have no bid left to make on it.
/// </remarks>
public sealed class LiveNextBid
{
    /// <summary>False when there is no next bid to cost — no ceiling, or the bidding has not
    /// started. Every dollar figure below is then zero and <see cref="Headline"/> says which.</summary>
    public bool Readable { get; set; }

    /// <summary>press | last | stop | over | unreadable. What the hand should do.</summary>
    public string Verdict { get; set; } = LiveNextBidVerdicts.Unreadable;

    /// <summary>How much one bid goes up by, as used. Always positive on a readable read.</summary>
    public decimal Increment { get; set; }

    /// <summary>seller | assumed — whether anybody actually said. Assumed is the usual case and the
    /// card states the figure either way, because an assumption nobody can see is one nobody can
    /// correct.</summary>
    public string IncrementSource { get; set; } = Services.LiveBidIncrement.SourceAssumed;

    /// <summary>That, in a sentence, including how to overrule it.</summary>
    public string IncrementNote { get; set; } = "";

    /// <summary>The bid that pressing produces — current bid plus one increment.</summary>
    public decimal Amount { get; set; }

    /// <summary>What winning at that bid costs all in: the bid, the premium and the shipping.</summary>
    public decimal Landed { get; set; }

    /// <summary>What is left after eBay's cut and shipping it on, if it hammers at
    /// <see cref="Amount"/>. Negative when the press would lose money — deliberately not clamped
    /// at zero, unlike the profit at the ceiling.</summary>
    public decimal Profit { get; set; }

    /// <summary>How many more presses land at or under the ceiling. Zero is the case this whole
    /// block exists for: room above the bid, and nothing to do with it.</summary>
    public int BidsLeft { get; set; }

    /// <summary>True when counting stopped at <see cref="Services.LiveBidIncrement.MaxBidsCounted"/>
    /// rather than at the ceiling — the card then says "40+" instead of a number nobody counts.</summary>
    public bool BidsLeftCapped { get; set; }

    /// <summary>The glance version: "2 more bids", "Last bid", "Don't press".</summary>
    public string Headline { get; set; } = "";

    /// <summary>The same thing in this lot's own numbers, for the second glance.</summary>
    public string Note { get; set; } = "";

    /// <summary>Set only in the case worth interrupting the warning list for: the ceiling is above
    /// the bid and no press stays under it.</summary>
    public string Warning { get; set; } = "";
}

/// <summary>What to do with the bid button. Spelled once so the strip, the speech and the CSS
/// agree.</summary>
public static class LiveNextBidVerdicts
{
    /// <summary>Two or more presses left. The ordinary case.</summary>
    public const string Press = "press";

    /// <summary>Exactly one press left under the ceiling. The state nothing on this screen has ever
    /// reported, and the one a bidder most needs to hear.</summary>
    public const string Last = "last";

    /// <summary>Still under the ceiling, and the next press is past it. No bid left to make.</summary>
    public const string Stop = "stop";

    /// <summary>The bidding is already past the ceiling.</summary>
    public const string Over = "over";

    /// <summary>Nothing to count — no ceiling, or no bidding yet.</summary>
    public const string Unreadable = "unreadable";
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
