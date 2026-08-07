using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Every other figure on the live card is a MIDDLE — the expected sale, the middle half, the bid
// that keeps the middle clearing a target. The seller is buying one object out of that spread, once,
// and it either resells above what winning cost or it does not. This counts the sales that would
// actually have covered it.
//
// What is pinned here is that the count is a count: it is made against the app's own break-even
// rather than a second arithmetic, every past sale is re-priced by exactly the cut the ceiling was
// built with, a percentage is refused when there are too few rows to have one, and nothing it finds
// is ever allowed to move a price.
public class LiveOddsTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labour

    /// <summary>Sold rows in the shape the pipeline hands over, priced as given.</summary>
    private static List<MarketplaceComparableResult> Sold(params decimal[] prices) =>
        [.. prices.Select((p, i) => new MarketplaceComparableResult
        {
            ItemId = $"c{i}", Title = "Bitmain Antminer S19j Pro", SoldPrice = p, TotalPrice = p,
        })];

    private static LiveOddsRead Read(
        IReadOnlyList<MarketplaceComparableResult>? comps, decimal need, decimal ratio = 1m,
        decimal atBid = 40m, bool atCeiling = false, decimal landedPerUnit = 40m, int lotUnits = 1) =>
        LiveOdds.Read(comps, need, ratio, atBid, atCeiling, landedPerUnit, lotUnits);

    // ── The count ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole feature in one assertion: the rows above the line are counted, the rows under it
    /// are not, and the row exactly ON it counts — a sale that lands precisely at break-even lost
    /// nothing, and the bar is "covers what winning cost", not "beats it".
    /// </summary>
    [Fact]
    public void It_counts_the_sales_that_cover_the_win_and_the_one_that_exactly_meets_it()
    {
        var read = Read(Sold(40m, 55m, 60m, 61m, 80m, 120m), need: 60m);

        Assert.True(read.Readable);
        Assert.Equal(6, read.Total);
        Assert.Equal(4, read.Covered);
        Assert.Equal(67, read.Percent);
    }

    /// <summary>
    /// The count falls as the bidding climbs, and that is the whole reason it is on a live card. The
    /// item did not change; what winning it costs did.
    /// </summary>
    [Fact]
    public void The_count_falls_as_the_bid_climbs()
    {
        var comps = Sold(40m, 55m, 60m, 61m, 80m, 120m);

        Assert.Equal(6, Read(comps, need: 30m).Covered);
        Assert.Equal(4, Read(comps, need: 60m).Covered);
        Assert.Equal(1, Read(comps, need: 100m).Covered);
        Assert.Equal(0, Read(comps, need: 200m).Covered);
    }

    /// <summary>
    /// A comp row is whatever that seller listed, and some of them were boxes of ten. The price is
    /// divided the way the pricing path divides it — <see cref="MarketPriceEstimator.UnitSoldPrice"/>,
    /// called and not restated, because two "what did one of them go for" rules is how a strip ends
    /// up disagreeing with the price above it.
    /// </summary>
    [Fact]
    public void A_comp_that_was_itself_a_multipack_is_counted_per_unit()
    {
        var box = new MarketplaceComparableResult
        {
            ItemId = "lot", Title = "Lot of 10 Antminer S19j Pro", SoldPrice = 500m, Quantity = 10,
        };

        Assert.Equal(50m, MarketPriceEstimator.UnitSoldPrice(box));

        // $500 for ten is $50 each — under a $60 bar, however much the row's own price beats it.
        Assert.Equal(0, Read([box], need: 60m).Covered);
        Assert.Equal(1, Read([box], need: 45m).Covered);
    }

    /// <summary>
    /// Rows with no price on them are not counted as failures. A missing figure is missing evidence,
    /// and counting it as a sale that came in under the line would make the odds worse for a reason
    /// that has nothing to do with the market.
    /// </summary>
    [Fact]
    public void A_row_with_no_price_is_not_counted_at_all()
    {
        var read = Read(Sold(0m, 80m, 90m, 0m, 100m), need: 60m);

        Assert.Equal(3, read.Total);
        Assert.Equal(3, read.Covered);
    }

    // ── What it refuses to claim ──────────────────────────────────────────────

    /// <summary>
    /// Four sales are enough to price an item and not enough to be odds — one row moves the
    /// percentage twenty-five points. The count is still shown, because "three of four" is a true
    /// sentence; what is withheld is the claim that it is a rate.
    /// </summary>
    [Fact]
    public void Too_few_sales_is_a_count_and_never_a_rate()
    {
        var thin = Read(Sold(80m, 90m, 100m, 40m), need: 60m);

        Assert.Equal(LiveOddsVerdicts.Thin, thin.Verdict);
        Assert.False(thin.Stated);
        Assert.Equal(3, thin.Covered);
        Assert.Equal(4, thin.Total);
        Assert.Contains("too few to be odds", thin.Headline, StringComparison.Ordinal);
        Assert.Equal("", thin.Warning);

        // One more row and it is a rate.
        var rated = Read(Sold(80m, 90m, 100m, 40m, 45m), need: 60m);
        Assert.True(rated.Stated);
        Assert.Equal(LiveOddsVerdicts.Even, rated.Verdict);
        Assert.Equal(LiveOdds.MinSalesToRate, rated.Total);
    }

    /// <summary>
    /// Nothing to count is not a bad answer, it is no answer, and the strip is absent rather than
    /// reading "0 of 0". Both ways of having nothing: no rows, and no break-even to measure them
    /// against.
    /// </summary>
    [Fact]
    public void Nothing_to_count_reads_as_nothing_rather_than_as_zero()
    {
        foreach (var read in new[]
        {
            Read(null, need: 60m),
            Read([], need: 60m),
            Read(Sold(80m, 90m), need: 0m),
            Read(Sold(80m, 90m), need: -5m),
            // The break-even the calculator returns when the fee profile eats the whole sale.
            Read(Sold(80m, 90m), need: decimal.MaxValue),
        })
        {
            Assert.False(read.Readable);
            Assert.Equal(LiveOddsVerdicts.None, read.Verdict);
            Assert.Equal("", read.Headline);
            Assert.Equal("", read.Warning);
            Assert.Equal(0, read.Total);
        }
    }

    // ── The bar it is counted against ─────────────────────────────────────────

    /// <summary>
    /// The line each sale is measured against is the app's own "what does this have to sell for",
    /// not an inversion of it written a second time here. Pinned against
    /// <see cref="ProfitCalculator"/> directly: a break-even computed twice is two break-evens.
    /// </summary>
    [Fact]
    public void The_bar_is_the_profit_calculators_own_break_even()
    {
        var resale = new ResalePricing { ExpectedSale = 200m, Median = 195m, QuickSale = 170m, AvgCompShipping = 9m };

        var need = LiveOdds.NeedPerUnit(Profit, landedPerUnit: 48.50m, resale, Fees);

        Assert.Equal(
            Profit.Calculate(48.50m, 1, 200m, 170m, 9m, Fees, actualShippingCostOverride: 9m).BreakEvenSalePrice,
            need);

        // And it moves with the cost of winning, which is what makes the count fall as the bid climbs.
        Assert.True(LiveOdds.NeedPerUnit(Profit, 90m, resale, Fees) > need);
    }

    /// <summary>
    /// A dearer win needs a dearer sale, and the difference is more than the extra dollar — eBay
    /// takes its cut of the higher price too. The bar is not the landed cost.
    /// </summary>
    [Fact]
    public void The_bar_is_above_what_winning_costs_because_the_fees_come_out_of_the_sale()
    {
        var resale = new ResalePricing { ExpectedSale = 200m, AvgCompShipping = 0m };

        Assert.True(LiveOdds.NeedPerUnit(Profit, 100m, resale, Fees) > 100m);
    }

    // ── The re-pricing ────────────────────────────────────────────────────────

    /// <summary>
    /// Each past sale is re-priced to what THIS one would fetch, by exactly the ratio the ceiling
    /// above it was built with — read off the two prices rather than re-multiplying the three cuts,
    /// so the count and the ceiling can never be arguing about different items.
    /// </summary>
    [Fact]
    public void Every_past_sale_is_repriced_by_the_ratio_the_ceiling_was_built_with()
    {
        var before = new ResalePricing { ExpectedSale = 200m, Median = 200m };
        var after = new ResalePricing { ExpectedSale = 150m, Median = 150m }; // trend + condition cuts

        var ratio = LiveOdds.RepriceRatio(before, after);
        Assert.Equal(0.75m, ratio);

        // $100 of past sales is $75 of yours, so a $80 bar that four of them cleared is now cleared
        // by none of them.
        var read = Read(Sold(100m, 100m, 100m, 100m, 100m), need: 80m, ratio: ratio);

        Assert.Equal(0, read.Covered);
        Assert.Equal(75m, read.TypicalSale);
        Assert.Equal(25, read.CutPercent);
        Assert.True(read.Repriced);
    }

    /// <summary>
    /// All three cuts only ever cut, so the ratio never lifts a past sale. A card whose reads found
    /// nothing to charge for is counted against the comps exactly as they sold.
    /// </summary>
    [Fact]
    public void The_ratio_never_raises_a_past_sale()
    {
        Assert.Equal(1m, LiveOdds.RepriceRatio(
            new ResalePricing { ExpectedSale = 100m }, new ResalePricing { ExpectedSale = 130m }));

        // And a missing price on either side leaves the comps alone rather than inventing a ratio.
        Assert.Equal(1m, LiveOdds.RepriceRatio(new ResalePricing(), new ResalePricing { ExpectedSale = 130m }));
        Assert.Equal(1m, LiveOdds.RepriceRatio(new ResalePricing { ExpectedSale = 100m }, new ResalePricing()));

        var read = Read(Sold(80m, 90m, 100m, 110m, 120m), need: 85m, ratio: 1m);
        Assert.False(read.Repriced);
        Assert.Equal(0, read.CutPercent);
        Assert.DoesNotContain("re-priced", read.Note, StringComparison.Ordinal);
    }

    // ── The verdicts ──────────────────────────────────────────────────────────

    /// <summary>
    /// Three rated states, on the two bars, and only the worst of them speaks to the card's warning
    /// list. A coin flip the seller can read on the strip is a decision; interrupting for it would
    /// be interrupting on the ordinary lot.
    /// </summary>
    [Fact]
    public void Only_the_long_state_interrupts()
    {
        // 8 of 10 — the evidence is behind the bid.
        var strong = Read(Sold(90m, 90m, 90m, 90m, 90m, 90m, 90m, 90m, 40m, 40m), need: 60m);
        Assert.Equal(LiveOddsVerdicts.Strong, strong.Verdict);
        Assert.Equal(80, strong.Percent);
        Assert.Equal("", strong.Warning);
        Assert.DoesNotContain("only", strong.Headline, StringComparison.Ordinal);

        // 5 of 10 — a coin flip, said on the strip and nowhere else.
        var even = Read(Sold(90m, 90m, 90m, 90m, 90m, 40m, 40m, 40m, 40m, 40m), need: 60m);
        Assert.Equal(LiveOddsVerdicts.Even, even.Verdict);
        Assert.Equal("", even.Warning);
        Assert.StartsWith("only ", even.Headline, StringComparison.Ordinal);

        // 3 of 10 — most of the evidence is under what winning costs.
        var over = Read(Sold(90m, 90m, 90m, 40m, 40m, 40m, 40m, 40m, 40m, 40m), need: 60m);
        Assert.Equal(LiveOddsVerdicts.Long, over.Verdict);
        Assert.Contains("Only 3 of 10 past sales", over.Warning, StringComparison.Ordinal);
        Assert.Contains("7 of them sold under that", over.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two bars are exactly where the states change, and they are pinned — a strip that quietly
    /// stopped warning at 39% would be a strip nobody could tell had changed.
    /// </summary>
    [Fact]
    public void The_bars_are_where_the_states_change()
    {
        // 70% covered — strong; 69% — even.
        Assert.Equal(LiveOddsVerdicts.Strong, Rate(70).Verdict);
        Assert.Equal(LiveOddsVerdicts.Even, Rate(69).Verdict);

        // 40% covered — even; 39% — long.
        Assert.Equal(LiveOddsVerdicts.Even, Rate(40).Verdict);
        Assert.Equal(LiveOddsVerdicts.Long, Rate(39).Verdict);

        Assert.Equal(LiveOdds.StrongPercent, 70);
        Assert.Equal(LiveOdds.EvenPercent, 40);

        // A hundred rows, `covered` of them over the line — so the percentage IS the count.
        static LiveOddsRead Rate(int covered) => Read(
            Sold([.. Enumerable.Repeat(90m, covered), .. Enumerable.Repeat(40m, 100 - covered)]), need: 60m);
    }

    // ── The sentences ─────────────────────────────────────────────────────────

    /// <summary>
    /// The line names both figures and the price on screen it is a count for, because "9 of 12" with
    /// no bid beside it is a statistic about nothing — the same twelve sales cover a $20 win and fail
    /// a $200 one.
    /// </summary>
    [Fact]
    public void The_line_names_the_count_and_the_bid_it_is_a_count_for()
    {
        var read = Read(Sold(90m, 90m, 90m, 90m, 90m, 40m), need: 60m, atBid: 46.50m);

        Assert.Equal("5 of 6 past sales cover a $46.50 win", read.Headline);
        Assert.Contains("$60.00", read.Note, StringComparison.Ordinal);
        Assert.Contains("These 6 sold between $40.00 and $90.00", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// Before the first bid the count is for a win at the card's own ceiling, and it says so. "If you
    /// win at $46" and "if you win at your $46 ceiling" are different promises, and only one of them
    /// is true of a lot nobody has bid on.
    /// </summary>
    [Fact]
    public void Before_the_first_bid_it_says_the_ceiling_is_whose_price_it_counted()
    {
        var read = Read(Sold(90m, 90m, 90m, 40m, 40m), need: 60m, atBid: 46m, atCeiling: true);

        Assert.True(read.AtCeiling);
        Assert.Contains("a win at your $46.00 ceiling", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// On a lot, every figure here is per unit — the sold rows are sales of one of the thing — and
    /// the sentence says which scale it is on rather than leaving the seller to divide a lot's
    /// landed cost by three in their head under a countdown.
    /// </summary>
    [Fact]
    public void On_a_lot_the_sentence_says_it_is_each_of_them()
    {
        var lot = Read(Sold(90m, 90m, 90m, 40m, 40m), need: 60m, lotUnits: 3);
        Assert.StartsWith("Each of them has to fetch", lot.Note, StringComparison.Ordinal);
        Assert.Equal(3, lot.LotUnits);

        var one = Read(Sold(90m, 90m, 90m, 40m, 40m), need: 60m);
        Assert.StartsWith("It has to fetch", one.Note, StringComparison.Ordinal);
    }

    // ── The spread it came out of ─────────────────────────────────────────────

    /// <summary>
    /// The three figures under the bar are the distribution the count was made from, so the seller
    /// can check it: the cheapest, the middle and the dearest of the re-priced sales.
    /// </summary>
    [Fact]
    public void The_strip_carries_the_spread_the_count_came_out_of()
    {
        var read = Read(Sold(120m, 40m, 90m, 60m, 200m), need: 60m);

        Assert.Equal(40m, read.LowSale);
        Assert.Equal(90m, read.TypicalSale);
        Assert.Equal(200m, read.HighSale);

        // An even count averages the two middles, which is the median every other screen means.
        Assert.Equal(75m, Read(Sold(120m, 40m, 90m, 60m, 200m, 30m), need: 60m).TypicalSale);
    }

    /// <summary>
    /// What winning cost per unit is carried beside the bar it produced. It is the one number that
    /// explains why a card the seller priced two minutes ago now reads differently.
    /// </summary>
    [Fact]
    public void It_carries_what_the_win_cost_per_unit()
    {
        var read = Read(Sold(90m, 90m, 90m, 40m, 40m), need: 62.31m, atBid: 46.50m, landedPerUnit: 55.20m);

        Assert.Equal(46.50m, read.AtBid);
        Assert.Equal(55.20m, read.LandedPerUnit);
        Assert.Equal(62.31m, read.NeedPerUnit);
    }
}
