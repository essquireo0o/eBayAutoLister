using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The pipeline board. Two rules govern every test in here:
//
//   * A projection is never money. Projected and realized profit are separate totals and nothing
//     adds them together.
//   * Capital at risk is only what was actually paid, and it must never be understated.
//
// Most of these tests exist to check the calculator REFUSES to do something: claim a sale it can't
// prove is this deal's, grade a forecast against a half-finished outcome, or nag anyone to go and
// buy something the numbers say loses money.
public class DealPipelineCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly DealPipelineCalculator _calc = new();

    private static DealRecord Deal(
        string stage = DealStages.Sourced,
        decimal? ask = 450m,
        decimal? projectedProfit = 320m,
        decimal? purchase = null,
        int quantity = 1,
        string listingId = "",
        int daysAgo = 1) => new()
    {
        Id = 1,
        Stage = stage,
        Title = "Antminer S19 95TH",
        Source = "craigslist",
        Quantity = quantity,
        AskPrice = ask,
        MaxBuyPrice = 780m,
        ProjectedSalePrice = 1100m,
        ProjectedNetProfit = projectedProfit,
        PurchasePrice = purchase,
        ListingId = listingId,
        CreatedUtc = Now.AddDays(-daysAgo),
        UpdatedUtc = Now.AddDays(-daysAgo),
        StageChangedUtc = Now.AddDays(-daysAgo),
        BoughtUtc = purchase.HasValue ? Now.AddDays(-daysAgo) : null,
        ListedUtc = stage is DealStages.Listed or DealStages.Sold ? Now.AddDays(-daysAgo) : null,
    };

    private static FlipProfit Sale(
        long id = 1, string listingId = "110001", decimal profit = 300m, int daysAgo = 1,
        int quantity = 1, decimal revenue = 1100m, string status = "paid", bool costKnown = true) => new()
    {
        Flip = new FlipRecord
        {
            Id = id, ListingId = listingId, Title = "Antminer S19 95TH",
            SoldUtc = Now.AddDays(-daysAgo), Quantity = quantity, Status = status,
        },
        GrossRevenue = revenue,
        NetProfit = costKnown ? profit : null,
        CostSource = costKnown ? "basis" : "none",
    };

    // ── What belongs to this deal ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_deal_with_no_listing_id_claims_nothing()
    {
        var result = _calc.Build([Deal(DealStages.Listed, purchase: 380m)], [Sale()], Now);

        Assert.Empty(result.Deals[0].FlipIds);
        Assert.Null(result.Deals[0].RealizedProfit);
    }

    // The expensive mistake: a listing ID that has sold fourteen times over two years, claimed
    // whole by a one-unit deal, reporting a $300 flip as $4,200 of realized profit.
    [Fact]
    public void A_one_unit_deal_claims_one_sale_from_a_listing_that_sold_many_times()
    {
        var sales = Enumerable.Range(1, 12)
            .Select(i => Sale(id: i, profit: 300m, daysAgo: 20 - i))
            .ToList();

        var result = _calc.Build([Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 30)], sales, Now);

        Assert.Single(result.Deals[0].FlipIds);
        Assert.Equal(300m, result.Deals[0].RealizedProfit);
    }

    [Fact]
    public void A_ten_unit_deal_claims_up_to_ten_units()
    {
        var sales = Enumerable.Range(1, 20).Select(i => Sale(id: i, profit: 50m, daysAgo: 25 - i)).ToList();

        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 100m, quantity: 10, listingId: "110001", daysAgo: 30)], sales, Now);

        Assert.Equal(10, result.Deals[0].FlipIds.Count);
        Assert.Equal(500m, result.Deals[0].RealizedProfit);
    }

    // A relisted item keeps its SKU and gets a new listing ID, so tracking it today must not
    // reach back and claim last year's sales of the same listing.
    [Fact]
    public void Sales_from_before_the_deal_existed_are_not_claimed()
    {
        var deal = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 5);

        var result = _calc.Build([deal], [Sale(daysAgo: 400)], Now);

        Assert.Empty(result.Deals[0].FlipIds);
    }

    // A deal entered after the fact — bought in March, logged in July — must still find its sale.
    [Fact]
    public void A_retroactively_entered_deal_still_finds_its_sale()
    {
        var deal = Deal(DealStages.Sold, purchase: 380m, listingId: "110001", daysAgo: 0);
        deal.BoughtUtc = Now.AddDays(-120);
        deal.CreatedUtc = Now;

        var result = _calc.Build([deal], [Sale(daysAgo: 40)], Now);

        Assert.Single(result.Deals[0].FlipIds);
    }

    [Fact]
    public void Two_deals_on_one_listing_split_its_sales_rather_than_both_claiming_all()
    {
        var first = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 30);
        var second = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 20);
        second.Id = 2;

        var result = _calc.Build([first, second], [Sale(id: 1, daysAgo: 10), Sale(id: 2, daysAgo: 5)], Now);

        var one = result.Deals.Single(d => d.Id == 1);
        var two = result.Deals.Single(d => d.Id == 2);
        Assert.Single(one.FlipIds);
        Assert.Single(two.FlipIds);
        Assert.NotEqual(one.FlipIds[0], two.FlipIds[0]);
        Assert.Equal(600m, result.Summary.RealizedProfit);
    }

    [Fact]
    public void A_cancelled_sale_is_not_a_sale()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 10)],
            [Sale(status: "cancelled")], Now);

        Assert.Empty(result.Deals[0].FlipIds);
        Assert.Equal(DealStages.Listed, result.Deals[0].Stage);
    }

    [Fact]
    public void A_sku_match_works_when_there_is_no_listing_id()
    {
        var deal = Deal(DealStages.Listed, purchase: 380m, daysAgo: 10);
        deal.Sku = "ING-S19-01";
        var sale = Sale(listingId: "");
        sale.Flip.Sku = "ING-S19-01";

        var result = _calc.Build([deal], [sale], Now);

        Assert.Single(result.Deals[0].FlipIds);
    }

    // ── Where a deal really is ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_imported_sale_moves_the_card_without_anyone_clicking()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 20)], [Sale()], Now);

        Assert.Equal(DealStages.Sold, result.Deals[0].Stage);
        Assert.True(result.Deals[0].StageAutoDerived);
    }

    [Fact]
    public void A_card_nothing_has_sold_on_stays_where_the_seller_put_it()
    {
        var result = _calc.Build([Deal(DealStages.Listed, purchase: 380m, listingId: "110001")], [], Now);

        Assert.Equal(DealStages.Listed, result.Deals[0].Stage);
        Assert.False(result.Deals[0].StageAutoDerived);
    }

    // ── Real money versus forecast money ──────────────────────────────────────────────────────

    [Fact]
    public void A_sourced_deal_has_no_capital_at_risk_however_big_its_forecast()
    {
        var result = _calc.Build([Deal()], [], Now);

        Assert.Equal(0m, result.Summary.CapitalAtRisk);
        Assert.Equal(320m, result.Summary.ProjectedProfitSourced);
        Assert.Equal(0m, result.Summary.ProjectedProfitInMotion);
        Assert.Equal(0m, result.Summary.RealizedProfit);
    }

    [Fact]
    public void Capital_at_risk_is_the_price_paid_plus_the_extras()
    {
        var deal = Deal(DealStages.Bought, purchase: 380m);
        deal.PurchaseExtraCost = 45m;

        var result = _calc.Build([deal], [], Now);

        Assert.Equal(425m, result.Summary.CapitalAtRisk);
        Assert.Equal(425m, result.Deals[0].CapitalSpent);
    }

    [Fact]
    public void A_forecast_on_something_nobody_bought_is_never_money_in_motion()
    {
        var result = _calc.Build([Deal(), Deal(DealStages.Bought, purchase: 380m)], [], Now);

        Assert.Equal(320m, result.Summary.ProjectedProfitSourced);
        Assert.Equal(390m, result.Summary.ProjectedProfitInMotion); // 320 + the $70 haggled off
        Assert.Equal(0m, result.Summary.RealizedProfit);
    }

    // Net profit moves exactly one dollar per dollar paid, so haggling $70 off is $70 more profit.
    [Fact]
    public void Expected_profit_is_rebased_on_what_was_actually_paid()
    {
        var haggled = _calc.Build([Deal(DealStages.Bought, ask: 450m, purchase: 380m)], [], Now).Deals[0];
        var overpaid = _calc.Build([Deal(DealStages.Bought, ask: 450m, purchase: 520m)], [], Now).Deals[0];

        Assert.Equal(320m, haggled.ForecastProfit);
        Assert.Equal(390m, haggled.ExpectedProfit);
        Assert.Equal(70m, haggled.NegotiatedSaving);

        Assert.Equal(250m, overpaid.ExpectedProfit);
        Assert.Equal(-70m, overpaid.NegotiatedSaving);
    }

    [Fact]
    public void Rebasing_scales_with_quantity_and_charges_the_extras_once()
    {
        var deal = Deal(DealStages.Bought, ask: 100m, projectedProfit: 40m, purchase: 90m, quantity: 6);
        deal.PurchaseExtraCost = 60m;

        var card = _calc.Build([deal], [], Now).Deals[0];

        Assert.Equal(240m, card.ForecastProfit);       // 40 × 6, as forecast
        Assert.Equal(240m, card.ExpectedProfit);       // (40 + 10) × 6 − 60 of pickup costs
        Assert.Equal(600m, card.CapitalSpent);         // 90 × 6 + 60
    }

    [Fact]
    public void A_deal_with_no_forecast_contributes_no_projected_profit_rather_than_a_zero()
    {
        var result = _calc.Build([Deal(DealStages.Bought, projectedProfit: null, purchase: 380m)], [], Now);

        Assert.Null(result.Deals[0].ForecastProfit);
        Assert.Null(result.Deals[0].ExpectedProfit);
        Assert.Equal(0m, result.Summary.ProjectedProfitInMotion);
    }

    [Fact]
    public void Realized_profit_comes_from_the_matched_sale_not_from_the_forecast()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 30)],
            [Sale(profit: 412.55m, revenue: 1150m)], Now);

        Assert.Equal(412.55m, result.Summary.RealizedProfit);
        Assert.Equal(1150m, result.Summary.RealizedRevenue);
        Assert.Equal(0m, result.Summary.CapitalAtRisk); // it came back
    }

    [Fact]
    public void A_sold_deal_whose_sale_has_no_cost_reports_no_realized_profit()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 30)],
            [Sale(costKnown: false)], Now);

        Assert.Null(result.Deals[0].RealizedProfit);
        Assert.Equal(1, result.Deals[0].SalesAwaitingCost);
        Assert.Equal(0m, result.Summary.RealizedProfit);
    }

    [Fact]
    public void A_part_sold_lot_still_has_the_unsold_half_at_risk()
    {
        var deal = Deal(DealStages.Listed, purchase: 100m, quantity: 4, listingId: "110001", daysAgo: 30);

        var result = _calc.Build([deal], [Sale(id: 1, profit: 60m), Sale(id: 2, profit: 60m, daysAgo: 2)], Now);

        Assert.Equal(DealStages.Listed, result.Deals[0].Stage); // half a lot is not a closed deal
        Assert.Equal(2, result.Deals[0].UnitsSold);
        Assert.Equal(200m, result.Deals[0].CapitalAtRisk);   // 2 of 4 units still out
        Assert.Equal(400m, result.Deals[0].CapitalSpent);
        Assert.Equal(120m, result.Deals[0].RealizedProfit);
    }

    // A write-off is a settled loss, not money in motion — leaving it in "at risk" keeps reporting
    // cash the seller cannot get back.
    [Fact]
    public void Dropped_capital_is_spent_not_at_risk()
    {
        var deal = Deal(DealStages.Dropped, purchase: 380m);

        var result = _calc.Build([deal], [], Now);

        Assert.Equal(0m, result.Summary.CapitalAtRisk);
        Assert.Equal(380m, result.Summary.CapitalDeployedAllTime);
        Assert.Equal(0, result.Summary.ActiveDeals);
    }

    // ── Grading the forecast ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_closed_deal_grades_its_forecast()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, ask: 450m, purchase: 450m, listingId: "110001", daysAgo: 40)],
            [Sale(profit: 256m)], Now);

        Assert.Equal(-64m, result.Deals[0].ProfitVariance);
        Assert.Equal(-20m, result.Deals[0].ProfitVariancePercent);
        Assert.Equal(1, result.Summary.GradedDeals);
        Assert.Equal(80m, result.Summary.ForecastAccuracyPercent);
        Assert.Equal(-64m, result.Summary.ForecastDelta);
    }

    // A 2-of-10 partial sale measured against a 10-unit forecast reports an 80% miss on a deal
    // that is going exactly to plan.
    [Fact]
    public void A_half_sold_lot_is_not_graded()
    {
        var deal = Deal(DealStages.Listed, ask: 100m, projectedProfit: 40m, purchase: 100m, quantity: 10,
            listingId: "110001", daysAgo: 30);

        var result = _calc.Build([deal], [Sale(id: 1, profit: 40m), Sale(id: 2, profit: 40m, daysAgo: 2)], Now);

        Assert.Null(result.Deals[0].ProfitVariance);
        Assert.Equal(0, result.Summary.GradedDeals);
    }

    [Fact]
    public void A_sale_missing_its_cost_is_not_graded_either()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 450m, listingId: "110001", daysAgo: 40)],
            [Sale(costKnown: false)], Now);

        Assert.Equal(0, result.Summary.GradedDeals);
        Assert.Null(result.Summary.ForecastAccuracyPercent);
    }

    [Fact]
    public void Accuracy_is_omitted_when_the_forecast_it_would_divide_by_is_not_positive()
    {
        var deal = Deal(DealStages.Listed, ask: 450m, projectedProfit: 0m, purchase: 450m,
            listingId: "110001", daysAgo: 40);

        var result = _calc.Build([deal], [Sale(profit: 120m)], Now);

        Assert.Equal(1, result.Summary.GradedDeals);
        Assert.Null(result.Summary.ForecastAccuracyPercent);
        Assert.Equal(120m, result.Summary.ForecastDelta);
    }

    // ── What to do next ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Money_in_a_box_is_the_prompt_and_it_escalates()
    {
        var fresh = _calc.Build([Deal(DealStages.Bought, purchase: 380m, daysAgo: 1)], [], Now).Deals[0];
        var stalling = _calc.Build([Deal(DealStages.Bought, purchase: 380m, daysAgo: 6)], [], Now).Deals[0];
        var stuck = _calc.Build([Deal(DealStages.Bought, purchase: 380m, daysAgo: 40)], [], Now).Deals[0];

        Assert.Equal("List it", fresh.NextAction!.Label);
        Assert.Equal("normal", fresh.NextAction.Urgency);
        Assert.Equal("warn", stalling.NextAction!.Urgency);
        Assert.Equal("urgent", stuck.NextAction!.Urgency);
        Assert.Equal(380m, stuck.NextAction.AmountAtStake);
    }

    // Found by running this against the real app: a seller logging a purchase they made in January
    // clicks the button today, and measuring the stall from the click reported zero days — hiding
    // the oldest, most stuck money on the board behind the newest card.
    [Fact]
    public void A_purchase_dated_in_the_past_is_stalled_from_the_date_it_was_bought()
    {
        var deal = Deal(DealStages.Bought, purchase: 380m, daysAgo: 0);
        deal.BoughtUtc = Now.AddDays(-200);
        deal.StageChangedUtc = Now;   // the card was clicked just now

        var card = _calc.Build([deal], [], Now).Deals[0];

        Assert.Equal(200, card.DaysInStage);
        Assert.Equal("urgent", card.NextAction!.Urgency);
    }

    [Fact]
    public void Stalled_capital_is_reported_as_a_dollar_figure()
    {
        var result = _calc.Build(
            [Deal(DealStages.Bought, purchase: 380m, daysAgo: 20), Deal(DealStages.Bought, purchase: 900m, daysAgo: 1)],
            [], Now);

        Assert.Equal(1, result.Summary.StalledDeals);
        Assert.Equal(380m, result.Summary.StalledCapital);
    }

    // The most useful thing a pipeline can do with a bad deal is fail to nag anyone to buy it.
    [Fact]
    public void Nobody_is_told_to_go_and_buy_something_that_loses_money()
    {
        var card = _calc.Build([Deal(projectedProfit: -40m)], [], Now).Deals[0];

        Assert.Null(card.NextAction);
        Assert.Contains(card.Flags, f => f.Contains("doesn't clear its costs"));
    }

    [Fact]
    public void A_stale_sourced_deal_is_asked_about_rather_than_pushed()
    {
        var card = _calc.Build([Deal(daysAgo: 10)], [], Now).Deals[0];

        Assert.Equal("Check it's still there", card.NextAction!.Label);
        Assert.Contains("$780", card.NextAction.Detail); // the ceiling travels with the prompt
    }

    [Fact]
    public void A_listed_deal_selling_inside_its_own_forecast_window_is_left_alone()
    {
        var deal = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 20);
        deal.ProjectedDaysToCash = 30;

        Assert.Null(_calc.Build([deal], [], Now).Deals[0].NextAction);
    }

    // A part that was always going to take four months is not overdue at day 46.
    [Fact]
    public void Overdue_is_measured_against_the_deals_own_forecast()
    {
        var slow = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 60);
        slow.ProjectedDaysToCash = 120;
        var fast = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 60);
        fast.ProjectedDaysToCash = 14;
        fast.Id = 2;

        var result = _calc.Build([slow, fast], [], Now);

        Assert.Null(result.Deals.Single(d => d.Id == 1).NextAction);
        var overdue = result.Deals.Single(d => d.Id == 2);
        Assert.Equal("Reprice it or send offers", overdue.NextAction!.Label);
        Assert.Equal("urgent", overdue.NextAction.Urgency);
        Assert.Equal("inventory", overdue.NextAction.Target);
        Assert.Equal(1, result.Summary.OverdueDeals);
    }

    [Fact]
    public void A_listing_with_no_id_is_asked_for_one_because_the_sale_can_never_find_it()
    {
        var card = _calc.Build([Deal(DealStages.Listed, purchase: 380m, daysAgo: 2)], [], Now).Deals[0];

        Assert.Equal("Add the eBay listing ID", card.NextAction!.Label);
    }

    // Real money the seller has already made, invisible in Money Made for want of one number the
    // pipeline is already holding.
    [Fact]
    public void A_sold_deal_whose_cost_never_reached_the_sale_offers_to_apply_it()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 40)],
            [Sale(costKnown: false, revenue: 1100m)], Now);

        var action = result.Deals[0].NextAction!;
        Assert.Equal("Apply what you paid", action.Label);
        Assert.Contains("$380.00", action.Detail);
        Assert.Equal(1100m, action.AmountAtStake);
        Assert.Equal(1, result.Summary.SalesAwaitingCost);
    }

    [Fact]
    public void A_closed_deal_with_nothing_outstanding_asks_for_nothing()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 40)], [Sale()], Now);

        Assert.Null(result.Deals[0].NextAction);
    }

    [Fact]
    public void The_do_this_next_list_leads_with_urgency_then_dollars()
    {
        var small = Deal(DealStages.Bought, purchase: 100m, daysAgo: 30);
        var big = Deal(DealStages.Bought, purchase: 5000m, daysAgo: 1);
        big.Id = 2;
        var biggerUrgent = Deal(DealStages.Bought, purchase: 9000m, daysAgo: 30);
        biggerUrgent.Id = 3;

        var actions = _calc.Build([small, big, biggerUrgent], [], Now).NextActions;

        Assert.Equal(3, actions[0].DealId);  // urgent and largest
        Assert.Equal(1, actions[1].DealId);  // urgent, smaller
        Assert.Equal(2, actions[2].DealId);  // big, but nothing wrong with it
    }

    // ── Flags ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Paying_over_the_break_even_ceiling_is_said_out_loud()
    {
        var card = _calc.Build([Deal(DealStages.Bought, purchase: 840m)], [], Now).Deals[0];

        Assert.Contains(card.Flags, f => f.Contains("$60 over") && f.Contains("break-even ceiling"));
    }

    [Fact]
    public void Haggling_is_credited_because_it_is_profit_with_no_fee_on_it()
    {
        var card = _calc.Build([Deal(DealStages.Bought, ask: 450m, purchase: 380m)], [], Now).Deals[0];

        Assert.Contains(card.Flags, f => f.Contains("Haggled $70"));
    }

    // ── Time ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_real_cash_cycle_is_measured_from_money_out_to_money_back()
    {
        var deal = Deal(DealStages.Listed, purchase: 380m, listingId: "110001", daysAgo: 60);

        var card = _calc.Build([deal], [Sale(daysAgo: 25)], Now).Deals[0];

        Assert.Equal(35, card.DaysToCashActual);
    }

    [Fact]
    public void A_median_cash_cycle_needs_three_closed_deals_to_mean_anything()
    {
        List<DealRecord> Deals(int n) => Enumerable.Range(1, n).Select(i =>
        {
            var d = Deal(DealStages.Listed, purchase: 380m, listingId: $"1100{i:00}", daysAgo: 60);
            d.Id = i;
            return d;
        }).ToList();

        List<FlipProfit> Sales(int n) => Enumerable.Range(1, n)
            .Select(i => Sale(id: i, listingId: $"1100{i:00}", daysAgo: 30 - i)).ToList();

        Assert.Null(_calc.Build(Deals(2), Sales(2), Now).Summary.MedianDaysToCash);
        Assert.NotNull(_calc.Build(Deals(3), Sales(3), Now).Summary.MedianDaysToCash);
    }

    // ── The board ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_board_always_has_its_four_columns_even_when_empty()
    {
        var result = _calc.Build([], [], Now);

        Assert.Equal(4, result.Stages.Count);
        Assert.Equal(["sourced", "bought", "listed", "sold"], result.Stages.Select(s => s.Stage));
        Assert.All(result.Stages, s => Assert.Equal(0, s.Count));
    }

    [Fact]
    public void Dropped_deals_are_off_the_board_but_still_counted()
    {
        var result = _calc.Build([Deal(DealStages.Dropped, purchase: 380m)], [], Now);

        Assert.All(result.Stages, s => Assert.Equal(0, s.Count));
        Assert.Equal(1, result.Summary.TotalDeals);
        Assert.Single(result.Deals);
    }

    [Fact]
    public void The_honesty_block_always_says_a_projection_is_not_money()
    {
        var result = _calc.Build([Deal()], [], Now);

        Assert.Contains(result.Honesty, h => h.Contains("never added to what you've actually made"));
    }

    [Fact]
    public void The_honesty_block_reports_the_grade_once_there_is_one()
    {
        var result = _calc.Build(
            [Deal(DealStages.Listed, ask: 450m, purchase: 450m, listingId: "110001", daysAgo: 40)],
            [Sale(profit: 256m)], Now);

        Assert.Contains(result.Honesty, h => h.Contains("20% under forecast"));
    }
}
