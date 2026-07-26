namespace ING_eBay_AutoLister.Models;

// ── Liquidation Lot / Manifest Analyzer ──────────────────────────────────────
//
// A reseller buying a pallet, a wholesale lot or an estate/auction lot is making one decision:
// pay the ask, or walk. Everything in this file exists to turn a pasted manifest plus an ask
// price into that answer, with the arithmetic shown.

/// <summary>
/// One row of a manifest — either read deterministically out of a CSV/TSV/price-list paste by
/// <see cref="Services.ManifestParser"/>, or extracted from prose/a photo by Claude.
/// </summary>
public class ManifestLine
{
    public string Description { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public string Upc { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>
    /// Retail/MSRP per unit **as claimed by the manifest**. Never used as a resale value — a
    /// manifest's retail column is the seller's marketing, not the market. It is used as a
    /// sanity check on the comp match (see LotAnalyzer.RetailSanityCheck) and to show the seller
    /// how far the "$12,000 retail value!" headline is from what the items actually resell for.
    /// </summary>
    public decimal? UnitRetail { get; set; }
    public string Condition { get; set; } = "";
    /// <summary>Short eBay-search-friendly keyword string; falls back to Description.</summary>
    public string SearchQuery { get; set; } = "";
}

public class LotAnalysisRequest
{
    /// <summary>Pasted manifest — CSV, TSV, pipe table, a price list, or a prose lot description.</summary>
    public string? ManifestText { get; set; }
    /// <summary>A photographed or screenshotted manifest. Read by Claude vision.</summary>
    public string? ImageBase64 { get; set; }
    public string MimeType { get; set; } = "image/jpeg";

    // ── What the lot costs, all in ──────────────────────────────────────────
    public decimal AskPrice { get; set; }
    /// <summary>Auction buyer's premium, 0-40%. The line most often left out of a pallet's cost.</summary>
    public decimal BuyerPremiumPercent { get; set; }
    public decimal FreightCost { get; set; }
    public decimal SalesTaxPercent { get; set; }

    // ── Recovery assumptions ────────────────────────────────────────────────
    public string ConditionGrade { get; set; } = "customer_returns";
    public decimal? SellableRatePercent { get; set; }
    public decimal? ConditionPriceFactorPercent { get; set; }
    /// <summary>Ship + pack + handling per unit sold, used when the comps sold with free shipping.</summary>
    public decimal PerUnitHandlingCost { get; set; } = 8m;

    /// <summary>The ROI the seller wants; drives the "pay under $X" number, not the buy/skip call.</summary>
    public decimal TargetRoiPercent { get; set; } = 40m;
    public int MaxLines { get; set; } = 60;
    /// <summary>Allow the Claude extraction fallback. Off = deterministic parsing only.</summary>
    public bool UseAi { get; set; } = true;
}

/// <summary>
/// The published recovery assumptions for a lot grade. Both numbers are estimates, both are
/// shown in the UI, and both can be overridden — a buyer who has opened forty of these pallets
/// knows their own rate better than any default.
/// </summary>
public class LotGradeAssumption
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Share of units expected to be sellable at all (0-100).</summary>
    public decimal SellableRatePercent { get; set; }
    /// <summary>What a sellable unit of this grade fetches against the comp price (0-100).</summary>
    public decimal PriceFactorPercent { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>One manifest line, priced.</summary>
public class LotLineAnalysis
{
    public string Description { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    /// <summary>The title the comp lookup actually ran on, when it differs from this line's wording.</summary>
    public string PricedAs { get; set; } = "";
    public int Quantity { get; set; } = 1;
    public string Condition { get; set; } = "";

    public decimal? UnitRetail { get; set; }
    public decimal RetailTotal { get; set; }

    /// <summary>Straight from the sold comps, before any condition adjustment.</summary>
    public decimal? CompUnitPrice { get; set; }
    /// <summary>What one unit of THIS lot's grade is expected to fetch.</summary>
    public decimal? UnitResale { get; set; }
    /// <summary>
    /// Expected sellable units — deliberately fractional. A 60%-recovery grade turns each
    /// one-unit line into 0.6 of a sale; rounding that to zero per line would quietly delete
    /// most of a mixed manifest's value, and rounding it up to one would invent stock that
    /// isn't there. Only the lot total is rounded, and only for display.
    /// </summary>
    public decimal SellableUnits { get; set; }
    public decimal GrossResale { get; set; }

    public decimal EstimatedFees { get; set; }
    public decimal EstimatedShipCost { get; set; }
    /// <summary>Gross resale minus every selling cost, before any of the lot price is charged to it.</summary>
    public decimal NetRecovery { get; set; }
    /// <summary>This line's pro-rata share of the all-in lot cost, allocated by resale value.</summary>
    public decimal AllocatedCost { get; set; }
    public decimal NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }

    /// <summary>Share of the whole lot's net recovery this one line carries (0-100).</summary>
    public decimal ValueSharePercent { get; set; }
    public decimal CumulativeSharePercent { get; set; }

    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "";
    public int? EstimatedDaysToSell { get; set; }
    public decimal EstimatedMonthlySales { get; set; }

    /// <summary>priced | thin | no_data | excluded</summary>
    public string Status { get; set; } = "no_data";
    public string StatusNote { get; set; } = "";
    /// <summary>Set when this line was kept out of the totals, and why.</summary>
    public string? ExclusionReason { get; set; }
    public bool CarriesTheValue { get; set; }
}

public class LotTotals
{
    // Cost side
    public decimal AskPrice { get; set; }
    public decimal BuyerPremium { get; set; }
    public decimal SalesTax { get; set; }
    public decimal FreightCost { get; set; }
    public decimal TotalCost { get; set; }

    // Unit counts
    public int ManifestUnits { get; set; }
    public decimal SellableUnits { get; set; }

    // Value side
    public decimal ManifestRetailTotal { get; set; }
    public decimal GrossResale { get; set; }
    public decimal EstimatedFees { get; set; }
    public decimal EstimatedShipCost { get; set; }
    public decimal NetRecovery { get; set; }
    public decimal NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal CostPerSellableUnit { get; set; }

    /// <summary>Resale as a percentage of the manifest's own claimed retail — the "$12,000 retail" test.</summary>
    public decimal? ResalePercentOfRetail { get; set; }

    // Time
    public int? DaysToSellSlowestLine { get; set; }
    public int? MedianDaysToSell { get; set; }
}

public class LotConcentration
{
    /// <summary>Number of lines that together make up 80% of the lot's net recovery.</summary>
    public int LinesForEightyPercent { get; set; }
    public decimal TopLineSharePercent { get; set; }
    public decimal TopThreeSharePercent { get; set; }
    public string? Warning { get; set; }
}

public class LotAnalysisResult
{
    /// <summary>ok | no_lines | no_pricing</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }

    /// <summary>csv | delimited | list | ai_text | ai_image — how the manifest was actually read.</summary>
    public string SourceFormat { get; set; } = "";
    public string SourceNote { get; set; } = "";

    public int LinesExtracted { get; set; }
    public int LinesAnalyzed { get; set; }
    public int LinesPriced { get; set; }
    public int LinesExcluded { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }

    /// <summary>
    /// Share of the manifest's own claimed value (or unit count, when it states no retail) that
    /// could be priced against real sold comps. A verdict on 30% coverage is a coin flip, and is
    /// reported as one.
    /// </summary>
    public decimal CoveragePercent { get; set; }

    /// <summary>buy | buy_below | skip | dead | thin | no_data</summary>
    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";

    /// <summary>Highest all-in ask that still clears the requested ROI. Exact arithmetic, not a guess.</summary>
    public decimal? MaxBid { get; set; }
    /// <summary>Highest ask at which the lot merely breaks even.</summary>
    public decimal? BreakEvenAsk { get; set; }
    public decimal TargetRoiPercent { get; set; }

    public LotTotals Totals { get; set; } = new();
    public LotConcentration Concentration { get; set; } = new();
    public LotGradeAssumption Assumptions { get; set; } = new();
    public List<LotLineAnalysis> Items { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? DataWarning { get; set; }
}
