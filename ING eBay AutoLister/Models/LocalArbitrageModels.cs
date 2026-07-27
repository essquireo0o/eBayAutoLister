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
    // The title the comp lookup was actually run against — often a sibling
    // listing's fuller title, so it needs to be visible rather than implied.
    public string PricedAs { get; set; } = "";
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
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
}

public class LocalArbitrageResult
{
    // Rolled up across every source that was searched (LocalSupplyMerger.RollUpStatus), so one
    // disconnected site never blanks a ranking another site filled: ok | not_connected |
    // session_expired | error | no_sources. The UI keeps its per-source connect prompts.
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }
    // The first searched source's own results URL, kept so the existing "open the search" link
    // still works; Sources below carries one per site.
    public string SearchUrl { get; set; } = "";
    public string? Error { get; set; }

    // What each site contributed, including the ones that couldn't answer and why.
    public List<LocalSupplySourceOutcome> Sources { get; set; } = [];

    public List<LocalArbitrageOpportunity> Items { get; set; } = [];
    public int Count => Items.Count;

    // ── What the run actually did, so the numbers can be judged ──────────────
    public int LocalListingsFound { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }        // distinct products, after grouping
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }
    public bool SoldCompsConfigured { get; set; }
    // Set when neither pricing source can answer — the table would otherwise be
    // a column of dashes with no explanation.
    public string? DataWarning { get; set; }

    public int GoldmineCount { get; set; }
    // Profitable rows whose money is expected back inside DaysToCashEstimator.FastCashDays — the
    // count a seller with limited cash actually shops from.
    public int FastCashCount { get; set; }
    public decimal TotalPotentialProfit { get; set; }
}
