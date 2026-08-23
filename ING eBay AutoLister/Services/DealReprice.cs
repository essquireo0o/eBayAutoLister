using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// The single-row echo of FindLocalArbitrageAsync's pricing half. The deals board prices every row
// against whatever sold comps were in the database when the scan ran; when a seller (or the auto
// top-3 pass) fires a live OpenWebNinja lookup for one product, the fresh sold rows land in that
// same database and one row can be re-costed without re-running the whole scan.
//
// Everything here rebuilds the exact LocalSupplyListing the row came from and classifies it the
// way the scan does (ResaleCategoryCatalog), so the endpoint can hand it to the same
// LocalArbitrageAnalyzer.Build — a repriced row is costed by exactly the rules the scan used,
// never a second way. Pure and side-effect-free, which is what makes the reprice endpoint testable
// without a live comps database in front of it.
public static class DealReprice
{
    /// <summary>
    /// Reconstructs the local classified/supply listing a board row came from, and classifies it in
    /// place the same way the scan did — so valuation and fees match FindLocalArbitrageAsync.
    /// </summary>
    public static LocalSupplyListing ToListing(RepriceRowRequest req)
    {
        var listing = new LocalSupplyListing
        {
            Source = req.Source ?? "",
            SourceLabel = req.SourceLabel ?? "",
            ItemId = req.ItemId ?? "",
            Title = req.Title ?? "",
            Url = req.Url ?? "",
            ImageUrl = req.ImageUrl ?? "",
            // A free row keeps a real zero cost basis rather than a missing price — IsFree is the
            // difference between "cost nothing" and "we couldn't read a price".
            Price = req.IsFree ? null : req.Price,
            IsFree = req.IsFree,
            OriginalPrice = req.OriginalPrice,
            Location = req.Location ?? "",
            DistanceMiles = req.DistanceMiles,
            SellerUsername = req.SellerUsername ?? "",
            SellerFeedbackScore = req.SellerFeedbackScore,
            SellerFeedbackPercent = req.SellerFeedbackPercent,
            PostedAgo = req.PostedAgo ?? "",
            PostedUtc = req.PostedUtc,
            IsRetail = req.IsRetail,
            Retailer = req.Retailer ?? "",
            FreeShipping = req.FreeShipping,
            CouponCode = req.CouponCode ?? "",
            // Pre-stamped so Classify keeps the scan's own answer: a source that already knew what
            // this row is wins over anything a title parser could re-derive here.
            CategoryId = req.CategoryId ?? "",
        };

        ResaleCategoryCatalog.Classify(listing, ResaleCategoryCatalog.Resolve(req.CategoryId));
        return listing;
    }

    /// <summary>
    /// The title the comp lookup should run against: the browser's own query when it sent one, else
    /// the fullest wording the row carried (PricedAs), else the row's own title.
    /// </summary>
    public static string LookupQueryFor(RepriceRowRequest req) =>
        !string.IsNullOrWhiteSpace(req.Query) ? req.Query!.Trim()
        : !string.IsNullOrWhiteSpace(req.PricedAs) ? req.PricedAs!.Trim()
        : (req.Title ?? "").Trim();
}
