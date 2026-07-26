namespace ING_eBay_AutoLister.Models;

// ── Facebook Marketplace local sourcing ───────────────────────────────────────
// The other half of the cross-listing story: CrossListingExporter pushes finished
// eBay drafts OUT to Facebook, this reads local Marketplace supply back IN so the
// seller can find inventory near them that's worth flipping. Facebook has no public
// search API, so this rides the same saved-browser-session pattern as Terapeak.
// See Services/FacebookMarketplaceService.cs.

// One result-grid tile exactly as the browser saw it — the href, the thumbnail and
// the visible text lines, with no interpretation applied. The DOM shape is the part
// Facebook changes; keeping the raw form separate means the meaning-extraction below
// is plain C# that can be unit-tested without a browser.
public class FacebookRawCard
{
    public string Href { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}

public class FacebookMarketplaceListing
{
    // Facebook's own item id, parsed out of /marketplace/item/<id>/ — the only stable
    // identity a tile carries (titles and prices both change while a listing is live).
    public string ItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    // Null when the tile showed something unparseable; IsFree covers the "Free" tiles,
    // which are a real and common case rather than a $0 price.
    public decimal? Price { get; set; }
    public string PriceText { get; set; } = "";
    public bool IsFree { get; set; }
    // Facebook strikes through the old price when a seller drops it — a price drop is a
    // motivated seller, which is exactly what a sourcing search is looking for.
    public decimal? OriginalPrice { get; set; }

    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    // "Just listed", "3 hours ago" — only some tiles carry it, so this is often empty.
    public string PostedAgo { get; set; } = "";
}

public class FacebookMarketplaceSearchResult
{
    // ok | not_connected | session_expired | error
    public string Status { get; set; } = "";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }
    public string SearchUrl { get; set; } = "";

    public List<FacebookMarketplaceListing> Items { get; set; } = [];
    public int Count => Items.Count;

    // Ask-price spread across the local results — what the item is going for near the
    // seller right now, which is the number to compare against an eBay sold comp.
    public decimal? Min { get; set; }
    public decimal? Median { get; set; }
    public decimal? Max { get; set; }

    public string? Error { get; set; }
}
