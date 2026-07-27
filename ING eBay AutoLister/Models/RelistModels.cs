namespace ING_eBay_AutoLister.Models;

// One eBay listing that ended without selling.
//
// This is the only inventory in the app that is invisible everywhere else: it is not in the
// active-listings import (it ended), it is not in Money Made (it never sold), and eBay's own
// Unsold page shows a title, a date and a Relist button with no price, no market and no cost
// attached. It is also the cheapest money in the business to recover — the photos are taken, the
// description is written, and the item is on a shelf being paid for either way.
//
// Fields eBay does not reliably return on the unsold list (HitCount on some accounts, WatchCount
// on others) are read opportunistically and reported as unknown rather than as zero: "nobody
// watched it" and "eBay didn't say" lead to opposite recommendations.
public sealed class EbayEndedListing
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Condition { get; set; } = "";
    public string Category { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public string ListingUrl { get; set; } = "";

    // Chinese = auction, FixedPriceItem / StoresFixedPrice = Buy It Now. The two take different
    // relist calls and only one of them can carry a Second Chance Offer.
    public string ListingType { get; set; } = "";
    public bool IsAuction => ListingType.Contains("Chinese", StringComparison.OrdinalIgnoreCase)
                          || ListingType.Contains("Auction", StringComparison.OrdinalIgnoreCase);

    // What it was asking when it ended. For an auction this is the opening price, not the top bid.
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public int QuantityUnsold { get; set; } = 1;

    public DateTime? StartTimeUtc { get; set; }
    public DateTime? EndTimeUtc { get; set; }

    // The evidence the listing collected while it was live. Null means eBay did not report the
    // figure on this account — never conflated with zero.
    public int? WatchCount { get; set; }
    public int? HitCount { get; set; }
    public int BidCount { get; set; }
    // The highest bid an auction actually attracted: a real dollar figure a real buyer committed
    // to, which is worth more as evidence than any comp.
    public decimal? HighBid { get; set; }

    // eBay's own record that this one has already been relisted. Non-empty means relisting again
    // would put a duplicate on the site.
    public string RelistedItemId { get; set; } = "";
    // Ended early by the seller rather than run to term. A listing pulled on purpose is not a
    // listing that failed, so it is never counted as a lost sale.
    public bool EndedByUser { get; set; }
}

// One ended listing, judged: whether to put it back up, at what price, and what that is worth.
//
// Every money field is nullable where it rests on data that may not exist. A listing with no sold
// comps has no market price and a listing with no recorded cost has no break-even — reported as
// unknown, because a zero floor is how a relist tool recommends re-listing at a loss.
public sealed class RelistCandidate
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Condition { get; set; } = "";
    public bool IsAuction { get; set; }

    // ── How it ended ─────────────────────────────────────────────────────────────────────────
    public decimal EndPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public DateTime? EndTimeUtc { get; set; }
    public int? DaysSinceEnded { get; set; }
    public int? DaysListed { get; set; }
    public int? WatchCount { get; set; }
    public int? HitCount { get; set; }
    public int BidCount { get; set; }
    public decimal? HighBid { get; set; }
    public bool AlreadyRelisted { get; set; }
    public string RelistedItemId { get; set; } = "";
    public bool EndedByUser { get; set; }

    // ── What the market says, from the same pricing stack every other screen uses ────────────
    public decimal? MarketPrice { get; set; }
    public decimal? QuickSalePrice { get; set; }
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public string PricedAs { get; set; } = "";
    public decimal? PriceGapPercent { get; set; }
    public int LotQuantity { get; set; } = 1;
    // False when the market figure is not a like-for-like comparison with the asking price. When
    // it is false, nothing about the market is allowed to move the relist price.
    public bool MarketComparable { get; set; } = true;

    // ── The floor the relist price is not allowed through ────────────────────────────────────
    public decimal? CostBasis { get; set; }
    public bool HasCostBasis => CostBasis.HasValue;
    public decimal? BreakEvenPrice { get; set; }
    public decimal? FloorPrice { get; set; }
    public string FloorBasis { get; set; } = "none";

    // ── The recommendation ───────────────────────────────────────────────────────────────────
    public decimal? RelistPrice { get; set; }
    public decimal? RelistChangePercent { get; set; }
    public decimal? NetProfitAtRelist { get; set; }
    public decimal? NetProfitAtEndPrice { get; set; }
    // True when the ladder wanted to cut deeper and the floor stopped it. Shown, never hidden.
    public bool FloorLimited { get; set; }
    // True when the honest answer is "the price was never the problem" — relisting at the same
    // number is still right, because the relist itself buys a fresh run in search.
    public bool SamePrice { get; set; }
    // Which rung of the ladder produced the price. Kept on the row so the verdict can be recomputed
    // after the bidder lookups come back without re-running the whole price decision.
    public string PriceReason { get; set; } = "";

    // ── Second Chance ────────────────────────────────────────────────────────────────────────
    // Populated only for auctions that attracted bids. These are people who already named a price
    // in public — the shortest distance in this whole app between a click and money.
    public List<SecondChanceBidder> Bidders { get; set; } = [];
    public bool BiddersChecked { get; set; }
    public string BidderNote { get; set; } = "";
    public int SendableBidders => Bidders.Count(b => b.CanSend);
    public decimal SecondChanceValue => Math.Round(Bidders.Where(b => b.CanSend).Sum(b => b.OfferPrice), 2);

    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";
    public List<string> Signals { get; set; } = [];

    // The gate every bulk action honours. Anything eBay has already relisted, or that has no
    // profitable price, is never in a select-all.
    public bool CanRelist => RelistPrice is > 0m && !AlreadyRelisted && !string.IsNullOrEmpty(ListingId)
                             && Verdict is "relist" or "relist_cheaper" or "relist_as_is" or "second_chance";
}

// One underbidder on an ended auction, and the Second Chance Offer worth sending them.
public sealed class SecondChanceBidder
{
    public string UserId { get; set; } = "";
    public decimal? MaxBid { get; set; }
    public int Quantity { get; set; } = 1;
    // What the offer would be priced at. eBay will not carry a Second Chance Offer above what the
    // bidder already bid, so this is their own number unless the floor raises it — and if the
    // floor raises it above their bid, there is no offer to send.
    public decimal OfferPrice { get; set; }
    public decimal? NetProfitAtOffer { get; set; }
    public string Status { get; set; } = "ready";   // ready | below_floor | anonymous | no_bid
    public string Note { get; set; } = "";
    public bool CanSend => Status == "ready" && OfferPrice > 0m && !string.IsNullOrWhiteSpace(UserId);
}

// The board-level view: how much of what already almost sold is still recoverable.
public sealed class RelistSummary
{
    public int EndedListings { get; set; }
    public int Analyzed { get; set; }
    // What the ended listings were asking, in total. This is the size of the pile, not a forecast.
    public decimal AskedAndUnsold { get; set; }
    // Cost basis actually sunk in the unsold units — the only figure here that is money already
    // gone rather than money not yet made.
    public decimal CashSunk { get; set; }
    public int WithCostBasis { get; set; }

    public int ReadyToRelist { get; set; }
    // Gross and net if every recommended relist sells this time. Conditional, and labelled that
    // way everywhere it is shown: a relist is a second run at the sale, not the sale.
    public decimal RelistValue { get; set; }
    public decimal NetIfAllSell { get; set; }
    // What the price cuts cost against the prices that already failed. Shown because a recovered
    // sale at a lower number is still a smaller number.
    public decimal PriceGivenUp { get; set; }

    public int SecondChanceListings { get; set; }
    public int SecondChanceBidders { get; set; }
    public decimal SecondChanceValue { get; set; }
    public decimal SecondChanceNet { get; set; }

    public int AlreadyRelisted { get; set; }
    public int Underwater { get; set; }
    public int NeedsWork { get; set; }
    public int NoData { get; set; }
}

public sealed class RelistRecoveryResult
{
    public string Status { get; set; } = "ok";           // ok | ebay_unavailable
    public string? Error { get; set; }
    public int LookbackDays { get; set; }
    public int ProductsPriced { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public int BidderLookups { get; set; }
    public string? DataWarning { get; set; }
    public decimal MinNetProfit { get; set; }
    public string DefaultSellerMessage { get; set; } = "";
    public RelistSummary Summary { get; set; } = new();
    public List<RelistCandidate> Items { get; set; } = [];
    public DateTimeOffset GeneratedUtc { get; set; } = DateTimeOffset.UtcNow;
}

// ── Relisting ────────────────────────────────────────────────────────────────────────────────

public sealed class RelistItemRequest
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal NewPrice { get; set; }
    public decimal EndPrice { get; set; }
    public int Quantity { get; set; } = 1;
    public bool IsAuction { get; set; }
}

public sealed class RelistRequest
{
    public List<RelistItemRequest> Items { get; set; } = [];
    // Nothing reaches eBay unless both of these say so. The default is a preview.
    public bool Confirmed { get; set; }
    public bool DryRun { get; set; } = true;
    public decimal MinNetProfit { get; set; }
    // Opt-in override for the floor refusal, on the record in the action log.
    public bool AllowBelowFloor { get; set; }
}

public sealed class RelistItemResult
{
    public string ListingId { get; set; } = "";
    public string NewListingId { get; set; } = "";
    public string Title { get; set; } = "";
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public decimal ChangePercent { get; set; }
    // What eBay charged to put it back up, when eBay says. A recovered sale that costs $0.35 of
    // insertion fee is still a recovered sale, but the seller gets to see the $0.35.
    public decimal? InsertionFee { get; set; }
    public string Status { get; set; } = "pending";   // relisted | preview | skipped | failed
    public string Message { get; set; } = "";
}

public sealed class RelistResult
{
    public bool DryRun { get; set; } = true;
    public int Requested { get; set; }
    public int Relisted { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public decimal ListedValue { get; set; }
    public decimal TotalFees { get; set; }
    public List<RelistItemResult> Items { get; set; } = [];
}

// ── Second Chance Offers ─────────────────────────────────────────────────────────────────────

public sealed class SecondChanceItemRequest
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string BidderUserId { get; set; } = "";
    public decimal OfferPrice { get; set; }
}

public sealed class SecondChanceRequest
{
    public List<SecondChanceItemRequest> Items { get; set; } = [];
    public string Message { get; set; } = "";
    // How long the buyer has. eBay carries 1, 3, 5 or 7 days.
    public int DurationDays { get; set; } = 3;
    public bool Confirmed { get; set; }
    public bool DryRun { get; set; } = true;
    public decimal MinNetProfit { get; set; }
    public bool AllowBelowFloor { get; set; }
}

public sealed class SecondChanceResultItem
{
    public string ListingId { get; set; } = "";
    public string Title { get; set; } = "";
    public string BidderUserId { get; set; } = "";
    public decimal OfferPrice { get; set; }
    public string OfferItemId { get; set; } = "";
    public string Status { get; set; } = "pending";   // sent | preview | skipped | failed
    public string Message { get; set; } = "";
}

public sealed class SecondChanceResult
{
    public bool DryRun { get; set; } = true;
    public int Requested { get; set; }
    public int Sent { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public decimal OfferedValue { get; set; }
    public List<SecondChanceResultItem> Items { get; set; } = [];
}
