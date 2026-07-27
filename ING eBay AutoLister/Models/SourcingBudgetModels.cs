namespace ING_eBay_AutoLister.Models;

// ── The Sourcing Budget Optimizer ─────────────────────────────────────────────────────────────
//
// Every sourcing screen in this app ranks deals. None of them spends money, because ranking and
// spending are different problems: a ranked list answers "which is the best single deal", and the
// seller standing at an ATM with $500 has to answer "which SET of deals". Those two answers are
// routinely different, and the gap between them is real money.
//
// The reason is arithmetic, not judgement. Buying down a ranked list takes the biggest profit
// first, and the biggest profit is usually also the biggest price — so the top row eats the budget
// and everything behind it is unaffordable. Three smaller flips that each make less can, together,
// make more. That is a knapsack problem, and it has an exact answer.
//
// The rules this file is governed by:
//
//   1. Nothing here invents a profit. Every candidate arrives already priced by the same stack
//      that priced it on the board it came from (LocalArbitrageAnalyzer / the frozen forecast in
//      the deal pipeline), and this only decides which of them to buy.
//   2. The basket is never allowed to cost more than the money the seller said they have.
//   3. The comparison is always against what the seller would otherwise have done — buy down the
//      list until the cash runs out — so the value of the optimizer is stated in dollars rather
//      than asserted.
//   4. A deal with no measured speed is never counted as fast, and a basket with one in it never
//      claims a date by which all the money is back.

/// <summary>
/// One buyable deal offered to the optimizer. Deliberately a flat, small type rather than a
/// <see cref="LocalArbitrageOpportunity"/>: the same basket can mix a live local scan, a tracked
/// deal frozen weeks ago and (later) anything else that can be priced, and the only things the
/// allocation actually needs are what it costs, what it nets and how long the money is gone.
/// </summary>
public class BudgetCandidate
{
    /// <summary>The source's own post id where there is one — used to dedupe a tracked deal against the live scan row for the same post.</summary>
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }

    /// <summary>What one unit costs to buy — the asking price, not the price you hope to talk them down to.</summary>
    public decimal BuyPrice { get; set; }

    /// <summary>
    /// Units bought together. A local post is one; a tracked lot can be several. Treated as
    /// all-or-nothing, because that is how the thing is actually sold — you buy the lot or you
    /// walk away from it.
    /// </summary>
    public int Quantity { get; set; } = 1;

    /// <summary>Net profit PER UNIT after fees and shipping, at <see cref="BuyPrice"/>. Computed elsewhere; never re-derived here.</summary>
    public decimal NetProfit { get; set; }

    /// <summary>The highest price that still breaks even, for the negotiation line on the pick.</summary>
    public decimal? MaxBuyPrice { get; set; }

    /// <summary>The drafted opening offer (NegotiationAdvisor). What the basket would cost if every seller said yes — a ceiling, never a plan.</summary>
    public decimal? TargetOffer { get; set; }

    /// <summary>Buy → sold → paid out, in days. Null means unmeasured, which is not the same as fast.</summary>
    public int? DaysToCash { get; set; }

    /// <summary>How much sold history is behind the profit figure, and how much the app believed it.</summary>
    public int CompCount { get; set; }
    public int ConfidenceScore { get; set; }

    /// <summary>goldmine | solid | thin | pass | no_data — the board's own verdict, carried so this screen can't invent a friendlier one.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>scan | tracked — where the number came from, because a frozen forecast is a weaker claim than a scan run five minutes ago.</summary>
    public string Origin { get; set; } = BudgetOrigins.Scan;

    /// <summary>Set for tracked deals: when the forecast was taken.</summary>
    public DateTimeOffset? ForecastUtc { get; set; }

    /// <summary>What the whole line costs, and what the whole line makes.</summary>
    public decimal TotalCost => Math.Round(BuyPrice * Math.Max(1, Quantity), 2);
    public decimal TotalProfit => Math.Round(NetProfit * Math.Max(1, Quantity), 2);
}

public static class BudgetOrigins
{
    public const string Scan = "scan";
    public const string Tracked = "tracked";

    public static string Label(string origin) => origin == Tracked ? "Tracked deal" : "This scan";
}

/// <summary>One line of the basket: what to buy, what it costs, and what it leaves.</summary>
public class BudgetPick
{
    public int Rank { get; set; }
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    public string Origin { get; set; } = BudgetOrigins.Scan;
    public string OriginLabel { get; set; } = "";

    public decimal BuyPrice { get; set; }
    public int Quantity { get; set; } = 1;
    /// <summary>Cash out of pocket for this line.</summary>
    public decimal Spend { get; set; }
    public decimal NetProfit { get; set; }
    public decimal TotalNetProfit { get; set; }
    /// <summary>Re-derived here from spend and profit rather than carried in — one definition of ROI on this screen.</summary>
    public decimal? RoiPercent { get; set; }

    public int? DaysToCash { get; set; }
    public decimal? ProfitPerDay { get; set; }
    public string SpeedTier { get; set; } = "unknown";
    public string SpeedLabel { get; set; } = "Speed unknown";

    /// <summary>Running totals down the basket, so "where does the money go" is readable without adding up columns.</summary>
    public decimal CumulativeSpend { get; set; }
    public decimal CumulativeProfit { get; set; }

    public decimal? MaxBuyPrice { get; set; }
    public decimal? TargetOffer { get; set; }
    /// <summary>Ask minus the drafted opening offer, across the line. Money that costs nothing to earn — if they say yes.</summary>
    public decimal? NegotiationUpside { get; set; }

    public int CompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string Verdict { get; set; } = "";
    /// <summary>Why this one earned its place in the basket, in a sentence.</summary>
    public string Why { get; set; } = "";
}

/// <summary>A deal that isn't in the basket, and the honest reason it isn't.</summary>
public class BudgetSkip
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Url { get; set; } = "";
    public decimal BuyPrice { get; set; }
    public decimal? NetProfit { get; set; }
    public int? DaysToCash { get; set; }
    /// <summary>loses_money | thin_evidence | too_slow | over_budget | duplicate | crowded_out | not_enough_left | capped</summary>
    public string ReasonCode { get; set; } = "";
    public string Reason { get; set; } = "";
}

/// <summary>One complete answer to "how should I spend it", under one definition of best.</summary>
public class BudgetPlan
{
    /// <summary>profit | fast_cash | per_day</summary>
    public string Objective { get; set; } = "";
    public string ObjectiveLabel { get; set; } = "";
    /// <summary>What this objective optimises for, said plainly — the seller is choosing between them.</summary>
    public string ObjectiveNote { get; set; } = "";
    public string Headline { get; set; } = "";

    public List<BudgetPick> Picks { get; set; } = [];

    public decimal Budget { get; set; }
    /// <summary>Budget minus whatever the seller held back — the money this plan is allowed to touch.</summary>
    public decimal Spendable { get; set; }
    public decimal CapitalDeployed { get; set; }
    public decimal Leftover { get; set; }

    public decimal TotalNetProfit { get; set; }
    public decimal? BlendedRoiPercent { get; set; }
    /// <summary>What the whole basket earns per day of the wait — the rate the capital works at.</summary>
    public decimal? ProfitPerDay { get; set; }

    public int? FastestDaysToCash { get; set; }
    public int? SlowestDaysToCash { get; set; }
    /// <summary>Days to cash weighted by the CAPITAL in each line, not by line count — a $400 slow flip ties up more money than a $20 fast one.</summary>
    public decimal? WeightedDaysToCash { get; set; }
    public decimal? CapitalTurnsPerYear { get; set; }
    public decimal? AnnualizedRoiPercent { get; set; }

    /// <summary>Set only when EVERY pick has a measured speed. One unknown and there is no date to promise.</summary>
    public string? AllCashBackBy { get; set; }
    public string? FirstCashBackBy { get; set; }
    public int UnknownSpeedCount { get; set; }

    public decimal NegotiationUpside { get; set; }
    public int NegotiableCount { get; set; }

    public string Note { get; set; } = "";
}

/// <summary>
/// What the same money does if it is spent the obvious way — straight down the ranked board until
/// it runs out. This is the number the optimizer has to beat to be worth anything, so it is
/// computed and shown whether it flatters the feature or not.
/// </summary>
public class BudgetComparison
{
    public string Method { get; set; } = "Buying straight down the list until the money runs out";
    public int Picks { get; set; }
    public decimal CapitalDeployed { get; set; }
    public decimal TotalNetProfit { get; set; }
    public decimal Leftover { get; set; }
    /// <summary>Optimised profit minus the above. Zero is a real and reportable answer.</summary>
    public decimal ExtraProfit { get; set; }
    public decimal? ExtraProfitPercent { get; set; }
    public string Note { get; set; } = "";
}

/// <summary>What the next slice of budget would actually buy — the marginal value of more cash.</summary>
public class BudgetStretch
{
    public decimal ExtraBudget { get; set; }
    public decimal ExtraProfit { get; set; }
    public string Note { get; set; } = "";
}

public class BudgetPlanRequest
{
    public decimal Budget { get; set; }
    /// <summary>Cash to hold back — gas, shipping supplies, the float that isn't for buying stock.</summary>
    public decimal Reserve { get; set; }
    /// <summary>Only consider deals whose money is back inside this many days. 0/null means no limit.</summary>
    public int? MaxDaysToCash { get; set; }
    /// <summary>How much sold history a deal needs before the seller's cash is allowed near it.</summary>
    public int? MinCompCount { get; set; }
    /// <summary>Let thin-evidence deals into the basket anyway. Off by default: it is the seller's money.</summary>
    public bool IncludeThin { get; set; }
    /// <summary>Fold in the deals already on the pipeline board at Sourced. On by default.</summary>
    public bool IncludeTrackedDeals { get; set; } = true;
    /// <summary>profit | fast_cash | per_day</summary>
    public string? Objective { get; set; }
    public List<BudgetCandidate> Candidates { get; set; } = [];
}

public class BudgetPlanResult
{
    /// <summary>ok | no_budget | no_candidates | nothing_affordable</summary>
    public string Status { get; set; } = "ok";
    public string Message { get; set; } = "";

    public decimal Budget { get; set; }
    public decimal Reserve { get; set; }
    public decimal Spendable { get; set; }

    public int CandidatesConsidered { get; set; }
    public int EligibleCount { get; set; }
    public int TrackedDealsIncluded { get; set; }
    public int DuplicatesMerged { get; set; }

    /// <summary>The plan for the objective that was asked for.</summary>
    public BudgetPlan Plan { get; set; } = new();
    /// <summary>The same money under the other two definitions of best, so the trade-off is visible rather than argued.</summary>
    public List<BudgetPlan> Alternatives { get; set; } = [];

    public BudgetComparison Comparison { get; set; } = new();
    public BudgetStretch? Stretch { get; set; }

    /// <summary>Everything that didn't make it, with the reason. A sourcing screen that silently drops deals is how a real one gets missed.</summary>
    public List<BudgetSkip> LeftOut { get; set; } = [];

    /// <summary>Anything the seller needs to know about how this answer was reached.</summary>
    public List<string> Notes { get; set; } = [];
}
