namespace ING_eBay_AutoLister.Models;

// ── Price Position (see Services/PricePositionAnalyzer.cs) ──────────────────────────────────
//
// Every other pricing surface in this app is built on SOLD comps — what buyers paid. This one is
// built on LIVE ones: the listings a buyer actually sees next to the seller's when they search.
// The two answer different questions, and only the second one explains why a fairly-priced listing
// has sat for ninety days behind eight cheaper copies of itself.

/// <summary>One live listing competing with the seller's, as the buyer sees it.</summary>
public sealed class PriceRival
{
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string SellerUsername { get; set; } = "";
    public string Condition { get; set; } = "";
    public int SellerFeedbackScore { get; set; }

    public decimal Price { get; set; }
    public decimal ShippingCost { get; set; }
    public bool ShippingStated { get; set; }

    /// <summary>Item price plus shipping — what the buyer pays, and what eBay sorts on.</summary>
    public decimal? DeliveredPrice { get; set; }

    /// <summary>Whether this one is inside the ranking, or only shown for context.</summary>
    public bool Counted { get; set; }

    /// <summary>Why it is only context. Null on a counted rival.</summary>
    public string? SkipReason { get; set; }
}

/// <summary>Where one of the seller's live listings sits among the listings it competes with.</summary>
public sealed class PricePositionRow
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string ListingUrl { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string SearchQuery { get; set; } = "";

    public decimal MyPrice { get; set; }
    public decimal MyShipping { get; set; }
    public bool MyShippingKnown { get; set; }
    /// <summary>What a buyer pays for the seller's listing, on whichever basis this row uses.</summary>
    public decimal MyComparedPrice { get; set; }

    /// <summary><c>delivered</c> or <c>item_price</c>. See <c>PricePositionAnalyzer.Basis</c>.</summary>
    public string Basis { get; set; } = "";

    public int Quantity { get; set; } = 1;
    public int WatchCount { get; set; }
    public int ViewCount { get; set; }
    /// <summary>False when eBay reported no view count at all — which is not the same as zero.</summary>
    public bool ViewsKnown { get; set; }
    public int? DaysListed { get; set; }

    public int RivalsFound { get; set; }
    public int RivalsCounted { get; set; }

    /// <summary>1 = cheapest on the shelf. Null when there was no market to place them in.</summary>
    public int? Rank { get; set; }
    public decimal? CheapestRival { get; set; }
    public decimal? MedianRival { get; set; }
    /// <summary>The cheapest rival this board is willing to price against — see the outlier rule.</summary>
    public decimal? TargetRival { get; set; }
    public bool TargetSkippedAnOutlier { get; set; }

    /// <summary>How far over the target the seller is, as a percent of it.</summary>
    public decimal? PremiumPercent { get; set; }

    /// <summary>The compared price that would put this listing first.</summary>
    public decimal? PriceToLead { get; set; }
    /// <summary>The same number as an asking price — what the seller types into the price box.</summary>
    public decimal? ItemPriceToLead { get; set; }
    public decimal? NetProfitAtLeadPrice { get; set; }

    public bool HasCostBasis { get; set; }
    public decimal? FloorPrice { get; set; }
    public string FloorBasis { get; set; } = "";
    public decimal? NetProfitNow { get; set; }

    /// <summary>leading | competitive | priced_out | cant_win | thin_market | alone | lookup_failed</summary>
    public string Verdict { get; set; } = "";

    /// <summary>price | visibility | supply | none — the thing actually standing between this listing and a sale.</summary>
    public string Blocker { get; set; } = "none";

    public string Headline { get; set; } = "";
    public List<string> Cautions { get; set; } = [];
    public List<PriceRival> Rivals { get; set; } = [];

    /// <summary>Capital this row is judging — asking price times units held.</summary>
    public decimal CapitalListed { get; set; }
}

public sealed class PricePositionSummary
{
    public int Rows { get; set; }
    public int PricedOut { get; set; }
    public int Leading { get; set; }
    public int CantWin { get; set; }
    public int VisibilityBlocked { get; set; }
    public int Alone { get; set; }

    /// <summary>Asking-price value of the listings sitting behind cheaper copies of themselves.</summary>
    public decimal CapitalBehindTheShelf { get; set; }
    /// <summary>What the seller would still take home if every priced-out row moved to the front.</summary>
    public decimal? ProfitStillOnTheTable { get; set; }
    /// <summary>How many priced-out rows could not be costed, so are not inside the figure above.</summary>
    public int PricedOutWithoutCost { get; set; }

    public decimal? WorstPremiumPercent { get; set; }
    public string WorstPremiumTitle { get; set; } = "";
}

public sealed class PricePositionResult
{
    /// <summary>ok | ebay_unavailable | no_listings</summary>
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }

    public int ActiveListings { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int SearchesUsed { get; set; }
    public int SearchesFailed { get; set; }
    /// <summary>False when not one listing came back with a view count — the board then never blames visibility.</summary>
    public bool ViewsReported { get; set; }

    public List<PricePositionRow> Rows { get; set; } = [];
    public PricePositionSummary Summary { get; set; } = new();

    /// <summary>What this board does not know, in the seller's words, shown under it every load.</summary>
    public List<string> Honesty { get; set; } = [];
}
