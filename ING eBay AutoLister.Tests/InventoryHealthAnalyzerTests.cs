using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// This is the only feature in the app that recommends changing the price of a listing buyers can
// already see, so the cases below pin the things that would cost the seller real money if they
// were wrong: that a markdown never goes through the break-even, that an aged listing is cut
// further than a new one, that demand evidence damps the cut instead of being ignored, and that a
// price rise is never silently bundled into a bulk apply.
public class InventoryHealthAnalyzerTests
{
    private static readonly InventoryHealthAnalyzer Analyzer = new(new ProfitCalculator());
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    private static EbayListingSummary Listing(
        decimal price = 500m, int? daysOld = 120, int watchers = 0, int qty = 1, int sold = 0,
        string title = "Bitmain Antminer S19j Pro 104TH", string id = "110000000001") =>
        new()
        {
            ListingId = id, Sku = "SKU-" + id, Title = title, Status = "ACTIVE",
            Price = price, Quantity = qty, WatchCount = watchers, QuantitySold = sold,
            ListingUrl = $"https://www.ebay.com/itm/{id}",
            StartTimeUtc = daysOld is null ? null : Now.AddDays(-daysOld.Value),
        };

    private static ResalePricing Pricing(
        decimal? expected = 400m, int soldComps = 9, decimal? quickSale = null,
        int confidence = 70, decimal avgShipping = 0m) =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = quickSale ?? expected * 0.85m,
            SoldCompCount = soldComps, AvgCompShipping = avgShipping,
            ConfidenceScore = confidence, ConfidenceLevel = "Moderate",
        };

    private static CostBasisEntry Cost(decimal unit, decimal inbound = 0m) =>
        new() { ListingId = "110000000001", UnitCost = unit, InboundShipping = inbound };

    // ── Age ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DaysListed_counts_whole_days_since_the_start_time()
        => Assert.Equal(45, InventoryHealthAnalyzer.DaysListed(Now.AddDays(-45.7), Now));

    [Fact]
    public void DaysListed_is_null_when_ebay_reported_no_start_time()
        => Assert.Null(InventoryHealthAnalyzer.DaysListed(null, Now));

    [Fact]
    public void DaysListed_never_goes_negative_on_clock_skew()
        => Assert.Equal(0, InventoryHealthAnalyzer.DaysListed(Now.AddDays(3), Now));

    // ── The markdown ladder ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_new_listing_is_left_alone_even_when_slightly_over_market()
    {
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 440m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 10, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Null(price); // 10% over at 10 days old is a listing that hasn't had its run yet
    }

    [Fact]
    public void A_new_listing_priced_far_over_market_is_corrected_anyway()
    {
        // 40% over on day 9 is not a fresh listing, it's a mispriced one — the age grace period
        // is for listings whose price is defensible.
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 560m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 9, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(399.99m, price);
    }

    [Theory]
    [InlineData(45, 399.99)]    // 30-59 days: meet the market
    [InlineData(75, 387.99)]    // 60-89:  market x 0.97
    [InlineData(100, 375.99)]   // 90-119: market x 0.94
    [InlineData(150, 359.99)]   // 120-179: market x 0.90
    public void The_ladder_cuts_deeper_the_longer_a_listing_has_sat(int days, decimal expected)
    {
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 400m, quickSale: 300m, floorPrice: null,
            daysListed: days, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(expected, price);
    }

    [Fact]
    public void Past_six_months_the_quick_sale_price_is_used_when_it_is_more_aggressive()
    {
        // market x 0.85 = 340; the estimator's own quick-sale figure of 300 is lower, and it is
        // derived from the comps rather than from a flat percentage, so it wins.
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 400m, market: 400m, quickSale: 300m, floorPrice: null,
            daysListed: 200, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(299.99m, price);
    }

    [Fact]
    public void An_unknown_age_corrects_the_price_gap_but_adds_no_aging_discount()
    {
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 400m, quickSale: 300m, floorPrice: null,
            daysListed: null, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(399.99m, price); // the market price, not a laddered discount to it
    }

    // ── The floor ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_markdown_is_never_recommended_below_the_break_even()
    {
        var (price, floorLimited, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 400m, quickSale: 300m, floorPrice: 380m,
            daysListed: 200, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(380m, price);   // the ladder wanted 300; the floor stopped it
        Assert.True(floorLimited);
        Assert.Contains("break-even", signal);
    }

    [Fact]
    public void No_price_is_suggested_when_the_break_even_sits_above_the_market()
    {
        var (price, floorLimited, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 400m, quickSale: 340m, floorPrice: 460m,
            daysListed: 200, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Null(price); // there is no profitable price — saying so IS the answer
        Assert.True(floorLimited);
        Assert.Contains("no profitable price", signal);
    }

    [Fact]
    public void Charm_pricing_never_crosses_the_floor_to_reach_a_99()
    {
        // Flooring 380.40 to 379.99 would break a $380 break-even, so the exact price stands.
        Assert.Equal(380.40m, InventoryHealthAnalyzer.Charm(380.40m, floorPrice: 380m));
        Assert.Equal(379.99m, InventoryHealthAnalyzer.Charm(380.40m, floorPrice: null));
    }

    [Fact]
    public void Charm_leaves_an_already_charmed_price_alone()
        => Assert.Equal(129.99m, InventoryHealthAnalyzer.Charm(129.99m, floorPrice: null));

    // ── Guard rails ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void No_single_revision_cuts_more_than_35_percent()
    {
        // The ladder wants 200 x 0.90 = 180 against a $1,000 asking price. One revision is not
        // allowed to be a 82% dump — the seller sees each step instead.
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 1000m, market: 200m, quickSale: 170m, floorPrice: null,
            daysListed: 150, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(649.99m, price); // 1000 x 0.65, charmed
        Assert.Contains("35% cut", signal);
    }

    [Fact]
    public void A_change_too_small_to_move_a_buyer_is_not_recommended()
    {
        // 500 -> 499.99 is a one-cent revision. It churns the listing and changes nothing.
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 500m, quickSale: 425m, floorPrice: null,
            daysListed: 45, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Null(price);
    }

    // ── Demand evidence ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Watchers_at_a_fair_price_stop_a_markdown_that_would_give_away_margin()
    {
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 420m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 100, watchCount: 6, compCount: 9, confidenceScore: 70);

        Assert.Null(price);
        Assert.Contains("6 watchers", signal);
    }

    [Fact]
    public void Strong_watcher_interest_holds_an_aged_listing_at_market_instead_of_below_it()
    {
        // 200 days old would normally ladder down to the quick-sale price. Seven watchers say the
        // audience exists and price is the only blocker, so meeting the market clears it.
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 600m, market: 400m, quickSale: 300m, floorPrice: null,
            daysListed: 200, watchCount: 7, compCount: 9, confidenceScore: 70);

        Assert.Equal(399.99m, price);
        Assert.Contains("7 watchers", signal);
    }

    // ── Raises ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_price_rise_is_flagged_for_individual_review()
    {
        var (price, _, requiresReview, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 300m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 45, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.NotNull(price);
        Assert.True(price > 300m);
        Assert.True(requiresReview);
    }

    [Fact]
    public void A_below_market_listing_that_has_sat_for_months_is_not_raised()
    {
        // Below market AND unsold for 120 days is evidence the comp match is wrong for this item,
        // not evidence the price is too low. Raising it would act on the reading its own history
        // contradicts.
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 300m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 120, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Null(price);
        Assert.Contains("check the comp match", signal);
    }

    [Fact]
    public void A_raise_is_not_recommended_on_thin_sold_data()
    {
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 300m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 20, watchCount: 0, compCount: 2, confidenceScore: 70);

        Assert.Null(price);
        Assert.Contains("too little sold data", signal);
    }

    [Fact]
    public void A_raise_is_capped_at_25_percent_in_one_step()
    {
        var (price, _, _, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 100m, market: 900m, quickSale: 800m, floorPrice: null,
            daysListed: 20, watchCount: 0, compCount: 9, confidenceScore: 70);

        Assert.Equal(124.99m, price); // 100 x 1.25, charmed — not 900
    }

    // ── Listings that are actually selling ───────────────────────────────────────────────────

    [Fact]
    public void A_listing_that_has_sold_units_is_never_marked_down_on_age()
    {
        // The real case this came from: 44 sold, 64 watchers, 138 days old, "38% above market".
        // The ladder wanted to cut $105 off each of the 80 remaining units — $8,400 of margin off
        // a listing that is demonstrably selling at its current price.
        var item = Analyzer.Build(
            Listing(price: 379.99m, daysOld: 138, watchers: 64, qty: 80, sold: 44),
            Pricing(274.99m, soldComps: 10), cost: null, Fees, Now);

        Assert.Equal("selling", item.Verdict);
        Assert.Null(item.SuggestedPrice);
        Assert.Contains("working", item.VerdictNote);
    }

    [Fact]
    public void A_selling_listing_is_not_described_as_unsold()
    {
        var item = Analyzer.Build(
            Listing(price: 379.99m, daysOld: 138, qty: 80, sold: 44), Pricing(274.99m), cost: null, Fees, Now);

        Assert.DoesNotContain("unsold", item.VerdictNote);
    }

    [Fact]
    public void The_sales_rate_is_reported_per_month_over_the_listings_life()
    {
        var item = Analyzer.Build(
            Listing(daysOld: 60, qty: 10, sold: 20), Pricing(), cost: null, Fees, Now);

        Assert.Equal(10m, item.SalesPerMonth);   // 20 units over 60 days
        Assert.Contains(item.Signals, s => s.Contains("lifetime average"));
    }

    [Fact]
    public void A_selling_listing_does_not_count_towards_stale_capital()
    {
        var selling = Analyzer.Build(Listing(daysOld: 300, qty: 5, sold: 12, id: "1"), Pricing(), cost: null, Fees, Now);
        var stuck   = Analyzer.Build(Listing(daysOld: 300, id: "2"), Pricing(), cost: null, Fees, Now);

        var summary = InventoryHealthAnalyzer.Summarize([selling, stuck]);

        Assert.Equal(1, summary.StaleCount);   // stock that is turning is not stuck capital
    }

    [Fact]
    public void A_lot_listing_that_is_selling_is_labelled_selling_not_unmatched()
    {
        // Whether the comps matched is irrelevant to whether units moved. "Selling" is both the
        // more accurate label and the more useful one.
        var item = Analyzer.Build(
            Listing(price: 3000m, daysOld: 196, qty: 4, sold: 6, title: "Lot of 20 - Antminer S19"),
            Pricing(35m), cost: null, Fees, Now, lotQuantity: 20);

        Assert.False(item.MarketComparable);
        Assert.Equal("selling", item.Verdict);
        Assert.Null(item.SuggestedPrice);
    }

    [Fact]
    public void A_selling_listing_priced_under_market_can_still_be_reviewed_for_a_raise()
    {
        // Selling suppresses markdowns, not the "you're leaving money on the table" case.
        var (price, _, requiresReview, _) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 300m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 45, watchCount: 0, compCount: 9, confidenceScore: 70, quantitySold: 12);

        Assert.NotNull(price);
        Assert.True(requiresReview);
    }

    // ── When the market comparison itself failed ─────────────────────────────────────────────
    // Every case below came out of running this over a real 87-listing inventory, not out of
    // reasoning about it beforehand. Each one produced a confident, badly wrong recommendation.

    [Fact]
    public void A_multi_unit_lot_is_not_priced_against_per_unit_comps()
    {
        // Real case: "Lot of 20 - Antminer S19" at $3,000 matched a $35 single-unit comp and was
        // told to cut 35%. The comps are per unit; the listing is twenty of them.
        var item = Analyzer.Build(
            Listing(price: 3000m, daysOld: 196, title: "Lot of 20 - Antminer S19 110TH/s ASIC Miner"),
            Pricing(35m), cost: null, Fees, Now, lotQuantity: 20);

        Assert.False(item.MarketComparable);
        Assert.Null(item.SuggestedPrice);
        Assert.Contains(item.Signals, s => s.Contains("lot of 20"));
    }

    [Fact]
    public void An_implausible_gap_is_treated_as_a_matching_failure_not_a_mispricing()
    {
        // Nobody lists at 14x the going rate by accident. That is the comps matching something
        // else, and a markdown recommended off it would be confidently wrong.
        var item = Analyzer.Build(Listing(price: 1499.99m, daysOld: 383), Pricing(35m), cost: null, Fees, Now);

        Assert.False(item.MarketComparable);
        Assert.Null(item.SuggestedPrice);
        Assert.Contains(item.Signals, s => s.Contains("matching failure"));
    }

    [Fact]
    public void An_aged_listing_that_could_not_be_compared_is_still_reported_as_stale()
    {
        // Age is a fact about the listing whatever the comps did. It just isn't accused of being
        // overpriced on evidence that failed.
        var item = Analyzer.Build(
            Listing(price: 3000m, daysOld: 196, title: "Lot of 20 - Antminer S19"),
            Pricing(35m), cost: null, Fees, Now, lotQuantity: 20);

        Assert.Equal("stale", item.Verdict);
        Assert.DoesNotContain("above market", item.VerdictNote);
    }

    [Fact]
    public void A_failed_comparison_never_reaches_the_priced_above_market_total()
    {
        var lot = Analyzer.Build(
            Listing(price: 9999.99m, daysOld: 472, id: "1", title: "Lot of 100 - Antminer S19"),
            Pricing(35m), cost: null, Fees, Now, lotQuantity: 100);
        var real = Analyzer.Build(Listing(price: 500m, daysOld: 60, id: "2"), Pricing(400m), cost: null, Fees, Now);

        var summary = InventoryHealthAnalyzer.Summarize([lot, real]);

        Assert.Equal(1, summary.OverpricedCount);
        Assert.Equal(100m, summary.TotalAboveMarket);   // not the $9,965 fiction from the lot
    }

    [Fact]
    public void No_price_change_is_recommended_on_fewer_than_three_sold_comps()
    {
        var (price, _, _, signal) = InventoryHealthAnalyzer.SuggestPrice(
            listPrice: 500m, market: 400m, quickSale: 340m, floorPrice: null,
            daysListed: 150, watchCount: 0, compCount: 2, confidenceScore: 70);

        Assert.Null(price);
        Assert.Contains("too little sold data", signal);
    }

    // ── Verdicts and the money on a whole row ────────────────────────────────────────────────

    [Fact]
    public void A_listing_with_no_sold_history_is_reported_as_such_not_priced()
    {
        var item = Analyzer.Build(Listing(), resale: null, cost: null, Fees, Now);

        Assert.Equal("no_data", item.Verdict);
        Assert.Null(item.MarketPrice);
        Assert.Null(item.SuggestedPrice);
        Assert.False(item.HasRecommendation);
    }

    [Fact]
    public void Six_months_unsold_with_no_watchers_above_market_is_dead_capital()
    {
        var item = Analyzer.Build(Listing(price: 600m, daysOld: 220, watchers: 0), Pricing(400m), cost: null, Fees, Now);

        Assert.Equal("dead_capital", item.Verdict);
        Assert.Equal(220, item.DaysListed);
    }

    [Fact]
    public void Three_months_unsold_is_stale_even_when_the_price_is_fair()
    {
        var item = Analyzer.Build(Listing(price: 400m, daysOld: 100, watchers: 1), Pricing(400m), cost: null, Fees, Now);

        Assert.Equal("stale", item.Verdict);
        Assert.Contains("may not be the blocker", item.VerdictNote);
    }

    [Fact]
    public void A_break_even_above_the_market_is_reported_as_underwater()
    {
        // Paid $500 for something the market now pays $400 for: no price both sells and profits.
        var item = Analyzer.Build(Listing(price: 650m, daysOld: 150), Pricing(400m), Cost(500m), Fees, Now);

        Assert.Equal("underwater", item.Verdict);
        Assert.NotNull(item.BreakEvenPrice);
        Assert.True(item.BreakEvenPrice > 400m);
        Assert.Null(item.SuggestedPrice);
    }

    [Fact]
    public void A_listing_below_market_reports_what_it_gives_away_per_sale()
    {
        var item = Analyzer.Build(Listing(price: 320m, daysOld: 20, qty: 2), Pricing(400m), cost: null, Fees, Now);

        Assert.Equal("underpriced", item.Verdict);
        Assert.Equal(-20m, item.PriceGapPercent);
        Assert.Contains("160", item.VerdictNote); // (400 - 320) x 2 units
    }

    [Fact]
    public void The_gap_is_measured_against_the_market_price_not_the_asking_price()
    {
        var item = Analyzer.Build(Listing(price: 500m, daysOld: 60), Pricing(400m), cost: null, Fees, Now);

        Assert.Equal(25m, item.PriceGapPercent); // (500-400)/400, not (500-400)/500
    }

    [Fact]
    public void Break_even_and_net_profit_come_from_the_shared_profit_calculator()
    {
        var item = Analyzer.Build(Listing(price: 500m, daysOld: 60), Pricing(400m), Cost(200m, inbound: 20m), Fees, Now);

        // Landed cost 220 against a 13.25% + $0.40 fee: break-even = 220.40 / 0.8675 = 254.06
        Assert.Equal(254.06m, item.BreakEvenPrice);
        // Net at the $500 asking price: 500 - 220 - (500 x .1325 + .40) = 213.35
        Assert.Equal(213.35m, item.NetProfitAtListPrice);
    }

    [Fact]
    public void A_missing_cost_basis_leaves_the_break_even_unknown_rather_than_zero()
    {
        var item = Analyzer.Build(Listing(price: 500m, daysOld: 150), Pricing(400m), cost: null, Fees, Now);

        Assert.Null(item.BreakEvenPrice);   // a zero floor would let a markdown run through a loss
        Assert.Null(item.NetProfitAtListPrice);
        Assert.Contains(item.Signals, s => s.Contains("not been checked against your break-even"));
    }

    [Fact]
    public void The_missing_cost_basis_warning_is_not_repeated_on_rows_with_no_recommendation()
    {
        // It is true of every listing until the seller enters costs, so saying it on all of them
        // buries the signals that do carry information. The empty cost cell already says it.
        var item = Analyzer.Build(Listing(price: 400m, daysOld: 10), Pricing(400m), cost: null, Fees, Now);

        Assert.False(item.HasRecommendation);
        Assert.DoesNotContain(item.Signals, s => s.Contains("cost basis"));
    }

    [Fact]
    public void Capital_is_measured_at_what_was_paid_when_that_is_known()
    {
        var item = Analyzer.Build(Listing(price: 500m, qty: 3), Pricing(400m), Cost(200m), Fees, Now);

        Assert.Equal(600m, item.CapitalTiedUp);      // 200 x 3, not 500 x 3
        Assert.Equal("cost_basis", item.CapitalBasis);
    }

    [Fact]
    public void Capital_falls_back_to_market_value_then_to_the_asking_price()
    {
        var atMarket = Analyzer.Build(Listing(price: 500m), Pricing(400m), cost: null, Fees, Now);
        Assert.Equal(400m, atMarket.CapitalTiedUp);
        Assert.Equal("market_value", atMarket.CapitalBasis);

        var atAsk = Analyzer.Build(Listing(price: 500m), resale: null, cost: null, Fees, Now);
        Assert.Equal(500m, atAsk.CapitalTiedUp);
        Assert.Equal("list_price", atAsk.CapitalBasis);
    }

    // ── Portfolio summary ────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_summary_separates_stale_capital_from_total_capital()
    {
        var items = new[]
        {
            Analyzer.Build(Listing(price: 500m, daysOld: 200, id: "1"), Pricing(400m), Cost(300m), Fees, Now),
            Analyzer.Build(Listing(price: 500m, daysOld: 10,  id: "2"), Pricing(500m), Cost(250m), Fees, Now),
        };
        var summary = InventoryHealthAnalyzer.Summarize(items);

        Assert.Equal(2, summary.ListingsAnalyzed);
        Assert.Equal(1000m, summary.TotalListedValue);
        Assert.Equal(550m, summary.TotalCapitalTiedUp);
        Assert.Equal(1, summary.StaleCount);
        Assert.Equal(300m, summary.StaleCapital);   // only the 200-day-old one
    }

    [Fact]
    public void The_summary_counts_money_left_on_the_table_by_underpriced_listings()
    {
        var items = new[]
        {
            Analyzer.Build(Listing(price: 320m, daysOld: 20, qty: 2, id: "1"), Pricing(400m), cost: null, Fees, Now),
        };
        var summary = InventoryHealthAnalyzer.Summarize(items);

        Assert.Equal(1, summary.UnderpricedCount);
        Assert.Equal(160m, summary.MoneyLeftOnTable);
    }

    [Fact]
    public void Price_rises_are_excluded_from_the_bulk_reprice_count()
    {
        // A raise (below market, still fresh) and a markdown (aged, over market).
        var raise = Analyzer.Build(Listing(price: 300m, daysOld: 20, id: "1"), Pricing(400m), cost: null, Fees, Now);
        var cut   = Analyzer.Build(Listing(price: 600m, daysOld: 150, id: "2"), Pricing(400m), cost: null, Fees, Now);

        Assert.True(raise.RequiresReview);
        Assert.False(cut.RequiresReview);
        Assert.Equal(1, InventoryHealthAnalyzer.Summarize([raise, cut]).RepriceCandidates);
    }

    [Fact]
    public void The_median_listing_age_ignores_listings_whose_age_ebay_did_not_report()
    {
        var items = new[]
        {
            Analyzer.Build(Listing(daysOld: 10,   id: "1"), Pricing(), cost: null, Fees, Now),
            Analyzer.Build(Listing(daysOld: 50,   id: "2"), Pricing(), cost: null, Fees, Now),
            Analyzer.Build(Listing(daysOld: 300,  id: "3"), Pricing(), cost: null, Fees, Now),
            Analyzer.Build(Listing(daysOld: null, id: "4"), Pricing(), cost: null, Fees, Now),
        };
        var summary = InventoryHealthAnalyzer.Summarize(items);

        Assert.Equal(50, summary.MedianDaysListed);
        Assert.Equal(1, summary.UnknownAgeCount);
    }

    // ── Ranking ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Actionable_rows_lead_and_the_biggest_stuck_money_comes_first()
    {
        var noAction = Analyzer.Build(Listing(price: 400m, daysOld: 5,   id: "1"), Pricing(400m), Cost(9000m), Fees, Now);
        var smallCut = Analyzer.Build(Listing(price: 600m, daysOld: 150, id: "2"), Pricing(400m), cost: null, Fees, Now);
        var bigCut   = Analyzer.Build(Listing(price: 6000m, daysOld: 150, id: "3"), Pricing(4000m), cost: null, Fees, Now);

        var ranked = InventoryHealthAnalyzer.Rank([noAction, smallCut, bigCut]);

        Assert.Equal("3", ranked[0].ListingId);   // actionable, most capital
        Assert.Equal("2", ranked[1].ListingId);   // actionable, less capital
        Assert.Equal("1", ranked[2].ListingId);   // nothing to do, however expensive it was
    }

    // ── Scrape rationing ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scrapes_go_to_the_biggest_dollars_at_stake_not_the_biggest_percentage()
    {
        var targets = InventoryHealthAnalyzer.SelectScrapeTargets(
        [
            ("cheap-but-way-off", 6m,    false, false),
            ("pricey-and-close",  400m,  false, false),
        ], budget: 1);

        Assert.Equal(["pricey-and-close"], targets);
    }

    [Fact]
    public void Products_terapeak_already_cached_never_consume_the_scrape_budget()
    {
        var targets = InventoryHealthAnalyzer.SelectScrapeTargets(
        [
            ("already-known", 900m, true,  false),
            ("unknown",       100m, false, false),
        ], budget: 2);

        Assert.Equal(["unknown"], targets);
    }

    [Fact]
    public void Unpriceable_products_are_scraped_after_the_mispriced_ones_most_valuable_first()
    {
        var targets = InventoryHealthAnalyzer.SelectScrapeTargets(
        [
            ("unpriced-cheap", 20m,  false, true),
            ("unpriced-rich",  900m, false, true),
            ("mispriced",      50m,  false, false),
        ], budget: 3);

        Assert.Equal(["mispriced", "unpriced-rich", "unpriced-cheap"], targets);
    }

    [Fact]
    public void A_zero_scrape_budget_spends_nothing()
        => Assert.Empty(InventoryHealthAnalyzer.SelectScrapeTargets([("a", 900m, false, false)], budget: 0));
}
