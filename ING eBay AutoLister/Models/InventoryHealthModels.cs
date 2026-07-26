namespace ING_eBay_AutoLister.Models;

// One live eBay listing, judged against what the market is paying for it today.
//
// Every money field is nullable where it depends on data that may not exist — a listing with no
// sold comps has no market price, and a listing with no recorded cost basis has no break-even.
// Those are reported as unknown rather than defaulted to zero, because a zero floor would let the
// repricer recommend a markdown straight through a loss.
public sealed class InventoryHealthItem
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Category { get; set; } = "";

    // ── What eBay says about the listing as it stands ────────────────────────────────────────
    public decimal ListPrice { get; set; }
    public int Quantity { get; set; }
    public int WatchCount { get; set; }
    public int QuantitySold { get; set; }
    public DateTime? StartTimeUtc { get; set; }
    public int? DaysListed { get; set; }
    // Units sold per 30 days over the listing's whole life. eBay reports a cumulative sold count
    // with no dates attached, so this is a lifetime average and is labelled as one — it cannot
    // distinguish "steady seller" from "sold out fast then stopped".
    public decimal? SalesPerMonth { get; set; }
    public bool IsSelling => QuantitySold > 0;

    // ── What the seller paid ─────────────────────────────────────────────────────────────────
    public decimal? CostBasis { get; set; }
    public string CostBasisNote { get; set; } = "";
    public bool HasCostBasis => CostBasis.HasValue;

    // ── What the market is paying ────────────────────────────────────────────────────────────
    public decimal? MarketPrice { get; set; }
    public decimal? MarketMedian { get; set; }
    public decimal? QuickSalePrice { get; set; }
    public string PricedAs { get; set; } = "";
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public string ResaleSource { get; set; } = "none";
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";

    // How far above (positive) or below (negative) today's market the listing sits, as a percent
    // of the market price. This is the number the whole feature turns on.
    public decimal? PriceGapPercent { get; set; }

    // Units in one sale of this listing, read from the title ("Lot of 20", "(4) …", "5x …").
    public int LotQuantity { get; set; } = 1;
    // False when the market figure is not a like-for-like comparison with the asking price — a
    // multi-unit lot against per-unit comps, or a gap so large it is a matching failure rather
    // than a mispricing. Nothing is ever recommended off a comparison that failed this.
    public bool MarketComparable { get; set; } = true;

    // ── The recommendation ───────────────────────────────────────────────────────────────────
    public decimal? BreakEvenPrice { get; set; }
    // The lowest price worth saying yes to on this item — break-even raised by whatever profit or
    // margin floor the seller set in Fees & Costs. This is the number to negotiate a Best Offer
    // against, and the floor the markdown ladder is not allowed through.
    public decimal? MinimumOfferPrice { get; set; }
    public string MinimumOfferBasis { get; set; } = "break_even";
    public decimal? NetProfitAtMinimumOffer { get; set; }
    public decimal? SuggestedPrice { get; set; }
    public decimal? SuggestedChangePercent { get; set; }
    public decimal? NetProfitAtListPrice { get; set; }
    public decimal? NetProfitAtSuggested { get; set; }
    // True when the age ladder wanted to cut deeper but the break-even floor stopped it. Shown,
    // not hidden: "this is as low as this item can go" is the useful half of the answer.
    public bool FloorLimited { get; set; }
    // A price rise is never bundled into a bulk apply — see InventoryHealthAnalyzer.SuggestPrice.
    public bool RequiresReview { get; set; }

    // Capital this listing is holding, at the best basis available: what was paid if known,
    // otherwise what the market says it's worth, otherwise the asking price.
    public decimal CapitalTiedUp { get; set; }
    public string CapitalBasis { get; set; } = "list_price";

    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";
    public List<string> Signals { get; set; } = [];

    public bool HasRecommendation => SuggestedPrice.HasValue;
}

// The portfolio view: what the seller's live inventory is doing as a whole, which is the thing no
// screen in eBay Seller Hub actually shows.
public sealed class InventoryHealthSummary
{
    public int ListingsAnalyzed { get; set; }
    public int ListingsPriced { get; set; }
    public decimal TotalListedValue { get; set; }
    public decimal TotalCapitalTiedUp { get; set; }
    public int WithCostBasis { get; set; }

    public int StaleCount { get; set; }
    public decimal StaleCapital { get; set; }
    public int DeadCapitalCount { get; set; }
    public decimal DeadCapital { get; set; }

    public int OverpricedCount { get; set; }
    public decimal TotalAboveMarket { get; set; }
    public int UnderpricedCount { get; set; }
    // What the underpriced listings give away per sale at their current prices — the quiet half
    // of the problem, and the half no markdown tool looks for.
    public decimal MoneyLeftOnTable { get; set; }

    public int UnderwaterCount { get; set; }
    public int PricedRightCount { get; set; }
    public int NoDataCount { get; set; }

    public int RepriceCandidates { get; set; }
    // Expected take-home if every recommended markdown were applied AND those items then sold —
    // conditional on a sale, and labelled that way everywhere it is shown.
    public decimal ProjectedNetIfRepricedSells { get; set; }
    public int? MedianDaysListed { get; set; }
    public int UnknownAgeCount { get; set; }
}

public sealed class InventoryHealthResult
{
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public int ActiveListings { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }
    public bool SoldCompsConfigured { get; set; }
    public string? DataWarning { get; set; }
    public InventoryHealthSummary Summary { get; set; } = new();
    public List<InventoryHealthItem> Items { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ── Repricing ────────────────────────────────────────────────────────────────────────────────

public sealed class RepriceItemRequest
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal NewPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public int Quantity { get; set; } = 1;
    // Sent back from the health scan so the server can re-check the price against the floor it
    // computed, rather than trusting a number that arrived from a browser.
    public decimal? BreakEvenPrice { get; set; }
}

public sealed class RepriceRequest
{
    public List<RepriceItemRequest> Items { get; set; } = [];
    // Nothing reaches eBay unless this is explicitly true. The default is a preview.
    public bool Confirmed { get; set; }
    public bool DryRun { get; set; } = true;
    // Opt-in override for the "this sells at a loss" refusal, so a seller who has decided to clear
    // dead stock at a loss can still do it — deliberately, and on the record in the action log.
    public bool AllowBelowBreakEven { get; set; }
}

public sealed class RepriceItemResult
{
    public string ListingId { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public decimal ChangePercent { get; set; }
    public string Status { get; set; } = "pending";   // applied | preview | skipped | failed
    public string Message { get; set; } = "";
}

public sealed class RepriceResult
{
    public bool DryRun { get; set; } = true;
    public int Requested { get; set; }
    public int Applied { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<RepriceItemResult> Items { get; set; } = [];
}
