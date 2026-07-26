using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns a priced manifest plus an ask price into a buy/skip decision.
///
/// The pricing itself is not done here — every line goes through the same
/// <c>AnalyzeProductAsync</c> stack (sold-comps database → ComparableMatcher →
/// MarketPriceEstimator → Terapeak blend) the rest of the app uses, arriving as a
/// <see cref="ResalePricing"/>. What this class owns is the part that is specific to buying a
/// whole lot at once, and that no per-item tool answers:
///
///   * how many of the units on a manifest are actually sellable, by grade;
///   * every unit's eBay fees and shipping, which is what quietly kills pallet math —
///     200 items at $8 to ship is $1,600 the spread sheet never showed;
///   * the ask price spread across the lines pro rata, so a per-line profit means something;
///   * the highest ask that still clears the seller's ROI, solved exactly rather than guessed;
///   * which handful of lines actually carry the value, because that is what the buyer is
///     really paying for and what they need to inspect before handing over money.
///
/// Everything here is pure except <see cref="BuildLine"/>, which delegates the money to the
/// shared <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/> — a unit out of a pallet is
/// costed by exactly the same rules as a dropship, a local flip or a listing being repriced.
/// </summary>
public sealed class LotAnalyzer(ProfitCalculator profitCalc)
{
    // Below this share of the manifest's own claimed value, a verdict is a coin flip and is
    // reported as one rather than dressed up as a decision.
    public const decimal MinCoverageForVerdict = 40m;
    public const decimal MinCoverageForConfidence = 60m;
    // The same evidence bar the local-arbitrage verdicts and the repricer use.
    public const int MinCompsForConfidence = 3;
    // Enough sold history to settle a disagreement between the comps and the manifest's own
    // retail column — the same "5 comps" bar Roll the Dice uses before it will show a product.
    public const int GoodEvidenceComps = 5;

    /// <summary>
    /// Recovery assumptions per lot grade.
    ///
    /// Two separate numbers, because they are two separate risks and folding them together
    /// hides both. <see cref="LotGradeAssumption.SellableRatePercent"/> is how many units are
    /// worth listing at all — the dead, the missing and the empty boxes. PriceFactorPercent is
    /// what the survivors fetch against the sold comps, which are mostly used-goods sales
    /// already, so it stays mild by design; the deep discount on a returns pallet lives in the
    /// units that never sell, not in a fictional haircut on the ones that do.
    ///
    /// These are published-industry starting points, not measurements of any particular
    /// supplier. Both are shown on screen and both can be overridden — a buyer who has opened
    /// forty of these pallets knows their own numbers better than any default.
    /// </summary>
    public static readonly LotGradeAssumption[] Grades =
    [
        new() { Id = "new", Label = "New / sealed", SellableRatePercent = 98m, PriceFactorPercent = 100m,
                Note = "Factory-sealed overstock. Almost everything sells; the comps are the price." },
        new() { Id = "shelf_pull", Label = "Shelf pulls / overstock", SellableRatePercent = 95m, PriceFactorPercent = 96m,
                Note = "Unsold retail stock. Packaging wear, product fine." },
        new() { Id = "open_box", Label = "Open box", SellableRatePercent = 90m, PriceFactorPercent = 90m,
                Note = "Opened but complete. Missing accessories are the usual loss." },
        new() { Id = "customer_returns", Label = "Customer returns (tested)", SellableRatePercent = 80m, PriceFactorPercent = 88m,
                Note = "Returns the supplier has sorted. Roughly one in five is not resellable." },
        new() { Id = "uninspected_returns", Label = "Customer returns (uninspected)", SellableRatePercent = 65m, PriceFactorPercent = 85m,
                Note = "Nobody has opened these. A third is the industry's usual write-off." },
        new() { Id = "mixed", Label = "Mixed / as-is / untested", SellableRatePercent = 60m, PriceFactorPercent = 82m,
                Note = "Unknown condition. Assume you are testing every unit yourself." },
        new() { Id = "salvage", Label = "Salvage / damaged", SellableRatePercent = 40m, PriceFactorPercent = 65m,
                Note = "Bought for the parts and the survivors. Most of the manifest is scrap." },
    ];

    public static LotGradeAssumption GradeFor(string? id) =>
        Grades.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Grades.First(g => g.Id == "customer_returns");

    /// <summary>Grade defaults with the seller's own overrides applied and clamped to 0-100.</summary>
    public static LotGradeAssumption Assumptions(string? gradeId, decimal? sellableOverride, decimal? priceFactorOverride)
    {
        var grade = GradeFor(gradeId);
        var sellable = Clamp(sellableOverride ?? grade.SellableRatePercent, 0m, 100m);
        var factor = Clamp(priceFactorOverride ?? grade.PriceFactorPercent, 0m, 100m);
        var edited = sellable != grade.SellableRatePercent || factor != grade.PriceFactorPercent;

        return new LotGradeAssumption
        {
            Id = grade.Id,
            Label = grade.Label,
            SellableRatePercent = sellable,
            PriceFactorPercent = factor,
            Note = edited ? grade.Note + " Adjusted from the default by you." : grade.Note,
        };
    }

    // ── One line ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Prices one manifest line. Cost is not charged here — the lot price is allocated across
    /// the lines afterwards (see <see cref="AllocateCost"/>), which needs every line's resale
    /// value to already be known.
    /// </summary>
    /// <param name="packQuantity">
    /// Units the line's own wording implies per unit sold ("case of 12"), from ProductNormalizer.
    /// Greater than one means the sold comps and the manifest are counting different things.
    /// </param>
    public LotLineAnalysis BuildLine(
        ManifestLine line, ResalePricing? resale, LotGradeAssumption grade, FeeProfile fees,
        decimal perUnitHandlingCost, int packQuantity = 1)
    {
        var qty = Math.Max(1, line.Quantity);
        var row = new LotLineAnalysis
        {
            Description = line.Description,
            SearchQuery = line.SearchQuery,
            Quantity = qty,
            Condition = line.Condition,
            UnitRetail = line.UnitRetail,
            RetailTotal = Math.Round((line.UnitRetail ?? 0m) * qty, 2),
            PricedAs = resale?.LookupTitle ?? "",
        };

        if (resale is null || !resale.HasPrice)
        {
            row.Status = "no_data";
            row.StatusNote = "No eBay sold history matched this line.";
            return row;
        }

        row.SoldCompCount = resale.SoldCompCount;
        row.TerapeakCompCount = resale.TerapeakCompCount;
        row.ConfidenceScore = resale.ConfidenceScore;
        row.ConfidenceLevel = resale.ConfidenceLevel;
        row.EstimatedMonthlySales = resale.EstimatedMonthlySales;

        // Lots get liquidated, not held out for the top price, so the quick-sale figure is the
        // honest basis — you are pricing 40 units to move, not one to wait for its buyer.
        var comp = resale.QuickSale is > 0 ? resale.QuickSale!.Value
            : resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value
            : resale.Median!.Value;
        row.CompUnitPrice = Math.Round(comp, 2);

        // A "case of 12" line matched against single-unit comps is the mistake that produced a
        // 27% markdown on a working listing in the inventory scan. Multiplying by the pack size
        // would be worse than refusing: packs trade at a discount to N x the single price.
        if (packQuantity > 1)
        {
            row.Status = "excluded";
            row.ExclusionReason = $"Reads as a multi-pack ({packQuantity} per unit) — sold comps are per single item, so this line has no like-for-like price.";
            row.StatusNote = row.ExclusionReason;
            return row;
        }

        var sanity = RetailSanityCheck(row.CompUnitPrice.Value, line.UnitRetail,
            resale.SoldCompCount + resale.TerapeakCompCount);
        if (sanity is not null)
        {
            row.Status = "excluded";
            row.ExclusionReason = sanity;
            row.StatusNote = sanity;
            return row;
        }

        var unitResale = Math.Round(comp * grade.PriceFactorPercent / 100m, 2);
        row.UnitResale = unitResale;
        row.SellableUnits = Math.Round(qty * grade.SellableRatePercent / 100m, 2);

        if (unitResale <= 0m || row.SellableUnits <= 0m)
        {
            row.Status = "no_data";
            row.StatusNote = "Priced at zero after the grade's recovery assumptions.";
            return row;
        }

        // Shipping is booked on both sides: buyers paid it (revenue, and eBay charges its final
        // value fee on it) and it costs the seller real money to ship. The seller's own per-unit
        // ship-and-pack figure wins when they give one, because a pallet buyer knows their own
        // freight and box costs far better than the comps do; the comps' observed buyer-paid
        // shipping is the fallback, exactly as in every other profit path here.
        var buyerShipping = resale.AvgCompShipping;
        var shipCost = perUnitHandlingCost > 0 ? perUnitHandlingCost : buyerShipping;

        var profit = profitCalc.Calculate(
            supplierUnitCost: 0m, quantity: 1, expectedSalePrice: unitResale, quickSalePrice: unitResale,
            buyerPaidShipping: buyerShipping, fees: fees, actualShippingCostOverride: shipCost);

        var units = row.SellableUnits;
        row.GrossResale = Math.Round((unitResale + buyerShipping) * units, 2);
        row.EstimatedFees = Math.Round(profit.MarketplaceFeeTotal * units, 2);
        row.EstimatedShipCost = Math.Round(profit.FulfilmentCostTotal * units, 2);
        row.NetRecovery = Math.Round(profit.NetProfitPerUnit * units, 2);

        // How long this line ties the money up: not how long one unit takes to sell, but how
        // long the whole quantity takes to clear at the observed rate. Forty of something that
        // sells twice a month is not a fast $2,000.
        row.EstimatedDaysToSell = resale.EstimatedMonthlySales > 0
            ? (int)Math.Ceiling(units / resale.EstimatedMonthlySales * 30m)
            : resale.EstimatedDaysToSell;

        var comps = resale.SoldCompCount + resale.TerapeakCompCount;
        if (comps < MinCompsForConfidence)
        {
            row.Status = "thin";
            row.StatusNote = $"Priced on {comps} sold comp{(comps == 1 ? "" : "s")} — too few to lean on.";
        }
        else
        {
            row.Status = "priced";
            row.StatusNote = $"{comps} sold comps · match confidence: {resale.ConfidenceLevel}.";
        }

        return row;
    }

    /// <summary>
    /// The manifest's retail column is worthless as a value and excellent as a cross-check.
    /// A comp price several times the item's own stated MSRP, or a small fraction of it, means
    /// the matcher found something else — a $9 phone case priced off a $300 phone, or a $169
    /// drill kit priced off a $14 spare battery. Both directions cost money: one talks the buyer
    /// into a bad lot, the other talks them out of a good one.
    ///
    /// The low side is scaled by evidence, because that is what the first live run against the
    /// real comps database showed: a DeWalt DCD771C2 kit came back at $14 on two comps against a
    /// $169 stated retail. Two comps cannot settle a disagreement that large — five or more can,
    /// and used goods really do sell at a small fraction of MSRP. So a deep discount stands when
    /// there is history behind it and is refused when there is not. Refusing costs coverage, which
    /// is reported; using the number would cost the seller a lot they should have bought.
    /// </summary>
    public static string? RetailSanityCheck(decimal compUnitPrice, decimal? unitRetail, int compCount = int.MaxValue)
    {
        if (unitRetail is not > 0m || compUnitPrice <= 0m) return null;
        var ratio = compUnitPrice / unitRetail.Value;

        if (ratio > 3m)
            return $"Sold comps say ${compUnitPrice:N0} against a stated retail of ${unitRetail.Value:N0} — that is a mismatched product, not a bargain.";
        if (ratio < 0.05m)
            return $"Sold comps say ${compUnitPrice:N2} against a stated retail of ${unitRetail.Value:N0} — the match looks like an accessory, not the item.";
        if (ratio < 0.15m && compCount < GoodEvidenceComps)
            return $"Sold comps say ${compUnitPrice:N2} against a stated retail of ${unitRetail.Value:N0}, on only {compCount} sold comp{(compCount == 1 ? "" : "s")} — not enough history to believe a gap that wide either way.";
        return null;
    }

    // ── The lot ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Charges the all-in lot cost to the lines pro rata by their net recovery, so a per-line
    /// profit is a real number rather than a share of an average. A line that recovers nothing
    /// is allocated nothing — it did not cost you anything to have it in the pallet, it just
    /// failed to pay for itself.
    /// </summary>
    public static void AllocateCost(List<LotLineAnalysis> lines, decimal totalCost)
    {
        var contributing = lines.Where(l => l.NetRecovery > 0m).ToList();
        var totalRecovery = contributing.Sum(l => l.NetRecovery);

        foreach (var line in lines)
        {
            line.AllocatedCost = 0m;
            line.NetProfit = line.NetRecovery;
            line.RoiPercent = null;
        }
        if (totalCost <= 0m || totalRecovery <= 0m) return;

        // Cents lost to rounding go to the largest line rather than vanishing, so the lines add
        // up to the lot exactly.
        var allocated = 0m;
        for (var i = 0; i < contributing.Count; i++)
        {
            var line = contributing[i];
            var share = i == contributing.Count - 1
                ? Math.Round(totalCost - allocated, 2)
                : Math.Round(totalCost * (line.NetRecovery / totalRecovery), 2);
            allocated += share;
            line.AllocatedCost = share;
            line.NetProfit = Math.Round(line.NetRecovery - share, 2);
            line.RoiPercent = share > 0m ? Math.Round(line.NetProfit / share * 100m, 1) : null;
        }
    }

    /// <summary>Cost side of the lot: the ask, plus everything an auction adds to it.</summary>
    public static LotTotals CostOf(decimal askPrice, decimal buyerPremiumPercent, decimal salesTaxPercent, decimal freight)
    {
        var ask = Math.Max(0m, askPrice);
        var premium = Math.Round(ask * Math.Max(0m, buyerPremiumPercent) / 100m, 2);
        // Tax follows the hammer plus the premium, which is how auction houses bill it; freight
        // is invoiced separately by the carrier.
        var tax = Math.Round((ask + premium) * Math.Max(0m, salesTaxPercent) / 100m, 2);
        var freightCost = Math.Max(0m, freight);

        return new LotTotals
        {
            AskPrice = ask,
            BuyerPremium = premium,
            SalesTax = tax,
            FreightCost = freightCost,
            TotalCost = Math.Round(ask + premium + tax + freightCost, 2),
        };
    }

    public static LotTotals Summarize(
        List<LotLineAnalysis> lines, LotTotals cost, int manifestUnits, decimal manifestRetailTotal)
    {
        var totals = cost;
        totals.ManifestUnits = manifestUnits;
        totals.ManifestRetailTotal = Math.Round(manifestRetailTotal, 2);
        totals.SellableUnits = Math.Round(lines.Sum(l => l.SellableUnits), 1);
        totals.GrossResale = Math.Round(lines.Sum(l => l.GrossResale), 2);
        totals.EstimatedFees = Math.Round(lines.Sum(l => l.EstimatedFees), 2);
        totals.EstimatedShipCost = Math.Round(lines.Sum(l => l.EstimatedShipCost), 2);
        totals.NetRecovery = Math.Round(lines.Sum(l => l.NetRecovery), 2);
        totals.NetProfit = Math.Round(totals.NetRecovery - totals.TotalCost, 2);
        totals.RoiPercent = totals.TotalCost > 0m ? Math.Round(totals.NetProfit / totals.TotalCost * 100m, 1) : null;
        totals.MarginPercent = totals.GrossResale > 0m ? Math.Round(totals.NetProfit / totals.GrossResale * 100m, 1) : null;
        totals.CostPerSellableUnit = totals.SellableUnits > 0m
            ? Math.Round(totals.TotalCost / totals.SellableUnits, 2) : 0m;

        // The headline every liquidation listing leads with, tested: "$12,400 retail value" is
        // worth exactly what the items resell for, and the gap is usually enormous.
        totals.ResalePercentOfRetail = totals.ManifestRetailTotal > 0m
            ? Math.Round(totals.GrossResale / totals.ManifestRetailTotal * 100m, 1) : null;

        var days = lines.Where(l => l.EstimatedDaysToSell is > 0).Select(l => l.EstimatedDaysToSell!.Value).ToList();
        // The slowest line, because that is when the capital actually comes back — the lot is
        // not finished until the last item on it sells.
        totals.DaysToSellSlowestLine = days.Count > 0 ? days.Max() : null;
        totals.MedianDaysToSell = days.Count > 0 ? Median(days) : null;

        return totals;
    }

    /// <summary>
    /// The most the lot can cost and still clear <paramref name="targetRoiPercent"/>.
    ///
    /// Exact arithmetic, not a search: net recovery R is fixed by the manifest, and total cost
    /// scales linearly with the ask —
    ///   C(A) = A x (1 + premium) x (1 + tax) + freight
    /// so requiring (R - C) / C = r gives C = R / (1 + r) and therefore
    ///   A = (R / (1 + r) - freight) / ((1 + premium) x (1 + tax)).
    /// This is the number to bid to, and it already has the premium, the tax and the freight
    /// taken out of it — which is precisely why a hammer price that "felt fine" so often isn't.
    /// </summary>
    public static decimal? MaxAsk(decimal netRecovery, decimal buyerPremiumPercent, decimal salesTaxPercent,
        decimal freight, decimal targetRoiPercent)
    {
        if (netRecovery <= 0m) return null;
        var r = Math.Max(0m, targetRoiPercent) / 100m;
        var k = (1m + Math.Max(0m, buyerPremiumPercent) / 100m) * (1m + Math.Max(0m, salesTaxPercent) / 100m);
        if (k <= 0m) return null;

        var ask = (netRecovery / (1m + r) - Math.Max(0m, freight)) / k;
        return ask > 0m ? Math.Round(ask, 2) : null;
    }

    /// <summary>
    /// Which lines carry the lot. Sorted by net recovery, cumulative share attached, and the
    /// handful that make up 80% flagged — those are the items to physically inspect before
    /// paying, because everything else on the pallet is packing material by value.
    /// </summary>
    public static LotConcentration Concentrate(List<LotLineAnalysis> lines)
    {
        var total = lines.Sum(l => Math.Max(0m, l.NetRecovery));
        var concentration = new LotConcentration();
        if (total <= 0m) return concentration;

        var ranked = lines.Where(l => l.NetRecovery > 0m).OrderByDescending(l => l.NetRecovery).ToList();
        var cumulative = 0m;
        var reachedEighty = false;

        foreach (var line in ranked)
        {
            line.ValueSharePercent = Math.Round(line.NetRecovery / total * 100m, 1);
            cumulative += line.NetRecovery;
            line.CumulativeSharePercent = Math.Round(cumulative / total * 100m, 1);
            if (!reachedEighty)
            {
                line.CarriesTheValue = true;
                concentration.LinesForEightyPercent++;
                if (line.CumulativeSharePercent >= 80m) reachedEighty = true;
            }
        }

        concentration.TopLineSharePercent = ranked.Count > 0 ? ranked[0].ValueSharePercent : 0m;
        concentration.TopThreeSharePercent = Math.Round(
            ranked.Take(3).Sum(l => l.NetRecovery) / total * 100m, 1);

        if (ranked.Count > 1 && concentration.TopLineSharePercent >= 50m)
            concentration.Warning = $"One line is {concentration.TopLineSharePercent:0.#}% of this lot's value: \"{Shorten(ranked[0].Description)}\". " +
                                    "If that item is missing, broken or not what the manifest says, the whole lot is a loss — check it before you pay.";
        else if (ranked.Count > 3 && concentration.TopThreeSharePercent >= 80m)
            concentration.Warning = $"Three lines carry {concentration.TopThreeSharePercent:0.#}% of the value. The rest of the manifest is padding — inspect those three.";

        return concentration;
    }

    /// <summary>
    /// The call. Deliberately refuses to make one more often than it makes one: a verdict on a
    /// manifest that could only be half priced is a coin flip with a dollar sign on it.
    /// </summary>
    public static (string Verdict, string Note) Judge(
        LotTotals totals, decimal coveragePercent, int linesPriced, decimal? breakEvenAsk,
        decimal? maxBid, decimal targetRoiPercent)
    {
        if (linesPriced == 0)
            return ("no_data", "Nothing on this manifest could be priced against real sold comps. That is not a skip — it is no answer at all.");

        if (totals.AskPrice <= 0m && totals.TotalCost <= 0m)
            return ("no_ask", $"This manifest resells for about {Money(totals.GrossResale)} gross and clears {Money(totals.NetRecovery)} after fees and shipping. " +
                              "Enter what the lot costs to get a buy or skip.");

        if (totals.NetRecovery <= 0m)
            return ("dead", "Even free, this lot loses money: eBay's fees and the shipping on every unit come to more than the items resell for.");

        if (coveragePercent < MinCoverageForVerdict)
            return ("no_data", $"Only {coveragePercent:0.#}% of this manifest's value could be priced against sold comps. " +
                               "The numbers below are real for the lines that were priced, but they are not a verdict on the lot.");

        // A price ceiling built from the lines that COULD be priced is a floor, not a cap: every
        // line the app refused to value can only add to what the lot returns, never subtract. So
        // the number is safe to bid to at any coverage — but saying "bid there or walk" without
        // that caveat would talk someone out of a lot whose unpriced half was the good half.
        var floorNote = coveragePercent < MinCoverageForConfidence
            ? $" Only {coveragePercent:0.#}% of the manifest could be priced, so treat that as a floor — the unpriced lines can only push it up."
            : "";

        if (totals.NetProfit <= 0m)
            return breakEvenAsk is > 0m
                ? ("buy_below", $"Not at {Money(totals.AskPrice)}. This lot breaks even at {Money(breakEvenAsk.Value)} and clears {targetRoiPercent:0.#}% ROI at {Money(maxBid ?? 0m)} — bid there or walk.{floorNote}")
                : ("skip", $"{Money(totals.AskPrice)} is more than the lot can return once fees, shipping and the premium are paid.{floorNote}");

        var roi = totals.RoiPercent ?? 0m;

        if (coveragePercent < MinCoverageForConfidence)
            return ("thin", $"{Money(totals.NetProfit)} net at {roi:0.#}% ROI — but only {coveragePercent:0.#}% of the manifest's value could be priced. " +
                            "Treat it as a lead, not a decision.");

        if (roi >= targetRoiPercent)
            return ("buy", $"{Money(totals.NetProfit)} net profit at {roi:0.#}% ROI on {Money(totals.TotalCost)} all-in, priced against real sold comps. " +
                           $"You can pay up to {Money(maxBid ?? totals.AskPrice)} and still clear {targetRoiPercent:0.#}%.");

        return ("thin", $"Profitable but thin: {Money(totals.NetProfit)} net at {roi:0.#}% ROI, under the {targetRoiPercent:0.#}% you asked for. " +
                        $"At {Money(maxBid ?? 0m)} it would clear your target.");
    }

    /// <summary>
    /// Coverage: how much of what the manifest claims to be worth could actually be priced.
    /// Weighted by the manifest's own retail where it states one, by unit count where it does
    /// not — either way it answers "how much of this lot did we really look at?"
    /// </summary>
    public static decimal Coverage(List<LotLineAnalysis> lines)
    {
        if (lines.Count == 0) return 0m;
        var hasRetail = lines.Any(l => l.RetailTotal > 0m);

        decimal Weight(LotLineAnalysis l) => hasRetail ? l.RetailTotal : l.Quantity;
        var total = lines.Sum(Weight);
        if (total <= 0m) return 0m;

        var priced = lines.Where(l => l.Status is "priced" or "thin").Sum(Weight);
        return Math.Round(priced / total * 100m, 1);
    }

    /// <summary>
    /// Biggest money first. Lines that could not be priced or were deliberately excluded sort
    /// last but are never dropped — a manifest line the app refused to value is exactly the one
    /// the buyer needs to eyeball themselves.
    /// </summary>
    public static List<LotLineAnalysis> Rank(IEnumerable<LotLineAnalysis> lines) =>
        lines.OrderByDescending(l => l.NetRecovery > 0m)
            .ThenByDescending(l => l.NetRecovery)
            .ThenByDescending(l => l.RetailTotal)
            .ThenBy(l => l.Description, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static decimal Clamp(decimal value, decimal min, decimal max) => Math.Min(max, Math.Max(min, value));

    private static int Median(List<int> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (int)Math.Round((sorted[mid - 1] + sorted[mid]) / 2.0);
    }

    private static string Money(decimal value) => value < 0m ? $"-${Math.Abs(value):N0}" : $"${value:N0}";

    private static string Shorten(string text) => text.Length <= 60 ? text : text[..57].TrimEnd() + "…";
}
