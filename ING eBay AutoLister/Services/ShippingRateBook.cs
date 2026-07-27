using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What it costs to put a label on a box: the services an eBay seller can actually buy, what each
/// will and will not carry, and the price by weight and zone.
/// </summary>
/// <remarks>
/// <para>
/// These are calibrated estimates of eBay/commercial label pricing, not a live carrier API. That is
/// a deliberate trade and it is stated everywhere the numbers surface: a rate API needs per-carrier
/// credentials the seller does not have, and would fail closed exactly when the app is most useful —
/// standing in a thrift store deciding whether to buy something. An estimate that is within a dollar,
/// always available and honest about being an estimate beats an exact number that is not there.
/// </para>
/// <para>
/// Rates are anchor points interpolated linearly on weight, held per zone band. Anchors are dense
/// where the rate card is steep (under a pound, where an ounce is worth real money) and sparse where
/// it is close to linear. Every service also carries its eligibility rules, because the cheapest
/// service that cannot legally carry the package is not a saving — it is a cancelled sale.
/// </para>
/// </remarks>
public static class ShippingRateBook
{
    /// <summary>The zone bands the rate columns are held at: zones 1-4 share a price, then 5, 6, 7, 8.</summary>
    private static readonly int[] ZoneColumns = [4, 5, 6, 7, 8];

    /// <summary>A weight anchor: cost at this weight, one entry per zone column.</summary>
    private sealed record Anchor(decimal WeightOz, decimal[] ByZoneColumn);

    private sealed record Service(
        string Code,
        string Name,
        string Carrier,
        decimal MaxWeightOz,
        Anchor[] Anchors,
        int TransitMin,
        int TransitMax,
        string Note)
    {
        public decimal FlatPrice { get; init; }
        public bool IsFlatRate => FlatPrice > 0m;

        /// <summary>Interior box dimensions for flat-rate services; null when size is unconstrained.</summary>
        public decimal[]? MaxDimensions { get; init; }

        /// <summary>Dimensional-weight divisor. 0 disables dim weight for this service.</summary>
        public int DimDivisor { get; init; }

        /// <summary>Volume in cubic inches below which dim weight is not applied at all.</summary>
        public decimal DimThresholdCubicIn { get; init; }

        public decimal MaxLengthPlusGirthIn { get; init; }
        public decimal MaxLongestSideIn { get; init; }

        /// <summary>Category keywords a restricted service is limited to. Empty means unrestricted.</summary>
        public string[] CategoryAllowList { get; init; } = [];
        public decimal MaxThicknessIn { get; init; }
    }

    // ── Rate tables ──────────────────────────────────────────────────────────────────────────────
    // Columns are zones [1-4, 5, 6, 7, 8].

    private static readonly Anchor[] GroundAdvantage =
    [
        new(4,     [4.20m,  4.70m,  4.95m,  5.30m,  5.60m]),
        new(8,     [4.75m,  5.40m,  5.75m,  6.15m,  6.60m]),
        new(12,    [5.15m,  6.00m,  6.45m,  7.00m,  7.45m]),
        new(15.99m,[5.60m,  6.60m,  7.15m,  7.70m,  8.20m]),
        new(16,    [6.60m,  7.90m,  8.60m,  9.50m, 10.40m]),
        new(32,    [7.30m,  9.20m, 10.40m, 11.80m, 13.10m]),
        new(48,    [8.10m, 10.60m, 12.30m, 14.30m, 16.20m]),
        new(80,    [9.60m, 13.70m, 16.60m, 19.60m, 22.50m]),
        new(160,  [14.20m, 22.00m, 27.60m, 33.00m, 38.00m]),
        new(320,  [22.50m, 36.00m, 45.50m, 54.00m, 62.00m]),
        new(640,  [38.00m, 58.00m, 74.00m, 87.00m, 99.00m]),
        new(1120, [58.00m, 88.00m, 112.00m, 132.00m, 150.00m]),
    ];

    private static readonly Anchor[] PriorityMail =
    [
        new(16,    [8.20m,  9.30m, 10.20m, 11.40m, 12.60m]),
        new(32,    [9.10m, 11.20m, 13.00m, 15.20m, 17.50m]),
        new(48,    [9.90m, 12.90m, 15.50m, 18.70m, 22.00m]),
        new(80,   [11.80m, 16.60m, 21.00m, 26.40m, 32.00m]),
        new(160,  [17.50m, 26.50m, 35.00m, 45.00m, 55.00m]),
        new(320,  [28.00m, 43.00m, 57.00m, 72.00m, 88.00m]),
        new(640,  [47.00m, 72.00m, 95.00m, 118.00m, 140.00m]),
        new(1120, [72.00m, 108.00m, 142.00m, 175.00m, 205.00m]),
    ];

    private static readonly Anchor[] UpsGround =
    [
        new(16,    [9.40m, 10.60m, 11.30m, 12.10m, 13.00m]),
        new(80,   [10.50m, 12.40m, 13.60m, 14.80m, 16.00m]),
        new(160,  [13.00m, 16.50m, 19.20m, 21.60m, 24.00m]),
        new(320,  [18.00m, 25.00m, 30.00m, 35.00m, 40.00m]),
        new(640,  [28.00m, 42.00m, 52.00m, 61.00m, 70.00m]),
        new(1120, [42.00m, 64.00m, 81.00m, 96.00m, 110.00m]),
        new(1600, [56.00m, 88.00m, 112.00m, 132.00m, 150.00m]),
    ];

    // eBay Standard Envelope: flat, no zone, and by far the cheapest thing on this page — but only
    // for flat, light, category-restricted goods. Sellers of cards and coins who do not know it
    // exists are paying five times what they need to on every single sale.
    private static readonly Anchor[] StandardEnvelope =
    [
        new(1, [0.62m, 0.62m, 0.62m, 0.62m, 0.62m]),
        new(2, [0.66m, 0.66m, 0.66m, 0.66m, 0.66m]),
        new(3, [0.70m, 0.70m, 0.70m, 0.70m, 0.70m]),
        new(4, [0.86m, 0.86m, 0.86m, 0.86m, 0.86m]),
    ];

    private static readonly Service[] Catalog =
    [
        new("ebay_standard_envelope", "eBay Standard Envelope", "USPS", 4, StandardEnvelope, 3, 8,
            "Flat rate to anywhere in the US. Trading cards, coins, stamps, photos and postcards only, and it must stay under 1/4 inch thick.")
        {
            MaxThicknessIn = 0.25m,
            CategoryAllowList = ["card", "trading card", "coin", "stamp", "postcard", "photo", "sports card", "pokemon", "currency", "banknote"],
        },

        new("usps_ground_advantage", "USPS Ground Advantage", "USPS", 1120, GroundAdvantage, 2, 5,
            "The default for almost everything under 70 lb. Priced by weight and distance.")
        {
            DimDivisor = 166,
            DimThresholdCubicIn = 1728,   // dim weight only bites above one cubic foot
            MaxLengthPlusGirthIn = 130,
            MaxLongestSideIn = 108,
        },

        new("usps_priority", "USPS Priority Mail", "USPS", 1120, PriorityMail, 1, 3,
            "Faster, and worth it only when speed sells the item or the buyer paid for it.")
        {
            DimDivisor = 166,
            DimThresholdCubicIn = 1728,
            MaxLengthPlusGirthIn = 130,
            MaxLongestSideIn = 108,
        },

        // Flat rate: the reason a 15 lb box of parts can cost $17.60 to cross the country instead
        // of $60. Weight is irrelevant up to 70 lb; only the box has to fit.
        new("usps_flat_padded", "Priority Mail Padded Flat Rate Envelope", "USPS", 1120, [], 1, 3,
            "One price anywhere in the US, up to 70 lb, as long as it fits the envelope.")
        { FlatPrice = 9.85m, MaxDimensions = [12.5m, 9.5m, 0.75m] },

        new("usps_flat_small", "Priority Mail Small Flat Rate Box", "USPS", 1120, [], 1, 3,
            "One price anywhere in the US, up to 70 lb, as long as it fits the box.")
        { FlatPrice = 10.10m, MaxDimensions = [8.6m, 5.4m, 1.6m] },

        new("usps_flat_medium", "Priority Mail Medium Flat Rate Box", "USPS", 1120, [], 1, 3,
            "One price anywhere in the US, up to 70 lb. The single best deal in shipping for anything small and heavy.")
        { FlatPrice = 17.60m, MaxDimensions = [11m, 8.5m, 5.5m] },

        new("usps_flat_large", "Priority Mail Large Flat Rate Box", "USPS", 1120, [], 1, 3,
            "One price anywhere in the US, up to 70 lb, as long as it fits the box.")
        { FlatPrice = 23.50m, MaxDimensions = [12m, 12m, 5.5m] },

        // The only service here that goes past USPS's 70 lb ceiling, which is what makes it the
        // answer for miners, monitors, amplifiers and anything else the app's own sourcing screens
        // routinely turn up.
        new("ups_ground", "UPS Ground", "UPS", 2400, UpsGround, 1, 5,
            "Takes what USPS will not: up to 150 lb, and cheaper than Priority once a box is heavy.")
        {
            DimDivisor = 139,             // UPS applies dim weight at any size, not just over a cubic foot
            MaxLengthPlusGirthIn = 165,
            MaxLongestSideIn = 108,
        },
    ];

    /// <summary>Every service, for the reference table in the UI.</summary>
    public static IReadOnlyList<object> Describe() => Catalog.Select(s => (object)new
    {
        code = s.Code,
        name = s.Name,
        carrier = s.Carrier,
        // Two decimals, not one: the envelope services cap at ounces, and 4 oz rounded to one
        // decimal reads as "0.2 lb", which looks like a bug rather than a limit.
        maxWeightLb = Math.Round(s.MaxWeightOz / 16m, 2),
        maxWeightOz = s.MaxWeightOz,
        flatRate = s.IsFlatRate,
        flatPrice = s.FlatPrice,
        maxDimensions = s.MaxDimensions,
        transitDaysMin = s.TransitMin,
        transitDaysMax = s.TransitMax,
        note = s.Note,
    }).ToList();

    /// <summary>
    /// Quotes every service against one package, eligible or not.
    /// </summary>
    /// <remarks>
    /// Ineligible services are returned rather than filtered out. "No carrier will take this box"
    /// and "this is expensive" are different problems with different fixes, and a seller looking at
    /// a $62 label needs to see that a $17.60 flat-rate box was two inches away from working.
    /// </remarks>
    public static List<ShippingServiceQuote> QuoteAll(PackageSpec package, IReadOnlyList<ZoneShare> zoneMix, string? category = null)
    {
        var quotes = new List<ShippingServiceQuote>();
        foreach (var service in Catalog)
            quotes.Add(Quote(service, package, zoneMix, category));
        return quotes;
    }

    private static ShippingServiceQuote Quote(Service service, PackageSpec package, IReadOnlyList<ZoneShare> zoneMix, string? category)
    {
        var quote = new ShippingServiceQuote
        {
            Code = service.Code,
            Name = service.Name,
            Carrier = service.Carrier,
            IsFlatRate = service.IsFlatRate,
            TransitDaysMin = service.TransitMin,
            TransitDaysMax = service.TransitMax,
            Note = service.Note,
        };

        var billable = BillableWeightOz(service, package);
        quote.BillableWeightOz = billable;
        quote.DimWeightApplied = billable > package.WeightOz + 0.01m;

        var reason = Ineligibility(service, package, billable, category);
        if (reason is not null)
        {
            quote.Eligible = false;
            quote.IneligibleReason = reason;
            return quote;
        }

        quote.Eligible = true;
        var (surcharge, surchargeReason) = Surcharge(service, package);
        quote.SurchargeAmount = surcharge;
        quote.SurchargeReason = surchargeReason;

        // An empty mix means the caller could not work out where the seller ships from. Pricing the
        // national-average zone is a better answer than no answer, and better than zero.
        var zones = zoneMix.Count > 0
            ? zoneMix
            : new List<ZoneShare> { new() { Zone = ShippingZones.DefaultZone, SharePercent = 100m } };

        foreach (var slice in zones)
            quote.ZoneCosts[slice.Zone] = Math.Round(CostAt(service, billable, slice.Zone) + surcharge, 2);

        quote.ExpectedCost = ShippingZones.ExpectedOver(zones, z => quote.ZoneCosts.GetValueOrDefault(z, 0m));
        quote.NearestZoneCost = quote.ZoneCosts[ShippingZones.NearestZone(zones)];
        quote.FarthestZoneCost = quote.ZoneCosts[ShippingZones.FarthestZone(zones)];

        return quote;
    }

    /// <summary>
    /// The weight the carrier bills: the greater of what the scale says and what the box implies.
    /// </summary>
    /// <remarks>
    /// Dimensional weight is the quietest way a reseller loses money. A big light box — a lampshade,
    /// a keyboard in its retail packaging, anything shipped in whatever carton was to hand — bills
    /// at the weight of the air inside it, and nothing on the eBay listing screen ever mentions it.
    /// </remarks>
    private static decimal BillableWeightOz(Service service, PackageSpec package) => BillableWeightOz(
        package, service.DimDivisor, service.DimThresholdCubicIn);

    /// <summary>Billable weight under an explicit divisor and threshold. Public for the tips engine.</summary>
    public static decimal BillableWeightOz(PackageSpec package, int dimDivisor, decimal thresholdCubicIn)
    {
        if (dimDivisor <= 0 || !package.HasDimensions) return package.WeightOz;
        if (package.VolumeCubicIn <= thresholdCubicIn) return package.WeightOz;

        var dimPounds = Math.Ceiling(package.VolumeCubicIn / dimDivisor);
        return Math.Max(package.WeightOz, dimPounds * 16m);
    }

    /// <summary>Dimensional weight for a package under a given divisor, in ounces. Public for the tips engine.</summary>
    public static decimal DimensionalWeightOz(PackageSpec package, int divisor) =>
        divisor > 0 && package.HasDimensions ? Math.Ceiling(package.VolumeCubicIn / divisor) * 16m : 0m;

    private static string? Ineligibility(Service service, PackageSpec package, decimal billableOz, string? category)
    {
        if (package.WeightOz <= 0) return "No weight to price.";

        if (billableOz > service.MaxWeightOz)
        {
            // Ounces for the ounce-scale services. A 4 oz cap rendered as "0.3 lb" reads as a
            // rounding artefact rather than as the actual rule the envelope services are sold on.
            var limit = service.MaxWeightOz < 16m
                ? $"{service.MaxWeightOz:0.#} oz"
                : $"{service.MaxWeightOz / 16m:0.#} lb";
            return $"Over the {limit} limit"
                 + (billableOz > package.WeightOz ? " once dimensional weight is applied." : ".");
        }

        if (service.MaxThicknessIn > 0 && package.HasDimensions)
        {
            var thinnest = Math.Min(package.LengthIn, Math.Min(package.WidthIn, package.HeightIn));
            if (thinnest > service.MaxThicknessIn)
                return $"Too thick — this service stops at {service.MaxThicknessIn:0.##}\".";
        }

        if (service.CategoryAllowList.Length > 0)
        {
            var haystack = ((category ?? "") + " " + (package.Profile ?? "")).ToLowerInvariant();
            if (!service.CategoryAllowList.Any(k => haystack.Contains(k, StringComparison.Ordinal)))
                return "Restricted to trading cards, coins, stamps, photos and postcards.";
        }

        if (service.MaxDimensions is { } max && package.HasDimensions)
        {
            if (!FitsIn(package, max))
                return $"Does not fit the {max[0]:0.#}\" x {max[1]:0.#}\" x {max[2]:0.#}\" box.";
        }
        else if (service.MaxDimensions is not null && !package.HasDimensions)
        {
            return "Needs the package dimensions to know whether it fits.";
        }

        if (package.HasDimensions)
        {
            if (service.MaxLongestSideIn > 0 && package.LongestSideIn > service.MaxLongestSideIn)
                return $"Longest side is over the {service.MaxLongestSideIn:0.#}\" limit.";
            if (service.MaxLengthPlusGirthIn > 0 && package.LengthPlusGirthIn > service.MaxLengthPlusGirthIn)
                return $"Length plus girth is over the {service.MaxLengthPlusGirthIn:0.#}\" limit.";
        }

        return null;
    }

    /// <summary>Whether the package fits the box in any orientation.</summary>
    private static bool FitsIn(PackageSpec package, decimal[] box)
    {
        var item = new[] { package.LengthIn, package.WidthIn, package.HeightIn }.OrderBy(v => v).ToArray();
        var slot = box.OrderBy(v => v).ToArray();
        return item[0] <= slot[0] && item[1] <= slot[1] && item[2] <= slot[2];
    }

    // Large-package and additional-handling surcharges. Approximated as two flat steps rather than
    // each carrier's full matrix: the point is that a big box costs materially more than the weight
    // table says, which is the part sellers do not budget for.
    private static (decimal Amount, string Reason) Surcharge(Service service, PackageSpec package)
    {
        if (service.IsFlatRate || !package.HasDimensions) return (0m, "");

        if (package.LongestSideIn >= 48m || package.LengthPlusGirthIn >= 105m)
            return (service.Carrier == "UPS" ? 30.00m : 20.00m, "Large-package surcharge — this box is oversize for the carrier.");

        if (package.LongestSideIn >= 30m || package.LengthPlusGirthIn >= 85m)
            return (service.Carrier == "UPS" ? 8.50m : 5.00m, "Additional-handling surcharge for an outsized box.");

        return (0m, "");
    }

    /// <summary>Cost for one service at one weight and zone, interpolating between rate anchors.</summary>
    private static decimal CostAt(Service service, decimal billableOz, int zone)
    {
        if (service.IsFlatRate) return service.FlatPrice;

        var column = ZoneColumnIndex(zone);
        var anchors = service.Anchors;
        if (anchors.Length == 0) return 0m;

        if (billableOz <= anchors[0].WeightOz) return anchors[0].ByZoneColumn[column];

        for (var i = 1; i < anchors.Length; i++)
        {
            if (billableOz > anchors[i].WeightOz) continue;

            var lower = anchors[i - 1];
            var upper = anchors[i];
            var span = upper.WeightOz - lower.WeightOz;
            if (span <= 0m) return upper.ByZoneColumn[column];

            var t = (billableOz - lower.WeightOz) / span;
            var low = lower.ByZoneColumn[column];
            var high = upper.ByZoneColumn[column];
            return Math.Round(low + (high - low) * t, 2);
        }

        return anchors[^1].ByZoneColumn[column];
    }

    private static int ZoneColumnIndex(int zone)
    {
        for (var i = 0; i < ZoneColumns.Length; i++)
            if (zone <= ZoneColumns[i]) return i;
        return ZoneColumns.Length - 1;
    }
}
