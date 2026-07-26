namespace ING_eBay_AutoLister.Models;

// ── Facebook Marketplace local sourcing ───────────────────────────────────────
// The other half of the cross-listing story: CrossListingExporter pushes finished eBay drafts
// OUT to Facebook, this reads local Marketplace supply back IN so the seller can find inventory
// near them that's worth flipping. Facebook has no public search API, so this rides the same
// saved-browser-session pattern as Terapeak. See Services/FacebookMarketplaceService.cs.
//
// The listing/result types that used to live here are now the shared LocalSupplyListing /
// LocalSupplySearchResult (Models/LocalSupplyModels.cs) — a Facebook tile and a Craigslist post
// are the same thing to everything downstream, and the ranked table shows both. What stays here
// is the one type that is genuinely Facebook-shaped: the raw scraped tile.

// One result-grid tile exactly as the browser saw it — the href, the thumbnail and the visible
// text lines, with no interpretation applied. The DOM shape is the part Facebook changes; keeping
// the raw form separate means the meaning-extraction in FacebookMarketplaceParser is plain C#
// that can be unit-tested without a browser.
public class FacebookRawCard
{
    public string Href { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public List<string> Lines { get; set; } = [];
}
