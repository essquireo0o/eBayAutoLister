using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A show's lot list is priced, and then it has to be put in an order. That order is the whole
// output of the feature: it is the app saying "of the next dozen, THIS is the one to be here for",
// and the seller will act on the top of it without reading the rest.
//
// So the two things pinned here are what the order is made of, and what it can never do. It is made
// of what a lot is worth at its own ceiling — not of how much room is left above the current bid,
// which shrinks every time somebody else bids and would reshuffle the list under the seller while
// they read it. And no amount of money lifts a lot the app said STOP to above one it said BID to.
public class LiveLotRankTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly JackpotHunter Hunter = new(Profit);
    private static readonly LiveBidAdvisor Advisor = new(Profit, Hunter);
    private static readonly FeeProfile Fees = new();
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    // ── The order the calls come in ───────────────────────────────────────────

    /// <summary>
    /// The call decides first. A lot the app has already said not to bid on does not belong above
    /// one it would, however much money is nominally in it — the list answers "be here for this",
    /// and being present for a lot you have been told to walk away from is worth nothing.
    /// </summary>
    [Fact]
    public void The_call_outranks_the_money_every_time()
    {
        var richStop = LiveBidAdvisor.RankLot(LiveBidCalls.Stop, 900_000m);
        var poorBid = LiveBidAdvisor.RankLot(LiveBidCalls.Bid, 1m);
        var poorRisky = LiveBidAdvisor.RankLot(LiveBidCalls.Risky, 1m);

        Assert.True(poorBid > poorRisky);
        Assert.True(poorRisky > richStop);
    }

    /// <summary>
    /// A stop sits above a no-data lot. A stop had sold history behind it and the app reached a
    /// conclusion; a no-data lot is not a bad opportunity, it is an absence of one.
    /// </summary>
    [Fact]
    public void A_stop_still_outranks_a_lot_nothing_could_price()
    {
        Assert.True(LiveBidAdvisor.RankLot(LiveBidCalls.Stop, 0m)
                  > LiveBidAdvisor.RankLot(LiveBidCalls.NoData, 999_999m));
    }

    /// <summary>An unrecognised call ranks with the ones nothing could price, rather than landing
    /// somewhere in the middle of the list by accident.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("something_new")]
    public void A_call_this_does_not_know_ranks_bottom(string? call)
    {
        Assert.Equal(LiveBidAdvisor.RankLot(LiveBidCalls.NoData, 0m), LiveBidAdvisor.RankLot(call, 0m));
    }

    // ── The money, within a call ──────────────────────────────────────────────

    [Fact]
    public void Within_one_call_the_bigger_money_ranks_higher()
    {
        Assert.True(LiveBidAdvisor.RankLot(LiveBidCalls.Bid, 240m)
                  > LiveBidAdvisor.RankLot(LiveBidCalls.Bid, 60m));
    }

    /// <summary>
    /// The tier gap is wider than any profit the ranking will consider, so no profit can ever carry
    /// a lot across a call boundary. That is the property the whole ordering rests on, and it is one
    /// arithmetic mistake away from being false.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999_998)]
    [InlineData(999_999)]
    [InlineData(4_000_000)]   // clamped — a profit larger than the tier step cannot climb a tier
    public void No_profit_can_climb_a_tier(decimal profit)
    {
        Assert.True(LiveBidAdvisor.RankLot(LiveBidCalls.Bid, 0m)
                  > LiveBidAdvisor.RankLot(LiveBidCalls.Risky, profit));
        Assert.True(LiveBidAdvisor.RankLot(LiveBidCalls.Risky, 0m)
                  > LiveBidAdvisor.RankLot(LiveBidCalls.Stop, profit));
    }

    // ── What it is measured on ────────────────────────────────────────────────

    /// <summary>
    /// Ranked on what the lot is worth, not on how much room is left above the bid. Room shrinks
    /// every few seconds as the bidding climbs; the lot's worth does not. A list ordered by room
    /// would reshuffle itself while the seller was reading it, and would put a lot nobody had bid on
    /// yet above a better one that happened to be halfway through its bidding.
    /// </summary>
    [Fact]
    public void The_rank_does_not_move_as_the_bidding_climbs()
    {
        var analysis = Analysis();
        var cheap = Advisor.Build("Antminer S19j Pro", analysis, Ask(0m), Fees, nowUtc: Now);
        var bidUp = Advisor.Build("Antminer S19j Pro", analysis, Ask(60m), Fees, nowUtc: Now);

        Assert.True(bidUp.Headroom < cheap.Headroom);          // the room shrank
        Assert.Equal(cheap.LotRank, bidUp.LotRank);            // the lot is worth what it was
    }

    // ── On the card ───────────────────────────────────────────────────────────

    /// <summary>Every card carries a rank, including the ones nothing could price — a row with no
    /// rank would sort into whatever position the browser's comparator happened to leave it in.</summary>
    [Fact]
    public void Every_card_carries_a_rank_including_the_ones_that_could_not_be_priced()
    {
        var priced = Advisor.Build("Antminer S19j Pro", Analysis(), Ask(0m), Fees, nowUtc: Now);
        var unpriced = Advisor.Build("Antminer S19j Pro", null, Ask(0m), Fees, nowUtc: Now);

        Assert.Equal(LiveBidCalls.NoData, unpriced.Call);
        Assert.Equal(LiveBidAdvisor.RankLot(unpriced.Call, unpriced.ProfitAtMaxBid), unpriced.LotRank);
        Assert.Equal(LiveBidAdvisor.RankLot(priced.Call, priced.ProfitAtMaxBid), priced.LotRank);
        Assert.True(priced.LotRank > unpriced.LotRank);
    }

    /// <summary>
    /// A re-price off held comps is the same card as a fresh one, and the rank is part of "the same
    /// card". A rank that changed on re-price would re-order the list every time the seller touched
    /// the bid box.
    /// </summary>
    [Fact]
    public void A_reprice_carries_the_same_rank_as_the_fresh_card()
    {
        var board = new LiveBidBoard();
        var analysis = Analysis();
        var fresh = Advisor.Build("Antminer S19j Pro", analysis, Ask(40m), Fees, nowUtc: Now);
        var quote = board.Hold("Antminer S19j Pro", analysis, null, Now);

        var held = board.Find(quote.Token, Now.AddMinutes(2));
        Assert.NotNull(held);
        var reprice = Advisor.Build(held!.Item, held.Analysis, Ask(55m), Fees, nowUtc: Now.AddMinutes(2));

        Assert.Equal(fresh.LotRank, reprice.LotRank);
    }

    /// <summary>
    /// A better lot outranks a worse one when both come off the same pipeline — the property the
    /// screen actually depends on, asserted through <c>Build</c> rather than through the raw
    /// function, so a change to what "worth" means on a card is caught here too.
    /// </summary>
    [Fact]
    public void The_lot_worth_more_ends_up_higher_on_the_list()
    {
        var rich = Advisor.Build("Antminer S19j Pro", Analysis(expected: 600m), Ask(0m), Fees, nowUtc: Now);
        var thin = Advisor.Build("Goldshell Mini Doge II", Analysis(expected: 90m), Ask(0m), Fees, nowUtc: Now);

        Assert.True(rich.ProfitAtMaxBid > thin.ProfitAtMaxBid);
        Assert.True(rich.LotRank > thin.LotRank);
    }

    // ── fixture ───────────────────────────────────────────────────────────────

    private static LiveBidRequest Ask(decimal? bid) =>
        new() { Title = "Antminer S19j Pro", CurrentBid = bid };

    /// <summary>An analysis in the shape the pipeline produces one.</summary>
    private static MarketAnalysisResult Analysis(decimal expected = 200m, int comps = 8) =>
        new()
        {
            PriceEstimate = new PriceEstimate
            {
                MedianPrice = expected,
                ExpectedSalePrice = expected,
                QuickSalePrice = expected * 0.85m,
                Percentile25 = expected * 0.85m,
                Percentile75 = expected * 1.2m,
                PricedOnCompCount = comps,
                IdentityVerified = true,
                LocalNewestSoldAtUtc = Now.AddDays(-9),
                LocalOldestSoldAtUtc = Now.AddDays(-60),
            },
            SellThrough = new SellThroughAnalysis
            {
                SoldComparableCount = comps,
                ActiveComparableCount = 10,
                SellThroughRate = 80m,
                SellThroughScore = 72,
                Interpretation = "Very Strong",
                EstimatedMonthlySales = 4m,
                EstimatedDaysToSell = 14,
                LiquidityLevel = "Fast Mover",
            },
            Confidence = new ConfidenceBreakdown { Score = 70, Level = "Good" },
            Sources = new SourceBreakdown
            {
                LocalComparableCount = comps,
                PricedOnCompCount = comps,
                IdentityVerified = true,
            },
            TopSoldComparables =
            [
                new MarketplaceComparableResult
                {
                    ItemId = "c1", Title = "Antminer S19j Pro", SoldPrice = expected,
                    TotalPrice = expected, Condition = "Used", SoldDate = Now.AddDays(-9),
                    ItemUrl = "https://www.ebay.com/itm/c1",
                },
            ],
        };
}
