using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The earnings tracker is the only screen in the app that reports money the seller already has, so
// the failure mode that matters is a total that is too BIG. Most of what follows tests a refusal:
// a sale with no cost basis contributing nothing, an assumed shipping cost being flagged rather
// than hidden, a cancelled order not counting as a sale that made nothing.
public class EarningsCalculatorTests
{
    private static EarningsCalculator NewCalculator() => new(new ProfitCalculator());

    private static FeeProfile Fees() => new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
    };

    private static FlipRecord Sale(
        decimal price = 1000m, decimal? cost = 400m, decimal shippingCharged = 0m,
        decimal? shippingCost = 0m, decimal? fee = 100m, int quantity = 1,
        string status = "paid", decimal refunded = 0m, DateTimeOffset? soldUtc = null,
        string source = "ebay") => new()
    {
        Source = source,
        Title = "Antminer S19",
        SoldUtc = soldUtc ?? DateTimeOffset.UtcNow,
        Quantity = quantity,
        SalePrice = price,
        ShippingCharged = shippingCharged,
        MarketplaceFee = fee,
        ShippingCost = shippingCost,
        UnitCost = cost,
        RefundedAmount = refunded,
        Status = status,
    };

    // ── The core number ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Net_profit_is_what_came_in_minus_the_fee_and_what_was_paid()
    {
        var profit = NewCalculator().Compute(Sale(price: 1000m, cost: 400m, fee: 100m), null, Fees());

        Assert.Equal(1000m, profit.GrossRevenue);
        Assert.Equal(100m, profit.Fees);
        Assert.Equal(400m, profit.CostOfGoods);
        Assert.Equal(500m, profit.NetProfit);
        Assert.Equal(125m, profit.RoiPercent);   // $500 back on $400 spent
        Assert.Equal(50m, profit.MarginPercent);
    }

    [Fact]
    public void Ebays_own_fee_is_used_and_marked_as_measured()
    {
        var profit = NewCalculator().Compute(Sale(fee: 137.25m), null, Fees());

        Assert.True(profit.FeesAreActual);
        Assert.Equal(137.25m, profit.Fees);
        Assert.DoesNotContain(profit.Caveats, c => c.Contains("estimated"));
    }

    [Fact]
    public void A_missing_fee_is_estimated_from_the_shared_fee_profile_and_said_so()
    {
        var profit = NewCalculator().Compute(Sale(price: 1000m, fee: null), null, Fees());

        Assert.False(profit.FeesAreActual);
        Assert.Equal(132.90m, profit.Fees);   // 13.25% + $0.40, the same model every forecast uses
        Assert.Contains(profit.Caveats, c => c.Contains("estimated"));
    }

    [Fact]
    public void Cost_of_goods_scales_with_quantity()
    {
        var profit = NewCalculator().Compute(Sale(price: 2000m, cost: 400m, fee: 200m, quantity: 3), null, Fees());

        Assert.Equal(1200m, profit.CostOfGoods);
        Assert.Equal(600m, profit.NetProfit);
    }

    // ── The refusal that keeps the headline honest ───────────────────────────────────────────

    [Fact]
    public void A_sale_with_no_recorded_cost_contributes_no_profit_at_all()
    {
        var profit = NewCalculator().Compute(Sale(cost: null), null, Fees());

        Assert.Null(profit.NetProfit);
        Assert.Null(profit.CostOfGoods);
        Assert.Equal("none", profit.CostSource);
        Assert.False(profit.CountsTowardProfit);
        // The proceeds are still known and still reported — it is only the profit that is unknown.
        Assert.Equal(900m, profit.NetProceeds);
    }

    [Fact]
    public void The_shared_cost_basis_is_used_when_the_sale_has_no_cost_of_its_own()
    {
        var basis = new CostBasisEntry { ListingId = "1100", UnitCost = 380m, InboundShipping = 20m };
        var profit = NewCalculator().Compute(Sale(cost: null), basis, Fees());

        Assert.Equal("basis", profit.CostSource);
        Assert.Equal(400m, profit.CostOfGoods);   // inbound freight is part of what it cost to own
        Assert.Equal(500m, profit.NetProfit);
    }

    [Fact]
    public void A_cost_typed_against_this_sale_beats_the_standing_cost_basis()
    {
        var basis = new CostBasisEntry { ListingId = "1100", UnitCost = 900m };
        var profit = NewCalculator().Compute(Sale(cost: 400m), basis, Fees());

        Assert.Equal("flip", profit.CostSource);
        Assert.Equal(400m, profit.CostOfGoods);
    }

    [Fact]
    public void A_free_item_has_no_roi_rather_than_a_zero_or_an_infinite_one()
    {
        var profit = NewCalculator().Compute(Sale(cost: 0m), null, Fees());

        Assert.Equal(900m, profit.NetProfit);
        Assert.Null(profit.RoiPercent);
    }

    // ── Shipping: the assumption that would otherwise inflate every row ──────────────────────

    [Fact]
    public void An_unrecorded_shipping_cost_is_assumed_to_equal_what_the_buyer_paid()
    {
        var profit = NewCalculator().Compute(
            Sale(price: 1000m, shippingCharged: 25m, shippingCost: null, fee: 100m), null, Fees());

        Assert.Equal(25m, profit.ShippingCost);
        Assert.True(profit.ShippingCostAssumed);
        // Buyer-paid shipping is revenue AND cost, so it nets to nothing rather than to $25 of
        // phantom profit.
        Assert.Equal(500m, profit.NetProfit);
    }

    [Fact]
    public void The_sellers_default_shipping_cost_wins_over_the_pass_through_assumption()
    {
        var fees = Fees();
        fees.DefaultShippingCost = 40m;

        var profit = NewCalculator().Compute(
            Sale(price: 1000m, shippingCharged: 25m, shippingCost: null, fee: 100m), null, fees);

        Assert.Equal(40m, profit.ShippingCost);
        Assert.Equal(485m, profit.NetProfit);
    }

    [Fact]
    public void Free_shipping_with_no_recorded_postage_cost_is_flagged_not_silently_counted_as_zero()
    {
        var profit = NewCalculator().Compute(
            Sale(price: 1000m, shippingCharged: 0m, shippingCost: null, fee: 100m), null, Fees());

        Assert.True(profit.ShippingCostUnknown);
        Assert.Contains(profit.Caveats, c => c.Contains("postage"));
    }

    // ── Refunds and cancellations ────────────────────────────────────────────────────────────

    [Fact]
    public void A_partial_refund_comes_off_the_revenue_and_scales_the_fee_back()
    {
        var profit = NewCalculator().Compute(
            Sale(price: 1000m, cost: 400m, fee: 100m, refunded: 250m, status: "refunded"), null, Fees());

        Assert.Equal(750m, profit.GrossRevenue);
        Assert.Equal(75m, profit.Fees);          // eBay refunds its fee in proportion
        Assert.Equal(275m, profit.NetProfit);
    }

    [Fact]
    public void A_cancelled_order_is_worth_nothing_rather_than_a_loss_of_its_costs()
    {
        var profit = NewCalculator().Compute(
            Sale(price: 1000m, cost: 400m, fee: 100m, status: "cancelled"), null, Fees());

        Assert.Equal(0m, profit.GrossRevenue);
        Assert.Equal(0m, profit.Fees);
        Assert.False(profit.CountsTowardProfit);
    }

    [Fact]
    public void Return_and_testing_reserves_are_not_charged_against_a_completed_sale()
    {
        // Those reserves price the risk of a return that hasn't happened. On a sale that already
        // completed cleanly, the refund column is the actual outcome, and charging both would
        // understate what the seller really made.
        var fees = Fees();
        fees.ReturnReservePercent = 5m;
        fees.TestingReservePercent = 3m;

        var profit = NewCalculator().Compute(Sale(price: 1000m, cost: 400m, fee: 100m), null, fees);

        Assert.Equal(500m, profit.NetProfit);
    }

    [Fact]
    public void Packaging_and_labour_the_seller_configured_are_real_costs_and_are_charged()
    {
        var fees = Fees();
        fees.DefaultPackagingCost = 4m;
        fees.DefaultLaborCost = 6m;

        var profit = NewCalculator().Compute(Sale(price: 1000m, cost: 400m, fee: 100m), null, fees);

        Assert.Equal(490m, profit.NetProfit);
    }

    // ── The summary ──────────────────────────────────────────────────────────────────────────

    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private List<FlipProfit> Computed(params FlipRecord[] flips) =>
        flips.Select(f => NewCalculator().Compute(f, null, Fees())).ToList();

    [Fact]
    public void This_month_counts_only_this_months_sales()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-3)),
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddMonths(-1))), Now);

        Assert.Equal(500m, result.Summary.NetProfitThisMonth);
        Assert.Equal(500m, result.Summary.NetProfitLastMonth);
        Assert.Equal(1000m, result.Summary.NetProfitAllTime);
    }

    [Fact]
    public void Proceeds_from_sales_with_no_cost_are_reported_separately_and_never_as_profit()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 800m, cost: null, fee: 80m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Equal(500m, result.Summary.NetProfitAllTime);
        Assert.Equal(1, result.Summary.SalesAwaitingCost);
        Assert.Equal(720m, result.Summary.ProceedsAwaitingCost);
        Assert.Equal(2, result.Summary.SalesAllTime);   // it is still a sale, just not yet a profit
        Assert.Contains(result.Honesty, h => h.Contains("no record of what you paid"));
    }

    [Fact]
    public void Average_roi_is_capital_weighted_so_one_tiny_flip_cannot_dominate_it()
    {
        // A $2 buy returning 400% alongside a $1,000 buy returning 10% is an 11% portfolio, not a
        // 205% one. The mean of the percentages would report the latter.
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 10m, cost: 2m, fee: 0m, soldUtc: Now.AddDays(-1)),
            Sale(price: 1200m, cost: 1000m, fee: 0m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Equal(208m, result.Summary.NetProfitAllTime);
        Assert.Equal(1002m, result.Summary.CostOfGoodsAllTime);
        Assert.Equal(20.8m, result.Summary.AverageRoiPercent);
    }

    [Fact]
    public void Month_over_month_is_omitted_rather_than_invented_when_last_month_was_empty()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1))), Now);

        Assert.Null(result.Summary.MonthOverMonthPercent);
    }

    [Fact]
    public void Month_over_month_is_reported_when_there_is_a_real_base_to_compare_against()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 600m, cost: 200m, fee: 100m, soldUtc: new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero))), Now);

        Assert.Equal(300m, result.Summary.NetProfitLastMonth);
        Assert.Equal(66.7m, result.Summary.MonthOverMonthPercent);
    }

    [Fact]
    public void Cancelled_orders_are_left_out_of_the_sale_count_entirely()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 900m, cost: 300m, fee: 90m, status: "cancelled", soldUtc: Now.AddDays(-2))), Now);

        Assert.Equal(1, result.Summary.SalesAllTime);
        Assert.Equal(500m, result.Summary.NetProfitAllTime);
    }

    [Fact]
    public void The_chart_buckets_by_month_and_marks_the_current_one()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 700m, cost: 300m, fee: 100m, soldUtc: new DateTimeOffset(2026, 5, 10, 0, 0, 0, TimeSpan.Zero))), Now);

        var may = result.Months.Single(m => m.Month == "2026-05");
        var july = result.Months.Single(m => m.Month == "2026-07");

        Assert.Equal(300m, may.NetProfit);
        Assert.Equal(500m, july.NetProfit);
        Assert.True(july.IsCurrentMonth);
        Assert.False(may.IsCurrentMonth);
        // Trimmed to where the history starts, so a new seller doesn't get a row of empty bars.
        Assert.True(result.Months.Count <= 12);
        Assert.True(result.Months.Count >= 3);
    }

    [Fact]
    public void A_losing_month_is_reported_as_a_loss_rather_than_floored_at_zero()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 300m, cost: 500m, fee: 40m, soldUtc: Now.AddDays(-1))), Now);

        Assert.Equal(-240m, result.Summary.NetProfitAllTime);
        Assert.Equal(-240m, result.Months.Single(m => m.IsCurrentMonth).NetProfit);
    }

    [Fact]
    public void Best_flips_stays_empty_rather_than_putting_a_trophy_on_the_least_bad_loss()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 300m, cost: 500m, fee: 40m, soldUtc: Now.AddDays(-1)),
            Sale(price: 200m, cost: 900m, fee: 30m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Empty(result.BestFlips);
        // The losses are shown instead — they are the rows actually worth reading.
        Assert.Equal(2, result.WorstFlips.Count);
        Assert.Equal(-730m, result.WorstFlips[0].NetProfit);   // biggest loss first
    }

    [Fact]
    public void Losing_sales_never_appear_in_best_flips_alongside_winners()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 300m, cost: 500m, fee: 40m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Single(result.BestFlips);
        Assert.Equal(500m, result.BestFlips[0].NetProfit);
        Assert.Single(result.WorstFlips);
        Assert.Equal(-240m, result.WorstFlips[0].NetProfit);
    }

    [Fact]
    public void Best_returns_ignores_flips_too_small_to_be_worth_repeating()
    {
        // 900% on a $1 buy is a rounding error dressed up as a strategy.
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 10m, cost: 1m, fee: 0m, soldUtc: Now.AddDays(-1)),
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Single(result.BestReturns);
        Assert.Equal(500m, result.BestReturns[0].NetProfit);
    }

    [Fact]
    public void The_split_between_measured_and_estimated_fees_is_reported()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 1000m, cost: 400m, fee: null, soldUtc: Now.AddDays(-2))), Now);

        Assert.Equal(500m, result.Summary.ProfitFromActualFees);
        Assert.Equal(467.10m, result.Summary.ProfitFromEstimatedFees);
        Assert.Equal(1, result.Summary.SalesWithMeasuredFees);
        Assert.Equal(100m, result.Summary.FeesMeasured);
        Assert.Contains(result.Honesty, h => h.Contains("1 of 2 sales"));
    }

    [Fact]
    public void The_fee_split_is_stated_over_fees_not_over_profit()
    {
        // Both sales carry eBay's real fee; neither has a cost yet, so neither contributes profit.
        // Reporting the split by profit would announce "$0 uses eBay's real fees" on an account
        // where every single fee was measured.
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: null, fee: 100m, soldUtc: Now.AddDays(-1)),
            Sale(price: 500m, cost: null, fee: 50m, soldUtc: Now.AddDays(-2))), Now);

        Assert.Equal(0m, result.Summary.ProfitFromActualFees);
        Assert.Equal(2, result.Summary.SalesWithMeasuredFees);
        Assert.Contains(result.Honesty, h => h.Contains("actually charged"));
        Assert.DoesNotContain(result.Honesty, h => h.Contains("$0"));
    }

    [Fact]
    public void With_no_measured_fees_the_honesty_line_says_how_to_get_them()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: null, soldUtc: Now.AddDays(-1))), Now);

        Assert.Equal(0, result.Summary.SalesWithMeasuredFees);
        Assert.Contains(result.Honesty, h => h.Contains("Importing your eBay orders"));
    }

    [Fact]
    public void Everything_measured_says_so_instead_of_saying_nothing()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1))), Now);

        Assert.Contains(result.Honesty, h => h.Contains("actually charged"));
    }

    [Fact]
    public void Ebay_and_hand_logged_profit_are_separated_so_the_total_stays_auditable()
    {
        var result = NewCalculator().Summarize(Computed(
            Sale(price: 1000m, cost: 400m, fee: 100m, soldUtc: Now.AddDays(-1), source: "ebay"),
            Sale(price: 500m, cost: 200m, fee: 50m, soldUtc: Now.AddDays(-2), source: "manual")), Now);

        Assert.Equal(500m, result.Summary.ProfitFromEbay);
        Assert.Equal(250m, result.Summary.ProfitFromManual);
        Assert.Contains(result.Honesty, h => h.Contains("logged yourself"));
    }

    [Fact]
    public void An_empty_ledger_produces_zeros_and_no_best_month_rather_than_throwing()
    {
        var result = NewCalculator().Summarize([], Now);

        Assert.Equal(0m, result.Summary.NetProfitAllTime);
        Assert.Equal(0, result.Summary.SalesAllTime);
        Assert.Null(result.Summary.BestMonthLabel);
        Assert.Null(result.Summary.AverageProfitPerSale);
        Assert.NotEmpty(result.Months);
    }
}
