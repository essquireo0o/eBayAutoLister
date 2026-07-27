using System.Text.Json.Serialization;

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

    // ── Liquidation supply (closeout / GOB auctions) ──────────────────────────
    // Null on every other source, which is the point: a Craigslist row is not an auction and does
    // not carry a dozen blank auction columns to say so. Non-null means Price above is a BID rather
    // than an asking price, that a buyer's premium and sales tax sit on top of it, and that the row
    // may be several units of one product rather than one thing — see Models/LiquidationModels.cs.
    public LiquidationLotDetails? Liquidation { get; set; }

    // ── Free / free-after-coupon supply ───────────────────────────────────────
    // Null on every other source. Non-null means the cost basis is zero or nearly so — and that the
    // row carries a deadline, because free things are claimed in hours and coupon prices are pulled
    // without notice. What "free" actually costs (a rebate's sales tax, its wait, its risk of never
    // being paid) is FreebiePricer's answer, not this field's. See Models/FreebieModels.cs.
    public FreebieDetails? Freebie { get; set; }

    // ── Used, but still covered ───────────────────────────────────────────────
    // Null on every row whose listing said nothing about warranty — which is most of them. Non-null
    // means the item carries (or explicitly lacks) cover, and that cover is worth money on the
    // resale and risk on the buy. Detected centrally in LocalArbitrageAnalyzer rather than per
    // source, so every board gets it at once. See Models/WarrantyModels.cs.
    public WarrantyDetails? Warranty { get; set; }

    // The source's own body text, where it published one — craigslist's RSS description, a deal
    // feed's content:encoded. Truncated at WarrantySelectors.MaxDetailChars, because everything
    // that reads it wants the opening paragraph: the condition, the purchase date and the warranty
    // wording live there, and past it are shipping boilerplate and forum chatter.
    //
    // Empty on the sources that publish tiles rather than posts (Facebook), which is the honest
    // answer rather than a gap: a tile has no body to read.
    //
    // Not serialised: it is read on the way through the pipeline and nothing in the browser renders
    // it, and three hundred posts' worth of body text is a few hundred kilobytes of response that
    // would slow down every board carrying it for no one's benefit.
    [JsonIgnore]
    public string DetailText { get; set; } = "";

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
    // Whether buying from this source puts sales tax on the bill. True for the retail deal feeds
    // and for liquidation auctions; false for a cash pickup off a stranger. Drives whether the
    // sales-tax field is worth showing — it used to be inferred from LocationBased, which was only
    // ever right by coincidence: an auction is local AND taxed.
    public bool ChargesSalesTax { get; set; }
    // Sites worth searching that refuse to be read by a program — liquidation.com and B-Stock both
    // answer an automated request with a block page. Rather than pretend the source covers them or
    // silently drop them, the picker offers a prefilled link the seller can open themselves.
    public List<LocalSupplyManualSite> ManualSites { get; set; } = [];
    // A radius this source won't search below, or 0 when it honours the form exactly. Stops the
    // panel promising "within 40 miles" for a source that searched 250 — see
    // ILocalSupplySource.MinRadiusMiles.
    public int MinRadiusMiles { get; set; }
    // Whether searching this source with no keyword at all is meaningful. True only for the freebie
    // board, where "everything being given away near me" is the best search there is; on every other
    // source a blank query is the whole classifieds section. Drives whether the panel lets the
    // seller press the button with the keyword box empty.
    public bool AllowsBlankQuery { get; set; }
}

// A site this app deliberately does not scrape, offered as a one-click search instead. UrlTemplate
// carries a {query} placeholder the browser fills in.
public class LocalSupplyManualSite
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string UrlTemplate { get; set; } = "";
    public string Note { get; set; } = "";
}
