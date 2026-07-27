namespace ING_eBay_AutoLister.Models;

// ── The Shipping Profit Engine ───────────────────────────────────────────────────────────────────
//
// Every profit number this app has ever shown was computed against ONE flat shipping figure
// (FeeProfile.DefaultShippingCost) applied identically to a phone case and a 40 lb miner. That
// single assumption sits underneath the local-arbitrage board, the lot analyzer, the deal scanner,
// inventory health, break-even and every offer floor — so when it is wrong, it is wrong everywhere
// at once and always in the seller's favour, which is the worst direction for a number to be wrong.
//
// These types replace that guess with a real label estimate: a package (measured or inferred from
// the item), the services that can legally carry it, what each costs to every part of the country,
// and what that spread means for the price the seller should put on the listing.

/// <summary>
/// The box, as the carrier will see it. Weight in ounces because that is the unit the cheap end of
/// the rate card is priced in, and getting from 15.9 oz to 16.1 oz is a real price cliff.
/// </summary>
public sealed class PackageSpec
{
    public decimal WeightOz { get; set; }
    public decimal LengthIn { get; set; }
    public decimal WidthIn { get; set; }
    public decimal HeightIn { get; set; }

    /// <summary>measured | estimated | fallback — how much the numbers below can be trusted.</summary>
    public string Source { get; set; } = "fallback";

    /// <summary>Plain-English reason this package is what it is, shown next to every figure it drives.</summary>
    public string Basis { get; set; } = "";

    /// <summary>The package class the estimator matched, when it matched one (e.g. "laptop").</summary>
    public string Profile { get; set; } = "";

    public decimal WeightLb => Math.Round(WeightOz / 16m, 3);
    public decimal VolumeCubicIn => Math.Round(LengthIn * WidthIn * HeightIn, 1);

    /// <summary>Longest side plus the girth around the other two — the surcharge trigger.</summary>
    public decimal LengthPlusGirthIn
    {
        get
        {
            var sides = new[] { LengthIn, WidthIn, HeightIn }.OrderByDescending(s => s).ToArray();
            return Math.Round(sides[0] + 2m * (sides[1] + sides[2]), 1);
        }
    }

    public decimal LongestSideIn => Math.Max(LengthIn, Math.Max(WidthIn, HeightIn));

    public bool HasDimensions => LengthIn > 0 && WidthIn > 0 && HeightIn > 0;

    public PackageSpec Clone() => (PackageSpec)MemberwiseClone();
}

/// <summary>What one carrier service costs to carry this package, everywhere it might go.</summary>
public sealed class ShippingServiceQuote
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string Carrier { get; set; } = "";

    public bool Eligible { get; set; }
    /// <summary>Why the carrier will not take this package — shown rather than hidden, because
    /// "no service can carry this" is itself an answer the seller needs before they buy the item.</summary>
    public string IneligibleReason { get; set; } = "";

    /// <summary>The weight actually billed: the greater of real weight and dimensional weight.</summary>
    public decimal BillableWeightOz { get; set; }
    public bool DimWeightApplied { get; set; }
    public decimal SurchargeAmount { get; set; }
    public string SurchargeReason { get; set; } = "";

    /// <summary>Flat-rate services cost the same everywhere, which is exactly when they win.</summary>
    public bool IsFlatRate { get; set; }

    /// <summary>Cost per zone, keyed by USPS zone number. Empty when ineligible.</summary>
    public Dictionary<int, decimal> ZoneCosts { get; set; } = [];

    /// <summary>
    /// The number that actually matters: cost weighted by how likely a buyer is to be in each zone,
    /// given where the seller ships from. This is what an average sale really costs.
    /// </summary>
    public decimal ExpectedCost { get; set; }

    public decimal NearestZoneCost { get; set; }
    public decimal FarthestZoneCost { get; set; }

    /// <summary>Farthest minus nearest — the money riding on where the buyer happens to live.</summary>
    public decimal ZoneSpread => Math.Round(FarthestZoneCost - NearestZoneCost, 2);

    public int TransitDaysMin { get; set; }
    public int TransitDaysMax { get; set; }

    public string Note { get; set; } = "";

    /// <summary>Set on the service the engine recommends, so the UI never has to re-derive it.</summary>
    public bool Recommended { get; set; }

    /// <summary>Expected cost above the cheapest eligible service. Zero on the winner.</summary>
    public decimal ExtraVsBest { get; set; }
}

/// <summary>
/// One way of charging the buyer for shipping, costed end to end.
/// </summary>
/// <remarks>
/// Compared at a constant buyer outlay, because that is the number that governs whether the item
/// sells at all. Held constant, eBay's cut is identical across all four modes — the final value fee
/// is charged on the shipping the buyer pays as well as the item price, which is the single most
/// commonly believed-backwards fact in reselling. What actually differs between these modes is who
/// absorbs the risk that the buyer lives far away.
/// </remarks>
public sealed class ShipModeOption
{
    /// <summary>free_expected | free_worst_case | flat | calculated</summary>
    public string Mode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>What goes in the price box.</summary>
    public decimal ItemPrice { get; set; }
    /// <summary>What the buyer is charged for shipping — 0 on a free-shipping listing.</summary>
    public decimal BuyerPaidShipping { get; set; }
    /// <summary>Item price plus shipping: what the buyer compares against other listings.</summary>
    public decimal BuyerOutlayNear { get; set; }
    public decimal BuyerOutlayFar { get; set; }

    /// <summary>Take-home after every fee and the real label, for a near / typical / far buyer.</summary>
    public decimal NetNear { get; set; }
    public decimal NetExpected { get; set; }
    public decimal NetFar { get; set; }

    /// <summary>Net at the far zone minus net at the near zone — the seller's exposure, in dollars.</summary>
    public decimal ZoneRisk => Math.Round(NetNear - NetFar, 2);

    /// <summary>Share of buyers, by population, whose label costs more than this mode collects.</summary>
    public decimal UnderwaterBuyerPercent { get; set; }

    public bool Recommended { get; set; }
    public string Verdict { get; set; } = "";
}

/// <summary>A packing change that is worth real money, ranked by how much.</summary>
public sealed class PackagingTip
{
    public string Kind { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public decimal SavingPerSale { get; set; }
}

/// <summary>The whole answer for one item: the box, the services, and how to charge for it.</summary>
public sealed class ShippingRecommendation
{
    public PackageSpec Package { get; set; } = new();
    public string OriginZip { get; set; } = "";
    public List<ShippingServiceQuote> Services { get; set; } = [];
    public ShippingServiceQuote? Best { get; set; }
    public List<ShipModeOption> Modes { get; set; } = [];
    public List<PackagingTip> Tips { get; set; } = [];

    /// <summary>The zone mix used, so the numbers above are checkable rather than magic.</summary>
    public List<ZoneShare> ZoneMix { get; set; } = [];

    public decimal ItemPrice { get; set; }
    /// <summary>Expected label cost as a share of the asking price — the "is this even worth shipping" number.</summary>
    public decimal ShippingLoadPercent { get; set; }

    public string Status { get; set; } = "ok";
    public string Headline { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary>How much of the country sits in one zone, from the seller's own doorstep.</summary>
public sealed class ZoneShare
{
    public int Zone { get; set; }
    public decimal SharePercent { get; set; }
    public string Regions { get; set; } = "";
}

// ── The bulk view: what shipping is quietly costing across everything already live ───────────────

/// <summary>One listing where shipping is costing more than the seller thinks it is.</summary>
public sealed class ShippingLeak
{
    public string ListingId { get; set; } = "";
    public string Sku { get; set; } = "";
    public string Title { get; set; } = "";
    public string ListingUrl { get; set; } = "";
    public string ThumbnailUrl { get; set; } = "";
    public decimal Price { get; set; }
    public int Quantity { get; set; }

    public PackageSpec Package { get; set; } = new();
    public string BestServiceName { get; set; } = "";
    public decimal ExpectedLabelCost { get; set; }
    public decimal AssumedLabelCost { get; set; }

    /// <summary>underpriced_label | wrong_service | dim_weight | zone_risk | shipping_heavy | oversize</summary>
    public string Kind { get; set; } = "";
    /// <summary>critical | warning | info</summary>
    public string Severity { get; set; } = "info";
    public string Headline { get; set; } = "";
    public string Detail { get; set; } = "";
    public string Fix { get; set; } = "";

    /// <summary>Money recovered per sale by acting on this. The whole point of the row.</summary>
    public decimal PerSaleImpact { get; set; }
    /// <summary>Per-sale impact across the units actually in stock.</summary>
    public decimal AtRisk { get; set; }

    /// <summary>Estimated, not measured — flagged so nobody treats an inferred box as a fact.</summary>
    public bool PackageEstimated { get; set; }
}

public sealed class ShippingScanSummary
{
    public int ListingsScanned { get; set; }
    public int LeaksFound { get; set; }
    public int CriticalCount { get; set; }
    public decimal TotalPerSaleImpact { get; set; }
    public decimal TotalAtRisk { get; set; }
    public decimal AverageLabelCost { get; set; }
    public int MeasuredPackages { get; set; }
    public int EstimatedPackages { get; set; }
}

public sealed class ShippingScanResult
{
    public string Status { get; set; } = "ok";
    public string Error { get; set; } = "";
    public string OriginZip { get; set; } = "";
    public int ActiveListings { get; set; }
    public decimal AssumedLabelCost { get; set; }
    public List<ShippingLeak> Leaks { get; set; } = [];
    public ShippingScanSummary Summary { get; set; } = new();
    public List<ZoneShare> ZoneMix { get; set; } = [];
    public string DataWarning { get; set; } = "";
    public long ElapsedMs { get; set; }
}

// ── Request shapes ───────────────────────────────────────────────────────────────────────────────

public sealed class ShippingQuoteRequest
{
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Condition { get; set; } = "";
    public decimal Price { get; set; }
    public decimal? UnitCost { get; set; }

    /// <summary>Measured package, when the seller has one. Zeroes mean "work it out for me".</summary>
    public decimal WeightLbs { get; set; }
    public decimal WeightOz { get; set; }
    public decimal PackageLengthIn { get; set; }
    public decimal PackageWidthIn { get; set; }
    public decimal PackageHeightIn { get; set; }

    /// <summary>Origin ZIP. Falls back to the listing default in credentials, then to a US centroid.</summary>
    public string OriginZip { get; set; } = "";
}
