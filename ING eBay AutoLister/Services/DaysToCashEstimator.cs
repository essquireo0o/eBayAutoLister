using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns the velocity numbers the comps lookup already produced into the one figure that decides
/// whether an opportunity is worth the money: how many days the cash stays tied up, and what the
/// profit is worth per day of that wait.
/// </summary>
/// <remarks>
/// Pure and static on purpose — every board that ranks opportunities (local arbitrage, Roll the
/// Dice, the trend radar) calls it on numbers it already has, with no lookup, no config and no
/// second opinion about what "fast" means. One definition, three boards.
///
/// Nothing here invents velocity. When the sold history can't say how fast a product moves, the
/// estimate is <c>unknown</c> and the UI says so, rather than a default that quietly ranks an
/// unmeasured product against measured ones.
/// </remarks>
public static class DaysToCashEstimator
{
    // ── The pipeline: sale → spendable money ─────────────────────────────────────────────────
    // Deliberately conservative and fixed. These are the days between "it sold" and "the money is
    // in the bank", which no velocity figure covers: eBay pays out after delivery is confirmed, so
    // a same-day sale is still not same-day cash.
    public const int HandlingDays = 2;   // pack it and get it to the carrier
    public const int TransitDays = 4;    // typical ground delivery
    public const int PayoutDays = 2;     // eBay initiates payout after delivery, then it settles
    public const int PipelineDays = HandlingDays + TransitDays + PayoutDays;

    // Days-to-cash bands. Anchored to how a seller actually thinks about their float: inside three
    // weeks the money is back this month; past three months it is capital they have effectively
    // lent to a shelf.
    public const int FastCashDays = 21;
    public const int SteadyCashDays = 45;
    public const int SlowCashDays = 90;

    // A dollar-a-day figure with four decimal places is noise, and an "annualized" percentage in the
    // tens of thousands reads as a bug. Both are capped rather than dropped — the row is still fast,
    // it just stops shouting a number nobody can act on.
    // Public because a basket of deals annualizes its blended return the same way one deal does —
    // see SourcingBudgetOptimizer. Two caps would mean two answers to "is this number real".
    public const decimal MaxAnnualizedRoiPercent = 9999m;

    /// <summary>
    /// Listing → sale. Prefers the sell-through estimate (already nudged for price position and
    /// competition by <see cref="SellThroughCalculator"/>); falls back to the monthly sales rate
    /// when only that survived the trip. Null means unmeasured, not fast and not slow.
    /// </summary>
    public static int? DaysToSell(int? estimatedDaysToSell, decimal estimatedMonthlySales)
    {
        if (estimatedDaysToSell is int days && days > 0) return days;
        if (estimatedMonthlySales > 0) return Math.Max(1, (int)Math.Round(30m / estimatedMonthlySales));
        return null;
    }

    /// <summary>
    /// The full estimate for one opportunity. <paramref name="netProfit"/> and
    /// <paramref name="roiPercent"/> are the money for the specific buy being judged — pass the
    /// live buy's numbers where supply exists, and the target-price numbers where it doesn't.
    /// </summary>
    public static DaysToCashEstimate Estimate(
        int? estimatedDaysToSell, decimal estimatedMonthlySales,
        decimal? netProfit = null, decimal? roiPercent = null)
    {
        var estimate = new DaysToCashEstimate { PipelineDays = PipelineDays };

        var toSell = DaysToSell(estimatedDaysToSell, estimatedMonthlySales);
        if (toSell is not int sellDays)
        {
            estimate.SpeedTier = "unknown";
            estimate.SpeedLabel = "Speed unknown";
            estimate.Note = "No dated sold history behind this one, so there's no honest estimate of " +
                            "how long the money stays tied up.";
            return estimate;
        }

        var days = sellDays + PipelineDays;
        estimate.DaysToSell = sellDays;
        estimate.DaysToCash = days;
        estimate.CapitalTurnsPerYear = Math.Round(365m / days, 1);

        if (netProfit is decimal profit)
        {
            estimate.ProfitPerDay = Math.Round(profit / days, 2);
            // Only a buy that actually makes money has a rate of return to annualize.
            if (profit > 0 && roiPercent is > 0)
                estimate.AnnualizedRoiPercent =
                    Math.Min(MaxAnnualizedRoiPercent, Math.Round(roiPercent.Value * 365m / days, 0));
        }

        (estimate.SpeedTier, estimate.SpeedLabel) = TierFor(days);
        estimate.Note = NoteFor(days, estimate.SpeedTier, estimate.CapitalTurnsPerYear!.Value, estimate.ProfitPerDay);
        return estimate;
    }

    /// <summary>Sort key for "fastest first" — unknown speed sorts last, never as instant.</summary>
    public static int SortableDaysToCash(int? daysToCash) => daysToCash ?? int.MaxValue;

    /// <summary>
    /// The speed band for a wait that is already known in days. Public so a screen holding a
    /// days-to-cash figure it did not compute (the budget optimizer holds one per candidate) bands
    /// it by exactly the same thresholds the row was banded by on the board it came from.
    /// </summary>
    public static (string Tier, string Label) TierFor(int days) => days switch
    {
        <= FastCashDays => ("fast", "⚡ Fast cash"),
        <= SteadyCashDays => ("steady", "Steady turn"),
        <= SlowCashDays => ("slow", "Slow money"),
        _ => ("dead_money", "🐢 Dead money"),
    };

    private static string NoteFor(int days, string tier, decimal turnsPerYear, decimal? profitPerDay)
    {
        var perDay = profitPerDay is > 0 ? $" — {profitPerDay:C} a day while it's tied up" : "";
        var turns = $"about {turnsPerYear:0.#} turn{(turnsPerYear == 1m ? "" : "s")} of this money a year";

        return tier switch
        {
            "fast" => $"Cash back in ~{days} days{perDay}: {turns}, so it funds the next buy fast.",
            "steady" => $"~{days} days to cash{perDay}: {turns}.",
            "slow" => $"~{days} days before this money comes back{perDay} — only {turns}.",
            _ => $"~{days} days to cash — {Math.Round(days / 30m, 1):0.#} months of capital parked here. " +
                 $"A smaller flip that clears in weeks usually beats it.",
        };
    }
}
