namespace ING_eBay_AutoLister.Models;

/// <summary>
/// What the deal judge decided about one candidate listing before Auto-Buy is allowed to spend on it.
/// </summary>
/// <remarks>
/// A price ceiling alone buys junk: a listing can be under the ceiling and still be a bad buy — sold
/// comps say it resells for less than the fees, or the title says "for parts". This is the layer that
/// turns "cheap enough" into "actually a deal": it prices the item against real eBay sold history,
/// subtracts what selling it will cost, and reads the title for the words that mean trouble.
/// </remarks>
public enum AutoBuyVerdict
{
    /// <summary>Comps support a real profit after fees and nothing raised a flag.</summary>
    Buy,
    /// <summary>Worth a human's eyes — thin comp history, or a red flag against a still-positive margin.</summary>
    Caution,
    /// <summary>Don't: it loses money against comps, or a hard red flag (for parts, not working).</summary>
    Skip,
}

/// <param name="Verdict">Buy, Caution, or Skip.</param>
/// <param name="ItemPrice">The listing's price, the money that would go out.</param>
/// <param name="CompCount">How many sold comps this rests on. Below the trust floor, it can't say Buy.</param>
/// <param name="EstimatedResale">The median sold price of comparable items — what it should fetch again.</param>
/// <param name="EstimatedFees">eBay + payment fees the resale would cost.</param>
/// <param name="EstimatedNetProfit">Resale minus fees minus the buy price. What the flip is worth.</param>
/// <param name="RoiPercent">Net profit over the buy price, as a percent.</param>
/// <param name="RedFlags">Trouble words found in the title/condition ("for parts", "as-is", …).</param>
/// <param name="Reason">One sentence a person can read to see why the judge said what it said.</param>
public sealed record AutoBuyJudgment(
    AutoBuyVerdict Verdict,
    decimal ItemPrice,
    int CompCount,
    decimal EstimatedResale,
    decimal EstimatedFees,
    decimal EstimatedNetProfit,
    decimal RoiPercent,
    IReadOnlyList<string> RedFlags,
    string Reason);
