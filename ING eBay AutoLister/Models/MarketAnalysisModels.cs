namespace ING_eBay_AutoLister.Models;

// ── Product normalization (see Services/ProductNormalizer.cs) ───────────────────────────────

// Structured, comparison-ready view of a product title — either a supplier item being priced, or
// one SoldListings candidate's own Title+RawJson.subtitle. Extends the simpler ProductIdentity
// (Services/ProductIdentityExtractor.cs) with the extra signals needed for real matching: negative
// keywords (parts/broken/empty-box/compatible/...), accessory detection, and quantity/lot parsing.
public class NormalizedProduct
{
    public string? Brand { get; set; }
    public string? Model { get; set; }
    public string? PartNumber { get; set; }
    public string? Category { get; set; }
    public string? ProductType { get; set; }
    public string? Capacity { get; set; }
    public string? Size { get; set; }
    public string? Generation { get; set; }
    public string? Processor { get; set; }
    public string? Ram { get; set; }
    public string? Storage { get; set; }
    public string? Color { get; set; }
    public string? Voltage { get; set; }
    public string? Revision { get; set; }
    public string? Condition { get; set; }
    public List<string> Accessories { get; set; } = [];
    public int Quantity { get; set; } = 1;
    public List<string> ImportantKeywords { get; set; } = [];
    public List<string> NegativeKeywords { get; set; } = [];
    public bool IsAccessoryListing { get; set; } // the item itself appears to BE an accessory, not the main product
    public string RawText { get; set; } = "";
}

// ── Comparable matching (see Services/ComparableMatcher.cs) ─────────────────────────────────

// Priority order for the local-DB search waterfall and for weighting a comparable's influence on
// price — strongest identifier first, exactly per the matching-priority spec.
public enum MatchTier
{
    ExactIdentifier = 5,
    ExactModel = 4,
    BrandModelSpec = 3,
    TitleSimilarity = 2,
    BroadKeyword = 1,
}

// One SoldListings candidate scored against a NormalizedProduct target.
public class ComparableMatch
{
    public MarketplaceComparableResult Comparable { get; set; } = new();
    public int MatchConfidence { get; set; } // 0-100, see ComparableMatcher's point table
    public MatchTier Tier { get; set; }
    public List<string> PenaltyReasons { get; set; } = [];
    public bool Excluded { get; set; }
    public string? ExclusionReason { get; set; }
}

// ── Price estimation (see Services/MarketPriceEstimator.cs) ─────────────────────────────────

public class PriceEstimate
{
    public decimal? MedianPrice { get; set; }
    public decimal? WeightedMedianPrice { get; set; }
    public decimal? TrimmedMeanPrice { get; set; }
    public decimal? Percentile25 { get; set; }
    public decimal? Percentile75 { get; set; }
    public decimal? MinimumRealisticPrice { get; set; }
    public decimal? MaximumRealisticPrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    public decimal? ExpectedSalePrice { get; set; }
    public decimal? RecommendedListingPrice { get; set; }
    public decimal? HighPriceTarget { get; set; }
    public decimal LocalWeight { get; set; }      // 0-1, the local-vs-Terapeak blend weight actually used
    public decimal TerapeakWeight { get; set; }   // 0-1
    public int TerapeakComparableCount { get; set; }
    public bool MarketDataDisagreement { get; set; } // local vs Terapeak medians differ by >20%
    public string? DisagreementMessage { get; set; }

    // ── How much evidence these figures actually rest on ─────────────────────────────────────
    // How many local sold comps produced the numbers above, AFTER per-unit normalization, the
    // identity guard, outlier removal and the strong-match filter. Nearly always smaller than the
    // number of comps the lookup returned, and it is this count — not that one — that says whether
    // a price is a market price or one loose sale. Anything gating a verdict on evidence must read
    // this: "12 comps found, 1 of them priced it" is how a jackpot ROI gets published.
    public int PricedOnCompCount { get; set; }
    // False when the target carried a real model/part identifier and NOT ONE comp title contained
    // it — the estimate is then priced off items that merely share a brand or a keyword, which is
    // exactly how a $60 part gets valued at $554. See MarketPriceEstimator.GuardIdentity.
    public bool IdentityVerified { get; set; } = true;
    // Comps whose title carried the target's model/part token. Equals PricedOnCompCount's input set
    // when the guard had an identifier to check; equals the whole set when it had none.
    public int IdentityMatchedCompCount { get; set; }
}

// ── Sell-through (see Services/SellThroughCalculator.cs) ────────────────────────────────────

public class SellThroughAnalysis
{
    public int SoldComparableCount { get; set; }
    public int ActiveComparableCount { get; set; }
    public decimal? SellThroughRate { get; set; }  // SoldCount / ActiveCount, null when unbounded
    public bool RateIsUnbounded { get; set; }        // sold comps exist but zero active listings found
    public int SellThroughScore { get; set; }        // 0-100 capped/interpolated score
    public string Interpretation { get; set; } = "Unknown"; // Excellent|Very Strong|Good|Moderate|Weak|Poor|Unknown
    public decimal EstimatedMonthlySales { get; set; }
    public int? EstimatedDaysToSell { get; set; }

    // Carried through from LiquidityScoringService.Assess (via MarketplacePricingSummary, already
    // computed once by MarketplaceRepository.FindComparablesAsync) — a distinct velocity+trend+
    // competition-weighted score from SellThroughScore above, kept for the pre-existing
    // "Fast Mover"/"Moderate"/"Slow Mover"/"Stale/Illiquid" gate and UI badge.
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "Stale/Illiquid";
}

// ── Active competition (see Program.cs orchestration) ───────────────────────────────────────

public class CompetitionAnalysis
{
    public int CloseActiveComparableCount { get; set; }
    public decimal? MedianActivePrice { get; set; }
    public decimal? LowestRealisticActivePrice { get; set; }
    public double? AverageSellerFeedbackPercent { get; set; }
    public string CompetitionLevel { get; set; } = "Unknown"; // Low|Moderate|High|Unknown
}

// ── Profit (see Services/ProfitCalculator.cs) ────────────────────────────────────────────────

public class ProfitBreakdown
{
    public decimal SupplierUnitCost { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal ExpectedSalePrice { get; set; }
    public decimal QuickSalePrice { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    public decimal EbayFees { get; set; }
    public decimal PromotedListingFees { get; set; }
    // Reported separately from OtherCosts, which it used to be folded into — see ProfitCalculator.
    public decimal PaymentProcessingFees { get; set; }
    public decimal ActualShippingCost { get; set; }
    public decimal PackagingCost { get; set; }
    public decimal LaborCost { get; set; }
    public decimal ReturnReserve { get; set; }
    public decimal TestingReserve { get; set; }
    public decimal OtherCosts { get; set; }
    public decimal NetProfitPerUnit { get; set; }
    public decimal TotalPotentialProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal BreakEvenSalePrice { get; set; }

    /// <summary>
    /// Everything taken out of the sale that is neither cost of goods nor fulfilment — the total
    /// the seller never sees on the price tag. One property rather than a sum re-typed at each
    /// call site, because the call sites did not agree: the local-arbitrage row was leaving the
    /// return and testing reserves out of the "fees" it displayed.
    /// </summary>
    public decimal MarketplaceFeeTotal => Math.Round(
        EbayFees + PromotedListingFees + PaymentProcessingFees + ReturnReserve + TestingReserve + OtherCosts, 2);

    /// <summary>What it costs to get the item to the buyer, once it sells.</summary>
    public decimal FulfilmentCostTotal => Math.Round(ActualShippingCost + PackagingCost + LaborCost, 2);
}

// ── Scoring (see Services/OpportunityScoringService.cs and ConfidenceScoringService.cs) ─────

public class ScoreBreakdown
{
    public int Score { get; set; } // 0-100
    public Dictionary<string, double> ComponentScores { get; set; } = [];
    public List<string> Reasons { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public bool HardRejected { get; set; }
    public string? RejectionReason { get; set; }
}

public class ConfidenceBreakdown
{
    public int Score { get; set; } // 0-100
    public string Level { get; set; } = "Insufficient Evidence"; // High|Good|Limited|Insufficient Evidence
    public List<string> Reasons { get; set; } = [];
}

public class PriceStability
{
    public int StabilityScore { get; set; } // 0-100 — narrow IQR relative to price = high stability
    public string Trend { get; set; } = "Unknown"; // Rising|Stable|Falling|Unknown
}

// ── Composite result — one per analyzed product ──────────────────────────────────────────────

public class SourceBreakdown
{
    public int LocalComparableCount { get; set; }
    public int TerapeakComparableCount { get; set; }
    public decimal LocalWeightPercent { get; set; }
    public decimal TerapeakWeightPercent { get; set; }
    // The subset of LocalComparableCount that actually priced it, and whether the model/part token
    // survived into that subset. LocalComparableCount answers "how many did the lookup return?",
    // which is a much friendlier number and the wrong one to trust a percentage to.
    // See PriceEstimate.PricedOnCompCount.
    public int PricedOnCompCount { get; set; }
    public bool IdentityVerified { get; set; } = true;
}

// Everything computed for one product by the matching/pricing/scoring pipeline
// (ProductNormalizer -> ComparableMatcher -> MarketPriceEstimator -> SellThroughCalculator ->
// competition analysis -> ProfitCalculator -> OpportunityScoringService/ConfidenceScoringService),
// before being flattened onto OpportunityListItem or DropshipAnalysisItem for the API response.
public class MarketAnalysisResult
{
    public NormalizedProduct Identity { get; set; } = new();
    public PriceEstimate PriceEstimate { get; set; } = new();
    public SellThroughAnalysis SellThrough { get; set; } = new();
    public CompetitionAnalysis Competition { get; set; } = new();
    public ProfitBreakdown? Profit { get; set; }
    public ScoreBreakdown Score { get; set; } = new();
    public ConfidenceBreakdown Confidence { get; set; } = new();
    public PriceStability Stability { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
    public List<MarketplaceComparableResult> TopSoldComparables { get; set; } = [];
    public SourceBreakdown Sources { get; set; } = new();
}
