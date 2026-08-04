namespace ING_eBay_AutoLister.Models;

// The Restock List — "what should I go and buy again?", answered from the seller's own till roll.
//
// Every other sourcing board in this app starts at the market: scan a feed, price what's on it,
// keep what clears a margin. They all answer "is THIS worth buying". None of them answers the
// question a seller actually asks on a Saturday morning with $600 and a van — "what do I go
// looking for", which is a question about the seller, not about the market.
//
// The evidence for that question is already in the app and has never been read: completed sales,
// with the fee eBay actually charged and the cost the seller actually paid. A product they have
// sold four times is the only product on eBay whose demand, price, shipping cost, condition and
// buyer they have first-hand proof of. This turns that history into a shopping list, ordered by
// the money each line makes per month rather than by the size of one lucky win.
//
// The rules are the earnings tracker's rules, because it is the same money:
//   * A sale with no recorded cost proves nothing about profit and is never counted as any. It is
//     reported separately, with what it would be worth to go and enter the cost.
//   * One sale is not a pattern. A line with a single sale is shown, labelled as exactly that, and
//     never ranked against lines with a repeat history behind them.
//   * A listing that is live and not selling is evidence. An item the seller simply hasn't got any
//     of is not — an empty shelf can't sell, and punishing a line for being sold out is how this
//     screen would talk a seller out of the restock it exists to recommend.

/// <summary>One completed sale, with the purchase date the cost basis carries when it has one.</summary>
/// <remarks>
/// <see cref="FlipProfit"/> is the earnings tracker's own computed row — the same arithmetic Money
/// Made and the Tax Pack print, reused rather than re-derived, so this screen can never disagree
/// with them about what a sale made. <see cref="AcquiredUtc"/> is the one thing it doesn't carry,
/// and it is what turns a margin into a speed: $40 on a $10 buy is a different business depending
/// entirely on whether the $10 was tied up for nine days or nine months.
/// </remarks>
public sealed class RestockSale
{
    public FlipProfit Sale { get; set; } = new();

    /// <summary>When the goods were bought, if the seller recorded it. Null is common and expected.</summary>
    public DateTimeOffset? AcquiredUtc { get; set; }
}

/// <summary>One product the seller has sold, with everything needed to decide whether to buy another.</summary>
public sealed class RestockLine
{
    /// <summary>The clustering key — <see cref="Services.JackpotHunter.ProductSignature"/>'s, so
    /// six sales of one item are one line rather than six.</summary>
    public string Key { get; set; } = "";

    /// <summary>The leanest of the titles this product sold under — the one that makes a search term.</summary>
    public string Title { get; set; } = "";

    /// <summary>What to type into a marketplace search to find another one.</summary>
    public string SearchQuery { get; set; } = "";

    // ── What happened ────────────────────────────────────────────────────────────────────────
    public int Orders { get; set; }
    public int UnitsSold { get; set; }
    public DateTimeOffset FirstSoldUtc { get; set; }
    public DateTimeOffset LastSoldUtc { get; set; }
    public int DaysSinceLastSale { get; set; }

    public decimal Revenue { get; set; }
    public decimal AverageSalePrice { get; set; }

    // ── What it made ─────────────────────────────────────────────────────────────────────────
    // Only ever from units whose cost is known. Null means "not knowable yet", never zero.
    public int UnitsWithKnownCost { get; set; }
    public int UnitsAwaitingCost { get; set; }
    public decimal ProceedsAwaitingCost { get; set; }

    public decimal? NetProfit { get; set; }
    public decimal? AverageProfitPerUnit { get; set; }
    public decimal? AverageUnitCost { get; set; }
    public decimal? RoiPercent { get; set; }

    // ── How fast it comes back ───────────────────────────────────────────────────────────────
    /// <summary>Units sold per month across this line's own selling window.</summary>
    public decimal SalesPerMonth { get; set; }

    /// <summary>The ranking figure: average profit per unit × units per month.</summary>
    public decimal? ProfitPerMonth { get; set; }

    /// <summary>Days from paying for it to selling it, median, when purchase dates were recorded.</summary>
    public int? MedianDaysHeld { get; set; }
    public int UnitsWithHoldingTime { get; set; }

    /// <summary>
    /// Return on the cash, restated as a yearly rate using how long the cash was actually tied up.
    /// Null unless purchase dates were recorded — it is not guessable.
    /// </summary>
    public decimal? AnnualReturnOnCashPercent { get; set; }

    // ── What went wrong ──────────────────────────────────────────────────────────────────────
    public int ReturnedUnits { get; set; }
    public decimal RefundRatePercent { get; set; }

    // ── What's on the shelf ──────────────────────────────────────────────────────────────────
    /// <summary>Live listings matching this product. Null when eBay could not be read.</summary>
    public int? ActiveListings { get; set; }
    public int? ActiveUnits { get; set; }
    /// <summary>True when this line has sold before and there is nothing live to sell now.</summary>
    public bool SoldOut { get; set; }

    // ── The call ─────────────────────────────────────────────────────────────────────────────
    /// <summary>"restock", "watch", "stop" or "needs_cost".</summary>
    public string Verdict { get; set; } = "watch";

    /// <summary>One sentence: what this line did, in the numbers that justify the verdict.</summary>
    public string Headline { get; set; } = "";

    /// <summary>Everything working against the headline, said out loud rather than netted off it.</summary>
    public List<string> Cautions { get; set; } = [];
}

/// <summary>The board.</summary>
public sealed class RestockResult
{
    /// <summary>"ok" or "no_sales".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>"read", "unavailable" — whether the live listings behind "sold out" could be checked.</summary>
    public string StockStatus { get; set; } = "read";
    public string? StockNote { get; set; }

    /// <summary>Proven repeaters, best money-per-month first. The shopping list.</summary>
    public List<RestockLine> Restock { get; set; } = [];

    /// <summary>Sold once, or sold well but a long time ago. Real, not proven.</summary>
    public List<RestockLine> Watch { get; set; } = [];

    /// <summary>Lines that lost money or came back. The most useful list on the page.</summary>
    public List<RestockLine> Stop { get; set; } = [];

    /// <summary>Sold, but with nothing recorded for what the goods cost — so nothing can be said.</summary>
    public List<RestockLine> NeedsCost { get; set; } = [];

    public RestockSummary Summary { get; set; } = new();

    /// <summary>What these numbers do and don't include, in the seller's language.</summary>
    public List<string> Honesty { get; set; } = [];
}

public sealed class RestockSummary
{
    public int SalesRead { get; set; }
    public int ProductLines { get; set; }
    public int RankedLines { get; set; }

    /// <summary>Money per month the ranked lines are making between them.</summary>
    public decimal ProvenMonthlyProfit { get; set; }

    /// <summary>
    /// The headline. Money per month from proven lines with nothing currently listed — earnings the
    /// seller has already demonstrated they can get and is not getting today.
    /// </summary>
    public decimal MonthlyProfitOffTheShelf { get; set; }
    public int SoldOutLines { get; set; }

    /// <summary>What restocking one of each sold-out line would cost, at what they paid last time.</summary>
    public decimal CashToRestockSoldOut { get; set; }

    /// <summary>Share of all proven profit coming from the single best line — concentration, stated.</summary>
    public decimal? TopLineShareOfProfitPercent { get; set; }
    public string? TopLineTitle { get; set; }

    public int LinesAwaitingCost { get; set; }
    public decimal ProceedsAwaitingCost { get; set; }
}
