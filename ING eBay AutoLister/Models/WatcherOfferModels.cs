namespace ING_eBay_AutoLister.Models;

// One live listing that has people watching it, and the offer worth sending them.
//
// A watcher is the warmest audience a seller ever gets for free: someone who found the item,
// looked at it, and told eBay to remember it. eBay's Send Offer to Interested Buyers puts a
// private, time-limited discount in front of exactly those people — and unlike a markdown, it
// costs nothing at all if nobody accepts, because the public price never moves.
//
// Every money field is nullable where it depends on data that may not exist. A listing with no
// recorded cost basis has no break-even, and an offer discount computed off a zero floor is how a
// seller sends a private invitation to buy at a loss.
public sealed class WatcherOfferItem
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Condition { get; set; } = "";

    // ── The listing as it stands ─────────────────────────────────────────────────────────────
    public decimal ListPrice { get; set; }
    public int Quantity { get; set; }
    public int WatchCount { get; set; }
    public int? DaysListed { get; set; }

    // ── What the market says, borrowed wholesale from the inventory-health scan ───────────────
    public decimal? MarketPrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public string PricedAs { get; set; } = "";
    // False when the market figure isn't a like-for-like comparison (a lot listing against
    // per-unit comps, or a gap so wide it's a matching failure). Nothing about the market is
    // allowed to move the offer when this is false.
    public bool MarketComparable { get; set; } = true;

    // ── The floor the offer is not allowed through ───────────────────────────────────────────
    public decimal? CostBasis { get; set; }
    public bool HasCostBasis => CostBasis.HasValue;
    public decimal? BreakEvenPrice { get; set; }
    // The binding floor: break-even plus the seller's minimum profit, or the quick-sale price
    // when that is higher — whichever actually stops the discount first.
    public decimal? FloorPrice { get; set; }
    public string FloorBasis { get; set; } = "none";   // profit | break_even | quick_sale | none

    // ── eBay's own eligibility answer ────────────────────────────────────────────────────────
    // null means eBay's eligibility list couldn't be read this scan — reported as unknown rather
    // than assumed either way.
    public bool? Eligible { get; set; }
    public string EligibilityNote { get; set; } = "";

    // ── The offer ────────────────────────────────────────────────────────────────────────────
    public int? DiscountPercent { get; set; }
    public decimal? OfferPrice { get; set; }
    public decimal? NetProfitAtListPrice { get; set; }
    public decimal? NetProfitAtOffer { get; set; }
    // What accepting the offer costs the seller versus a sale at the asking price. Shown because
    // an offer is margin spent deliberately, and the seller should see the size of it.
    public decimal? MarginGivenUp { get; set; }
    // True when the ladder wanted to go deeper and the floor stopped it. Shown, not hidden.
    public bool FloorLimited { get; set; }

    public string Verdict { get; set; } = "no_watchers";
    public string VerdictNote { get; set; } = "";
    public List<string> Signals { get; set; } = [];

    public bool CanSend => Verdict == "ready" && DiscountPercent.HasValue && !string.IsNullOrEmpty(ListingId);
}

// The board-level view: how much warm demand is sitting unconverted right now.
public sealed class WatcherOfferSummary
{
    public int ListingsAnalyzed { get; set; }
    public int ListingsWithWatchers { get; set; }
    public int TotalWatchers { get; set; }

    public int ReadyToSend { get; set; }
    // Watchers on the rows that are actually ready — the size of the audience one click reaches.
    public int WatchersReachable { get; set; }
    public decimal AverageDiscountPercent { get; set; }
    // Take-home if ONE unit of every ready listing were bought at its offer price. Conditional on
    // acceptance, and labelled that way everywhere it is shown — a watcher is not a buyer yet.
    public decimal NetIfOneEachAccepts { get; set; }
    public decimal RevenueIfOneEachAccepts { get; set; }
    // What that costs versus the same units selling at the asking price.
    public decimal MarginGivenUpIfAllAccept { get; set; }

    public int BlockedByFloor { get; set; }
    public int NotEligible { get; set; }
    public int WithoutCostBasis { get; set; }
}

public sealed class WatcherOfferResult
{
    public string Status { get; set; } = "ok";           // ok | ebay_unavailable
    public string? Error { get; set; }
    public int ActiveListings { get; set; }
    public int ItemsAnalyzed { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    // False when eBay's eligibility list couldn't be read — every row then carries an unknown
    // eligibility rather than a guess.
    public bool EligibilityChecked { get; set; }
    public string EligibilityNote { get; set; } = "";
    // True when the saved eBay connection predates the offers permission. The board still renders
    // — reading watchers needs nothing new — but sending needs a reconnect.
    public bool NeedsReconnect { get; set; }
    public string? DataWarning { get; set; }
    public decimal MinNetProfit { get; set; }
    public string DefaultMessage { get; set; } = "";
    public WatcherOfferSummary Summary { get; set; } = new();
    public List<WatcherOfferItem> Items { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ── Sending ──────────────────────────────────────────────────────────────────────────────────

public sealed class SendWatcherOfferItem
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal ListPrice { get; set; }
    public int DiscountPercent { get; set; }
    public int WatchCount { get; set; }
    public int Quantity { get; set; } = 1;
}

public sealed class SendWatcherOffersRequest
{
    public List<SendWatcherOfferItem> Items { get; set; } = [];
    public string Message { get; set; } = "";
    // A counter-offer is another chance at the sale, so this defaults on.
    public bool AllowCounterOffer { get; set; } = true;
    // Nothing reaches eBay unless both of these say so. The default is a preview.
    public bool Confirmed { get; set; }
    public bool DryRun { get; set; } = true;
    // The profit the seller wants left after fees, on top of break-even.
    public decimal MinNetProfit { get; set; }
    // Opt-in override for the floor refusal, so a seller deliberately clearing dead stock can,
    // on the record in the action log.
    public bool AllowBelowFloor { get; set; }
}

public sealed class SendWatcherOfferResultItem
{
    public string ListingId { get; set; } = "";
    public string Title { get; set; } = "";
    public int DiscountPercent { get; set; }
    public decimal ListPrice { get; set; }
    public decimal OfferPrice { get; set; }
    public int WatchCount { get; set; }
    public string Status { get; set; } = "pending";   // sent | preview | skipped | failed
    public string Message { get; set; } = "";
    public string OfferId { get; set; } = "";
}

public sealed class SendWatcherOffersResult
{
    public bool DryRun { get; set; } = true;
    public int Requested { get; set; }
    public int Sent { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public int WatchersReached { get; set; }
    public List<SendWatcherOfferResultItem> Items { get; set; } = [];
}
