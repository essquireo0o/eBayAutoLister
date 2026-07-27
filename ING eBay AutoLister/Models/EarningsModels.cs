namespace ING_eBay_AutoLister.Models;

// The "Money Made" tracker. Every other money screen in this app is a forecast — what a flip
// *would* make. This is the only one that reports what already happened, and that difference
// governs every rule in here: a realized sale is not an estimate, so nothing may be padded,
// reserved against, or rounded in the seller's favour to make the headline larger.
//
// The one number that decides whether a sale counts as profit at all is the cost basis, which
// eBay cannot supply (see CostBasisStore). A sale whose cost is unknown is NOT counted as
// profit — it is reported separately as money waiting on a cost. Counting proceeds as profit is
// the single easiest way to make this feature a lie, and it is the lie every "you've earned $X"
// dashboard tells.

/// <summary>One sold line — the stored record, before any money is computed from it.</summary>
/// <remarks>
/// The grain is the order line item, not the order: a two-item order is two flips with two
/// different cost bases, and rolling them together makes per-item ROI unrecoverable.
/// </remarks>
public sealed class FlipRecord
{
    public long Id { get; set; }

    /// <summary>"ebay" for an imported order line, "manual" for one the seller logged themselves.</summary>
    public string Source { get; set; } = "manual";

    // eBay identity. Together these are the idempotency key for an import: re-importing an
    // overlapping date range must update rows, never duplicate them into double-counted profit.
    public string OrderId { get; set; } = "";
    public string LineItemId { get; set; } = "";

    // Product identity, and the join to the cost the seller already entered in Inventory Health.
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";

    public DateTimeOffset SoldUtc { get; set; }
    public int Quantity { get; set; } = 1;

    /// <summary>What the buyer paid for the goods, excluding shipping and excluding sales tax.</summary>
    /// <remarks>
    /// Tax is excluded on purpose: eBay collects and remits marketplace facilitator tax, so it
    /// never reaches the seller. Including it would inflate both revenue and the apparent margin.
    /// </remarks>
    public decimal SalePrice { get; set; }

    /// <summary>Shipping the buyer paid on top. Revenue to the seller, and eBay charges fees on it.</summary>
    public decimal ShippingCharged { get; set; }

    /// <summary>eBay's actual marketplace fee for this line, when eBay reported one.</summary>
    public decimal? MarketplaceFee { get; set; }

    /// <summary>What the seller actually paid to ship it. Null means "not recorded", not "free".</summary>
    public decimal? ShippingCost { get; set; }

    /// <summary>Anything else this specific sale cost — a replacement part, a gas run, a return label.</summary>
    public decimal OtherCosts { get; set; }

    /// <summary>
    /// Per-flip cost override. Null means "use the cost basis stored for this listing/SKU", which
    /// is how a cost typed once in Inventory Health flows straight through to earnings.
    /// </summary>
    public decimal? UnitCost { get; set; }

    /// <summary>Money refunded to the buyer. A partial refund reduces revenue; a full one voids the sale.</summary>
    public decimal RefundedAmount { get; set; }

    /// <summary>"paid", "refunded" or "cancelled". Only "paid" lines contribute to earnings.</summary>
    public string Status { get; set; } = "paid";

    public string Note { get; set; } = "";
    public DateTimeOffset UpdatedUtc { get; set; }
}

/// <summary>A flip with its money worked out — what the UI actually renders.</summary>
public sealed class FlipProfit
{
    public FlipRecord Flip { get; set; } = new();

    public long Id => Flip.Id;
    public string Source => Flip.Source;
    public string Title => Flip.Title;
    public string ListingId => Flip.ListingId;
    public string Sku => Flip.Sku;
    public DateTimeOffset SoldUtc => Flip.SoldUtc;
    public int Quantity => Flip.Quantity;
    public string Status => Flip.Status;

    /// <summary>Sale price + buyer-paid shipping − refunds. What actually came in.</summary>
    public decimal GrossRevenue { get; set; }

    public decimal Fees { get; set; }
    /// <summary>True when <see cref="Fees"/> is eBay's own figure rather than this app's estimate.</summary>
    public bool FeesAreActual { get; set; }

    public decimal ShippingCost { get; set; }
    public decimal OtherCosts { get; set; }

    /// <summary>The landed cost of the goods sold (unit cost × quantity), when it is known.</summary>
    public decimal? CostOfGoods { get; set; }
    /// <summary>"flip" (typed on this sale), "basis" (from the shared cost-basis table), or "none".</summary>
    public string CostSource { get; set; } = "none";

    /// <summary>Revenue minus every known cost EXCEPT the goods. Always computable.</summary>
    public decimal NetProceeds { get; set; }

    /// <summary>The real number — null when the cost of goods is unknown, never zero-filled.</summary>
    public decimal? NetProfit { get; set; }

    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }

    /// <summary>True when the shipping cost is a genuine unknown that is quietly flattering this row.</summary>
    public bool ShippingCostUnknown { get; set; }
    /// <summary>Set when the shipping cost was assumed equal to what the buyer paid.</summary>
    public bool ShippingCostAssumed { get; set; }

    /// <summary>Plain-language notes about what in this row is measured and what is assumed.</summary>
    public List<string> Caveats { get; set; } = [];

    /// <summary>True when this sale is counted in the headline totals.</summary>
    public bool CountsTowardProfit => NetProfit.HasValue && Status == "paid";
}

/// <summary>One bucket of the profit-over-time chart.</summary>
public sealed class EarningsMonthPoint
{
    /// <summary>"2026-07" — sortable, and the client formats it for display.</summary>
    public string Month { get; set; } = "";
    public string Label { get; set; } = "";
    public decimal NetProfit { get; set; }
    public decimal GrossRevenue { get; set; }
    public int Sales { get; set; }
    /// <summary>Sales in this month whose cost is unknown — the bar is this much incomplete.</summary>
    public int SalesAwaitingCost { get; set; }
    public bool IsCurrentMonth { get; set; }
}

/// <summary>The headline: what the seller has actually made, and how much of it is measured.</summary>
public sealed class EarningsSummary
{
    public decimal NetProfitThisMonth { get; set; }
    public decimal NetProfitLastMonth { get; set; }
    public decimal NetProfitAllTime { get; set; }
    public decimal NetProfitLast30Days { get; set; }
    public decimal NetProfitThisYear { get; set; }

    /// <summary>Signed change against last month, in percent. Null when last month was zero.</summary>
    public decimal? MonthOverMonthPercent { get; set; }

    public decimal GrossRevenueAllTime { get; set; }
    public decimal FeesAllTime { get; set; }
    public decimal CostOfGoodsAllTime { get; set; }
    public decimal ShippingCostAllTime { get; set; }

    public int SalesAllTime { get; set; }
    public int SalesThisMonth { get; set; }
    public int UnitsAllTime { get; set; }

    public decimal? AverageProfitPerSale { get; set; }
    public decimal? AverageRoiPercent { get; set; }
    public decimal? AverageMarginPercent { get; set; }

    /// <summary>Sales whose cost is unknown, and the proceeds sitting behind them.</summary>
    public int SalesAwaitingCost { get; set; }
    public decimal ProceedsAwaitingCost { get; set; }

    public int SalesRefunded { get; set; }
    public decimal RefundedAmount { get; set; }

    /// <summary>How much of the all-time profit rests on eBay's own fee figures rather than an estimate.</summary>
    public decimal ProfitFromActualFees { get; set; }
    public decimal ProfitFromEstimatedFees { get; set; }

    /// <summary>
    /// How much of <see cref="FeesAllTime"/> is eBay's own figure, and on how many sales.
    /// </summary>
    /// <remarks>
    /// Reported on fees rather than only on profit because a sale can have a measured fee and still
    /// contribute no profit (no cost basis yet). Saying "$0 of your profit uses eBay's real fees" on
    /// an account where every fee WAS measured is technically true and completely misleading.
    /// </remarks>
    public decimal FeesMeasured { get; set; }
    public int SalesWithMeasuredFees { get; set; }
    /// <summary>How much came from eBay orders versus flips the seller typed in.</summary>
    public decimal ProfitFromEbay { get; set; }
    public decimal ProfitFromManual { get; set; }

    public int SalesWithUnknownShipping { get; set; }

    public string? BestMonthLabel { get; set; }
    public decimal BestMonthProfit { get; set; }

    public DateTimeOffset? FirstSaleUtc { get; set; }
    public DateTimeOffset? LastSaleUtc { get; set; }
    public DateTimeOffset? LastImportUtc { get; set; }
}

/// <summary>Everything the Money Made page renders in one response.</summary>
public sealed class EarningsResult
{
    public EarningsSummary Summary { get; set; } = new();
    public List<EarningsMonthPoint> Months { get; set; } = [];
    /// <summary>Top PROFITABLE flips — the proof, one line each. Empty when nothing made money.</summary>
    public List<FlipProfit> BestFlips { get; set; } = [];
    /// <summary>The sales that lost money. Shown, not hidden — they are the most useful rows here.</summary>
    public List<FlipProfit> WorstFlips { get; set; } = [];
    /// <summary>Top flips by return on what was paid. A $40 profit on a $10 buy beats a $60 on a $900.</summary>
    public List<FlipProfit> BestReturns { get; set; } = [];
    /// <summary>Sales that would count if the seller entered what they paid — the fastest way to grow the number honestly.</summary>
    public List<FlipProfit> AwaitingCost { get; set; } = [];
    public List<FlipProfit> Flips { get; set; } = [];
    /// <summary>Plain-language statements of what the totals do and don't include.</summary>
    public List<string> Honesty { get; set; } = [];
}

/// <summary>What an import of eBay orders actually did.</summary>
public sealed class EarningsImportResult
{
    public string Status { get; set; } = "ok";
    public string? Message { get; set; }
    public int OrdersRead { get; set; }
    public int LinesImported { get; set; }
    public int LinesAdded { get; set; }
    public int LinesUpdated { get; set; }
    public int LinesSkipped { get; set; }
    public int DaysRequested { get; set; }
    public bool FeesReportedByEbay { get; set; }
    public int MatchedToCostBasis { get; set; }
}

/// <summary>An eBay order as the Sell Fulfillment API returns it, reduced to what earnings needs.</summary>
public sealed class EbayOrderSummary
{
    public string OrderId { get; set; } = "";
    public string LegacyOrderId { get; set; } = "";
    public DateTimeOffset CreationDate { get; set; }
    public string PaymentStatus { get; set; } = "";
    public string CancelState { get; set; } = "";
    /// <summary>eBay's own total fee for the order. The whole reason this import beats hand-entry.</summary>
    public decimal? TotalMarketplaceFee { get; set; }
    public decimal OrderTotal { get; set; }
    public List<EbayOrderLineItem> LineItems { get; set; } = [];
}

public sealed class EbayOrderLineItem
{
    public string LineItemId { get; set; } = "";
    public string LegacyItemId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public int Quantity { get; set; } = 1;
    /// <summary>Goods only — excludes shipping and excludes eBay-collected tax.</summary>
    public decimal LineItemCost { get; set; }
    public decimal ShippingCharged { get; set; }
    public decimal RefundedAmount { get; set; }
}

/// <summary>A manual flip arriving from the browser, or an edit to an imported one.</summary>
public sealed class FlipUpsertRequest
{
    public long? Id { get; set; }
    public string? Title { get; set; }
    public string? ListingId { get; set; }
    public string? Sku { get; set; }
    public DateTimeOffset? SoldUtc { get; set; }
    public int? Quantity { get; set; }
    public decimal? SalePrice { get; set; }
    public decimal? ShippingCharged { get; set; }
    public decimal? MarketplaceFee { get; set; }
    public decimal? ShippingCost { get; set; }
    public decimal? OtherCosts { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? RefundedAmount { get; set; }
    public string? Status { get; set; }
    public string? Note { get; set; }
}
