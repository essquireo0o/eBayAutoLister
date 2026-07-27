namespace ING_eBay_AutoLister.Models;

// Promoted Listings ROI — what an ad rate is actually worth on one listing.
//
// eBay Promoted Listings Standard charges a percentage of the whole sale (item + shipping) on every
// ad-attributed sale. That fee comes out of the margin, and eBay's own interface offers a
// "suggested" rate with no reference to what the seller paid for the item — so a 12% rate on a 9%
// margin is one click away and looks like a recommendation. These types carry the other half of the
// decision: what each rate costs per sale, how much extra volume it has to buy to pay for itself,
// and the rate that leaves the most money on the table at the end of the month.

/// <summary>One rung of the ad-rate ladder — the tradeoff, made visible.</summary>
public sealed class AdRatePoint
{
    public decimal RatePercent { get; set; }

    /// <summary>What eBay bills on one ad-attributed sale at this rate.</summary>
    public decimal AdFeePerSale { get; set; }

    /// <summary>Net profit on one ad-attributed sale — the same net every other screen shows, minus the ad fee.</summary>
    public decimal? NetPerSale { get; set; }

    /// <summary>
    /// The extra sales this rate has to buy just to leave the seller no worse off. Pure arithmetic
    /// off the ad fee, the margin and the cannibalisation assumption — no lift model involved, which
    /// is why it is the number to trust when the model is only a model. Null when no amount of extra
    /// volume can pay for the rate, because the fee is bigger than the margin.
    /// </summary>
    public decimal? BreakEvenLiftPercent { get; set; }

    /// <summary>What the lift curve expects this rate to actually deliver. An estimate, labelled as one.</summary>
    public decimal ModeledLiftPercent { get; set; }

    /// <summary>
    /// Take-home across 100 sales the listing would have made without ads — the units-free way to
    /// compare rates, since the optimal rate does not depend on how many units the listing moves.
    /// </summary>
    public decimal? NetPer100Sales { get; set; }

    /// <summary>Change in that figure against running no ads at all.</summary>
    public decimal? NetChangePer100 { get; set; }

    public bool IsRecommended { get; set; }
    public bool IsCurrent { get; set; }

    /// <summary>The ad fee at this rate is bigger than the profit on the sale — no volume fixes that.</summary>
    public bool AboveCeiling { get; set; }
}

/// <summary>
/// The three numbers the recommendation is built on, reported rather than buried, because they are
/// assumptions and the seller is entitled to disagree with them.
/// </summary>
public sealed class PromotedAssumptions
{
    /// <summary>Most extra sales ads can add on this listing, as a percentage of its organic sales.</summary>
    public decimal MaxLiftPercent { get; set; }

    /// <summary>The ad rate that buys half of that ceiling — set by what the category typically pays.</summary>
    public decimal HalfLiftRatePercent { get; set; }

    /// <summary>
    /// Share of the sales the listing would have made anyway that get billed as ad-attributed. This
    /// is the quiet cost of Promoted Listings: a buyer who would have found the item regardless
    /// still arrives through the ad, and the fee is charged all the same.
    /// </summary>
    public decimal CannibalizationPercent { get; set; }

    public string Basis { get; set; } = "";
}

/// <summary>The ad-rate decision for one listing.</summary>
public sealed class PromotedAdvice
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    // ── The money on one sale ────────────────────────────────────────────────────────────────
    public decimal ListPrice { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    /// <summary>Item price plus buyer-paid shipping — eBay charges the ad rate on the whole thing.</summary>
    public decimal GrossPerSale { get; set; }
    public decimal? UnitCost { get; set; }
    public bool HasCostBasis { get; set; }
    /// <summary>Net profit per sale with no ads running. Every rate below is measured against this.</summary>
    public decimal? NetPerSaleNoAds { get; set; }
    public decimal? MarginPercent { get; set; }

    // ── What the category pays ───────────────────────────────────────────────────────────────
    public string Category { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    public decimal CategoryRatePercent { get; set; }
    public string CategoryCompetition { get; set; } = "";
    /// <summary>matched | default | seller — where the category rate came from.</summary>
    public string CategoryBasis { get; set; } = "default";

    // ── Now, versus what it should be ────────────────────────────────────────────────────────
    public decimal CurrentRatePercent { get; set; }
    public decimal AdFeeAtCurrent { get; set; }
    public decimal? NetPerSaleAtCurrent { get; set; }
    public decimal? BreakEvenLiftAtCurrentPercent { get; set; }

    public decimal? RecommendedRatePercent { get; set; }
    public decimal? AdFeeAtRecommended { get; set; }
    public decimal? NetPerSaleAtRecommended { get; set; }
    public decimal? BreakEvenLiftAtRecommendedPercent { get; set; }
    public decimal? ModeledLiftAtRecommendedPercent { get; set; }

    /// <summary>
    /// The rate at which the ad fee eats the entire profit on the sale. Not a target — a wall.
    /// </summary>
    public decimal? MaxSustainableRatePercent { get; set; }

    public decimal? NetPer100AtCurrent { get; set; }
    public decimal? NetPer100AtRecommended { get; set; }
    /// <summary>What moving from the current rate to the recommended one is worth, per 100 organic sales.</summary>
    public decimal? NetGainPer100 { get; set; }
    /// <summary>Change in the ad bill per sale — negative means the recommendation spends less.</summary>
    public decimal AdFeeChangePerSale { get; set; }

    // ── The evidence behind it ───────────────────────────────────────────────────────────────
    public int? DaysListed { get; set; }
    public int WatchCount { get; set; }
    public int QuantitySold { get; set; }
    public decimal? SalesPerMonth { get; set; }
    public int SoldCompCount { get; set; }
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";
    public decimal? MarketPrice { get; set; }
    public decimal? PriceGapPercent { get; set; }
    public bool MarketComparable { get; set; } = true;
    /// <summary>proven (this listing has sold) | market (comps exist) | thin (neither).</summary>
    public string EvidenceLevel { get; set; } = "thin";

    /// <summary>Expected extra take-home per month — only when this listing's own sales history says how many it moves.</summary>
    public decimal? ExtraProfitPerMonth { get; set; }
    public decimal? AdSpendPerMonthAtRecommended { get; set; }
    public decimal? AdSpendPerMonthAtCurrent { get; set; }

    public string Verdict { get; set; } = "no_data";
    public string Headline { get; set; } = "";
    public string Note { get; set; } = "";
    public List<string> Signals { get; set; } = [];

    public PromotedAssumptions Assumptions { get; set; } = new();
    public List<AdRatePoint> Ladder { get; set; } = [];

    /// <summary>
    /// The gain is big enough to be worth the trip to Seller Hub. A rate that is optimal by a dime
    /// per hundred sales is not a task; it is rounding.
    /// </summary>
    public bool ChangeWorthMaking { get; set; }

    public bool HasRecommendation => RecommendedRatePercent.HasValue;
    /// <summary>A rate change worth acting on, as opposed to noise inside the model's precision.</summary>
    public bool NeedsChange =>
        ChangeWorthMaking && RecommendedRatePercent is decimal r && Math.Abs(r - CurrentRatePercent) >= 1m;
}

/// <summary>What the whole board is doing with its ad budget.</summary>
public sealed class PromotedBoardSummary
{
    public int ListingsAnalyzed { get; set; }
    public int WithCostBasis { get; set; }
    public int Advised { get; set; }

    public int UnderPromoted { get; set; }
    public int OverPromoted { get; set; }
    public int OnTarget { get; set; }
    /// <summary>Listings where the margin cannot carry an ad rate at all.</summary>
    public int ShouldNotPromote { get; set; }

    /// <summary>
    /// Ad fees on one sale of every listing at the current rate. The "one of each" basis is used
    /// rather than a projected monthly bill, because eBay reports no per-listing sales rate and a
    /// fabricated volume would put a made-up dollar sign on the headline.
    /// </summary>
    public decimal AdFeePerRoundAtCurrent { get; set; }
    public decimal AdFeePerRoundAtRecommended { get; set; }
    /// <summary>Ad money currently being spent past the point it can earn back — one sale of each.</summary>
    public decimal OverspendPerRound { get; set; }

    /// <summary>Take-home gained by moving every listing to its recommended rate, per 100 sales of each.</summary>
    public decimal NetGainPer100 { get; set; }
    /// <summary>Revenue-weighted recommended rate — the single number to set as the app-wide default.</summary>
    public decimal? BlendedRecommendedPercent { get; set; }

    /// <summary>Monthly figures, over the listings whose own sales history supports one.</summary>
    public int WithSalesHistory { get; set; }
    public decimal ExtraProfitPerMonth { get; set; }
}

public sealed class PromotedBoardResult
{
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }
    public int ActiveListings { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public string? DataWarning { get; set; }
    /// <summary>The ad rate the app currently assumes on every net figure (Settings → Fees &amp; Costs).</summary>
    public decimal DefaultRatePercent { get; set; }
    /// <summary>
    /// The rate every row was judged against — the seller's own answer to "what do you run today",
    /// since eBay exposes no API for a listing's live ad rate. Reported so the board can say what it
    /// compared against instead of leaving the reader to assume.
    /// </summary>
    public decimal ComparedRatePercent { get; set; }
    public PromotedBoardSummary Summary { get; set; } = new();
    public List<PromotedAdvice> Items { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>One listing's economics, straight from the editor — no eBay account needed.</summary>
public sealed class PromotedAdviceRequest
{
    public string Title { get; set; } = "";
    /// <summary>
    /// The category NAME, not the id — see <see cref="Services.PromotedRateNorms.Resolve"/> for why
    /// a leaf category id is the wrong key for a figure that is only accurate to a percentage point.
    /// </summary>
    public string Category { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    public decimal? ShippingCost { get; set; }
    /// <summary>Null falls back to the ad rate configured in Fees &amp; Costs.</summary>
    public decimal? CurrentRatePercent { get; set; }
    /// <summary>The seller's own read of what their category pays, overriding the published norm.</summary>
    public decimal? CategoryRatePercent { get; set; }
    public decimal? SalesPerMonth { get; set; }
    public int? DaysListed { get; set; }
    public int WatchCount { get; set; }
    public int QuantitySold { get; set; }
    public int SoldCompCount { get; set; }
    public decimal? MarketPrice { get; set; }
}
