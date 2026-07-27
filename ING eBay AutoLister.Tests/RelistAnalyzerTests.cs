using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Recovering a lost sale is the cheapest money in reselling and the easiest to get wrong: eBay's
// own Relist button puts the failed price straight back up, and a tool that "optimizes" it without
// a floor just relists a loss. The cases below pin the things that cost the seller real money if
// they were wrong:
//
//   * no relist price ever lands under break-even or under the profit the seller asked to keep;
//   * a listing nobody ever SAW is never marked down, because the price was not the blocker;
//   * a listing eBay has already relisted is never relisted again (that is a duplicate on the site);
//   * a Second Chance Offer is never priced above what the bidder actually bid — eBay won't carry
//     it — and never below the seller's floor;
//   * "eBay didn't say" and "nobody looked" lead to different recommendations, and are kept apart.
public class RelistAnalyzerTests
{
    private static readonly FeeProfile Fees = new();          // 13.25% + $0.40, nothing else
    private static readonly ProfitCalculator Profit = new();
    private static readonly RelistAnalyzer Analyzer = new(Profit);

    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    private static decimal BreakEvenFor(decimal unitCost) =>
        Profit.Calculate(unitCost, 1, 100m, 100m, 0m, Fees).BreakEvenSalePrice;

    private static RelistAnalyzer.PriceSuggestion Suggest(
        decimal endPrice = 200m, decimal? market = null, decimal? floor = null,
        int? watchers = 0, int? hits = 0, int comps = 8, bool comparable = true) =>
        RelistAnalyzer.SuggestRelistPrice(endPrice, market, floor, watchers, hits, comps, comparable);

    private static EbayEndedListing Ended(
        string id = "110001", string title = "Antminer S19 95TH Bitcoin Miner",
        decimal price = 200m, int? watchers = 0, int? hits = 0, int bids = 0,
        decimal? highBid = null, string type = "FixedPriceItem",
        string relistedAs = "", int daysAgo = 10, int? ranDays = 7, int quantity = 1) =>
        new()
        {
            ListingId = id,
            Title = title,
            ListingType = type,
            Price = price,
            Quantity = quantity,
            QuantityUnsold = quantity,
            WatchCount = watchers,
            HitCount = hits,
            BidCount = bids,
            HighBid = highBid,
            RelistedItemId = relistedAs,
            EndTimeUtc = Now.AddDays(-daysAgo),
            StartTimeUtc = ranDays is int d ? Now.AddDays(-daysAgo - d) : null,
        };

    private static ResalePricing Priced(decimal market, int comps = 8, decimal? quickSale = null) =>
        new()
        {
            LookupTitle = "Antminer S19 95TH",
            ExpectedSale = market,
            Median = market,
            QuickSale = quickSale ?? market * 0.9m,
            SoldCompCount = comps,
        };

    private static CostBasisEntry Cost(decimal unitCost) => new() { UnitCost = unitCost };

    // ── The floor: the one rule that is about the seller's own money ─────────────────────────

    [Fact]
    public void A_relist_price_never_lands_under_the_floor()
    {
        // Every ladder input pushing down at once, against a floor at $180.
        var s = Suggest(endPrice: 200m, market: 120m, floor: 180m, watchers: 0, hits: 400);
        Assert.NotNull(s.Price);
        Assert.True(s.Price >= 180m, $"relist price {s.Price} went under the $180 floor");
        Assert.True(s.FloorLimited);
    }

    [Fact]
    public void A_listing_that_was_under_its_own_break_even_goes_back_up_at_the_floor_not_the_old_price()
    {
        // Listed at $150 with a floor of $190: it was losing money the entire time it was up.
        var s = Suggest(endPrice: 150m, market: 260m, floor: 190m, watchers: 3);
        Assert.Equal(190m, s.Price);
        Assert.True(s.FloorLimited);
        Assert.Contains("under the $190.00", s.Signal);
    }

    [Fact]
    public void No_profitable_price_is_reported_as_underwater_rather_than_relisted_at_a_loss()
    {
        var item = Analyzer.Build(
            Ended(price: 200m, watchers: 4), Priced(market: 120m), Cost(150m), Fees, Now);

        Assert.Equal("underwater", item.Verdict);
        Assert.Contains("clear costs", item.VerdictNote);
        Assert.False(item.CanRelist);
    }

    [Fact]
    public void The_profit_the_seller_asked_to_keep_raises_the_floor_above_break_even()
    {
        var breakEven = BreakEvenFor(100m);
        var floorAtBreakEven = NetProceedsCalculator.MinimumOffer(breakEven, Fees, 0m, 0m).Price!.Value;
        var floorWithProfit = NetProceedsCalculator.MinimumOffer(breakEven, Fees, 0m, 40m).Price!.Value;

        Assert.True(floorWithProfit > floorAtBreakEven);

        var withProfit = Analyzer.Build(
            Ended(price: 200m, watchers: 6), Priced(market: 130m), Cost(100m), Fees, Now,
            minNetProfitOverride: 40m);

        Assert.True(withProfit.RelistPrice >= floorWithProfit,
            $"relist price {withProfit.RelistPrice} undercut the ${floorWithProfit} profit floor");
    }

    // ── Diagnosing why it didn't sell ────────────────────────────────────────────────────────

    [Fact]
    public void Priced_above_market_comes_back_down_to_the_market()
    {
        var s = Suggest(endPrice: 200m, market: 150m, watchers: 2);
        Assert.Equal("above_market", s.Reason);
        // Charm rounding takes it to $149.99, never above the market figure.
        Assert.True(s.Price <= 150m);
        Assert.True(s.Price >= 149m);
    }

    [Fact]
    public void A_listing_nobody_saw_is_never_marked_down()
    {
        // At market, zero watchers, three views: the price cannot be what stopped this.
        var s = Suggest(endPrice: 200m, market: 200m, watchers: 0, hits: 3);
        Assert.Equal("visibility", s.Reason);
        Assert.Equal(200m, s.Price);
        Assert.True(s.SamePrice);
        Assert.Contains("almost nobody found it", s.Signal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Views_without_watchers_is_a_price_problem_and_does_get_a_cut()
    {
        var s = Suggest(endPrice: 200m, market: 200m, watchers: 0, hits: 120);
        Assert.Equal("seen", s.Reason);
        Assert.True(s.Price < 200m);
        Assert.False(s.SamePrice);
    }

    [Fact]
    public void A_crowd_of_watchers_takes_a_smaller_step_than_a_lone_one()
    {
        var crowd = Suggest(endPrice: 200m, market: 200m, watchers: 12);
        var lonely = Suggest(endPrice: 200m, market: 200m, watchers: 1);

        Assert.Equal("crowd", crowd.Reason);
        Assert.Equal("interested", lonely.Reason);
        Assert.True(crowd.Price > lonely.Price,
            "a queue of watchers should need less of a discount than a single one, not more");
    }

    [Fact]
    public void Unknown_watcher_and_view_counts_are_not_treated_as_zero()
    {
        var unknown = Suggest(endPrice: 200m, market: 200m, watchers: null, hits: null);
        var known = Suggest(endPrice: 200m, market: 200m, watchers: 0, hits: 0);

        Assert.Equal("no_evidence", unknown.Reason);
        Assert.Equal("visibility", known.Reason);
        // Both relist unchanged, but for different stated reasons — one is a finding, one is a gap.
        Assert.Equal(200m, unknown.Price);
        Assert.DoesNotContain("nobody found it", unknown.Signal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Already_under_market_and_still_unsold_is_never_raised()
    {
        var s = Suggest(endPrice: 100m, market: 200m, watchers: 4);
        Assert.Equal("under_market", s.Reason);
        Assert.Equal(100m, s.Price);
        Assert.True(s.SamePrice);
    }

    // ── Evidence bars ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Thin_sold_history_cannot_move_the_price()
    {
        // Two comps saying "$120" is not enough to cut a $200 listing by 40%.
        var s = Suggest(endPrice: 200m, market: 120m, watchers: 3, comps: 2);
        Assert.NotEqual("above_market", s.Reason);
        Assert.Equal("interested", s.Reason);
    }

    [Fact]
    public void A_failed_comp_match_is_never_acted_on()
    {
        var s = Suggest(endPrice: 200m, market: 12m, watchers: 3, comparable: false);
        Assert.NotEqual("above_market", s.Reason);
        Assert.True(s.Price > 150m, "a matching failure must not drag the relist price to $12");
    }

    [Fact]
    public void A_lot_listing_is_not_priced_against_per_unit_comps()
    {
        var item = Analyzer.Build(
            Ended(title: "Lot of 20 Antminer S19 Power Supplies", price: 900m, watchers: 3),
            Priced(market: 60m), cost: null, Fees, Now, lotQuantity: 20);

        Assert.False(item.MarketComparable);
        Assert.True(item.RelistPrice > 800m, "a lot must not be repriced down to one unit's comp");
        Assert.Contains(item.Signals, s => s.Contains("lot of 20"));
    }

    // ── Caps and no-op changes ───────────────────────────────────────────────────────────────

    [Fact]
    public void No_single_relist_cuts_more_than_the_cap()
    {
        var s = Suggest(endPrice: 1000m, market: 200m, watchers: 3);
        // $699.99 — the cap at $700, then a cent off for the charm price.
        Assert.True(s.Price >= 699m, $"one relist cut to {s.Price} — deeper than the 30% cap");
    }

    [Fact]
    public void A_change_too_small_to_matter_relists_at_the_old_price_rather_than_doing_nothing()
    {
        // The floor sits $3 under the price that failed, so the 3% step the watchers earned has
        // nowhere to go. The whole difference from the repricer: the listing is DOWN, so "no
        // change" still means put it back up — it must not come back as "leave it alone".
        var s = Suggest(endPrice: 200m, market: 200m, watchers: 12, floor: 197m);

        Assert.True(s.SamePrice);
        Assert.Equal(200m, s.Price);
        Assert.Equal("no_change", s.Reason);
        Assert.True(s.FloorLimited);
    }

    [Fact]
    public void A_listing_with_no_price_produces_no_relist()
    {
        var s = Suggest(endPrice: 0m, market: 200m);
        Assert.Null(s.Price);
        Assert.Equal("no_price", s.Reason);

        var item = Analyzer.Build(Ended(price: 0m), Priced(market: 200m), cost: null, Fees, Now);
        Assert.Equal("no_price", item.Verdict);
        Assert.False(item.CanRelist);
    }

    // ── Things that must never be relisted ───────────────────────────────────────────────────

    [Fact]
    public void A_listing_ebay_has_already_relisted_is_never_relisted_again()
    {
        var item = Analyzer.Build(
            Ended(relistedAs: "220002", watchers: 9), Priced(market: 250m), Cost(80m), Fees, Now);

        Assert.Equal("already_relisted", item.Verdict);
        Assert.True(item.AlreadyRelisted);
        Assert.False(item.CanRelist);
        Assert.Contains("duplicate", item.VerdictNote);
    }

    [Fact]
    public void A_listing_the_seller_ended_early_is_not_counted_as_a_lost_sale()
    {
        // Booked for 7 days, pulled after 2.
        var item = Analyzer.Build(
            new EbayEndedListing
            {
                ListingId = "110009", Title = "Antminer S19", Price = 300m, Quantity = 1, QuantityUnsold = 1,
                ListingType = "FixedPriceItem", WatchCount = 3, HitCount = 40, EndedByUser = true,
                StartTimeUtc = Now.AddDays(-9), EndTimeUtc = Now.AddDays(-7),
            },
            Priced(market: 300m), Cost(100m), Fees, Now);

        Assert.Equal("ended_by_seller", item.Verdict);
        Assert.False(item.CanRelist);

        var summary = RelistAnalyzer.Summarize([item]);
        Assert.Equal(0m, summary.AskedAndUnsold);
        Assert.Equal(0m, summary.CashSunk);
    }

    // ── Second Chance Offers ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_second_chance_offer_is_priced_at_exactly_what_the_bidder_bid()
    {
        var bidder = RelistAnalyzer.BuildBidder("realbuyer", 180m, 1, floorPrice: 100m,
            breakEvenPrice: BreakEvenFor(80m), Fees);

        Assert.True(bidder.CanSend);
        Assert.Equal(180m, bidder.OfferPrice);
        Assert.NotNull(bidder.NetProfitAtOffer);
    }

    [Fact]
    public void A_bid_under_the_floor_produces_no_offer_at_all()
    {
        // eBay won't carry an offer above their bid, so there is no price that works for both.
        var bidder = RelistAnalyzer.BuildBidder("realbuyer", 90m, 1, floorPrice: 140m,
            breakEvenPrice: BreakEvenFor(110m), Fees);

        Assert.Equal("below_floor", bidder.Status);
        Assert.False(bidder.CanSend);
        Assert.Contains("$140.00", bidder.Note);
        // The net at their price is still shown — negative — so the seller can decide for themselves.
        Assert.NotNull(bidder.NetProfitAtOffer);
    }

    [Fact]
    public void A_masked_bidder_id_can_never_be_sent_to()
    {
        var bidder = RelistAnalyzer.BuildBidder("r***r", 180m, 1, floorPrice: 100m, breakEvenPrice: 90m, Fees);
        Assert.Equal("anonymous", bidder.Status);
        Assert.False(bidder.CanSend);
    }

    [Fact]
    public void A_bidder_whose_bid_ebay_withheld_gets_no_invented_price()
    {
        var bidder = RelistAnalyzer.BuildBidder("realbuyer", null, 1, floorPrice: 100m, breakEvenPrice: 90m, Fees);
        Assert.Equal("no_bid", bidder.Status);
        Assert.Equal(0m, bidder.OfferPrice);
        Assert.False(bidder.CanSend);
    }

    [Fact]
    public void A_reachable_losing_bidder_takes_over_the_headline()
    {
        var item = Analyzer.Build(
            Ended(type: "Chinese", price: 200m, bids: 4, highBid: 175m, watchers: 6),
            Priced(market: 210m), Cost(80m), Fees, Now);

        Assert.NotEqual("second_chance", item.Verdict);   // no bidders looked up yet

        RelistAnalyzer.ApplyBidders(item,
        [
            RelistAnalyzer.BuildBidder("bidder_one", 175m, 1, item.FloorPrice, item.BreakEvenPrice, Fees),
            RelistAnalyzer.BuildBidder("bidder_two", 168m, 1, item.FloorPrice, item.BreakEvenPrice, Fees),
        ]);

        Assert.Equal("second_chance", item.Verdict);
        Assert.Equal(2, item.SendableBidders);
        Assert.Equal(343m, item.SecondChanceValue);
        Assert.True(item.BiddersChecked);
    }

    [Fact]
    public void An_auction_whose_bidders_are_all_under_the_floor_says_so_and_falls_back_to_a_relist()
    {
        var item = Analyzer.Build(
            Ended(type: "Chinese", price: 300m, bids: 2, highBid: 120m, watchers: 4),
            Priced(market: 320m), Cost(150m), Fees, Now);

        RelistAnalyzer.ApplyBidders(item,
        [
            RelistAnalyzer.BuildBidder("bidder_one", 120m, 1, item.FloorPrice, item.BreakEvenPrice, Fees),
        ]);

        Assert.Equal(0, item.SendableBidders);
        Assert.NotEqual("second_chance", item.Verdict);
        Assert.Contains(item.Signals, s => s.Contains("under what you need to clear costs"));
    }

    [Fact]
    public void Only_ended_auctions_with_bids_are_worth_a_bidder_lookup()
    {
        var picks = RelistAnalyzer.SelectBidderLookups(
        [
            Ended(id: "A", type: "FixedPriceItem", bids: 0, price: 900m),
            Ended(id: "B", type: "Chinese", bids: 3, highBid: 120m),
            Ended(id: "C", type: "Chinese", bids: 5, highBid: 480m),
            Ended(id: "D", type: "Chinese", bids: 2, highBid: 700m, relistedAs: "999"),
        ], budget: 10);

        Assert.Equal(["C", "B"], picks);   // biggest money first, no fixed-price, no already-relisted
    }

    [Fact]
    public void The_bidder_lookup_budget_is_respected()
    {
        var picks = RelistAnalyzer.SelectBidderLookups(
        [
            Ended(id: "B", type: "Chinese", bids: 3, highBid: 120m),
            Ended(id: "C", type: "Chinese", bids: 5, highBid: 480m),
            Ended(id: "E", type: "Chinese", bids: 1, highBid: 90m),
        ], budget: 1);

        Assert.Equal(["C"], picks);
    }

    [Fact]
    public void Only_the_four_durations_ebay_carries_are_accepted()
    {
        Assert.Equal(7, RelistAnalyzer.NormalizeDuration(7));
        Assert.Equal(RelistAnalyzer.DefaultOfferDays, RelistAnalyzer.NormalizeDuration(4));
        Assert.Equal(RelistAnalyzer.DefaultOfferDays, RelistAnalyzer.NormalizeDuration(0));
        Assert.Equal(RelistAnalyzer.DefaultOfferDays, RelistAnalyzer.NormalizeDuration(-3));
    }

    [Fact]
    public void A_seller_message_is_trimmed_to_what_ebay_will_carry()
    {
        Assert.Equal("", RelistAnalyzer.CleanMessage(null));
        Assert.Equal("hi", RelistAnalyzer.CleanMessage("  hi  "));
        Assert.Equal(RelistAnalyzer.MaxMessageLength,
            RelistAnalyzer.CleanMessage(new string('x', 400)).Length);
    }

    // ── The board totals ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_headline_totals_only_count_money_that_is_actually_recoverable()
    {
        var relistable = Analyzer.Build(
            Ended(id: "1", price: 200m, watchers: 4), Priced(market: 200m), Cost(80m), Fees, Now);
        var alreadyUp = Analyzer.Build(
            Ended(id: "2", price: 500m, relistedAs: "777"), Priced(market: 500m), Cost(200m), Fees, Now);
        var underwater = Analyzer.Build(
            Ended(id: "3", price: 300m, watchers: 2), Priced(market: 100m), Cost(200m), Fees, Now);

        var summary = RelistAnalyzer.Summarize([relistable, alreadyUp, underwater]);

        Assert.Equal(3, summary.Analyzed);
        Assert.Equal(1, summary.AlreadyRelisted);
        Assert.Equal(1, summary.Underwater);
        // The already-relisted $500 is counted on its relist, not here.
        Assert.Equal(500m, summary.AskedAndUnsold);
        Assert.Equal(280m, summary.CashSunk);
        // Only the row that can actually go back up contributes to the recovery figure.
        Assert.Equal(1, summary.ReadyToRelist);
        Assert.Equal(relistable.RelistPrice, summary.RelistValue);
    }

    [Fact]
    public void Cash_sunk_counts_every_unsold_unit_not_just_the_listing()
    {
        var item = Analyzer.Build(
            Ended(price: 50m, quantity: 6, watchers: 2), Priced(market: 50m), Cost(20m), Fees, Now);

        var summary = RelistAnalyzer.Summarize([item]);
        Assert.Equal(120m, summary.CashSunk);
        Assert.Equal(300m, summary.AskedAndUnsold);
    }

    [Fact]
    public void Second_chance_money_is_totalled_separately_from_relist_money()
    {
        var auction = Analyzer.Build(
            Ended(id: "9", type: "Chinese", price: 200m, bids: 3, highBid: 180m, watchers: 5),
            Priced(market: 220m), Cost(70m), Fees, Now);

        RelistAnalyzer.ApplyBidders(auction,
        [
            RelistAnalyzer.BuildBidder("one", 180m, 1, auction.FloorPrice, auction.BreakEvenPrice, Fees),
            RelistAnalyzer.BuildBidder("two", 165m, 1, auction.FloorPrice, auction.BreakEvenPrice, Fees),
        ]);

        var summary = RelistAnalyzer.Summarize([auction]);
        Assert.Equal(1, summary.SecondChanceListings);
        Assert.Equal(2, summary.SecondChanceBidders);
        Assert.Equal(345m, summary.SecondChanceValue);
        Assert.True(summary.SecondChanceNet > 0m);
    }

    [Fact]
    public void A_lost_bidder_outranks_every_relist_on_the_board()
    {
        var bigRelist = Analyzer.Build(
            Ended(id: "big", price: 2000m, watchers: 3), Priced(market: 2000m), Cost(400m), Fees, Now);
        var smallAuction = Analyzer.Build(
            Ended(id: "small", type: "Chinese", price: 60m, bids: 2, highBid: 55m),
            Priced(market: 60m), Cost(10m), Fees, Now);
        RelistAnalyzer.ApplyBidders(smallAuction,
            [RelistAnalyzer.BuildBidder("one", 55m, 1, smallAuction.FloorPrice, smallAuction.BreakEvenPrice, Fees)]);

        var ranked = RelistAnalyzer.Rank([bigRelist, smallAuction]);
        Assert.Equal("small", ranked[0].ListingId);
    }

    // ── Reporting ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Days_since_it_ended_and_days_it_ran_are_both_reported()
    {
        var item = Analyzer.Build(Ended(daysAgo: 12, ranDays: 30), Priced(market: 200m), null, Fees, Now);
        Assert.Equal(12, item.DaysSinceEnded);
        Assert.Equal(30, item.DaysListed);
    }

    [Fact]
    public void A_missing_end_date_is_reported_as_unknown_rather_than_as_today()
    {
        var item = Analyzer.Build(
            new EbayEndedListing { ListingId = "1", Title = "Thing", Price = 50m, ListingType = "FixedPriceItem" },
            Priced(market: 50m), null, Fees, Now);

        Assert.Null(item.DaysSinceEnded);
        Assert.Null(item.DaysListed);
    }

    [Fact]
    public void The_net_at_the_relist_price_is_shown_next_to_the_net_at_the_price_that_failed()
    {
        var item = Analyzer.Build(
            Ended(price: 200m, watchers: 0, hits: 300), Priced(market: 200m), Cost(80m), Fees, Now);

        Assert.NotNull(item.NetProfitAtEndPrice);
        Assert.NotNull(item.NetProfitAtRelist);
        Assert.True(item.NetProfitAtRelist < item.NetProfitAtEndPrice,
            "a cheaper relist has to show a smaller take-home, not a flattering one");
    }

    [Fact]
    public void Without_a_cost_basis_the_row_says_the_price_was_not_checked_against_break_even()
    {
        var item = Analyzer.Build(Ended(price: 200m, watchers: 3), Priced(market: 160m), cost: null, Fees, Now);

        Assert.Null(item.BreakEvenPrice);
        Assert.Null(item.NetProfitAtRelist);
        Assert.Contains(item.Signals, s => s.Contains("No cost recorded"));
        Assert.True(item.CanRelist);   // still relistable — just not profit-checked
    }

    [Fact]
    public void The_lookback_window_never_exceeds_what_ebay_will_return()
    {
        Assert.True(RelistAnalyzer.DefaultLookbackDays <= RelistAnalyzer.MaxLookbackDays);
        Assert.Equal(60, RelistAnalyzer.MaxLookbackDays);
    }
}
