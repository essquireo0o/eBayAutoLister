using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// An auction lot and a Craigslist drill go through the same analyzer, but they do not cost the
// same. These pin the three differences that move real money:
//
//   1. the price is a BID, so the number that matters is the highest bid still worth making;
//   2. a buyer's premium and sales tax sit on top of it;
//   3. it may be several units, priced per unit through the Liquidation Lot Analyzer's own grades.
//
// The rule underneath all of them is that a liquidation row must never be flattering. Leaving the
// premium out overstates every profit figure; multiplying a comp by a unit count nobody stated
// invents one outright.
public class LiquidationArbitrageTests
{
    private static readonly FeeProfile Fees = new()
    {
        EbayFinalValueFeePercent = 13.25m,
        EbayFinalValueFeeFixed = 0.40m,
        DefaultShippingCost = 0m,
    };

    private static LocalArbitrageAnalyzer Analyzer() =>
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static LiquidationLotPricer Pricer() => new(new ProfitCalculator());

    private static LiquidationLotDetails Auction(
        bool isLot = false, int units = 1, string gradeId = "", decimal premium = 15m,
        decimal? claimedRetail = null, string? unpriceable = null) => new()
    {
        AuctionHouse = "Redwood Auctions",
        EventName = "Overstock Product Liquidation",
        IsLiquidationEvent = true,
        BuyerPremiumPercent = premium,
        BidCount = 2,
        IsLot = isLot,
        Units = units,
        GradeId = gradeId,
        ClaimedRetailTotal = claimedRetail,
        UnpriceableReason = unpriceable,
    };

    private static LocalSupplyListing Lot(decimal bid, LiquidationLotDetails details) => new()
    {
        Source = LiquidationCatalog.SourceId,
        SourceLabel = LiquidationCatalog.Site,
        ItemId = "306747345",
        Title = "Dyson V8 Cordless Vacuum",
        Price = bid,
        Location = "Lindon, UT",
        Liquidation = details,
    };

    private static LocalSupplyListing LocalDeal(decimal price) => new()
    {
        Source = "craigslist",
        SourceLabel = "Craigslist",
        ItemId = "7712345678",
        Title = "Dyson V8 Cordless Vacuum",
        Price = price,
    };

    private static ResalePricing Resale(decimal price, int comps = 12, decimal shipping = 0m) => new()
    {
        LookupTitle = "Dyson V8 Cordless Vacuum",
        Median = price,
        ExpectedSale = price,
        QuickSale = price,
        SoldCompCount = comps,
        AvgCompShipping = shipping,
        ConfidenceScore = 85,
        ConfidenceLevel = "High",
        EstimatedDaysToSell = 14,
        EstimatedMonthlySales = 8m,
    };

    // ── The premium and the tax ──────────────────────────────────────────────

    [Fact]
    public void The_buyers_premium_and_the_tax_are_both_in_the_cost_basis()
    {
        // $100 bid, 15% premium, 8% tax on the hammer + the premium = $124.20. An auction house
        // bills tax that way, and LotAnalyzer.CostOf has always known it.
        var row = Analyzer().Build(Lot(100m, Auction()), Resale(300m), Fees, retailSalesTaxPercent: 8m);

        Assert.Equal(15m, row.Liquidation!.BuyerPremium);
        Assert.Equal(9.20m, row.Liquidation.SalesTax);
        Assert.Equal(124.20m, row.BuyCostAllIn);
        Assert.Equal(100m, row.LocalAsk);
    }

    [Fact]
    public void Roi_is_measured_against_the_all_in_cost_not_against_the_bare_bid()
    {
        var row = Analyzer().Build(Lot(100m, Auction()), Resale(300m), Fees, 8m);

        // Against the bid alone this would read materially better than it is. The seller pays
        // $124.20, so that is what the return is measured on.
        var expected = Math.Round(row.NetProfit!.Value / row.BuyCostAllIn!.Value * 100m, 1);
        Assert.Equal(expected, row.RoiPercent);
    }

    [Fact]
    public void An_identical_item_costs_more_at_auction_than_off_a_stranger()
    {
        var analyzer = Analyzer();
        var resale = Resale(300m);

        var auction = analyzer.Build(Lot(100m, Auction()), resale, Fees, 8m);
        var craigslist = analyzer.Build(LocalDeal(100m), resale, Fees, 8m);

        // Same item, same ask, same comps — and the auction is worse by exactly the premium and the
        // tax. A board that showed them as equal would be lying about $24.20.
        Assert.True(auction.NetProfit < craigslist.NetProfit);
        Assert.Equal(24.20m, Math.Round(craigslist.NetProfit!.Value - auction.NetProfit!.Value, 2));
    }

    [Fact]
    public void The_premium_and_the_tax_leave_a_private_party_row_completely_alone()
    {
        var row = Analyzer().Build(LocalDeal(100m), Resale(300m), Fees, 8m);

        Assert.Null(row.Liquidation);
        Assert.Null(row.SalesTax);
        Assert.Null(row.BuyCostAllIn);
    }

    // ── The max bid ──────────────────────────────────────────────────────────

    [Fact]
    public void Max_to_pay_on_an_auction_row_is_a_bid_with_the_premium_and_tax_taken_out_of_it()
    {
        var row = Analyzer().Build(Lot(100m, Auction()), Resale(300m), Fees, 8m);

        // Bidding the untaxed, un-premiumed break-even would lose money on every dollar of it: each
        // extra dollar bid costs 1.15 x 1.08 = $1.242 in the end.
        var maxBid = row.MaxBuyPrice!.Value;
        Assert.True(maxBid > 100m);

        var costAtMax = LotAnalyzer.CostOf(maxBid, 15m, 8m, 0m).TotalCost;
        var recoveryAtMax = row.NetProfit!.Value + row.BuyCostAllIn!.Value;
        Assert.True(Math.Abs(costAtMax - recoveryAtMax) < 0.05m,
            $"break-even bid {maxBid} should cost {recoveryAtMax} all-in but costs {costAtMax}");
    }

    [Fact]
    public void The_target_roi_bid_is_below_the_break_even_bid()
    {
        var row = Analyzer().Build(Lot(100m, Auction()), Resale(300m), Fees, 8m);

        var target = row.Liquidation!.MaxBidForTargetRoi!.Value;
        Assert.True(target > 0m);
        Assert.True(target < row.MaxBuyPrice!.Value);
        Assert.Equal(LiquidationLotPricer.TargetRoiPercent, row.Liquidation.TargetRoiPercent);
    }

    [Fact]
    public void The_verdict_says_where_to_stop_because_the_bid_has_not_finished_moving()
    {
        var row = Analyzer().Build(Lot(20m, Auction()), Resale(300m), Fees, 8m);

        // A profit quoted against a price that is still climbing is only honest with the ceiling
        // attached to it.
        Assert.Contains("bid up to", row.VerdictNote);
    }

    [Fact]
    public void A_bid_already_past_the_target_is_told_to_let_it_go_not_to_bid_more()
    {
        // Seen live: a lot standing at $37 whose target-ROI bid was $29.60 was being told to
        // "bid up to $29.60" — an instruction to bid, when the honest answer is to stop.
        var row = Analyzer().Build(Lot(37m, Auction()), Resale(60m), Fees, 8m);

        Assert.True(row.Liquidation!.MaxBidForTargetRoi < 37m);
        Assert.Contains("already past", row.VerdictNote);
        Assert.DoesNotContain("bid up to", row.VerdictNote);
    }

    [Fact]
    public void An_auction_row_gets_no_negotiation_plan_because_an_auctioneer_takes_bids_not_offers()
    {
        var row = Analyzer().Build(Lot(20m, Auction()), Resale(300m), Fees, 8m);

        Assert.Null(row.Negotiation);
    }

    // ── Lots ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_lot_of_eight_is_worth_more_than_one_of_the_same_item()
    {
        var analyzer = Analyzer();
        var resale = Resale(60m);

        var single = analyzer.Build(Lot(50m, Auction()), resale, Fees, 8m);
        var eight = analyzer.Build(Lot(50m, Auction(isLot: true, units: 8, gradeId: "shelf_pull")), resale, Fees, 8m);

        Assert.True(eight.NetProfit > single.NetProfit);
        Assert.Equal(8, eight.Liquidation!.Units);
    }

    [Fact]
    public void A_lots_units_are_discounted_by_its_grades_recovery_rate()
    {
        var row = Analyzer().Build(
            Lot(50m, Auction(isLot: true, units: 10, gradeId: "uninspected_returns")), Resale(60m), Fees, 8m);

        var grade = LotAnalyzer.GradeFor("uninspected_returns");
        // Ten units at a 65% sellable rate is 6.5 sales, not ten. Fractional on purpose: rounding
        // down throws away value that is really there and rounding up invents stock that isn't.
        Assert.Equal(Math.Round(10 * grade.SellableRatePercent / 100m, 2), row.Liquidation!.SellableUnits);
        Assert.Equal(Math.Round(60m * grade.PriceFactorPercent / 100m, 2), row.Liquidation.UnitResale);
    }

    [Fact]
    public void A_single_item_is_not_graded_at_all()
    {
        // Applying a returns-pallet haircut to one auction item and not to the identical Craigslist
        // one would make two rows in the same ranking incomparable.
        var row = Analyzer().Build(Lot(50m, Auction()), Resale(60m), Fees, 8m);

        Assert.False(row.Liquidation!.IsLot);
        Assert.Equal("", row.Liquidation.GradeId);
        Assert.Equal(1m, row.Liquidation.SellableUnits);
        Assert.Equal(60m, row.Liquidation.UnitResale);
    }

    [Fact]
    public void A_lot_reports_what_one_sellable_unit_actually_cost()
    {
        var row = Analyzer().Build(
            Lot(240m, Auction(isLot: true, units: 40, gradeId: "shelf_pull")), Resale(30m), Fees, 8m);

        // "$240 for a pallet" means nothing until you know it is $6.83 an item.
        var expected = Math.Round(row.BuyCostAllIn!.Value / row.Liquidation!.SellableUnits, 2);
        Assert.Equal(expected, row.Liquidation.CostPerSellableUnit);
    }

    [Fact]
    public void A_lot_needs_more_sold_history_than_a_single_item_before_it_can_be_a_goldmine()
    {
        var analyzer = Analyzer();
        // Exactly the board's ordinary goldmine bar: enough for one item, not for twelve of it.
        var evidence = Resale(600m, comps: LocalArbitrageAnalyzer.GoldmineMinComps);

        var single = analyzer.Build(Lot(20m, Auction()), evidence, Fees, 8m);
        var lot = analyzer.Build(Lot(20m, Auction(isLot: true, units: 12, gradeId: "shelf_pull")), evidence, Fees, 8m);

        // A lot multiplies one comp by its unit count, so whatever that comp gets wrong is
        // multiplied too — and the bar rises with the count.
        Assert.Equal("goldmine", single.Verdict);
        Assert.Equal("thin", lot.Verdict);
        Assert.Contains("multiplies", lot.VerdictNote);
    }

    [Fact]
    public void A_lots_evidence_bar_rises_with_its_unit_count_and_then_stops()
    {
        // Don't claim to know the market for N units from fewer than N observed sales — floored at
        // the board's ordinary bar so a lot of two isn't held to a lower one, and capped where the
        // demand would stop being meetable for any real product.
        Assert.Equal(LocalArbitrageAnalyzer.GoldmineMinComps, LiquidationLotPricer.RequiredCompsForLot(2));
        Assert.Equal(12, LiquidationLotPricer.RequiredCompsForLot(12));
        Assert.Equal(LiquidationLotPricer.MaxCompsRequiredForLot, LiquidationLotPricer.RequiredCompsForLot(400));

        // And it is genuinely stricter than the single-item bar, or it would be no bar at all.
        Assert.True(LiquidationLotPricer.RequiredCompsForLot(40) > LocalArbitrageAnalyzer.GoldmineMinComps);
    }

    [Fact]
    public void A_well_evidenced_lot_still_gets_the_green_badge()
    {
        // The gate has to be a gate, not a blanket refusal to ever call a lot good.
        var row = Analyzer().Build(
            Lot(20m, Auction(isLot: true, units: 6, gradeId: "shelf_pull")), Resale(300m, comps: 20), Fees, 8m);

        Assert.Equal("goldmine", row.Verdict);
    }

    // ── The refusals ─────────────────────────────────────────────────────────

    [Fact]
    public void An_unpriceable_lot_reports_the_reason_instead_of_a_number()
    {
        var row = Analyzer().Build(
            Lot(50m, Auction(isLot: true, units: 1, unpriceable: "The contents are described as assorted, so there is no single product to price the units against.")),
            Resale(300m), Fees, 8m);

        Assert.Equal("no_data", row.Verdict);
        Assert.Contains("assorted", row.VerdictNote);
        Assert.Null(row.NetProfit);
    }

    [Fact]
    public void An_unpriceable_row_still_says_what_the_bid_would_cost()
    {
        // "This bid costs you $124.20 all in" is true and useful even when the resale side has no
        // answer, and it is the half of the question the app can always answer.
        var row = Analyzer().Build(
            Lot(100m, Auction(unpriceable: "Sold for parts.")), Resale(300m), Fees, 8m);

        Assert.Equal(124.20m, row.BuyCostAllIn);
        Assert.Null(row.NetProfit);
    }

    [Fact]
    public void A_comp_that_fails_the_manifests_retail_cross_check_is_refused()
    {
        // The listing claims $60 a unit and the comps say $600 — that is a mismatched product, not
        // a bargain, and on a lot the error would be multiplied by the unit count first.
        var quote = Pricer().Price(
            Auction(isLot: true, units: 10, gradeId: "shelf_pull", claimedRetail: 600m),
            bid: 100m, Resale(600m), Fees, 8m);

        Assert.NotNull(quote.Economics.UnpriceableReason);
        Assert.Equal(0m, quote.NetRecovery);
    }

    [Fact]
    public void A_credible_retail_claim_does_not_block_pricing_and_is_reported_against_the_resale()
    {
        var quote = Pricer().Price(
            Auction(isLot: true, units: 10, gradeId: "shelf_pull", claimedRetail: 900m),
            bid: 100m, Resale(60m), Fees, 8m);

        Assert.Null(quote.Economics.UnpriceableReason);
        // The "$900 retail value!" test: what it really resells for, as a share of the claim.
        Assert.NotNull(quote.Economics.ResalePercentOfRetail);
        Assert.True(quote.Economics.ResalePercentOfRetail < 100m);
    }

    [Fact]
    public void No_sold_history_is_no_verdict_rather_than_a_zero()
    {
        var row = Analyzer().Build(Lot(50m, Auction()), resale: null, Fees, 8m);

        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.NetProfit);
        // The cost side is still answerable, and still answered.
        Assert.True(row.BuyCostAllIn > 50m);
    }

    // ── The clock ────────────────────────────────────────────────────────────

    [Fact]
    public void An_auction_closing_inside_two_days_counts_as_closing_soon()
    {
        var now = new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(LiquidationLotPricer.ClosingSoon(new LiquidationLotEconomics { ClosesUtc = now.AddHours(6) }, now));
        Assert.False(LiquidationLotPricer.ClosingSoon(new LiquidationLotEconomics { ClosesUtc = now.AddDays(9) }, now));
        // Already gone is not "closing soon" — it is a deal the seller never had.
        Assert.False(LiquidationLotPricer.ClosingSoon(new LiquidationLotEconomics { ClosesUtc = now.AddHours(-1) }, now));
        Assert.False(LiquidationLotPricer.ClosingSoon(new LiquidationLotEconomics(), now));
    }

    // ── The source, in the registry ──────────────────────────────────────────

    [Fact]
    public void The_liquidation_source_is_local_and_taxed_and_offers_the_sites_it_cannot_read()
    {
        ILocalSupplySource source = new LiquidationSourceService(new StubHttpClientFactory(), new ActionLog());

        Assert.Equal(LiquidationCatalog.SourceId, source.Id);
        Assert.False(source.RequiresConnection);
        // Unlike the deal feeds, an auction really does happen somewhere — but not within 40 miles.
        Assert.True(source.IsLocationBased);
        Assert.Equal(LiquidationCatalog.MinRadiusMiles, source.MinRadiusMiles);
        // And unlike a cash pickup, the register applies.
        Assert.True(source.ChargesSalesTax);
        Assert.NotEmpty(source.ManualSites);
    }

    [Fact]
    public void Every_other_source_is_untouched_by_the_two_new_interface_members()
    {
        // All of them are default interface members precisely so no existing source had to be
        // edited — which is also why they are only reachable through the interface.
        ILocalSupplySource craigslist = new CraigslistService(new StubHttpClientFactory(), new ActionLog());

        Assert.True(craigslist.IsLocationBased);
        Assert.False(craigslist.ChargesSalesTax);
        Assert.Empty(craigslist.ManualSites);
        Assert.Equal(0, craigslist.MinRadiusMiles);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
