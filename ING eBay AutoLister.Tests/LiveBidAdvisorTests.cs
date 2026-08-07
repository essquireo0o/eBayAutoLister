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
        int? quantity = null, decimal? increment = null) =>
        new()
        {
            Title = Product, CurrentBid = bid, ShippingCost = shipping, BuyerFeePercent = fee,
            TargetRoiPercent = target, Quantity = quantity, BidIncrement = increment,
        };

    private static LiveBidCard Card(
        MarketAnalysisResult? analysis, decimal? bid = null, decimal? shipping = null,
        decimal? fee = null, decimal? target = null, int? quantity = null, decimal? increment = null) =>
        Advisor.Build(Product, analysis, Ask(bid, shipping, fee, target, quantity, increment), Fees, nowUtc: Now);

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

    // ── What the card says it searched for ────────────────────────────────────
    // Five statistics stand on a keyword lookup the seller never sees. On a live show the name and
    // the search stopped being the same string, so the card carries both. See LiveSearchQuery.

    [Fact]
    public void Every_card_says_what_the_sold_search_asked_for()
    {
        var card = Card(Analysis());

        Assert.Equal(Product, card.Search.Typed);
        Assert.Equal(Product, card.Search.Query);
        Assert.False(card.Search.Changed);
        // And it is what PricedAs has always claimed to be: the title the lookup ran against.
        Assert.Equal(card.Search.Query, card.PricedAs);
    }

    [Fact]
    public void A_card_built_without_a_search_still_cannot_claim_one_that_nothing_would_run()
    {
        // The advisor is handed the terms by the endpoint. Called without them it falls back to what
        // the builder WOULD ask for, rather than to the typed name — a card claiming a search of
        // "🔥3x Antminer S9 NO RESERVE" would be describing a lookup that returns nothing.
        var card = Advisor.Build("🔥3x " + Product + " NO RESERVE", Analysis(),
            new LiveBidRequest { Title = "🔥3x " + Product + " NO RESERVE" }, Fees, nowUtc: Now);

        Assert.Equal(Product, card.Search.Query);
        Assert.True(card.Search.Changed);
        Assert.Contains(card.Search.Dropped, d => d.Text == "NO RESERVE");
    }

    [Fact]
    public void The_search_the_endpoint_ran_is_the_search_the_card_reports()
    {
        // The endpoint decides what to ask (and whether to widen); the card only reports it. A card
        // that re-derived the query would eventually disagree with the lookup that produced its
        // comps, which is the one thing on this screen nothing can check.
        var asked = LiveSearchQuery.Exact("🔥 " + Product + " NO RESERVE");
        var card = Advisor.Build("🔥 " + Product + " NO RESERVE", Analysis(),
            new LiveBidRequest { Title = "🔥 " + Product + " NO RESERVE" }, Fees, nowUtc: Now, search: asked);

        Assert.Equal(asked.Query, card.Search.Query);
        Assert.True(card.Search.AskedForExactly);
        Assert.Equal(asked.Query, card.PricedAs);
    }

    [Fact]
    public void A_widened_search_is_a_warning_and_not_a_footnote()
    {
        // The ceiling is then a real ceiling for a slightly different thing, which changes what
        // every other number on the card means. It goes at the TOP of the warnings for that reason.
        var wide = LiveSearchQuery.Widen(LiveSearchQuery.Build("Pokemon 151 Ultra Premium Collection sealed"))!;
        var card = Advisor.Build("Pokemon 151 Ultra Premium Collection sealed", Analysis(),
            new LiveBidRequest { Title = "Pokemon 151 Ultra Premium Collection sealed" }, Fees,
            nowUtc: Now, search: wide);

        Assert.True(card.Search.Widened);
        Assert.Contains("Pokemon 151 Ultra", card.Warnings[0], StringComparison.Ordinal);
        Assert.Contains("not for the whole name", card.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_card_that_was_not_widened_says_nothing_about_widening()
    {
        Assert.DoesNotContain(Card(Analysis()).Warnings,
            w => w.Contains("widened", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Nothing_matched_names_the_words_that_matched_nothing()
    {
        // "No sold history matched this item" is a diagnosis with no next move. The words the search
        // actually used are one the seller can act on in a single press.
        var card = Advisor.Build("🔥3x " + Product + " NO RESERVE", analysis: null,
            new LiveBidRequest { Title = "🔥3x " + Product + " NO RESERVE" }, Fees, nowUtc: Now);

        Assert.Equal(LiveBidCalls.NoData, card.Call);
        Assert.Contains(Product, card.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("NO RESERVE", card.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sold_search_link_opens_the_search_that_was_actually_run()
    {
        // The bidder's own eyes are the last check on a thin card, and a link that reproduces the
        // query that found nothing is a link that shows them an empty eBay page.
        var card = Advisor.Build("🔥3x " + Product + " NO RESERVE", Analysis(),
            new LiveBidRequest { Title = "🔥3x " + Product + " NO RESERVE" }, Fees, nowUtc: Now);

        Assert.DoesNotContain("RESERVE", card.SoldSearchUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Antminer", card.SoldSearchUrl, StringComparison.OrdinalIgnoreCase);
    }

    // ── Which way the price has been going ────────────────────────────────────
    // The card has always said how OLD its evidence was and never which way it was moving. A median
    // across two months is the right price for an item that has held it and an overstatement for one
    // that has been sliding since — and the difference is paid in cash, in seconds, at a hammer.

    /// <summary>
    /// The comps the trend is read from are the comps the price came from. No second lookup, no
    /// second clock, and — because the whole set is held with the analysis — the same reading on a
    /// re-price as on the fresh card.
    /// </summary>
    private static MarketAnalysisResult WithTrend(decimal recent, decimal prior, decimal? expected = 200m)
    {
        var analysis = Analysis(expected: expected);
        var recentDays = new[] { 3, 8, 14, 20, 27 };
        var priorDays = new[] { 33, 39, 45, 51, 57 };

        analysis.AllSoldComparables = recentDays
            .Select((d, i) => (Days: d, Price: recent + (2 - i)))
            .Concat(priorDays.Select((d, i) => (Days: d, Price: prior + (2 - i))))
            .Select(x => new MarketplaceComparableResult
            {
                ItemId = $"t{x.Days}", Title = Product, SoldPrice = x.Price, TotalPrice = x.Price,
                SoldDate = Now.AddDays(-x.Days), Quantity = 1,
            })
            .ToList();

        return analysis;
    }

    [Fact]
    public void A_confirmed_slide_lowers_the_ceiling_the_card_prints()
    {
        var steady = Card(WithTrend(recent: 200m, prior: 202m));
        var sliding = Card(WithTrend(recent: 140m, prior: 200m));

        Assert.True(sliding.Trend.Discounted);
        Assert.Equal(LiveTrendDirections.Falling, sliding.Trend.Direction);

        // The ceiling really moved, and it moved DOWN. This is the whole feature.
        Assert.True(sliding.MaxBid < steady.MaxBid);
        Assert.True(sliding.BreakEvenBid < steady.BreakEvenBid);
        Assert.Equal(140m, sliding.ResalePrice);
    }

    /// <summary>
    /// The one number the haircut is allowed to move is the price the ceiling is built from.
    /// Everything the comps DESCRIBE — the middle-half spread, the comp table, the sell-through, the
    /// confidence — is a record of sales that really happened, and scaling those would be inventing
    /// sales nobody made.
    /// </summary>
    [Fact]
    public void The_haircut_moves_the_price_and_never_the_evidence()
    {
        var steady = Card(WithTrend(recent: 200m, prior: 202m));
        var sliding = Card(WithTrend(recent: 140m, prior: 200m));

        Assert.Equal(steady.PriceLow, sliding.PriceLow);
        Assert.Equal(steady.PriceHigh, sliding.PriceHigh);
        Assert.Equal(steady.CompCount, sliding.CompCount);
        Assert.Equal(steady.ConfidenceScore, sliding.ConfidenceScore);
        Assert.Equal(steady.EvidenceTier, sliding.EvidenceTier);
        Assert.Equal(steady.SellThroughRate, sliding.SellThroughRate);
        Assert.Equal(steady.Comps.Count, sliding.Comps.Count);
    }

    [Fact]
    public void A_climbing_item_is_priced_exactly_as_if_nothing_had_been_read()
    {
        var blind = Card(Analysis());                              // no comps carried, nothing to read
        var climbing = Card(WithTrend(recent: 200m, prior: 140m));

        Assert.Equal(LiveTrendDirections.Rising, climbing.Trend.Direction);
        Assert.Equal(blind.MaxBid, climbing.MaxBid);
        Assert.Equal(blind.ResalePrice, climbing.ResalePrice);
    }

    /// <summary>
    /// Above the money, with the widened-search warning, because both are facts about what the
    /// numbers underneath them MEAN rather than facts about the money itself.
    /// </summary>
    [Fact]
    public void The_slide_is_warned_about_before_anything_it_changes_the_meaning_of()
    {
        var card = Card(WithTrend(recent: 140m, prior: 200m), quantity: 3);

        Assert.Contains("selling for less", card.Warnings[0], StringComparison.OrdinalIgnoreCase);
        // And before the lot warning, which is the next-most-surprising thing on the card.
        Assert.Contains(card.Warnings, w => w.Contains("3 units", StringComparison.OrdinalIgnoreCase));
        Assert.True(card.Warnings.FindIndex(w => w.Contains("3 units", StringComparison.OrdinalIgnoreCase)) > 0);
    }

    [Fact]
    public void A_card_with_nothing_to_read_says_so_rather_than_saying_nothing()
    {
        var card = Card(Analysis());

        Assert.False(card.Trend.Readable);
        Assert.Equal(LiveTrendDirections.Unknown, card.Trend.Direction);
        Assert.NotEqual("", card.Trend.Headline);
        Assert.NotEqual("", card.Trend.MoneyNote);
        Assert.DoesNotContain(card.Warnings, w => w.Contains("selling for less", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The spoken line is glanced at, not studied, so it speaks in exactly one case: the one where
    /// the badge the seller just heard is lower than the comps under it suggest.
    /// </summary>
    [Fact]
    public void The_spoken_line_mentions_the_cut_and_nothing_else_about_the_trend()
    {
        Assert.Contains("sliding", Card(WithTrend(140m, 200m)).Say, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sliding", Card(WithTrend(200m, 140m)).Say, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sliding", Card(WithTrend(200m, 202m)).Say, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sliding", Card(Analysis()).Say, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The lot arithmetic is unchanged by the cut: the ceiling for three is three times the ceiling
    /// for one, at whatever price the trend left standing. Two features that each multiply are how
    /// a lot of three ends up priced at nine.
    /// </summary>
    [Fact]
    public void The_cut_and_the_unit_count_do_not_multiply_each_other()
    {
        var one = Card(WithTrend(recent: 140m, prior: 200m));
        var three = Card(WithTrend(recent: 140m, prior: 200m), quantity: 3);

        Assert.Equal(140m, one.ResalePrice);
        Assert.Equal(420m, three.ResalePrice);
        Assert.Equal(140m, three.Units.ResalePerUnit);
    }

    // ── The press, not the price ──────────────────────────────────────────────
    // Every figure above compares the bid ON SCREEN against the ceiling. Nobody buys at that price:
    // pressing bid commits to the next increment. See LiveBidIncrement.

    /// <summary>
    /// The case the whole block exists for, on a real card rather than a hand-built one: the room
    /// figure says there is money above the bid and there is no press that stays inside it.
    /// </summary>
    [Fact]
    public void A_card_can_show_room_above_the_bid_and_still_have_no_press_left()
    {
        var card = Card(Analysis(), bid: 70m);

        Assert.Equal(73.10m, card.MaxBid);
        Assert.True(card.Headroom > 0m, "the ceiling really is above the bid");
        Assert.Equal(LiveBidCalls.Bid, card.Call);

        // And pressing bid makes it $75, which is past it.
        Assert.Equal(LiveNextBidVerdicts.Stop, card.NextBid.Verdict);
        Assert.Equal(75m, card.NextBid.Amount);
        Assert.Equal(0, card.NextBid.BidsLeft);
    }

    /// <summary>
    /// And it reaches the warning list, above the money warnings, because it is the one line on this
    /// card that contradicts the room figure printed next to it.
    /// </summary>
    [Fact]
    public void The_no_press_left_case_is_warned_about_before_the_money_warnings()
    {
        var card = Card(Analysis(), bid: 70m);

        var press = card.Warnings.FindIndex(w => w.Contains("no press", StringComparison.OrdinalIgnoreCase));
        var shipping = card.Warnings.FindIndex(w => w.Contains("No shipping cost", StringComparison.Ordinal));

        Assert.True(press >= 0, "the card never said there was no press left to make");
        Assert.True(shipping > press, "the press warning has to be read before the ones about the money");
    }

    /// <summary>
    /// One press below that, the same card has a bid left to make and says which one. Nothing about
    /// the ceiling changed — only whether the hand can act on it.
    /// </summary>
    [Fact]
    public void One_step_lower_the_same_ceiling_still_has_a_press_in_it()
    {
        var card = Card(Analysis(), bid: 68m);

        Assert.Equal(73.10m, card.MaxBid);
        Assert.Equal(LiveNextBidVerdicts.Last, card.NextBid.Verdict);
        Assert.Equal(73m, card.NextBid.Amount);
        Assert.Equal(1, card.NextBid.BidsLeft);
        Assert.DoesNotContain(card.Warnings, w => w.Contains("no press", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The seller can see the show's own next-bid amount and this app cannot. A typed step is used
    /// as typed — and on the very card that had no press left at the assumed ladder, it finds three.
    /// </summary>
    [Fact]
    public void A_typed_bid_step_outranks_the_assumed_ladder_on_the_card()
    {
        var card = Card(Analysis(), bid: 70m, increment: 1m);

        Assert.Equal(LiveBidIncrement.SourceSeller, card.NextBid.IncrementSource);
        Assert.Equal(71m, card.NextBid.Amount);
        Assert.Equal(3, card.NextBid.BidsLeft);   // 71, 72, 73
        Assert.Equal(LiveNextBidVerdicts.Press, card.NextBid.Verdict);
    }

    /// <summary>
    /// The step moves no price at all. It is a fact about the bid button, and a card priced with one
    /// stated has exactly the ceiling, break-even and profit of a card priced without one.
    /// </summary>
    [Fact]
    public void The_bid_step_changes_no_number_the_ceiling_is_made_of()
    {
        var assumed = Card(Analysis(), bid: 70m);
        var stated = Card(Analysis(), bid: 70m, increment: 1m);

        Assert.Equal(assumed.MaxBid, stated.MaxBid);
        Assert.Equal(assumed.BreakEvenBid, stated.BreakEvenBid);
        Assert.Equal(assumed.ProfitAtMaxBid, stated.ProfitAtMaxBid);
        Assert.Equal(assumed.Headroom, stated.Headroom);
        Assert.Equal(assumed.ResalePrice, stated.ResalePrice);
    }

    /// <summary>
    /// The profit at the next bid and the profit at the ceiling are one subtraction apart, because
    /// both are the same break-even minus a landed cost. Two break-evens on one card is how the
    /// strip and the badge end up disagreeing about the same dollar.
    /// </summary>
    [Fact]
    public void The_profit_at_the_next_bid_is_the_same_break_even_as_the_profit_at_the_ceiling()
    {
        var card = Card(Analysis(), bid: 55m, fee: 8m, shipping: 12m);

        var atCeiling = LiveBidAdvisor.LandedCost(card.MaxBid, card.BuyerFeePercent, card.ShippingCost);
        var atNext = LiveBidAdvisor.LandedCost(card.NextBid.Amount, card.BuyerFeePercent, card.ShippingCost);

        Assert.Equal(card.NextBid.Landed, atNext);
        Assert.Equal(Math.Round(card.ProfitAtMaxBid - (atNext - atCeiling), 2), card.NextBid.Profit);
    }

    /// <summary>
    /// Present on every card, including the ones nothing priced. A block that only appears when it
    /// has something to say is a block whose silence means both "press away" and "nothing looked".
    /// </summary>
    [Fact]
    public void Every_card_carries_the_block_even_when_nothing_priced_it()
    {
        var nothing = Card(null, bid: 40m);

        Assert.NotNull(nothing.NextBid);
        Assert.False(nothing.NextBid.Readable);
        Assert.Equal(LiveNextBidVerdicts.Unreadable, nothing.NextBid.Verdict);
        Assert.Equal("", nothing.NextBid.Headline);

        // And before the bidding starts, on a card that priced perfectly well.
        var notStarted = Card(Analysis());
        Assert.False(notStarted.NextBid.Readable);
        Assert.Equal("Bidding hasn't started", notStarted.NextBid.Headline);
    }

    /// <summary>
    /// On a lot, the presses are counted against the LOT's ceiling — one hammer buys all of them, so
    /// the bid on screen is a lot price and the ceiling it is compared with has to be too.
    /// </summary>
    [Fact]
    public void On_a_lot_the_presses_are_counted_against_the_lots_own_ceiling()
    {
        var three = Card(Analysis(), bid: 200m, quantity: 3);

        // The lot's ceiling, off the lot's own break-even — not one unit's ceiling times three.
        Assert.Equal(
            AuctionSniperAnalyzer.MaxBidDetail(Math.Round(173.10m * 3, 2), 0m, LiveBidAdvisor.DefaultTargetRoiPercent).MaxBid,
            three.MaxBid);

        Assert.Equal(LiveNextBidVerdicts.Press, three.NextBid.Verdict);
        Assert.Equal(210m, three.NextBid.Amount);   // $10 steps at $200
        Assert.True(three.NextBid.Amount <= three.MaxBid);
        Assert.Equal(19, three.NextBid.BidsLeft);   // 210 … 390; 400 is past the ceiling
    }

    // ── What condition it is, against what condition the comps were ──────────────────────────

    /// <summary>
    /// The same analysis, plus a full comp set that states a condition on every row — the shape
    /// <c>AnalyzeProductAsync</c> hands back from the hosted sold-comps database.
    /// </summary>
    private static MarketAnalysisResult WithConditions(int newCount, decimal newPrice, int usedCount, decimal usedPrice)
    {
        var analysis = Analysis();
        for (var i = 0; i < newCount; i++)
        {
            analysis.AllSoldComparables.Add(new MarketplaceComparableResult
            {
                ItemId = $"n{i}", Title = Product, Condition = "Brand New",
                SoldPrice = newPrice, TotalPrice = newPrice,
            });
        }
        for (var i = 0; i < usedCount; i++)
        {
            analysis.AllSoldComparables.Add(new MarketplaceComparableResult
            {
                ItemId = $"u{i}", Title = Product, Condition = "Pre-Owned",
                SoldPrice = usedPrice, TotalPrice = usedPrice,
            });
        }
        return analysis;
    }

    private static LiveBidCard ConditionCard(MarketAnalysisResult analysis, string? picked, decimal? bid = null) =>
        Advisor.Build(Product, analysis,
            new LiveBidRequest { Title = Product, CurrentBid = bid, Condition = picked }, Fees, nowUtc: Now);

    /// <summary>
    /// The whole feature in one card. Nine sealed comps at $200 and three used ones at $100: the
    /// blend the ceiling used to be built on is a $200 price, and the thing on screen is used.
    /// </summary>
    [Fact]
    public void A_used_lot_priced_off_mostly_new_comps_has_its_ceiling_cut_to_the_used_median()
    {
        var mixed = WithConditions(9, 200m, 3, 100m);

        var blind = ConditionCard(mixed, null);
        var used = ConditionCard(mixed, LiveConditionBands.Used);

        Assert.False(blind.Condition.Discounted);
        Assert.True(used.Condition.Discounted);
        Assert.Equal(50m, used.Condition.CutPercent);

        // Half the resale, so a lower ceiling and a lower break-even. Nothing else on the card is a
        // second opinion about the money — both come off the same Build.
        Assert.Equal(Math.Round(blind.ResalePrice!.Value * 0.5m, 2), used.ResalePrice);
        Assert.True(used.MaxBid < blind.MaxBid);
        Assert.True(used.BreakEvenBid < blind.BreakEvenBid);
    }

    /// <summary>
    /// The comps DESCRIBE sales that really happened. The cut scales what the ceiling is built out
    /// of and touches none of the evidence — scaling a percentile or a comp count would be
    /// inventing sales nobody made.
    /// </summary>
    [Fact]
    public void The_cut_moves_the_ceiling_and_leaves_every_description_of_the_market_alone()
    {
        var mixed = WithConditions(9, 200m, 3, 100m);

        var blind = ConditionCard(mixed, null);
        var used = ConditionCard(mixed, LiveConditionBands.Used);

        Assert.Equal(blind.PriceLow, used.PriceLow);
        Assert.Equal(blind.PriceHigh, used.PriceHigh);
        Assert.Equal(blind.CompCount, used.CompCount);
        Assert.Equal(blind.SellThroughRate, used.SellThroughRate);
        Assert.Equal(blind.ConfidenceScore, used.ConfidenceScore);
        Assert.Equal(blind.Comps.Count, used.Comps.Count);
        Assert.Equal(blind.FreshnessNote, used.FreshnessNote);
    }

    /// <summary>
    /// A card whose comps state no condition is priced exactly as it was before any of this
    /// existed. The whole existing suite runs on that analysis, which is the real assertion — this
    /// says it out loud.
    /// </summary>
    [Fact]
    public void Comps_that_state_no_condition_price_the_card_exactly_as_before()
    {
        var plain = Card(Analysis(), bid: 120m, fee: 8m, shipping: 12m);

        Assert.NotNull(plain.Condition);
        Assert.False(plain.Condition.Readable);
        Assert.False(plain.Condition.Discounted);
        Assert.Equal(200m, plain.ResalePrice);
    }

    /// <summary>
    /// Present on every card, including the one nothing priced — the same discipline the next-bid
    /// and trend blocks follow, for the same reason.
    /// </summary>
    [Fact]
    public void Every_card_carries_the_condition_block()
    {
        Assert.NotNull(Card(null, bid: 40m).Condition);
        Assert.NotNull(Card(Analysis()).Condition);
        Assert.False(string.IsNullOrWhiteSpace(ConditionCard(WithConditions(9, 200m, 3, 100m), null).Condition.Headline));
    }

    /// <summary>
    /// The warning belongs with the two other facts about what the money MEANS, and above the ones
    /// about the money itself. A seller who reads the ceiling and stops reading has to have hit it.
    /// </summary>
    [Fact]
    public void The_condition_warning_lands_above_the_warnings_about_the_money()
    {
        var card = ConditionCard(WithConditions(9, 200m, 3, 100m), LiveConditionBands.Used, bid: 20m);

        var condition = card.Warnings.FindIndex(w => w.Contains("comps are mixed", StringComparison.OrdinalIgnoreCase));
        var shipping = card.Warnings.FindIndex(w => w.StartsWith("No shipping cost", StringComparison.Ordinal));

        Assert.True(condition >= 0, $"the condition warning is gone: {string.Join(" | ", card.Warnings)}");
        Assert.True(shipping > condition, "the condition warning sank below the warnings about the money");
    }

    /// <summary>
    /// A better condition than the comps never raises anything — and never quietly lowers anything
    /// either. The card is the blind one, exactly.
    /// </summary>
    [Fact]
    public void A_sealed_lot_over_mostly_used_comps_is_priced_identically_to_a_blind_one()
    {
        var mixed = WithConditions(3, 200m, 9, 100m);

        var blind = ConditionCard(mixed, null, bid: 30m);
        var sealedLot = ConditionCard(mixed, LiveConditionBands.New, bid: 30m);

        Assert.False(sealedLot.Condition.Discounted);
        Assert.Equal(blind.ResalePrice, sealedLot.ResalePrice);
        Assert.Equal(blind.MaxBid, sealedLot.MaxBid);
        Assert.Equal(blind.BreakEvenBid, sealedLot.BreakEvenBid);
        Assert.Equal(blind.ProfitAtMaxBid, sealedLot.ProfitAtMaxBid);
    }

    /// <summary>
    /// The two cuts compose rather than compete: one is what these fetch lately, the other is what
    /// they fetch in this shape. Both come off the same rows, both only ever cut, and the resale
    /// price is the product of the two — not the deeper of them and not the last one applied.
    /// </summary>
    [Fact]
    public void The_condition_cut_stacks_on_the_trend_cut_rather_than_replacing_it()
    {
        var analysis = Analysis();
        // A confirmed slide: five recent sales around $140, five earlier ones around $200 — the
        // shape LiveTrendTests calls a 30% fall. Every row states a condition, so both read.
        var recentDays = new[] { 3, 8, 14, 20, 27 };
        var priorDays = new[] { 33, 39, 45, 51, 57 };
        void Add(string id, int? daysAgo, decimal price, string condition) =>
            analysis.AllSoldComparables.Add(new MarketplaceComparableResult
            {
                ItemId = id, Title = Product, Condition = condition,
                SoldPrice = price, TotalPrice = price,
                SoldDate = daysAgo is int d ? Now.AddDays(-d) : null,
            });

        for (var i = 0; i < recentDays.Length; i++) Add($"r{i}", recentDays[i], 140m + (2 - i), "Brand New");
        for (var i = 0; i < priorDays.Length; i++) Add($"p{i}", priorDays[i], 200m + (2 - i), "Brand New");
        // Undated on purpose. A condition band needs no date and the trend read only looks at rows
        // that carry one, so these give the condition read something to price off without putting a
        // second price cluster inside the trend's own windows.
        for (var i = 0; i < 4; i++) Add($"u{i}", null, 60m, "Pre-Owned");

        var used = ConditionCard(analysis, LiveConditionBands.Used);

        Assert.True(used.Trend.Discounted);
        Assert.True(used.Condition.Discounted);

        // Both ratios, applied to the estimator's own expected sale price.
        Assert.Equal(
            Math.Round(200m * used.Trend.ResaleMultiplier * used.Condition.ResaleMultiplier, 2),
            used.ResalePrice);
    }

    // ── How many of these you'd then own ──────────────────────────────────────
    //
    // The failure this catches is not a mispriced lot. Every lot in it is priced correctly — the
    // host has a pallet of one product and puts one up every four minutes, and the card says BID UP
    // TO $90 six times because six times it is true. What is not true is the implied sixth
    // sentence: that six of them is six times the profit.

    /// <summary>
    /// Every card carries the block, so its silence can never mean "nothing looked". A card built
    /// with no own-record at all — the state the endpoint only reaches when the seller's book threw
    /// — says so rather than reporting an empty shelf, which is the failure that would turn "you
    /// already have four" into silence.
    /// </summary>
    [Fact]
    public void Every_card_carries_a_stock_read_and_an_unread_shelf_says_so()
    {
        var blind = Card(Analysis(), bid: 40m);

        Assert.NotNull(blind.Stock);
        Assert.False(blind.Stock.ShelfRead);
        Assert.Equal(LiveStockVerdicts.None, blind.Stock.Verdict);
        Assert.NotEmpty(blind.Stock.Headline);

        var read = Advisor.Build(
            Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now, own: new OwnSalesEvidence());

        Assert.True(read.Stock.ShelfRead);
        Assert.Equal(LiveStockVerdicts.Single, read.Stock.Verdict);
        Assert.Equal(1, read.Stock.UnitsAfter);
    }

    /// <summary>The units the ceiling was built for are the units the pile counts. A lot of three is
    /// three things to sell, not one.</summary>
    [Fact]
    public void A_multi_unit_lot_puts_all_of_its_units_in_the_pile()
    {
        var card = Advisor.Build("3x " + Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now);

        Assert.Equal(3, card.Units.Count);
        Assert.Equal(3, card.Stock.LotUnits);
        Assert.Equal(3, card.Stock.UnitsAfter);
    }

    /// <summary>Tonight's buy sheet reaches the card — the count nothing else on it can see.</summary>
    [Fact]
    public void Lots_won_tonight_reach_the_card()
    {
        // Seven already in boxes tonight against four sales a month — two months of stock, which is
        // the first depth this is allowed to spend a warning on.
        var card = Advisor.Build(
            Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now, tonight: new LiveStockTonight(7, 7));

        Assert.Equal(7, card.Stock.WonTonight);
        Assert.Equal(8, card.Stock.UnitsAfter);
        Assert.Equal(LiveStockVerdicts.Deep, card.Stock.Verdict);
        Assert.Contains(card.Warnings, w => w.Contains("7 won tonight", StringComparison.Ordinal));
    }

    /// <summary>
    /// And it changes nothing about the money. Saturation is a claim about a calendar, not about
    /// what the object fetches — the fourth one still resells for exactly what the comps say.
    /// </summary>
    [Fact]
    public void A_deep_shelf_moves_no_figure_on_the_card()
    {
        var clear = Card(Analysis(), bid: 40m);
        var deep = Advisor.Build(
            Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now, tonight: new LiveStockTonight(15, 15));

        Assert.Equal(LiveStockVerdicts.Flooded, deep.Stock.Verdict);
        Assert.Equal(clear.ResalePrice, deep.ResalePrice);
        Assert.Equal(clear.MaxBid, deep.MaxBid);
        Assert.Equal(clear.BreakEvenBid, deep.BreakEvenBid);
        Assert.Equal(clear.ProfitAtMaxBid, deep.ProfitAtMaxBid);
        Assert.Equal(clear.ProfitNow, deep.ProfitNow);
        Assert.Equal(clear.Call, deep.Call);
        Assert.Equal(clear.CallLabel, deep.CallLabel);
        Assert.Equal(clear.PriceLow, deep.PriceLow);
        Assert.Equal(clear.PriceHigh, deep.PriceHigh);
        Assert.Equal(clear.SellThroughRate, deep.SellThroughRate);
    }

    /// <summary>
    /// A card nothing could price still counts the pile. "Nothing on eBay priced this AND you are
    /// already holding four" is the most useful thing an unpriceable card can say, and it is exactly
    /// the card on which a seller talks themselves into one more cheap one.
    /// </summary>
    [Fact]
    public void An_unpriceable_card_still_counts_the_pile()
    {
        var card = Advisor.Build(
            Product, Analysis(expected: null), Ask(bid: 40m), Fees, nowUtc: Now,
            tonight: new LiveStockTonight(4, 4));

        Assert.Equal(LiveBidCalls.NoData, card.Call);
        Assert.Equal(5, card.Stock.UnitsAfter);
        Assert.Equal(LiveStockVerdicts.Blind, card.Stock.Verdict);
        Assert.Contains(card.Warnings, w => w.Contains("no dated sold history", StringComparison.Ordinal));
    }

    /// <summary>A card built without a sheet is priced identically and simply counts one fewer
    /// thing — the count is additive, like every WhatsNot read before it.</summary>
    [Fact]
    public void A_card_built_without_a_sheet_is_priced_identically()
    {
        var without = Card(Analysis(), bid: 40m);
        var with = Advisor.Build(
            Product, Analysis(), Ask(bid: 40m), Fees, nowUtc: Now, tonight: LiveStockTonight.Nothing);

        Assert.Equal(without.MaxBid, with.MaxBid);
        Assert.Equal(without.ResalePrice, with.ResalePrice);
        Assert.Equal(without.Say, with.Say);
        Assert.Equal(without.Warnings.Count, with.Warnings.Count);
    }

    // ── What the lot really costs to get delivered ────────────────────────────
    //
    // A live seller posts ONE box per show, not one per lot. Every ceiling this app produced before
    // LiveShipShare existed charged the full first-item rate to every lot of the night, which
    // overstates what the fourth win costs — on exactly the cheap lots where that is most of the
    // margin. It is the only read on this card that can raise a ceiling, so what is pinned here is
    // mostly the three gates it fails closed on.

    /// <summary>A card that says nothing about a show is charged exactly what it always was.</summary>
    [Fact]
    public void A_card_with_no_show_named_is_charged_full_freight()
    {
        var card = Card(Analysis(), bid: 40m, shipping: 12m);

        Assert.Equal(LiveShipVerdicts.Alone, card.Ship.Verdict);
        Assert.Equal(12m, card.Ship.FirstItemShipping);
        Assert.Equal(12m, card.ShippingCost);
        Assert.False(card.Ship.Applied);
    }

    /// <summary>
    /// The one state that moves money, and the direction it moves in: the lot rides in a box that is
    /// already going out, so the ceiling really is higher than the first lot of the show got.
    /// </summary>
    [Fact]
    public void A_lot_riding_in_an_open_box_is_costed_at_the_extra_item_rate()
    {
        var first = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees, nowUtc: Now);
        var second = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees, nowUtc: Now,
            ship: new LiveShipTonight(2, 13m));

        Assert.Equal(LiveShipVerdicts.First, first.Ship.Verdict);
        Assert.Equal(12m, first.ShippingCost);

        Assert.Equal(LiveShipVerdicts.Combined, second.Ship.Verdict);
        Assert.Equal(1m, second.ShippingCost);
        Assert.Equal(11m, second.Ship.Saved);

        // Eleven dollars less to get it delivered is eleven more dollars of bid, and the profit at
        // the ceiling is unchanged — the money moved from the freight into the bid, not out of thin
        // air. Both are the ceiling's own arithmetic, not this read's.
        Assert.Equal(11m, second.MaxBid - first.MaxBid);
        Assert.Equal(11m, second.BreakEvenBid - first.BreakEvenBid);
        Assert.Equal(first.ProfitAtMaxBid, second.ProfitAtMaxBid);
        // And the landed cost at the bid on screen falls by exactly the freight that came off it.
        Assert.Equal(11m, first.LandedCostNow - second.LandedCostNow);
    }

    /// <summary>
    /// A show that combines does not change what the item is WORTH. The resale side, the spread, the
    /// sell-through and the comps are records of sales that really happened, and freight is a fact
    /// about a parcel.
    /// </summary>
    [Fact]
    public void Combining_the_freight_moves_no_resale_figure()
    {
        var full = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees, nowUtc: Now);
        var combined = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees, nowUtc: Now,
            ship: new LiveShipTonight(1, 12m));

        Assert.Equal(full.ResalePrice, combined.ResalePrice);
        Assert.Equal(full.MedianPrice, combined.MedianPrice);
        Assert.Equal(full.PriceLow, combined.PriceLow);
        Assert.Equal(full.PriceHigh, combined.PriceHigh);
        Assert.Equal(full.SellThroughRate, combined.SellThroughRate);
        Assert.Equal(full.CompCount, combined.CompCount);
        Assert.Equal(full.DaysToSell, combined.DaysToSell);
    }

    /// <summary>
    /// Repeated wins from one show with no extra-item rate. Nothing is assumed on the seller's
    /// behalf — the ceiling stays exactly where it was and the warning says what would move it.
    /// </summary>
    [Fact]
    public void Repeated_wins_with_no_extra_rate_warn_rather_than_guess()
    {
        var plain = Card(Analysis(), bid: 40m, shipping: 12m);
        var repeated = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: null), Fees, nowUtc: Now,
            ship: new LiveShipTonight(3, 36m));

        Assert.Equal(LiveShipVerdicts.Unstated, repeated.Ship.Verdict);
        Assert.Equal(plain.MaxBid, repeated.MaxBid);
        Assert.Contains(repeated.Warnings, w => w.Contains("one box per show", StringComparison.Ordinal));
    }

    /// <summary>
    /// A saving is good news, and good news does not belong in a warning list read under time
    /// pressure. The strip carries it; the warnings stay for what could cost money.
    /// </summary>
    [Fact]
    public void A_combined_lot_raises_no_warning()
    {
        var card = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees, nowUtc: Now,
            ship: new LiveShipTonight(2, 13m));

        Assert.True(card.Ship.Applied);
        Assert.Empty(card.Ship.Warning);
        Assert.DoesNotContain(card.Warnings, w => w.Contains("shipping", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Free combined shipping is a real arrangement and a typed zero has to reach the ceiling as
    /// one — without the old "no shipping cost entered" warning firing on a lot whose freight is
    /// genuinely nothing.
    /// </summary>
    [Fact]
    public void A_lot_that_ships_free_in_the_box_is_not_mistaken_for_one_with_no_shipping_entered()
    {
        var card = Advisor.Build(
            Product, Analysis(), Freight(bid: 40m, shipping: 9m, extra: 0m), Fees, nowUtc: Now,
            ship: new LiveShipTonight(1, 9m));

        Assert.Equal(0m, card.ShippingCost);
        Assert.Equal(LiveShipVerdicts.Combined, card.Ship.Verdict);
        Assert.DoesNotContain(card.Warnings, w => w.Contains("No shipping cost entered", StringComparison.Ordinal));
    }

    /// <summary>A card nothing could price still says what the freight is doing — it is a fact about
    /// a parcel, not about a comp.</summary>
    [Fact]
    public void An_unpriceable_card_still_reads_the_freight()
    {
        var card = Advisor.Build(
            Product, Analysis(expected: null), Freight(bid: 40m, shipping: 12m, extra: 1m), Fees,
            nowUtc: Now, ship: new LiveShipTonight(2, 13m));

        Assert.Equal(LiveBidCalls.NoData, card.Call);
        Assert.Equal(LiveShipVerdicts.Combined, card.Ship.Verdict);
        Assert.Equal(1m, card.ShippingCost);
    }

    /// <summary>A card built without a sheet is priced identically — additive, like every WhatsNot
    /// read before it.</summary>
    [Fact]
    public void A_card_built_without_a_box_is_priced_identically()
    {
        var without = Card(Analysis(), bid: 40m, shipping: 12m);
        var with = Advisor.Build(
            Product, Analysis(), Ask(bid: 40m, shipping: 12m), Fees, nowUtc: Now,
            ship: LiveShipTonight.Nothing);

        Assert.Equal(without.MaxBid, with.MaxBid);
        Assert.Equal(without.ShippingCost, with.ShippingCost);
        Assert.Equal(without.Say, with.Say);
        Assert.Equal(without.Warnings.Count, with.Warnings.Count);
    }

    private static LiveBidRequest Freight(decimal? bid, decimal? shipping, decimal? extra,
                                          string show = "ingmining") =>
        new()
        {
            Title = Product, CurrentBid = bid, ShippingCost = shipping,
            AdditionalItemShipping = extra, ShowName = show,
        };
}
