namespace ING_eBay_AutoLister.Models;

// ── All-in net proceeds (see Services/NetProceedsCalculator.cs) ──────────────────────────────

/// <summary>One deduction between the sticker price and the seller's bank account.</summary>
/// <remarks>
/// Zero-value lines are kept rather than dropped. A seller who has not configured return reserves
/// should see "Returns reserve $0.00" and understand it is a knob they have not turned, not be
/// left to assume the app forgot about returns.
/// </remarks>
public sealed record NetQuoteLine(string Key, string Label, decimal Amount, string Detail);

/// <summary>
/// What one sale at a given asking price actually leaves the seller, and the two prices they must
/// not go below.
/// </summary>
/// <remarks>
/// <para>
/// This is the single object behind every price shown anywhere in the app. Before it, the pricing
/// surfaces disagreed by construction: the listing editor showed a bare asking price with no fees
/// at all, the market-research panel recommended a median with no costs subtracted, and only the
/// sourcing screens (arbitrage, lots, inventory health) ever ran the numbers through
/// <see cref="ProfitBreakdown"/>. A seller could be told "$120" by three screens and take home
/// three different amounts.
/// </para>
/// <para>
/// The three numbers that matter, in the order a seller needs them:
/// <see cref="NetProfit"/> is what this sale is worth; <see cref="BreakEvenPrice"/> is the price
/// below which it costs money to make the sale; <see cref="MinimumOfferPrice"/> is the lowest
/// number to say yes to in a negotiation, which is higher than break-even whenever the seller has
/// set a floor policy worth honouring.
/// </para>
/// </remarks>
public sealed class NetQuote
{
    // ── What was asked ───────────────────────────────────────────────────────────────────────
    public decimal AskPrice { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    public decimal GrossRevenue { get; set; }
    public int Quantity { get; set; } = 1;

    /// <summary>What the seller paid for one unit, or 0 when they have not told the app.</summary>
    public decimal UnitCost { get; set; }
    /// <summary>
    /// False when no cost basis is known. Everything on the fee side is still exact; only the
    /// profit-side figures (<see cref="NetProfit"/>, ROI, break-even, the floor) are assuming a
    /// cost of zero, and the UI says so rather than showing a flattering number as fact.
    /// </summary>
    public bool HasCostBasis { get; set; }

    // ── What comes out ───────────────────────────────────────────────────────────────────────
    public decimal EbayFinalValueFee { get; set; }
    public decimal PromotedListingFee { get; set; }
    public decimal PaymentProcessingFee { get; set; }
    public decimal ReturnReserve { get; set; }
    public decimal TestingReserve { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal PackagingCost { get; set; }
    public decimal LaborCost { get; set; }
    public decimal OtherCosts { get; set; }

    /// <summary>Everything the marketplace and the processor take.</summary>
    public decimal TotalFees { get; set; }
    /// <summary>Fees plus fulfilment plus reserves — every deduction except cost of goods.</summary>
    public decimal TotalDeductions { get; set; }
    /// <summary>Gross minus every deduction: what lands in the bank, before what the item cost.</summary>
    public decimal NetProceeds { get; set; }
    /// <summary>Net proceeds minus the unit cost. The number the whole app is about.</summary>
    public decimal NetProfit { get; set; }
    public decimal TotalNetProfit { get; set; }

    public decimal? MarginPercent { get; set; }
    public decimal? RoiPercent { get; set; }
    /// <summary>Deductions as a share of gross — "eBay and shipping take 21% of this sale".</summary>
    public decimal FeeLoadPercent { get; set; }

    // ── The floors ───────────────────────────────────────────────────────────────────────────
    public decimal BreakEvenPrice { get; set; }
    public decimal MinimumOfferPrice { get; set; }
    /// <summary>break_even | profit_target | margin_target — which rule set the floor.</summary>
    public string MinimumOfferBasis { get; set; } = "break_even";
    public decimal MinimumNetProfit { get; set; }
    public decimal MinimumMarginPercent { get; set; }
    /// <summary>Take-home at the floor. Equals the profit target when that is what bound it.</summary>
    public decimal NetProfitAtMinimumOffer { get; set; }

    public bool BelowBreakEven { get; set; }
    public bool BelowMinimumOffer { get; set; }

    // ── How to say it ────────────────────────────────────────────────────────────────────────
    /// <summary>loss | below_floor | thin | profitable — drives the colour, not just the copy.</summary>
    public string Verdict { get; set; } = "profitable";
    public string Headline { get; set; } = "";
    public string Note { get; set; } = "";
    public List<NetQuoteLine> Lines { get; set; } = [];
}

/// <summary>
/// One request for net figures at several candidate prices at once — the asking price, the comps
/// median, a suggested price — so a whole pricing panel costs one round trip instead of one per
/// number shown.
/// </summary>
public sealed class NetQuoteRequest
{
    public List<decimal> Prices { get; set; } = [];
    public decimal? UnitCost { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    public int Quantity { get; set; } = 1;
    /// <summary>Per-item actual shipping cost, when it differs from the profile default.</summary>
    public decimal? ShippingCost { get; set; }
    public decimal? OtherCosts { get; set; }
}

public sealed class NetQuoteResponse
{
    public List<NetQuote> Quotes { get; set; } = [];
    /// <summary>
    /// Independent of any asking price, so the UI can show the two floors even before a price is
    /// typed. Null when no cost basis was supplied — there is no floor without one.
    /// </summary>
    public decimal? BreakEvenPrice { get; set; }
    public decimal? MinimumOfferPrice { get; set; }
    public string MinimumOfferBasis { get; set; } = "break_even";
    public bool HasCostBasis { get; set; }
    /// <summary>The fee assumptions used, echoed back so the panel can explain and link to them.</summary>
    public FeeProfileView Fees { get; set; } = new();
}

/// <summary>
/// The wire shape of <c>Services/FeeProfile</c>. A DTO rather than serialising the service object
/// directly: FeeProfile exposes public fields and computed properties, and a settings form needs a
/// stable, bindable set of properties it can round-trip.
/// </summary>
public sealed class FeeProfileView
{
    public decimal EbayFinalValueFeePercent { get; set; } = 13.25m;
    public decimal EbayFinalValueFeeFixed { get; set; } = 0.40m;
    public decimal PromotedListingRatePercent { get; set; }
    public decimal PaymentProcessingPercent { get; set; }
    public decimal DefaultShippingCost { get; set; }
    public decimal DefaultPackagingCost { get; set; }
    public decimal DefaultLaborCost { get; set; }
    public decimal ReturnReservePercent { get; set; }
    public decimal TestingReservePercent { get; set; }
    public decimal MinimumNetProfit { get; set; }
    public decimal MinimumMarginPercent { get; set; }

    /// <summary>Read-only echo — what share of every sale the configured percentages take.</summary>
    public decimal RevenueFeePercent { get; set; }
}
