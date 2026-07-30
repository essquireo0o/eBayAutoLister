using System.Globalization;
using System.Text;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns the same sold lines the Money Made screen reports into a Schedule C, a quarterly set-aside
/// plan, and the two figures that cost resellers the most money at tax time: proceeds with no
/// recorded cost, and what an unrecorded expense is worth. Pure — no I/O, no clock of its own.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately disagrees with <see cref="EarningsCalculator"/>, and the disagreement is the
/// feature. Money Made is conservative on purpose: a sale with no cost basis contributes nothing to
/// profit, because claiming profit that might not exist is the one direction that screen must never
/// be wrong in. The IRS applies the opposite rule to the same sale — with no cost recorded, the
/// whole of the proceeds is taxable, as though the goods were free. So the number that is honestly
/// cautious on the earnings screen is the number that costs real money on a return, and this is the
/// one place in the app that says so with a dollar figure attached.
/// </para>
/// <para>
/// The second difference is labour. Every forecast in this app charges the seller's own time against
/// profit, which is right for deciding whether a flip is worth doing. It is not deductible on a sole
/// proprietor's Schedule C — you cannot pay yourself a wage and deduct it — so it is added back here.
/// </para>
/// <para>
/// Nothing in here is tax advice and nothing is filed. Every rule below runs in the direction that
/// leaves the seller holding back slightly too much rather than slightly too little, because the
/// failure mode of the other direction is a penalty on money they already spent.
/// </para>
/// </remarks>
public sealed class TaxPackCalculator
{
    /// <summary>Net earnings from self-employment are 92.35% of net profit — the standard adjustment.</summary>
    private const decimal SelfEmploymentBase = 0.9235m;

    /// <summary>15.3% — 12.4% Social Security plus 2.9% Medicare.</summary>
    private const decimal SelfEmploymentRate = 15.3m;

    /// <summary>Below $400 of net earnings there is no self-employment tax at all.</summary>
    private const decimal SelfEmploymentFloor = 400m;

    /// <summary>The highest income-tax bracket the app will accept, so a typo can't invent a bill.</summary>
    private const decimal MaxIncomeRate = 50m;

    /// <summary>
    /// Builds the whole pack for one year.
    /// </summary>
    /// <param name="flips">Every sale on record, already costed by <see cref="EarningsCalculator"/>.</param>
    /// <param name="fees">
    /// The seller's own cost settings. Packaging is a deductible supply; labour is read from here
    /// only so it can be added back, since <see cref="FlipProfit.NetProceeds"/> has already taken it out.
    /// </param>
    /// <param name="incomeTaxRatePercent">The seller's marginal federal bracket. Only they know it.</param>
    /// <param name="requestedYear">The tax year, or null for the one currently running.</param>
    /// <param name="now">
    /// The seller's local "now". Sales are bucketed into years and quarters in this offset, not UTC:
    /// a sale at 7pm on 31 December belongs to the year the seller filed it in.
    /// </param>
    public TaxPackResult Build(
        IReadOnlyList<FlipProfit> flips, FeeProfile fees,
        decimal incomeTaxRatePercent, int? requestedYear, DateTimeOffset now)
    {
        var rate = Math.Clamp(Math.Round(incomeTaxRatePercent, 2), 0m, MaxIncomeRate);

        // Cancelled orders are not sales that made nothing; they are sales that did not happen, and
        // they are excluded from every figure here exactly as they are from Money Made.
        var allSales = flips
            .Where(f => f.Status != "cancelled")
            .Select(f => ToSale(f, fees, now))
            .ToList();

        var years = allSales.Select(s => s.SoldLocal.Year).Distinct().ToList();
        if (!years.Contains(now.Year)) years.Add(now.Year);
        years.Sort((a, b) => b.CompareTo(a));

        // A year outside living memory is a bad query string rather than a request, but a year with
        // no sales in it is a perfectly reasonable thing to ask about and answers "nothing yet".
        var year = requestedYear is int asked && asked >= 2000 && asked <= now.Year + 1
            ? asked : now.Year;

        var sales = allSales.Where(s => s.SoldLocal.Year == year).ToList();

        var result = new TaxPackResult
        {
            Year = year,
            HasSales = sales.Count > 0,
            YearInProgress = year >= now.Year,
            AvailableYears = years,
            Assumptions = new TaxAssumptions
            {
                IncomeTaxRatePercent = rate,
                SelfEmploymentRatePercent = SelfEmploymentRate,
            },
        };

        result.ScheduleC = BuildScheduleC(sales, out var netProfit);
        result.NetProfit = netProfit;

        // Money Made's figure for the same year, so the two can be told apart on purpose. Without
        // this line the seller finds two different "net profit" numbers in one app and assumes one
        // of them is a bug.
        result.EarningsNetProfit = Round(sales
            .Where(s => s.Flip.CountsTowardProfit)
            .Sum(s => s.Flip.NetProfit ?? 0m));

        ApplyTax(result, netProfit, rate);
        result.CostGap = BuildCostGap(sales, result.Assumptions.DeductionValuePercent, result.TotalTax);
        result.Form1099K = Build1099KCheck(sales, year);

        // Shared out on the unrounded ratio, not on EffectiveRatePercent: a rate rounded to a tenth
        // of a percent puts the four payments a couple of dollars off the bill they are supposed to
        // add up to, and a plan that doesn't land is a plan the seller stops trusting.
        result.Quarters = BuildQuarters(sales, year, now,
            netProfit > 0 ? result.TotalTax / netProfit : 0m);

        var deductions = result.ScheduleC
            .Where(l => l.Line is "4" or "10" or "22" or "27a")
            .Sum(l => l.Amount);
        result.DeductionsTotal = Round(deductions);
        result.TaxSavedByDeductions = Round(deductions * result.Assumptions.DeductionValuePercent / 100m);

        result.Honesty = BuildHonesty(result, sales, fees);

        result.SummaryCsv = BuildSummaryCsv(result);
        result.SummaryCsvFilename = $"schedule-c-{year}-ING.csv";
        result.LedgerCsv = BuildLedgerCsv(sales);
        result.LedgerCsvFilename = $"sales-ledger-{year}-ING.csv";

        return result;
    }

    // ── One sale, restated in the terms a return uses ────────────────────────────────────────
    // FlipProfit already holds every dollar this needs; what changes is which of them are allowed
    // to reduce the taxable figure. Packaging is a deductible supply and stays; labour comes back.
    private static TaxSale ToSale(FlipProfit flip, FeeProfile fees, DateTimeOffset now)
    {
        var buyerPaid = flip.Flip.SalePrice + flip.Flip.ShippingCharged;
        var refunded = Math.Clamp(flip.Flip.RefundedAmount, 0m, buyerPaid);

        // A sale that was refunded in full is one the goods came back from. It still belongs on
        // lines 1 and 2 — eBay reports it in gross payments and this is where it gets backed out —
        // but the item is stock again, not a cost of goods SOLD. Deducting it would claim a loss on
        // something sitting on the shelf, which is the one direction this feature must not run in.
        // A PARTIAL refund is a price concession: the buyer kept the item, so its cost still counts.
        var voided = flip.Status == "refunded" || (buyerPaid > 0m && refunded >= buyerPaid);

        return new TaxSale(
            Flip: flip,
            SoldLocal: flip.SoldUtc.ToOffset(now.Offset),
            GrossReceipts: Round(buyerPaid),
            Returns: Round(refunded),
            Fees: Round(flip.Fees),
            ShippingCost: Round(flip.ShippingCost),
            OtherCosts: Round(flip.OtherCosts),
            Packaging: Round(Math.Max(0m, fees.DefaultPackagingCost)),
            Labor: Round(Math.Max(0m, fees.DefaultLaborCost)),
            CostOfGoods: flip.CostOfGoods,
            Voided: voided);
    }

    /// <summary>One sale with the figures a Schedule C is built from.</summary>
    private sealed record TaxSale(
        FlipProfit Flip, DateTimeOffset SoldLocal,
        decimal GrossReceipts, decimal Returns, decimal Fees,
        decimal ShippingCost, decimal OtherCosts, decimal Packaging, decimal Labor,
        decimal? CostOfGoods, bool Voided)
    {
        /// <summary>What actually came in after refunds — line 3's share of this sale.</summary>
        public decimal NetReceipts => GrossReceipts - Returns;

        /// <summary>Every deductible expense on this sale. Labour is not among them, by design.</summary>
        public decimal Expenses => Fees + ShippingCost + OtherCosts + Packaging;

        /// <summary>Line 4's share of this sale. Nothing, when the goods came back.</summary>
        public decimal DeductibleCost => Voided ? 0m : (CostOfGoods ?? 0m);

        /// <summary>This sale's contribution to line 31. Summing these IS line 31.</summary>
        public decimal ScheduleCNet => NetReceipts - Expenses - DeductibleCost;

        /// <summary>
        /// True when nothing records what the goods cost, so all of it is taxable profit. A refunded
        /// sale is not missing a cost — it has no cost of sale to record.
        /// </summary>
        public bool CostMissing => !Voided && CostOfGoods is null;
    }

    // ── The form ─────────────────────────────────────────────────────────────────────────────
    // Line numbers, not categories, because the seller (or their accountant) is copying these into
    // a form that is laid out this way. Every line carries the arithmetic that produced it: a
    // number an accountant cannot trace is a number they will re-derive by hand, which is exactly
    // the cost this is supposed to remove.
    private static List<TaxLine> BuildScheduleC(IReadOnlyList<TaxSale> sales, out decimal netProfit)
    {
        var grossReceipts = Round(sales.Sum(s => s.GrossReceipts));
        var returns = Round(sales.Sum(s => s.Returns));
        var netReceipts = Round(grossReceipts - returns);

        var cogs = Round(sales.Sum(s => s.DeductibleCost));
        var voided = sales.Count(s => s.Voided);
        var grossProfit = Round(netReceipts - cogs);

        var feesTotal = Round(sales.Sum(s => s.Fees));
        var supplies = Round(sales.Sum(s => s.Packaging));
        var postage = Round(sales.Sum(s => s.ShippingCost + s.OtherCosts));
        var expenses = Round(feesTotal + supplies + postage);

        netProfit = Round(grossProfit - expenses);

        var salesWithCost = sales.Count(s => !s.CostMissing);
        var measuredFees = sales.Count(s => s.Flip.FeesAreActual);
        var assumedShipping = sales.Count(s => s.Flip.ShippingCostAssumed || s.Flip.ShippingCostUnknown);

        var lines = new List<TaxLine>
        {
            new()
            {
                Line = "1", Label = "Gross receipts or sales", Amount = grossReceipts,
                Basis = $"{sales.Count} sale{S(sales.Count)} — what buyers paid, including the shipping they paid, before refunds. Sales tax eBay collected is not in here: they remitted it, it never reached you.",
                Measured = true,
            },
            new()
            {
                Line = "2", Label = "Returns and allowances", Amount = returns,
                Basis = returns > 0
                    ? $"{Money(returns)} refunded to buyers across {sales.Count(s => s.Returns > 0)} sale{S(sales.Count(s => s.Returns > 0))}."
                    : "No refunds recorded for this year.",
                Measured = true,
            },
            new() { Line = "3", Label = "Line 1 minus line 2", Amount = netReceipts, Basis = "What you actually took in.", IsSubtotal = true, Measured = true },
            new()
            {
                Line = "4", Label = "Cost of goods sold", Amount = cogs,
                Basis = (sales.Count == voided
                    ? "Every sale this year was refunded in full."
                    : salesWithCost == sales.Count
                        ? $"What you paid for the {sales.Count - voided} item{S(sales.Count - voided)} that stayed sold."
                        : $"What you paid for {salesWithCost - voided} of {sales.Count - voided} sold item{S(sales.Count - voided)}. The other {sales.Count - salesWithCost} contribute nothing here, because nothing records what they cost.")
                    + (voided > 0
                        ? $" {voided} fully refunded sale{S(voided)} {(voided == 1 ? "is" : "are")} left out — the goods came back to you, so they are stock again rather than a cost of sale."
                        : ""),
                Measured = salesWithCost == sales.Count,
            },
            new() { Line = "5", Label = "Gross profit", Amount = grossProfit, Basis = "Line 3 minus line 4.", IsSubtotal = true, Measured = salesWithCost == sales.Count },
            new() { Line = "7", Label = "Gross income", Amount = grossProfit, Basis = "Line 5, with no other business income.", IsSubtotal = true, Measured = salesWithCost == sales.Count },
            new()
            {
                Line = "10", Label = "Commissions and fees", Amount = feesTotal,
                Basis = measuredFees == sales.Count
                    ? "Every dollar of this is what eBay actually charged."
                    : measuredFees == 0
                        ? "Estimated from your Fees & Costs settings — eBay has not reported a fee on any of these sales."
                        : $"eBay's own figure on {measuredFees} of {sales.Count} sales; the rest estimated from your Fees & Costs settings.",
                Measured = measuredFees == sales.Count,
            },
            new()
            {
                Line = "22", Label = "Supplies (packaging)", Amount = supplies,
                Basis = supplies > 0
                    ? $"Your packaging cost from Fees & Costs, on {sales.Count} sale{S(sales.Count)}. Boxes, tape and mailers you bought are deductible whether or not this app knows about them."
                    : "No packaging cost set in Fees & Costs — so nothing is claimed here, and every box you bought this year is a deduction going unclaimed.",
                Measured = false,
            },
            new()
            {
                Line = "27a", Label = "Other expenses (postage and per-sale costs)", Amount = postage,
                Basis = assumedShipping == 0
                    ? "Postage you recorded, plus anything else a specific sale cost you."
                    : $"Postage plus per-sale costs. {assumedShipping} of {sales.Count} sale{S(sales.Count)} had no postage recorded, so this app used your default or assumed a pass-through — a real receipt would likely make this larger.",
                Measured = assumedShipping == 0,
            },
            new() { Line = "28", Label = "Total expenses", Amount = expenses, Basis = "Lines 10, 22 and 27a.", IsSubtotal = true, Measured = false },
            new()
            {
                Line = "31", Label = "Net profit or loss", Amount = netProfit,
                Basis = "Line 7 minus line 28. This is the figure your tax is worked out from.",
                IsSubtotal = true, Measured = false,
            },
        };

        return lines;
    }

    // ── The bill ─────────────────────────────────────────────────────────────────────────────
    // Self-employment tax first, because half of it is deductible against the income tax and doing
    // it the other way round overstates the income-tax base on every return.
    private static void ApplyTax(TaxPackResult result, decimal netProfit, decimal rate)
    {
        var seEarnings = Round(Math.Max(0m, netProfit) * SelfEmploymentBase);
        var seApplies = seEarnings >= SelfEmploymentFloor;

        result.Assumptions.SelfEmploymentApplies = seApplies;
        result.SelfEmploymentTax = seApplies ? Round(seEarnings * SelfEmploymentRate / 100m) : 0m;

        // Half the self-employment tax comes off income before the bracket is applied.
        var incomeBase = Math.Max(0m, netProfit - result.SelfEmploymentTax / 2m);
        result.IncomeTax = Round(incomeBase * rate / 100m);
        result.TotalTax = Round(result.SelfEmploymentTax + result.IncomeTax);

        result.EffectiveRatePercent = netProfit > 0
            ? Math.Round(result.TotalTax / netProfit * 100m, 1) : 0m;

        // What one more provable dollar of expense is worth. A dollar of deduction removes a dollar
        // of net profit, which removes 92.35% of a dollar of SE earnings, and the half of that SE
        // saving that was deductible comes back out of the income-tax base too.
        var seMarginal = seApplies ? SelfEmploymentBase * SelfEmploymentRate : 0m;
        result.Assumptions.DeductionValuePercent =
            Math.Round(seMarginal + rate * (1m - seMarginal / 200m), 1);
    }

    // ── The number this whole feature exists to show ─────────────────────────────────────────
    // Proceeds the seller keeps that will be taxed as pure profit, because nothing on record says
    // what the goods cost. It is the one figure here the seller can act on today, and every dollar
    // of cost they go and dig up removes it at the marginal rate.
    private static TaxCostGap BuildCostGap(
        IReadOnlyList<TaxSale> sales, decimal deductionValuePercent, decimal totalTax)
    {
        var missing = sales.Where(s => s.CostMissing).ToList();
        var proceeds = Round(missing.Sum(s => Math.Max(0m, s.NetReceipts - s.Expenses)));

        return new TaxCostGap
        {
            Sales = missing.Count,
            TaxableProceeds = proceeds,
            // Capped at the whole bill. A marginal rate applied to a large block can otherwise come
            // out a few cents above the tax that actually exists, and "$X of this bill" has to be a
            // share of it rather than a figure the seller can prove wrong with a subtraction.
            TaxAtRisk = Math.Min(Round(proceeds * deductionValuePercent / 100m), totalTax),
        };
    }

    // ── What eBay will tell the IRS ──────────────────────────────────────────────────────────
    // The 1099-K is the single most common reason a reseller panics in February: it reports gross
    // payments, so it is always far larger than anything on this page, and a seller who does not
    // know why assumes they owe tax on all of it.
    private static Form1099KCheck Build1099KCheck(IReadOnlyList<TaxSale> sales, int year)
    {
        var ebay = sales.Where(s => s.Flip.Source == "ebay").ToList();
        var check = new Form1099KCheck
        {
            Sales = ebay.Count,
            ExpectedGross = Round(ebay.Sum(s => s.GrossReceipts)),
            RefundsNotDeducted = Round(ebay.Sum(s => s.Returns)),
        };

        if (ebay.Count == 0)
        {
            check.Notes.Add($"No eBay-sourced sales are on record for {year}, so there is nothing here to match a 1099-K against. Connect eBay and your orders import themselves.");
            return check;
        }

        check.Notes.Add($"eBay reports gross payments — the sale price plus the shipping the buyer paid, with no fees, no postage and no cost of goods taken out. Expect roughly {Money(check.ExpectedGross)} across {ebay.Count} sale{S(ebay.Count)}, which is line 1 above and NOT what you are taxed on.");

        if (check.RefundsNotDeducted > 0)
            check.Notes.Add($"{Money(check.RefundsNotDeducted)} of refunds is included in that figure. eBay does not subtract refunds from the form — you deduct them yourself, on line 2.");

        check.Notes.Add("eBay's number may also include the sales tax they collected and remitted for you, which this app never records because it never reaches you. If their figure comes in higher than the one above, that gap is the most likely reason, and it is deductible against itself.");
        check.Notes.Add($"Whether a form is issued at all depends on the federal threshold in force for {year}, which has been changed three times since 2022, and several states set their own much lower. Seller Hub shows the figure eBay is using for your account. You owe the tax on your profit either way — the form only decides who else gets told.");

        return check;
    }

    // ── When to have the money ───────────────────────────────────────────────────────────────
    // The IRS estimated-tax windows are 3, 2, 3 and 4 months long. Splitting the year into four
    // equal quarters puts June's sales in the wrong payment, and that is the most common way a
    // seller ends up with an underpayment penalty on a year they had the money for all along.
    private static List<TaxQuarter> BuildQuarters(
        IReadOnlyList<TaxSale> sales, int year, DateTimeOffset now, decimal taxFraction)
    {
        var offset = now.Offset;

        (string Name, int FromMonth, int ToMonthExclusive, DateTimeOffset Due, string Covers)[] windows =
        [
            ("Q1", 1,  4,  new DateTimeOffset(year,     4, 15, 0, 0, 0, offset), "January 1 – March 31"),
            ("Q2", 4,  6,  new DateTimeOffset(year,     6, 15, 0, 0, 0, offset), "April 1 – May 31"),
            ("Q3", 6,  9,  new DateTimeOffset(year,     9, 15, 0, 0, 0, offset), "June 1 – August 31"),
            ("Q4", 9, 13,  new DateTimeOffset(year + 1, 1, 15, 0, 0, 0, offset), "September 1 – December 31"),
        ];

        return windows.Select(w =>
        {
            var start = new DateTimeOffset(year, w.FromMonth, 1, 0, 0, 0, offset);
            var end = w.ToMonthExclusive > 12
                ? new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, offset)
                : new DateTimeOffset(year, w.ToMonthExclusive, 1, 0, 0, 0, offset);

            var inWindow = sales.Where(s => s.SoldLocal >= start && s.SoldLocal < end).ToList();
            var profit = Round(inWindow.Sum(s => s.ScheduleCNet));

            return new TaxQuarter
            {
                Name = w.Name,
                Covers = w.Covers,
                DueDate = w.Due,
                Sales = inWindow.Count,
                NetProfit = profit,
                // A quarter that lost money sets nothing aside rather than a negative: the loss
                // shows up as a smaller bill in the quarters that made money, not as a refund here.
                SetAside = Round(Math.Max(0m, profit) * taxFraction),
                IsPast = w.Due < now,
                IsCurrent = now >= start && now < end,
            };
        }).ToList();
    }

    // ── Saying out loud what the numbers assume ──────────────────────────────────────────────
    // Every sentence here is something the seller would otherwise have to infer from a figure being
    // bigger or smaller than they expected. On a tax screen an unexplained gap does not read as
    // conservative — it reads as broken, and a seller who distrusts one number distrusts all of them.
    private static List<string> BuildHonesty(TaxPackResult r, IReadOnlyList<TaxSale> sales, FeeProfile fees)
    {
        var lines = new List<string>
        {
            "This is a set-aside plan and a starting point for a return. It is not tax advice, nothing here is filed, and no figure is rounded in your favour.",
        };

        if (!r.HasSales) return lines;

        var labourAddedBack = Round(sales.Sum(s => s.Labor));
        var difference = Round(r.NetProfit - r.EarningsNetProfit);

        if (difference != 0)
        {
            var reasons = new List<string>();
            if (r.CostGap.Sales > 0)
                reasons.Add($"{r.CostGap.Sales} sale{S(r.CostGap.Sales)} with no recorded cost, which Money Made leaves out of profit entirely and the IRS taxes in full");
            if (labourAddedBack > 0)
                reasons.Add($"{Money(labourAddedBack)} of your own labour, which every forecast in this app charges against profit but which is not deductible on a Schedule C");

            var voided = sales.Count(s => s.Voided);
            if (voided > 0)
                reasons.Add($"{voided} refunded sale{S(voided)}, which Money Made leaves out of profit altogether and a return still has to account for on lines 1 and 2");

            lines.Add($"Money Made puts {Money(r.EarningsNetProfit)} of profit against {r.Year}; line 31 above says {Money(r.NetProfit)}. The {Money(Math.Abs(difference))} between them is {(reasons.Count > 0 ? string.Join(", and ", reasons) : "the different rules the two screens run")}. Both numbers are right for the question they answer.");
        }

        if (r.CostGap.Sales > 0)
            lines.Add($"{Money(r.CostGap.TaxableProceeds)} of proceeds has no cost recorded behind it, so it is taxed as though the goods were free — about {Money(r.CostGap.TaxAtRisk)}. Entering what you paid, even from memory backed by a bank line, takes that straight back off.");

        if (r.Assumptions.SelfEmploymentApplies)
            lines.Add($"Self-employment tax is charged at {SelfEmploymentRate}% on {SelfEmploymentBase * 100m:0.##}% of net profit. The Social Security half stops above an annual wage cap and any W-2 job you hold uses that cap up first — neither is modelled, so on a high-income year this line is too big rather than too small.");
        else if (r.NetProfit > 0)
            lines.Add($"Net profit is under the {Money(SelfEmploymentFloor)} floor where self-employment tax starts, so none is charged. Income tax still applies to the first dollar.");

        lines.Add($"Income tax uses the flat {r.Assumptions.IncomeTaxRatePercent:0.##}% bracket you set. It ignores your standard deduction, the 20% qualified-business-income deduction most resellers can take, any other income, your filing status and every credit. Change the rate above to match your situation — it is the only figure here only you know.");

        lines.Add("State and local income tax is not included. Most states charge it, so the real hold-back is higher than the figure above by whatever your state takes.");

        var unclaimed = new List<string>();
        if (fees.DefaultPackagingCost <= 0) unclaimed.Add("packaging");
        if (fees.DefaultShippingCost <= 0 && sales.Any(s => s.Flip.ShippingCostUnknown)) unclaimed.Add("postage on free-shipped sales");
        lines.Add(unclaimed.Count > 0
            ? $"Nothing is claimed here for {string.Join(" or ", unclaimed)}, mileage to pick up stock, your home office, storage, or subscriptions — including this one. Setting them in Fees & Costs and keeping receipts is worth about {r.Assumptions.DeductionValuePercent:0.#} cents off the bill for every dollar."
            : $"Mileage to pick up stock, your home office, storage and subscriptions — including this one — are not in here. Every provable dollar of them is worth about {r.Assumptions.DeductionValuePercent:0.#} cents off the bill.");

        if (r.YearInProgress)
            lines.Add($"{r.Year} is still running, so everything above is a position to date and will grow with the rest of the year.");

        lines.Add("Cost of goods is counted when an item sells, not when you bought it — the treatment most resellers use. Stock you paid for that has not sold yet is not a deduction this year.");

        return lines;
    }

    // ── The handover ─────────────────────────────────────────────────────────────────────────
    // Two files, because an accountant wants the summary and then wants to check one line of it.
    private static string BuildSummaryCsv(TaxPackResult r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"ING Listing Engine — Schedule C summary,{r.Year}");
        sb.AppendLine("Not tax advice. Figures are computed from sold orders and the seller's own cost settings.");
        sb.AppendLine();
        sb.AppendLine("Schedule C line,Description,Amount,How it was worked out");

        foreach (var line in r.ScheduleC)
            sb.AppendLine(Csv(line.Line, line.Label, Amount(line.Amount), line.Basis));

        sb.AppendLine();
        sb.AppendLine("Estimate,Description,Amount,How it was worked out");
        sb.AppendLine(Csv("SE tax", "Self-employment tax", Amount(r.SelfEmploymentTax),
            r.Assumptions.SelfEmploymentApplies
                ? $"{SelfEmploymentRate}% of {SelfEmploymentBase * 100m:0.##}% of line 31."
                : $"Net profit is below the {Money(SelfEmploymentFloor)} floor."));
        sb.AppendLine(Csv("Income tax", "Federal income tax", Amount(r.IncomeTax),
            $"{r.Assumptions.IncomeTaxRatePercent:0.##}% of line 31 less half the self-employment tax. No standard deduction, QBI, other income or credits."));
        sb.AppendLine(Csv("Total", "Set aside", Amount(r.TotalTax),
            $"{r.EffectiveRatePercent:0.#}% of net profit. Federal only — state and local not included."));

        sb.AppendLine();
        sb.AppendLine("Quarter,Covers,Payment due,Sales,Net profit,Set aside");
        foreach (var q in r.Quarters)
            sb.AppendLine(Csv(q.Name, q.Covers, q.DueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                q.Sales.ToString(CultureInfo.InvariantCulture), Amount(q.NetProfit), Amount(q.SetAside)));

        if (r.CostGap.Sales > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Csv("Cost basis missing", $"{r.CostGap.Sales} sale(s) with no recorded cost of goods",
                Amount(r.CostGap.TaxableProceeds), $"Taxed as pure profit. About {Amount(r.CostGap.TaxAtRisk)} of tax rests on finding these costs."));
        }

        return sb.ToString();
    }

    private static string BuildLedgerCsv(IReadOnlyList<TaxSale> sales)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Date sold,Item,SKU,Listing ID,Source,Qty,Sale price,Buyer-paid shipping,Refunded,Fees,Fee is eBay's figure,Postage,Packaging,Other costs,Cost of goods,Cost recorded,Schedule C net");

        foreach (var s in sales.OrderBy(x => x.SoldLocal))
        {
            sb.AppendLine(Csv(
                s.SoldLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                s.Flip.Title,
                s.Flip.Sku,
                s.Flip.ListingId,
                s.Flip.Source,
                Math.Max(1, s.Flip.Quantity).ToString(CultureInfo.InvariantCulture),
                Amount(s.Flip.Flip.SalePrice),
                Amount(s.Flip.Flip.ShippingCharged),
                Amount(s.Returns),
                Amount(s.Fees),
                s.Flip.FeesAreActual ? "yes" : "estimated",
                Amount(s.ShippingCost),
                Amount(s.Packaging),
                Amount(s.OtherCosts),
                Amount(s.DeductibleCost),
                s.Voided ? "not claimed — refunded, goods came back"
                    : s.CostMissing ? "NO — taxed as pure profit" : "yes",
                Amount(s.ScheduleCNet)));
        }

        return sb.ToString();
    }

    private static string Csv(params string[] fields) => string.Join(",", fields.Select(CsvField));

    private static string CsvField(string? value)
    {
        var text = value ?? "";
        return text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r')
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }

    private static string Amount(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal Round(decimal value) => Math.Round(value, 2);

    private static string S(int count) => count == 1 ? "" : "s";

    private static string Money(decimal value) =>
        value.ToString("C0", CultureInfo.GetCultureInfo("en-US"));
}
