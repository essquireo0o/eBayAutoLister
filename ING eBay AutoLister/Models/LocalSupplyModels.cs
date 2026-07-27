namespace ING_eBay_AutoLister.Models;

// ── Local supply, source-agnostic ─────────────────────────────────────────────
// One local classified listing, whichever site it came from. Facebook Marketplace was the
// first source (Services/FacebookMarketplaceService.cs) and Craigslist the second
// (Services/CraigslistService.cs); OfferUp and the rest are the same shape again.
//
// The arbitrage pipeline downstream of this — grouping, comp lookup, ProfitCalculator,
// ranking — knows nothing about any particular site, so a new source is a new
// ILocalSupplySource and nothing else. That's the whole reason this type exists instead of
// each site carrying its own listing class.

public class LocalSupplyListing
{
    // Which site this came from: craigslist | facebook | ... Carried on every row all the way
    // to the ranked table, because "where do I go buy this" is part of the answer.
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";

    // The site's own id for the post — Facebook's /marketplace/item/<id>, Craigslist's
    // <id>.html. Unique per source, not across sources, so dedupe keys on (Source, ItemId).
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    // Null when the post showed no parseable price; IsFree covers "Free" listings, which are a
    // real and common case rather than a $0 price.
    public decimal? Price { get; set; }
    public string PriceText { get; set; } = "";
    public bool IsFree { get; set; }
    // Set when the seller struck their own price through — a motivated seller, which is exactly
    // what a sourcing search is looking for.
    public decimal? OriginalPrice { get; set; }

    public string Location { get; set; } = "";
    // Facebook prints a distance on the tile; Craigslist filters by distance server-side but
    // never reports one, so this is legitimately null for Craigslist rows. Always null for a
    // retail deal, which has no location at all.
    public double? DistanceMiles { get; set; }

    // ── Retail supply (deal feeds) ────────────────────────────────────────────
    // A clearance item bought from Amazon and a drill bought off a stranger for cash are the same
    // shape to the pipeline, but they do NOT cost the same: retail charges sales tax and doesn't
    // haggle. Rather than a second listing type, the handful of things that actually differ are
    // carried here and read by LocalArbitrageAnalyzer — see RetailBuyCosts for what they change.
    public bool IsRetail { get; set; }
    // Amazon, Walmart, Best Buy — the store, not the aggregator that reported the deal (that's
    // SourceLabel). Mirrored into Location too, because that is the field every board renders.
    public string Retailer { get; set; } = "";
    // Stated on the deal itself. Not folded into any cost: it means no shipping is added to the
    // buy, which is already how an unshipped price is treated.
    public bool FreeShipping { get; set; }
    // The code that has to be typed for the price to be the price. Shown on the row, because a
    // price that only exists with a code is not a price the seller can get without it.
    public string CouponCode { get; set; } = "";
    // "Just listed", "3 hours ago" — free text as the site rendered it.
    public string PostedAgo { get; set; } = "";
    // Craigslist's feed carries a real timestamp; Facebook only ever shows relative text.
    public DateTime? PostedUtc { get; set; }
}

public class LocalSupplySearchResult
{
    public string SourceId { get; set; } = "";
    public string SourceLabel { get; set; } = "";

    // ok | not_connected | session_expired | error
    // Sources that need no login (Craigslist) never return not_connected/session_expired.
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }
    public string SearchUrl { get; set; } = "";
    // What the source actually searched, when that isn't simply "the zip you typed" —
    // Craigslist searches a regional site, so the row says "las vegas craigslist".
    public string ScopeLabel { get; set; } = "";

    public List<LocalSupplyListing> Items { get; set; } = [];
    public int Count => Items.Count;

    // Ask-price spread across the local results — what the item is going for near the seller
    // right now, which is the number to compare against an eBay sold comp.
    public decimal? Min { get; set; }
    public decimal? Median { get; set; }
    public decimal? Max { get; set; }

    public string? Error { get; set; }

    // Whether searching this source again in a minute is likely to work. A rate-limit, a block
    // page and a timeout are all "come back shortly"; a bad zip code or a 404 is not. The UI puts
    // a Retry button on the source's chip only when this is true — offering one for a failure that
    // will repeat identically is worse than offering none.
    public bool Retryable { get; set; }
}

// What one source contributed to a multi-source search: everything except the listings
// themselves, which are merged into a single ranked list.
public class LocalSupplySourceOutcome
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Status { get; set; } = "";
    public int Count { get; set; }
    public string SearchUrl { get; set; } = "";
    public string ScopeLabel { get; set; } = "";
    public string? Error { get; set; }
    public bool Retryable { get; set; }

    public static LocalSupplySourceOutcome From(LocalSupplySearchResult r) => new()
    {
        Id = r.SourceId, Label = r.SourceLabel, Status = r.Status, Count = r.Count,
        SearchUrl = r.SearchUrl, ScopeLabel = r.ScopeLabel, Error = r.Error, Retryable = r.Retryable,
    };
}

// One local search across several sites at once.
public class LocalSupplyMultiResult
{
    // Rolled up from the per-source statuses: ok if ANY source came back with results, so one
    // disconnected site never blanks a search another site answered. See LocalSupplyMerger.
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }

    public List<LocalSupplyListing> Items { get; set; } = [];
    public int Count => Items.Count;
    public List<LocalSupplySourceOutcome> Sources { get; set; } = [];

    public decimal? Min { get; set; }
    public decimal? Median { get; set; }
    public decimal? Max { get; set; }
    public string? Error { get; set; }
}

// One pluggable source as the UI sees it, for the source picker.
public class LocalSupplySourceInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public bool RequiresConnection { get; set; }
    public bool Available { get; set; }
    public string Note { get; set; } = "";
    // False for the online sources, whose results have nothing to do with the zip and radius on
    // the form. The picker uses it to stop promising "within 40 miles of 89101" for a scan that
    // searched the whole country — see ILocalSupplySource.IsLocationBased.
    public bool LocationBased { get; set; } = true;
}
