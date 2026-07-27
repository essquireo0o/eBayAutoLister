using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Aging stock is where a reseller's capital quietly dies, and the two ways out of it both spend
// real money: a markdown ladder gives up margin, and a bundle gives away an item that was already
// going to sell. So the cases below pin the things that would cost the seller if they were wrong —
// that no rung of any ladder goes under the break-even floor, that a listing which is actually
// selling is never dragged into a rescue, that a bundle is only suggested when it beats what
// already happens today, and that no item is promised to two different buyers at once.
public class AgingInventoryRescuerTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly AgingInventoryRescuer Rescuer = new(Profit);
    private static readonly FeeProfile Fees = new();   // 13.25% + $0.40, no promoted/shipping/labor
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    // Mirrors what InventoryHealthAnalyzer.Build produces, so the rescuer is exercised against the
    // same shape of row it actually receives in the app rather than an idealised one.
    private static InventoryHealthItem Item(
        string id = "110000000001", decimal listPrice = 500m, int? daysListed = 120,
        int watchers = 0, int sold = 0, decimal? market = 400m, decimal? quickSale = 340m,
        decimal? cost = 200m, string category = "ASIC Miners", string title = "Bitmain Antminer S19j Pro 104TH",
        int qty = 1, bool comparable = true, string verdict = "stale", int? daysToSell = null)
    {
        var item = new InventoryHealthItem
        {
            ListingId = id, Sku = "SKU-" + id, Title = title, Category = category,
            Url = $"https://www.ebay.com/itm/{id}",
            ListPrice = listPrice, Quantity = qty, WatchCount = watchers, QuantitySold = sold,
            DaysListed = daysListed, MarketPrice = market, MarketMedian = market, QuickSalePrice = quickSale,
            CostBasis = cost, MarketComparable = comparable, Verdict = verdict,
            EstimatedDaysToSell = daysToSell,
            SoldCompCount = 9, ConfidenceScore = 70,
        };

        if (sold > 0 && daysListed is int life && life > 0)
            item.SalesPerMonth = Math.Round(sold * 30m / life, 2);

        if (cost is decimal paid)
        {
            var breakdown = Profit.Calculate(
                supplierUnitCost: paid, quantity: 1, expectedSalePrice: listPrice,
                quickSalePrice: listPrice, buyerPaidShipping: 0m, fees: Fees);
            item.BreakEvenPrice = breakdown.BreakEvenSalePrice;
            item.NetProfitAtListPrice = breakdown.NetProfitPerUnit;
            var (floor, basis) = NetProceedsCalculator.MinimumOffer(item.BreakEvenPrice, Fees);
            item.MinimumOfferPrice = floor;
            item.MinimumOfferBasis = basis;
            item.CapitalTiedUp = Math.Round(paid * qty, 2);
            item.CapitalBasis = "cost_basis";
        }
        else
        {
            item.CapitalTiedUp = Math.Round((market ?? listPrice) * qty, 2);
            item.CapitalBasis = market.HasValue ? "market_value" : "list_price";
        }

        return item;
    }

    // ── What counts as stuck ─────────────────────────────────────────────────────────────────

    [Fact]
    public void An_old_listing_with_no_sales_is_stuck()
        => Assert.True(AgingInventoryRescuer.IsStuck(Item(daysListed: 120), 90));

    [Fact]
    public void An_old_listing_that_is_still_selling_units_is_not_stuck()
    {
        // Its stock is turning. Marking it down would spend margin on every remaining unit to fix a
        // problem it does not have — the same trap InventoryHealthAnalyzer refuses to fall into.
        Assert.False(AgingInventoryRescuer.IsStuck(Item(daysListed: 300, sold: 44, qty: 80), 90));
    }

    [Fact]
    public void A_listing_younger_than_the_stale_line_is_not_stuck()
        => Assert.False(AgingInventoryRescuer.IsStuck(Item(daysListed: 45), 90));

    [Fact]
    public void A_listing_of_unknown_age_is_not_assumed_to_be_stuck()
        => Assert.False(AgingInventoryRescuer.IsStuck(Item(daysListed: null), 90));

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_plan_steps_down_from_todays_price_to_the_clearance_price()
    {
        var plan = Rescuer.BuildPlan(Item(listPrice: 500m, market: 400m, quickSale: 340m, daysListed: 120), Fees, Now);

        Assert.True(plan.HasPlan);
        Assert.Equal(3, plan.Steps.Count);                       // "high" urgency at 120 days
        Assert.Equal(339.99m, plan.FinalPrice);                  // the clearance target, charmed
        Assert.All(plan.Steps, s => Assert.True(s.Price < 500m));
        // Strictly descending — a ladder that ever steps back up is not a ladder.
        Assert.True(plan.Steps.Zip(plan.Steps.Skip(1)).All(p => p.Second.Price < p.First.Price));
    }

    [Fact]
    public void The_first_step_is_due_today_and_the_rest_are_dated_forward()
    {
        var plan = Rescuer.BuildPlan(Item(daysListed: 120), Fees, Now);

        Assert.Equal(0, plan.Steps[0].DaysFromNow);
        Assert.Equal(Now, plan.Steps[0].OnUtc);
        Assert.Equal(14, plan.Steps[1].DaysFromNow);
        Assert.Equal(28, plan.Steps[2].DaysFromNow);
        Assert.Equal(Now.AddDays(28), plan.ClearByUtc);
        Assert.Equal(28, plan.PlanDays);
    }

    [Fact]
    public void Each_step_reports_the_age_the_listing_will_have_reached()
    {
        var plan = Rescuer.BuildPlan(Item(daysListed: 120), Fees, Now);

        Assert.Equal(120, plan.Steps[0].ListingAgeAtStep);
        Assert.Equal(134, plan.Steps[1].ListingAgeAtStep);
        Assert.Equal(148, plan.Steps[2].ListingAgeAtStep);
    }

    [Fact]
    public void No_step_of_any_ladder_goes_below_the_break_even_floor()
    {
        // Cost $380 puts the floor at $438.50, well above the $400 quick-sale price the ladder would
        // otherwise walk down to. The floor has to win. This is the single most expensive thing this
        // feature could get wrong.
        var item = Item(listPrice: 500m, cost: 380m, market: 500m, quickSale: 400m, daysListed: 120);
        var plan = Rescuer.BuildPlan(item, Fees, Now);

        var floor = plan.FloorPrice!.Value;
        Assert.True(plan.HasPlan);
        Assert.All(plan.Steps, s => Assert.True(s.Price >= floor, $"${s.Price} is under the ${floor} floor"));
        Assert.True(plan.Steps[^1].IsFloor);
        Assert.All(plan.Steps, s => Assert.True(s.NetProfit >= 0m));
    }

    [Fact]
    public void Six_month_old_stock_gets_a_shorter_more_urgent_plan_than_three_month_old_stock()
    {
        var urgent = Rescuer.BuildPlan(Item(daysListed: 200, verdict: "dead_capital"), Fees, Now);
        var patient = Rescuer.BuildPlan(Item(daysListed: 95), Fees, Now);

        Assert.Equal("critical", urgent.Urgency);
        Assert.Equal("watch", patient.Urgency);
        // Past six months the seller is buying their capital back, not optimising the sale price:
        // fewer, deeper rungs, closer together.
        Assert.True(urgent.Steps.Count < patient.Steps.Count);
        Assert.True(urgent.PlanDays < patient.PlanDays);
    }

    [Fact]
    public void Both_ladders_still_finish_at_the_same_clearance_price()
    {
        // Urgency changes how fast the seller gets there, never how low the price is allowed to go.
        var urgent = Rescuer.BuildPlan(Item(daysListed: 200), Fees, Now);
        var patient = Rescuer.BuildPlan(Item(daysListed: 95), Fees, Now);

        Assert.Equal(urgent.FinalPrice, patient.FinalPrice);
    }

    [Fact]
    public void An_underwater_listing_gets_no_markdown_plan_at_all()
    {
        // Break-even $576 against a market paying $400: every rung of every ladder is a loss, so
        // there is no honest plan and the board says so instead of inventing one.
        var plan = Rescuer.BuildPlan(Item(cost: 500m, market: 400m, daysListed: 150), Fees, Now);

        Assert.False(plan.HasPlan);
        Assert.Contains("underwater", plan.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_listing_the_comps_could_not_match_gets_no_plan()
    {
        var plan = Rescuer.BuildPlan(Item(comparable: false, daysListed: 150), Fees, Now);

        Assert.False(plan.HasPlan);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void A_lot_listing_is_told_why_rather_than_laddered_against_per_unit_comps()
    {
        var item = Item(comparable: false, daysListed: 150);
        item.LotQuantity = 20;
        var plan = Rescuer.BuildPlan(item, Fees, Now);

        Assert.False(plan.HasPlan);
        Assert.Contains(plan.Signals, s => s.Contains("lot of 20"));
    }

    [Fact]
    public void A_listing_with_no_sold_history_gets_no_plan_but_is_still_reported()
    {
        var plan = Rescuer.BuildPlan(Item(market: null, quickSale: null, daysListed: 150), Fees, Now);

        Assert.False(plan.HasPlan);
        Assert.Contains("sold history", plan.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_listing_already_at_its_clearance_price_is_not_cut_again()
    {
        // $330 against a $340 quick-sale price: the price is not what is holding this one, and
        // cutting further would just donate margin.
        var plan = Rescuer.BuildPlan(Item(listPrice: 330m, market: 400m, quickSale: 340m, daysListed: 150), Fees, Now);

        Assert.False(plan.HasPlan);
        Assert.Contains("clearance", plan.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Steps_too_small_to_change_a_buyers_mind_are_dropped()
    {
        // Only $20 of room between $500 and the $480 clearance price: split three ways, the first
        // two rungs are 1.4% and 2.8% moves that no buyer notices and that churn the listing for
        // nothing. Only the rung that clears 3% survives.
        var plan = Rescuer.BuildPlan(Item(listPrice: 500m, cost: 400m, market: 600m, quickSale: 480m, daysListed: 120), Fees, Now);

        var step = Assert.Single(plan.Steps);
        Assert.Equal(479.99m, step.Price);
        Assert.True(step.PercentOffListPrice >= 3m);
    }

    [Fact]
    public void A_drop_worth_making_is_not_left_waiting_for_a_schedule_slot()
    {
        // The rungs before it were skipped as too small, so the first worthwhile drop is the one the
        // seller makes today rather than in four weeks' time.
        var plan = Rescuer.BuildPlan(Item(listPrice: 500m, cost: 400m, market: 600m, quickSale: 480m, daysListed: 120), Fees, Now);

        Assert.Equal(0, plan.Steps[0].DaysFromNow);
        Assert.Equal(Now, plan.Steps[0].OnUtc);
        Assert.Equal(120, plan.Steps[0].ListingAgeAtStep);
    }

    [Fact]
    public void The_plan_states_the_profit_it_gives_up_rather_than_hiding_it()
    {
        var item = Item(listPrice: 500m, cost: 200m, market: 400m, quickSale: 340m, daysListed: 120);
        var plan = Rescuer.BuildPlan(item, Fees, Now);

        Assert.NotNull(plan.ProfitGivenUp);
        Assert.True(plan.ProfitGivenUp > 0m);
        // It is the difference between what today's price would have netted and what the last rung
        // nets — not a number invented for the headline.
        Assert.Equal(
            Math.Round(item.NetProfitAtListPrice!.Value - plan.CashAtFinalStep!.Value, 2),
            plan.ProfitGivenUp);
    }

    [Fact]
    public void Without_a_cost_basis_the_plan_says_so_and_reports_no_profit()
    {
        var plan = Rescuer.BuildPlan(Item(cost: null, daysListed: 150), Fees, Now);

        Assert.True(plan.HasPlan);
        Assert.All(plan.Steps, s => Assert.Null(s.NetProfit));
        Assert.Contains(plan.Signals, s => s.Contains("No cost basis"));
    }

    // ── Fast movers ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_item_that_has_actually_sold_units_counts_as_fast()
        => Assert.True(AgingInventoryRescuer.IsFastMover(Item(daysListed: 60, sold: 5), 90));

    [Fact]
    public void Measured_velocity_counts_as_fast_even_with_no_sales_on_this_listing()
        => Assert.True(AgingInventoryRescuer.IsFastMover(Item(daysListed: 30, daysToSell: 10), 90));

    [Fact]
    public void An_unmeasured_item_is_not_assumed_to_be_fast()
        => Assert.False(AgingInventoryRescuer.IsFastMover(Item(daysListed: 30, daysToSell: null), 90));

    [Fact]
    public void A_stale_item_can_never_be_the_fast_half_of_a_bundle()
    {
        // Two slow movers in a box is a bigger slow mover, not a bundle.
        Assert.False(AgingInventoryRescuer.IsFastMover(Item(daysListed: 150, daysToSell: 10), 90));
    }

    // ── Bundles ──────────────────────────────────────────────────────────────────────────────

    private static List<InventoryHealthItem> SlowAndFast() =>
    [
        Item(id: "SLOW", listPrice: 500m, daysListed: 150, cost: 200m, market: 400m, quickSale: 340m,
             title: "Bitmain Antminer S9 13TH"),
        Item(id: "FAST", listPrice: 450m, daysListed: 20, sold: 5, cost: 150m, market: 460m, quickSale: 400m,
             title: "Bitmain Antminer S19 95TH"),
    ];

    [Fact]
    public void A_slow_mover_is_paired_with_something_that_already_sells()
    {
        var bundles = Rescuer.FindBundles(SlowAndFast(), Fees, Now, staleAfterDays: 90, maxBundles: 12);

        var bundle = Assert.Single(bundles);
        Assert.Equal("SLOW", bundle.SlowListingId);
        Assert.Equal("FAST", bundle.FastListingId);
        Assert.True(bundle.SameCategory);
    }

    [Fact]
    public void The_slow_half_goes_in_at_the_same_clearance_price_its_ladder_walks_to()
    {
        // One item, one value. The gain over the ladder is reaching that price inside a bundle
        // instead of publicly cutting the standalone listing.
        var items = SlowAndFast();
        var bundle = Rescuer.FindBundles(items, Fees, Now, 90, 12).Single();
        var plan = Rescuer.BuildPlan(items[0], Fees, Now);

        Assert.Equal(340m, bundle.SlowContribution);
        Assert.Equal(plan.FinalPrice, InventoryHealthAnalyzer.Charm(bundle.SlowContribution, null));
    }

    [Fact]
    public void The_bundle_is_priced_below_the_two_asking_prices_added_up()
    {
        var bundle = Rescuer.FindBundles(SlowAndFast(), Fees, Now, 90, 12).Single();

        Assert.Equal(950m, bundle.ComponentValue);
        Assert.Equal(789.99m, bundle.BundlePrice);
        Assert.True(bundle.DiscountPercent > 0m);
        Assert.True(bundle.BundlePrice > bundle.FastPrice);   // it must add revenue, not replace it
    }

    [Fact]
    public void A_bundle_is_scored_against_what_actually_happens_today()
    {
        // Which is the fast item selling on its own and the slow one continuing to sit — not against
        // the fiction that both were going to sell at full price.
        var bundle = Rescuer.FindBundles(SlowAndFast(), Fees, Now, 90, 12).Single();

        Assert.True(bundle.HasCostBasis);
        Assert.Equal(
            Math.Round(bundle.NetIfBundleSells!.Value - bundle.NetIfFastSellsAlone!.Value, 2),
            bundle.IncrementalNet);
        Assert.True(bundle.IncrementalNet > 0m);
    }

    [Fact]
    public void A_bundle_that_would_net_less_than_selling_the_fast_item_alone_is_not_suggested()
    {
        // A $400 item already marked down to $200 is underwater: giving it away attached to a good
        // seller destroys more value than leaving it on the shelf.
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW", listPrice: 200m, daysListed: 150, cost: 400m, market: 400m, quickSale: 340m),
            Item(id: "FAST", listPrice: 450m, daysListed: 20, sold: 5, cost: 150m),
        };

        Assert.Empty(Rescuer.FindBundles(items, Fees, Now, 90, 12));
    }

    [Fact]
    public void The_per_order_costs_paid_once_instead_of_twice_are_broken_out()
    {
        var fees = new FeeProfile { DefaultShippingCost = 12m, DefaultPackagingCost = 2m, DefaultLaborCost = 3m };
        var bundle = Rescuer.FindBundles(SlowAndFast(), fees, Now, 90, 12).Single();

        // eBay's fixed per-order fee, one label, one box, one trip.
        Assert.Equal(17.40m, bundle.SavedByShippingTogether);
    }

    [Fact]
    public void Two_slow_movers_are_never_bundled_together()
    {
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW1", daysListed: 150),
            Item(id: "SLOW2", daysListed: 200),
        };

        Assert.Empty(Rescuer.FindBundles(items, Fees, Now, 90, 12));
    }

    [Fact]
    public void Items_from_unrelated_categories_are_not_paired()
    {
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW", daysListed: 150, category: "ASIC Miners"),
            Item(id: "FAST", daysListed: 20, sold: 5, category: "Womens Handbags"),
        };

        Assert.Empty(Rescuer.FindBundles(items, Fees, Now, 90, 12));
    }

    [Fact]
    public void A_trivially_cheap_partner_is_not_used_to_carry_an_expensive_slow_mover()
    {
        // A $4 cable does not pull a $900 miner out of the warehouse; pairing them just discounts
        // the cable.
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW", listPrice: 900m, daysListed: 150, cost: 400m, market: 800m, quickSale: 700m),
            Item(id: "FAST", listPrice: 4m, daysListed: 20, sold: 40, cost: 1m, market: 5m, quickSale: 4m),
        };

        Assert.Empty(Rescuer.FindBundles(items, Fees, Now, 90, 12));
    }

    [Fact]
    public void No_listing_is_promised_to_two_different_bundles()
    {
        // One fast mover, two slow ones: it can only be sold once, so it anchors exactly one bundle.
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW1", listPrice: 500m, daysListed: 150, cost: 200m),
            Item(id: "SLOW2", listPrice: 480m, daysListed: 160, cost: 190m),
            Item(id: "FAST", listPrice: 450m, daysListed: 20, sold: 5, cost: 150m),
        };

        var bundles = Rescuer.FindBundles(items, Fees, Now, 90, 12);

        Assert.Single(bundles);
        Assert.Equal("FAST", bundles[0].FastListingId);
    }

    [Fact]
    public void Bundles_lead_with_the_most_trapped_capital()
    {
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SMALL", listPrice: 200m, daysListed: 150, cost: 90m, market: 160m, quickSale: 140m),
            Item(id: "BIG",   listPrice: 900m, daysListed: 150, cost: 400m, market: 800m, quickSale: 700m),
            Item(id: "FAST1", listPrice: 850m, daysListed: 20, sold: 5, cost: 300m, market: 860m, quickSale: 800m),
            Item(id: "FAST2", listPrice: 190m, daysListed: 20, sold: 5, cost: 70m, market: 200m, quickSale: 180m),
        };

        var bundles = Rescuer.FindBundles(items, Fees, Now, 90, 12);

        Assert.Equal(2, bundles.Count);
        Assert.Equal("BIG", bundles[0].SlowListingId);
    }

    [Fact]
    public void The_suggested_bundle_title_leads_with_the_item_buyers_actually_search_for()
    {
        var bundle = Rescuer.FindBundles(SlowAndFast(), Fees, Now, 90, 12).Single();

        Assert.StartsWith("Bitmain Antminer S19 95TH", bundle.SuggestedTitle);
        Assert.True(bundle.SuggestedTitle.Length <= 80);   // eBay's title limit
    }

    // ── The board as a whole ─────────────────────────────────────────────────────────────────

    [Fact]
    public void The_summary_totals_only_the_capital_that_is_actually_stuck()
    {
        var items = new List<InventoryHealthItem>
        {
            Item(id: "SLOW1", daysListed: 150, cost: 200m),
            Item(id: "SLOW2", daysListed: 200, cost: 300m),
            Item(id: "FRESH", daysListed: 10, cost: 999m),                    // not old enough
            Item(id: "MOVING", daysListed: 300, sold: 12, qty: 5, cost: 999m), // old but turning
        };

        var result = Rescuer.Build(items, Fees, Now, staleAfterDays: 90);

        Assert.Equal(2, result.Summary.StaleListings);
        Assert.Equal(500m, result.Summary.TrappedCapital);   // $200 + $300, neither $999 counted
        Assert.Equal(200, result.Summary.OldestDaysListed);
    }

    [Fact]
    public void The_board_leads_with_the_most_urgent_stuck_money()
    {
        var items = new List<InventoryHealthItem>
        {
            Item(id: "MILD", listPrice: 500m, daysListed: 95, cost: 200m),
            Item(id: "DEAD", listPrice: 500m, daysListed: 220, cost: 200m, verdict: "dead_capital"),
        };

        var result = Rescuer.Build(items, Fees, Now, staleAfterDays: 90);

        Assert.Equal("DEAD", result.Plans[0].ListingId);
        Assert.Equal("critical", result.Plans[0].Urgency);
    }

    [Fact]
    public void Listings_that_could_not_be_rescued_are_counted_rather_than_dropped()
    {
        // Money that is still stuck is the whole point of the board, so a listing with no workable
        // plan has to stay visible instead of quietly vanishing from the list.
        var items = new List<InventoryHealthItem>
        {
            Item(id: "PLANNED", daysListed: 150, cost: 200m),
            Item(id: "UNDERWATER", daysListed: 150, cost: 500m, market: 400m),
        };

        var result = Rescuer.Build(items, Fees, Now, staleAfterDays: 90);

        Assert.Equal(2, result.Plans.Count);
        Assert.Equal(1, result.Summary.PlansReady);
        Assert.Equal(1, result.Summary.NoPlanCount);
        Assert.Equal(1, result.Summary.StepsDueNow);
    }

    [Fact]
    public void An_inventory_with_nothing_stale_produces_an_empty_board_rather_than_busywork()
    {
        var result = Rescuer.Build([Item(daysListed: 20), Item(id: "B", daysListed: 45)], Fees, Now, 90);

        Assert.Empty(result.Plans);
        Assert.Empty(result.Bundles);
        Assert.Equal(0m, result.Summary.TrappedCapital);
        Assert.Null(result.Summary.OldestDaysListed);
    }
}
