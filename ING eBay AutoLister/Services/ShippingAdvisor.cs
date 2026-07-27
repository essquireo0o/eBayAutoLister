using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Answers the two shipping questions that decide whether a flip makes money: which label to buy,
/// and how to charge the buyer for it.
/// </summary>
/// <remarks>
/// <para>
/// The second question is the one sellers get wrong, and they get it wrong because of a single
/// widely-believed falsehood: that charging shipping separately avoids eBay's cut on it. It does
/// not. The final value fee is charged on the order total, shipping included, so at a fixed buyer
/// outlay the fee is identical whether the listing says "$50 + $10 shipping" or "$60 free shipping".
/// </para>
/// <para>
/// What actually differs is who carries the zone risk. Free shipping priced at the average label
/// cost is a bet that the buyer lives nearby, and it loses that bet on a predictable share of sales.
/// So rather than declaring a winner, this prices all four ways of charging at the same buyer outlay
/// and reports the seller's exposure under each — the near, expected and far take-home, and the
/// percentage of the country that costs more to reach than the listing collects.
/// </para>
/// </remarks>
public sealed class ShippingAdvisor(PackageEstimator estimator, NetProceedsCalculator netCalc)
{
    /// <summary>Above this share of the asking price, shipping is the problem with the item.</summary>
    private const decimal HeavyShippingLoadPercent = 30m;

    /// <summary>A packing tip has to be worth at least this much per sale to be worth reading.</summary>
    private const decimal MinimumTipSaving = 1.00m;

    public ShippingRecommendation Advise(ShippingQuoteRequest request, FeeProfile fees)
    {
        var package = estimator.Estimate(
            request.Title, request.Category,
            weightOz: request.WeightLbs * 16m + request.WeightOz,
            lengthIn: request.PackageLengthIn, widthIn: request.PackageWidthIn, heightIn: request.PackageHeightIn);

        return Advise(package, request.Price, request.UnitCost, request.OriginZip, request.Category, fees);
    }

    public ShippingRecommendation Advise(
        PackageSpec package, decimal itemPrice, decimal? unitCost, string? originZip, string? category, FeeProfile fees)
    {
        var mix = ShippingZones.MixFor(originZip);
        var result = new ShippingRecommendation
        {
            Package = package,
            OriginZip = originZip ?? "",
            ZoneMix = mix,
            ItemPrice = Math.Max(0m, itemPrice),
        };

        var quotes = ShippingRateBook.QuoteAll(package, mix, category);
        var eligible = quotes.Where(q => q.Eligible).OrderBy(q => q.ExpectedCost).ToList();

        if (eligible.Count == 0)
        {
            // Worth its own status rather than a $0 label: an item no carrier will take is a
            // freight problem or a local-pickup listing, and either way it is not a flip.
            result.Status = "no_service";
            result.Services = quotes;
            result.Headline = "No standard service will carry this package.";
            result.Note = quotes.Select(q => q.IneligibleReason).FirstOrDefault(r => r.Length > 0)
                ?? "Check the weight and dimensions — this is outside what USPS and UPS will take on a normal label.";
            return result;
        }

        var best = eligible[0];
        best.Recommended = true;
        foreach (var quote in eligible)
            quote.ExtraVsBest = Math.Round(quote.ExpectedCost - best.ExpectedCost, 2);

        // Recommended first, then the rest of the eligible field by price, then everything that
        // cannot carry it — the order the seller reads in.
        result.Services = eligible.Concat(quotes.Where(q => !q.Eligible)).ToList();
        result.Best = best;
        result.ShippingLoadPercent = result.ItemPrice > 0m
            ? Math.Round(best.ExpectedCost / result.ItemPrice * 100m, 1)
            : 0m;

        result.Modes = BuildModes(best, mix, result.ItemPrice, unitCost, fees);
        result.Tips = BuildTips(package, quotes, best, mix);
        (result.Headline, result.Note) = Describe(result, best);
        return result;
    }

    // ── How to charge for it ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The four ways to charge for shipping, all priced at the same buyer outlay.
    /// </summary>
    /// <remarks>
    /// Holding the buyer's total constant is what makes the comparison mean anything. Comparing a
    /// $50 free-shipping listing against a $50 + $10 listing compares two different offers, and the
    /// free one "loses" only because the buyer is paying $10 less. The real question is what the
    /// seller keeps when the buyer spends the same money either way.
    /// </remarks>
    private List<ShipModeOption> BuildModes(
        ShippingServiceQuote best, IReadOnlyList<ZoneShare> mix, decimal itemPrice, decimal? unitCost, FeeProfile fees)
    {
        if (itemPrice <= 0m) return [];

        decimal CostIn(int zone) => best.ZoneCosts.GetValueOrDefault(zone, best.ExpectedCost);

        var expected = best.ExpectedCost;
        var near = best.NearestZoneCost;
        var far = best.FarthestZoneCost;

        // The buyer's all-in total, held constant across every mode: the item at its asking price
        // plus what an average label costs. Everything below is a different way of splitting it.
        var outlay = Math.Round(itemPrice + expected, 2);

        var modes = new List<ShipModeOption>
        {
            Mode("free_expected", "Free shipping, priced at your average cost",
                "The listing says free shipping and the price carries an average label. Near buyers pay for the far ones.",
                itemPrice: outlay, buyerShipping: 0m, collected: expected),

            Mode("free_worst_case", "Free shipping, priced so it never loses",
                "The listing says free shipping and the price carries your most expensive label. You are never underwater, and never the cheapest listing on the page.",
                itemPrice: Math.Round(itemPrice + far, 2), buyerShipping: 0m, collected: far),

            Mode("flat", "Flat shipping charge",
                "The buyer sees a fixed shipping line. Same fee to eBay as free shipping at the same total — it just moves the number.",
                itemPrice: itemPrice, buyerShipping: expected, collected: expected),

            // Note this does NOT flatten the seller's take-home completely. The buyer covers the
            // label, but eBay's cut is charged on the shipping line too, so a dearer label still
            // costs the seller the fee on the difference. Smaller exposure, not zero exposure.
            Mode("calculated", "Calculated shipping",
                "eBay charges each buyer their own zone cost. Distant buyers see a bigger total, and you keep all but the fee on the difference.",
                itemPrice: itemPrice, buyerShipping: expected, collected: decimal.MaxValue),
        };

        ShipModeOption Mode(string code, string name, string description, decimal itemPrice, decimal buyerShipping, decimal collected)
        {
            // Calculated shipping bills the buyer the actual zone cost, so the seller's label is
            // always covered; the sentinel keeps that out of the underwater arithmetic.
            var isCalculated = collected == decimal.MaxValue;

            decimal NetIn(int zone)
            {
                var label = CostIn(zone);
                var shippingCharged = isCalculated ? label : buyerShipping;
                var quote = netCalc.Quote(
                    askPrice: itemPrice, unitCost: unitCost, fees: fees,
                    buyerPaidShipping: shippingCharged, quantity: 1, shippingCostOverride: label);
                return unitCost is > 0m ? quote.NetProfit : quote.NetProceeds;
            }

            var option = new ShipModeOption
            {
                Mode = code,
                Name = name,
                Description = description,
                ItemPrice = itemPrice,
                BuyerPaidShipping = isCalculated ? 0m : buyerShipping,
                BuyerOutlayNear = Math.Round(itemPrice + (isCalculated ? near : buyerShipping), 2),
                BuyerOutlayFar = Math.Round(itemPrice + (isCalculated ? far : buyerShipping), 2),
                NetNear = NetIn(ShippingZones.NearestZone(mix)),
                NetFar = NetIn(ShippingZones.FarthestZone(mix)),
                UnderwaterBuyerPercent = isCalculated ? 0m : ShippingZones.ShareAboveCost(mix, CostIn, collected),
            };

            option.NetExpected = ShippingZones.ExpectedOver(mix, NetIn);
            return option;
        }

        Recommend(modes, best);
        return modes;
    }

    /// <summary>
    /// Picks the mode to lead with, and says why in one line per option.
    /// </summary>
    /// <remarks>
    /// The rule is about exposure, not about which mode nets most — held at a constant buyer outlay
    /// they are within pennies of each other, so picking "the highest number" would be picking
    /// noise. Free shipping wins when the zone spread is small enough that the bet is not really a
    /// bet; once the spread is material, the seller should be told to stop carrying it.
    /// </remarks>
    private static void Recommend(List<ShipModeOption> modes, ShippingServiceQuote best)
    {
        var spread = best.ZoneSpread;
        var spreadIsMaterial = spread >= 4.00m;

        var pick = best.IsFlatRate || !spreadIsMaterial ? "free_expected"
                 : spread >= 12.00m ? "calculated"
                 : "free_worst_case";

        foreach (var mode in modes)
        {
            mode.Recommended = mode.Mode == pick;
            mode.Verdict = mode.Mode switch
            {
                "free_expected" when best.IsFlatRate =>
                    "Safe here — this service costs the same everywhere, so there is no bet to lose.",
                "free_expected" when !spreadIsMaterial =>
                    $"Safe here — the label only swings {Money(spread)} across the country.",
                "free_expected" =>
                    $"Loses money on {mode.UnderwaterBuyerPercent:0.#}% of US buyers. That is the cost of the free-shipping badge.",
                "free_worst_case" when mode.Recommended =>
                    $"Keeps the free-shipping badge and never goes underwater. Costs you {Money(mode.ItemPrice - modes[0].ItemPrice)} of price competitiveness.",
                "free_worst_case" =>
                    "Never underwater, but you are pricing every nearby buyer out to protect against the far ones.",
                "flat" =>
                    "Identical fee to free shipping at the same total — eBay charges its cut on the shipping line too. Only the search placement differs.",
                "calculated" when mode.Recommended =>
                    $"The spread is {Money(spread)} and this hands almost all of it to the buyer — "
                    + $"you are left carrying {Money(mode.ZoneRisk)}, which is only eBay's fee on the difference.",
                "calculated" =>
                    $"Cuts your exposure from {Money(modes[0].ZoneRisk)} to {Money(mode.ZoneRisk)} — what is left is "
                    + "eBay's cut of the shipping line, which no mode escapes.",
                _ => "",
            };
        }
    }

    // ── Packing tips: the money that is in the box rather than the price ─────────────────────────

    private static List<PackagingTip> BuildTips(
        PackageSpec package, List<ShippingServiceQuote> quotes, ShippingServiceQuote best, IReadOnlyList<ZoneShare> mix)
    {
        var tips = new List<PackagingTip>();

        // A flat-rate box that is blocked only by size is the single most actionable tip in the app:
        // the fix is "use a different box", and it can be worth $40 on one heavy item.
        foreach (var blocked in quotes.Where(q => !q.Eligible && q.IsFlatRate && q.IneligibleReason.StartsWith("Does not fit")))
        {
            var flatPrice = FlatPriceOf(blocked);
            var saving = Math.Round(best.ExpectedCost - flatPrice, 2);
            if (saving < MinimumTipSaving) continue;
            if (!CouldBeRepackedInto(package, blocked.Code)) continue;

            tips.Add(new PackagingTip
            {
                Kind = "flat_rate_within_reach",
                Headline = $"Repack into the {blocked.Name} and save {Money(saving)} a sale",
                Detail = $"Your box is {package.LengthIn:0.#}\" x {package.WidthIn:0.#}\" x {package.HeightIn:0.#}\". "
                       + $"{blocked.IneligibleReason} At {package.WeightLb:0.#} lb, flat rate would cost {Money(flatPrice)} to anywhere in the country "
                       + $"against {Money(best.ExpectedCost)} on {best.Name}.",
                SavingPerSale = saving,
            });
        }

        // Dimensional weight: the seller is paying to ship air and there is nothing on eBay's
        // listing screen that would ever tell them.
        if (best.DimWeightApplied && package.HasDimensions)
        {
            var billedLb = Math.Round(best.BillableWeightOz / 16m, 1);
            var realLb = Math.Round(package.WeightLb, 1);
            var shrunk = package.Clone();
            shrunk.HeightIn = Math.Max(1m, Math.Round(package.HeightIn * 0.7m, 1));
            var shrunkQuotes = ShippingRateBook.QuoteAll(shrunk, mix).Where(q => q.Eligible).OrderBy(q => q.ExpectedCost).ToList();
            var saving = shrunkQuotes.Count > 0 ? Math.Round(best.ExpectedCost - shrunkQuotes[0].ExpectedCost, 2) : 0m;

            if (saving >= MinimumTipSaving)
                tips.Add(new PackagingTip
                {
                    Kind = "dim_weight",
                    Headline = $"You are being billed for {billedLb} lb of air on a {realLb} lb item",
                    Detail = $"The box is {package.VolumeCubicIn:0} cubic inches, so {best.Carrier} charges dimensional weight instead of real weight. "
                           + $"Taking about 30% off the height gets that back — worth {Money(saving)} a sale.",
                    SavingPerSale = saving,
                });
        }

        if (best.SurchargeAmount > 0m)
            tips.Add(new PackagingTip
            {
                Kind = "surcharge",
                Headline = $"{Money(best.SurchargeAmount)} of this label is an oversize surcharge",
                Detail = best.SurchargeReason + " Getting the longest side under 30\" removes it, if the item allows.",
                SavingPerSale = best.SurchargeAmount,
            });

        // The cheapest service is not always the one a seller reaches for. Naming the runner-up
        // and its price turns "use Ground Advantage" into a checkable claim.
        var runnerUp = quotes.FirstOrDefault(q => q.Eligible && q.Code != best.Code);
        if (runnerUp is not null && runnerUp.ExtraVsBest >= MinimumTipSaving)
            tips.Add(new PackagingTip
            {
                Kind = "service_choice",
                Headline = $"{best.Name} beats {runnerUp.Name} by {Money(runnerUp.ExtraVsBest)} a sale",
                Detail = $"{Money(best.ExpectedCost)} against {Money(runnerUp.ExpectedCost)} for an average US buyer, "
                       + $"at {best.BillableWeightOz / 16m:0.##} lb billable.",
                SavingPerSale = runnerUp.ExtraVsBest,
            });

        return tips.OrderByDescending(t => t.SavingPerSale).ToList();
    }

    private static decimal FlatPriceOf(ShippingServiceQuote quote) => quote.Code switch
    {
        "usps_flat_padded" => 9.85m,
        "usps_flat_small" => 10.10m,
        "usps_flat_medium" => 17.60m,
        "usps_flat_large" => 23.50m,
        _ => 0m,
    };

    /// <summary>Interior volume of each flat-rate container, in cubic inches.</summary>
    private static decimal FlatVolumeOf(string code) => code switch
    {
        "usps_flat_padded" => 12.5m * 9.5m * 0.75m,
        "usps_flat_small" => 8.6m * 5.4m * 1.6m,
        "usps_flat_medium" => 11m * 8.5m * 5.5m,
        "usps_flat_large" => 12m * 12m * 5.5m,
        _ => 0m,
    };

    /// <summary>
    /// Whether "just use the flat-rate box" is advice a person could actually follow.
    /// </summary>
    /// <remarks>
    /// The rate book rejects a flat-rate box on a strict dimension check, which is the right rule
    /// for pricing and the wrong one for advice: a 14 x 12 x 10 carton is technically only "too big"
    /// for the padded envelope, but suggesting the seller repack ten inches of depth into
    /// three-quarters of an inch is nonsense that costs the board its credibility. Comparing volume
    /// with a little slack keeps the tip to boxes that are genuinely one size away — loose contents
    /// that would settle into a smaller carton — and drops the rest.
    /// </remarks>
    private static bool CouldBeRepackedInto(PackageSpec package, string flatRateCode)
    {
        var boxVolume = FlatVolumeOf(flatRateCode);
        if (boxVolume <= 0m || !package.HasDimensions) return false;

        const decimal SlackFactor = 1.4m;
        return package.VolumeCubicIn <= boxVolume * SlackFactor;
    }

    private static (string Headline, string Note) Describe(ShippingRecommendation result, ShippingServiceQuote best)
    {
        var estimated = result.Package.Source != "measured";
        var caveat = estimated
            ? " Based on an estimated package — weigh it to make this exact."
            : "";

        if (result.ItemPrice > 0m && result.ShippingLoadPercent >= HeavyShippingLoadPercent)
            return ($"{Money(best.ExpectedCost)} to ship — {result.ShippingLoadPercent:0.#}% of the asking price",
                // Billable, not actual. On a dimensional-weight item these differ by an order of
                // magnitude, and the actual weight is precisely the number that makes the label
                // look inexplicable.
                $"Shipping is eating this listing. {best.Name} is the cheapest label available at "
                + $"{best.BillableWeightOz / 16m:0.##} lb billable, and it still takes {result.ShippingLoadPercent:0.#}% of {Money(result.ItemPrice)}. "
                + "Either the price is too low for the weight, or this item wants to be sold locally or bundled." + caveat);

        var spreadNote = best.IsFlatRate
            ? "It costs the same to every state, which is why it wins."
            : $"Anywhere from {Money(best.NearestZoneCost)} nearby to {Money(best.FarthestZoneCost)} across the country.";

        return ($"{Money(best.ExpectedCost)} to ship, on average",
            $"{best.Name} at {best.BillableWeightOz / 16m:0.##} lb billable. {spreadNote}" + caveat);
    }

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
