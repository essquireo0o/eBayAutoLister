namespace ING_eBay_AutoLister.Models;

// ── The Deal Pipeline: Sourced → Bought → Listed → Sold ───────────────────────────────────────
//
// Every other money screen in this app is a snapshot of one moment. The Opportunity Finder says
// what a thing would make if you bought it; Inventory Health says what a live listing is worth
// today; Money Made says what a completed sale earned. Nothing joined them, so a flip existed as
// four unconnected facts on four different screens and the seller carried the thread in their head.
//
// This is that thread. One deal, tracked from the moment it was spotted to the moment the money
// landed, carrying the forecast that justified the buy all the way to the sale that settled it.
//
// The rules that govern this file:
//
//   1. A projection is NEVER money. Projected profit and realized profit are separate totals on
//      every card and in every roll-up, and no arithmetic anywhere adds them together.
//   2. Capital at risk is only what the seller says they actually PAID. A forecast can't put money
//      at risk; a bank transfer can.
//   3. Realized profit is not computed here. It comes from the matched sale in EarningsStore,
//      which carries the fee eBay actually charged — see EarningsCalculator.
//   4. The forecast is graded against the outcome, honestly and in public. A pipeline that only
//      ever showed optimistic projections would be a wish list; one that reports "your forecasts
//      came in 18% high across 11 closed deals" makes the next forecast worth something.

/// <summary>One flip, as stored: the forecast that justified it and what has actually happened since.</summary>
/// <remarks>
/// The stored <see cref="Stage"/> is what the seller last told us. It is not necessarily the
/// current truth — a listed deal whose sale has since been imported from eBay is sold whether or
/// not anyone moved the card. <see cref="DealCard.Stage"/> is the derived answer; this is the
/// record of what was said, which is deliberately never overwritten by a read.
/// </remarks>
public sealed class DealRecord
{
    public long Id { get; set; }

    /// <summary>sourced | bought | listed | sold | dropped. See <see cref="DealStages"/>.</summary>
    public string Stage { get; set; } = DealStages.Sourced;

    public string Title { get; set; } = "";

    // Where it came from, so a card can be opened back up on the site it was found on. Set by the
    // "Track this deal" button in the goldmine table; blank for a deal typed in by hand.
    public string Source { get; set; } = "manual";
    public string SourceLabel { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    /// <summary>The source's own post id. Tracking the same post twice updates the deal, not duplicates it.</summary>
    public string SourceItemId { get; set; } = "";

    public int Quantity { get; set; } = 1;

    // ── The forecast, frozen at the moment the deal was tracked ───────────────────────────────
    // Frozen on purpose. Re-running the comps later would quietly rewrite history and make the
    // app's forecasts look better than they were, which is the one thing that would make the
    // accuracy figure below worthless.

    /// <summary>What the seller was asking when the deal was spotted.</summary>
    public decimal? AskPrice { get; set; }
    /// <summary>The highest price that still breaks even, from LocalArbitrageAnalyzer.</summary>
    public decimal? MaxBuyPrice { get; set; }
    public decimal? ProjectedSalePrice { get; set; }
    /// <summary>Projected net profit PER UNIT, after fees and shipping, at the ask price above.</summary>
    public decimal? ProjectedNetProfit { get; set; }
    public decimal? ProjectedRoiPercent { get; set; }
    public int? ProjectedDaysToCash { get; set; }
    /// <summary>What the forecast rested on — "14 sold comps · High confidence". Shown, not summarised away.</summary>
    public string ProjectedBasis { get; set; } = "";
    public DateTimeOffset? ProjectedUtc { get; set; }

    // ── Bought ────────────────────────────────────────────────────────────────────────────────

    /// <summary>What was actually paid, per unit. This is the number that puts capital at risk.</summary>
    public decimal? PurchasePrice { get; set; }
    /// <summary>Everything else the buy cost, for the whole deal — the gas, the freight, the parts.</summary>
    public decimal PurchaseExtraCost { get; set; }
    public DateTimeOffset? BoughtUtc { get; set; }

    // ── Listed ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The eBay listing ID. This is the join that lets a sale find its way back to this deal.</summary>
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal? ListedPrice { get; set; }
    public DateTimeOffset? ListedUtc { get; set; }

    // ── Sold ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Only set when the seller closed the deal by hand; a matched eBay sale carries its own date.</summary>
    public DateTimeOffset? SoldUtc { get; set; }

    public string Note { get; set; } = "";
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
    /// <summary>When the stage last changed — what "9 days in Bought" is measured from.</summary>
    public DateTimeOffset StageChangedUtc { get; set; }
}

public static class DealStages
{
    public const string Sourced = "sourced";
    public const string Bought = "bought";
    public const string Listed = "listed";
    public const string Sold = "sold";
    /// <summary>Walked away, or bought and written off. Off the board, still counted.</summary>
    public const string Dropped = "dropped";

    /// <summary>The four board columns, in pipeline order. Dropped is deliberately not one.</summary>
    public static readonly string[] Board = [Sourced, Bought, Listed, Sold];

    public static readonly string[] All = [Sourced, Bought, Listed, Sold, Dropped];

    public static string Normalize(string? stage) => (stage ?? "").Trim().ToLowerInvariant() switch
    {
        Bought or "buy" or "purchased" => Bought,
        Listed or "listing" or "live" => Listed,
        Sold => Sold,
        Dropped or "dead" or "passed" or "walked" => Dropped,
        _ => Sourced,
    };

    public static string Label(string stage) => stage switch
    {
        Bought => "Bought",
        Listed => "Listed",
        Sold => "Sold",
        Dropped => "Dropped",
        _ => "Sourced",
    };

    public static int Order(string stage) => Array.IndexOf(All, stage) is var i and >= 0 ? i : 0;
}

/// <summary>What to do about this deal next, and what it's worth doing.</summary>
/// <remarks>
/// The whole point of a pipeline. A board that only shows where things are is a filing cabinet;
/// the money is in "$1,240 of your cash has been in a box for 19 days — list it".
/// </remarks>
public sealed class DealAction
{
    public long DealId { get; set; }
    public string Title { get; set; } = "";
    public string Stage { get; set; } = "";
    /// <summary>The imperative — "List it", "Reprice it", "Record what you paid".</summary>
    public string Label { get; set; } = "";
    /// <summary>Why, in one sentence, with the number in it.</summary>
    public string Detail { get; set; } = "";
    /// <summary>normal | warn | urgent. Urgency tracks money and time, never novelty.</summary>
    public string Urgency { get; set; } = "normal";
    /// <summary>Dollars this action is about — the ranking key for the do-this-next list.</summary>
    public decimal AmountAtStake { get; set; }
    /// <summary>Where the fix lives: inventory | offers | listings | pipeline | source.</summary>
    public string Target { get; set; } = "pipeline";
}

/// <summary>One deal with its money worked out — what a board card renders.</summary>
public sealed class DealCard
{
    public DealRecord Deal { get; set; } = new();

    public long Id => Deal.Id;
    public string Title => Deal.Title;
    public int Quantity => Deal.Quantity;

    /// <summary>The stage this deal is really in, which may be ahead of what the seller last said.</summary>
    public string Stage { get; set; } = DealStages.Sourced;
    public string StageLabel { get; set; } = "";
    /// <summary>True when a matched eBay sale moved this card, not a click.</summary>
    public bool StageAutoDerived { get; set; }

    // ── Projected ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Projected profit for the whole deal at the ORIGINAL ask — the forecast as made.</summary>
    public decimal? ForecastProfit { get; set; }

    /// <summary>
    /// Projected profit rebased on what was actually paid, once it has been. Null until then.
    /// </summary>
    /// <remarks>
    /// Net profit moves exactly one dollar per dollar paid — the same identity that makes
    /// LocalArbitrageAnalyzer's max-buy price exact arithmetic rather than a heuristic — so
    /// haggling $80 off the ask is $80 more profit, and paying $80 over is $80 less. Rebasing is
    /// therefore not a re-forecast: the sale-side estimate is untouched.
    /// </remarks>
    public decimal? ExpectedProfit { get; set; }

    /// <summary>What the buy-side negotiation actually saved (ask − paid), for the whole deal.</summary>
    public decimal? NegotiatedSaving { get; set; }

    // ── Real money ────────────────────────────────────────────────────────────────────────────

    /// <summary>What was actually spent: unit price × quantity + the extras. Zero until bought.</summary>
    public decimal CapitalSpent { get; set; }
    /// <summary>Capital still out — spent, and not yet returned by a sale.</summary>
    public decimal CapitalAtRisk { get; set; }

    /// <summary>Realized net profit from matched sales. Null when nothing has sold, or no cost is known.</summary>
    public decimal? RealizedProfit { get; set; }
    public decimal RealizedRevenue { get; set; }
    public int UnitsSold { get; set; }
    /// <summary>Matched sales whose profit is unknown because no cost basis reached them.</summary>
    public int SalesAwaitingCost { get; set; }
    /// <summary>Ids of the sales in Money Made this deal is joined to — the audit trail for the number.</summary>
    public List<long> FlipIds { get; set; } = [];

    /// <summary>Realized − expected, on closed deals that have both. The grade on the forecast.</summary>
    public decimal? ProfitVariance { get; set; }
    public decimal? ProfitVariancePercent { get; set; }

    // ── Time ──────────────────────────────────────────────────────────────────────────────────

    public int DaysInStage { get; set; }
    public int DaysTracked { get; set; }
    /// <summary>Days from money out to money back, on deals that closed. The real cash cycle.</summary>
    public int? DaysToCashActual { get; set; }

    /// <summary>What this card is waiting on. Null when there is genuinely nothing to do.</summary>
    public DealAction? NextAction { get; set; }
    /// <summary>Things worth knowing that aren't actions — "paid $60 over the break-even ceiling".</summary>
    public List<string> Flags { get; set; } = [];
}

/// <summary>One board column.</summary>
public sealed class DealStageSummary
{
    public string Stage { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    public int Units { get; set; }
    /// <summary>Real money sitting in this column.</summary>
    public decimal Capital { get; set; }
    /// <summary>Forecast profit in this column. Kept separate from <see cref="Capital"/> everywhere.</summary>
    public decimal ProjectedProfit { get; set; }
    public decimal RealizedProfit { get; set; }
}

/// <summary>The headline: what money is in motion, and what has already come back.</summary>
public sealed class DealPipelineSummary
{
    public int TotalDeals { get; set; }
    /// <summary>Deals still in play — sourced, bought or listed.</summary>
    public int ActiveDeals { get; set; }

    /// <summary>Money actually spent that hasn't come back yet. The number that keeps a seller awake.</summary>
    public decimal CapitalAtRisk { get; set; }
    /// <summary>Everything ever put into tracked deals.</summary>
    public decimal CapitalDeployedAllTime { get; set; }

    /// <summary>Forecast profit on deals already bought — the money the seller is working to collect.</summary>
    public decimal ProjectedProfitInMotion { get; set; }
    /// <summary>Forecast profit on deals not yet bought. A shopping list, not an asset. Reported apart.</summary>
    public decimal ProjectedProfitSourced { get; set; }

    /// <summary>Realized profit across matched sales. Comes from Money Made; uses eBay's real fees.</summary>
    public decimal RealizedProfit { get; set; }
    public decimal RealizedRevenue { get; set; }
    public int DealsClosed { get; set; }
    public int DealsProfitable { get; set; }
    public int DealsAtALoss { get; set; }

    // ── The grade on the forecast ─────────────────────────────────────────────────────────────

    /// <summary>Closed deals that have both a forecast and a realized profit. The sample size.</summary>
    public int GradedDeals { get; set; }
    public decimal GradedForecastProfit { get; set; }
    public decimal GradedRealizedProfit { get; set; }
    /// <summary>Realized as a percentage of forecast. 100 means the forecasts were right.</summary>
    public decimal? ForecastAccuracyPercent { get; set; }
    public decimal ForecastDelta { get; set; }

    // ── What's stuck ──────────────────────────────────────────────────────────────────────────

    /// <summary>Bought and not listed past the grace period — cash in a box.</summary>
    public int StalledDeals { get; set; }
    public decimal StalledCapital { get; set; }
    /// <summary>Listed and unsold past what the forecast said it would take.</summary>
    public int OverdueDeals { get; set; }
    public decimal OverdueCapital { get; set; }

    /// <summary>Matched sales still missing a cost basis the pipeline could supply.</summary>
    public int SalesAwaitingCost { get; set; }

    /// <summary>Median days from money out to money back, on closed deals. Null under 3 of them.</summary>
    public int? MedianDaysToCash { get; set; }

    public DateTimeOffset? FirstDealUtc { get; set; }
    public DateTimeOffset? LastActivityUtc { get; set; }
}

/// <summary>Everything the pipeline board renders in one response.</summary>
public sealed class DealPipelineResult
{
    public DealPipelineSummary Summary { get; set; } = new();
    public List<DealStageSummary> Stages { get; set; } = [];
    public List<DealCard> Deals { get; set; } = [];
    /// <summary>Ranked by dollars at stake. The list a seller works down on a Tuesday morning.</summary>
    public List<DealAction> NextActions { get; set; } = [];
    /// <summary>Plain statements of what these totals do and don't mean.</summary>
    public List<string> Honesty { get; set; } = [];
}

/// <summary>A deal arriving from the browser — a tracked goldmine row, or one typed in by hand.</summary>
public sealed class DealUpsertRequest
{
    public long? Id { get; set; }
    public string? Stage { get; set; }
    public string? Title { get; set; }
    public string? Source { get; set; }
    public string? SourceLabel { get; set; }
    public string? SourceUrl { get; set; }
    public string? SourceItemId { get; set; }
    public int? Quantity { get; set; }

    public decimal? AskPrice { get; set; }
    public decimal? MaxBuyPrice { get; set; }
    public decimal? ProjectedSalePrice { get; set; }
    public decimal? ProjectedNetProfit { get; set; }
    public decimal? ProjectedRoiPercent { get; set; }
    public int? ProjectedDaysToCash { get; set; }
    public string? ProjectedBasis { get; set; }

    public decimal? PurchasePrice { get; set; }
    public decimal? PurchaseExtraCost { get; set; }
    public DateTimeOffset? BoughtUtc { get; set; }

    public string? ListingId { get; set; }
    public string? Sku { get; set; }
    public decimal? ListedPrice { get; set; }
    public DateTimeOffset? ListedUtc { get; set; }

    public DateTimeOffset? SoldUtc { get; set; }
    public string? Note { get; set; }
}

/// <summary>What a stage change did, beyond moving a card.</summary>
public sealed class DealStageChangeResult
{
    public DealPipelineResult Pipeline { get; set; } = new();
    /// <summary>True when the purchase price was written to the shared cost-basis table.</summary>
    public bool CostBasisSaved { get; set; }
    public decimal? CostBasisUnitCost { get; set; }
    /// <summary>Completed sales this deal's cost basis just priced, in Money Made.</summary>
    public int SalesPriced { get; set; }
    public string? Message { get; set; }
}
