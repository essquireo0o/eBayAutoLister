using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The sniper hands someone a number to type into a bid box, so what is pinned here is the honesty
// of that number: the ceiling is exact and never rounds in the seller's favour, shipping comes out
// of the bid rather than out of the profit afterwards, and an auction that is merely cheap SO FAR
// is never scored as a cheap auction.
public class AuctionSniperAnalyzerTests
{
    private static readonly AuctionSniperAnalyzer Sniper = new(new ProfitCalculator(), new JackpotHunter(new ProfitCalculator()));
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor
    private static readonly DateTime Now = new(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    private static EbayOpportunityItem Item(
        decimal price, DateTime? endUtc = null, string title = Product, string option = "AUCTION",
        decimal shipping = 0m, bool shippingStated = true, int bids = 0, int feedback = 500, string id = "item-1") =>
        new()
        {
            ItemId = id, Title = title, Price = price, ShippingCost = shipping, ShippingStated = shippingStated,
            Url = $"https://www.ebay.com/itm/{id}", EndDate = endUtc, BuyingOption = option, BidCount = bids,
            SellerUsername = "seller", SellerFeedbackScore = feedback,
        };

    private static ResalePricing Pricing(
        decimal? expected = 200m, int soldComps = 8, int terapeakComps = 0,
        decimal avgShipping = 0m, int confidence = 70) =>
        new()
        {
            LookupTitle = Product,
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, TerapeakCompCount = terapeakComps,
            AvgCompShipping = avgShipping, ConfidenceScore = confidence, ConfidenceLevel = "Good",
            EstimatedDaysToSell = 14, EstimatedMonthlySales = 4m,
        };

    // ── The ceiling ───────────────────────────────────────────────────────────

    /// <summary>
    /// The ceiling is the stricter of the two bars, and on an item this size that is the CASH bar.
    /// $200 sells for $173.10 after fees, so the most you can bid and still clear
    /// <see cref="LocalArbitrageAnalyzer.SolidProfit"/> in the hand is $73.10 — a bid of $133.15
    /// would return 30% and put $39.95 in your pocket, which is not a flip worth doing.
    /// </summary>
    [Fact]
    public void MaxBidFor_IsTheBidThatStillLeavesAProfitWorthHaving()
    {
        var maxBid = AuctionSniperAnalyzer.MaxBidFor(breakEvenAllIn: 173.10m, shippingCost: 0m);

        Assert.Equal(73.10m, maxBid);
        Assert.Equal(LocalArbitrageAnalyzer.SolidProfit, Math.Round(173.10m - maxBid, 2));
    }

    /// <summary>
    /// On a big enough item the percentage binds instead: $2,000 resells at $1,731 after fees, and
    /// 30% of a bid that size is worth far more than $100, so the ROI bar is the one that stops you.
    /// </summary>
    [Fact]
    public void MaxBidFor_OnALargeItem_IsBoundByThePercentage_NotTheCashBar()
    {
        var maxBid = AuctionSniperAnalyzer.MaxBidFor(breakEvenAllIn: 1731.00m, shippingCost: 0m);

        Assert.Equal(1331.53m, maxBid);   // 1731.00 / 1.30, truncated
        Assert.InRange(Math.Round((1731.00m - maxBid) / maxBid * 100m, 1), 30m, 30.1m);
        Assert.True(1731.00m - maxBid > LocalArbitrageAnalyzer.SolidProfit);
    }

    [Fact]
    public void MaxBidFor_TruncatesRatherThanRoundsUp()
    {
        // 1731.00 / 1.30 = 1331.5384…, and a ceiling that rounds up gives away the margin it exists
        // to protect. It must never round to 1331.54.
        Assert.Equal(1331.53m, AuctionSniperAnalyzer.MaxBidFor(1731.00m, 0m));
    }

    [Fact]
    public void MaxBidFor_ShippingComesOutOfTheBid_NotOutOfTheProfit()
    {
        var free = AuctionSniperAnalyzer.MaxBidFor(173.10m, shippingCost: 0m);
        var paid = AuctionSniperAnalyzer.MaxBidFor(173.10m, shippingCost: 10m);

        // Winning costs the bid plus the shipping, so the ceiling falls by exactly the shipping and
        // the profit at that ceiling is unchanged.
        Assert.Equal(free - 10m, paid);
    }

    /// <summary>
    /// A cheap item has no bid worth making. On a $50 break-even, 30% would allow $38.46 and leave
    /// $11.54 — and there is no bid at all, down to and including free, that clears
    /// <see cref="LocalArbitrageAnalyzer.SolidProfit"/> in cash. The honest ceiling is zero: this
    /// is not an auction to win cheaply, it is an auction to leave alone.
    /// </summary>
    [Fact]
    public void MaxBidFor_CheapItem_HasNoBidWorthMaking()
    {
        Assert.Equal(0m, AuctionSniperAnalyzer.MaxBidFor(50m, 0m));
        Assert.Equal(0m, AuctionSniperAnalyzer.MaxBidFor(LocalArbitrageAnalyzer.SolidProfit, 0m));
    }

    /// <summary>The first break-even at which a bid becomes worth placing at all.</summary>
    [Fact]
    public void MaxBidFor_JustAboveTheCashBar_IsTheFirstRealCeiling()
    {
        var maxBid = AuctionSniperAnalyzer.MaxBidFor(LocalArbitrageAnalyzer.SolidProfit + 10m, 0m);

        Assert.Equal(10m, maxBid);
        Assert.Equal(LocalArbitrageAnalyzer.SolidProfit, (LocalArbitrageAnalyzer.SolidProfit + 10m) - maxBid);
    }

    [Fact]
    public void MaxBidFor_NothingClearsFees_IsZero_NotNegative()
    {
        Assert.Equal(0m, AuctionSniperAnalyzer.MaxBidFor(breakEvenAllIn: 20m, shippingCost: 0m));
        Assert.Equal(0m, AuctionSniperAnalyzer.MaxBidFor(breakEvenAllIn: 0m, shippingCost: 0m));
    }

    // ── The money on a row ────────────────────────────────────────────────────

    [Fact]
    public void Build_ProfitAtTheCurrentBidIsAfterEveryFee()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        // $200 sale − $26.90 in eBay fees − $60 all-in.
        Assert.Equal(26.90m, row.EstimatedFees);
        Assert.Equal(113.10m, row.ProfitAtCurrentPrice);
        Assert.Equal(188.5m, row.RoiAtCurrentPrice);
    }

    [Fact]
    public void Build_BreakEvenBidIsTheBidThatMakesNothing()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);
        Assert.Equal(173.10m, row.BreakEvenBid);

        // Paying exactly that leaves zero — which is what a break-even has to mean to someone
        // deciding whether to place one more bid.
        var atBreakEven = Sniper.Build(Item(173.10m, Now.AddMinutes(30)), Pricing(), Fees, Now);
        Assert.Equal(0m, atBreakEven.ProfitAtCurrentPrice);
    }

    [Fact]
    public void Build_ShippingIsPartOfWhatWinningCosts()
    {
        var free = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);
        var paid = Sniper.Build(Item(60m, Now.AddMinutes(30), shipping: 15m), Pricing(), Fees, Now);

        Assert.Equal(75m, paid.AllInCost);
        // A dollar of shipping is a dollar of profit, and a dollar off the ceiling.
        Assert.Equal(free.ProfitAtCurrentPrice - 15m, paid.ProfitAtCurrentPrice);
        Assert.Equal(free.MaxBid - 15m, paid.MaxBid);
    }

    [Fact]
    public void Build_HeadroomIsWhatIsLeftBeforeWalkingAway()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        Assert.Equal(73.10m, row.MaxBid);
        Assert.Equal(13.10m, row.BidHeadroom);
    }

    [Fact]
    public void Build_HeadroomGoesNegativeOnceTheBiddingPassesTheCeiling()
    {
        var row = Sniper.Build(Item(150m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        // Still profitable at $23.10, but past the point where it is worth the work — and the row
        // says so rather than hiding the overshoot at zero.
        Assert.Equal(-76.90m, row.BidHeadroom);
        Assert.Equal(23.10m, row.ProfitAtCurrentPrice);
        Assert.Equal(AuctionSniperAnalyzer.VerdictWatch, row.Verdict);
    }

    [Fact]
    public void Build_DiscountIsMeasuredOnTheAllInCost_NotTheBid()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30), shipping: 20m), Pricing(expected: 200m), Fees, Now);

        // $80 all-in against a $200 median is 60% under, not the 70% the bid alone would claim.
        Assert.Equal(60m, row.DiscountPercent);
    }

    [Fact]
    public void Build_ProfitAtMaxBidIsWhatWinningAtTheCeilingPays()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        Assert.Equal(100.00m, row.ProfitAtMaxBid);
        Assert.True(row.ProfitAtMaxBid < row.ProfitAtCurrentPrice,
            "Winning at the ceiling must always pay less than winning at today's price.");
    }

    [Fact]
    public void Build_NoSoldHistory_IsNoData_NotZeroProfit()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), resale: null, Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictNoData, row.Verdict);
        Assert.Null(row.ProfitAtCurrentPrice);
        Assert.Null(row.MaxBid);
        Assert.Contains("no price to bid against", row.VerdictNote);
    }

    // ── The clock, which is the whole game ────────────────────────────────────

    [Theory]
    [InlineData(-5, "ended")]
    [InlineData(30, "closing")]
    [InlineData(60, "closing")]
    [InlineData(61, "today")]
    [InlineData(24 * 60, "today")]
    [InlineData(24 * 60 + 1, "open")]
    public void TimeTierFor_BandsTheClock(int minutes, string expected) =>
        Assert.Equal(expected, AuctionSniperAnalyzer.TimeTierFor(minutes));

    [Fact]
    public void TimeTierFor_NoEndDateIsNone_NotEnded() =>
        Assert.Equal("none", AuctionSniperAnalyzer.TimeTierFor(null));

    [Fact]
    public void PriceIsReal_AFixedPriceListingAlwaysIs()
    {
        Assert.True(AuctionSniperAnalyzer.PriceIsRealFor("FIXED_PRICE", null));
        Assert.True(AuctionSniperAnalyzer.PriceIsRealFor("FIXED_PRICE", 99_999));
    }

    [Fact]
    public void PriceIsReal_AnAuctionOnlyIsNearTheEnd()
    {
        Assert.True(AuctionSniperAnalyzer.PriceIsRealFor("AUCTION", 60));
        Assert.True(AuctionSniperAnalyzer.PriceIsRealFor("AUCTION", AuctionSniperAnalyzer.PriceIsRealHours * 60));
        Assert.False(AuctionSniperAnalyzer.PriceIsRealFor("AUCTION", AuctionSniperAnalyzer.PriceIsRealHours * 60 + 1));
        Assert.False(AuctionSniperAnalyzer.PriceIsRealFor("AUCTION", null));
    }

    [Fact]
    public void Build_AnAuctionDaysOutIsTooEarly_HoweverGoodTheArithmeticLooks()
    {
        var row = Sniper.Build(Item(12m, Now.AddDays(3)), Pricing(expected: 200m), Fees, Now);

        // $12 against a $200 median is the most attractive row a naive board could print, and it is
        // not a $12 item. The ceiling still holds, because it comes from the resale side.
        Assert.Equal(AuctionSniperAnalyzer.VerdictTooEarly, row.Verdict);
        Assert.Equal(73.10m, row.MaxBid);
        Assert.Contains("isn't real yet", row.VerdictNote);
    }

    [Fact]
    public void Build_TheSameAuctionInsideTheWindowIsASnipe()
    {
        var row = Sniper.Build(Item(12m, Now.AddHours(2)), Pricing(expected: 200m), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictSnipe, row.Verdict);
        Assert.True(row.PriceIsReal);
    }

    [Fact]
    public void Build_AFixedPriceListingIsNeverTooEarly()
    {
        var row = Sniper.Build(Item(60m, endUtc: null, option: "FIXED_PRICE"), Pricing(), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictSnipe, row.Verdict);
        Assert.Equal("none", row.TimeTier);
        Assert.True(row.PriceIsReal);
    }

    [Fact]
    public void Build_AClosedAuctionIsEnded_NotAnOpportunity()
    {
        var row = Sniper.Build(Item(12m, Now.AddMinutes(-5)), Pricing(), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictEnded, row.Verdict);
        Assert.Null(row.SnipeAtUtc);
    }

    [Fact]
    public void Build_SnipeTimeIsSecondsBeforeTheClose_NotNow()
    {
        var end = Now.AddMinutes(45);
        var row = Sniper.Build(Item(60m, end), Pricing(), Fees, Now);

        Assert.Equal(end.AddSeconds(-AuctionSniperAnalyzer.SnipeSecondsBeforeEnd), row.SnipeAtUtc);
    }

    [Fact]
    public void Build_NoSnipeTimeOnARowNobodyShouldBidOn()
    {
        var tooDear = Sniper.Build(Item(190m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictPass, tooDear.Verdict);
        Assert.Null(tooDear.SnipeAtUtc);
    }

    [Fact]
    public void Build_NoSnipeTimeOnAFixedPriceListing()
    {
        var bin = Sniper.Build(Item(60m, Now.AddDays(20), option: "FIXED_PRICE"), Pricing(), Fees, Now);

        // There is no moment to be ready for — it can be bought now, or somebody else buys it.
        Assert.Null(bin.SnipeAtUtc);
    }

    // ── The verdicts ──────────────────────────────────────────────────────────

    [Fact]
    public void Judge_AlreadyAboveWhatItIsWorth_IsPass()
    {
        var row = Sniper.Build(Item(180m, Now.AddMinutes(30)), Pricing(expected: 200m), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictPass, row.Verdict);
        Assert.Contains("already worth less than it costs", row.VerdictNote);
    }

    [Fact]
    public void Judge_NothingCanCarryItsOwnFees_IsPass()
    {
        // A $2 item cannot pay a $0.40 fixed fee and a percentage and still be worth listing.
        var row = Sniper.Build(Item(1m, Now.AddMinutes(30)), Pricing(expected: 2m), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictPass, row.Verdict);
    }

    [Fact]
    public void Judge_TooFewComps_IsThin_EvenWhenTheMoneyLooksHuge()
    {
        var row = Sniper.Build(Item(20m, Now.AddMinutes(30)), Pricing(expected: 400m, soldComps: 2), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictThin, row.Verdict);
        Assert.Contains("2 sold comps", row.VerdictNote);
    }

    [Fact]
    public void Judge_ThinEvidenceNeverWearsTheSnipeBadge()
    {
        var row = Sniper.Build(Item(20m, Now.AddMinutes(30)),
            Pricing(expected: 400m, soldComps: 4, confidence: 40), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictThin, row.Verdict);
        Assert.Contains("weak", row.VerdictNote);
    }

    [Fact]
    public void Judge_ProfitableButUnderTheBar_IsWatch()
    {
        // $158 all-in against a $173.10 break-even leaves $15 — real money, and not worth the work.
        var row = Sniper.Build(Item(158m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictWatch, row.Verdict);
        Assert.Equal(15.10m, row.ProfitAtCurrentPrice);
    }

    [Fact]
    public void Judge_TheEvidenceBarsAreTheSameOnesTheRestOfTheAppUses()
    {
        Assert.Equal(LocalArbitrageAnalyzer.GoldmineMinComps, AuctionSniperAnalyzer.MinCompsToSnipe);
        Assert.Equal(LocalArbitrageAnalyzer.GoldmineMinConfidence, AuctionSniperAnalyzer.MinConfidenceToSnipe);
    }

    // ── Why it might be cheap ─────────────────────────────────────────────────

    [Fact]
    public void Risks_UnstatedShippingIsNotFreeShipping()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30), shippingStated: false), Pricing(), Fees, Now);

        Assert.Contains(row.Risks, r => r.Contains("Shipping isn't stated"));
    }

    [Fact]
    public void Risks_ACrowdedAuctionSaysSo()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30), bids: 9), Pricing(), Fees, Now);

        Assert.Contains(row.Risks, r => r.Contains("9 bids already"));
    }

    [Fact]
    public void Risks_HoursOutMeansThePriceWillClimb()
    {
        var row = Sniper.Build(Item(60m, Now.AddHours(6)), Pricing(), Fees, Now);

        Assert.Equal("today", row.TimeTier);
        Assert.Contains(row.Risks, r => r.Contains("final minutes"));
    }

    [Fact]
    public void Risks_ABrandNewSellerIsNamed()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30), feedback: 2), Pricing(), Fees, Now);

        Assert.Contains(row.Risks, r => r.Contains("2 feedback"));
    }

    [Fact]
    public void Risks_SuspiciouslyCheapIsFlagged_NotHidden()
    {
        // Under a quarter of the quick-sale price. On an auction this is the row worth looking
        // hardest at — in both directions — so it is kept and flagged rather than dropped.
        var row = Sniper.Build(Item(20m, Now.AddMinutes(30)), Pricing(expected: 200m), Fees, Now);

        Assert.Equal(AuctionSniperAnalyzer.VerdictSnipe, row.Verdict);
        Assert.Contains(row.Risks, r => r.Contains("under a quarter"));
    }

    [Fact]
    public void Risks_NoneOnARowNobodyShouldBidOn()
    {
        var row = Sniper.Build(Item(180m, Now.AddMinutes(30), shippingStated: false, feedback: 1), Pricing(), Fees, Now);

        // A pass has already been refused; piling five reasons on top of it is noise on a row
        // nobody is going to act on.
        Assert.Equal(AuctionSniperAnalyzer.VerdictPass, row.Verdict);
        Assert.Empty(row.Risks);
    }

    // ── The identity guard ────────────────────────────────────────────────────

    private static readonly ProductNormalizer Normalizer = new(new ProductIdentityExtractor());

    private static (bool Plausible, string? Reason) Guard(EbayOpportunityItem item, decimal floor) =>
        AuctionSniperAnalyzer.IsPlausibleSnipe(item, Normalizer.Normalize(item.Title), Product, floor);

    [Fact]
    public void Guard_AnAccessoryIsNeverThisProduct()
    {
        var (plausible, _) = Guard(Item(15m, Now.AddMinutes(30), title: "Control Board for Antminer S19j Pro 104TH"), 42.50m);
        Assert.False(plausible);
    }

    [Fact]
    public void Guard_APartsUnitIsNeverPricedAgainstWorkingComps()
    {
        var (plausible, _) = Guard(Item(40m, Now.AddMinutes(30), title: "Antminer S19j Pro 104TH FOR PARTS not working"), 42.50m);
        Assert.False(plausible);
    }

    [Fact]
    public void Guard_ACheapAuctionSurvives_BecauseThatIsThePointOfTheFeature()
    {
        // Auctions legitimately open at pennies. Rejecting on price here would throw away every row
        // worth finding — identity is the only thing checked.
        var (plausible, _) = Guard(Item(0.99m, Now.AddMinutes(30)), floor: 42.50m);
        Assert.True(plausible);
    }

    [Fact]
    public void Guard_ACheapFixedPriceListingDoesNotSurvive()
    {
        // A Buy It Now at a fifth of the going rate is an accessory, a shell or a scam — its price
        // does not move, so nothing explains it away.
        var (plausible, reason) = Guard(
            Item(0.99m, endUtc: null, option: "FIXED_PRICE"), floor: 42.50m);

        Assert.False(plausible);
        Assert.Contains("floor", reason);
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_UrgencyPutsTheSoonestCloseFirst_NotTheBiggestNumber()
    {
        var later = Sniper.Build(Item(20m, Now.AddHours(5), id: "big"), Pricing(expected: 400m), Fees, Now);
        var sooner = Sniper.Build(Item(60m, Now.AddMinutes(10), id: "soon"), Pricing(expected: 200m), Fees, Now);

        var ranked = AuctionSniperAnalyzer.Rank([later, sooner]);

        // The biggest margin on the board is worth nothing if it closes while the seller is reading
        // about a different row.
        Assert.Equal("soon", ranked[0].ItemId);
    }

    [Fact]
    public void Rank_ByProfitPutsTheBiggestCeilingFirst()
    {
        var later = Sniper.Build(Item(20m, Now.AddHours(5), id: "big"), Pricing(expected: 400m), Fees, Now);
        var sooner = Sniper.Build(Item(60m, Now.AddMinutes(10), id: "soon"), Pricing(expected: 200m), Fees, Now);

        var ranked = AuctionSniperAnalyzer.Rank([sooner, later], AuctionSniperAnalyzer.SortByProfit);

        Assert.Equal("big", ranked[0].ItemId);
    }

    [Fact]
    public void Rank_AnActionableRowAlwaysOutranksOneThatIsNotYet()
    {
        var tooEarly = Sniper.Build(Item(12m, Now.AddDays(3), id: "early"), Pricing(expected: 400m), Fees, Now);
        var snipe = Sniper.Build(Item(60m, Now.AddMinutes(30), id: "now"), Pricing(expected: 200m), Fees, Now);

        var ranked = AuctionSniperAnalyzer.Rank([tooEarly, snipe], AuctionSniperAnalyzer.SortByProfit);

        Assert.Equal("now", ranked[0].ItemId);
        Assert.Equal("early", ranked[1].ItemId);
    }

    [Fact]
    public void Rank_TooEarlyOutranksARowTheBiddingHasSpoiled()
    {
        var watch = Sniper.Build(Item(158m, Now.AddMinutes(30), id: "spoiled"), Pricing(), Fees, Now);
        var tooEarly = Sniper.Build(Item(12m, Now.AddDays(3), id: "early"), Pricing(), Fees, Now);

        var ranked = AuctionSniperAnalyzer.Rank([watch, tooEarly]);

        Assert.Equal("early", ranked[0].ItemId);
    }

    [Fact]
    public void Rank_AListingWithNoClockNeverSortsAsEndingNow()
    {
        var bin = Sniper.Build(Item(60m, endUtc: null, option: "FIXED_PRICE", id: "bin"), Pricing(), Fees, Now);
        var auction = Sniper.Build(Item(60m, Now.AddHours(10), id: "auction"), Pricing(), Fees, Now);

        var ranked = AuctionSniperAnalyzer.Rank([bin, auction]);

        Assert.Equal("auction", ranked[0].ItemId);
    }

    // ── The board's totals ────────────────────────────────────────────────────

    [Fact]
    public void Summarize_OnlyCountsRowsWhosePriceIsReal()
    {
        var snipe = Sniper.Build(Item(60m, Now.AddMinutes(30), id: "now"), Pricing(), Fees, Now);
        var tooEarly = Sniper.Build(Item(12m, Now.AddDays(3), id: "early"), Pricing(), Fees, Now);
        var pass = Sniper.Build(Item(180m, Now.AddMinutes(30), id: "dear"), Pricing(), Fees, Now);

        var summary = AuctionSniperAnalyzer.Summarize([snipe, tooEarly, pass], Now);

        Assert.Equal(1, summary.SnipeCount);
        Assert.Equal(1, summary.TooEarlyCount);
        // The too-early row's apparent $161 of profit must not reach any total — it is profit on a
        // price that hasn't happened.
        Assert.Equal(100.00m, summary.ProfitAtCeilings);
        Assert.Equal(73.10m, summary.CapitalToWinAll);
    }

    [Fact]
    public void Summarize_NextEndIsTheSoonestSnipe_NotTheSoonestRow()
    {
        var passSoon = Sniper.Build(Item(180m, Now.AddMinutes(5), id: "dear"), Pricing(), Fees, Now);
        var snipe = Sniper.Build(Item(60m, Now.AddMinutes(40), id: "now"), Pricing(), Fees, Now);

        var summary = AuctionSniperAnalyzer.Summarize([passSoon, snipe], Now);

        Assert.Equal(Now.AddMinutes(40), summary.NextEndUtc);
        Assert.Equal(1, summary.ClosingWithinTheHour);
    }

    [Fact]
    public void Summarize_AnEmptyBoardIsZeroed_NotNull()
    {
        var summary = AuctionSniperAnalyzer.Summarize([], Now);

        Assert.Equal(0, summary.SnipeCount);
        Assert.Equal(0m, summary.ProfitAtCeilings);
        Assert.Null(summary.NextEndUtc);
    }

    // ── The watch list ────────────────────────────────────────────────────────

    private static FlipRecord Sale(string title, int daysAgo = 10, string status = "paid") => new()
    {
        Title = title, Status = status, SoldUtc = new DateTimeOffset(Now).AddDays(-daysAgo),
        SalePrice = 200m,
    };

    [Fact]
    public void WatchTerms_TwelveSalesOfOneItemAreOneTerm()
    {
        var terms = AuctionSniperAnalyzer.WatchTermsFromSales(
        [
            Sale("Bitmain Antminer S19j Pro 104TH Miner"),
            Sale("Antminer S19j Pro 104TH ASIC Miner Bitcoin"),
            Sale("BITMAIN ANTMINER S19J PRO 104TH"),
        ], max: 5);

        var term = Assert.Single(terms);
        Assert.Equal(3, term.SalesBehindIt);
        Assert.Equal("You've sold 3 of these", term.Reason);
        Assert.Equal("sold", term.Source);
    }

    [Fact]
    public void WatchTerms_RankedByHowOftenTheSellerHasActuallySoldIt()
    {
        var terms = AuctionSniperAnalyzer.WatchTermsFromSales(
        [
            Sale("Dyson V11 Torque Drive Cordless Vacuum"),
            Sale("Bitmain Antminer S19j Pro 104TH Miner"),
            Sale("Antminer S19j Pro 104TH ASIC Miner"),
        ], max: 5);

        Assert.Equal(2, terms.Count);
        Assert.Contains("antminer", terms[0].Term);
        Assert.Equal(2, terms[0].SalesBehindIt);
    }

    [Fact]
    public void WatchTerms_ACancelledOrderIsNotEvidenceOfAnything()
    {
        var terms = AuctionSniperAnalyzer.WatchTermsFromSales(
        [
            Sale("Dyson V11 Torque Drive Cordless Vacuum", status: "cancelled"),
            Sale("Bitmain Antminer S19j Pro 104TH Miner"),
        ], max: 5);

        var term = Assert.Single(terms);
        Assert.Contains("antminer", term.Term);
    }

    [Fact]
    public void WatchTerms_TheTermIsAKeyword_NotSomebodysListingCopy()
    {
        var terms = AuctionSniperAnalyzer.WatchTermsFromSales(
            [Sale("🔥 BITMAIN Antminer S19j Pro 104TH Miner EXCELLENT CONDITION Fast Free Ship USA 🔥")], max: 5);

        var term = Assert.Single(terms);
        Assert.DoesNotContain("excellent", term.Term);
        Assert.DoesNotContain("ship", term.Term);
        Assert.Contains("antminer", term.Term);
    }

    [Fact]
    public void WatchTerms_RespectsTheCap()
    {
        var terms = AuctionSniperAnalyzer.WatchTermsFromSales(
        [
            Sale("Dyson V11 Torque Drive Vacuum"),
            Sale("Bitmain Antminer S19j Pro 104TH"),
            Sale("Sony WH-1000XM4 Headphones Black"),
        ], max: 2);

        Assert.Equal(2, terms.Count);
    }

    [Fact]
    public void WatchTerms_NoSalesIsNoTerms_NotABlankSearch()
    {
        Assert.Empty(AuctionSniperAnalyzer.WatchTermsFromSales([], max: 5));
        Assert.Empty(AuctionSniperAnalyzer.WatchTermsFromSales([Sale("Bitmain Antminer S19j Pro")], max: 0));
    }

    [Fact]
    public void ParseTypedTerms_SplitsOnCommasAndNewlines_AndDropsDuplicates()
    {
        var terms = AuctionSniperAnalyzer.ParseTypedTerms("iphone 13, dyson v11\niphone 13\n  ", max: 5);

        Assert.Equal(2, terms.Count);
        Assert.Equal("iphone 13", terms[0].Term);
        Assert.Equal("typed", terms[0].Source);
    }

    [Fact]
    public void ParseTypedTerms_NothingTypedIsNoTerms()
    {
        Assert.Empty(AuctionSniperAnalyzer.ParseTypedTerms(null, max: 5));
        Assert.Empty(AuctionSniperAnalyzer.ParseTypedTerms("   ", max: 5));
    }

    // ── Small things that show up on screen ───────────────────────────────────

    [Theory]
    [InlineData(0, "No time")]
    [InlineData(45, "45m")]
    [InlineData(120, "2h")]
    [InlineData(200, "3h 20m")]
    [InlineData(2880, "2 days")]
    [InlineData(1500, "1d 1h")]
    public void FormatSpan_ReadsLikeAClock(int minutes, string expected) =>
        Assert.Equal(expected, AuctionSniperAnalyzer.FormatSpan(minutes));

    [Fact]
    public void Build_CarriesTheSpeedOfTheMoney()
    {
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now);

        // 14 days to sell plus the pipeline nobody can skip — packing, transit and payout.
        Assert.Equal(14 + DaysToCashEstimator.PipelineDays, row.DaysToCash);
        Assert.True(row.ProfitPerDay > 0);
    }

    [Fact]
    public void Build_TheTermThatFoundItIsCarried()
    {
        var term = new SnipeWatchTerm { Term = "antminer s19j pro", LookupTitle = Product, Source = "sold" };
        var row = Sniper.Build(Item(60m, Now.AddMinutes(30)), Pricing(), Fees, Now, term);

        Assert.Equal("antminer s19j pro", row.FoundBy);
        Assert.Equal(Product, row.PricedAs);
    }
}
