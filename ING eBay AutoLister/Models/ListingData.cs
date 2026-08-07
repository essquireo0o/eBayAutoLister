using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Models;

public class ListingData
{
    // ── Title & Category ──────────────────────────────────────────
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Category { get; set; } = "";          // human-readable display name
    public string CategoryId { get; set; } = "";
    public string SecondaryCategoryId { get; set; } = "";

    // ── Condition ────────────────────────────────────────────────
    // NEW | LIKE_NEW | USED_EXCELLENT | USED_VERY_GOOD | USED_GOOD | USED_ACCEPTABLE | FOR_PARTS_OR_NOT_WORKING
    public string Condition { get; set; } = "USED_EXCELLENT";
    public string ConditionDescription { get; set; } = "";

    // ── Product Identifiers ──────────────────────────────────────
    public string Brand { get; set; } = "";
    public string Mpn { get; set; } = "";               // manufacturer part number
    public string Upc { get; set; } = "";
    public string Ean { get; set; } = "";
    public string Isbn { get; set; } = "";

    // ── Description ──────────────────────────────────────────────
    public string Description { get; set; } = "";

    // ── Pricing ──────────────────────────────────────────────────
    public decimal Price { get; set; }
    public bool BestOfferEnabled { get; set; } = false;
    public decimal? AutoAcceptPrice { get; set; }
    public decimal? AutoDeclinePrice { get; set; }
    public int Quantity { get; set; } = 1;
    public int? QuantityLimitPerBuyer { get; set; }

    // ── Package / Shipping ───────────────────────────────────────
    // PackageType: LETTER | LARGE_ENVELOPE_OR_FLAT_PACK | PACKAGE_THICK_ENVELOPE | MAILING_BOX | BULKY_GOODS | VERY_LARGE_PACKAGE
    public string PackageType { get; set; } = "PACKAGE_THICK_ENVELOPE";
    public decimal WeightLbs { get; set; }
    public decimal WeightOz { get; set; }
    public decimal PackageLengthIn { get; set; }
    public decimal PackageWidthIn { get; set; }
    public decimal PackageHeightIn { get; set; }
    public int HandlingTimeBusinessDays { get; set; } = 1;
    public string ItemLocationPostalCode { get; set; } = "";
    public string ItemLocationCountry { get; set; } = "US";

    // ── Listing Options ──────────────────────────────────────────
    public bool PrivateListing { get; set; } = false;
    public int CharityDonationPercentage { get; set; } = 0;
    public string CharityId { get; set; } = "";

    // ── Images & Specifics ───────────────────────────────────────
    public List<string> ImageUrls { get; set; } = [];
    public Dictionary<string, string> ItemSpecifics { get; set; } = [];

    // Visual description Claude generates for AI photo generation
    public string VisualDescription { get; set; } = "";

    // "product_photo" = clean product image suitable for img2img
    // "webpage_screenshot" = website/listing screenshot, use txt2img only
    public string ImageType { get; set; } = "webpage_screenshot";
}

public class FetchPhotoUrlRequest { public string Url { get; set; } = ""; }

public class RemoveBgRequest
{
    public string ImageBase64 { get; set; } = "";
    public string MimeType    { get; set; } = "image/jpeg";
}

public class SaveUploadedPhotoRequest
{
    public string ImageBase64 { get; set; } = "";
    public string MimeType    { get; set; } = "image/jpeg";
}

public class AnalyzeRequest
{
    public string ImageBase64 { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
}

public class AnalyzeUrlRequest { public string Url { get; set; } = ""; }

public class QuickFillRequest { public string ItemName { get; set; } = ""; }

/// <summary>One debounced snapshot of a listing being written, so a crash cannot take it.</summary>
public class WorkAutosaveRequest
{
    /// <summary>Stable per-draft id minted by the client and reused for every later save.</summary>
    public string Key { get; set; } = "";

    /// <summary>What to call this draft in the recovery banner — normally its title so far.</summary>
    public string? Label { get; set; }

    /// <summary>The form state, serialised by the client. Opaque to the server on purpose.</summary>
    public string? Payload { get; set; }

    public string? Stage { get; set; }
}

public class WorkDiscardRequest { public string Key { get; set; } = ""; }

public class SoldComp
{
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public DateTime SoldDate { get; set; }
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
}

public class SoldCompsResult
{
    public string Query { get; set; } = "";
    public List<SoldComp> Items { get; set; } = [];
    public int Count { get; set; }
    public decimal Average { get; set; }
    public decimal Median { get; set; }
    public decimal Min { get; set; }
    public decimal Max { get; set; }
    public decimal? SellThroughPercent { get; set; }
    public decimal AvgShipping { get; set; }
}

public class EbayOpportunityItem
{
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public decimal ShippingCost { get; set; }
    // Whether eBay actually stated that shipping cost. A listing with no shipping option quoted is
    // shipping-unknown, NOT free shipping — booking it as free silently understates what winning an
    // auction costs, and the sniper's max bid is exactly that number minus shipping.
    public bool ShippingStated { get; set; }
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public DateTime? EndDate { get; set; }
    public string SellerUsername { get; set; } = "";
    public int SellerFeedbackScore { get; set; }
    // eBay's percentage-positive for the seller. Null when the search didn't report one.
    public decimal? SellerFeedbackPercent { get; set; }
    public string BuyingOption { get; set; } = "";
    public int BidCount { get; set; }
    // eBay's own item id, for the rows that carry one — the join that lets a tracked deal point
    // back at the listing it came from.
    public string ItemId { get; set; } = "";
    public string Condition { get; set; } = "";
}

// Mutable (not anonymous) so the top-candidate Terapeak re-check can overwrite profit fields
// in place after the initial broad-estimate pass.
public class OpportunityListItem
{
    public string Title { get; set; } = "";
    public decimal Price { get; set; }
    public decimal ShippingCost { get; set; }
    public decimal TotalCost { get; set; }
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public DateTime? EndDate { get; set; }
    public string SellerUsername { get; set; } = "";
    public int SellerFeedbackScore { get; set; }
    public string BuyingOption { get; set; } = "";
    public int BidCount { get; set; }
    public decimal? MarketAverage { get; set; }
    public decimal? EstimatedResaleShipping { get; set; }
    public decimal? EstimatedResalePrice { get; set; }
    public decimal? EstimatedProfit { get; set; }
    public decimal? ProfitPercent { get; set; }
    // How fast this exact item's comps actually sell on eBay (Terapeak sell-through %) — only
    // populated once the per-item Terapeak recheck verifies this candidate, same as ProfitPercent.
    public decimal? SellThroughPercent { get; set; }
    // True when that sell-through couldn't be measured for this item (no rate, or the degenerate
    // zero-active-comps denominator — see SellThroughCalculator.IsUnverified). The row is running
    // on thin data, so the UI shows a low-confidence indicator instead of the green
    // "Terapeak-matched" badge that would make it read as a guaranteed flip.
    public bool SellThroughUnverified { get; set; }
    // Sell-through / liquidity (see LiquidityScoringService) — only populated when the per-item
    // recheck was priced from the local sold-history database, since that's the only source with
    // per-comparable sold dates to compute velocity/trend from (Terapeak only returns aggregates).
    public int? LiquidityScore { get; set; }
    public string? LiquidityLevel { get; set; }
    public int? EstimatedDaysToSell { get; set; }
    public int? OpportunityScore { get; set; }
    public bool IsVerified { get; set; }
    public bool IsUnderpriced { get; set; }
    public bool IsHighProfitMargin { get; set; }
    public bool IsHighThroughput { get; set; }
    public bool IsEndingSoon { get; set; }
    public bool IsHighDemand { get; set; }
    public bool IsNewlyListed { get; set; }
    public bool HasPoorTitle { get; set; }
    public bool HasMisspelledTitle { get; set; }
    public bool HasPoorPhoto { get; set; }

    // ── Real matching/pricing/scoring engine output (see Program.cs AnalyzeProductAsync and
    // Models/MarketAnalysisModels.cs MarketAnalysisResult) — additive, so nothing that already
    // reads the fields above breaks; these are the fuller breakdown the newer UI surfaces. ──────
    public decimal? QuickSalePrice { get; set; }
    public decimal? RecommendedListingPrice { get; set; }
    public decimal? HighPriceTarget { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal? BreakEvenSalePrice { get; set; }
    public decimal? EstimatedMonthlySales { get; set; }
    public int LocalComparableCount { get; set; }
    public int TerapeakComparableCount { get; set; }
    public decimal LocalWeightPercent { get; set; }
    public decimal TerapeakWeightPercent { get; set; }
    public int ConfidenceScore { get; set; }
    public string? ConfidenceLevel { get; set; }
    // Where the resale price and that confidence score came from, arithmetically: each sold-data
    // source's own figure and weight, and the seven terms of the score with the points each earned.
    // Null when nothing could price the item. See Services/PriceBasis.cs.
    public PriceBasis? PriceBasis { get; set; }
    public int PriceStabilityScore { get; set; }
    public string PriceTrend { get; set; } = "Unknown";
    public bool MarketDataDisagreement { get; set; }
    public string? DisagreementMessage { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> ScoreReasons { get; set; } = [];
    public Dictionary<string, double>? ScoreComponents { get; set; }
    public List<MarketplaceComparableResult> TopComparables { get; set; } = [];
    public string CompetitionLevel { get; set; } = "Unknown";
    public int CloseActiveComparableCount { get; set; }
}

// Return shape of the opportunity-search pipeline (Program.cs FindOpportunitiesAsync).
public class OpportunitySearchResult
{
    public string Query { get; set; } = "";
    public decimal MarketValue { get; set; }
    public decimal AveragePrice { get; set; }
    public string SoldSource { get; set; } = "none";
    public string ListingType { get; set; } = "AUCTION";
    public decimal? SellThroughPercent { get; set; }
    public List<OpportunityListItem> Items { get; set; } = [];
}

// ── Supplier File Analyzer (dropship profit calculator) ──────────────────────

public class AnalyzeSupplierFileRequest
{
    public string ImageBase64 { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
}

// Claude's raw extraction from a supplier price list / product photo — one entry per
// distinct product or model found in the image.
public class SupplierProduct
{
    public string ProductName { get; set; } = "";
    public string Brand { get; set; } = "";
    public string Model { get; set; } = "";
    public string PartNumber { get; set; } = "";
    // Concise eBay-search-friendly keyword string (brand + model + key spec), not the
    // full marketing name — this is what actually gets searched against sold comps.
    public string SearchQuery { get; set; } = "";
    public decimal WholesaleCostUsd { get; set; }
    public string Notes { get; set; } = "";
}

// One priced dropship candidate — a SupplierProduct plus whatever real sold-comp data
// (Terapeak or cache) could be found for it.
public class DropshipAnalysisItem
{
    public string ProductName { get; set; } = "";
    public string SearchQuery { get; set; } = "";
    public decimal WholesaleCostUsd { get; set; }
    public string Notes { get; set; } = "";
    public decimal? EbaySoldAverage { get; set; }
    public decimal? EbaySoldMedian { get; set; }
    public decimal? SellThroughPercent { get; set; }
    public decimal? AvgShipping { get; set; }
    public decimal? EstimatedFees { get; set; }
    public decimal? EstimatedProfit { get; set; }
    public decimal? EstimatedProfitPercent { get; set; }
    public bool IsVerified { get; set; }
    public string TerapeakUrl { get; set; } = "";

    // ── Local market research enrichment (generic naming — never expose provider/schema details) ──
    public bool LocalDataAvailable { get; set; }
    public decimal? EstimatedResalePrice { get; set; }
    public int ComparableCount { get; set; }
    public int ConfidenceScore { get; set; }
    public List<MarketplaceComparableResult> ComparableListings { get; set; } = [];
    // Set only when a local lookup ran and found nothing reliable — e.g. "No reliable local
    // sold-history matches found." UI shows this instead of the local-data fields.
    public string? LocalDataMessage { get; set; }

    // ── Sell-through / liquidity (see LiquidityScoringService) ──────────────────────────────
    public int? EstimatedDaysToSell { get; set; }
    public string? LiquidityLevel { get; set; }
    // Set when liquidity couldn't be reliably estimated (e.g. fewer than 3 recent comparables) —
    // e.g. "Not enough recent sales to estimate how fast this sells."
    public string? LiquidityMessage { get; set; }

    // ── Real matching/pricing/scoring engine output (see Program.cs AnalyzeProductAsync) ──────
    public decimal? QuickSalePrice { get; set; }
    public decimal? RecommendedListingPrice { get; set; }
    public decimal? HighPriceTarget { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal? BreakEvenSalePrice { get; set; }
    public decimal? EstimatedMonthlySales { get; set; }
    public int TerapeakComparableCount { get; set; }
    public string? ConfidenceLevel { get; set; }
    public int PriceStabilityScore { get; set; }
    public string PriceTrend { get; set; } = "Unknown";
    public bool MarketDataDisagreement { get; set; }
    public string? DisagreementMessage { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> ScoreReasons { get; set; } = [];
    public int? OpportunityScore { get; set; }
}

public class DropshipAnalysisResult
{
    public List<DropshipAnalysisItem> Items { get; set; } = [];
    public int ProductsExtracted { get; set; }
    public int ProductsPriced { get; set; }
}

public class GeneratePhotosRequest
{
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string VisualDescription { get; set; } = "";
    public string ImageBase64 { get; set; } = "";
    public string MimeType { get; set; } = "image/jpeg";
    public string ImageType { get; set; } = "webpage_screenshot";
}

public class PostListingRequest : ListingData
{
    public string? EbayToken { get; set; }

    /// <summary>
    /// The autosave key of the draft this publish came from, when there is one.
    /// </summary>
    /// <remarks>
    /// Lets the publish outcome be written back against the recovered draft, so a listing that went
    /// live stops being offered back as unfinished work, and one that failed keeps eBay's reason
    /// attached to it. Optional: the duplicate-publish guard keys on the listing's content, never on
    /// this, so an old client that doesn't send it is still protected.
    /// </remarks>
    public string? WorkKey { get; set; }

    /// <summary>
    /// The seller's own code for this item, carried from the draft that made it.
    /// </summary>
    /// <remarks>
    /// The one key that outlives the listing: eBay mints a new listing ID on every relist, and
    /// <see cref="Services.CostBasisStore"/> falls back to the SKU so the cost the seller already
    /// recorded survives it. Blank on a listing typed from scratch, and blank is honoured — see
    /// <see cref="Services.SellerSku"/>, which never invents a code for somebody's Seller Hub.
    /// </remarks>
    public string Sku { get; set; } = "";

    public string ListingFormat { get; set; } = "FIXED_PRICE";
    public int DurationDays { get; set; } = 30;
    // Per-listing policy overrides (fall back to saved credentials when blank)
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
}

public class EbayAuthRequest
{
    public string Code { get; set; } = "";
}

public class EbayOAuthRedirectRequest
{
    public string RedirectUrl { get; set; } = "";
}

public class EbayListingSummary
{
    public string OfferId { get; set; } = "";
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Status { get; set; } = "";
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string LastUpdated { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }
    public string Condition { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public int WatchCount { get; set; }
    public string ListingUrl { get; set; } = "";
    // How long this listing has been live, and how it has performed while it was. Only the
    // Trading API reports these; the Inventory API has no equivalent, which is why the merge in
    // GetListingsAsync is field-level rather than whole-object. Age is the single strongest
    // signal of stuck capital, so a null here is reported as "age unknown" rather than as 0 days.
    public DateTime? StartTimeUtc { get; set; }
    public int QuantitySold { get; set; }
    public int HitCount { get; set; }
    // What the BUYER pays to have it shipped — not what the label costs the seller. A buyer
    // compares delivered prices and eBay's cheapest-first sort orders on them, so Price Position
    // cannot rank a listing without this. Read opportunistically, same as HitCount: eBay omits
    // the block entirely on some listings, and an unreported charge is not free shipping, so the
    // flag is carried separately rather than letting a zero stand in for "we don't know".
    public decimal ShippingCost { get; set; }
    public bool ShippingCostKnown { get; set; }
    public PostListingRequest Data { get; set; } = new();
}

public class UpdateListingRequest : PostListingRequest
{
    public string OfferId { get; set; } = "";
    public string ListingId { get; set; } = "";
    // Sku is inherited. It used to be redeclared here, which meant an edit and a publish had two
    // different properties of the same name — and code holding one of these as a PostListingRequest
    // read the base's blank one. One property, so the SKU an edit carries is the SKU a publish sends.
    public bool ManualRevisionConfirmed { get; set; }
}

public class ImproveSeoRequest : ListingData { }

public class ModifyListingRequest : ListingData
{
    public string Instruction { get; set; } = "";
}

public class SniperBidRequest
{
    public string  ItemId  { get; set; } = "";
    public decimal MaxBid  { get; set; }
}

/// <summary>
/// The listings the seller picked for an AI rewrite on the Listing Copilot screen.
/// </summary>
/// <remarks>
/// Ids rather than a flag, deliberately: there is no "improve everything" request this API can
/// receive, so a bulk rewrite of someone's whole account cannot happen by accident or by a stray
/// call. The seller scans, sees the list, and chooses.
/// </remarks>
public class CopilotSeoRequest
{
    public List<string> ListingIds { get; set; } = [];
}

/// <summary>
/// The policies the seller chose to rename on the Listing Copilot screen.
/// </summary>
/// <remarks>
/// Ids only, never names. The server recomputes the plan from the policies as they stand and
/// renames only where its own plan still agrees, so a stale page cannot write a name derived from
/// data that has since changed and no caller can put an arbitrary string on a policy.
/// </remarks>
public class CopilotRenameRequest
{
    public List<string> PolicyIds { get; set; } = [];
}

/// <summary>The listings to look up categories for, or to re-categorise.</summary>
public class CopilotCategoryRequest
{
    public List<string> ListingIds { get; set; } = [];
}
