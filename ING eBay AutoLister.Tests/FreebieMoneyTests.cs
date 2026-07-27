using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What a free thing actually costs, and what the board is allowed to claim about it.
///
/// The failure this guards against is specific: a $0 cost basis makes ROI unbounded, so without
/// these rules every free row on the board clears the goldmine bar on return alone and the badge
/// stops meaning anything. Everything here either adds a cost the word "free" hides, or holds a
/// verdict below what the arithmetic alone would have given it.
/// </summary>
public class FreebieMoneyTests
{
    private static FreebieDetails Free() => new()
    {
        Kind = FreebieKinds.Free,
        IsPickup = true,
        DeliveryCostKnown = true,
        // What a local free post always is: no clock, but no queue either.
        Urgency = FreebieUrgency.FirstCome,
    };

    private static FreebieDetails Rebate(decimal price, decimal rebate, string via = "") => new()
    {
        Kind = FreebieKinds.FreeAfterRebate,
        ListPrice = price,
        RebateAmount = rebate,
        RebateVia = via,
        DeliveryCostKnown = true,
    };

    // ── A curb pickup really is free ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_local_pickup_costs_nothing_and_is_never_taxed()
    {
        // Cash between two people. Whatever the seller's sales-tax rate says, no till was involved.
        var economics = FreebiePricer.Cost(Free(), salesTaxPercent: 8.25m);

        Assert.Equal(0m, economics.NetCost);
        Assert.Equal(0m, economics.SalesTax);
        Assert.Equal(0m, economics.OutOfPocketNow);
    }

    // ── A rebate is not free ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sales_tax_on_a_rebate_deal_is_never_refunded()
    {
        // The register charges tax on $49.99; the rebate cheque only ever covers the price. That
        // $3.75 is gone for good, and a board showing this row at $0 would be describing a
        // different deal from the one on offer.
        var economics = FreebiePricer.Cost(Rebate(49.99m, 49.99m), salesTaxPercent: 7.5m);

        Assert.Equal(3.75m, economics.SalesTax);
        Assert.Equal(53.74m, economics.OutOfPocketNow);
        Assert.Equal(49.99m, economics.RefundExpected);
        Assert.Contains("never refunded", economics.CostNote);
    }

    [Fact]
    public void Part_of_the_rebate_is_held_back_rather_than_banked()
    {
        var economics = FreebiePricer.Cost(Rebate(40m, 40m), salesTaxPercent: 0m);

        // A reserve, not a forecast: the row only earns its verdict if it still works when a claim
        // goes unpaid. 15% of $40.
        Assert.Equal(6m, economics.RebateReserve);
        Assert.Equal(6m, economics.NetCost);
        Assert.Contains("in case the claim isn't paid", economics.CostNote);
    }

    [Fact]
    public void The_cost_basis_is_the_tax_plus_the_reserve_plus_whatever_the_rebate_missed()
    {
        // $60 item, $50 rebate, 10% tax: $6 tax + $10 uncovered + $7.50 reserve = $23.50.
        var economics = FreebiePricer.Cost(Rebate(60m, 50m), salesTaxPercent: 10m);

        Assert.Equal(66m, economics.OutOfPocketNow);
        Assert.Equal(23.50m, economics.NetCost);
        Assert.Contains("isn't covered either", economics.CostNote);
    }

    [Fact]
    public void A_mail_in_rebate_ties_the_money_up_far_longer_than_an_app_paid_one()
    {
        Assert.Equal(FreebiePricer.MailInRebateWaitDays, FreebiePricer.Cost(Rebate(20m, 20m), 0m).RefundWaitDays);
        Assert.Equal(FreebiePricer.AppRebateWaitDays, FreebiePricer.Cost(Rebate(20m, 20m, "Venmo"), 0m).RefundWaitDays);
    }

    [Fact]
    public void The_rebate_wait_lands_in_the_days_to_cash_figure()
    {
        // Sells in 10 days either way; the rebate flip does not get its money back at the same
        // speed, and a board that ignored that would rank the two as equals.
        var cash = DaysToCashEstimator.Estimate(10, 3m, netProfit: 30m);
        var waiting = DaysToCashEstimator.Estimate(10, 3m, netProfit: 30m, extraPipelineDays: FreebiePricer.MailInRebateWaitDays);

        Assert.Equal(cash.DaysToCash + FreebiePricer.MailInRebateWaitDays, waiting.DaysToCash);
        Assert.True(waiting.ProfitPerDay < cash.ProfitPerDay);
    }

    [Fact]
    public void An_unchanged_call_still_gets_the_old_pipeline()
    {
        // Every existing caller passes no extra days and must be unaffected.
        Assert.Equal(DaysToCashEstimator.PipelineDays, DaysToCashEstimator.Estimate(10, 3m).PipelineDays);
    }

    // ── Free after coupon ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_coupon_that_takes_the_till_price_to_zero_is_taxed_at_zero()
    {
        var economics = FreebiePricer.Cost(
            new FreebieDetails { Kind = FreebieKinds.FreeAfterCoupon, RequiresCoupon = true, DeliveryCostKnown = true },
            salesTaxPercent: 8m);

        Assert.Equal(0m, economics.NetCost);
        Assert.Equal(0m, economics.SalesTax);
        // The risk here isn't a number, it's a dead code — so it is said rather than priced.
        Assert.Contains("only with the code", economics.CostNote);
    }

    // ── Near free is taxed like the retail buy it is ─────────────────────────────────────────────

    [Fact]
    public void A_near_free_retail_item_pays_tax_and_a_near_free_pickup_does_not()
    {
        var shop = FreebiePricer.Cost(
            new FreebieDetails { Kind = FreebieKinds.NearFree, ListPrice = 4m, DeliveryCostKnown = true }, 7.5m);
        var kerb = FreebiePricer.Cost(
            new FreebieDetails { Kind = FreebieKinds.NearFree, ListPrice = 4m, IsPickup = true, DeliveryCostKnown = true }, 7.5m);

        Assert.Equal(4.30m, shop.NetCost);
        Assert.Equal(4m, kerb.NetCost);
    }

    // ── What the board is allowed to claim ───────────────────────────────────────────────────────

    [Fact]
    public void A_goldmine_verdict_is_held_back_when_a_real_cost_could_not_be_seen()
    {
        var economics = FreebiePricer.Cost(
            new FreebieDetails { Kind = FreebieKinds.Free, DeliveryCostKnown = false }, 0m);

        var (verdict, note) = LocalArbitrageAnalyzer.JudgeFreebie(
            "goldmine", "$120 net on a $0 buy, backed by 9 sold comps.", economics, netProfit: 120m);

        Assert.Equal("solid", verdict);
        Assert.Contains("shipping IS the price", note);
    }

    [Fact]
    public void A_goldmine_survives_when_nothing_was_hidden()
    {
        var economics = FreebiePricer.Cost(Free(), 0m);

        var (verdict, _) = LocalArbitrageAnalyzer.JudgeFreebie(
            "goldmine", "$120 net on a $0 buy, backed by 9 sold comps.", economics, netProfit: 120m);

        Assert.Equal("goldmine", verdict);
    }

    [Fact]
    public void Free_does_not_make_a_six_dollar_flip_worth_the_trip()
    {
        var economics = FreebiePricer.Cost(Free(), 0m);

        var (verdict, note) = LocalArbitrageAnalyzer.JudgeFreebie(
            "solid", "$6 net after fees.", economics, netProfit: 6m);

        Assert.Equal("thin", verdict);
        Assert.Contains("not worth the trip", note);
    }

    [Fact]
    public void An_unbounded_roi_never_reaches_a_sentence_as_a_number()
    {
        // Live, before this was fixed: "$736.46 net after fees (79228162514264337593543950335% ROI)".
        // That is decimal.MaxValue standing in for "no cost basis" and leaking out of the comparison
        // it exists for — and it reads as a bug in every figure printed beside it.
        var goldmine = LocalArbitrageAnalyzer.Judge(
            netProfit: 736.46m, roiPercent: null, localAsk: 0m, compCount: 13, confidenceScore: 80);
        Assert.DoesNotContain("7922816251426433759354395033", goldmine.Note);
        Assert.Contains("for nothing spent", goldmine.Note);

        // The branch it actually leaked from: "solid" is the only one that quotes a percentage.
        var solid = LocalArbitrageAnalyzer.Judge(
            netProfit: 40m, roiPercent: null, localAsk: 0m, compCount: 13, confidenceScore: 80);
        Assert.DoesNotContain("7922816251426433759354395033", solid.Note);
        Assert.Contains("cost nothing to buy", solid.Note);
    }

    [Fact]
    public void A_free_item_that_still_loses_money_says_so_without_quoting_a_zero_ask()
    {
        var (verdict, note) = LocalArbitrageAnalyzer.Judge(
            netProfit: -3m, roiPercent: null, localAsk: 0m, compCount: 8, confidenceScore: 70);

        Assert.Equal("pass", verdict);
        Assert.Contains("Even at nothing", note);
        Assert.DoesNotContain("$0 ask", note);
    }

    [Fact]
    public void A_priced_row_still_quotes_its_real_roi()
    {
        var (_, note) = LocalArbitrageAnalyzer.Judge(
            netProfit: 40m, roiPercent: 66m, localAsk: 60m, compCount: 8, confidenceScore: 70);

        Assert.Contains("66% ROI", note);
    }

    [Fact]
    public void A_row_that_loses_money_stays_a_pass_and_still_gets_its_deadline()
    {
        var economics = FreebiePricer.Cost(Free(), 0m);

        var (verdict, note) = LocalArbitrageAnalyzer.JudgeFreebie(
            "pass", "Sells for less than the $0 ask once fees are paid.", economics, netProfit: -4m);

        Assert.Equal("pass", verdict);
        Assert.Contains("First come", note);
    }

    [Fact]
    public void Every_free_row_carries_a_deadline_or_says_it_has_none()
    {
        Assert.Contains("claimed the same day", FreebiePricer.UrgencyNote(
            new FreebieDetails { Kind = FreebieKinds.Free, Urgency = FreebieUrgency.FirstCome }));

        Assert.Contains("Gone today", FreebiePricer.UrgencyNote(
            new FreebieDetails { Kind = FreebieKinds.Free, Urgency = FreebieUrgency.Today, ExpiresText = "today only" }));

        Assert.Contains("pulled without notice", FreebiePricer.UrgencyNote(
            new FreebieDetails { Kind = FreebieKinds.Free, Urgency = FreebieUrgency.Unknown }));
    }

    // ── Through the analyzer, end to end ─────────────────────────────────────────────────────────

    private static LocalArbitrageAnalyzer Analyzer() =>
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static ResalePricing Resale(decimal price, int comps = 9) => new()
    {
        LookupTitle = "Sony WH-1000XM4",
        Median = price,
        ExpectedSale = price,
        QuickSale = price * 0.9m,
        SoldCompCount = comps,
        ConfidenceScore = 80,
        ConfidenceLevel = "High",
    };

    private static LocalSupplyListing Listing(FreebieDetails details, decimal price = 0m) => new()
    {
        Source = FreebieCatalog.SourceId,
        SourceLabel = "Free — local pickup",
        ItemId = "1",
        Title = "Sony WH-1000XM4",
        Price = price,
        IsFree = price == 0m,
        Freebie = details,
    };

    [Fact]
    public void A_free_row_is_priced_off_zero_and_keeps_all_of_the_resale()
    {
        var row = Analyzer().Build(Listing(Free()), Resale(200m), new FeeProfile());

        Assert.NotNull(row.Freebie);
        Assert.Null(row.BuyCostAllIn);          // nothing left the wallet, so there is nothing to state
        Assert.True(row.NetProfit > 0);
        Assert.Equal("goldmine", row.Verdict);
        // Nobody to haggle with at zero, so no plan is drafted and none of this counts as buy-side
        // upside on the board.
        Assert.Null(row.Negotiation);
    }

    [Fact]
    public void A_rebate_row_is_priced_off_what_it_really_costs_not_off_zero()
    {
        var details = Rebate(49.99m, 49.99m);
        var row = Analyzer().Build(Listing(details), Resale(200m), new FeeProfile(), retailSalesTaxPercent: 7.5m);

        // $3.75 unrefundable tax + $7.50 held in reserve.
        Assert.Equal(11.25m, row.BuyCostAllIn);
        Assert.Equal(3.75m, row.SalesTax);

        // And it is genuinely worse than the same item found on a kerb — by exactly that much.
        var kerbside = Analyzer().Build(Listing(Free()), Resale(200m), new FeeProfile());
        Assert.Equal(11.25m, Math.Round(kerbside.NetProfit!.Value - row.NetProfit!.Value, 2));
    }

    [Fact]
    public void Nothing_changes_for_a_row_that_is_not_a_freebie()
    {
        // The whole feature has to be invisible to the boards that existed before it.
        var ordinary = new LocalSupplyListing { Source = "craigslist", ItemId = "9", Title = "Sony WH-1000XM4", Price = 60m };
        var row = Analyzer().Build(ordinary, Resale(200m), new FeeProfile(), retailSalesTaxPercent: 7.5m);

        Assert.Null(row.Freebie);
        Assert.Null(row.SalesTax);
        Assert.Null(row.BuyCostAllIn);
        Assert.NotNull(row.Negotiation);
    }
}
