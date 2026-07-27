using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What remaining cover is worth, and — mostly — what it is not allowed to be worth.
///
/// The uplift priced here is the only place in the app where a listing's own prose can raise a
/// resale estimate above what the sold comps produced. So these tests are weighted the way the code
/// is: one case where the premium is paid, and a wall of cases where it is refused and the row's
/// money comes back exactly as it would have without this feature.
/// </summary>
public class WarrantyMoneyTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40

    private static WarrantyDetails Covered(
        int? months = 24, string kind = WarrantyKinds.Manufacturer,
        string evidence = WarrantyEvidence.Stated, bool transfers = true) => new()
        {
            Kind = kind, Evidence = evidence, MonthsRemaining = months,
            TransfersToBuyer = transfers, KindLabel = "Manufacturer warranty",
        };

    private static LocalSupplyListing Listing(decimal price, string title, string? body = null) => new()
    {
        Source = "craigslist", SourceLabel = "Craigslist", ItemId = "1", Title = title,
        Url = "https://lasvegas.craigslist.org/1.html", Price = price, DetailText = body ?? "",
        Location = "Las Vegas, NV",
    };

    private static ResalePricing Pricing(decimal expected = 200m, int soldComps = 8, int confidence = 70) => new()
    {
        LookupTitle = "Bitmain Antminer S19j Pro 104TH",
        Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
        SoldCompCount = soldComps, ConfidenceScore = confidence, ConfidenceLevel = "Good",
    };

    // ── The one case where the premium is paid ───────────────────────────────────────────────────

    [Fact]
    public void Stated_transferable_cover_on_believable_comps_is_worth_a_capped_premium()
    {
        var economics = WarrantyPricer.Value(
            Covered(), expectedSale: 200m, buyCostAllIn: 50m, compCount: 8, confidenceScore: 70);

        Assert.Null(economics.HeldBackReason);
        Assert.Equal(20m, economics.ResaleUplift);       // 10% of $200
        Assert.Equal(220m, economics.ResaleWithWarranty);
        Assert.Equal(200m, economics.ResaleWithoutWarranty);
        // And the reseller's own $50 stops being at risk, which is the half of this that isn't price.
        Assert.True(economics.CoversYourBuy);
        Assert.Equal(50m, economics.ProtectedCost);
    }

    [Fact]
    public void The_premium_scales_with_time_left_and_stops_scaling_early()
    {
        // A step function, because that is how it works in a buyer's head: "still covered" is worth
        // a lot more than "isn't", and two years left is worth barely more than one.
        Assert.Equal(10m, WarrantyPricer.UpliftPercentFor(36));
        Assert.Equal(10m, WarrantyPricer.UpliftPercentFor(12));
        Assert.Equal(7m, WarrantyPricer.UpliftPercentFor(6));
        Assert.Equal(4m, WarrantyPricer.UpliftPercentFor(3));
        Assert.Equal(2m, WarrantyPricer.UpliftPercentFor(1));
        Assert.Equal(0m, WarrantyPricer.UpliftPercentFor(0));
    }

    [Fact]
    public void The_dollar_cap_catches_what_the_percentage_cap_lets_through()
    {
        // 10% of a $2,400 miner is $240 of unverified prose sitting inside a profit ranking. A
        // warranty is worth a real but bounded amount, and the ceiling is where that is enforced.
        var economics = WarrantyPricer.Value(
            Covered(), expectedSale: 2400m, buyCostAllIn: 1200m, compCount: 12, confidenceScore: 80);

        Assert.Equal(WarrantySelectors.MaxUpliftDollars, economics.ResaleUplift);
        Assert.Equal(2475m, economics.ResaleWithWarranty);
    }

    // ── The fences ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void An_estimated_warranty_is_never_worth_a_cent()
    {
        // However plausible. "Bought three months ago and Dyson runs two years" is worth telling the
        // seller and is not a warranty anybody stated.
        var economics = WarrantyPricer.Value(
            Covered(evidence: WarrantyEvidence.Estimated), 200m, 50m, compCount: 20, confidenceScore: 90);

        Assert.Equal(0m, economics.ResaleUplift);
        Assert.Equal(200m, economics.ResaleWithWarranty);
        Assert.NotNull(economics.HeldBackReason);
        // Still protects the buy — that part was never in doubt.
        Assert.True(economics.CoversYourBuy);
    }

    [Fact]
    public void Cover_that_does_not_transfer_protects_the_buy_and_moves_no_price()
    {
        var economics = WarrantyPricer.Value(
            Covered(kind: WarrantyKinds.Seller, transfers: false), 200m, 50m, compCount: 20, confidenceScore: 90);

        Assert.Equal(0m, economics.ResaleUplift);
        Assert.True(economics.CoversYourBuy);
        Assert.Contains("covers YOU", economics.HeldBackReason);
    }

    [Fact]
    public void A_premium_is_never_stacked_on_top_of_a_price_nobody_believes()
    {
        // Two guesses stacked on each other. Held to the same bar the goldmine badge is.
        Assert.NotNull(WarrantyPricer.Value(Covered(), 200m, 50m, compCount: 2, confidenceScore: 90).HeldBackReason);
        Assert.NotNull(WarrantyPricer.Value(Covered(), 200m, 50m, compCount: 9, confidenceScore: 40).HeldBackReason);
    }

    [Fact]
    public void A_term_with_no_start_date_earns_nothing_and_says_what_to_go_and_ask()
    {
        var economics = WarrantyPricer.Value(Covered(months: null), 200m, 50m, 9, 80);

        Assert.Equal(0m, economics.ResaleUplift);
        Assert.Contains("receipt", economics.HeldBackReason);
    }

    [Fact]
    public void Expired_and_nearly_expired_cover_earn_nothing()
    {
        Assert.Equal(0m, WarrantyPricer.Value(Covered(months: 0), 200m, 50m, 9, 80).ResaleUplift);
        // A fortnight left is a fact about the item, not something a buyer pays for.
        Assert.Equal(0m, WarrantyPricer.Value(Covered(months: 0), 200m, 50m, 9, 80).ResaleUplift);
    }

    // ── The other direction: a listing that says there is no cover ───────────────────────────────

    [Fact]
    public void As_is_with_no_returns_warns_only_where_the_money_makes_it_matter()
    {
        var expensive = Covered(months: 0, kind: WarrantyKinds.None, transfers: false);

        Assert.NotNull(WarrantyPricer.RiskNote(expensive, buyCostAllIn: 600m));
        // A warning that fires on every $30 row is a warning nobody reads by the time it counts.
        Assert.Null(WarrantyPricer.RiskNote(expensive, buyCostAllIn: 40m));
    }

    [Fact]
    public void A_goldmine_the_seller_cannot_return_is_held_down_to_solid()
    {
        var economics = WarrantyPricer.Value(
            Covered(months: 0, kind: WarrantyKinds.None, transfers: false), 1000m, 600m, 9, 80);

        var (verdict, note) = WarrantyPricer.JudgeWarranty("goldmine", "$400 net.", economics);

        // The money isn't in dispute; the exposure is. A green badge is an instruction to go and buy
        // it, and this is the row where that instruction needs a hand on the arm.
        Assert.Equal("solid", verdict);
        Assert.Contains("Test it before", note);
    }

    [Fact]
    public void A_row_already_telling_the_seller_to_walk_is_left_alone()
    {
        var economics = WarrantyPricer.Value(
            Covered(months: 0, kind: WarrantyKinds.None, transfers: false), 1000m, 600m, 9, 80);

        Assert.Equal("pass", WarrantyPricer.JudgeWarranty("pass", "Loses money.", economics).Verdict);
        Assert.Equal("no_data", WarrantyPricer.JudgeWarranty("no_data", "No history.", economics).Verdict);
    }

    // ── End to end, through the board's own arithmetic ───────────────────────────────────────────

    [Fact]
    public void The_board_prices_a_covered_flip_higher_and_shows_the_price_it_used()
    {
        var covered = Analyzer.Build(
            Listing(50m, "Bitmain Antminer S19j Pro — 3 year manufacturer warranty, bought last month"),
            Pricing(expected: 200m), Fees);

        var bare = Analyzer.Build(
            Listing(50m, "Bitmain Antminer S19j Pro"), Pricing(expected: 200m), Fees);

        // $20 of premium (10% of $200), and the resale column moves with it: a profit computed
        // against a price the row doesn't show is a row that doesn't add up.
        Assert.Equal(20m, covered.Warranty?.ResaleUplift);
        Assert.Equal(220m, covered.EbayExpectedSale);
        Assert.Equal(140.45m, covered.NetProfit);   // 220 - 29.55 fees - 50 buy
        Assert.Equal(123.10m, bare.NetProfit);      // 200 - 26.90 fees - 50 buy
        // And it raises what the item is worth paying, which is the number the seller negotiates on.
        Assert.Equal(190.45m, covered.MaxBuyPrice);
        Assert.Null(bare.Warranty);
    }

    [Fact]
    public void An_expensive_as_is_buy_keeps_its_profit_and_loses_its_green_badge()
    {
        var row = Analyzer.Build(
            Listing(400m, "Bitmain Antminer S19j Pro — sold as-is, no returns"),
            Pricing(expected: 1000m), Fees);

        // $1000 - $132.90 fees - $400 = $467.10 at 116% ROI on eight comps: a goldmine by arithmetic.
        Assert.Equal(467.10m, row.NetProfit);
        Assert.Equal("solid", row.Verdict);
        Assert.NotNull(row.Warranty?.RiskNote);
    }

    [Fact]
    public void An_estimated_warranty_changes_no_number_on_the_board()
    {
        var estimated = Analyzer.Build(
            Listing(50m, "Dyson V15 Detect", "Brand new in box, never opened, still sealed."),
            Pricing(expected: 200m), Fees);

        // Read, labelled and shown — and the money is identical to a row with no warranty at all.
        Assert.NotNull(estimated.Warranty);
        Assert.Equal(WarrantyEvidence.Estimated, estimated.Warranty!.Evidence);
        Assert.Equal(0m, estimated.Warranty.ResaleUplift);
        Assert.Equal(200m, estimated.EbayExpectedSale);
        Assert.Equal(123.10m, estimated.NetProfit);
    }

    [Fact]
    public void An_unpriceable_row_still_says_what_the_listing_claimed()
    {
        // No sold history, so no premium and no profit — and "this one is under factory warranty
        // until March" is the most useful thing a row like this can carry.
        var row = Analyzer.Build(
            Listing(50m, "Zephyrtech ZT-900 — still under manufacturer warranty until 3/2028"),
            resale: null, Fees);

        Assert.Equal("no_data", row.Verdict);
        Assert.NotNull(row.Warranty);
        Assert.Equal(0m, row.Warranty!.ResaleUplift);
        Assert.True(row.Warranty.CoversYourBuy);
    }
}
