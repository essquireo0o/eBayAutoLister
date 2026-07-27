namespace ING_eBay_AutoLister.Models;

// ── Aging-inventory rescue ───────────────────────────────────────────────────────────────────
//
// Inventory Health answers "what should this be priced at today". These types answer the question
// that comes after it: an item has been sitting for months, one markdown clearly wasn't enough, so
// what is the actual plan to turn it back into money — and by when.
//
// Two ways out of stuck stock, both here:
//   * a dated ladder of price drops the seller commits to in advance, and
//   * a bundle that sells the slow item alongside something that already moves.

/// <summary>One dated price drop in a rescue plan.</summary>
public sealed class RescueStep
{
    public int StepNumber { get; set; }
    public DateTime OnUtc { get; set; }
    /// <summary>0 for the step that is due today, so the UI can separate "do this now" from "later".</summary>
    public int DaysFromNow { get; set; }
    /// <summary>How old the listing will be when this step lands — the case for the step, restated.</summary>
    public int? ListingAgeAtStep { get; set; }

    public decimal Price { get; set; }
    /// <summary>Cut from today's asking price, not from the previous step — that is the number the seller feels.</summary>
    public decimal PercentOffListPrice { get; set; }
    /// <summary>Take-home if it sells at this step. Null with no cost basis recorded.</summary>
    public decimal? NetProfit { get; set; }
    /// <summary>True when this step is the floor — as low as this item goes without selling at a loss.</summary>
    public bool IsFloor { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>
/// The whole escape plan for one aging listing: where its price goes, on what dates, and what the
/// seller gets back at the end of it.
/// </summary>
public sealed class RescuePlan
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Category { get; set; } = "";

    public decimal ListPrice { get; set; }
    public int Quantity { get; set; }
    public int? DaysListed { get; set; }
    public int WatchCount { get; set; }

    /// <summary>Money the listing is holding, at the truest basis available — see InventoryHealthItem.</summary>
    public decimal CapitalTiedUp { get; set; }
    public string CapitalBasis { get; set; } = "list_price";

    public decimal? MarketPrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    /// <summary>Break-even raised by the seller's own floor policy. The ladder never goes under it.</summary>
    public decimal? FloorPrice { get; set; }
    public string FloorBasis { get; set; } = "break_even";

    /// <summary>Carried through from the health scan so one listing has one verdict app-wide.</summary>
    public string Verdict { get; set; } = "stale";
    /// <summary>critical | high | watch — how much longer this money can be left where it is.</summary>
    public string Urgency { get; set; } = "watch";

    public List<RescueStep> Steps { get; set; } = [];

    public decimal? FinalPrice { get; set; }
    /// <summary>The date the last step lands: when this listing has had every chance the plan gives it.</summary>
    public DateTime? ClearByUtc { get; set; }
    public int PlanDays { get; set; }

    /// <summary>Take-home if it sells at the last step — the money the plan is actually going after.</summary>
    public decimal? CashAtFinalStep { get; set; }
    /// <summary>
    /// Profit the plan gives up against today's asking price. Stated plainly rather than buried:
    /// this is the price of getting the money back, and it is only real if the item was ever going
    /// to sell at today's price — which is the thing months of silence has already argued against.
    /// </summary>
    public decimal? ProfitGivenUp { get; set; }

    public string Headline { get; set; } = "";
    public string Why { get; set; } = "";
    public List<string> Signals { get; set; } = [];

    public bool HasPlan => Steps.Count > 0;
    /// <summary>The drop that is due now — what a bulk apply would actually send to eBay.</summary>
    public RescueStep? FirstStep => Steps.Count > 0 ? Steps[0] : null;
}

/// <summary>
/// A slow mover paired with something that already sells, so the item nobody is searching for gets
/// carried to the buyer by the item they were already going to buy.
/// </summary>
public sealed class BundleSuggestion
{
    public string SlowListingId { get; set; } = "";
    public string SlowTitle { get; set; } = "";
    public decimal SlowPrice { get; set; }
    public int? SlowDaysListed { get; set; }
    public decimal SlowCapital { get; set; }
    /// <summary>What the slow item is worth inside the bundle — its clearance price, never under its floor.</summary>
    public decimal SlowContribution { get; set; }

    public string FastListingId { get; set; } = "";
    public string FastTitle { get; set; } = "";
    public decimal FastPrice { get; set; }
    /// <summary>Why this one counts as a fast mover — units sold, watchers, or measured velocity.</summary>
    public string FastEvidence { get; set; } = "";

    public string Category { get; set; } = "";
    public bool SameCategory { get; set; }

    /// <summary>Both asking prices added up — what the bundle is discounted against.</summary>
    public decimal ComponentValue { get; set; }
    public decimal BundlePrice { get; set; }
    public decimal DiscountPercent { get; set; }

    /// <summary>
    /// Per-order overhead paid once instead of twice: eBay's fixed final value fee, one label, one
    /// box, one trip. Already inside the net figures below; broken out because it is the part of a
    /// bundle that is money rather than hope.
    /// </summary>
    public decimal SavedByShippingTogether { get; set; }

    public bool HasCostBasis { get; set; }
    public decimal? NetIfBundleSells { get; set; }
    public decimal? NetIfFastSellsAlone { get; set; }
    /// <summary>
    /// The only honest way to score a bundle: what it makes over what actually happens today, which
    /// is the fast item selling on its own and the slow one continuing to sit. Null without cost
    /// basis on both halves — then <see cref="AddedRevenue"/> carries the case instead.
    /// </summary>
    public decimal? IncrementalNet { get; set; }
    /// <summary>Revenue the bundle adds over selling the fast item alone. True with or without cost basis.</summary>
    public decimal AddedRevenue { get; set; }

    /// <summary>The slow item's trapped capital — what this bundle is trying to set loose.</summary>
    public decimal CapitalFreed { get; set; }

    public string SuggestedTitle { get; set; } = "";
    public string Rationale { get; set; } = "";
    public List<string> Signals { get; set; } = [];
}

/// <summary>Portfolio totals for the rescue board: how much money is stuck, and what gets it out.</summary>
public sealed class RescueSummary
{
    public int StaleListings { get; set; }
    /// <summary>Every dollar sitting in listings old enough to qualify for rescue.</summary>
    public decimal TrappedCapital { get; set; }
    public int? OldestDaysListed { get; set; }
    public int? MedianDaysListed { get; set; }

    public int PlansReady { get; set; }
    /// <summary>Plans whose first drop is due today — the work sitting in front of the seller now.</summary>
    public int StepsDueNow { get; set; }
    public decimal CapitalUnderPlan { get; set; }
    /// <summary>Take-home if every plan runs to its last step and every item sells there.</summary>
    public decimal CashIfEveryPlanClears { get; set; }
    public decimal ProfitGivenUpIfEveryPlanClears { get; set; }
    /// <summary>Listings old enough to rescue that no plan could be built for, and therefore still stuck.</summary>
    public int NoPlanCount { get; set; }

    public int BundlesFound { get; set; }
    public decimal CapitalFreedByBundles { get; set; }
    public decimal IncrementalNetFromBundles { get; set; }
    public decimal AddedRevenueFromBundles { get; set; }
}

public sealed class RescueResult
{
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }

    public int ActiveListings { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public string? DataWarning { get; set; }

    /// <summary>The age at which a listing entered this scan, so the board can say what it means by "stale".</summary>
    public int StaleAfterDays { get; set; }
    public int StepIntervalDays { get; set; }

    public RescueSummary Summary { get; set; } = new();
    public List<RescuePlan> Plans { get; set; } = [];
    public List<BundleSuggestion> Bundles { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}
