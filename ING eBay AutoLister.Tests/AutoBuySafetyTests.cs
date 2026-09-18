using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The second layer of Auto-Buy brakes, added 2026-09-18 after a read-only audit of the first
/// release: money is counted all-in (shipping included), a rule can name what a match must say and
/// the least it may cost, the two master switches are ordered, an auction is bid on only near its
/// end, and what eBay's "accepted" means is written down truthfully — a Buy It Now is a commitment
/// to pay, not a payment; a bid that is already outbid is not a spend.
/// </summary>
public class AutoBuySafetyTests
{
    private static AutoBuyRule Rule(decimal max = 100m, AutoBuyMode mode = AutoBuyMode.BuyItNow) => new()
    {
        Id = 1, Name = "r", Query = "thing", Mode = mode,
        MaxItemPrice = max, OfferOrBidPrice = mode == AutoBuyMode.BuyItNow ? 0m : max - 10m,
        BudgetCap = 1000m, Enabled = true, IntervalMinutes = 15,
    };

    private static EbayOpportunityItem Item(decimal price = 50m, decimal shipping = 0m, bool stated = true,
        string title = "A good thing", string id = "123456789012") =>
        new() { ItemId = id, Title = title, Price = price, ShippingCost = shipping, ShippingStated = stated,
                SellerFeedbackScore = 500, Url = "https://ebay/itm/123" };

    private static AutoBuyStore FreshStore() =>
        new(Path.Combine(Path.GetTempPath(), $"autobuy-safety-{Guid.NewGuid():N}.db"));

    // ── Money is all-in ───────────────────────────────────────────────────────

    [Fact]
    public void Shipping_pushes_a_listing_over_the_ceiling()
    {
        // $45 item + $10 shipping is a $55 item against a $50 ceiling.
        var (ok, reason) = AutoBuyService.Evaluate(Rule(max: 50m), Item(price: 45m, shipping: 10m));
        Assert.False(ok);
        Assert.StartsWith("over the ceiling", reason);
        Assert.True(AutoBuyService.Evaluate(Rule(max: 50m), Item(price: 45m, shipping: 5m)).Ok);
    }

    [Fact]
    public void Unknown_shipping_is_refused_not_booked_as_free()
    {
        var (ok, reason) = AutoBuyService.Evaluate(Rule(), Item(price: 10m, stated: false));
        Assert.False(ok);
        Assert.Equal("shipping not stated", reason);
    }

    [Fact]
    public void An_offer_or_bid_plus_shipping_must_clear_the_ceiling_too()
    {
        // Ceiling $100, bid $90: fine with $5 shipping, not with $15.
        Assert.True(AutoBuyService.Evaluate(Rule(mode: AutoBuyMode.AuctionBid), Item(price: 20m, shipping: 5m)).Ok);
        var (ok, reason) = AutoBuyService.Evaluate(Rule(mode: AutoBuyMode.AuctionBid), Item(price: 20m, shipping: 15m));
        Assert.False(ok);
        Assert.StartsWith("over the ceiling", reason);
    }

    [Fact]
    public void The_ledger_and_the_budget_see_the_all_in_figure()
    {
        Assert.Equal(60m, AutoBuyService.AllInPrice(Rule(), Item(price: 50m, shipping: 10m)));
        Assert.Equal(100m, AutoBuyService.AllInPrice(Rule(mode: AutoBuyMode.BestOffer), Item(price: 50m, shipping: 10m))); // $90 offer + $10
        Assert.Equal(50m, AutoBuyService.PriceFor(Rule(), Item(price: 50m, shipping: 10m)));     // what goes on eBay
    }

    // ── What a match must say, and the least it may cost ─────────────────────

    [Fact]
    public void A_price_floor_keeps_accessories_out()
    {
        var rule = Rule(max: 300m);
        rule.MinItemPrice = 150m;
        var (ok, reason) = AutoBuyService.Evaluate(rule, Item(price: 12m, title: "Antminer S19 power cord"));
        Assert.False(ok);
        Assert.StartsWith("below the floor", reason);
        Assert.True(AutoBuyService.Evaluate(rule, Item(price: 220m)).Ok);
    }

    [Fact]
    public void Every_required_word_must_appear()
    {
        var rule = Rule(max: 500m);
        rule.RequiredKeywords = "S19, 95TH";
        var (ok, reason) = AutoBuyService.Evaluate(rule, Item(price: 200m, title: "Antminer S19 hashboard"));
        Assert.False(ok);
        Assert.Contains("missing required word", reason);
        Assert.Contains("95TH", reason);
        Assert.True(AutoBuyService.Evaluate(rule, Item(price: 200m, title: "Bitmain Antminer S19 95TH/s tested")).Ok);
    }

    [Fact]
    public void A_floor_at_or_above_the_ceiling_cannot_be_saved()
    {
        var store = FreshStore();
        Assert.Throws<InvalidOperationException>(() => store.SaveRule(new AutoBuyRuleRequest
        {
            Query = "x", MaxItemPrice = 100m, MinItemPrice = 100m, BudgetCap = 500m,
        }));
    }

    [Fact]
    public void The_new_fields_round_trip_through_the_store()
    {
        var store = FreshStore();
        var saved = store.SaveRule(new AutoBuyRuleRequest
        {
            Query = "Antminer S19", MaxItemPrice = 300m, MinItemPrice = 150m, BudgetCap = 900m,
            RequiredKeywords = "S19, tested", Enabled = true,
        });
        var read = store.GetRule(saved.Id)!;
        Assert.Equal(150m, read.MinItemPrice);
        Assert.Equal("S19, tested", read.RequiredKeywords);
        store.RecordRun(read.Id, "no_match", DateTimeOffset.UtcNow, 15, "40 scanned: 40 over the ceiling. Nothing matched.");
        Assert.Contains("40 scanned", store.GetRule(read.Id)!.LastNote);
    }

    [Fact]
    public void A_database_from_the_first_release_is_migrated_on_open()
    {
        // Build the 2.6.9 shape by hand - no required_keywords, min_item_price or last_note - then
        // open it through the store and expect the columns to be there and a rule to read cleanly.
        var path = Path.Combine(Path.GetTempPath(), $"autobuy-old-{Guid.NewGuid():N}.db");
        using (var c = new SqliteConnection($"Data Source={path}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE autobuy_rules (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL DEFAULT '', query TEXT NOT NULL DEFAULT '',
                    mode INTEGER NOT NULL DEFAULT 0, condition TEXT NOT NULL DEFAULT '', max_item_price NUMERIC NOT NULL DEFAULT 0,
                    offer_bid_price NUMERIC NOT NULL DEFAULT 0, exclude_keywords TEXT NOT NULL DEFAULT '',
                    min_seller_feedback INTEGER NOT NULL DEFAULT 0, budget_cap NUMERIC NOT NULL DEFAULT 0,
                    max_buys INTEGER NOT NULL DEFAULT 0, spent NUMERIC NOT NULL DEFAULT 0, buys INTEGER NOT NULL DEFAULT 0,
                    enabled INTEGER NOT NULL DEFAULT 0, interval_minutes INTEGER NOT NULL DEFAULT 15,
                    created_at TEXT NOT NULL DEFAULT '', last_run_at TEXT NOT NULL DEFAULT '', next_run_at TEXT NOT NULL DEFAULT '',
                    last_status TEXT NOT NULL DEFAULT 'never_run');
                INSERT INTO autobuy_rules (name, query, max_item_price, budget_cap, enabled, created_at)
                VALUES ('old', 'thing', 50, 200, 1, '2026-09-15T00:00:00.0000000+00:00');
                CREATE TABLE autobuy_settings (id INTEGER PRIMARY KEY CHECK (id = 1), armed INTEGER NOT NULL DEFAULT 0,
                    live_buying INTEGER NOT NULL DEFAULT 0, global_budget_cap NUMERIC NOT NULL DEFAULT 500,
                    global_spent NUMERIC NOT NULL DEFAULT 0, max_buys_per_day INTEGER NOT NULL DEFAULT 5,
                    buys_today INTEGER NOT NULL DEFAULT 0, buys_today_date TEXT NOT NULL DEFAULT '');
                INSERT INTO autobuy_settings (id, armed, live_buying) VALUES (1, 0, 1);
                """;
            cmd.ExecuteNonQuery();
        }

        var store = new AutoBuyStore(path);
        var rule = Assert.Single(store.ListRules());
        Assert.Equal("old", rule.Name);
        Assert.Equal(0m, rule.MinItemPrice);
        Assert.Equal("", rule.RequiredKeywords);
        // The owner's exact 2026-09-18 state: live on while disarmed. Corrected on open.
        Assert.False(store.GetSettings().LiveBuying);
    }

    // ── The two switches are ordered ──────────────────────────────────────────

    [Fact]
    public void Disarming_clears_live_buying()
    {
        var store = FreshStore();
        store.SaveSettings(armed: true, null, null, null);
        store.SaveSettings(null, liveBuying: true, null, null);
        Assert.True(store.GetSettings().LiveBuying);

        store.SaveSettings(armed: false, null, null, null);
        var s = store.GetSettings();
        Assert.False(s.Armed);
        Assert.False(s.LiveBuying);
    }

    [Fact]
    public void Live_buying_is_refused_while_disarmed()
    {
        var store = FreshStore();
        store.SaveSettings(null, liveBuying: true, null, null);
        Assert.False(store.GetSettings().LiveBuying);
    }

    [Fact]
    public void Arming_and_going_live_in_one_body_arms_only()
    {
        var store = FreshStore();
        var s = store.SaveSettings(armed: true, liveBuying: true, null, null);
        Assert.True(s.Armed);
        Assert.False(s.LiveBuying);   // live is a second, deliberate act
    }

    [Fact]
    public void A_new_rule_is_on_unless_the_body_says_otherwise()
    {
        var store = FreshStore();
        var on = store.SaveRule(new AutoBuyRuleRequest { Query = "x", MaxItemPrice = 50m, BudgetCap = 100m });
        Assert.True(on.Enabled);
        var off = store.SaveRule(new AutoBuyRuleRequest { Query = "y", MaxItemPrice = 50m, BudgetCap = 100m, Enabled = false });
        Assert.False(off.Enabled);
        // An edit that says nothing about enabled leaves it where it was.
        var edited = store.SaveRule(new AutoBuyRuleRequest { Id = off.Id, Name = "renamed" });
        Assert.False(edited.Enabled);
    }

    // ── What eBay can and cannot be asked ─────────────────────────────────────

    [Fact]
    public void A_REST_form_item_id_is_refused_before_any_call()
    {
        var (ok, reason) = AutoBuyService.Evaluate(Rule(), Item(id: "v1|123456|0"));
        Assert.False(ok);
        Assert.StartsWith("unusable item id", reason);
    }

    [Fact]
    public void An_auction_is_only_bid_on_inside_its_last_window()
    {
        var rule = Rule(mode: AutoBuyMode.AuctionBid);   // checks every 15 min -> window 18 min
        var now = new DateTimeOffset(2026, 9, 18, 12, 0, 0, TimeSpan.Zero);

        var far = Item(price: 20m); far.EndDate = now.AddHours(6).UtcDateTime;
        var (ok, reason) = AutoBuyService.Evaluate(rule, far, now);
        Assert.False(ok);
        Assert.StartsWith("not ending yet", reason);

        var soon = Item(price: 20m); soon.EndDate = now.AddMinutes(10).UtcDateTime;
        Assert.True(AutoBuyService.Evaluate(rule, soon, now).Ok);
    }

    [Fact]
    public void An_auction_already_above_the_max_bid_is_not_bid_on()
    {
        var rule = Rule(mode: AutoBuyMode.AuctionBid);   // max bid $90
        var (ok, reason) = AutoBuyService.Evaluate(rule, Item(price: 95m));
        Assert.False(ok);
        Assert.StartsWith("current bid too high", reason);
    }

    [Fact]
    public void A_low_positive_rating_is_refused_when_the_seller_floor_is_set()
    {
        var rule = Rule(); rule.MinSellerFeedback = 100;
        var seller = Item(); seller.SellerFeedbackScore = 4000; seller.SellerFeedbackPercent = 88m;
        var (ok, reason) = AutoBuyService.Evaluate(rule, seller);
        Assert.False(ok);
        Assert.StartsWith("seller positive rating too low", reason);

        var noFloor = Rule();   // no seller floor set: the percentage is not judged either
        Assert.True(AutoBuyService.Evaluate(noFloor, seller).Ok);
    }

    // ── The loop, with eBay stubbed ───────────────────────────────────────────

    private sealed class Harness
    {
        public AutoBuyStore Store = FreshStore();
        public List<(string ItemId, decimal Price)> Placed = [];
        public List<EbayOpportunityItem> Listings = [Item(price: 50m, shipping: 10m)];
        public Func<AutoBuyPlacement> Answer = () => new AutoBuyPlacement(true, "ok", Committed: true, Link: "https://www.ebay.com/mye/myebay/purchase");
        public bool Hang;

        public AutoBuyService Build()
        {
            AutoBuyListingSource source = async (_, ct) =>
            {
                if (Hang) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return Listings;
            };
            AutoBuyPlacer placer = (_, item, price, _) => { Placed.Add((item.ItemId, price)); return Task.FromResult(Answer()); };
            return new AutoBuyService(Store, source, placer, new ActionLog());
        }

        public long AddRule(AutoBuyMode mode = AutoBuyMode.BuyItNow) => Store.SaveRule(new AutoBuyRuleRequest
        {
            Query = "x", Mode = mode.ToString(), MaxItemPrice = 100m, OfferOrBidPrice = mode == AutoBuyMode.BuyItNow ? 0m : 80m,
            BudgetCap = 500m, Enabled = true,
        }).Id;

        public void GoLive()
        {
            Store.SaveSettings(armed: true, null, null, null);
            Store.SaveSettings(null, liveBuying: true, null, null);
        }
    }

    [Fact]
    public async Task A_buy_it_now_is_recorded_as_committed_and_banked_all_in()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.GoLive();

        var report = await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Equal("placed", report.Status);
        Assert.Equal(50m, h.Placed.Single().Price);                 // eBay is asked for the listing price
        var row = Assert.Single(h.Store.ListRecent());
        Assert.Equal(AutoBuyOutcome.Placed, row.Outcome);
        Assert.Equal(60m, row.Price);                               // the ledger carries item + shipping
        Assert.Contains("Committed", row.Detail);
        Assert.Contains("Pay on eBay", row.Detail);
        Assert.DoesNotContain("Bought", row.Detail);
        Assert.Equal(60m, h.Store.GetSettings().GlobalSpent);       // banked all-in
    }

    [Fact]
    public async Task An_outbid_bid_is_recorded_but_never_banked()
    {
        var h = new Harness();
        var id = h.AddRule(AutoBuyMode.AuctionBid);
        var ending = Item(price: 20m); ending.EndDate = DateTime.UtcNow.AddMinutes(5);
        h.Listings = [ending];
        h.Answer = () => new AutoBuyPlacement(true, "Outbid: the auction already stands at $95.00.", HighBidder: false);
        h.GoLive();

        var report = await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Single(h.Placed);
        Assert.Equal("no_match", report.Status);
        var row = Assert.Single(h.Store.ListRecent());
        Assert.Equal(AutoBuyOutcome.Failed, row.Outcome);
        Assert.Contains("outbid", row.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0m, h.Store.GetSettings().GlobalSpent);
        Assert.Contains("1 outbid", report.Note);
    }

    [Fact]
    public async Task A_standing_high_bid_is_banked()
    {
        var h = new Harness();
        var id = h.AddRule(AutoBuyMode.AuctionBid);
        var ending = Item(price: 20m, shipping: 5m); ending.EndDate = DateTime.UtcNow.AddMinutes(5);
        h.Listings = [ending];
        h.Answer = () => new AutoBuyPlacement(true, "High bidder; current price $25.00.", HighBidder: true);
        h.GoLive();

        await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Equal(80m, h.Placed.Single().Price);                 // the max bid goes to eBay
        Assert.Equal(85m, h.Store.GetSettings().GlobalSpent);       // max bid + shipping reserved
        Assert.Contains("NOT released", h.Store.ListRecent().Single().Detail);
    }

    [Fact]
    public async Task When_eBay_will_not_let_the_app_place_offers_the_rule_is_paused_with_the_reason()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Answer = () => new AutoBuyPlacement(false, "This call is not enabled for the application.", NotEnabled: true);
        h.GoLive();

        var report = await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Equal("not_enabled", report.Status);
        var rule = h.Store.GetRule(id)!;
        Assert.False(rule.Enabled);
        Assert.Contains("paused", rule.LastNote);
        Assert.Single(h.Placed);                                     // tried once, never again
        Assert.Equal(0m, h.Store.GetSettings().GlobalSpent);
    }

    [Fact]
    public async Task The_run_note_says_why_nothing_matched()
    {
        var h = new Harness();
        var id = h.AddRule();
        h.Listings =
        [
            Item(price: 500m),                       // over the ceiling
            Item(price: 40m, stated: false),         // shipping not stated
            Item(price: 40m, title: "broken thing"), // excluded word below
        ];
        var rule = h.Store.GetRule(id)!; rule.ExcludeKeywords = "broken"; h.Store.Upsert(rule);
        h.Store.SaveSettings(armed: true, null, null, null);

        var report = await h.Build().RunRuleAsync(id, manual: true, CancellationToken.None);

        Assert.Equal("no_match", report.Status);
        Assert.Contains("3 scanned", report.Note);
        Assert.Contains("over the ceiling", report.Note);
        Assert.Contains("shipping not stated", report.Note);
        Assert.Contains("excluded word", report.Note);
        Assert.Equal(report.Note, h.Store.GetRule(id)!.LastNote);   // the console can show it
    }

    [Fact]
    public async Task A_run_that_times_out_records_its_own_status_not_the_previous_rules()
    {
        var h = new Harness();
        var first = h.AddRule();
        h.Store.SaveSettings(armed: true, null, null, null);
        var service = h.Build();
        await service.RunRuleAsync(first, manual: true, CancellationToken.None);          // simulated
        Assert.Equal("simulated", h.Store.GetRule(first)!.LastStatus);

        var second = h.AddRule();
        h.Hang = true;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var report = await Record.ExceptionAsync(() => service.RunRuleAsync(second, manual: true, cts.Token));

        var status = h.Store.GetRule(second)!.LastStatus;
        Assert.NotEqual("simulated", status);   // the old bug: the previous rule's status, inherited
        Assert.NotEqual("never_run", status);   // and it did record something for THIS rule
    }
}
