using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns stored flips into the numbers the Money Made page shows. Pure — no I/O, no clock of its
/// own — so every rule below is testable and the totals are reproducible.
/// </summary>
/// <remarks>
/// This is the only screen in the app that reports the past, which changes what "correct" means.
/// Everywhere else an optimistic estimate costs the seller a bad decision they can still walk away
/// from; here an optimistic total is a claim that money exists which does not. So the rules run one
/// direction only:
///
///   * A sale with no cost basis contributes NOTHING to profit. It is reported separately, with
///     what it would add once the cost is entered. Treating proceeds as profit is what makes most
///     "you've earned $X" dashboards worthless.
///   * eBay's own fee figure is used when eBay reported it, and the estimate is flagged when not.
///   * Return and testing reserves — which every forward-looking screen in the app applies — are
///     NOT applied here. A reserve prices the risk of a return that hasn't happened yet; on a sale
///     that completed, the refund column is the actual outcome, and charging both would understate
///     real earnings on every sale that went fine.
///   * A free-shipped sale with no recorded shipping cost is counted, but flagged and totalled, so
///     the one assumption that flatters the number is visible rather than buried.
/// </remarks>
public sealed class EarningsCalculator(ProfitCalculator profitCalculator)
{
    /// <summary>Works out the money on one sale.</summary>
    /// <param name="costBasis">
    /// The shared cost-basis row for this listing/SKU, if the seller has entered one anywhere in
    /// the app. A per-flip <see cref="FlipRecord.UnitCost"/> wins over it — that is a figure typed
    /// against this specific sale, and the shared basis is the standing default.
    /// </param>
    public FlipProfit Compute(FlipRecord flip, CostBasisEntry? costBasis, FeeProfile fees)
    {
        var quantity = Math.Max(1, flip.Quantity);
        var result = new FlipProfit { Flip = flip };

        // ── Revenue ──────────────────────────────────────────────────────────────────────────
        // A cancelled order never produced money and a fully refunded one gave it all back, so both
        // are zeroed rather than carried as a sale with a small residue.
        var buyerPaid = flip.SalePrice + flip.ShippingCharged;
        var refunded = Math.Clamp(flip.RefundedAmount, 0m, buyerPaid);
        var cancelled = flip.Status is "cancelled";
        var gross = cancelled ? 0m : Math.Round(buyerPaid - refunded, 2);
        result.GrossRevenue = gross;

        // ── Fees ─────────────────────────────────────────────────────────────────────────────
        if (flip.MarketplaceFee.HasValue)
        {
            result.Fees = Math.Round(flip.MarketplaceFee.Value, 2);
            result.FeesAreActual = true;
        }
        else
        {
            result.Fees = EstimateFees(flip.SalePrice, flip.ShippingCharged, fees);
            result.FeesAreActual = false;
            result.Caveats.Add($"eBay didn't report a fee for this sale, so {Money(result.Fees)} is estimated from your fee settings.");
        }

        // A refunded sale usually gets its fee back too. Scaling the fee by the share of the sale
        // that stuck is closer than either extreme: charging the full fee on a cancelled order
        // invents a loss, and waiving it entirely on a partial refund invents profit.
        if (gross < buyerPaid && buyerPaid > 0)
            result.Fees = Math.Round(result.Fees * (gross / buyerPaid), 2);

        // ── Shipping ─────────────────────────────────────────────────────────────────────────
        // Three cases, in the order that keeps the total honest:
        //   1. The seller recorded what it cost. Use it.
        //   2. They configured a default shipping cost in Fees & Costs. Use that — it is their number.
        //   3. Nothing recorded. If the buyer paid shipping, assume it cost what they paid (a
        //      pass-through, so it adds nothing to profit). If shipping was free, the cost is a
        //      genuine unknown that is inflating this row, and it is flagged and counted.
        if (flip.ShippingCost.HasValue)
        {
            result.ShippingCost = Math.Round(flip.ShippingCost.Value, 2);
        }
        else if (fees.DefaultShippingCost > 0)
        {
            result.ShippingCost = Math.Round(fees.DefaultShippingCost, 2);
            result.ShippingCostAssumed = true;
            result.Caveats.Add($"Shipping cost not recorded — using your {Money(result.ShippingCost)} default.");
        }
        else if (flip.ShippingCharged > 0)
        {
            result.ShippingCost = Math.Round(flip.ShippingCharged, 2);
            result.ShippingCostAssumed = true;
            result.Caveats.Add($"Shipping cost not recorded — assumed it cost the {Money(flip.ShippingCharged)} the buyer paid, so it adds nothing to profit.");
        }
        else
        {
            result.ShippingCost = 0m;
            result.ShippingCostUnknown = true;
            result.Caveats.Add("Shipped free with no shipping cost recorded — your real profit is lower by whatever postage cost.");
        }

        if (cancelled) { result.ShippingCost = 0m; result.ShippingCostUnknown = false; result.ShippingCostAssumed = false; }

        result.OtherCosts = Math.Round(Math.Max(0m, flip.OtherCosts), 2);

        // Packaging and labour are real per-sale costs the seller already told the app about; they
        // default to zero, so a seller who hasn't set them isn't charged for them.
        var overhead = cancelled ? 0m : Math.Round(fees.DefaultPackagingCost + fees.DefaultLaborCost, 2);

        result.NetProceeds = Math.Round(gross - result.Fees - result.ShippingCost - result.OtherCosts - overhead, 2);

        // ── Cost of goods — the number that decides whether this counts at all ────────────────
        decimal? unitCost = flip.UnitCost;
        if (unitCost.HasValue) result.CostSource = "flip";
        else if (costBasis is not null) { unitCost = costBasis.TotalUnitCost; result.CostSource = "basis"; }
        else result.CostSource = "none";

        if (unitCost is null)
        {
            result.NetProfit = null;
            result.Caveats.Add("No cost recorded for this item, so it isn't counted in your profit yet.");
            return result;
        }

        result.CostOfGoods = Math.Round(unitCost.Value * quantity, 2);
        result.NetProfit = Math.Round(result.NetProceeds - result.CostOfGoods.Value, 2);

        // ROI is measured against the cost of the goods alone, matching ProfitCalculator, so the
        // return a seller sees after the sale is directly comparable to the one the app quoted them
        // before the buy. A free item has no cost to return on, so ROI is undefined rather than 0.
        result.RoiPercent = result.CostOfGoods > 0
            ? Math.Round(result.NetProfit.Value / result.CostOfGoods.Value * 100m, 1) : null;
        result.MarginPercent = gross > 0
            ? Math.Round(result.NetProfit.Value / gross * 100m, 1) : null;

        return result;
    }

    /// <summary>
    /// The marketplace fee this app would have predicted for a sale at this price, via the same
    /// <see cref="ProfitCalculator"/> every other money screen uses.
    /// </summary>
    /// <remarks>
    /// Routed through ProfitCalculator rather than re-deriving the percentages here so there is
    /// exactly one fee model in the app: if the seller changes their final value fee, the earnings
    /// estimate moves with every forecast on every other screen.
    /// </remarks>
    private decimal EstimateFees(decimal salePrice, decimal shippingCharged, FeeProfile fees)
    {
        var breakdown = profitCalculator.Calculate(
            supplierUnitCost: 0m, quantity: 1, expectedSalePrice: salePrice, quickSalePrice: salePrice,
            buyerPaidShipping: shippingCharged, fees: fees);

        return Math.Round(breakdown.EbayFees + breakdown.PromotedListingFees + breakdown.PaymentProcessingFees, 2);
    }

    /// <summary>Rolls a set of computed flips into the headline, the chart and the leaderboards.</summary>
    /// <param name="now">
    /// The seller's local "now". Months are bucketed in this offset, not UTC — a sale at 7pm on the
    /// 31st belongs to the month the seller thinks it happened in.
    /// </param>
    public EarningsResult Summarize(IReadOnlyList<FlipProfit> flips, DateTimeOffset now)
    {
        var result = new EarningsResult { Flips = flips.ToList() };
        var summary = result.Summary;

        // Cancelled orders are excluded from every count as well as every total: they are not sales
        // that made nothing, they are sales that didn't happen.
        var live = flips.Where(f => f.Status != "cancelled").ToList();
        var counted = live.Where(f => f.CountsTowardProfit).ToList();

        summary.SalesAllTime = live.Count;
        summary.UnitsAllTime = live.Sum(f => Math.Max(1, f.Quantity));
        summary.GrossRevenueAllTime = Round(live.Sum(f => f.GrossRevenue));
        summary.FeesAllTime = Round(live.Sum(f => f.Fees));
        summary.ShippingCostAllTime = Round(live.Sum(f => f.ShippingCost));
        summary.CostOfGoodsAllTime = Round(counted.Sum(f => f.CostOfGoods ?? 0m));
        summary.SalesRefunded = live.Count(f => f.Status == "refunded" || f.Flip.RefundedAmount > 0);
        summary.RefundedAmount = Round(live.Sum(f => Math.Min(f.Flip.RefundedAmount, f.Flip.SalePrice + f.Flip.ShippingCharged)));
        summary.SalesWithUnknownShipping = live.Count(f => f.ShippingCostUnknown);

        summary.NetProfitAllTime = Round(counted.Sum(f => f.NetProfit ?? 0m));
        summary.ProfitFromActualFees = Round(counted.Where(f => f.FeesAreActual).Sum(f => f.NetProfit ?? 0m));
        summary.ProfitFromEstimatedFees = Round(summary.NetProfitAllTime - summary.ProfitFromActualFees);
        summary.ProfitFromEbay = Round(counted.Where(f => f.Source == "ebay").Sum(f => f.NetProfit ?? 0m));
        summary.ProfitFromManual = Round(summary.NetProfitAllTime - summary.ProfitFromEbay);
        summary.FeesMeasured = Round(live.Where(f => f.FeesAreActual).Sum(f => f.Fees));
        summary.SalesWithMeasuredFees = live.Count(f => f.FeesAreActual);

        var awaiting = live.Where(f => !f.NetProfit.HasValue).ToList();
        summary.SalesAwaitingCost = awaiting.Count;
        summary.ProceedsAwaitingCost = Round(awaiting.Sum(f => f.NetProceeds));

        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var lastMonthStart = monthStart.AddMonths(-1);
        var yearStart = new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset);

        summary.NetProfitThisMonth = Round(counted.Where(f => Local(f, now) >= monthStart).Sum(f => f.NetProfit ?? 0m));
        summary.NetProfitLastMonth = Round(counted.Where(f => Local(f, now) >= lastMonthStart && Local(f, now) < monthStart).Sum(f => f.NetProfit ?? 0m));
        summary.NetProfitLast30Days = Round(counted.Where(f => Local(f, now) >= now.AddDays(-30)).Sum(f => f.NetProfit ?? 0m));
        summary.NetProfitThisYear = Round(counted.Where(f => Local(f, now) >= yearStart).Sum(f => f.NetProfit ?? 0m));
        summary.SalesThisMonth = live.Count(f => Local(f, now) >= monthStart);

        // Percent change needs a non-zero, non-negative base. Against a losing or empty month there
        // is no meaningful percentage, and inventing one ("+∞%") is noise.
        summary.MonthOverMonthPercent = summary.NetProfitLastMonth > 0
            ? Math.Round((summary.NetProfitThisMonth - summary.NetProfitLastMonth) / summary.NetProfitLastMonth * 100m, 1)
            : null;

        if (counted.Count > 0)
        {
            summary.AverageProfitPerSale = Math.Round(summary.NetProfitAllTime / counted.Count, 2);
            // Capital-weighted, not the mean of the per-sale percentages: one $4 flip that returned
            // 900% would otherwise drag the seller's "average ROI" far above what their money
            // actually earned.
            if (summary.CostOfGoodsAllTime > 0)
                summary.AverageRoiPercent = Math.Round(summary.NetProfitAllTime / summary.CostOfGoodsAllTime * 100m, 1);
            var countedRevenue = counted.Sum(f => f.GrossRevenue);
            if (countedRevenue > 0)
                summary.AverageMarginPercent = Math.Round(summary.NetProfitAllTime / countedRevenue * 100m, 1);
        }

        if (live.Count > 0)
        {
            summary.FirstSaleUtc = live.Min(f => f.SoldUtc);
            summary.LastSaleUtc = live.Max(f => f.SoldUtc);
        }

        result.Months = BuildMonths(live, counted, now);
        var best = result.Months.Where(m => m.NetProfit > 0).OrderByDescending(m => m.NetProfit).FirstOrDefault();
        if (best is not null) { summary.BestMonthLabel = best.Label; summary.BestMonthProfit = best.NetProfit; }

        // Profitable flips only. Ranked without that filter, a seller whose sales all lost money
        // gets their least-bad loss presented under a trophy — which is worse than an empty list,
        // because it dresses a loss up as the best thing they did.
        result.BestFlips = counted.Where(f => (f.NetProfit ?? 0m) > 0)
            .OrderByDescending(f => f.NetProfit ?? 0m).Take(8).ToList();
        // The mirror image, and the reason it exists: a sale that lost money is the most useful row
        // on this page, and hiding it would make the tracker a highlight reel.
        result.WorstFlips = counted.Where(f => (f.NetProfit ?? 0m) < 0)
            .OrderBy(f => f.NetProfit ?? 0m).Take(8).ToList();
        // Ranked by ROI but with a floor on the profit, because the best *return* on a $3 buy is
        // usually a rounding error the seller can't repeat at scale.
        result.BestReturns = counted
            .Where(f => f.RoiPercent.HasValue && (f.NetProfit ?? 0m) >= 10m)
            .OrderByDescending(f => f.RoiPercent!.Value).Take(8).ToList();
        result.AwaitingCost = awaiting.OrderByDescending(f => f.NetProceeds).Take(25).ToList();

        result.Honesty = BuildHonesty(summary);
        return result;
    }

    // Twelve buckets, trimmed at the front so a seller two months in doesn't get ten empty bars —
    // but never below four, or the chart stops reading as a time series at all.
    private static List<EarningsMonthPoint> BuildMonths(
        IReadOnlyList<FlipProfit> live, IReadOnlyList<FlipProfit> counted, DateTimeOffset now)
    {
        var currentMonth = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);
        var months = new List<EarningsMonthPoint>();

        for (var i = 11; i >= 0; i--)
        {
            var start = currentMonth.AddMonths(-i);
            var end = start.AddMonths(1);
            var inMonth = live.Where(f => Local(f, now) >= start && Local(f, now) < end).ToList();

            months.Add(new EarningsMonthPoint
            {
                Month = start.ToString("yyyy-MM"),
                Label = start.ToString("MMM yyyy"),
                NetProfit = Round(counted.Where(f => Local(f, now) >= start && Local(f, now) < end).Sum(f => f.NetProfit ?? 0m)),
                GrossRevenue = Round(inMonth.Sum(f => f.GrossRevenue)),
                Sales = inMonth.Count,
                SalesAwaitingCost = inMonth.Count(f => !f.NetProfit.HasValue),
                IsCurrentMonth = i == 0,
            });
        }

        var firstWithData = months.FindIndex(m => m.Sales > 0);
        if (firstWithData <= 0) return months;

        var keepFrom = Math.Min(firstWithData, months.Count - 4);
        return months.Skip(Math.Max(0, keepFrom)).ToList();
    }

    // Every sentence here is something that would otherwise have to be inferred from a number being
    // smaller than the seller expected. Said out loud, an incomplete total reads as honest; left
    // unsaid, it reads as broken.
    private static List<string> BuildHonesty(EarningsSummary s)
    {
        var lines = new List<string>();

        if (s.SalesAwaitingCost > 0)
            lines.Add($"{s.SalesAwaitingCost} sale{(s.SalesAwaitingCost == 1 ? "" : "s")} worth {Money(s.ProceedsAwaitingCost)} after fees {(s.SalesAwaitingCost == 1 ? "isn't" : "aren't")} counted above, because there's no record of what you paid. Enter the cost and the total goes up by whatever you actually made.");

        // Stated over FEES, not over profit. A sale can have eBay's real fee on it and still
        // contribute no profit because its cost hasn't been entered — reporting the split by profit
        // would then announce "$0 uses eBay's real fees" on an account where every fee was measured.
        if (s.SalesAllTime > 0)
        {
            if (s.SalesWithMeasuredFees == s.SalesAllTime)
                lines.Add($"All {Money(s.FeesAllTime)} of fees above is what eBay actually charged, not an estimate.");
            else if (s.SalesWithMeasuredFees == 0)
                lines.Add($"eBay hasn't reported fees on any of these sales, so the {Money(s.FeesAllTime)} above is estimated from your Fees & Costs settings. Importing your eBay orders replaces the estimate with the real charge.");
            else
                lines.Add($"{Money(s.FeesMeasured)} of the {Money(s.FeesAllTime)} in fees is eBay's own figure, across {s.SalesWithMeasuredFees} of {s.SalesAllTime} sales. The rest is estimated from your Fees & Costs settings.");
        }

        if (s.ProfitFromManual != 0 && s.ProfitFromEbay != 0)
            lines.Add($"{Money(s.ProfitFromEbay)} came from imported eBay orders; {Money(s.ProfitFromManual)} from flips you logged yourself.");

        if (s.SalesWithUnknownShipping > 0)
            lines.Add($"{s.SalesWithUnknownShipping} sale{(s.SalesWithUnknownShipping == 1 ? " shipped" : "s shipped")} free with no postage cost recorded, so {(s.SalesWithUnknownShipping == 1 ? "it is" : "they are")} counted at full value. Set a default shipping cost in Fees & Costs to fix that.");

        if (s.RefundedAmount > 0)
            lines.Add($"{Money(s.RefundedAmount)} in refunds has already been taken back out.");

        lines.Add("Sales tax eBay collects is excluded throughout — it never reaches you. Return and testing reserves aren't charged here either: these sales already completed, so refunds are counted as what actually happened rather than as a percentage set aside.");

        return lines;
    }

    private static DateTimeOffset Local(FlipProfit flip, DateTimeOffset now) => flip.SoldUtc.ToOffset(now.Offset);

    private static decimal Round(decimal value) => Math.Round(value, 2);

    private static string Money(decimal value) => value.ToString("C0", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
