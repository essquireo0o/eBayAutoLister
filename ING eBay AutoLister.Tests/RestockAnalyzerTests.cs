using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The Restock List tells a seller what to spend their own money on, which makes its failure mode
// expensive in a way the forecasting screens' isn't: a bad opportunity score costs a deal the seller
// walks away from, and a bad restock recommendation costs a van trip and a shelf full of something
// that doesn't sell.
//
// So the rules pinned here are the ones that keep it from flattering itself — a rate measured over
// three days, a sale with no cost counted as free money, one lucky flip ranked as a product line,
// and a losing line that looks busy. Every one of them makes the board's numbers SMALLER, and every
// one of them is the sort of thing a later "simplification" removes without noticing.
public class RestockAnalyzerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
    };

    private static readonly EarningsCalculator Calculator = new(new ProfitCalculator());

    /// <summary>One sold line, priced through the same calculator Money Made uses.</summary>
    private static RestockSale Sale(
        string title, DateTimeOffset soldUtc, decimal price = 300m, decimal? cost = 100m,
        decimal? fee = 30m, decimal? shippingCost = 0m, int quantity = 1,
        string status = "paid", decimal refunded = 0m, DateTimeOffset? acquiredUtc = null)
    {
        var flip = new FlipRecord
        {
            Source = "ebay",
            Title = title,
            SoldUtc = soldUtc,
            Quantity = quantity,
            SalePrice = price,
            MarketplaceFee = fee,
            ShippingCost = shippingCost,
            UnitCost = cost,
            RefundedAmount = refunded,
            Status = status,
        };

        return new RestockSale
        {
            Sale = Calculator.Compute(flip, null, Fees),
            AcquiredUtc = acquiredUtc,
        };
    }

    private static DateTimeOffset DaysAgo(int days) => Now.AddDays(-days);

    private static EbayListingSummary Listed(string title, int quantity = 1) =>
        new() { Title = title, Status = "ACTIVE", Quantity = quantity, Price = 199m };

    // ── Grouping: a product, not a title ─────────────────────────────────────────────────────

    [Fact]
    public void Three_spellings_of_one_product_are_one_line_not_three_one_off_sales()
    {
        // The whole board depends on this. Sold titles for one item are never worded the same twice,
        // and a grouping that keys on the words makes every line a single sale — which this screen
        // then correctly refuses to rank, and shows the seller nothing.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Bitmain Antminer S19j Pro 104TH Bitcoin Miner", DaysAgo(90)),
            Sale("🔥 ANTMINER S19J PRO 100TH TESTED WORKING FAST SHIP 🔥", DaysAgo(60)),
            Sale("Antminer S19j Pro miner with PSU", DaysAgo(30)),
        ], [], Now);

        Assert.Equal(1, result.Summary.ProductLines);
        var line = Assert.Single(result.Restock);
        Assert.Equal(3, line.UnitsSold);

        // And the search term is the lean title, not the one with the flames in it.
        Assert.DoesNotContain("🔥", line.SearchQuery);
        Assert.Contains("antminer", line.SearchQuery, StringComparison.OrdinalIgnoreCase);
    }

    // ── The ranking: money per month, not money ──────────────────────────────────────────────

    [Fact]
    public void The_line_that_repeats_outranks_the_bigger_single_win()
    {
        // $250 once a quarter is a better STORY than $60 a month. This board is a shopping list, so
        // it ranks the one the seller can go and do again — which is the entire reason it exists.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Dyson V11 Torque Drive", DaysAgo(130), price: 400m, cost: 100m, fee: 50m),
            Sale("Dyson V11 Animal cordless", DaysAgo(90), price: 400m, cost: 100m, fee: 50m),
            Sale("Dyson V11 vacuum tested", DaysAgo(50), price: 400m, cost: 100m, fee: 50m),
            Sale("Dyson V11 with tools", DaysAgo(10), price: 400m, cost: 100m, fee: 50m),

            Sale("Antminer S21 Hydro 335TH", DaysAgo(160), price: 3000m, cost: 2000m, fee: 400m),
            Sale("Antminer S21 Hydro miner", DaysAgo(10), price: 3000m, cost: 2000m, fee: 400m),
        ], [], Now);

        Assert.Equal(2, result.Restock.Count);

        var dyson = result.Restock.Single(l => l.Title.Contains("Dyson", StringComparison.OrdinalIgnoreCase));
        var miner = result.Restock.Single(l => l.Title.Contains("S21", StringComparison.OrdinalIgnoreCase));

        // The miner makes more than twice as much per unit and is still the second line on the
        // board, because it takes five months to do it twice.
        Assert.True(miner.AverageProfitPerUnit > dyson.AverageProfitPerUnit);
        Assert.Equal(dyson.Key, result.Restock[0].Key);
        Assert.True(dyson.ProfitPerMonth > miner.ProfitPerMonth);
    }

    [Fact]
    public void A_rate_is_never_measured_over_less_than_a_month()
    {
        // Two sales three days apart is a coincidence. Divided by its own window it reads as twenty
        // a month, and there is no seller alive who can go and find twenty of them.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Milwaukee M18 Fuel Hammer Drill", DaysAgo(10)),
            Sale("Milwaukee M18 Fuel drill kit", DaysAgo(7)),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(2m, line.SalesPerMonth);
    }

    [Fact]
    public void Four_sales_evenly_spread_over_three_months_is_about_one_a_month()
    {
        // Four sales span three gaps, not four. Dividing four sales by the span between the first
        // and the last invents a third of a sale a month here, and worse on thinner histories.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Dyson V11 Torque Drive", DaysAgo(100)),
            Sale("Dyson V11 vacuum tested", DaysAgo(70)),
            Sale("Dyson V11 Animal cordless", DaysAgo(40)),
            Sale("Dyson V11 with dock", DaysAgo(10)),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.InRange(line.SalesPerMonth, 0.95m, 1.05m);
    }

    [Fact]
    public void Sales_per_month_never_divides_by_a_zero_window()
    {
        // Two sales on the same day. The floor is what stops this being an infinity.
        Assert.Equal(2m, RestockAnalyzer.SalesPerMonth(2, 2, Now, Now));
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_sale_with_no_recorded_cost_is_never_counted_as_free_money()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Sony WH-1000XM4 headphones", DaysAgo(60), price: 200m, cost: null),
            Sale("Sony WH-1000XM4 wireless", DaysAgo(20), price: 200m, cost: null),
        ], [], Now);

        Assert.Empty(result.Restock);
        var line = Assert.Single(result.NeedsCost);

        Assert.Null(line.NetProfit);
        Assert.Null(line.ProfitPerMonth);
        Assert.Equal(2, line.UnitsAwaitingCost);
        Assert.True(line.ProceedsAwaitingCost > 0);
        Assert.Equal(0m, result.Summary.ProvenMonthlyProfit);
        Assert.Contains(result.Honesty, h => h.Contains("not as zero", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_partly_priced_line_is_measured_on_the_units_that_can_prove_it_and_says_so()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Nintendo Switch OLED console", DaysAgo(80), price: 300m, cost: 200m, fee: 30m),
            Sale("Nintendo Switch OLED white", DaysAgo(50), price: 300m, cost: 200m, fee: 30m),
            Sale("Nintendo Switch OLED boxed", DaysAgo(20), price: 300m, cost: null, fee: 30m),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(3, line.UnitsSold);
        Assert.Equal(2, line.UnitsWithKnownCost);
        Assert.Equal(1, line.UnitsAwaitingCost);

        // The profit is the two that can prove it — not two thirds of a three-unit total.
        Assert.Equal(140m, line.NetProfit);
        Assert.Equal(70m, line.AverageProfitPerUnit);
        Assert.Contains(line.Cautions, c => c.Contains("no cost recorded", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_sale_is_shown_and_never_ranked()
    {
        var result = RestockAnalyzer.Analyze([Sale("Leica M6 rangefinder camera", DaysAgo(15), price: 3000m, cost: 900m, fee: 400m)], [], Now);

        Assert.Empty(result.Restock);
        var line = Assert.Single(result.Watch);
        Assert.Equal("watch", line.Verdict);
        Assert.Contains("not a pattern", line.Headline, StringComparison.OrdinalIgnoreCase);

        // It still carries its profit — it is evidence, just not proof.
        Assert.True(line.NetProfit > 0);
        Assert.Equal(0m, result.Summary.ProvenMonthlyProfit);
    }

    [Fact]
    public void One_sale_produces_no_rate_at_all_rather_than_a_rate_of_one_a_month()
    {
        // The one-month floor turns a single sale into exactly one a month, which is not a cautious
        // estimate — it is a figure invented out of one event. Left alone it would print "$1,700 a
        // month" on the same card that says one sale is not a pattern.
        var result = RestockAnalyzer.Analyze([Sale("Leica M6 rangefinder camera", DaysAgo(15), price: 3000m, cost: 900m, fee: 400m)], [], Now);

        var line = Assert.Single(result.Watch);
        Assert.Equal(0m, line.SalesPerMonth);
        Assert.Null(line.ProfitPerMonth);
        Assert.NotNull(line.AverageProfitPerUnit);
    }

    [Fact]
    public void A_line_that_has_not_sold_in_half_a_year_stops_being_a_restock()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Canon EOS 5D Mark III body", DaysAgo(600)),
            Sale("Canon EOS 5D Mark III camera", DaysAgo(400)),
        ], [], Now);

        Assert.Empty(result.Restock);
        var line = Assert.Single(result.Watch);
        Assert.Contains("last one sold", line.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(line.Cautions, c => c.Contains("Check what these go for now", StringComparison.OrdinalIgnoreCase));
    }

    // ── The stop list ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_busy_line_that_loses_money_is_a_stop_however_fast_it_moves()
    {
        // The most expensive thing a reseller can own: something that sells briskly at a loss. It
        // looks like the best line on the board until somebody works out the margin.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("HP LaserJet Pro M404n printer", DaysAgo(60), price: 100m, cost: 90m, fee: 15m),
            Sale("HP LaserJet M404n tested", DaysAgo(40), price: 100m, cost: 90m, fee: 15m),
            Sale("HP LaserJet Pro M404n mono", DaysAgo(20), price: 100m, cost: 90m, fee: 15m),
        ], [], Now);

        Assert.Empty(result.Restock);
        var line = Assert.Single(result.Stop);
        Assert.Contains("Lost", line.Headline, StringComparison.Ordinal);
        Assert.True(line.AverageProfitPerUnit < 0);
    }

    [Fact]
    public void A_profitable_line_that_keeps_coming_back_is_a_stop_and_the_margin_is_the_warning()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Milwaukee M18 Fuel Hammer Drill", DaysAgo(70)),
            Sale("Milwaukee M18 Fuel drill kit", DaysAgo(50)),
            Sale("Milwaukee M18 Fuel brushless", DaysAgo(40), status: "refunded", refunded: 300m),
            Sale("Milwaukee M18 Fuel drill only", DaysAgo(20), status: "refunded", refunded: 300m),
        ], [], Now);

        Assert.Empty(result.Restock);
        var line = Assert.Single(result.Stop);
        Assert.Equal(50m, line.RefundRatePercent);
        Assert.Equal(2, line.ReturnedUnits);

        // The seller is told what makes it tempting, not just that it's bad.
        Assert.Contains(line.Cautions, c => c.Contains("easy to keep buying", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_return_out_of_three_is_a_caution_not_a_stop()
    {
        // Everything returns sometimes. A rule that stopped a line on one return would empty the
        // shopping list of every product a seller has ever sold more than twice.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Bose QuietComfort 45 headphones", DaysAgo(80)),
            Sale("Bose QuietComfort 45 black", DaysAgo(50)),
            Sale("Bose QuietComfort 45 boxed", DaysAgo(20), status: "refunded", refunded: 300m),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(1, line.ReturnedUnits);
        Assert.Contains(line.Cautions, c => c.Contains("came back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_refunded_sale_is_not_demand_and_is_not_profit()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Garmin Fenix 7 watch", DaysAgo(60)),
            Sale("Garmin Fenix 7 sapphire", DaysAgo(30)),
            Sale("Garmin Fenix 7 solar", DaysAgo(10), status: "cancelled"),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(2, line.UnitsSold);
        Assert.Equal(1, line.ReturnedUnits);
        Assert.Equal(2, line.UnitsWithKnownCost);
    }

    // ── The shelf ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Money_the_seller_is_not_earning_because_the_shelf_is_empty_is_the_headline()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Dyson V11 Torque Drive", DaysAgo(60)),
            Sale("Dyson V11 Animal", DaysAgo(30)),
            Sale("Weber Genesis II gas grill", DaysAgo(50)),
            Sale("Weber Genesis II E-335", DaysAgo(20)),
        ],
        [Listed("Weber Genesis II gas grill, tested")], Now);

        var dyson = result.Restock.Single(l => l.Title.Contains("Dyson", StringComparison.OrdinalIgnoreCase));
        var weber = result.Restock.Single(l => l.Title.Contains("Weber", StringComparison.OrdinalIgnoreCase));

        Assert.True(dyson.SoldOut);
        Assert.Equal(0, dyson.ActiveListings);
        Assert.False(weber.SoldOut);
        Assert.Equal(1, weber.ActiveListings);

        Assert.Equal(1, result.Summary.SoldOutLines);
        Assert.Equal(dyson.ProfitPerMonth, result.Summary.MonthlyProfitOffTheShelf);
        Assert.Equal(dyson.AverageUnitCost, result.Summary.CashToRestockSoldOut);
        Assert.Contains(dyson.Cautions, c => c.Contains("none listed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Being_sold_out_is_never_held_against_a_line()
    {
        // An empty shelf can't sell. A board that treated "nothing sold lately" and "nothing listed
        // lately" as the same fact would bury exactly the lines it exists to surface.
        var soldOut = RestockAnalyzer.Analyze(
        [
            Sale("Shark Navigator Lift-Away vacuum", DaysAgo(150)),
            Sale("Shark Navigator vacuum cleaner", DaysAgo(120)),
        ], [], Now);

        var line = Assert.Single(soldOut.Restock);
        Assert.Equal("restock", line.Verdict);
        Assert.True(line.SoldOut);
        Assert.DoesNotContain(line.Cautions, c => c.Contains("may be slower", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_listing_that_is_live_and_not_selling_is_evidence_and_is_said_out_loud()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Shark Navigator Lift-Away vacuum", DaysAgo(150)),
            Sale("Shark Navigator vacuum cleaner", DaysAgo(120)),
        ],
        [Listed("Shark Navigator Lift-Away vacuum, works great")], Now);

        var line = Assert.Single(result.Restock);
        Assert.False(line.SoldOut);
        Assert.Contains(line.Cautions, c => c.Contains("may be slower", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void When_ebay_cannot_be_read_nothing_is_reported_as_in_stock_or_out_of_it()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Dyson V11 Torque Drive", DaysAgo(60)),
            Sale("Dyson V11 Animal", DaysAgo(30)),
        ], null, Now);

        Assert.Equal("unavailable", result.StockStatus);
        var line = Assert.Single(result.Restock);
        Assert.Null(line.ActiveListings);
        Assert.False(line.SoldOut);
        Assert.Equal(0m, result.Summary.MonthlyProfitOffTheShelf);
        Assert.Contains(result.Honesty, h => h.Contains("could not be read", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void With_ebay_unreadable_no_caution_claims_anything_about_what_is_listed()
    {
        // The slow-on-the-shelf caution is an argument from a live listing that isn't selling. With
        // no idea what is listed there is no such argument, and the sentence would print "You have
        // listed and none has sold in 120 days" — a claim about stock nobody looked at.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Shark Navigator Lift-Away vacuum", DaysAgo(150)),
            Sale("Shark Navigator vacuum cleaner", DaysAgo(120)),
        ], null, Now);

        var line = Assert.Single(result.Restock);
        Assert.All(line.Cautions, c =>
        {
            Assert.DoesNotContain("listed", c, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("in stock", c, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void An_ended_listing_is_not_stock()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Dyson V11 Torque Drive", DaysAgo(60)),
            Sale("Dyson V11 Animal", DaysAgo(30)),
        ],
        [new EbayListingSummary { Title = "Dyson V11 Torque Drive vacuum", Status = "ENDED", Quantity = 1 }], Now);

        var line = Assert.Single(result.Restock);
        Assert.True(line.SoldOut);
    }

    // ── Return on cash ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_margin_becomes_a_speed_only_when_the_purchase_date_was_recorded()
    {
        // $70 on a $200 buy is 35%. Whether that is a good business depends entirely on whether the
        // $200 was tied up for twelve days or twelve months, and nothing can guess which.
        var withDates = RestockAnalyzer.Analyze(
        [
            Sale("Nintendo Switch OLED console", DaysAgo(80), price: 300m, cost: 200m, fee: 30m, acquiredUtc: DaysAgo(100)),
            Sale("Nintendo Switch OLED white", DaysAgo(50), price: 300m, cost: 200m, fee: 30m, acquiredUtc: DaysAgo(70)),
        ], [], Now);

        var line = Assert.Single(withDates.Restock);
        Assert.Equal(20, line.MedianDaysHeld);
        Assert.Equal(2, line.UnitsWithHoldingTime);
        Assert.NotNull(line.AnnualReturnOnCashPercent);
        Assert.True(line.AnnualReturnOnCashPercent > line.RoiPercent);

        var withoutDates = RestockAnalyzer.Analyze(
        [
            Sale("Nintendo Switch OLED console", DaysAgo(80), price: 300m, cost: 200m, fee: 30m),
            Sale("Nintendo Switch OLED white", DaysAgo(50), price: 300m, cost: 200m, fee: 30m),
        ], [], Now);

        var blind = Assert.Single(withoutDates.Restock);
        Assert.Null(blind.MedianDaysHeld);
        Assert.Null(blind.AnnualReturnOnCashPercent);
        Assert.NotNull(blind.RoiPercent);
    }

    [Fact]
    public void A_same_day_flip_does_not_report_an_infinite_return()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Weber Genesis II grill", DaysAgo(40), acquiredUtc: DaysAgo(40)),
            Sale("Weber Genesis II E-335", DaysAgo(20), acquiredUtc: DaysAgo(20)),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(0, line.MedianDaysHeld);
        Assert.NotNull(line.AnnualReturnOnCashPercent);
        Assert.True(line.AnnualReturnOnCashPercent < 100_000m);
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Concentration_is_reported_rather_than_left_for_the_seller_to_notice()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Antminer S19j Pro 104TH", DaysAgo(60), price: 2000m, cost: 900m, fee: 250m),
            Sale("Antminer S19j Pro miner", DaysAgo(30), price: 2000m, cost: 900m, fee: 250m),
            Sale("Milwaukee M18 Fuel drill", DaysAgo(60), price: 150m, cost: 100m, fee: 20m),
            Sale("Milwaukee M18 Fuel kit", DaysAgo(30), price: 150m, cost: 100m, fee: 20m),
        ], [], Now);

        Assert.Equal(2, result.Summary.RankedLines);
        Assert.Contains("Antminer", result.Summary.TopLineTitle, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Summary.TopLineShareOfProfitPercent > 90m);
    }

    [Fact]
    public void With_no_sales_at_all_the_board_says_where_the_sales_come_from()
    {
        var result = RestockAnalyzer.Analyze([], [], Now);

        Assert.Equal("no_sales", result.Status);
        Assert.Empty(result.Restock);
        Assert.Contains(result.Honesty, h => h.Contains("Money Made", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_product_whose_every_sale_came_back_is_a_stop_with_no_rate_behind_it()
    {
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Beats Studio3 wireless headphones", DaysAgo(40), status: "refunded", refunded: 300m),
            Sale("Beats Studio3 over-ear", DaysAgo(20), status: "refunded", refunded: 300m),
        ], [], Now);

        var line = Assert.Single(result.Stop);
        Assert.Equal(0, line.UnitsSold);
        Assert.Equal(0m, line.SalesPerMonth);
        Assert.Null(line.ProfitPerMonth);
        Assert.Contains("came back", line.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_number_on_the_board_carries_the_same_quantity_a_multi_unit_sale_reported()
    {
        // A two-unit order is two units of demand and two units of cost, not one of each. Getting
        // this wrong halves the rate on every seller who lists with quantity.
        var result = RestockAnalyzer.Analyze(
        [
            Sale("Ubiquiti UniFi U6 access point", DaysAgo(60), price: 200m, cost: 60m, fee: 25m, quantity: 2),
            Sale("Ubiquiti UniFi U6 Lite AP", DaysAgo(30), price: 200m, cost: 60m, fee: 25m, quantity: 2),
        ], [], Now);

        var line = Assert.Single(result.Restock);
        Assert.Equal(4, line.UnitsSold);
        Assert.Equal(2, line.Orders);
        Assert.Equal(4, line.UnitsWithKnownCost);
        Assert.Equal(60m, line.AverageUnitCost);
    }
}
