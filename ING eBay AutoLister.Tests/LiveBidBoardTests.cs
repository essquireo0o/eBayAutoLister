using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Holding the comps behind a live lot is a cache, and a cache in front of a number somebody is
// about to bet money on has exactly two ways to hurt them: it can serve something stale without
// saying so, or it can serve a DIFFERENT answer from the one a fresh read would have given. The
// second is the one pinned hardest here — a re-priced card is not a cheaper approximation of a
// fresh card, it is the same computation over the same sold history.
public class LiveBidBoardTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly JackpotHunter Hunter = new(Profit);
    private static readonly LiveBidAdvisor Advisor = new(Profit, Hunter);
    private static readonly FeeProfile Fees = new();
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";

    /// <summary>An analysis in the shape the pipeline produces — the board stores this object
    /// untouched, so the tests hand it the same thing the endpoint does.</summary>
    private static MarketAnalysisResult Analysis(decimal expected = 200m, int comps = 8)
    {
        return new MarketAnalysisResult
        {
            PriceEstimate = new PriceEstimate
            {
                MedianPrice = expected,
                ExpectedSalePrice = expected,
                QuickSalePrice = expected * 0.85m,
                Percentile25 = 170m,
                Percentile75 = 240m,
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
                    ItemId = "c1", Title = Product, SoldPrice = 195m, TotalPrice = 195m,
                    Condition = "Used", SoldDate = Now.AddDays(-9), ItemUrl = "https://www.ebay.com/itm/c1",
                },
            ],
        };
    }

    private static LiveBidRequest Ask(decimal? bid = null, decimal? fee = null, decimal? shipping = null, decimal? target = null) =>
        new() { Title = Product, CurrentBid = bid, BuyerFeePercent = fee, ShippingCost = shipping, TargetRoiPercent = target };

    // ── The point of the whole thing ──────────────────────────────────────────

    /// <summary>
    /// The one property that makes holding comps legitimate rather than a shortcut: at the same
    /// inputs, a card built from a held quote is the same card, number for number. If a re-price
    /// could differ from a fresh read the app would hold two opinions about one item and the bidder
    /// would have none — which is the thing every other board in this app is careful not to do.
    /// </summary>
    [Fact]
    public void A_reprice_from_held_comps_is_the_same_card_as_a_fresh_one()
    {
        var analysis = Analysis();
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, analysis, category: null, Now);

        var fresh = Advisor.Build(Product, analysis, Ask(bid: 40m, fee: 8m, shipping: 12m), Fees, null, Now);
        var held = board.Find(quote.Token, Now)!;
        var repriced = Advisor.Build(held.Item, held.Analysis, Ask(bid: 40m, fee: 8m, shipping: 12m), Fees, held.Category, Now);

        Assert.Equal(fresh.MaxBid, repriced.MaxBid);
        Assert.Equal(fresh.BreakEvenBid, repriced.BreakEvenBid);
        Assert.Equal(fresh.Headroom, repriced.Headroom);
        Assert.Equal(fresh.LandedCostNow, repriced.LandedCostNow);
        Assert.Equal(fresh.ProfitAtMaxBid, repriced.ProfitAtMaxBid);
        Assert.Equal(fresh.Call, repriced.Call);
        Assert.Equal(fresh.CallLabel, repriced.CallLabel);
        Assert.Equal(fresh.Reason, repriced.Reason);
        Assert.Equal(fresh.ResalePrice, repriced.ResalePrice);
        Assert.Equal(fresh.CompCount, repriced.CompCount);
    }

    /// <summary>
    /// The bid is the thing that moves, so the ceiling under it must not. A held quote re-priced at
    /// four climbing bids gives one ceiling and four headrooms — the moment the ceiling moved with
    /// the bid, the card would be chasing the auction rather than bounding it.
    /// </summary>
    [Fact]
    public void The_ceiling_does_not_move_as_the_bid_climbs()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);
        var held = board.Find(quote.Token, Now)!;

        var cards = new[] { 10m, 30m, 55m, 90m }
            .Select(bid => Advisor.Build(held.Item, held.Analysis, Ask(bid), Fees, held.Category, Now))
            .ToList();

        Assert.Single(cards.Select(c => c.MaxBid).Distinct());
        Assert.Equal(cards[0].MaxBid - 10m, cards[0].Headroom);
        Assert.Equal(cards[3].MaxBid - 90m, cards[3].Headroom);
    }

    /// <summary>
    /// And the call flips exactly where the ceiling is, not near it. This is the go/no-go a bidder
    /// reads in the second before they raise their hand.
    /// </summary>
    [Fact]
    public void The_call_turns_to_stop_the_dollar_the_bid_passes_the_ceiling()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);
        var held = board.Find(quote.Token, Now)!;

        var ceiling = Advisor.Build(held.Item, held.Analysis, Ask(bid: 1m), Fees, held.Category, Now).MaxBid;

        var at = Advisor.Build(held.Item, held.Analysis, Ask(ceiling), Fees, held.Category, Now);
        var over = Advisor.Build(held.Item, held.Analysis, Ask(ceiling + 0.01m), Fees, held.Category, Now);

        Assert.NotEqual(LiveBidCalls.Stop, at.Call);
        Assert.Equal(LiveBidCalls.Stop, over.Call);
    }

    /// <summary>
    /// Moving the target return is instant too, and that is the more valuable half: the seller can
    /// see what a thinner margin would let them bid while the lot is still on screen.
    /// </summary>
    /// <remarks>
    /// Priced at $2,000, because the ceiling has two bars and only one of them moves with the
    /// target. On a $200 item the $100 cash floor binds at 15% and at 60% alike, so the ceiling is
    /// the same number both times — correct, and the reason this test would be meaningless there.
    /// </remarks>
    [Fact]
    public void A_thinner_target_raises_the_ceiling_off_the_same_held_comps()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(expected: 2000m), null, Now);
        var held = board.Find(quote.Token, Now)!;

        var strict = Advisor.Build(held.Item, held.Analysis, Ask(target: 60m), Fees, held.Category, Now);
        var loose = Advisor.Build(held.Item, held.Analysis, Ask(target: 15m), Fees, held.Category, Now);

        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, strict.CeilingBoundBy);
        Assert.Equal(AuctionSniperAnalyzer.CeilingByRoi, loose.CeilingBoundBy);
        Assert.True(loose.MaxBid > strict.MaxBid);
        // Same comps underneath, so the resale price has to be identical — only the bar moved.
        Assert.Equal(strict.ResalePrice, loose.ResalePrice);
    }

    /// <summary>
    /// And the other bar does not move with it. On a cheap item the ceiling is set by the cash floor
    /// — the hour spent finding, listing and packing costs the same whatever the thing cost — so
    /// sweeping the target return there changes nothing, and the card says which bar bound.
    /// </summary>
    [Fact]
    public void On_a_cheap_lot_the_cash_floor_binds_and_the_target_cannot_move_it()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(expected: 200m), null, Now);
        var held = board.Find(quote.Token, Now)!;

        var strict = Advisor.Build(held.Item, held.Analysis, Ask(target: 60m), Fees, held.Category, Now);
        var loose = Advisor.Build(held.Item, held.Analysis, Ask(target: 15m), Fees, held.Category, Now);

        Assert.Equal(AuctionSniperAnalyzer.CeilingByCash, loose.CeilingBoundBy);
        Assert.Equal(strict.MaxBid, loose.MaxBid);
    }

    // ── What the board will not do ────────────────────────────────────────────

    /// <summary>
    /// Past its life a token stops resolving. The endpoint turns that into "press Price it", which
    /// is the only honest answer — the alternative is a ceiling built on sold history of unstated
    /// age, and the age is the thing that makes it evidence.
    /// </summary>
    [Fact]
    public void A_quote_older_than_the_hold_is_gone()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);

        Assert.NotNull(board.Find(quote.Token, Now + LiveBidBoard.HoldFor - TimeSpan.FromSeconds(1)));
        Assert.Null(board.Find(quote.Token, Now + LiveBidBoard.HoldFor));
        Assert.Null(board.Find(quote.Token, Now + LiveBidBoard.HoldFor + TimeSpan.FromHours(2)));
    }

    /// <summary>An expired quote is not just hidden, it is dropped — a board that only stopped
    /// SHOWING stale quotes would grow all night on a stream that ran all night.</summary>
    [Fact]
    public void Expired_quotes_are_swept_rather_than_merely_hidden()
    {
        var board = new LiveBidBoard();
        board.Hold(Product, Analysis(), null, Now);
        board.Hold("Something else", Analysis(), null, Now);
        Assert.Equal(2, board.Count);

        board.Hold("A third lot", Analysis(), null, Now + LiveBidBoard.HoldFor);

        Assert.Equal(1, board.Count);
    }

    /// <summary>
    /// A live sale runs a queue, so the board holds several lots — but bounded, and the lot on
    /// screen is always the newest, so the oldest is what goes.
    /// </summary>
    [Fact]
    public void The_board_holds_a_queue_of_lots_and_drops_the_oldest_first()
    {
        var board = new LiveBidBoard();
        var first = board.Hold("Lot 1", Analysis(), null, Now);
        var tokens = new List<string>();

        for (var i = 2; i <= LiveBidBoard.Capacity + 1; i++)
            tokens.Add(board.Hold($"Lot {i}", Analysis(), null, Now.AddSeconds(i)).Token);

        Assert.Equal(LiveBidBoard.Capacity, board.Count);
        Assert.Null(board.Find(first.Token, Now));
        Assert.NotNull(board.Find(tokens[^1], Now));
    }

    /// <summary>A token nobody issued resolves to nothing. Said out loud because the alternative —
    /// any string being accepted — is how a re-price would end up pricing against an empty
    /// analysis.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-token")]
    public void An_unknown_token_holds_nothing(string? token)
    {
        var board = new LiveBidBoard();
        board.Hold(Product, Analysis(), null, Now);

        Assert.Null(board.Find(token, Now));
    }

    /// <summary>The token carries the item it was issued for, so the endpoint can refuse to price a
    /// different one against it. Held comps are an answer about a particular lot.</summary>
    [Fact]
    public void A_quote_remembers_which_item_it_priced()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);

        Assert.Equal(Product, board.Find(quote.Token, Now)!.Item);
    }

    /// <summary>The analysis is handed back exactly as it went in. The board is storage, not a
    /// second opinion — nothing here rounds, trims or re-scores what the pipeline produced.</summary>
    [Fact]
    public void The_board_stores_the_analysis_untouched()
    {
        var analysis = Analysis();
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, analysis, null, Now);

        Assert.Same(analysis, board.Find(quote.Token, Now)!.Analysis);
    }

    /// <summary>Releasing is what the screen does when the item changes under it.</summary>
    [Fact]
    public void A_released_quote_stops_resolving()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);

        Assert.True(board.Release(quote.Token));
        Assert.Null(board.Find(quote.Token, Now));
        Assert.False(board.Release(quote.Token));
    }

    /// <summary>The age is what the card prints, so it is whole seconds and never negative — a
    /// clock that steps backwards must not produce "read -3s ago".</summary>
    [Fact]
    public void The_age_of_a_quote_is_whole_seconds_and_never_negative()
    {
        var board = new LiveBidBoard();
        var quote = board.Hold(Product, Analysis(), null, Now);

        Assert.Equal(0, quote.AgeSeconds(Now));
        Assert.Equal(90, quote.AgeSeconds(Now.AddSeconds(90)));
        Assert.Equal(0, quote.AgeSeconds(Now.AddSeconds(-30)));
    }

    /// <summary>Tokens are not guessable and not sequential. The board is per-machine, but a
    /// predictable handle onto somebody's market data is not a thing to hand out on purpose.</summary>
    [Fact]
    public void Tokens_are_unique_and_not_sequential()
    {
        var board = new LiveBidBoard();
        var tokens = Enumerable.Range(0, 5).Select(i => board.Hold($"Lot {i}", Analysis(), null, Now).Token).ToList();

        Assert.Equal(5, tokens.Distinct().Count());
        Assert.All(tokens, t => Assert.Equal(32, t.Length));
    }
}
