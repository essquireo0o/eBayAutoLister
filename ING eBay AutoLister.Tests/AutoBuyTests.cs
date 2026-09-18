using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// eBay Auto-Buy is the one feature that spends money on its own, so its tests are almost all about
/// the brakes: that a disarmed feature buys nothing, that simulation never reaches eBay, that a rule
/// cannot exist without a ceiling and a budget, that no caps can be walked past, and that the same
/// listing is never bought twice.
/// </summary>
public class AutoBuyTests
{
    // ── The pure decisions ────────────────────────────────────────────────────

    private static AutoBuyRule Rule(decimal max = 100m, AutoBuyMode mode = AutoBuyMode.BuyItNow) => new()
    {
        Id = 1, Name = "r", Query = "thing", Mode = mode,
        MaxItemPrice = max, OfferOrBidPrice = mode == AutoBuyMode.BuyItNow ? 0m : max - 10m,
        BudgetCap = 1000m, Enabled = true,
    };

    // A legacy numeric item id (the only kind PlaceOffer can buy) with shipping stated as free.
    // Both are now conditions of a match, so the fixture has to say them out loud.
    private static EbayOpportunityItem Item(decimal price = 50m, string title = "A good thing", int fb = 100) =>
        new() { ItemId = "123456789012", Title = title, Price = price, SellerFeedbackScore = fb,
                ShippingCost = 0m, ShippingStated = true, Url = "https://ebay/itm/123" };

    [Fact]
    public void A_listing_over_the_ceiling_is_refused()
    {
        var (ok, reason) = AutoBuyService.Evaluate(Rule(max: 40m), Item(price: 50m));
        Assert.False(ok);
        Assert.Contains("ceiling", reason);
    }

    [Fact]
    public void An_excluded_word_in_the_title_refuses_the_listing()
    {
        var rule = Rule();
        rule.ExcludeKeywords = "broken, for parts";
        var (ok, reason) = AutoBuyService.Evaluate(rule, Item(title: "Widget for parts only"));
        Assert.False(ok);
        Assert.Contains("excluded word", reason);
    }

    [Fact]
    public void A_seller_under_the_feedback_floor_is_refused()
    {
        var rule = Rule();
        rule.MinSellerFeedback = 50;
        var (ok, _) = AutoBuyService.Evaluate(rule, Item(fb: 10));
        Assert.False(ok);
    }

    [Fact]
    public void A_clean_listing_passes_and_prices_by_mode()
    {
        Assert.True(AutoBuyService.Evaluate(Rule(), Item()).Ok);
        Assert.Equal(50m, AutoBuyService.PriceFor(Rule(), Item(price: 50m)));           // Buy It Now = listing price
        Assert.Equal(90m, AutoBuyService.PriceFor(Rule(mode: AutoBuyMode.BestOffer), Item()));  // offer = the offer amount
        Assert.Equal(90m, AutoBuyService.PriceFor(Rule(mode: AutoBuyMode.AuctionBid), Item())); // bid = the bid amount
    }

    [Fact]
    public void Every_cap_stops_a_buy_before_it_is_placed()
    {
        var settings = new AutoBuySettings { GlobalBudgetCap = 1000m, MaxBuysPerDay = 5 };

        var countCapped = Rule(); countCapped.MaxBuys = 2; countCapped.Buys = 2;
        Assert.NotNull(AutoBuyService.CapReason(countCapped, settings, 50m));

        var budgetCapped = Rule(); budgetCapped.BudgetCap = 60m; budgetCapped.Spent = 40m;
        Assert.NotNull(AutoBuyService.CapReason(budgetCapped, settings, 50m)); // only $20 left

        var dayCapped = new AutoBuySettings { GlobalBudgetCap = 1000m, MaxBuysPerDay = 3, BuysToday = 3 };
        Assert.NotNull(AutoBuyService.CapReason(Rule(), dayCapped, 50m));

        var globalCapped = new AutoBuySettings { GlobalBudgetCap = 100m, GlobalSpent = 80m, MaxBuysPerDay = 99 };
        Assert.NotNull(AutoBuyService.CapReason(Rule(), globalCapped, 50m)); // only $20 of global left

        Assert.Null(AutoBuyService.CapReason(Rule(), settings, 50m)); // all clear
    }

    [Fact]
    public void The_safety_line_names_the_state()
    {
        Assert.Contains("Disarmed", AutoBuyService.SafetyLine(new AutoBuySettings { Armed = false }));
        Assert.Contains("simulating", AutoBuyService.SafetyLine(new AutoBuySettings { Armed = true, LiveBuying = false }));
        Assert.Contains("LIVE", AutoBuyService.SafetyLine(new AutoBuySettings { Armed = true, LiveBuying = true }));
    }

    // ── The store ─────────────────────────────────────────────────────────────

    private static AutoBuyStore FreshStore() =>
        new(Path.Combine(Path.GetTempPath(), $"autobuy-test-{Guid.NewGuid():N}.db"));

    [Fact]
    public void The_feature_ships_disarmed_and_simulate_only()
    {
        var s = FreshStore().GetSettings();
        Assert.False(s.Armed);
        Assert.False(s.LiveBuying);
    }

    [Fact]
    public void A_rule_cannot_be_saved_without_a_ceiling_and_a_budget()
    {
        var store = FreshStore();
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveRule(new AutoBuyRuleRequest { Query = "x", MaxItemPrice = 0m, BudgetCap = 100m }));
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveRule(new AutoBuyRuleRequest { Query = "x", MaxItemPrice = 50m, BudgetCap = 0m }));
        // Budget smaller than one item could never buy anything.
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveRule(new AutoBuyRuleRequest { Query = "x", MaxItemPrice = 50m, BudgetCap = 40m }));
        // An offer above the ceiling is refused.
        Assert.Throws<InvalidOperationException>(() =>
            store.SaveRule(new AutoBuyRuleRequest { Query = "x", Mode = "BestOffer", MaxItemPrice = 50m, OfferOrBidPrice = 60m, BudgetCap = 100m }));
    }

    [Fact]
    public void A_saved_rule_round_trips()
    {
        var store = FreshStore();
        var saved = store.SaveRule(new AutoBuyRuleRequest
        {
            Query = "Antminer S19", Mode = "BuyItNow", MaxItemPrice = 300m, BudgetCap = 900m, MaxBuys = 3, Enabled = true,
        });
        var read = store.GetRule(saved.Id);
        Assert.NotNull(read);
        Assert.Equal("Antminer S19", read!.Query);
        Assert.Equal(300m, read.MaxItemPrice);
        Assert.True(read.Enabled);
    }

    [Fact]
    public void A_recorded_buy_moves_the_rule_and_the_global_counters_together()
    {
        var store = FreshStore();
        var rule = store.SaveRule(new AutoBuyRuleRequest { Query = "x", MaxItemPrice = 100m, BudgetCap = 500m, Enabled = true });
        store.RecordBuy(rule.Id, 75m);

        Assert.Equal(75m, store.GetRule(rule.Id)!.Spent);
        Assert.Equal(1, store.GetRule(rule.Id)!.Buys);
        var s = store.GetSettings();
        Assert.Equal(75m, s.GlobalSpent);
        Assert.Equal(1, s.BuysToday);
    }

    [Fact]
    public void A_rule_never_acts_on_the_same_item_twice()
    {
        var store = FreshStore();
        Assert.True(store.MarkSeen(5, "item-1"));   // first time: newly remembered
        Assert.False(store.MarkSeen(5, "item-1"));  // already known
        Assert.True(store.HasSeen(5, "item-1"));
    }

    // ── The loop, with eBay stubbed out ───────────────────────────────────────

    private sealed class Harness
    {
        public AutoBuyStore Store = FreshStore();
        public List<(string ItemId, decimal Price)> Placed = [];
        public List<EbayOpportunityItem> Listings = [Item()];
        public bool PlacerSucceeds = true;

        public AutoBuyService Build()
        {
            AutoBuyListingSource source = (_, _) => Task.FromResult<IReadOnlyList<EbayOpportunityItem>>(Listings);
            AutoBuyPlacer placer = (_, item, price, _) =>
            {
                Placed.Add((item.ItemId, price));
                return Task.FromResult(new AutoBuyPlacement(PlacerSucceeds, PlacerSucceeds ? "accepted" : "refused"));
            };
            return new AutoBuyService(Store, source, placer, new ActionLog());
        }

        public long AddRule() => Store.SaveRule(new AutoBuyRuleRequest
        {
            Query = "x", Mode = "BuyItNow", MaxItemPrice = 100m, BudgetCap = 500m, Enabled = true,
        }).Id;
    }

    [Fact]
    public async Task Simulation_records_a_would_have_bought_and_never_reaches_eBay()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Store.SaveSettings(armed: true, liveBuying: false, null, null); // armed, but simulate

        await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Empty(h.Placed); // the placer was never called
        var recent = h.Store.ListRecent();
        Assert.Contains(recent, e => e.Outcome == AutoBuyOutcome.Simulated);
        Assert.Equal(0m, h.Store.GetSettings().GlobalSpent); // nothing spent
    }

    [Fact]
    public async Task Live_buying_places_the_purchase_and_banks_the_spend()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Store.SaveSettings(armed: true, null, null, null);        // arm first...
        h.Store.SaveSettings(null, liveBuying: true, null, null);   // ...then live, as two deliberate acts

        await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Single(h.Placed);
        Assert.Equal(50m, h.Placed[0].Price);
        Assert.Contains(h.Store.ListRecent(), e => e.Outcome == AutoBuyOutcome.Placed);
        Assert.Equal(50m, h.Store.GetSettings().GlobalSpent);
        Assert.Equal(1, h.Store.GetRule(id)!.Buys);
    }

    [Fact]
    public async Task A_disarmed_tick_scans_nothing()
    {
        var h = new Harness();
        h.AddRule();
        // Not armed. The scheduled loop must not even look.
        await h.Build().TickAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Empty(h.Placed);
        Assert.Empty(h.Store.ListRecent());
    }

    [Fact]
    public async Task The_same_listing_is_not_bought_on_a_second_run()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Store.SaveSettings(armed: true, null, null, null);        // arm first...
        h.Store.SaveSettings(null, liveBuying: true, null, null);   // ...then live, as two deliberate acts
        var service = h.Build();

        await service.RunRuleAsync(id, manual: true, CancellationToken.None);
        await service.RunRuleAsync(id, manual: true, CancellationToken.None); // same one listing still returned

        Assert.Single(h.Placed); // bought once, remembered, not bought again
    }

    [Fact]
    public async Task The_global_budget_stops_a_live_buy()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Store.SaveSettings(armed: true, null, globalBudgetCap: 30m, null); // less than the $50 item
        h.Store.SaveSettings(null, liveBuying: true, null, null);

        await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Empty(h.Placed); // capped before any spend
        Assert.Contains(h.Store.ListRecent(), e => e.Outcome == AutoBuyOutcome.Skipped);
    }
}
