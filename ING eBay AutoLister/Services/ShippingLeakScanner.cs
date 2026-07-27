using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Finds the money leaking out of the shipping line across everything already listed.
/// </summary>
/// <remarks>
/// <para>
/// A shipping calculator you have to open per item is a calculator. This is the thing a seller
/// with 200 listings actually needs: a single pass that prices every live listing's real label
/// against what the seller has been assuming, and reports the gap in dollars per sale.
/// </para>
/// <para>
/// Deliberately read-only and entirely local — no eBay writes, no scraping, no network beyond the
/// one listings call every other board already makes. There is nothing to rate-limit, so unlike the
/// comp-driven scans this one runs over the whole inventory without a budget.
/// </para>
/// </remarks>
public sealed class ShippingLeakScanner(PackageEstimator estimator, ShippingAdvisor advisor)
{
    /// <summary>A gap smaller than this is inside the estimate's own error bars — not a finding.</summary>
    private const decimal MinimumReportable = 1.50m;

    /// <summary>Under-recovering this much per sale is not a rounding error, it is a policy problem.</summary>
    private const decimal CriticalGap = 5.00m;

    /// <summary>Above this share of the asking price, shipping is the reason the item is not profitable.</summary>
    private const decimal HeavyLoadPercent = 30m;

    /// <summary>Zone exposure worth flagging on its own, even when the average is fine.</summary>
    private const decimal MaterialZoneSpread = 8.00m;

    public ShippingScanResult Scan(
        IReadOnlyList<EbayListingSummary> listings, string? originZip, FeeProfile fees, int maxItems = 500)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var mix = ShippingZones.MixFor(originZip);
        var assumed = fees.DefaultShippingCost;

        var result = new ShippingScanResult
        {
            OriginZip = originZip ?? "",
            AssumedLabelCost = assumed,
            ZoneMix = mix,
            ActiveListings = listings.Count,
        };

        if (assumed <= 0m)
            result.DataWarning =
                "Your fee profile has no shipping cost in it, so every profit figure in the app is currently "
                + "assuming shipping is free. The per-sale gaps below are the full label cost.";

        var costs = new List<decimal>();

        foreach (var listing in listings.Take(Math.Max(1, maxItems)))
        {
            var package = estimator.Estimate(
                listing.Title, listing.Category,
                weightOz: listing.Data.WeightLbs * 16m + listing.Data.WeightOz,
                lengthIn: listing.Data.PackageLengthIn,
                widthIn: listing.Data.PackageWidthIn,
                heightIn: listing.Data.PackageHeightIn);

            var advice = advisor.Advise(package, listing.Price, unitCost: null, originZip, listing.Category, fees);
            result.Summary.ListingsScanned++;
            if (package.Source == "measured") result.Summary.MeasuredPackages++;
            else result.Summary.EstimatedPackages++;

            if (advice.Best is null)
            {
                result.Leaks.Add(NoServiceLeak(listing, package));
                continue;
            }

            costs.Add(advice.Best.ExpectedCost);
            var leak = Diagnose(listing, package, advice, assumed);
            if (leak is not null) result.Leaks.Add(leak);
        }

        result.Leaks = result.Leaks.OrderByDescending(l => l.PerSaleImpact).ToList();
        result.Summary.LeaksFound = result.Leaks.Count;
        result.Summary.CriticalCount = result.Leaks.Count(l => l.Severity == "critical");
        result.Summary.TotalPerSaleImpact = Math.Round(result.Leaks.Sum(l => l.PerSaleImpact), 2);
        result.Summary.TotalAtRisk = Math.Round(result.Leaks.Sum(l => l.AtRisk), 2);
        result.Summary.AverageLabelCost = costs.Count > 0 ? Math.Round(costs.Average(), 2) : 0m;

        result.ElapsedMs = stopwatch.ElapsedMilliseconds;
        return result;
    }

    /// <summary>
    /// The single worst thing about one listing's shipping, or nothing.
    /// </summary>
    /// <remarks>
    /// One finding per listing, not all of them. A board that reports four overlapping problems on
    /// the same item is a board nobody finishes reading, and the fixes are usually the same fix.
    /// Ordered by how much money is on it: under-recovering the label beats a packing improvement,
    /// which beats zone exposure, which beats "this item is too heavy for its price".
    /// </remarks>
    private static ShippingLeak? Diagnose(
        EbayListingSummary listing, PackageSpec package, ShippingRecommendation advice, decimal assumed)
    {
        var best = advice.Best!;
        var gap = Math.Round(best.ExpectedCost - assumed, 2);

        // 1. The label costs more than the app has been told it does. Every profit number shown for
        //    this item — and every sourcing decision made on an item like it — is overstated by this.
        if (gap >= MinimumReportable)
            return Leak(listing, package, advice, assumed, "underpriced_label",
                gap >= CriticalGap ? "critical" : "warning",
                $"Costs {Money(gap)} more to ship than your profit numbers assume",
                $"An average {package.WeightLb:0.##} lb label on {best.Name} runs {Money(best.ExpectedCost)}, "
                + $"against the {Money(assumed)} in your fee profile. Every net-profit figure in the app for this "
                + "item is that much too high.",
                assumed <= 0m
                    ? "Set a real default in Fees & Costs, or record this item's own shipping cost."
                    : $"Raise the price by {Money(gap)}, or update your default shipping cost so the rest of the app stops flattering itself.",
                gap);

        // 2. A packing change worth real money — the fix is a different box, not a different price.
        var tip = advice.Tips.FirstOrDefault(t => t.SavingPerSale >= MinimumReportable
                                               && t.Kind is "flat_rate_within_reach" or "dim_weight" or "surcharge");
        if (tip is not null)
            return Leak(listing, package, advice, assumed,
                tip.Kind == "dim_weight" ? "dim_weight" : tip.Kind == "surcharge" ? "oversize" : "wrong_service",
                tip.SavingPerSale >= CriticalGap ? "warning" : "info",
                tip.Headline, tip.Detail, "Repack it — the price does not need to change.", tip.SavingPerSale);

        // 3. The average is fine but the tails are not. Only worth saying on a free-shipping-shaped
        //    listing, which is what the flat assumption in the fee profile implies.
        if (!best.IsFlatRate && best.ZoneSpread >= MaterialZoneSpread)
            return Leak(listing, package, advice, assumed, "zone_risk", "info",
                $"{Money(best.ZoneSpread)} of this listing's profit depends on where the buyer lives",
                $"{Money(best.NearestZoneCost)} to ship nearby, {Money(best.FarthestZoneCost)} across the country. "
                + $"With one flat assumption of {Money(assumed)} you are quietly betting on the near end.",
                "Switch this listing to calculated shipping, or price it for the far zone.",
                best.ZoneSpread);

        // 4. Nothing is wrong with the shipping — the item is wrong for shipping.
        if (advice.ShippingLoadPercent >= HeavyLoadPercent && listing.Price > 0m)
            return Leak(listing, package, advice, assumed, "shipping_heavy", "warning",
                $"Shipping is {advice.ShippingLoadPercent:0.#}% of this item's price",
                $"{Money(best.ExpectedCost)} of label on a {Money(listing.Price)} listing, at {package.WeightLb:0.##} lb. "
                + "Heavy, cheap items are where margin goes to die.",
                "Bundle it with something small and valuable, sell it locally, or raise the price.",
                Math.Round(best.ExpectedCost, 2));

        return null;
    }

    private static ShippingLeak Leak(
        EbayListingSummary listing, PackageSpec package, ShippingRecommendation advice, decimal assumed,
        string kind, string severity, string headline, string detail, string fix, decimal impact) =>
        new()
        {
            ListingId = listing.ListingId,
            Sku = listing.Sku,
            Title = listing.Title,
            ListingUrl = listing.ListingUrl,
            ThumbnailUrl = listing.ThumbnailUrl,
            Price = listing.Price,
            Quantity = Math.Max(1, listing.Quantity),
            Package = package,
            BestServiceName = advice.Best?.Name ?? "",
            ExpectedLabelCost = advice.Best?.ExpectedCost ?? 0m,
            AssumedLabelCost = assumed,
            Kind = kind,
            Severity = severity,
            Headline = headline,
            Detail = detail,
            Fix = fix,
            PerSaleImpact = Math.Round(impact, 2),
            AtRisk = Math.Round(impact * Math.Max(1, listing.Quantity), 2),
            PackageEstimated = package.Source != "measured",
        };

    private static ShippingLeak NoServiceLeak(EbayListingSummary listing, PackageSpec package) => new()
    {
        ListingId = listing.ListingId,
        Sku = listing.Sku,
        Title = listing.Title,
        ListingUrl = listing.ListingUrl,
        ThumbnailUrl = listing.ThumbnailUrl,
        Price = listing.Price,
        Quantity = Math.Max(1, listing.Quantity),
        Package = package,
        Kind = "oversize",
        Severity = "critical",
        Headline = "No standard carrier service will take this package",
        Detail = $"At {package.WeightLb:0.##} lb and {package.LengthIn:0.#}\" x {package.WidthIn:0.#}\" x {package.HeightIn:0.#}\", "
               + "this is past what USPS and UPS carry on a normal label. If it sells, you are buying freight.",
        Fix = "Price in freight, or switch the listing to local pickup only.",
        PerSaleImpact = 0m,
        AtRisk = 0m,
        PackageEstimated = package.Source != "measured",
    };

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
