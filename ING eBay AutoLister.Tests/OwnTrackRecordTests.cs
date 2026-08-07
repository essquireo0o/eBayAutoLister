using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The live card's newest claim is the boldest one on it: "on what YOU actually got for these, the
// most to bid is $48" — a ceiling derived from the seller's own bank account rather than from
// strangers' sold listings. It is the strongest evidence the app has, and for exactly that reason
// it is the easiest to get expensively wrong: a break-even flattered by one missing postage cost,
// or a match made on two ordinary words, produces a HIGHER ceiling and somebody bids to it with a
// hammer coming down.
//
// So most of what is pinned here is refusal. The record is not priced off a loose identity, off a
// single sale, off sales whose postage was never recorded, or off orders that came back — and it
// never, at any strength, moves the call on the badge.
public class OwnTrackRecordTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
    };

    private static readonly EarningsCalculator Calculator = new(new ProfitCalculator());

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    /// <summary>One of the seller's own sales, priced through the calculator Money Made uses.</summary>
    private static RestockSale Sale(
        string title, int daysAgo, decimal price = 300m, decimal? cost = 100m,
        decimal? fee = 30m, decimal? shippingCost = 20m, int quantity = 1,
        string status = "paid", DateTimeOffset? acquiredUtc = null, decimal shippingCharged = 0m)
    {
        var flip = new FlipRecord
        {
            Source = "ebay",
            Title = title,
            SoldUtc = Now.AddDays(-daysAgo),
            Quantity = quantity,
            SalePrice = price,
            ShippingCharged = shippingCharged,
            MarketplaceFee = fee,
            ShippingCost = shippingCost,
            UnitCost = cost,
            Status = status,
        };

        return new RestockSale { Sale = Calculator.Compute(flip, null, Fees), AcquiredUtc = acquiredUtc };
    }

    private static DealRecord Deal(
        string title, string stage = DealStages.Bought, int quantity = 1,
        decimal? purchasePrice = 120m, decimal extra = 0m, int boughtDaysAgo = 30) =>
        new()
        {
            Title = title,
            Stage = stage,
            Quantity = quantity,
            PurchasePrice = purchasePrice,
            PurchaseExtraCost = extra,
            BoughtUtc = Now.AddDays(-boughtDaysAgo),
            CreatedUtc = Now.AddDays(-boughtDaysAgo),
        };

    private static OwnSalesEvidence Match(
        string title, IEnumerable<RestockSale>? sales = null, IEnumerable<DealRecord>? deals = null) =>
        OwnTrackRecord.Match(title, (sales ?? []).ToList(), (deals ?? []).ToList(), Now);

    private static LiveOwnHistory Price(
        OwnSalesEvidence evidence, decimal shipping = 0m, decimal buyerFee = 0m,
        decimal target = 30m, decimal compsMaxBid = 100m, decimal? compsResale = 300m) =>
        OwnTrackRecord.Price(evidence, shipping, buyerFee, target, compsMaxBid, compsResale);

    // ── Which sales are "these" ───────────────────────────────────────────────────────────────

    [Fact]
    public void Three_spellings_of_one_product_all_count_as_the_same_thing()
    {
        // Sold titles for one item are never worded the same twice. A match on the words would find
        // none of them, and the seller's own record — the whole point — would read as empty.
        var evidence = Match(Product,
        [
            Sale("Bitmain Antminer S19j Pro 104TH Bitcoin Miner", 30),
            Sale("🔥 ANTMINER S19J PRO 100TH TESTED WORKING 🔥", 60),
            Sale("Antminer s19j pro - working, 104th", 90),
        ]);

        Assert.Equal(3, evidence.Orders);
        Assert.Equal(3, evidence.UnitsSold);
    }

    [Fact]
    public void It_groups_the_seller_s_sales_the_way_the_restock_board_does()
    {
        // Both screens claim to show "your sales of this". They read the same table through the
        // same signature, so they cannot be made to disagree about which sales those are.
        var sales = new List<RestockSale>
        {
            Sale("Bitmain Antminer S19j Pro 104TH", 30),
            Sale("ANTMINER S19J PRO tested", 60),
            Sale("Dell PowerEdge R720 Server", 45),
        };

        var evidence = Match(Product, sales);
        var board = RestockAnalyzer.Analyze(sales, null, Now);
        var line = board.Restock.Concat(board.Watch).Concat(board.Stop).Concat(board.NeedsCost)
            .Single(l => string.Equals(l.Key, evidence.Key, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(line.UnitsSold, evidence.UnitsSold);
        Assert.Equal(line.Orders, evidence.Orders);
    }

    [Fact]
    public void A_different_product_is_not_this_product()
    {
        var evidence = Match(Product, [Sale("Dell PowerEdge R720 Server", 30), Sale("Whatnot mystery box", 10)]);

        Assert.Equal(0, evidence.Orders);
        Assert.Equal(OwnTrackVerdicts.None, Price(evidence).Verdict);
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_title_with_no_model_number_is_shown_but_never_priced_off()
    {
        // "silver plated flatware" keys on two ordinary words, and those words match a great deal
        // that is not this item. A ceiling built on that would be a real number derived from the
        // wrong sales — the exact failure this screen exists to prevent, wearing the seller's own
        // name.
        var evidence = Match("silver plated flatware",
        [
            Sale("Silver plated flatware service set", 20, price: 400m),
            Sale("silver plated flatware canteen", 50, price: 380m),
        ]);

        Assert.True(evidence.IdentityIsLoose);
        Assert.Equal(2, evidence.Orders);

        var history = Price(evidence);
        Assert.Equal(0m, history.OwnMaxBid);
        Assert.Contains(history.Notes, n => n.Contains("no model number", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void One_sale_is_a_data_point_and_does_not_price_a_ceiling()
    {
        var history = Price(Match(Product, [Sale(Product, 20)]));

        Assert.Equal(OwnTrackVerdicts.Once, history.Verdict);
        Assert.Equal(0m, history.OwnMaxBid);
        Assert.Contains("not a pattern", history.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(history.Notes, n => n.Contains("second sale", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_bar_for_trusting_the_seller_s_own_prices_is_the_restock_board_s_bar()
    {
        // Two screens with two different definitions of "you have sold enough of these to know" is
        // one screen contradicting the other about the seller's own history.
        Assert.Equal(RestockAnalyzer.MinOrdersToRank, OwnTrackRecord.MinOrdersToTrust);
    }

    [Fact]
    public void A_sale_with_no_postage_recorded_is_left_out_of_the_break_even_and_said()
    {
        // Proceeds with the label missing read HIGHER than they were, and a flattered break-even is
        // a raised ceiling. Left out, counted, and said out loud.
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 300m, shippingCost: 20m),
            Sale(Product, 40, price: 300m, shippingCost: null),
            Sale(Product, 60, price: 300m, shippingCost: 20m),
        ]);

        Assert.Equal(3, evidence.Orders);
        Assert.Equal(2, evidence.UnitsPricingProceeds);
        Assert.Equal(1, evidence.UnitsMissingShippingCost);

        var withoutTheUnknownOne = Match(Product,
        [
            Sale(Product, 20, price: 300m, shippingCost: 20m),
            Sale(Product, 60, price: 300m, shippingCost: 20m),
        ]);
        Assert.Equal(withoutTheUnknownOne.AverageNetProceeds, evidence.AverageNetProceeds);

        Assert.Contains(Price(evidence).Notes, n => n.Contains("postage", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Sales_where_every_postage_cost_is_missing_price_nothing_at_all()
    {
        var evidence = Match(Product,
        [
            Sale(Product, 20, shippingCost: null),
            Sale(Product, 40, shippingCost: null),
        ]);

        Assert.Null(evidence.AverageNetProceeds);
        Assert.Equal(0m, Price(evidence).OwnMaxBid);
    }

    [Fact]
    public void Refunded_and_cancelled_orders_are_counted_separately_and_priced_nowhere()
    {
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 300m),
            Sale(Product, 30, price: 300m),
            Sale(Product, 40, price: 900m, status: "refunded"),
            Sale(Product, 50, price: 900m, status: "cancelled"),
        ]);

        Assert.Equal(2, evidence.Orders);
        Assert.Equal(2, evidence.ReturnedUnits);

        var clean = Match(Product, [Sale(Product, 20, price: 300m), Sale(Product, 30, price: 300m)]);
        Assert.Equal(clean.AverageNetProceeds, evidence.AverageNetProceeds);
        Assert.Equal(clean.AverageSalePrice, evidence.AverageSalePrice);

        Assert.Contains(Price(evidence).Notes, n => n.Contains("came back", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_sale_with_no_cost_recorded_contributes_no_profit_rather_than_zero_profit()
    {
        var evidence = Match(Product, [Sale(Product, 20, cost: null), Sale(Product, 40, cost: 100m)]);

        Assert.Equal(1, evidence.UnitsWithKnownCost);
        Assert.Equal(1, evidence.UnitsAwaitingCost);
        // The one costed sale, not the average of one number and a zero.
        Assert.NotNull(evidence.AverageNetProfit);
        Assert.Equal(100m, evidence.AverageUnitCost);
        Assert.Contains(Price(evidence).Notes, n => n.Contains("no cost recorded", StringComparison.OrdinalIgnoreCase));
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_break_even_is_what_the_seller_measured_not_what_a_fee_model_predicts()
    {
        // NetProceeds is money in minus money out, excluding the goods: eBay's real fee, the real
        // label. Averaged per unit it IS the highest price this seller could have paid and broken
        // even — which is exactly what a ceiling is built on.
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 300m, fee: 40m, shippingCost: 20m),
            Sale(Product, 40, price: 300m, fee: 40m, shippingCost: 20m),
        ]);

        Assert.Equal(240m, evidence.AverageNetProceeds);
    }

    [Fact]
    public void The_ceiling_is_the_auction_sniper_s_ceiling_run_against_the_seller_s_own_numbers()
    {
        // Not a second opinion about money — the same function, at the same terms, with different
        // evidence under it. If these two ever disagree the app has two ceilings and the bidder has
        // none.
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 300m, fee: 40m, shippingCost: 20m),
            Sale(Product, 40, price: 300m, fee: 40m, shippingCost: 20m),
        ]);

        var history = Price(evidence, shipping: 12m, buyerFee: 8m, target: 30m);
        var expected = AuctionSniperAnalyzer.MaxBidDetail(evidence.AverageNetProceeds!.Value, 12m, 30m, 8m);

        Assert.Equal(expected.MaxBid, history.OwnMaxBid);
        Assert.Equal(expected.BoundBy, history.OwnCeilingBoundBy);
        Assert.Equal(LiveBidAdvisor.BreakEvenBid(evidence.AverageNetProceeds.Value, 8m, 12m), history.OwnBreakEvenBid);
    }

    [Fact]
    public void The_premium_and_the_shipping_come_out_of_the_seller_s_own_ceiling_too()
    {
        var evidence = Match(Product, [Sale(Product, 20), Sale(Product, 40)]);

        var plain = Price(evidence, shipping: 0m, buyerFee: 0m);
        var costly = Price(evidence, shipping: 15m, buyerFee: 8m);

        Assert.True(costly.OwnMaxBid < plain.OwnMaxBid);
    }

    [Fact]
    public void A_seller_who_does_worse_than_the_comps_is_told_so_in_dollars()
    {
        // The case that costs real money: the badge is the market's number, the seller has never got
        // the market's number for one of these, and nothing on the card said so until now.
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 150m, fee: 20m, shippingCost: 15m),
            Sale(Product, 40, price: 150m, fee: 20m, shippingCost: 15m),
        ]);

        var history = Price(evidence, compsMaxBid: 200m);

        Assert.True(history.CeilingIsLower);
        Assert.True(history.OwnMaxBid < 200m);
        Assert.Equal(Math.Round(history.OwnMaxBid - 200m, 2), history.CeilingGap);
        Assert.Contains("under", history.CeilingComparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OwnTrackRecord.Warnings(history), w => w.Contains("rather", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_seller_who_does_better_than_the_comps_is_not_told_to_bid_higher()
    {
        // Encouraging, and it stops there. The badge keeps sold history behind it; the seller's own
        // two sales are two sales, and a card that raised the ceiling on them would be inviting an
        // overbid on the strength of a good week.
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 600m, fee: 60m, shippingCost: 20m),
            Sale(Product, 40, price: 600m, fee: 60m, shippingCost: 20m),
        ]);

        var history = Price(evidence, compsMaxBid: 100m);

        Assert.False(history.CeilingIsLower);
        Assert.True(history.OwnMaxBid > 100m);
        Assert.DoesNotContain(OwnTrackRecord.Warnings(history), w => w.Contains("rather than the badge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Two_ceilings_within_a_few_percent_of_each_other_are_reported_as_agreeing()
    {
        var evidence = Match(Product, [Sale(Product, 20), Sale(Product, 40)]);
        var mine = Price(evidence, compsMaxBid: 500m).OwnMaxBid;

        var history = Price(evidence, compsMaxBid: mine);

        Assert.False(history.CeilingIsLower);
        Assert.Contains("agree", history.CeilingComparison, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void When_the_comps_priced_nothing_the_seller_s_own_record_is_the_only_ceiling()
    {
        // The most valuable case on the whole screen: eBay matched nothing, the seller has sold four
        // of them, and the app can still say what to bid — off their own evidence, labelled as such.
        var evidence = Match(Product, [Sale(Product, 20), Sale(Product, 40)]);
        var history = Price(evidence, compsMaxBid: 0m, compsResale: null);

        Assert.True(history.OwnIsTheOnlyCeiling);
        Assert.True(history.OwnMaxBid > 0m);
        Assert.Null(history.CeilingGap);
        Assert.Contains(OwnTrackRecord.Warnings(history), w => w.Contains("Nothing on eBay priced this", StringComparison.Ordinal));
    }

    [Fact]
    public void A_product_that_never_cleared_its_own_costs_is_told_not_to_be_bought_at_any_price()
    {
        var evidence = Match(Product,
        [
            Sale(Product, 20, price: 30m, fee: 25m, shippingCost: 25m),
            Sale(Product, 40, price: 30m, fee: 25m, shippingCost: 25m),
        ]);

        var history = Price(evidence, compsMaxBid: 200m);

        Assert.Equal(0m, history.OwnMaxBid);
        Assert.Contains("any price", history.CeilingComparison, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OwnTrackRecord.Warnings(history), w => w.Contains("lose money", StringComparison.OrdinalIgnoreCase));
    }

    // ── What is already on the shelf ─────────────────────────────────────────────────────────

    [Fact]
    public void Units_already_bought_and_unsold_are_counted_with_the_cash_in_them()
    {
        var evidence = Match(Product, [],
        [
            Deal(Product, DealStages.Bought, quantity: 2, purchasePrice: 120m, extra: 30m, boughtDaysAgo: 96),
            Deal("Antminer S19j Pro spare", DealStages.Listed, purchasePrice: 100m, boughtDaysAgo: 20),
        ]);

        Assert.Equal(3, evidence.UnitsHeld);
        Assert.Equal(370m, evidence.CapitalHeld);
        Assert.Equal(96, evidence.OldestHeldDays);
    }

    [Fact]
    public void A_deal_that_has_already_sold_or_been_dropped_is_not_stock()
    {
        var evidence = Match(Product, [],
        [
            Deal(Product, DealStages.Sold),
            Deal(Product, DealStages.Dropped),
            Deal(Product, DealStages.Sourced),
        ]);

        Assert.Equal(0, evidence.UnitsHeld);
    }

    [Fact]
    public void Holding_stock_of_something_never_sold_is_its_own_verdict_and_its_own_warning()
    {
        // Buying a fourth while three sit unsold is not arbitrage, it is moving cash onto a shelf —
        // and at 11pm with a stream running it is completely invisible.
        var history = Price(Match(Product, [], [Deal(Product, quantity: 3, boughtDaysAgo: 96)]));

        Assert.Equal(OwnTrackVerdicts.Holding, history.Verdict);
        Assert.Equal(3, history.UnitsHeld);
        Assert.Contains(OwnTrackRecord.Warnings(history), w => w.Contains("competes with your own", StringComparison.OrdinalIgnoreCase));
    }

    // ── The sentences ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Never_having_sold_one_is_said_differently_from_having_no_sales_at_all()
    {
        // "You have never sold one of these" is a claim about the product. On an app with an empty
        // book it would be a claim about nothing, and it would be on every lot of the night.
        var noBook = Price(Match(Product));
        var soldOtherThings = Price(Match(Product, [Sale("Dell PowerEdge R720 Server", 30)]));

        Assert.Contains("Money Made", noBook.Headline, StringComparison.Ordinal);
        Assert.Contains("never sold one of these", soldOtherThings.Headline, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_proven_record_says_the_count_the_price_the_net_and_the_speed()
    {
        var history = Price(Match(Product,
        [
            Sale(Product, 20, price: 300m, cost: 100m, acquiredUtc: Now.AddDays(-32)),
            Sale(Product, 40, price: 300m, cost: 100m, acquiredUtc: Now.AddDays(-52)),
        ]));

        Assert.Equal(OwnTrackVerdicts.Proven, history.Verdict);
        Assert.Contains("You have sold 2 of these", history.Headline, StringComparison.Ordinal);
        Assert.Contains("$300", history.Headline, StringComparison.Ordinal);
        Assert.Contains("net", history.Headline, StringComparison.Ordinal);
        Assert.Contains("12 days", history.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_a_year_old_still_prices_the_ceiling_and_says_how_old_it_is()
    {
        var history = Price(Match(Product, [Sale(Product, 500), Sale(Product, 600)]));

        Assert.True(history.OwnMaxBid > 0m);
        Assert.Contains(history.Notes, n => n.Contains("months ago", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_seller_s_own_selling_price_is_compared_with_the_comps_resale()
    {
        var history = Price(
            Match(Product, [Sale(Product, 20, price: 150m), Sale(Product, 40, price: 150m)]),
            compsResale: 300m);

        Assert.Contains(history.Notes, n =>
            n.Contains("$150.00", StringComparison.Ordinal) && n.Contains("$300.00", StringComparison.Ordinal));
    }

    [Fact]
    public void The_listed_sales_are_the_most_recent_ones_and_there_are_never_many()
    {
        var sales = Enumerable.Range(1, 12).Select(i => Sale(Product, i * 5, price: 100m + i)).ToList();
        var evidence = Match(Product, sales);

        Assert.Equal(OwnTrackRecord.MaxRowsShown, evidence.Sales.Count);
        Assert.Equal(5, evidence.Sales[0].DaysAgo);
        Assert.True(evidence.Sales.Zip(evidence.Sales.Skip(1)).All(p => p.First.DaysAgo <= p.Second.DaysAgo));
    }

    [Fact]
    public void A_row_whose_postage_was_never_recorded_shows_no_proceeds_rather_than_a_flattering_one()
    {
        var evidence = Match(Product, [Sale(Product, 10, shippingCost: null)]);

        var row = Assert.Single(evidence.Sales);
        Assert.True(row.ShippingCostUnknown);
        Assert.Null(row.NetProceeds);
    }

    [Fact]
    public void A_multi_unit_sale_is_reported_per_unit()
    {
        // A lot of four at $1,200 is a $300 product, not a $1,200 one. Getting this wrong quadruples
        // a ceiling.
        var single = Match(Product, [Sale(Product, 20, price: 300m, quantity: 1), Sale(Product, 30, price: 300m)]);
        var lot = Match(Product, [Sale(Product, 20, price: 300m, quantity: 4), Sale(Product, 30, price: 300m)]);

        Assert.Equal(300m, lot.AverageSalePrice);
        Assert.Equal(5, lot.UnitsSold);
        Assert.True(lot.AverageNetProceeds < single.AverageNetProceeds * 1.2m);
    }

    // ── What it is never allowed to do ───────────────────────────────────────────────────────

    [Fact]
    public void Nothing_here_returns_null_for_a_seller_with_no_history()
    {
        // The card asks for this on every lot. A null on the empty case is a crash on the first
        // night of a new install.
        var history = OwnTrackRecord.Price(null, 0m, 0m, 30m, 100m, 200m);

        Assert.Equal(OwnTrackVerdicts.None, history.Verdict);
        Assert.NotEmpty(history.Headline);
        Assert.Empty(OwnTrackRecord.Warnings(history));
    }

    [Fact]
    public void An_empty_title_matches_nothing_rather_than_everything()
    {
        var evidence = Match("   ", [Sale(Product, 20), Sale(Product, 40)]);

        Assert.Equal(0, evidence.Orders);
        Assert.Equal(0m, Price(evidence).OwnMaxBid);
    }
}
