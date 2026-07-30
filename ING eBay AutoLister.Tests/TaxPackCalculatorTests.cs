using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The Tax Pack is the one screen in the app allowed to disagree with Money Made about the same
// year, and most of what follows pins that disagreement down so it can never become an accident:
// a sale with no recorded cost contributes nothing to earnings and is taxed in full, and the
// seller's own labour is charged against every forecast in the app but added back here.
//
// The failure mode that matters is the mirror of the earnings tracker's. There, a total that is too
// BIG is a lie about money the seller has. Here, a total that is too SMALL is a seller who spent
// the tax money, so every rounding that could go either way goes against them.
public class TaxPackCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    private static EarningsCalculator Earnings() => new(new ProfitCalculator());

    private static FeeProfile Fees(decimal packaging = 0m, decimal labor = 0m, decimal shipping = 0m) => new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        DefaultPackagingCost = packaging,
        DefaultLaborCost = labor,
        DefaultShippingCost = shipping,
    };

    private static FlipRecord Sale(
        decimal price = 1000m, decimal? cost = 400m, decimal shippingCharged = 0m,
        decimal? shippingCost = 0m, decimal? fee = 100m, decimal refunded = 0m,
        string status = "paid", string source = "ebay", DateTimeOffset? soldUtc = null) => new()
    {
        Source = source,
        Title = "Antminer S19",
        SoldUtc = soldUtc ?? new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero),
        Quantity = 1,
        SalePrice = price,
        ShippingCharged = shippingCharged,
        MarketplaceFee = fee,
        ShippingCost = shippingCost,
        UnitCost = cost,
        RefundedAmount = refunded,
        Status = status,
    };

    private static TaxPackResult Build(
        IEnumerable<FlipRecord> sales, FeeProfile? fees = null,
        decimal rate = 12m, int? year = 2026, DateTimeOffset? now = null)
    {
        var profile = fees ?? Fees();
        var computed = sales.Select(s => Earnings().Compute(s, null, profile)).ToList();
        return new TaxPackCalculator().Build(computed, profile, rate, year, now ?? Now);
    }

    private static decimal Line(TaxPackResult pack, string line) =>
        pack.ScheduleC.Single(l => l.Line == line).Amount;

    // ── The form ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Schedule_c_reads_top_to_bottom_and_line_31_is_what_is_left()
    {
        var pack = Build([Sale(price: 1000m, shippingCharged: 20m, cost: 400m, fee: 135m, shippingCost: 30m)]);

        Assert.Equal(1020m, Line(pack, "1"));    // sale price plus the shipping the buyer paid
        Assert.Equal(0m, Line(pack, "2"));
        Assert.Equal(1020m, Line(pack, "3"));
        Assert.Equal(400m, Line(pack, "4"));
        Assert.Equal(620m, Line(pack, "5"));
        Assert.Equal(620m, Line(pack, "7"));
        Assert.Equal(135m, Line(pack, "10"));
        Assert.Equal(30m, Line(pack, "27a"));
        Assert.Equal(165m, Line(pack, "28"));
        Assert.Equal(455m, Line(pack, "31"));
        Assert.Equal(455m, pack.NetProfit);
    }

    [Fact]
    public void Refunds_come_off_as_returns_and_allowances_rather_than_off_gross_receipts()
    {
        // The distinction is not cosmetic: line 1 has to match what eBay reports on the 1099-K, or
        // the seller cannot reconcile the two and assumes one of them is wrong.
        var pack = Build([Sale(price: 1000m, refunded: 250m, cost: 400m, fee: 135m)]);

        Assert.Equal(1000m, Line(pack, "1"));
        Assert.Equal(250m, Line(pack, "2"));
        Assert.Equal(750m, Line(pack, "3"));
    }

    [Fact]
    public void A_cancelled_order_is_not_a_sale_that_made_nothing()
    {
        var pack = Build([Sale(status: "cancelled"), Sale(price: 500m, cost: 200m, fee: 50m)]);

        Assert.Equal(500m, Line(pack, "1"));
        Assert.Equal(250m, pack.NetProfit);
    }

    // ── Where this deliberately parts company with Money Made ────────────────────────────────

    [Fact]
    public void A_sale_with_no_recorded_cost_earns_nothing_upstairs_and_is_taxed_in_full_here()
    {
        var pack = Build([Sale(price: 1000m, cost: null, fee: 135m, shippingCost: 25m)]);

        // Money Made refuses to count it, because the profit might not exist.
        Assert.Equal(0m, pack.EarningsNetProfit);
        // The IRS counts all of it, because as far as any record shows the goods were free.
        Assert.Equal(840m, pack.NetProfit);
        Assert.Equal(0m, Line(pack, "4"));

        Assert.Equal(1, pack.CostGap.Sales);
        Assert.Equal(840m, pack.CostGap.TaxableProceeds);
        Assert.True(pack.CostGap.TaxAtRisk > 0m);

        // And the two figures are reconciled in words, not left to look like a bug.
        Assert.Contains(pack.Honesty, h => h.Contains("Money Made") && h.Contains("line 31"));
    }

    [Fact]
    public void The_tax_at_risk_is_what_recording_the_cost_would_actually_save()
    {
        // The gap is priced at the marginal value of a deduction, so "enter what you paid and this
        // goes away" is a promise the arithmetic keeps rather than a slogan.
        var withoutCost = Build([Sale(price: 1000m, cost: null, fee: 135m, shippingCost: 25m)]);
        var withCost = Build([Sale(price: 1000m, cost: 400m, fee: 135m, shippingCost: 25m)]);

        var saved = withoutCost.TotalTax - withCost.TotalTax;
        var predicted = 400m * withoutCost.Assumptions.DeductionValuePercent / 100m;

        // Within a dollar: the rate is published to a tenth of a percent, so the promise is that
        // typing a $400 cost removes $400 worth of tax, not that it removes it to the cent.
        Assert.True(Math.Abs(saved - predicted) < 1m,
            $"recording a $400 cost saved {saved:C2} but a deduction was priced at {predicted:C2}");

        // And the block never claims more than the whole bill.
        Assert.True(withoutCost.CostGap.TaxAtRisk <= withoutCost.TotalTax);
    }

    [Fact]
    public void The_sellers_own_labour_is_charged_against_earnings_and_added_back_for_tax()
    {
        // Every forecast in the app charges the seller's time. You cannot pay yourself a wage and
        // deduct it on a Schedule C, so it comes back — which makes the taxable figure LARGER than
        // the one on the earnings screen, and saying so is the whole reason this line exists.
        var pack = Build([Sale(price: 1000m, cost: 400m, fee: 135m)], Fees(labor: 15m));

        Assert.Equal(450m, pack.EarningsNetProfit);   // earnings charged the $15 of labour
        Assert.Equal(465m, pack.NetProfit);           // the return does not
        Assert.Contains(pack.Honesty, h => h.Contains("labour"));
    }

    [Fact]
    public void Packaging_is_a_deductible_supply_and_lands_on_its_own_line()
    {
        var pack = Build([Sale(price: 1000m, cost: 400m, fee: 135m), Sale(price: 500m, cost: 200m, fee: 70m)],
            Fees(packaging: 2.50m));

        Assert.Equal(5m, Line(pack, "22"));
        Assert.Equal(690m, Line(pack, "31"));
    }

    [Fact]
    public void No_packaging_cost_set_is_reported_as_a_deduction_going_unclaimed()
    {
        var pack = Build([Sale(price: 1000m, cost: 400m, fee: 135m)]);

        Assert.Equal(0m, Line(pack, "22"));
        Assert.Contains("unclaimed", pack.ScheduleC.Single(l => l.Line == "22").Basis);
    }

    [Fact]
    public void A_fully_refunded_sale_does_not_deduct_the_cost_of_goods_that_came_back()
    {
        // The failure this pins down was found against real data: a $285 sale refunded in full was
        // claiming a $180 loss on a miner that is back on the shelf. It belongs on lines 1 and 2 —
        // eBay reports it in gross payments and this is where it comes back out — but the item is
        // stock again, not a cost of goods SOLD.
        var pack = Build([Sale(price: 285m, cost: 180m, fee: 0m, refunded: 285m, status: "refunded")]);

        Assert.Equal(285m, Line(pack, "1"));
        Assert.Equal(285m, Line(pack, "2"));
        Assert.Equal(0m, Line(pack, "3"));
        Assert.Equal(0m, Line(pack, "4"));
        Assert.Equal(0m, pack.NetProfit);

        // It is not a sale with a missing cost, so it must not appear in the gap the seller is
        // being asked to go and fix.
        Assert.Equal(0, pack.CostGap.Sales);
        Assert.Contains("came back to you", pack.ScheduleC.Single(l => l.Line == "4").Basis);
        Assert.Contains("refunded, goods came back", pack.LedgerCsv);
    }

    [Fact]
    public void A_partial_refund_is_a_price_cut_and_the_goods_are_still_gone()
    {
        // The buyer kept the item, so its cost is still a cost of sale. Treating a $50 goodwill
        // refund the way a full return is treated would erase the whole cost basis of the sale.
        var pack = Build([Sale(price: 1000m, cost: 400m, fee: 100m, refunded: 250m)]);

        Assert.Equal(1000m, Line(pack, "1"));
        Assert.Equal(250m, Line(pack, "2"));
        Assert.Equal(400m, Line(pack, "4"));
    }

    // ── The bill ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Self_employment_tax_is_charged_on_the_standard_share_of_net_profit()
    {
        var pack = Build([Sale(price: 10_000m, cost: 4_000m, fee: 1_350m)], rate: 0m);

        // 4,650 × 92.35% × 15.3%
        Assert.Equal(4_650m, pack.NetProfit);
        Assert.True(pack.Assumptions.SelfEmploymentApplies);
        Assert.Equal(657.02m, pack.SelfEmploymentTax);
        Assert.Equal(0m, pack.IncomeTax);
    }

    [Fact]
    public void Half_the_self_employment_tax_comes_off_before_the_income_bracket_is_applied()
    {
        var pack = Build([Sale(price: 10_000m, cost: 4_000m, fee: 1_350m)], rate: 22m);

        // (4,650 − 657.02/2) × 22%. Doing this the other way round overstates every return.
        Assert.Equal(950.73m, pack.IncomeTax);
        Assert.Equal(1_607.75m, pack.TotalTax);
        Assert.Equal(34.6m, pack.EffectiveRatePercent);
    }

    [Fact]
    public void Below_the_four_hundred_dollar_floor_there_is_no_self_employment_tax()
    {
        var pack = Build([Sale(price: 500m, cost: 200m, fee: 70m)], rate: 12m);

        Assert.Equal(230m, pack.NetProfit);
        Assert.False(pack.Assumptions.SelfEmploymentApplies);
        Assert.Equal(0m, pack.SelfEmploymentTax);
        Assert.Equal(27.60m, pack.IncomeTax);         // income tax still applies to the first dollar
        Assert.Contains(pack.Honesty, h => h.Contains("floor"));
    }

    [Fact]
    public void A_losing_year_owes_nothing_rather_than_a_negative_bill()
    {
        var pack = Build([Sale(price: 300m, cost: 900m, fee: 45m)]);

        Assert.True(pack.NetProfit < 0m);
        Assert.Equal(0m, pack.SelfEmploymentTax);
        Assert.Equal(0m, pack.IncomeTax);
        Assert.Equal(0m, pack.TotalTax);
        Assert.Equal(0m, pack.EffectiveRatePercent);
    }

    [Fact]
    public void A_deduction_is_worth_the_self_employment_saving_plus_the_bracket()
    {
        var pack = Build([Sale(price: 10_000m, cost: 4_000m, fee: 1_350m)], rate: 22m);

        // 92.35% × 15.3% = 14.13 cents, plus 22 cents of bracket on the 92.94% of the dollar the
        // SE deduction leaves behind. This is the number the whole "keep the receipt" case rests on.
        Assert.Equal(34.6m, pack.Assumptions.DeductionValuePercent);
    }

    [Fact]
    public void A_bracket_nobody_could_be_in_is_clamped_rather_than_believed()
    {
        var pack = Build([Sale()], rate: 3_700m);

        Assert.Equal(50m, pack.Assumptions.IncomeTaxRatePercent);
    }

    // ── When to have the money ───────────────────────────────────────────────────────────────

    [Fact]
    public void The_quarters_are_the_irs_windows_not_calendar_quarters()
    {
        var pack = Build([Sale()]);

        var q2 = pack.Quarters.Single(q => q.Name == "Q2");
        Assert.Equal("April 1 – May 31", q2.Covers);
        Assert.Equal(new DateTime(2026, 6, 15), q2.DueDate.Date);

        // June belongs to Q3, which is the mistake four equal quarters makes and the one that
        // earns a seller an underpayment penalty on a year they had the money for.
        var q3 = pack.Quarters.Single(q => q.Name == "Q3");
        Assert.Equal("June 1 – August 31", q3.Covers);
        Assert.Equal(new DateTime(2026, 9, 15), q3.DueDate.Date);

        // Q4's payment is not due until January of the following year.
        Assert.Equal(new DateTime(2027, 1, 15), pack.Quarters.Single(q => q.Name == "Q4").DueDate.Date);
    }

    [Fact]
    public void A_sale_lands_in_the_window_it_was_made_in_and_carries_its_own_set_aside()
    {
        var pack = Build([
            Sale(price: 10_000m, cost: 4_000m, fee: 1_350m, soldUtc: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Sale(price: 5_000m,  cost: 2_000m, fee: 675m,   soldUtc: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero)),
        ], rate: 22m);

        var q1 = pack.Quarters.Single(q => q.Name == "Q1");
        var q3 = pack.Quarters.Single(q => q.Name == "Q3");

        Assert.Equal(1, q1.Sales);
        Assert.Equal(4_650m, q1.NetProfit);
        Assert.Equal(1, q3.Sales);
        Assert.Equal(2_325m, q3.NetProfit);

        // Every quarter's set-aside adds up to the year's bill, so a seller who follows the plan
        // lands on the number rather than a few dollars short of it.
        Assert.True(Math.Abs(pack.Quarters.Sum(q => q.SetAside) - pack.TotalTax) < 0.05m,
            $"the four payments came to {pack.Quarters.Sum(q => q.SetAside):C2} against a bill of {pack.TotalTax:C2}");

        // Sitting in July, Q1 and Q2 are behind us and Q3 is the one being earned into.
        Assert.True(q1.IsPast);
        Assert.True(q3.IsCurrent);
        Assert.False(q3.IsPast);
    }

    // ── The 1099-K ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_1099k_estimate_is_gross_payments_on_ebay_sales_only()
    {
        var pack = Build([
            Sale(price: 1000m, shippingCharged: 20m, refunded: 100m, source: "ebay"),
            Sale(price: 400m, source: "manual"),
        ]);

        // Gross, before refunds and before anything else — which is exactly why it is so much
        // larger than net profit, and why a seller who does not know that panics in February.
        Assert.Equal(1020m, pack.Form1099K.ExpectedGross);
        Assert.Equal(1, pack.Form1099K.Sales);
        Assert.Equal(100m, pack.Form1099K.RefundsNotDeducted);
        Assert.Contains(pack.Form1099K.Notes, n => n.Contains("refunds"));
    }

    [Fact]
    public void The_1099k_block_never_states_a_threshold_it_cannot_stand_behind()
    {
        var pack = Build([Sale()]);
        var notes = string.Join(" ", pack.Form1099K.Notes);

        // The federal threshold has moved three times since 2022 and states set their own. A wrong
        // number here would tell a seller they are not being reported on when they are.
        Assert.Contains("threshold", notes);
        Assert.DoesNotContain("$600", notes);
        Assert.DoesNotContain("$20,000", notes);
    }

    // ── Years, and the handover ──────────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_year_asked_for_is_counted_and_the_others_are_offered()
    {
        var pack = Build([
            Sale(price: 1000m, cost: 400m, fee: 135m, soldUtc: new DateTimeOffset(2025, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            Sale(price: 2000m, cost: 800m, fee: 270m, soldUtc: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
        ], year: 2025);

        Assert.Equal(2025, pack.Year);
        Assert.Equal(1000m, Line(pack, "1"));
        Assert.False(pack.YearInProgress);
        Assert.Equal(new[] { 2026, 2025 }, pack.AvailableYears);
    }

    [Fact]
    public void A_year_with_nothing_in_it_says_so_instead_of_printing_zeros_as_a_result()
    {
        var pack = Build([Sale(soldUtc: new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero))], year: 2026);

        Assert.False(pack.HasSales);
        Assert.Equal(0m, pack.TotalTax);
        // The one line that is true whether or not there are sales is still said.
        Assert.Contains(pack.Honesty, h => h.Contains("not tax advice"));
    }

    [Fact]
    public void The_summary_export_carries_every_line_and_the_ledger_carries_every_sale()
    {
        var pack = Build([
            Sale(price: 1000m, cost: 400m, fee: 135m),
            Sale(price: 500m, cost: null, fee: 70m),
        ]);

        Assert.Equal("schedule-c-2026-ING.csv", pack.SummaryCsvFilename);
        Assert.Contains("Schedule C line,Description,Amount,How it was worked out", pack.SummaryCsv);
        foreach (var line in pack.ScheduleC)
            Assert.Contains($"{line.Line},{line.Label}", pack.SummaryCsv.Replace("\"", ""));

        Assert.Equal("sales-ledger-2026-ING.csv", pack.LedgerCsvFilename);
        // Two sales plus the header. The one with no cost is called out in the row itself, because
        // that is the row the accountant has to go and ask about.
        Assert.Equal(3, pack.LedgerCsv.TrimEnd().Split('\n').Length);
        Assert.Contains("NO — taxed as pure profit", pack.LedgerCsv);
    }

    [Fact]
    public void A_comma_in_an_item_title_cannot_shift_the_ledger_by_a_column()
    {
        var sale = Sale();
        sale.Title = "Antminer S19, 95TH, \"pro\"";
        var pack = Build([sale]);

        Assert.Contains("\"Antminer S19, 95TH, \"\"pro\"\"\"", pack.LedgerCsv);
    }

    // ── The one figure the seller supplies ───────────────────────────────────────────────────

    [Fact]
    public void The_stored_bracket_survives_a_save_of_the_fee_settings()
    {
        // The bracket lives in the fee-profile table but is NOT part of FeeProfile, because the
        // Fees & Costs screen round-trips that object and knows nothing about tax. If it ever
        // becomes a FeeProfile field, saving a shipping cost silently resets the seller's bracket.
        var path = Path.Combine(Path.GetTempPath(), $"tax-rate-{Guid.NewGuid():N}.db");
        try
        {
            var store = new FeeProfileStore(path);
            Assert.Equal(12m, store.LoadTaxRate());

            Assert.Equal(22m, store.SaveTaxRate(22m));
            store.Save(new FeeProfile { DefaultShippingCost = 9m });

            Assert.Equal(22m, store.LoadTaxRate());
            Assert.Equal(50m, store.SaveTaxRate(3_700m));   // clamped, not believed
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
