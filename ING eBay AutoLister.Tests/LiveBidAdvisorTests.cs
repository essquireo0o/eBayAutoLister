using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The WhatsNot card hands somebody a number to shout at a live auction, with seconds to read it.
// What is pinned here is the honesty of that number: the ceiling never rounds in the bidder's
// favour, the platform's premium and the shipping come out of the BID rather than out of the
// margin afterwards, thin evidence is said rather than scored, and the card cannot disagree with
// the auction sniper about the same item at the same price.
public class LiveBidAdvisorTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly JackpotHunter Hunter = new(Profit);
    private static readonly LiveBidAdvisor Advisor = new(Profit, Hunter);
    private static readonly AuctionSniperAnalyzer Sniper = new(Profit, Hunter);
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    /// <summary>
    /// A market analysis in the shape <c>AnalyzeProductAsync</c> produces one, so the card is
    /// exercised through the same object the endpoint hands it — never a hand-built ResalePricing
    /// that could drift from what the pipeline actually returns.
    /// </summary>
    private static MarketAnalysisResult Analysis(
        decimal? expected = 200m, int comps = 8, int pricedComps = 8, int terapeakComps = 0,
        int confidence = 70, int activeComps = 10, decimal? sellThroughRate = 80m,
        bool identityVerified = true, decimal avgShipping = 0m,
        int newestCompDays = 9, int oldestCompDays = 60, bool datedComps = true,
        bool rateIsUnbounded = false, decimal? p25 = 170m, decimal? p75 = 240m)
    {
        var newest = Now.AddDays(-newestCompDays);
        var oldest = Now.AddDays(-oldestCompDays);

        return new MarketAnalysisResult
        {
            PriceEstimate = new PriceEstimate
            {
                MedianPrice = expected,
                ExpectedSalePrice = expected,
                QuickSalePrice = expected * 0.85m,
                Percentile25 = p25,
                Percentile75 = p75,
                MinimumRealisticPrice = p25 * 0.8m,
                MaximumRealisticPrice = p75 * 1.2m,
                LocalMedianPrice = expected,
                LocalExpectedSalePrice = expected,
                LocalWeight = 1m,
                PricedOnCompCount = pricedComps,
                IdentityVerified = identityVerified,
                LocalOldestSoldAtUtc = datedComps ? oldest : null,
                LocalNewestSoldAtUtc = datedComps ? newest : null,
            },
            SellThrough = new SellThroughAnalysis
            {
                SoldComparableCount = comps,
                ActiveComparableCount = activeComps,
                SellThroughRate = rateIsUnbounded ? null : sellThroughRate,
                RateIsUnbounded = rateIsUnbounded,
                SellThroughScore = 72,
                Interpretation = rateIsUnbounded ? "Unverified — no active comps to measure against" : "Very Strong",
                EstimatedMonthlySales = 4m,
                EstimatedDaysToSell = 14,
                LiquidityLevel = "Fast Mover",
            },
            Confidence = new ConfidenceBreakdown { Score = confidence, Level = "Good" },
            Sources = new SourceBreakdown
            {
                LocalComparableCount = comps,
                TerapeakComparableCount = terapeakComps,
                LocalWeightPercent = 100m,
                PricedOnCompCount = pricedComps,
                IdentityVerified = identityVerified,
            },
            TopSoldComparables =
            [
                new MarketplaceComparableResult
                {
                    ItemId = "c1", Title = Product, SoldPrice = 195m, Shipping = avgShipping,
                    TotalPrice = 195m + avgShipping, Condition = "Used",
                    SoldDate = datedComps ? newest : null, ItemUrl = "https://www.ebay.com/itm/c1",
                },
                new MarketplaceComparableResult
                {
                    ItemId = "c2", Title = Product, SoldPrice = 205m, Shipping = avgShipping,
                    TotalPrice = 205m + avgShipping, Condition = "Used",
                    SoldDate = datedComps ? oldest : null, ItemUrl = "https://www.ebay.com/itm/c2",
                },
            ],
        };
    }

    private static LiveBidRequest Ask(
        decimal? bid = null, decimal? shipping = null, decimal? fee = null, decimal? target = null,
        int? quantity = null) =>
        new()
        {
            Title = Product, CurrentBid = bid, ShippingCost = shipping, BuyerFeePercent = fee,
            TargetRoiPercent = target, Quantity = quantity,
        };

    private static LiveBidCard Card(
        MarketAnalysisResult? analysis, decimal? bid = null, decimal? shipping = null,
        decimal? fee = null, decimal? target = null, int? quantity = null) =>
        Advisor.Build(Product, analysis, Ask(bid, shipping, fee, target, quantity), Fees, nowUtc: Now);

    // ── The ceiling ───────────────────────────────────────────────────────────

    /// <summary>
    /// At the app's own bar with nothing on top of the bid, the live card's ceiling IS the auction
    /// sniper's ceiling — the same function, called with its defaults. Two ceilings for one item at
    /// one price would mean the app has two opinions and the bidder has none.
    /// </summary>
    [Fact]
    public void The_ceiling_at_the_defaults_is_the_snipers_own()
    {
        var breakEven = 173.10m;

        Assert.Equal(
            AuctionSniperAnalyzer.MaxBidFor(breakEven, shippingCost: 0m),
            AuctionSniperAnalyzer.MaxBidDetail(breakEven, 0m, LiveBidAdvisor.DefaultTargetRoiPercent, 0m).MaxBid);

        Assert.Equal(73.10m, AuctionSniperAnalyzer.MaxBidFor(breakEven, 0m));
    }

    /// <summary>
    /// The premium is charged on the winning bid, so it has to be divided back out of the ceiling.
    /// Take it off as though it were a flat cost and the quoted bid costs more than the arithmetic
    /// behind it allowed — which is the whole failure this feature exists to avoid.
    /// </summary>
    [Fact]
    public void A_buyers_premium_divides_out_of_the_bid_rather_than_off_the_margin()
    {
        var (maxBid, _) = AuctionSniperAnalyzer.MaxBidDetail(173.10m, shippingCost: 0m, targetRoiPercent: 30m, buyerFeePercent: 8m);

        Assert.Equal(67.68m, maxBid);

        // And winning at that bid still leaves the cash bar intact once the premium is paid.
        var landed = LiveBidAdvisor.LandedCost(maxBid, 8m, 0m);
        Assert.True(173.10m - landed >= LocalArbitrageAnalyzer.SolidProfit,
            $"landed {landed} leaves {173.10m - landed}, under the {LocalArbitrageAnalyzer.SolidProfit} bar");
    }

    /// <summary>
    /// Shipping is spent whatever the bid is, so it comes off the top; the premium is a multiple of
    /// the bid, so it divides. Applied in the other order the ceiling is wrong by the premium ON the
    /// shipping — small, and the kind of small that quietly eats a margin.
    /// </summary>
    [Fact]
    public void Shipping_comes_off_the_top_before_the_premium_divides_out()
    {
        var (maxBid, _) = AuctionSniperAnalyzer.MaxBidDetail(173.10m, shippingCost: 12m, targetRoiPercent: 30m, buyerFeePercent: 8m);

        Assert.Equal(56.57m, maxBid);
        Assert.Equal(73.10m, LiveBidAdvisor.LandedCost(maxBid, 8m, 12m));
    }

    [Fact]
    public void The_ceiling_is_truncated_to_the_cent_never_rounded_up()
    {
        // 1000 / 1.33 = 751.8796… — rounded that is 751.88, which is a cent more than the target
        // return allows. A ceiling rounded up gives away the margin it exists to protect.
        var (maxBid, boundBy) = AuctionSniperAnalyzer.MaxBidDetail(1_000m, 0m, targetRoiPercent: 33m);

        Assert.Equal(751.87m, maxBid);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, boundBy);
    }

    /// <summary>
    /// Which bar bound is the bidder's next move: raise the target and the ceiling drops, but on a
    /// cheap item the cash floor is what stopped them and no target change will move it.
    /// </summary>
    [Fact]
    public void The_card_says_which_bar_set_the_ceiling()
    {
        // $200 item: 30% of it is nowhere near $100, so the cash floor binds.
        var cheap = AuctionSniperAnalyzer.MaxBidDetail(173.10m, 0m);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByCash, cheap.BoundBy);

        // $2,000 item: 30% of the money is worth far more than $100, so the return binds.
        var dear = AuctionSniperAnalyzer.MaxBidDetail(1_731m, 0m);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, dear.BoundBy);
    }

    [Fact]
    public void A_higher_target_lowers_the_ceiling()
    {
        var atThirty = AuctionSniperAnalyzer.MaxBidDetail(1_731m, 0m, targetRoiPercent: 30m).MaxBid;
        var atHundred = AuctionSniperAnalyzer.MaxBidDetail(1_731m, 0m, targetRoiPercent: 100m).MaxBid;

        Assert.True(atHundred < atThirty, $"a 100% target ({atHundred}) must ask for less than a 30% one ({atThirty})");
        Assert.Equal(865.50m, atHundred);
    }

    [Fact]
    public void Nothing_clears_the_cash_bar_on_a_small_item_so_the_ceiling_is_zero()
    {
        var (maxBid, boundBy) = AuctionSniperAnalyzer.MaxBidDetail(60m, 0m);

        Assert.Equal(0m, maxBid);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByCash, boundBy);
    }

    [Fact]
    public void The_break_even_bid_has_the_premium_and_the_shipping_taken_out_of_it()
    {
        var breakEvenBid = LiveBidAdvisor.BreakEvenBid(breakEvenAllIn: 173.10m, buyerFeePercent: 8m, shipping: 12m);

        Assert.Equal(149.16m, breakEvenBid);
        // Winning at it costs exactly what the flip can carry, give or take the rounded cent.
        Assert.InRange(LiveBidAdvisor.LandedCost(breakEvenBid, 8m, 12m), 173.09m, 173.11m);
    }

    // ── One opinion, not two ──────────────────────────────────────────────────

    /// <summary>
    /// The same item, the same sold history, the same bid — priced once by the live card and once by
    /// the eBay auction sniper. The two features share JackpotHunter's break-even and the sniper's
    /// own ceiling function, and this is the test that keeps them sharing it.
    /// </summary>
    [Fact]
    public void The_live_card_and_the_auction_sniper_name_the_same_ceiling()
    {
        var analysis = Analysis();
        var resale = ResalePricing.From(analysis, Product);

        var card = Card(analysis, bid: 40m);
        var snipe = Sniper.Build(
            new EbayOpportunityItem
            {
                ItemId = "x", Title = Product, Price = 40m, BuyingOption = "AUCTION",
                EndDate = Now.AddMinutes(5), SellerFeedbackScore = 500,
            },
            resale, Fees, Now);

        Assert.Equal(snipe.MaxBid, card.MaxBid);
        Assert.Equal(snipe.BreakEvenBid, card.BreakEvenBid);
    }

    // ── The call ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_bid_under_the_ceiling_on_real_evidence_is_a_bid()
    {
        var card = Card(Analysis(), bid: 40m);

        Assert.Equal(LiveBidCalls.Bid, card.Call);
        Assert.StartsWith("BID UP TO", card.CallLabel, StringComparison.Ordinal);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, card.EvidenceTier);
        Assert.True(card.Headroom > 0m);
        Assert.Equal(Math.Round(card.MaxBid - 40m, 2), card.Headroom);

        // Winning at the ceiling has to clear the bar the ceiling was derived from.
        Assert.True(card.ProfitAtMaxBid >= LocalArbitrageAnalyzer.SolidProfit,
            $"{card.ProfitAtMaxBid} at the ceiling is under the {LocalArbitrageAnalyzer.SolidProfit} bar");
    }

    /// <summary>
    /// The badge is the only part of this card read at a glance, so it is the one part that must
    /// not overstate the ceiling. $67.68 shown as "$68" is a badge telling somebody to bid 32 cents
    /// past the number the rest of the card exists to protect.
    /// </summary>
    [Fact]
    public void The_badge_rounds_the_ceiling_down_never_to_the_nearest_dollar()
    {
        Assert.Equal(Math.Floor(67.68m).ToString("C0"), LiveBidAdvisor.Badge(67.68m));

        var card = Card(Analysis(), bid: 10m, shipping: 12m, fee: 8m);
        Assert.Equal(56.57m, card.MaxBid);
        Assert.Contains(Math.Floor(56.57m).ToString("C0"), card.CallLabel, StringComparison.Ordinal);
        Assert.DoesNotContain(Math.Round(56.57m, 0).ToString("C0"), card.CallLabel, StringComparison.Ordinal);
    }

    [Fact]
    public void Past_the_ceiling_but_still_profitable_is_a_stop_that_says_where_the_money_ends()
    {
        var card = Card(Analysis(), bid: 100m);

        Assert.Equal(LiveBidCalls.Stop, card.Call);
        Assert.Equal("STOP", card.CallLabel);
        Assert.True(card.Headroom < 0m);
        Assert.Contains("before it loses money", card.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Past_the_break_even_is_a_stop_that_says_so_plainly()
    {
        var card = Card(Analysis(), bid: 500m);

        Assert.Equal(LiveBidCalls.Stop, card.Call);
        Assert.Contains("stops making money", card.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Thin evidence lowers the call and never removes the number. The bidder has seconds; "we
    /// couldn't say" with the ceiling withheld is less useful than the ceiling plus the reason to
    /// distrust it.
    /// </summary>
    [Fact]
    public void Two_comps_still_gets_a_ceiling_and_gets_called_risky()
    {
        var card = Card(Analysis(comps: 2, pricedComps: 2), bid: 20m);

        Assert.Equal(LiveBidCalls.Risky, card.Call);
        Assert.Contains("UP TO", card.CallLabel, StringComparison.Ordinal);
        Assert.True(card.MaxBid > 0m);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, card.EvidenceTier);
    }

    [Fact]
    public void Comps_for_another_product_are_called_risky_however_many_there_are()
    {
        var card = Card(Analysis(comps: 20, pricedComps: 20, identityVerified: false), bid: 20m);

        Assert.Equal(LiveBidCalls.Risky, card.Call);
        Assert.False(card.IdentityVerified);
        Assert.Contains("model or part number", card.EvidenceNote, StringComparison.Ordinal);
    }

    [Fact]
    public void An_item_that_cannot_carry_its_own_fees_is_a_dont_bid()
    {
        var card = Card(Analysis(expected: 3m), bid: 1m);

        Assert.Equal(LiveBidCalls.Stop, card.Call);
        Assert.Equal("DON'T BID", card.CallLabel);
        Assert.Equal(0m, card.MaxBid);
    }

    [Fact]
    public void Nothing_priced_it_so_nothing_is_claimed()
    {
        var card = Card(analysis: null, bid: 40m);

        Assert.Equal(LiveBidCalls.NoData, card.Call);
        Assert.Equal("CAN'T PRICE IT", card.CallLabel);
        Assert.Contains("no resale price to bid against", card.Reason, StringComparison.Ordinal);
        Assert.Equal(0m, card.MaxBid);
        Assert.Null(card.ResalePrice);
        Assert.Null(card.ProfitNow);
        // Still worth something: the eBay sold search the bidder can run with their own eyes.
        Assert.Contains("LH_Sold=1", card.SoldSearchUrl, StringComparison.Ordinal);
    }

    /// <summary>
    /// Before the first bid there is no price to judge, and the ceiling is the whole of the useful
    /// answer. Reporting a profit "at the current bid" of zero would be arithmetic about a price
    /// nobody has offered.
    /// </summary>
    [Fact]
    public void Before_the_bidding_starts_the_answer_is_the_ceiling_alone()
    {
        var card = Card(Analysis());

        Assert.False(card.BidWasKnown);
        Assert.Null(card.ProfitNow);
        Assert.Null(card.RoiNow);
        Assert.True(card.MaxBid > 0m);
        Assert.Equal(card.MaxBid, card.Headroom);
        Assert.Equal(LiveBidCalls.Bid, card.Call);
    }

    // ── The statistics behind the ceiling ─────────────────────────────────────

    /// <summary>
    /// Every figure on the card is carried from the analysis, not recomputed. The same title on the
    /// Opportunity Finder has to show the same sell-through and the same spread.
    /// </summary>
    [Fact]
    public void The_sell_through_the_spread_and_the_velocity_are_the_pipelines_own_figures()
    {
        var analysis = Analysis();
        var card = Card(analysis, bid: 40m);

        Assert.Equal(analysis.SellThrough.SellThroughRate, card.SellThroughRate);
        Assert.Equal(analysis.SellThrough.Interpretation, card.SellThroughLabel);
        Assert.Equal(analysis.SellThrough.ActiveComparableCount, card.ActiveCompCount);
        Assert.Equal(analysis.SellThrough.EstimatedMonthlySales, card.EstimatedMonthlySales);
        Assert.Equal(analysis.PriceEstimate.Percentile25, card.PriceLow);
        Assert.Equal(analysis.PriceEstimate.Percentile75, card.PriceHigh);
        Assert.Equal(analysis.PriceEstimate.MedianPrice, card.MedianPrice);
        Assert.Equal(analysis.Confidence.Score, card.ConfidenceScore);
        Assert.Equal(14, card.DaysToSell);
        Assert.NotNull(card.DaysToCash);
    }

    [Fact]
    public void A_rate_with_no_denominator_is_reported_as_unmeasured_rather_than_as_a_hundred_percent()
    {
        var card = Card(Analysis(activeComps: 0, rateIsUnbounded: true), bid: 40m);

        Assert.True(card.SellThroughUnbounded);
        Assert.Null(card.SellThroughRate);
        Assert.Contains(card.Warnings, w => w.Contains("no sell-through rate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_comps_come_back_with_their_prices_and_their_age()
    {
        var card = Card(Analysis(avgShipping: 6m), bid: 40m);

        Assert.Equal(2, card.Comps.Count);
        Assert.Equal(201m, card.Comps[0].TotalPrice);
        Assert.Equal(9, card.Comps[0].AgeDays);
        Assert.Equal(60, card.Comps[1].AgeDays);
        Assert.Equal("https://www.ebay.com/itm/c1", card.Comps[0].Url);
    }

    // ── Freshness ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_card_says_how_old_the_evidence_is_even_when_it_is_fresh()
    {
        var card = Card(Analysis(newestCompDays: 9, oldestCompDays: 60), bid: 40m);

        Assert.Equal(9, card.NewestCompAgeDays);
        Assert.Contains("9 days ago", card.FreshnessNote, StringComparison.Ordinal);
        Assert.Contains("Sold comps, not live asking prices", card.FreshnessNote, StringComparison.Ordinal);
        // Fresh evidence is stated, not warned about.
        Assert.DoesNotContain(card.Warnings, w => w.Contains("days old", StringComparison.Ordinal));
    }

    [Fact]
    public void Evidence_older_than_the_stale_bar_becomes_a_warning()
    {
        var card = Card(Analysis(newestCompDays: 200, oldestCompDays: 400), bid: 40m);

        Assert.Contains(card.Warnings, w => w.Contains("200 days old", StringComparison.Ordinal));
        Assert.Contains("not a price from now", string.Join(" ", card.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void Undated_comps_are_said_to_be_undated_rather_than_treated_as_new()
    {
        var card = Card(Analysis(datedComps: false), bid: 40m);

        Assert.Null(card.NewestCompAgeDays);
        Assert.Contains("cannot be checked", card.FreshnessNote, StringComparison.Ordinal);
    }

    // ── What the ceiling cannot say ───────────────────────────────────────────

    [Fact]
    public void An_unstated_premium_or_shipping_is_warned_about_rather_than_assumed_to_be_zero()
    {
        var blind = Card(Analysis(), bid: 40m);

        Assert.Contains(blind.Warnings, w => w.Contains("No shipping cost entered", StringComparison.Ordinal));
        Assert.Contains(blind.Warnings, w => w.Contains("No buyer's premium entered", StringComparison.Ordinal));

        var stated = Card(Analysis(), bid: 40m, shipping: 12m, fee: 8m);

        Assert.DoesNotContain(stated.Warnings, w => w.Contains("No shipping cost entered", StringComparison.Ordinal));
        Assert.DoesNotContain(stated.Warnings, w => w.Contains("No buyer's premium entered", StringComparison.Ordinal));
        Assert.True(stated.MaxBid < blind.MaxBid, "stating the real costs has to lower the ceiling");
    }

    [Fact]
    public void A_market_that_barely_moves_is_said_out_loud()
    {
        var card = Card(Analysis(sellThroughRate: 6m, activeComps: 130), bid: 40m);

        Assert.Contains(card.Warnings, w => w.Contains("Sell-through is 6%", StringComparison.Ordinal));
    }

    [Fact]
    public void A_scattered_spread_is_flagged_because_condition_decides_which_end_you_get()
    {
        var card = Card(Analysis(p25: 80m, p75: 400m), bid: 40m);

        Assert.Contains(card.Warnings, w => w.Contains("Sold prices are scattered", StringComparison.Ordinal));
    }

    // ── The inputs ────────────────────────────────────────────────────────────

    // Written out rather than as a [Theory]: a decimal is not a legal attribute argument, so the
    // bars would have to be re-typed as literals in the InlineData and would then stop tracking the
    // constants they are supposed to be pinning.
    [Fact]
    public void A_target_return_that_would_make_the_ceiling_meaningless_is_clamped()
    {
        Assert.Equal(LiveBidAdvisor.DefaultTargetRoiPercent, LiveBidAdvisor.SanitizeTargetRoi(null));
        Assert.Equal(LiveBidAdvisor.DefaultTargetRoiPercent, LiveBidAdvisor.SanitizeTargetRoi(0m));
        Assert.Equal(LiveBidAdvisor.DefaultTargetRoiPercent, LiveBidAdvisor.SanitizeTargetRoi(-40m));
        Assert.Equal(45m, LiveBidAdvisor.SanitizeTargetRoi(45m));
        Assert.Equal(LiveBidAdvisor.MaxTargetRoiPercent, LiveBidAdvisor.SanitizeTargetRoi(9_999m));

        // And the app's own bar is what "no target" means — not a friendlier one for the feature
        // with a countdown on it.
        Assert.Equal(LocalArbitrageAnalyzer.SolidRoiPercent, LiveBidAdvisor.DefaultTargetRoiPercent);
    }

    [Fact]
    public void A_mistyped_buyers_premium_costs_a_wrong_number_not_the_answer()
    {
        Assert.Equal(0m, LiveBidAdvisor.SanitizeBuyerFee(null));
        Assert.Equal(0m, LiveBidAdvisor.SanitizeBuyerFee(-3m));
        Assert.Equal(8m, LiveBidAdvisor.SanitizeBuyerFee(8m));
        Assert.Equal(LiveBidAdvisor.MaxBuyerFeePercent, LiveBidAdvisor.SanitizeBuyerFee(800m));
    }

    [Fact]
    public void The_landed_cost_is_the_bid_the_premium_and_the_shipping()
    {
        Assert.Equal(73.10m, LiveBidAdvisor.LandedCost(50m, 8m, 19.10m));
        Assert.Equal(50m, LiveBidAdvisor.LandedCost(50m, 0m, 0m));
    }

    [Fact]
    public void The_card_reports_what_winning_at_the_bid_on_screen_would_cost()
    {
        var card = Card(Analysis(), bid: 50m, shipping: 12m, fee: 8m);

        Assert.Equal(4m, card.BuyerFee);
        Assert.Equal(66m, card.LandedCostNow);
        Assert.NotNull(card.ProfitNow);
        Assert.NotNull(card.RoiNow);
    }

    // ── The seller's own record, on the card ──────────────────────────────────
    // The card grew a second ceiling: what the seller's OWN completed sales of this product say the
    // most to bid is. It is the strongest evidence on the screen and it is allowed to say so — and
    // it is never allowed to move the badge, because two of the seller's own sales are two sales
    // and the call above them is the market's answer.

    private static readonly EarningsCalculator Earnings = new(new ProfitCalculator());

    /// <summary>One of the seller's own sales of this product, priced as Money Made prices it.</summary>
    private static RestockSale Sold(decimal price, int daysAgo, decimal fee = 40m, decimal shippingCost = 20m) =>
        new()
        {
            Sale = Earnings.Compute(
                new FlipRecord
                {
                    Source = "ebay", Title = Product, SoldUtc = Now.AddDays(-daysAgo), Quantity = 1,
                    SalePrice = price, MarketplaceFee = fee, ShippingCost = shippingCost, UnitCost = 60m,
                },
                null, Fees),
        };

    private static OwnSalesEvidence OwnRecord(params RestockSale[] sales) =>
        OwnTrackRecord.Match(Product, sales, [], new DateTimeOffset(Now, TimeSpan.Zero));

    private static LiveBidCard CardWithRecord(
        MarketAnalysisResult? analysis, OwnSalesEvidence own, decimal? bid = null, decimal? fee = null) =>
        Advisor.Build(Product, analysis, Ask(bid, fee: fee), Fees, nowUtc: Now, own: own);

    [Fact]
    public void The_seller_s_own_sales_never_move_the_call_or_the_ceiling_on_the_badge()
    {
        // The whole safety property. The record can disagree with the comps by any margin in either
        // direction and the badge stays the market's answer, because that is the one with sold
        // history behind it.
        var analysis = Analysis();
        var plain = Card(analysis, bid: 40m);

        var poor = CardWithRecord(analysis, OwnRecord(Sold(90m, 20), Sold(90m, 40)), bid: 40m);
        var rich = CardWithRecord(analysis, OwnRecord(Sold(900m, 20), Sold(900m, 40)), bid: 40m);

        foreach (var card in new[] { poor, rich })
        {
            Assert.Equal(plain.Call, card.Call);
            Assert.Equal(plain.CallLabel, card.CallLabel);
            Assert.Equal(plain.MaxBid, card.MaxBid);
            Assert.Equal(plain.BreakEvenBid, card.BreakEvenBid);
            Assert.Equal(plain.Headroom, card.Headroom);
            Assert.Equal(plain.LotRank, card.LotRank);
        }
    }

    [Fact]
    public void A_record_that_prices_lower_than_the_comps_reaches_the_card_s_warnings()
    {
        // Not folded into a score and not netted off the ceiling: said, on the list the seller reads
        // for everything else the number cannot account for.
        var card = CardWithRecord(Analysis(), OwnRecord(Sold(210m, 20), Sold(210m, 40)), bid: 40m);

        Assert.NotNull(card.OwnHistory);
        Assert.True(card.OwnHistory!.CeilingIsLower);
        Assert.True(card.OwnHistory.OwnMaxBid < card.MaxBid);
        Assert.Contains(card.Warnings, w => w.Contains("what you actually got", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_record_is_priced_at_the_same_premium_and_target_as_the_badge_above_it()
    {
        // Two ceilings on one card computed at different terms would be unreadable: the seller would
        // be comparing a number that includes the premium with one that doesn't.
        var own = OwnRecord(Sold(300m, 20), Sold(300m, 40));
        var card = CardWithRecord(Analysis(), own, bid: 40m, fee: 8m);

        var expected = AuctionSniperAnalyzer.MaxBidDetail(
            own.AverageNetProceeds!.Value, card.ShippingCost, card.TargetRoiPercent, card.BuyerFeePercent);

        Assert.Equal(expected.MaxBid, card.OwnHistory!.OwnMaxBid);
    }

    [Fact]
    public void A_card_the_market_could_not_price_still_carries_the_seller_s_own_ceiling()
    {
        // The most valuable case there is: eBay matched nothing, and the seller has sold four of
        // them. The badge still says CAN'T PRICE IT — the market genuinely didn't — and the card
        // now has a number on it anyway, labelled as the seller's own.
        var card = CardWithRecord(analysis: null, OwnRecord(Sold(300m, 20), Sold(300m, 40)));

        Assert.Equal(LiveBidCalls.NoData, card.Call);
        Assert.Equal(0m, card.MaxBid);
        Assert.NotNull(card.OwnHistory);
        Assert.True(card.OwnHistory!.OwnIsTheOnlyCeiling);
        Assert.True(card.OwnHistory.OwnMaxBid > 0m);
        Assert.Contains(card.Warnings, w => w.Contains("Nothing on eBay priced this", StringComparison.Ordinal));
    }

    [Fact]
    public void A_card_built_without_the_seller_s_book_carries_no_record_at_all()
    {
        // Null, not an empty record that renders as "you have never sold one of these" on a screen
        // that simply never read the seller's sales.
        Assert.Null(Card(Analysis(), bid: 40m).OwnHistory);
    }

    [Fact]
    public void The_spoken_line_gains_the_seller_s_own_record_and_only_when_it_is_proven()
    {
        var proven = CardWithRecord(Analysis(), OwnRecord(Sold(210m, 20), Sold(210m, 40)), bid: 40m);
        var once = CardWithRecord(Analysis(), OwnRecord(Sold(210m, 20)), bid: 40m);

        Assert.Contains("You've sold 2", proven.Say, StringComparison.Ordinal);
        Assert.Contains("on your own prices, stop at", proven.Say, StringComparison.Ordinal);
        Assert.DoesNotContain("You've sold", once.Say, StringComparison.Ordinal);

        // And the line still leads with the call and the ceiling — the record is the last clause,
        // never the first.
        Assert.StartsWith(proven.CallLabel, proven.Say, StringComparison.Ordinal);
    }

    // ── When the lot is more than one thing ───────────────────────────────────
    // Sold comps are per unit everywhere in this app, so every figure above this line is a per-unit
    // figure. A live show sells "3x Antminer S9" under one hammer price, and a ceiling for one of
    // them is not a small error on that lot — it is the error that makes the seller pass on the
    // best lot of the night, while the card looks exactly as confident as it always does.

    /// <summary>
    /// Three of them are worth three times as much, and the ceiling says so — with the per-unit
    /// figure carried alongside, because that is the number the seller compares against the single
    /// unit they priced ten minutes ago.
    /// </summary>
    [Fact]
    public void A_lot_of_three_is_priced_as_three()
    {
        var one = Card(Analysis(), bid: 40m);
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Equal(3, three.Units.Count);
        Assert.True(three.Units.IsLot);
        Assert.Equal(one.ResalePrice!.Value * 3m, three.ResalePrice!.Value);

        // At least three times the ceiling, and the per-unit figure is that ceiling divided back
        // down. Never less: a lot is worth what its units are worth, and the app has no reason to
        // pay less per unit for buying three at once than for buying one.
        Assert.True(three.MaxBid >= one.MaxBid * 3m);
        Assert.Equal(Math.Floor(three.MaxBid / 3m * 100m) / 100m, three.Units.MaxBidPerUnit);
    }

    /// <summary>
    /// And on a cheap item it is worth <b>more</b> than three times, because the cash floor is
    /// charged once for the lot. That is the whole reason multi-unit lots are where live buying
    /// pays: a $73 ceiling on one $200 miner is the $100-of-profit bar eating most of the margin,
    /// and three of them clear that bar between them with room to spare.
    /// </summary>
    [Fact]
    public void A_lot_clears_the_cash_floor_the_single_item_was_stuck_under()
    {
        var one = Card(Analysis(), bid: 40m);
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Equal(AuctionSniperAnalyzer.CeilingByCash, one.CeilingBoundBy);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, three.CeilingBoundBy);
        Assert.True(three.MaxBid > one.MaxBid * 3m);
    }

    [Fact]
    public void The_ceiling_scales_exactly_when_the_target_return_is_what_binds_it()
    {
        // With the cash floor out of the way at both sizes, three of something is worth precisely
        // three times as much — there is no lot premium and no lot discount in this arithmetic.
        var one = Card(Analysis(expected: 900m, p25: 800m, p75: 1000m), bid: 40m);
        var three = Card(Analysis(expected: 900m, p25: 800m, p75: 1000m), bid: 40m, quantity: 3);

        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, one.CeilingBoundBy);

        // Within a cent or two of exactly three times, and never under it. Both ceilings are
        // truncated to the cent — the lot's once, the single item's once and then multiplied — so
        // the two arithmetics differ by the fraction of a cent each of them threw away.
        Assert.InRange(three.MaxBid - one.MaxBid * 3m, 0m, 0.03m);
        Assert.InRange(one.MaxBid - three.Units.MaxBidPerUnit, -0.01m, 0.01m);
    }

    /// <summary>
    /// The break-even multiplies too, so the walk-away line and the ceiling stay the same distance
    /// apart in relative terms. A ceiling that scaled while the break-even did not would put the
    /// seller past the point of losing money at three times the speed.
    /// </summary>
    [Fact]
    public void The_walk_away_line_multiplies_with_the_ceiling()
    {
        var one = Card(Analysis(), bid: 40m);
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Equal(one.BreakEvenBid * 3m, three.BreakEvenBid);
        Assert.True(three.MaxBid < three.BreakEvenBid);
    }

    /// <summary>
    /// The cash floor is charged once for the lot rather than once per unit.
    /// </summary>
    /// <remarks>
    /// The floor exists because "finding it, listing it and packing it costs the same hour whatever
    /// it cost to buy" — and of those three, the packing and the label on each unit are already
    /// money inside the break-even (ProfitCalculator bills packaging and labour per unit). What is
    /// left is the hour of finding it and deciding, which happens once for the whole lot. Charging
    /// it N times would refuse most multi-unit lots on a bar nobody ever agreed to.
    /// </remarks>
    [Fact]
    public void The_cash_floor_is_charged_once_for_the_lot()
    {
        var three = Card(Analysis(), bid: 40m, quantity: 3);
        var breakEvenPerUnit = Hunter.BreakEvenBuyPrice(
            ResalePricing.From(Analysis(), Product), Fees);

        Assert.Equal(
            AuctionSniperAnalyzer.MaxBidFor(breakEvenPerUnit * 3m, shippingCost: 0m),
            three.MaxBid);
    }

    [Fact]
    public void The_shipping_and_the_premium_stay_the_lots_own()
    {
        // One shipment and one premium, however many things are in the box — both are charged on
        // the hammer price, not per unit.
        var three = Card(Analysis(), bid: 100m, shipping: 20m, fee: 8m, quantity: 3);

        Assert.Equal(20m, three.ShippingCost);
        Assert.Equal(8m, three.BuyerFee);                              // 8% of 100, once
        Assert.Equal(LiveBidAdvisor.LandedCost(100m, 8m, 20m), three.LandedCostNow);
    }

    [Fact]
    public void The_profit_at_the_bid_on_screen_is_the_whole_lots()
    {
        var three = Card(Analysis(), bid: 90m, quantity: 3);

        Assert.NotNull(three.ProfitNow);
        // Three units of resale against one landed cost. The single-unit card at a third of the
        // bid is the same trade, so the return is the same and the cash is three times.
        var oneAtAThird = Card(Analysis(), bid: 30m);
        Assert.Equal(oneAtAThird.RoiNow, three.RoiNow);
        Assert.Equal(oneAtAThird.ProfitNow!.Value * 3m, three.ProfitNow!.Value);
    }

    /// <summary>
    /// The market statistics stay per unit, because they are descriptions of sales that happened —
    /// of ONE of the thing. Multiplying a percentile by three would be inventing a lot nobody sold.
    /// </summary>
    [Fact]
    public void The_spread_and_the_median_are_not_multiplied()
    {
        var one = Card(Analysis(), bid: 40m);
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Equal(one.MedianPrice, three.MedianPrice);
        Assert.Equal(one.PriceLow, three.PriceLow);
        Assert.Equal(one.PriceHigh, three.PriceHigh);
        Assert.Equal(one.SellThroughRate, three.SellThroughRate);
        Assert.Equal(one.CompCount, three.CompCount);
    }

    [Fact]
    public void The_call_says_what_the_lot_costs_and_what_one_of_them_costs()
    {
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Contains("for all 3", three.Reason, StringComparison.Ordinal);
        Assert.Contains($"{three.Units.MaxBidPerUnit:C} each", three.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spoken_line_says_how_many_before_it_says_anything_else()
    {
        // A seller who hears "BID UP TO $500" on a miner they know goes for $170 needs the next two
        // words to be "for three".
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.StartsWith(three.CallLabel, three.Say, StringComparison.Ordinal);
        Assert.Contains("For all 3", three.Say, StringComparison.Ordinal);
        Assert.True(
            three.Say.IndexOf("For all 3", StringComparison.Ordinal)
            < three.Say.IndexOf("Resells around", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lot_is_warned_about_being_a_lot()
    {
        var three = Card(Analysis(), bid: 40m, quantity: 3);

        Assert.Contains(three.Warnings, w => w.Contains("sell all 3", StringComparison.Ordinal));
        // And what one unsold unit costs, in cash, because that is the risk the ceiling assumes away.
        Assert.Contains(three.Warnings, w => w.Contains($"{three.Units.ProfitPerUnit:C} of this gone", StringComparison.Ordinal));
    }

    [Fact]
    public void A_lot_bigger_than_the_market_says_how_long_the_money_is_in_it()
    {
        // Four of these sell a month. Twelve of them is a quarter of a year of listing, and nothing
        // else on this card says so — the days-to-sell figure is the wait for the FIRST one.
        var twelve = Card(Analysis(), bid: 40m, quantity: 12);

        Assert.Equal(3m, twelve.Units.MonthsToClear);
        Assert.True(twelve.Units.DaysToSellAll > twelve.DaysToSell);
        Assert.Contains(twelve.Warnings, w => w.Contains("months of selling", StringComparison.Ordinal));
    }

    [Fact]
    public void The_resale_price_is_never_discounted_for_being_several()
    {
        // The lot pays for itself in TIME, which is measured, and not in a haircut off the one
        // figure on this card that comes from real sales.
        var one = Card(Analysis(), bid: 40m);
        var five = Card(Analysis(), bid: 40m, quantity: 5);

        Assert.Equal(one.ResalePrice!.Value * 5m, five.ResalePrice!.Value);
        Assert.Equal(one.ResalePrice, five.Units.ResalePerUnit);
    }

    [Fact]
    public void A_lot_ranks_above_the_same_item_on_its_own()
    {
        // The list is ordered by what a lot is worth at its own ceiling, and three of something is
        // worth three times as much of the night as one of it.
        Assert.True(Card(Analysis(), quantity: 3).LotRank > Card(Analysis()).LotRank);
    }

    [Fact]
    public void The_count_comes_off_the_lots_own_name_when_nobody_typed_one()
    {
        var card = Advisor.Build("3x " + Product, Analysis(), new LiveBidRequest { Title = "3x " + Product },
            Fees, nowUtc: Now);

        Assert.Equal(3, card.Units.Count);
        Assert.Equal(LiveLotSize.SourceTitle, card.Units.Source);
    }

    [Fact]
    public void A_single_item_is_untouched_by_any_of_this()
    {
        // The whole compatibility property, stated once: with no count anywhere, every figure is
        // the figure this card has always shown, and the block says so rather than being absent.
        var card = Card(Analysis(), bid: 40m);

        Assert.Equal(1, card.Units.Count);
        Assert.False(card.Units.IsLot);
        Assert.Equal("Priced as a single item.", card.Units.Note);
        Assert.Equal(card.MaxBid, card.Units.MaxBidPerUnit);
        Assert.Equal(card.ResalePrice, card.Units.ResalePerUnit);
        Assert.Equal(card.ProfitAtMaxBid, card.Units.ProfitPerUnit);
        Assert.DoesNotContain("for all", card.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("For all", card.Say, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lot_whose_size_nobody_stated_is_priced_as_one_and_says_so_out_loud()
    {
        var card = Advisor.Build("MYSTERY MINER LOT", Analysis(), new LiveBidRequest { Title = "MYSTERY MINER LOT" },
            Fees, nowUtc: Now);

        Assert.Equal(1, card.Units.Count);
        Assert.True(card.Units.CountUnstated);
        Assert.Contains(card.Warnings, w => w.Contains("priced as ONE", StringComparison.Ordinal));
        Assert.Contains("priced as ONE", card.Say, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sellers_own_record_stays_on_the_per_unit_scale()
    {
        // Their record is a record of selling ONE of these at a time — one listing, one buyer, one
        // fee. Measured against a ceiling for three it would report their own history as a third of
        // what it is, on the card where it is the strongest evidence there is.
        var own = OwnRecord(Sold(300m, 20), Sold(300m, 40));
        var one = Advisor.Build(Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now, own: own);
        var three = Advisor.Build(Product, Analysis(), Ask(bid: 40m, quantity: 3), Fees, nowUtc: Now, own: own);

        Assert.Equal(one.OwnHistory!.OwnMaxBid, three.OwnHistory!.OwnMaxBid);
        Assert.Contains(three.Warnings, w => w.Contains("per unit", StringComparison.Ordinal));
    }

    [Fact]
    public void A_typed_one_puts_a_lot_back_to_a_single_item()
    {
        // The undo. A name this read as three, said by the host to be one, is priced as one — and
        // the ceiling goes back to exactly what it would have been with no count anywhere.
        var lot = Advisor.Build("3x " + Product, Analysis(), new LiveBidRequest { Title = "3x " + Product },
            Fees, nowUtc: Now);
        var single = Advisor.Build("3x " + Product, Analysis(),
            new LiveBidRequest { Title = "3x " + Product, Quantity = 1 }, Fees, nowUtc: Now);

        Assert.Equal(3, lot.Units.Count);
        Assert.Equal(1, single.Units.Count);
        Assert.Equal(Card(Analysis()).MaxBid, single.MaxBid);
    }
}
