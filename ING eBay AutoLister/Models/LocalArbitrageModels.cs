using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Models;

// ── Local arbitrage ("buy local, resell on eBay") ─────────────────────────────
// Section 10 (FacebookMarketplaceService) answers "what is for sale near me?".
// This answers the question that actually makes money: "which of those is worth
// driving to?" — every local ask priced against real eBay sold data (the hosted
// sold-comps database first, Terapeak second) and run through the same
// ProfitCalculator/FeeProfile the rest of the app uses, so the number on screen
// is net of fees rather than a gross spread.
// See Services/LocalArbitrageAnalyzer.cs.

// One local listing, priced. The local half comes straight from the source's
// listing; the resale half is shared by every listing of the same product (one comp
// lookup serves all of them), and only the money columns are per-listing.
public class LocalArbitrageOpportunity
{
    // ── The local buy ────────────────────────────────────────────────────────
    // Which site to go buy it on: craigslist | facebook | ... One ranked table mixes them, so
    // the row has to say where it came from — the drive and the haggling differ by site.
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public decimal LocalAsk { get; set; }
    // Set when the seller struck their own price through — a motivated seller.
    public decimal? OriginalPrice { get; set; }
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    public string PostedAgo { get; set; } = "";
    // Only the sources that publish a real timestamp set this (Craigslist does, Facebook doesn't).
    public DateTime? PostedUtc { get; set; }

    // ── When the buy is retail, not a stranger ───────────────────────────────
    // A clearance item off a deal feed is bought differently from a Craigslist pickup, in two ways
    // that both cost money: the register adds sales tax, and there is nobody to haggle with. Both
    // are priced in rather than glossed over — see Services/RetailBuyCosts.cs.
    public bool IsRetail { get; set; }
    public string Retailer { get; set; } = "";
    public bool FreeShipping { get; set; }
    public string CouponCode { get; set; } = "";
    // Sales tax on LocalAsk, and what therefore actually leaves the wallet. Null on a private-party
    // buy, where the ask IS the cost. Every profit figure on a retail row is computed from
    // BuyCostAllIn, never from LocalAsk. A liquidation row fills both in too — an auction charges
    // tax like a till does, and a buyer's premium on top (see Liquidation below).
    public decimal? SalesTax { get; set; }
    public decimal? BuyCostAllIn { get; set; }

    // ── When the buy is an auction lot, not an item on a shelf ───────────────
    // Null on every other source. Non-null means LocalAsk is a BID rather than a price — so
    // MaxBuyPrice is the highest bid worth making rather than the highest sticker worth paying —
    // and that the row may be several units of one product, priced per unit through the same grade
    // assumptions the Liquidation Lot Analyzer applies to a pasted manifest.
    // See Services/LiquidationLotPricer.cs.
    public LiquidationLotEconomics? Liquidation { get; set; }

    // ── When the buy is free, or nearly ──────────────────────────────────────
    // Null on every other source. Non-null means LocalAsk is zero (or nearly) and the interesting
    // number is not the margin — it is what "free" still costs and how long there is to act. A
    // rebate's sales tax is never refunded, a claim can go unpaid, and a free classified post is
    // claimed the same day. See Services/FreebiePricer.cs.
    public FreebieEconomics? Freebie { get; set; }

    // ── When the item is used but still covered ──────────────────────────────
    // Null on every row whose listing said nothing about warranty. Non-null means the listing either
    // claims remaining cover or explicitly disclaims it — and both change what this row is worth.
    // Remaining, transferable, STATED cover is the one thing in this app allowed to lift a resale
    // estimate above the sold comps, capped hard and gated on evidence; a stated "as-is, no returns"
    // on an expensive buy holds the verdict down instead. See Services/WarrantyPricer.cs.
    public WarrantyEconomics? Warranty { get; set; }

    // ── When a code cuts the buy price ───────────────────────────────────────
    // Null on every non-retail row (nobody on Craigslist takes a promo code) and on any retail row
    // no public code was found for. Non-null means the same flip costs less than LocalAsk if the
    // code works — and every figure in it is a SECOND set of numbers, deliberately not folded into
    // the ones above: a public code is a claim, not a price, and a dead one must never be able to
    // move a row up the ranking. See Services/CouponStacker.cs.
    public CouponSavings? Coupons { get; set; }

    // ── What kind of thing this is ───────────────────────────────────────────
    // Set on every row, including the ordinary parcel ones. It decides all three of the answers
    // below it: what was allowed to value it (Valuation), what selling it costs (Category), and
    // which filter it sits under on the board. See Services/ResaleCategoryCatalog.cs.
    public string CategoryId { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    // Year / make / model / mileage, on the rows that have them. Null everywhere else.
    public VehicleDetails? Vehicle { get; set; }

    // How the money below was costed, in words: the fee basis, whether anything ships, and the
    // title and transport a parcel row has no line for. Never null on a priced row — a net profit
    // computed with no eBay fee and no shipping is a good number and a misleading one if the row
    // doesn't say that is what happened. See Services/CategoryCosts.cs.
    public CategoryEconomics? Category { get; set; }

    // Where the resale price came from, or the reason there isn't one. The rule it carries: this
    // app never invents a resale price for a category it can't value. A refused row keeps its
    // listing, its ask and a prefilled sold-listings search, and shows dashes where the money
    // would be. See Services/ResaleValuation.cs.
    public ResaleValuation? Valuation { get; set; }

    // ── The eBay resale ──────────────────────────────────────────────────────
    // Blended median across whichever sources had data, and the price the profit
    // math actually used (MarketPriceEstimator's expected sale price).
    public decimal? EbayResaleMedian { get; set; }
    public decimal? EbayExpectedSale { get; set; }
    public decimal? EbayQuickSale { get; set; }
    // hosted_comps | terapeak | hosted_comps+terapeak | none
    public string ResaleSource { get; set; } = "none";
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public decimal SoldCompWeightPercent { get; set; }
    public decimal TerapeakWeightPercent { get; set; }

    // ── How much to believe the four percentages below ───────────────────────
    // SoldCompCount is how many comps the lookup RETURNED. This is how many of them actually
    // produced the resale price, once the identity guard, outlier removal and the strong-match
    // filter had run — and it is the number that decides whether this row's ROI is a market rate or
    // one loose sale multiplied out. A twelve-comp search that prices off one comp is the single
    // most common way this board publishes a 698% return on a $60 item.
    public int PricedCompCount { get; set; }
    // False when this item states a model or part number and NO sold comp title carried it: the
    // resale price is then a price for a different product that happens to share the brand.
    public bool IdentityVerified { get; set; } = true;
    // confident | low | none — how the money columns should be read, decided by
    // LocalArbitrageAnalyzer.GradeEvidence. Only "confident" earns a Worth-it/goldmine badge and an
    // ROI presented as a fact; "low" means the arithmetic is right and the price under it is a
    // guess, and the UI dims it and says so rather than letting a jackpot percentage stand.
    public string EvidenceTier { get; set; } = "none";
    // The one sentence that explains the tier, in the row's own numbers. Shown next to the dimmed
    // figures — a greyed-out 698% with no explanation reads as a rendering bug, not as a warning.
    public string EvidenceNote { get; set; } = "";
    // The title the comp lookup was actually run against — often a sibling
    // listing's fuller title, so it needs to be visible rather than implied.
    public string PricedAs { get; set; } = "";
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    // The working behind both numbers above and behind every money column on this row: what each
    // sold-data source said on its own, how much of the blend is its opinion, and where the missing
    // confidence points went. A price nobody can take apart is a price nobody can check, and this
    // board is read by someone about to hand over cash. Null on a row nothing priced, and on the
    // hand-built pricings that never ran a lookup. See Services/PriceBasis.cs.
    public PriceBasis? PriceBasis { get; set; }
    public string? DisagreementMessage { get; set; }
    // How fast it moves, from the sold-history date density the comps lookup already computed
    // (LiquidityScoringService) — profit you can't realise for six months isn't the same deal.
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";

    // ── The wait ─────────────────────────────────────────────────────────────
    // How long the buy price stays spent: days to sell, plus ship and payout time. Flattened onto
    // the row (rather than nested) because it is sorted on and rendered as columns —
    // see Services/DaysToCashEstimator.cs for what each one means.
    public int? DaysToSell { get; set; }
    public int CashPipelineDays { get; set; }
    public int? DaysToCash { get; set; }
    // Net profit per day of tied-up cash: the ranking key behind "fastest profit".
    public decimal? ProfitPerDay { get; set; }
    public decimal? CapitalTurnsPerYear { get; set; }
    public decimal? AnnualizedRoiPercent { get; set; }
    // fast | steady | slow | dead_money | unknown
    public string SpeedTier { get; set; } = "unknown";
    public string SpeedLabel { get; set; } = "Speed unknown";
    public string SpeedNote { get; set; } = "";

    // ── The money ────────────────────────────────────────────────────────────
    // eBay final value + promoted + payment processing, per FeeProfile.
    public decimal? EstimatedFees { get; set; }
    // Cost to ship it to the buyer (+ packaging/labor when configured). Treated
    // as a cost, never as extra revenue — see LocalArbitrageAnalyzer.
    public decimal? EstimatedShipCost { get; set; }
    public decimal? NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    // The highest local ask that still breaks even — the number to walk into a
    // negotiation with, which a bare profit figure doesn't give you.
    public decimal? MaxBuyPrice { get; set; }

    // goldmine | solid | thin | pass | no_data
    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";

    // ── The buy side ─────────────────────────────────────────────────────────
    // What to open at, where to stop, and the message to send — anchored on the same comps that
    // produced the numbers above. Null on rows that couldn't be priced: there is nothing honest to
    // negotiate against without sold history. See Services/NegotiationAdvisor.cs.
    public NegotiationPlan? Negotiation { get; set; }
}

public class LocalArbitrageResult
{
    // Rolled up across every source that was searched (LocalSupplyMerger.RollUpStatus), so one
    // disconnected site never blanks a ranking another site filled: ok | not_connected |
    // session_expired | error | skipped | no_sources. The UI keeps its per-source connect prompts.
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }
    // The first searched source's own results URL, kept so the existing "open the search" link
    // still works; Sources below carries one per site.
    public string SearchUrl { get; set; } = "";
    public string? Error { get; set; }

    // What each site contributed, including the ones that couldn't answer and why. On this board
    // they also carry what became of their listings past the search — analyzed, ranked, and the
    // money each site is responsible for. See Services/LocalSupplyAttribution.cs.
    public List<LocalSupplySourceOutcome> Sources { get; set; } = [];

    // The site holding the most profit on this board, or empty when nothing on it is profitable.
    // Ranked on money and not on rows: one $340 flip beats nine $6 ones, and which site to open
    // first next weekend is the decision a sourcing seller repeats more often than any other.
    // Never a recommendation among losers — see LocalSupplyAttribution.BestEarner.
    public string BestSourceId { get; set; } = "";
    public string BestSourceLabel { get; set; } = "";

    // ── What was searched for, and what came back ────────────────────────────
    // The category the seller pointed the scan at — "anything" unless they picked one. Echoed back
    // because it changes what was searched (a craigslist board rather than the for-sale board), not
    // just how the results are shown.
    public string CategoryId { get; set; } = ResaleCategoryCatalog.AnythingId;
    public string CategoryLabel { get; set; } = "Anything";
    // Every category actually on this board, with its row count — the data behind the board's
    // category filter. A scan for "anything" routinely comes back with six of these.
    public List<ResaleCategoryTally> Categories { get; set; } = [];
    // Rows the app deliberately refused to price, because nothing it can read values that kind of
    // thing. Counted and surfaced rather than hidden: on a scan of the cars board this is most of
    // the board, and a column of dashes with no explanation reads as a broken feature instead of as
    // the honest answer it is. See Services/ResaleValuation.cs.
    public int ManualValuationCount { get; set; }

    // Listings dropped before pricing because they weren't the product — repair services, manuals,
    // core charges. Counted and said out loud for the same reason as the line above: a seller who
    // searches a part number and sees "8 hidden" can tell the app screened them from believing the
    // search came back thin. See Services/NonItemListingDetector.cs.
    public int NotTheItemCount { get; set; }

    public List<LocalArbitrageOpportunity> Items { get; set; } = [];

    /// <summary>
    /// What the seller typed, when the search that produced these results was a spelling correction
    /// of it. Null when nothing was corrected.
    /// </summary>
    /// <remarks>
    /// Both halves are kept so the UI can say "showing results for X - search instead for Y", the
    /// way a search engine does. eBay returns a bare <c>total: 0</c> for a typo with no correction
    /// and no hint, which reads as "there is nothing out there" rather than "you slipped a key" -
    /// a verdict on the market instead of on the spelling.
    /// </remarks>
    public string? CorrectedFrom { get; set; }

    /// <summary>The corrected spelling actually searched. Null when nothing was corrected.</summary>
    public string? CorrectedTo { get; set; }
    public int Count => Items.Count;

    // ── What the run actually did, so the numbers can be judged ──────────────
    public int LocalListingsFound { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }        // distinct products, after grouping
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }

    // ── The live half of the sold-comps path ─────────────────────────────────
    // Every row is priced against stored sold comps first and, for the products that leaves thin,
    // a live eBay sold-price lookup filed into the same database and re-read — the path the eBay
    // scanner's rows always had and a Facebook Marketplace feed never did. How many lookups this
    // scan spent (one API call each, off the same daily allowance the browser's own lookups use),
    // how many products came back re-priced on the rows they filed, and — when the pass stopped
    // early — the lookup's own sentence saying why. See Services/LiveCompsPass.cs.
    public int LiveLookupsUsed { get; set; }
    public int LiveLookupsRefreshed { get; set; }
    public string LiveLookupNote { get; set; } = "";
    public bool SoldCompsConfigured { get; set; }
    // Set when neither pricing source can answer — the table would otherwise be
    // a column of dashes with no explanation.
    public string? DataWarning { get; set; }

    public int GoldmineCount { get; set; }
    // Profitable rows whose money is expected back inside DaysToCashEstimator.FastCashDays — the
    // count a seller with limited cash actually shops from.
    public int FastCashCount { get; set; }
    public decimal TotalPotentialProfit { get; set; }

    // ── What haggling is worth on this board ─────────────────────────────────
    // Rows with a drafted offer worth sending, and the total gap between those asking prices and
    // those opening offers. A ceiling on the buy-side saving, not a forecast — nobody accepts every
    // opening offer — but every dollar of it is profit with no fee and no wait attached.
    public int NegotiableCount { get; set; }
    public decimal NegotiationUpside { get; set; }

    // ── What the liquidation half of the board found ─────────────────────────
    // Counted separately because they are a different kind of buy: they close on a deadline, they
    // cost a premium on top of the bid, and the profitable ones are usually the cheapest rows on
    // the board rather than the biggest. A seller who scanned four sites needs to know whether the
    // money is in the auctions before the clock runs out on them.
    public int LiquidationCount { get; set; }
    // Profitable liquidation rows whose auction closes inside LiquidationLotPricer.ClosingSoonHours.
    public int ClosingSoonCount { get; set; }

    // ── What the free half of the board found ────────────────────────────────
    // Counted separately because it is the one kind of row where the money is all upside: nothing
    // was spent, so there is nothing to lose and no ROI to compare. A seller with no cash at all can
    // work this column and nothing else on the board.
    public int FreebieCount { get; set; }
    // Everything the profitable free rows would make. Not "potential profit less what it cost" —
    // on these there is nothing to subtract, which is the entire point of the board.
    public decimal FreeMoneyOnTheTable { get; set; }
    // Profitable free rows with a stated deadline inside the day, or a local post that will be gone
    // as soon as somebody drives over. The count that decides whether this list is read now or
    // tomorrow.
    public int ExpiringTodayCount { get; set; }

    // ── What the warranty finder found ───────────────────────────────────────
    // Counted separately because these are a different KIND of row rather than a better-priced one:
    // a used item with cover left is the same flip with the downside cut off. A seller who cannot
    // afford one bad buy shops this column first, whatever the profit ranking says.
    public int WarrantyCount { get; set; }
    // Profitable rows whose remaining cover transfers to the buyer — the ones the seller can
    // advertise "still under warranty until X" on, which is where the premium below comes from.
    public int TransferableWarrantyCount { get; set; }
    // Extra resale value across the board that came from stated, transferable, remaining cover.
    // Already inside TotalPotentialProfit, unlike the coupon and negotiation figures — this is not a
    // claim about a code that might be dead, it is a claim about the goods that the seller can
    // repeat in their own listing. Reported separately anyway so it can be subtracted back out by
    // anyone who wants the bare-comps number.
    public decimal WarrantyUpliftOnTheTable { get; set; }
    // Rows the listing states are sold as-is with no returns, at a price big enough for that to be
    // the loss. The count that stops a green badge from reading as "commit four figures to a
    // stranger's word about a thing you cannot return".
    public int AsIsRiskCount { get; set; }

    // ── What the coupon lists found on the buy side ──────────────────────────
    // Counted separately from every other figure on this result, and never added into
    // TotalPotentialProfit, because a public promo code is a claim rather than a price. What these
    // say is "there is more money here if the codes work", which is worth acting on and is not the
    // same statement as the board's own total.
    public int CouponedCount { get; set; }
    // Extra profit across the rows a code improves — the money on the buy side, which arrives with
    // no eBay fee and nothing to ship. A ceiling, exactly like NegotiationUpside: nobody's every
    // code works.
    public decimal CouponSavingsOnTheTable { get; set; }
    // Rows that make no money at the shelf price and do make money with a code. The most valuable
    // count here: these are deals the board would otherwise have told the seller to walk away from.
    public int CouponRescuedCount { get; set; }
    // Which stores were checked, what each one had, and the code lists that have to be opened by
    // hand. Present even when nothing was found — "we looked at Amazon and Amazon doesn't do codes"
    // is an answer, and a silent empty column is not.
    public List<CouponStoreOutcome> CouponStores { get; set; } = [];
}

// One deals-board row, handed back so the server can reprice just that row after the browser has
// run a live sold-comps lookup for it. It is the buy side of a LocalArbitrageOpportunity read back
// off the board — enough to reconstruct the LocalSupplyListing it came from, so the same
// analyzer.Build the scan used can re-cost it against the now-deepened comps database. The resale
// half (fees, net, evidence) is deliberately NOT sent: it is exactly what this round-trip
// recomputes. See Services/DealReprice.cs and POST /api/opportunities/reprice-row.
public class RepriceRowRequest
{
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    // The local ask, as the board shows it (LocalArbitrageOpportunity.LocalAsk). Null/zero on a
    // free row, which IsFree marks so the cost basis stays a real zero rather than a missing price.
    public decimal? Price { get; set; }
    public bool IsFree { get; set; }
    public decimal? OriginalPrice { get; set; }
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    public string PostedAgo { get; set; } = "";
    public DateTime? PostedUtc { get; set; }
    public bool IsRetail { get; set; }
    public string Retailer { get; set; } = "";
    public bool FreeShipping { get; set; }
    public string CouponCode { get; set; } = "";
    // The category the scan already stamped on this row. Carried so the reprice classifies it the
    // same way (a source that already knew wins), keeping valuation and fees identical to the scan.
    public string CategoryId { get; set; } = "";
    // The title the comp lookup should run against — the fullest wording the board had for this
    // product (PricedAs, when it differs from the row's own title). Falls back to Title.
    public string? PricedAs { get; set; }
    // Set by the browser when it wants a specific query priced (the same string it sent to
    // /api/comps/live/start). Preferred over PricedAs/Title when present.
    public string? Query { get; set; }
}

/// <summary>One item handed to <see cref="Services.ClaudeService.EstimateResaleAsync"/>.</summary>
public sealed record AiEstimateItem(string ItemId, string? Title, decimal? AskingPrice, string? Condition);

/// <summary>
/// What the AI thinks something sells for when no sold comp could price it. Never evidence: it
/// cannot raise a row's grade, and any comp-backed figure outranks it.
/// </summary>
public sealed record AiResaleEstimate(string ItemId, decimal Low, decimal High, string? Basis)
{
    /// <summary>The single number a card shows — the middle of the range.</summary>
    public decimal Mid => Math.Round((Low + High) / 2m, 2);
}

/// <summary>The board asking for estimates for everything its comps could not price.</summary>
public sealed record AiEstimateRequest(List<AiEstimateItem>? Items);
