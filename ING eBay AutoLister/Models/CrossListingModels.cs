namespace ING_eBay_AutoLister.Models;

// ── Cross-listing exporter ────────────────────────────────────────────────────
// Turns one eBay draft into ready-to-post listings for other marketplaces. Same
// item in three places = three times the buyer pool, and the net-parity price
// keeps the seller's take-home the same instead of quietly eating the fee
// difference. See Services/CrossListingExporter.cs.

public class CrossListRequest : ListingData
{
    // Marketplace keys to export ("facebook", "mercari", "amazon"). Empty = all of them.
    public List<string> Targets { get; set; } = [];
}

public class CrossListingField
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    // True when the target marketplace rejects the listing without it — drives the UI highlight.
    public bool Required { get; set; }
}

public class CrossListingResult
{
    public string Marketplace { get; set; } = "";
    public string DisplayName { get; set; } = "";

    // Where the seller goes to actually post it.
    public string CreateUrl { get; set; } = "";

    // "catalog_csv" / "flat_file" — the marketplace really ingests this file.
    // "manual" — no public bulk import exists, so the CSV is a worksheet and the
    // copy buttons are the real path. Stated plainly so nobody uploads it and waits.
    public string ImportSupport { get; set; } = "manual";
    public string ImportNote { get; set; } = "";

    public string Title { get; set; } = "";
    public int TitleLimit { get; set; }
    public bool TitleTruncated { get; set; }

    public string Description { get; set; } = "";
    public int DescriptionLimit { get; set; }
    public bool DescriptionTruncated { get; set; }

    public string Condition { get; set; } = "";

    // ── Pricing ──────────────────────────────────────────────────────────────
    // SamePrice: list at the eBay price. NetParityPrice: list at whatever price
    // leaves the same money in the seller's pocket after that site's fees.
    public decimal SamePrice { get; set; }
    public decimal NetAtSamePrice { get; set; }
    public decimal NetParityPrice { get; set; }
    public decimal EstimatedFees { get; set; }
    public decimal FeePercent { get; set; }
    public decimal FeeFixed { get; set; }
    public string FeeNote { get; set; } = "";

    public List<CrossListingField> Fields { get; set; } = [];
    public List<string> Warnings { get; set; } = [];

    public string Csv { get; set; } = "";
    public string CsvFilename { get; set; } = "";
}

public class CrossListingExport
{
    public string Sku { get; set; } = "";
    public decimal EbayPrice { get; set; }
    public decimal EbayFees { get; set; }
    public decimal EbayNet { get; set; }
    public List<CrossListingResult> Listings { get; set; } = [];
    // Problems that apply to every target (no photos, no price, empty title).
    public List<string> Warnings { get; set; } = [];
}
