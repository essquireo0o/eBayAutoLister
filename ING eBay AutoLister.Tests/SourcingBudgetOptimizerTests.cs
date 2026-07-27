using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// This is the one screen in the app that decides where real cash goes, so what's pinned here is
// the money: the basket never costs more than the seller has, it is genuinely optimal rather than
// merely plausible, it never spends on a deal that loses money or rests on nothing, it never buys
// the same post twice, and the lift it claims over buying down the list is measured rather than
// asserted.
public class SourcingBudgetOptimizerTests
{
    private static readonly DateTime Today = new(2026, 7, 27);

    private static readonly SourcingBudgetOptimizer Optimizer = new();

    private static BudgetCandidate Deal(
        string title, decimal buy, decimal profit, int? days = 20, int comps = 12,
        int quantity = 1, string origin = BudgetOrigins.Scan, string source = "craigslist",
        string verdict = "solid", decimal? target = null) => new()
        {
            Id = title,
            Title = title,
            Source = source,
            SourceLabel = source,
            Url = $"https://example.test/{title}",
            BuyPrice = buy,
            NetProfit = profit,
            Quantity = quantity,
            DaysToCash = days,
            CompCount = comps,
            ConfidenceScore = 70,
            Verdict = verdict,
            Origin = origin,
            TargetOffer = target,
        };

    private static BudgetPlanResult Plan(decimal budget, params BudgetCandidate[] candidates) =>
        Optimizer.Plan(new BudgetPlanRequest { Budget = budget, Candidates = [.. candidates] }, Today);

    // ── The point of the whole feature ────────────────────────────────────────────────────────

    [Fact]
    public void The_basket_beats_buying_the_biggest_profit_first()
    {
        // The classic trap: the fattest single deal eats the budget and blocks two that together
        // make more. A ranked list can't see this; an allocation can.
        var result = Plan(500m,
            Deal("big", buy: 450m, profit: 300m),
            Deal("first", buy: 250m, profit: 200m),
            Deal("second", buy: 250m, profit: 200m));

        Assert.Equal("ok", result.Status);
        Assert.Equal(2, result.Plan.Picks.Count);
        Assert.Equal(400m, result.Plan.TotalNetProfit);
        Assert.Equal(500m, result.Plan.CapitalDeployed);
        // And the greedy answer it beat is reported, in dollars, rather than being implied.
        Assert.Equal(300m, result.Comparison.TotalNetProfit);
        Assert.Equal(100m, result.Comparison.ExtraProfit);
    }

    [Fact]
    public void The_claimed_lift_is_never_negative()
    {
        // The optimizer must never report itself as doing worse than the obvious thing — if it
        // ever could, the arithmetic behind it would be wrong.
        var result = Plan(300m,
            Deal("a", buy: 100m, profit: 40m),
            Deal("b", buy: 120m, profit: 55m),
            Deal("c", buy: 90m, profit: 30m),
            Deal("d", buy: 200m, profit: 90m));

        Assert.True(result.Comparison.ExtraProfit >= 0m);
        Assert.True(result.Plan.TotalNetProfit >= result.Comparison.TotalNetProfit);
    }

    [Fact]
    public void The_basket_is_actually_optimal_checked_against_every_possible_basket()
    {
        // Brute force over all 2^n subsets. The knapsack is the only part of this app whose
        // correctness is a mathematical claim rather than a judgement call, so it gets checked
        // against the definition instead of against a hand-picked expectation.
        var deals = new[]
        {
            Deal("a", buy: 130m, profit: 61m),
            Deal("b", buy: 92m, profit: 44m),
            Deal("c", buy: 217m, profit: 96m),
            Deal("d", buy: 45m, profit: 19m),
            Deal("e", buy: 168m, profit: 88m),
            Deal("f", buy: 76m, profit: 31m),
            Deal("g", buy: 249m, profit: 120m),
            Deal("h", buy: 38m, profit: 12m),
        };

        const decimal budget = 500m;
        var best = 0m;
        for (var mask = 0; mask < 1 << deals.Length; mask++)
        {
            decimal cost = 0, profit = 0;
            for (var i = 0; i < deals.Length; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                cost += deals[i].TotalCost;
                profit += deals[i].TotalProfit;
            }
            if (cost <= budget && profit > best) best = profit;
        }

        var result = Plan(budget, deals);
        Assert.Equal(best, result.Plan.TotalNetProfit);
    }

    [Fact]
    public void Awkward_prices_are_still_solved_exactly()
    {
        // Cents matter: an item priced at $99.99 must not be rounded into unaffordability, and two
        // of them must not be rounded into fitting a $199 budget.
        var result = Plan(199m,
            Deal("a", buy: 99.99m, profit: 50m),
            Deal("b", buy: 99.99m, profit: 50m),
            Deal("c", buy: 98.50m, profit: 40m));

        Assert.Equal(2, result.Plan.Picks.Count);
        Assert.Equal(198.49m, result.Plan.CapitalDeployed);
        Assert.Equal(90m, result.Plan.TotalNetProfit);
    }

    // ── The seller's money is never overspent ─────────────────────────────────────────────────

    [Fact]
    public void The_basket_never_costs_more_than_the_budget()
    {
        var result = Plan(100m,
            Deal("a", buy: 60m, profit: 40m),
            Deal("b", buy: 55m, profit: 38m),
            Deal("c", buy: 45m, profit: 25m));

        Assert.True(result.Plan.CapitalDeployed <= 100m);
        Assert.Equal(result.Plan.CapitalDeployed, result.Plan.Picks.Sum(p => p.Spend));
        Assert.Equal(100m - result.Plan.CapitalDeployed, result.Plan.Leftover);
    }

    [Fact]
    public void Held_back_cash_is_never_spent()
    {
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 500m,
            Reserve = 200m,
            Candidates = [Deal("a", buy: 280m, profit: 150m), Deal("b", buy: 300m, profit: 200m)],
        }, Today);

        Assert.Equal(300m, result.Spendable);
        Assert.True(result.Plan.CapitalDeployed <= 300m);
        // The $500 stays visible on the plan; the reserve is held back, not forgotten.
        Assert.Equal(500m, result.Plan.Budget);
        Assert.Equal(300m, result.Plan.Spendable);
    }

    [Fact]
    public void Holding_back_everything_buys_nothing_rather_than_dipping_into_the_reserve()
    {
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 400m, Reserve = 400m, Candidates = [Deal("a", buy: 100m, profit: 90m)],
        }, Today);

        Assert.Equal("no_budget", result.Status);
        Assert.Empty(result.Plan.Picks);
    }

    [Fact]
    public void A_lot_is_bought_whole_or_not_at_all()
    {
        // Six units at $80 is a $480 decision, not six $80 ones — that is how the thing is sold.
        var lot = Deal("pallet", buy: 80m, profit: 45m, quantity: 6);
        var result = Plan(400m, lot, Deal("single", buy: 300m, profit: 120m));

        Assert.Single(result.Plan.Picks);
        Assert.Equal("single", result.Plan.Picks[0].Title);
        // Left out whole: the basket never buys four of the six units to make the money fit.
        var missed = Assert.Single(result.LeftOut, s => s.Title == "pallet");
        Assert.Equal(480m, missed.BuyPrice);
    }

    [Fact]
    public void A_lot_that_fits_is_costed_and_credited_across_every_unit()
    {
        var result = Plan(600m, Deal("pallet", buy: 80m, profit: 45m, quantity: 6));

        var pick = Assert.Single(result.Plan.Picks);
        Assert.Equal(480m, pick.Spend);
        Assert.Equal(270m, pick.TotalNetProfit);
        Assert.Equal(45m, pick.NetProfit);
        Assert.Equal(6, pick.Quantity);
    }

    // ── What the cash is never pointed at ─────────────────────────────────────────────────────

    [Fact]
    public void A_deal_that_loses_money_is_never_bought_however_much_budget_is_left()
    {
        var result = Plan(1000m,
            Deal("good", buy: 100m, profit: 80m),
            Deal("loser", buy: 200m, profit: -30m));

        Assert.Single(result.Plan.Picks);
        Assert.Equal(900m, result.Plan.Leftover);
        Assert.Contains(result.LeftOut, s => s.Title == "loser" && s.ReasonCode == "loses_money");
    }

    [Fact]
    public void Thin_sold_history_is_kept_out_of_the_basket_by_default()
    {
        var result = Plan(500m,
            Deal("solid", buy: 100m, profit: 60m, comps: 9),
            Deal("thin", buy: 120m, profit: 200m, comps: 1, verdict: "thin"));

        Assert.Single(result.Plan.Picks);
        Assert.Equal("solid", result.Plan.Picks[0].Title);
        Assert.Contains(result.LeftOut, s => s.Title == "thin" && s.ReasonCode == "thin_evidence");
    }

    [Fact]
    public void Thin_deals_can_be_let_in_but_only_on_purpose()
    {
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 500m,
            IncludeThin = true,
            Candidates = [Deal("thin", buy: 120m, profit: 200m, comps: 1, verdict: "thin")],
        }, Today);

        Assert.Single(result.Plan.Picks);
    }

    [Fact]
    public void A_deal_priced_off_nothing_is_never_bought_even_with_a_profit_attached()
    {
        var result = Plan(500m, Deal("mystery", buy: 50m, profit: 400m, comps: 0, verdict: "no_data"));

        Assert.Empty(result.Plan.Picks);
        Assert.Equal("nothing_affordable", result.Status);
        Assert.Contains(result.LeftOut, s => s.ReasonCode == "thin_evidence");
    }

    [Fact]
    public void The_same_post_is_never_bought_twice()
    {
        // Tracked last week and scanned again this morning is one item, one price, one profit.
        var scanned = Deal("drill", buy: 100m, profit: 70m, source: "craigslist");
        var tracked = Deal("drill", buy: 140m, profit: 40m, source: "craigslist", origin: BudgetOrigins.Tracked);

        var result = Plan(500m, scanned, tracked);

        Assert.Equal(1, result.DuplicatesMerged);
        var pick = Assert.Single(result.Plan.Picks);
        // The live scan wins: its price is what the post says today.
        Assert.Equal(100m, pick.Spend);
        Assert.Equal(70m, pick.TotalNetProfit);
    }

    [Fact]
    public void The_live_scan_wins_the_dedupe_whichever_order_the_two_arrive_in()
    {
        var tracked = Deal("drill", buy: 140m, profit: 40m, origin: BudgetOrigins.Tracked);
        var scanned = Deal("drill", buy: 100m, profit: 70m);

        var result = Plan(500m, tracked, scanned);

        Assert.Equal(100m, Assert.Single(result.Plan.Picks).Spend);
    }

    // ── Speed, and never pretending to know it ────────────────────────────────────────────────

    [Fact]
    public void A_deal_with_no_measured_speed_is_never_promised_a_date()
    {
        var result = Plan(500m,
            Deal("measured", buy: 100m, profit: 60m, days: 18),
            Deal("unmeasured", buy: 150m, profit: 90m, days: null));

        Assert.Equal(2, result.Plan.Picks.Count);
        Assert.Equal(1, result.Plan.UnknownSpeedCount);
        // One unknown in the basket and there is no honest "all your money back by" date.
        Assert.Null(result.Plan.AllCashBackBy);
        Assert.NotNull(result.Plan.FirstCashBackBy);
    }

    [Fact]
    public void A_fully_measured_basket_says_exactly_when_the_last_of_the_money_lands()
    {
        var result = Plan(500m,
            Deal("quick", buy: 100m, profit: 40m, days: 10),
            Deal("slower", buy: 200m, profit: 90m, days: 30));

        Assert.Equal(0, result.Plan.UnknownSpeedCount);
        Assert.Equal(10, result.Plan.FastestDaysToCash);
        Assert.Equal(30, result.Plan.SlowestDaysToCash);
        Assert.Equal(Today.AddDays(30).ToString("MMM d"), result.Plan.AllCashBackBy);
        Assert.Equal(Today.AddDays(10).ToString("MMM d"), result.Plan.FirstCashBackBy);
    }

    [Fact]
    public void An_unmeasured_deal_is_never_counted_as_fast_cash()
    {
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 500m,
            Objective = SourcingBudgetOptimizer.ObjectiveFastCash,
            Candidates = [Deal("unmeasured", buy: 100m, profit: 300m, days: null)],
        }, Today);

        Assert.Empty(result.Plan.Picks);
        Assert.Contains("inside", result.Plan.Headline);
    }

    [Fact]
    public void A_horizon_keeps_slow_money_out_of_the_basket()
    {
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 500m,
            MaxDaysToCash = 21,
            Candidates =
            [
                Deal("fast", buy: 100m, profit: 40m, days: 14),
                Deal("slow", buy: 300m, profit: 250m, days: 120),
            ],
        }, Today);

        Assert.Single(result.Plan.Picks);
        Assert.Equal("fast", result.Plan.Picks[0].Title);
        Assert.Contains(result.LeftOut, s => s.Title == "slow" && s.ReasonCode == "too_slow");
    }

    [Fact]
    public void The_days_to_cash_of_the_basket_is_weighted_by_the_money_not_by_the_line_count()
    {
        // $400 tied up for 40 days and $100 for 10 is not "25 days on average" — most of the money
        // is in the slow one, and the average has to say so.
        var result = Plan(500m,
            Deal("cheap-fast", buy: 100m, profit: 30m, days: 10),
            Deal("dear-slow", buy: 400m, profit: 120m, days: 40));

        Assert.Equal(2, result.Plan.Picks.Count);
        Assert.Equal(34m, result.Plan.WeightedDaysToCash);
    }

    [Fact]
    public void The_fastest_money_is_listed_first_because_that_is_the_order_to_work_them_in()
    {
        var result = Plan(500m,
            Deal("slow", buy: 200m, profit: 150m, days: 60),
            Deal("fast", buy: 100m, profit: 40m, days: 9));

        Assert.Equal("fast", result.Plan.Picks[0].Title);
        Assert.Equal(1, result.Plan.Picks[0].Rank);
        // Running totals go down the basket in that same order.
        Assert.Equal(100m, result.Plan.Picks[0].CumulativeSpend);
        Assert.Equal(300m, result.Plan.Picks[1].CumulativeSpend);
        Assert.Equal(190m, result.Plan.Picks[1].CumulativeProfit);
    }

    [Fact]
    public void The_per_day_objective_buys_the_money_that_works_hardest_not_the_biggest_pile()
    {
        // Same $300: one big slow flip, or three quick ones that each pay more per day.
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 300m,
            Objective = SourcingBudgetOptimizer.ObjectivePerDay,
            Candidates =
            [
                Deal("slow-big", buy: 300m, profit: 200m, days: 200),
                Deal("q1", buy: 100m, profit: 50m, days: 10),
                Deal("q2", buy: 100m, profit: 50m, days: 10),
                Deal("q3", buy: 100m, profit: 50m, days: 10),
            ],
        }, Today);

        Assert.Equal(3, result.Plan.Picks.Count);
        Assert.DoesNotContain(result.Plan.Picks, p => p.Title == "slow-big");
        // And the alternative that maximises the total is still offered, so the trade-off is the
        // seller's to make rather than the app's.
        var profitPlan = result.Alternatives.Single(p => p.Objective == SourcingBudgetOptimizer.ObjectiveProfit);
        Assert.Equal(200m, profitPlan.TotalNetProfit);
        Assert.Equal(150m, result.Plan.TotalNetProfit);
    }

    [Fact]
    public void Every_objective_is_solved_every_time_so_the_trade_off_is_visible()
    {
        var result = Plan(500m, Deal("a", buy: 100m, profit: 60m, days: 14));

        Assert.Equal(SourcingBudgetOptimizer.ObjectiveProfit, result.Plan.Objective);
        Assert.Equal(2, result.Alternatives.Count);
        Assert.Contains(result.Alternatives, p => p.Objective == SourcingBudgetOptimizer.ObjectiveFastCash);
        Assert.Contains(result.Alternatives, p => p.Objective == SourcingBudgetOptimizer.ObjectivePerDay);
    }

    // ── The numbers on the plan ───────────────────────────────────────────────────────────────

    [Fact]
    public void Roi_is_re_derived_from_the_two_numbers_beside_it()
    {
        var result = Plan(500m, Deal("a", buy: 200m, profit: 50m, days: 20));

        var pick = Assert.Single(result.Plan.Picks);
        Assert.Equal(25m, pick.RoiPercent);
        Assert.Equal(2.5m, pick.ProfitPerDay);
        Assert.Equal(25m, result.Plan.BlendedRoiPercent);
    }

    [Fact]
    public void The_negotiation_upside_is_stated_as_a_ceiling_and_only_where_there_is_one()
    {
        var result = Plan(500m,
            Deal("haggle", buy: 200m, profit: 80m, target: 160m),
            Deal("firm", buy: 100m, profit: 40m, target: null));

        Assert.Equal(40m, result.Plan.NegotiationUpside);
        Assert.Equal(1, result.Plan.NegotiableCount);
        Assert.Contains("if all 1 sellers took your opening offer", result.Plan.Note);
    }

    [Fact]
    public void A_free_item_costs_no_budget_and_is_always_taken()
    {
        var result = Plan(100m,
            Deal("free", buy: 0m, profit: 35m),
            Deal("paid", buy: 100m, profit: 60m));

        Assert.Equal(2, result.Plan.Picks.Count);
        Assert.Equal(100m, result.Plan.CapitalDeployed);
        Assert.Equal(95m, result.Plan.TotalNetProfit);
    }

    [Fact]
    public void More_cash_is_only_claimed_to_buy_more_when_it_actually_would()
    {
        var result = Plan(200m,
            Deal("a", buy: 200m, profit: 100m),
            Deal("b", buy: 250m, profit: 160m));

        // b costs more than the budget, so a bigger budget genuinely would buy more.
        Assert.NotNull(result.Stretch);
        Assert.Equal(50m, result.Stretch!.ExtraBudget);
        Assert.Equal(60m, result.Stretch.ExtraProfit);
    }

    [Fact]
    public void When_the_pool_is_already_fully_bought_extra_cash_is_reported_as_buying_nothing()
    {
        var result = Plan(500m, Deal("a", buy: 100m, profit: 60m));

        Assert.NotNull(result.Stretch);
        Assert.Equal(0m, result.Stretch!.ExtraProfit);
        Assert.Contains("buys nothing extra", result.Stretch.Note);
    }

    // ── Saying why, when the answer is no ─────────────────────────────────────────────────────

    [Fact]
    public void A_deal_left_out_only_for_want_of_cash_is_named_as_the_next_one_to_buy()
    {
        var result = Plan(150m,
            Deal("bought", buy: 150m, profit: 90m),
            Deal("nearly", buy: 140m, profit: 80m));

        var missed = Assert.Single(result.LeftOut, s => s.Title == "nearly");
        Assert.Equal("not_enough_left", missed.ReasonCode);
        Assert.Contains("free up more cash", missed.Reason);
    }

    [Fact]
    public void A_deal_this_objective_cannot_rank_is_named_as_such_not_silently_dropped()
    {
        // "Hardest-working cash" needs a measured speed to divide by. A profitable deal without one
        // isn't rejected — it is told where it did end up, so the seller doesn't lose sight of it.
        var result = Optimizer.Plan(new BudgetPlanRequest
        {
            Budget = 500m,
            Objective = SourcingBudgetOptimizer.ObjectivePerDay,
            Candidates = [Deal("measured", buy: 100m, profit: 60m, days: 15), Deal("unmeasured", buy: 120m, profit: 90m, days: null)],
        }, Today);

        Assert.Single(result.Plan.Picks);
        var missed = Assert.Single(result.LeftOut, s => s.Title == "unmeasured");
        Assert.Equal("objective_excluded", missed.ReasonCode);
        Assert.Contains("Most money", missed.Reason);
        // And it really is in that other basket, not lost between the two.
        var profitPlan = result.Alternatives.Single(p => p.Objective == SourcingBudgetOptimizer.ObjectiveProfit);
        Assert.Contains(profitPlan.Picks, p => p.Title == "unmeasured");
    }

    [Fact]
    public void A_deal_just_past_the_budget_is_kept_in_view_as_the_one_to_buy_next()
    {
        var result = Plan(200m,
            Deal("affordable", buy: 200m, profit: 90m),
            Deal("just-past", buy: 230m, profit: 180m),
            Deal("way-past", buy: 900m, profit: 600m));

        Assert.Equal("affordable", Assert.Single(result.Plan.Picks).Title);
        // Just past the budget: still worth naming, because $30 more changes the answer.
        Assert.Contains(result.LeftOut, s => s.Title == "just-past" && s.ReasonCode == "not_enough_left");
        // Way past it: out of the conversation entirely.
        Assert.Contains(result.LeftOut, s => s.Title == "way-past" && s.ReasonCode == "over_budget");
    }

    [Fact]
    public void Nothing_affordable_says_what_the_cheapest_real_deal_actually_costs()
    {
        var result = Plan(50m,
            Deal("a", buy: 300m, profit: 200m),
            Deal("b", buy: 120m, profit: 90m));

        Assert.Equal("nothing_affordable", result.Status);
        Assert.Contains("$120", result.Message);
    }

    [Fact]
    public void No_budget_and_no_deals_are_different_answers()
    {
        Assert.Equal("no_budget", Plan(0m, Deal("a", buy: 100m, profit: 60m)).Status);
        Assert.Equal("no_candidates", Plan(500m).Status);
    }

    [Fact]
    public void A_tracked_deal_with_no_recorded_comp_count_is_the_sellers_call_not_ours()
    {
        // The seller put this on their own board. The screen labels where the number came from
        // instead of overruling them about their own deal.
        var result = Plan(500m,
            Deal("tracked", buy: 100m, profit: 60m, comps: 0, verdict: "", origin: BudgetOrigins.Tracked));

        var pick = Assert.Single(result.Plan.Picks);
        Assert.Equal(BudgetOrigins.Tracked, pick.Origin);
        Assert.Equal("Tracked deal", pick.OriginLabel);
        Assert.Equal(1, result.TrackedDealsIncluded);
        Assert.Contains(result.Notes, n => n.Contains("frozen"));
    }

    [Fact]
    public void A_tracked_deal_whose_evidence_IS_recorded_and_thin_is_still_kept_out()
    {
        var result = Plan(500m,
            Deal("tracked-thin", buy: 100m, profit: 60m, comps: 1, origin: BudgetOrigins.Tracked));

        Assert.Empty(result.Plan.Picks);
        Assert.Contains(result.LeftOut, s => s.ReasonCode == "thin_evidence");
    }

    [Fact]
    public void A_big_pool_still_answers_and_still_never_overspends()
    {
        // The bound that keeps a click from becoming a hang, exercised at the size that trips it.
        var deals = Enumerable.Range(1, 400)
            .Select(i => Deal($"d{i}", buy: 20m + i % 97, profit: 5m + i % 31, days: 10 + i % 40))
            .ToArray();

        var result = Plan(2000m, deals);

        Assert.Equal("ok", result.Status);
        Assert.True(result.EligibleCount <= SourcingBudgetOptimizer.MaxCandidates);
        Assert.True(result.Plan.CapitalDeployed <= 2000m);
        Assert.True(result.Plan.TotalNetProfit >= result.Comparison.TotalNetProfit);
        Assert.Contains(result.Notes, n => n.Contains("profit per dollar"));
    }

    [Fact]
    public void A_large_budget_is_still_solved_within_the_grid_and_still_fits()
    {
        // Big budgets coarsen the solve grid. What must not change is that the basket fits.
        var deals = Enumerable.Range(1, 40)
            .Select(i => Deal($"d{i}", buy: 900m + i * 37, profit: 200m + i * 11))
            .ToArray();

        var result = Plan(25_000m, deals);

        Assert.True(result.Plan.CapitalDeployed <= 25_000m);
        Assert.Equal(result.Plan.CapitalDeployed, result.Plan.Picks.Sum(p => p.Spend));
    }
}
