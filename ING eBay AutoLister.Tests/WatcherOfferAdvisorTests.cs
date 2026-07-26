using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// An offer to watchers is a private, buyer-visible discount on a live listing. The cases below pin
// the things that would cost the seller real money if they were wrong: that no offer ever lands
// under the break-even or under the profit the seller asked to keep, that a listing with no
// recorded cost can't be discounted deeply on a guess, that the discount tracks the audience and
// the market rather than a flat percentage, and that eBay's own rules (5% minimum, eligibility)
// are respected before a call is ever made.
public class WatcherOfferAdvisorTests
{
    private static readonly FeeProfile Fees = new();          // 13.25% + $0.40, nothing else
    private static readonly ProfitCalculator Profit = new();

    private static decimal BreakEvenFor(decimal unitCost) =>
        Profit.Calculate(unitCost, 1, 100m, 100m, 0m, Fees).BreakEvenSalePrice;

    private static WatcherOfferAdvisor.Suggestion Suggest(
        decimal listPrice = 200m, int watchers = 4, int? daysListed = 45,
        decimal? market = null, decimal? quickSale = null, decimal? floor = null,
        string floorBasis = "break_even", bool hasCostBasis = true, bool? eligible = true,
        bool marketComparable = true) =>
        WatcherOfferAdvisor.Suggest(
            listPrice, watchers, daysListed, market, quickSale, floor, floorBasis,
            hasCostBasis, eligible, "", marketComparable);

    // ── eBay's own rules ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_offer_is_ever_smaller_than_the_five_percent_ebay_requires()
    {
        var s = Suggest(watchers: 30, daysListed: 1);
        Assert.Equal("ready", s.Verdict);
        Assert.True(s.DiscountPercent >= WatcherOfferAdvisor.EbayMinDiscountPercent);
    }

    [Fact]
    public void No_single_offer_goes_deeper_than_the_cap()
    {
        // Wildly over market, ancient, one watcher — every ladder input pushing at once.
        var s = Suggest(listPrice: 1000m, watchers: 1, daysListed: 900, market: 300m);
        Assert.Equal(WatcherOfferAdvisor.MaxDiscountPercent, s.DiscountPercent);
    }

    [Fact]
    public void A_listing_nobody_is_watching_gets_no_offer()
    {
        var s = Suggest(watchers: 0);
        Assert.Equal("no_watchers", s.Verdict);
        Assert.Null(s.DiscountPercent);
    }

    [Fact]
    public void A_listing_ebay_says_is_not_eligible_gets_no_offer()
    {
        var s = Suggest(eligible: false);
        Assert.Equal("not_eligible", s.Verdict);
        Assert.Null(s.DiscountPercent);
    }

    [Fact]
    public void Unknown_eligibility_still_offers_but_says_the_send_may_be_refused()
    {
        var s = Suggest(eligible: null);
        Assert.Equal("ready", s.Verdict);
        Assert.Contains(s.Signals, x => x.Contains("eligibility", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_listing_with_no_price_is_not_offered()
        => Assert.Equal("not_ready", Suggest(listPrice: 0m).Verdict);

    // ── The floor: the one thing that must never be crossed ──────────────────────────────────

    [Fact]
    public void The_offer_never_lands_below_the_break_even()
    {
        var breakEven = BreakEvenFor(150m);                       // ≈ $173.63 on a $150 unit
        var s = Suggest(listPrice: 190m, watchers: 1, daysListed: 400, floor: breakEven);

        Assert.Equal("ready", s.Verdict);
        Assert.True(s.OfferPrice >= breakEven,
            $"offer {s.OfferPrice} went under the {breakEven} break-even");
        Assert.True(s.FloorLimited);
    }

    [Fact]
    public void When_even_the_minimum_offer_would_lose_money_nothing_is_suggested()
    {
        var breakEven = BreakEvenFor(180m);                       // ≈ $207.83
        var s = Suggest(listPrice: 210m, watchers: 8, floor: breakEven);

        Assert.Equal("no_room", s.Verdict);
        Assert.Null(s.DiscountPercent);
        Assert.Contains("5%", s.Note);
    }

    [Fact]
    public void The_seller_s_minimum_profit_raises_the_floor_above_break_even()
    {
        var breakEven = BreakEvenFor(100m);
        var floorNoProfit = WatcherOfferAdvisor.ProfitFloorPrice(breakEven, 0m, Fees)!.Value;
        var floorWith25 = WatcherOfferAdvisor.ProfitFloorPrice(breakEven, 25m, Fees)!.Value;

        Assert.Equal(breakEven, floorNoProfit, 2);
        // $25 of profit costs more than $25 of price, because eBay's cut scales with the sale.
        Assert.True(floorWith25 > floorNoProfit + 25m);
    }

    [Fact]
    public void A_sale_at_the_profit_floor_actually_leaves_the_requested_profit()
    {
        var breakEven = BreakEvenFor(100m);
        var floor = WatcherOfferAdvisor.ProfitFloorPrice(breakEven, 30m, Fees)!.Value;

        Assert.Equal(30m, WatcherOfferAdvisor.NetProfitAt(floor, breakEven, Fees)!.Value, 1);
    }

    [Fact]
    public void An_offer_held_at_the_profit_floor_still_clears_that_profit()
    {
        var breakEven = BreakEvenFor(60m);
        var floor = WatcherOfferAdvisor.ProfitFloorPrice(breakEven, 20m, Fees)!.Value;
        var s = Suggest(listPrice: 105m, watchers: 2, daysListed: 300, floor: floor, floorBasis: "profit");

        Assert.Equal("ready", s.Verdict);
        Assert.True(WatcherOfferAdvisor.NetProfitAt(s.OfferPrice!.Value, breakEven, Fees) >= 20m);
    }

    [Fact]
    public void With_no_cost_basis_recorded_the_offer_stays_shallow_and_says_why()
    {
        var s = Suggest(listPrice: 300m, watchers: 1, daysListed: 400, hasCostBasis: false);

        Assert.Equal(WatcherOfferAdvisor.NoCostBasisCapPercent, s.DiscountPercent);
        Assert.Contains(s.Signals, x => x.Contains("No cost recorded"));
    }

    [Fact]
    public void The_quick_sale_price_is_a_floor_of_its_own()
    {
        // The comps say this moves at $180 without any offer at all, so discounting under that is
        // paying for a sale the listing was already going to get.
        var (floor, basis) = WatcherOfferAdvisor.Floor(
            breakEvenPrice: 100m, minNetProfit: 0m, quickSalePrice: 180m, marketComparable: true, Fees);

        Assert.Equal(180m, floor);
        Assert.Equal("quick_sale", basis);
    }

    [Fact]
    public void A_failed_market_match_cannot_supply_a_quick_sale_floor()
    {
        var (floor, basis) = WatcherOfferAdvisor.Floor(
            breakEvenPrice: 100m, minNetProfit: 0m, quickSalePrice: 180m, marketComparable: false, Fees);

        Assert.Equal(100m, floor);
        Assert.Equal("break_even", basis);
    }

    [Fact]
    public void With_nothing_known_there_is_no_floor_at_all()
    {
        var (floor, basis) = WatcherOfferAdvisor.Floor(null, 0m, null, true, Fees);
        Assert.Null(floor);
        Assert.Equal("none", basis);
    }

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_older_listing_is_offered_a_deeper_discount_than_a_new_one()
    {
        var fresh = Suggest(daysListed: 5).DiscountPercent!.Value;
        var stale = Suggest(daysListed: 120).DiscountPercent!.Value;
        var dead = Suggest(daysListed: 400).DiscountPercent!.Value;

        Assert.True(stale > fresh);
        Assert.True(dead > stale);
    }

    [Fact]
    public void A_bigger_audience_needs_a_smaller_discount_to_close()
    {
        var thin = Suggest(watchers: 1).DiscountPercent!.Value;
        var some = Suggest(watchers: 4).DiscountPercent!.Value;
        var crowd = Suggest(watchers: 12).DiscountPercent!.Value;

        Assert.True(thin > some);
        Assert.True(some > crowd);
    }

    [Fact]
    public void An_offer_on_an_over_market_listing_reaches_at_least_the_going_rate()
    {
        // Listed at $250, comps say $200. A 5% offer at $237.50 is still above the market the
        // watchers can see for themselves, so it closes nothing.
        var s = Suggest(listPrice: 250m, watchers: 6, daysListed: 10, market: 200m);

        Assert.True(s.OfferPrice <= 200m, $"offer {s.OfferPrice} never reached the $200 market price");
        Assert.Contains(s.Signals, x => x.Contains("going rate"));
    }

    [Fact]
    public void A_listing_already_under_market_gets_a_nudge_not_a_giveaway()
    {
        var s = Suggest(listPrice: 150m, watchers: 1, daysListed: 400, market: 200m);

        Assert.Equal(WatcherOfferAdvisor.AlreadyUnderMarketCapPercent, s.DiscountPercent);
        Assert.Contains(s.Signals, x => x.Contains("under market"));
    }

    [Fact]
    public void A_market_price_that_did_not_match_cannot_deepen_the_offer()
    {
        // A "lot of 20" listing against per-unit comps: $2,000 vs $100 is not a 95% mispricing.
        var s = Suggest(listPrice: 2000m, watchers: 3, daysListed: 10, market: 100m, marketComparable: false);

        Assert.True(s.DiscountPercent < WatcherOfferAdvisor.MaxDiscountPercent);
        Assert.DoesNotContain(s.Signals, x => x.Contains("going rate"));
    }

    // ── Arithmetic ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 10, 90)]
    [InlineData(19.99, 10, 17.99)]
    [InlineData(24.95, 15, 21.21)]
    [InlineData(1499.00, 25, 1124.25)]
    public void The_offer_price_is_the_percentage_ebay_will_actually_apply(
        decimal listPrice, int discount, decimal expected)
        => Assert.Equal(expected, WatcherOfferAdvisor.OfferPriceFor(listPrice, discount));

    [Fact]
    public void Net_profit_is_unknown_rather_than_zero_without_a_break_even()
        => Assert.Null(WatcherOfferAdvisor.NetProfitAt(100m, null, Fees));

    [Fact]
    public void Net_profit_at_an_offer_matches_the_shared_profit_calculator()
    {
        var breakEven = BreakEvenFor(100m);
        var offerPrice = 200m;

        var viaAdvisor = WatcherOfferAdvisor.NetProfitAt(offerPrice, breakEven, Fees)!.Value;
        var viaCalculator = Profit.Calculate(100m, 1, offerPrice, offerPrice, 0m, Fees).NetProfitPerUnit;

        Assert.Equal(viaCalculator, viaAdvisor, 1);
    }

    // ── Building a row from an inventory-health item ─────────────────────────────────────────

    private static InventoryHealthItem Health(
        decimal listPrice = 200m, int watchers = 6, int? days = 45, decimal? cost = 80m,
        decimal? market = 210m, decimal? quickSale = null, bool comparable = true) =>
        new()
        {
            ListingId = "110000000001", Sku = "SKU-1", Title = "Bitmain Antminer S19j Pro",
            ListPrice = listPrice, Quantity = 1, WatchCount = watchers, DaysListed = days,
            CostBasis = cost, MarketPrice = market, QuickSalePrice = quickSale,
            MarketComparable = comparable, SoldCompCount = 8,
            BreakEvenPrice = cost is null ? null : BreakEvenFor(cost.Value),
        };

    [Fact]
    public void Build_carries_the_money_across_and_prices_the_offer()
    {
        var item = WatcherOfferAdvisor.Build(Health(), eligible: true, "", minNetProfit: 0m, Fees);

        Assert.True(item.CanSend);
        Assert.Equal("ready", item.Verdict);
        Assert.Equal(item.ListPrice - item.OfferPrice, item.MarginGivenUp);
        Assert.True(item.NetProfitAtOffer > 0m);
        Assert.Equal("break_even", item.FloorBasis);
    }

    [Fact]
    public void Build_leaves_net_profit_unknown_when_the_seller_never_recorded_a_cost()
    {
        var item = WatcherOfferAdvisor.Build(Health(cost: null), eligible: true, "", 0m, Fees);

        Assert.Null(item.NetProfitAtOffer);
        Assert.Null(item.FloorPrice);
        Assert.False(item.HasCostBasis);
        Assert.True(item.DiscountPercent <= WatcherOfferAdvisor.NoCostBasisCapPercent);
    }

    [Fact]
    public void Build_refuses_the_offer_when_the_minimum_profit_leaves_no_room()
    {
        // $180 cost, $210 asking price: break-even is already ~$207, so a $50 profit floor is
        // unreachable at any discount.
        var item = WatcherOfferAdvisor.Build(
            Health(listPrice: 210m, cost: 180m, market: null), eligible: true, "", minNetProfit: 50m, Fees);

        Assert.Equal("no_room", item.Verdict);
        Assert.False(item.CanSend);
    }

    [Fact]
    public void Build_notes_an_aged_listing_that_still_has_people_watching()
    {
        var item = WatcherOfferAdvisor.Build(Health(days: 200), eligible: true, "", 0m, Fees);
        Assert.Contains(item.Signals, s => s.Contains("still on it"));
    }

    // ── Board totals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_headline_counts_one_sale_per_listing_not_one_per_watcher()
    {
        var item = WatcherOfferAdvisor.Build(Health(watchers: 30), eligible: true, "", 0m, Fees);
        var summary = WatcherOfferAdvisor.Summarize([item]);

        Assert.Equal(1, summary.ReadyToSend);
        Assert.Equal(30, summary.WatchersReachable);
        Assert.Equal(item.OfferPrice, summary.RevenueIfOneEachAccepts);
    }

    [Fact]
    public void The_summary_separates_what_is_blocked_by_the_floor_from_what_ebay_refuses()
    {
        var ready = WatcherOfferAdvisor.Build(Health(), eligible: true, "", 0m, Fees);
        var blocked = WatcherOfferAdvisor.Build(
            Health(listPrice: 210m, cost: 180m, market: null), eligible: true, "", 50m, Fees);
        var refused = WatcherOfferAdvisor.Build(Health(), eligible: false, "no", 0m, Fees);

        var summary = WatcherOfferAdvisor.Summarize([ready, blocked, refused]);

        Assert.Equal(1, summary.ReadyToSend);
        Assert.Equal(1, summary.BlockedByFloor);
        Assert.Equal(1, summary.NotEligible);
        Assert.Equal(3, summary.ListingsWithWatchers);
    }

    [Fact]
    public void Ranking_puts_the_warmest_biggest_money_first()
    {
        var small = WatcherOfferAdvisor.Build(Health(listPrice: 30m, watchers: 2, cost: 5m), true, "", 0m, Fees);
        var big = WatcherOfferAdvisor.Build(Health(listPrice: 900m, watchers: 14, cost: 300m, market: 950m), true, "", 0m, Fees);
        var dead = WatcherOfferAdvisor.Build(Health(watchers: 0), true, "", 0m, Fees);

        var ranked = WatcherOfferAdvisor.Rank([dead, small, big]);

        Assert.Equal(900m, ranked[0].ListPrice);
        Assert.Equal("no_watchers", ranked[^1].Verdict);
    }

    // ── The message eBay carries to the watcher ──────────────────────────────────────────────

    [Fact]
    public void A_message_longer_than_ebay_allows_is_trimmed_rather_than_rejected()
    {
        var trimmed = WatcherOfferAdvisor.CleanMessage(new string('x', 400));
        Assert.Equal(WatcherOfferAdvisor.MaxMessageLength, trimmed.Length);
    }

    [Fact]
    public void An_empty_message_stays_empty_so_ebay_uses_its_own_default()
        => Assert.Equal("", WatcherOfferAdvisor.CleanMessage("   "));
}
