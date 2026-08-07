using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The live card has always compared the bid ON SCREEN against the ceiling, which answers whether the
// last bid was all right. Nobody buys at that price: pressing bid commits to the next increment. So a
// lot showing a dollar of room can have no press left that stays inside the ceiling, and until this
// existed nothing on the screen could tell the two apart.
//
// What is pinned here is the direction every approximation leans. The increment is an assumption, and
// an assumption about how much a press costs can only be wrong in two ways: too small, which promises
// presses that will never be offered, or too large, which gives up a lot there will be another of in
// four minutes. Everything below rounds towards the second one.
public class LiveBidIncrementTests
{
    // ── The assumed ladder ────────────────────────────────────────────────────

    /// <summary>
    /// A live sale goes up in dollars at $12 and in twenties at $600. A fixed step is wrong at one
    /// end and useless at the other, so the assumption is a ladder — and it is the ladder the bid
    /// stepper has always used, because the + button has to land on the number this file calls the
    /// next bid.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(12, 1)]
    [InlineData(24.99, 1)]
    [InlineData(25, 5)]
    [InlineData(99.99, 5)]
    [InlineData(100, 10)]
    [InlineData(499.99, 10)]
    [InlineData(500, 25)]
    [InlineData(1999.99, 25)]
    [InlineData(2000, 100)]
    [InlineData(9999, 100)]
    public void The_assumed_step_grows_with_the_price(decimal bid, decimal expected) =>
        Assert.Equal(expected, LiveBidIncrement.Assumed(bid));

    /// <summary>A bid cannot be negative and the ladder does not have to know that — it clamps
    /// rather than falling off the bottom of its own switch.</summary>
    [Fact]
    public void A_negative_bid_reads_as_the_bottom_rung() =>
        Assert.Equal(1m, LiveBidIncrement.Assumed(-40m));

    // ── Whose number it is ────────────────────────────────────────────────────

    /// <summary>
    /// The seller is looking at a screen that states the next bid amount and this app is not. A
    /// typed figure is used exactly as typed — never averaged with the ladder, never overridden by
    /// it.
    /// </summary>
    [Fact]
    public void A_stated_increment_outranks_the_ladder()
    {
        var (increment, source) = LiveBidIncrement.Sanitize(2.50m, bid: 600m);

        Assert.Equal(2.50m, increment);
        Assert.Equal(LiveBidIncrement.SourceSeller, source);
        // The ladder would have said 25 at that level. It does not get a vote.
        Assert.Equal(25m, LiveBidIncrement.Assumed(600m));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-5d)]
    public void Nothing_stated_falls_back_to_the_ladder(double? stated)
    {
        var (increment, source) = LiveBidIncrement.Sanitize((decimal?)stated, bid: 120m);

        Assert.Equal(10m, increment);
        Assert.Equal(LiveBidIncrement.SourceAssumed, source);
    }

    /// <summary>
    /// Clamped rather than rejected, like the buyer's premium: somebody typing the bid into the step
    /// box should cost a wrong number they can see on the card, not the whole answer.
    /// </summary>
    [Fact]
    public void An_absurd_increment_is_clamped_and_still_counts_as_the_sellers()
    {
        var (increment, source) = LiveBidIncrement.Sanitize(999_999m, bid: 40m);

        Assert.Equal(LiveBidIncrement.MaxStatedIncrement, increment);
        Assert.Equal(LiveBidIncrement.SourceSeller, source);
    }

    [Fact]
    public void A_stated_increment_is_held_to_the_cent() =>
        Assert.Equal(2.57m, LiveBidIncrement.Sanitize(2.5678m, bid: 40m).Increment);

    // ── Counting the presses ──────────────────────────────────────────────────

    [Fact]
    public void The_count_is_the_presses_that_land_at_or_under_the_ceiling()
    {
        // 45, 50, 55, 60 — and the first one past.
        var (count, capped, firstOver) = LiveBidIncrement.CountBids(from: 40m, ceiling: 60m, increment: 5m);

        Assert.Equal(4, count);
        Assert.False(capped);
        Assert.Equal(65m, firstOver);
    }

    /// <summary>
    /// A press that lands exactly on the ceiling is a press worth making — the ceiling is the
    /// highest bid that still clears the target, not the first one that does not.
    /// </summary>
    [Fact]
    public void A_press_that_lands_exactly_on_the_ceiling_counts()
    {
        var (count, _, firstOver) = LiveBidIncrement.CountBids(from: 40m, ceiling: 45m, increment: 5m);

        Assert.Equal(1, count);
        Assert.Equal(50m, firstOver);
    }

    /// <summary>And one cent over does not. This is the whole feature in one assertion.</summary>
    [Fact]
    public void A_press_a_cent_over_the_ceiling_does_not_count()
    {
        var (count, _, firstOver) = LiveBidIncrement.CountBids(from: 40m, ceiling: 44.99m, increment: 5m);

        Assert.Equal(0, count);
        Assert.Equal(45m, firstOver);
    }

    /// <summary>
    /// The step is not constant, so the count is walked rather than divided. A show's increments grow
    /// with the price, and dividing the room by the increment in hand over-counts every lot whose
    /// ceiling sits above the next rung of the ladder.
    /// </summary>
    [Fact]
    public void The_ladder_used_after_the_first_press_grows_with_the_price()
    {
        // $1 steps from $20 — but the moment the bidding passes $25 a live show is going up in
        // fives, so the real answer is 12 presses and not the 40 the division would report.
        var (count, capped, _) = LiveBidIncrement.CountBids(from: 20m, ceiling: 60m, increment: 1m);

        Assert.Equal(12, count);
        Assert.False(capped);
        Assert.True(count < (60m - 20m) / 1m, "the flat division would have promised presses that do not exist");
    }

    /// <summary>
    /// A step the seller typed is held flat instead, at every level. They are watching the show and
    /// this app is not, so the ladder does not get to talk their number upwards — a $1 show stays a
    /// $1 show at $71, whatever the convention says.
    /// </summary>
    [Fact]
    public void A_stated_step_is_held_flat_all_the_way_up()
    {
        // The assumed ladder would have gone to $5 the moment the bidding passed $25 and reported
        // one press. The seller said dollars.
        var (count, _, firstOver) = LiveBidIncrement.CountBids(
            from: 70m, ceiling: 73.10m, increment: 1m, stated: true);

        Assert.Equal(3, count);   // 71, 72, 73
        Assert.Equal(74m, firstOver);

        Assert.Equal(1, LiveBidIncrement.CountBids(from: 70m, ceiling: 73.10m, increment: 1m).Count);
    }

    /// <summary>Larger than the ladder, and equally untouched.</summary>
    [Fact]
    public void A_stated_step_larger_than_the_ladder_is_kept_too()
    {
        var (count, _, firstOver) = LiveBidIncrement.CountBids(
            from: 40m, ceiling: 120m, increment: 20m, stated: true);

        Assert.Equal(4, count);   // 60, 80, 100, 120
        Assert.Equal(140m, firstOver);
    }

    /// <summary>
    /// Which is safe because the count is not what anybody acts on: the NEXT press is costed exactly
    /// either way, and the whole card is re-answered every time the bid moves.
    /// </summary>
    [Fact]
    public void The_next_press_itself_is_the_same_whichever_way_the_rest_is_counted()
    {
        Assert.Equal(
            LiveBidIncrement.CountBids(from: 70m, ceiling: 71m, increment: 1m, stated: true).Count,
            LiveBidIncrement.CountBids(from: 70m, ceiling: 71m, increment: 1m, stated: false).Count);
    }

    /// <summary>
    /// A ceiling a hundred presses above the bid is not a decision anybody makes one press at a
    /// time. Counting stops, the card says "40+", and the room figure carries the rest.
    /// </summary>
    [Fact]
    public void Counting_stops_rather_than_walking_forever()
    {
        var (count, capped, _) = LiveBidIncrement.CountBids(
            from: 0m, ceiling: 100_000m, increment: 0.01m, stated: true);

        Assert.Equal(LiveBidIncrement.MaxBidsCounted, count);
        Assert.True(capped);
    }

    /// <summary>
    /// Never reachable from <see cref="LiveBidIncrement.Sanitize"/>, which cannot return a
    /// non-positive step. Guarded anyway, because this loop is the one place on the live path where
    /// a zero would not produce a wrong number — it would produce no answer at all.
    /// </summary>
    [Fact]
    public void A_zero_increment_terminates_rather_than_hanging_the_card()
    {
        var (count, capped, firstOver) = LiveBidIncrement.CountBids(from: 40m, ceiling: 60m, increment: 0m);

        Assert.Equal(0, count);
        Assert.False(capped);
        Assert.Equal(40m, firstOver);
    }

    /// <summary>
    /// The property the whole block stands on: whatever the numbers, walking the reported presses
    /// never ends above the ceiling. A count that was one too generous would be this screen telling
    /// somebody to spend money it had just finished proving they should not.
    /// </summary>
    [Fact]
    public void No_reported_press_ever_lands_above_the_ceiling()
    {
        foreach (var from in new[] { 0m, 3m, 24m, 25m, 99m, 100m, 480m, 1_990m, 2_400m })
        foreach (var room in new[] { 0m, 0.99m, 1m, 4.99m, 12m, 60m, 250m })
        foreach (var increment in new[] { 0.5m, 1m, 2.5m, 5m, 20m })
        foreach (var stated in new[] { true, false })
        {
            var ceiling = from + room;
            var (count, capped, firstOver) = LiveBidIncrement.CountBids(from, ceiling, increment, stated);

            var at = from;
            var step = increment;
            for (var i = 0; i < count; i++)
            {
                at = Math.Round(at + step, 2);
                step = stated ? increment : LiveBidIncrement.Assumed(at);
            }

            Assert.True(at <= ceiling,
                $"{count} presses from {from} at {increment} reached {at}, over the {ceiling} ceiling");
            if (!capped)
                Assert.True(firstOver > ceiling, $"the press after the last one, {firstOver}, is not past {ceiling}");
        }
    }

    // ── What the card is told ─────────────────────────────────────────────────

    private static LiveBidCard Priced(
        decimal bid, decimal maxBid, decimal fee = 0m, decimal shipping = 0m, bool bidKnown = true) =>
        new()
        {
            CurrentBid = bid,
            BidWasKnown = bidKnown,
            MaxBid = maxBid,
            BuyerFeePercent = fee,
            ShippingCost = shipping,
        };

    /// <summary>
    /// The gap this file exists for. The bidding is a dollar under the ceiling, the card says so, and
    /// there is no press that stays inside it — because bids go up in fives.
    /// </summary>
    [Fact]
    public void A_dollar_of_room_and_no_press_that_fits_inside_it()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 46m), breakEvenAllIn: 60m, stated: null);

        Assert.True(read.Readable);
        Assert.Equal(LiveNextBidVerdicts.Stop, read.Verdict);
        Assert.Equal(0, read.BidsLeft);
        Assert.Equal(50m, read.Amount);
        Assert.Contains("Don't press", read.Headline, StringComparison.Ordinal);

        // And it is loud enough to reach the warning list, because the room figure printed further
        // down the same card says the opposite.
        Assert.NotEqual("", read.Warning);
        Assert.Contains(46m.ToString("C"), read.Warning, StringComparison.Ordinal);
        Assert.Contains(50m.ToString("C"), read.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The press that would go over the ceiling can still be a long way under break-even. That is
    /// the point of reporting both: the ceiling is where it stops being worth the work, not where it
    /// starts losing money.
    /// </summary>
    [Fact]
    public void A_press_past_the_ceiling_still_reports_what_it_would_make()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 46m), breakEvenAllIn: 60m, stated: null);

        Assert.Equal(10m, read.Profit);   // 60 break-even − 50 landed
    }

    /// <summary>
    /// The state nothing on this screen has ever reported, and the one a live bidder most needs: one
    /// press left. It names the press after it, because "why is this the last one" is answered by a
    /// number the seller can check against the show.
    /// </summary>
    [Fact]
    public void The_last_press_says_so_and_names_the_one_after_it()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 50m), breakEvenAllIn: 90m, stated: null);

        Assert.Equal(LiveNextBidVerdicts.Last, read.Verdict);
        Assert.Equal(1, read.BidsLeft);
        Assert.Equal(50m, read.Amount);
        Assert.Equal("Last bid", read.Headline);
        Assert.Contains(55m.ToString("C"), read.Note, StringComparison.Ordinal);
        // Not a warning. It is a bid the seller may still make, and the strip is already amber.
        Assert.Equal("", read.Warning);
    }

    [Fact]
    public void More_than_one_press_is_reported_as_a_count()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 70m), breakEvenAllIn: 120m, stated: null);

        Assert.Equal(LiveNextBidVerdicts.Press, read.Verdict);
        Assert.Equal(5, read.BidsLeft);   // 50, 55, 60, 65, 70
        Assert.False(read.BidsLeftCapped);
        Assert.Equal("5 more bids", read.Headline);
        Assert.Equal("", read.Warning);
    }

    [Fact]
    public void A_ceiling_far_above_the_bid_reports_a_capped_count()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 5m, maxBid: 4_000m), breakEvenAllIn: 9_000m, stated: 0.50m);

        Assert.True(read.BidsLeftCapped);
        Assert.Equal(LiveBidIncrement.MaxBidsCounted, read.BidsLeft);
        Assert.Contains($"{LiveBidIncrement.MaxBidsCounted}+", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// Already past it is said without a count. "0 more bids" on a lot that is gone reads as a lot
    /// still in play, and the badge above has already called it a stop.
    /// </summary>
    [Fact]
    public void Past_the_ceiling_is_its_own_answer_and_carries_no_warning()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 80m, maxBid: 70m), breakEvenAllIn: 120m, stated: null);

        Assert.Equal(LiveNextBidVerdicts.Over, read.Verdict);
        Assert.Equal(0, read.BidsLeft);
        Assert.Equal("Already past it", read.Headline);
        Assert.Equal("", read.Warning);
    }

    /// <summary>
    /// Before the first bid there is no next one — the opening price is the host's to name. The
    /// strip still appears saying that, because a block that is silent before the bidding starts and
    /// silent when it cannot read anything is a block whose silence means two things.
    /// </summary>
    [Fact]
    public void Before_the_bidding_starts_nothing_is_costed_and_the_strip_says_why()
    {
        var read = LiveBidIncrement.Read(
            Priced(bid: 0m, maxBid: 70m, bidKnown: false), breakEvenAllIn: 120m, stated: null);

        Assert.False(read.Readable);
        Assert.Equal(LiveNextBidVerdicts.Unreadable, read.Verdict);
        Assert.Equal(0m, read.Amount);
        Assert.Equal("Bidding hasn't started", read.Headline);
        Assert.Contains(70m.ToString("C"), read.Note, StringComparison.Ordinal);

        // And no ladder is asserted for a price nobody has named yet.
        Assert.Equal("", read.IncrementNote);
    }

    /// <summary>
    /// A card with no ceiling gets no strip at all. The badge already says DON'T BID, and counting
    /// presses under a ceiling of zero is arithmetic about a lot nobody should touch.
    /// </summary>
    [Fact]
    public void A_card_with_no_ceiling_renders_nothing()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 0m), breakEvenAllIn: 0m, stated: null);

        Assert.False(read.Readable);
        Assert.Equal("", read.Headline);
        Assert.Equal("", read.Note);
    }

    // ── What it costs ─────────────────────────────────────────────────────────

    /// <summary>
    /// The landed cost of the next bid is the card's own <see cref="LiveBidAdvisor.LandedCost"/>,
    /// not a second assembly of bid-plus-premium-plus-shipping. Two of those in one app is how the
    /// ceiling and the press end up disagreeing about the same dollar.
    /// </summary>
    [Fact]
    public void The_next_bid_is_landed_by_the_cards_own_arithmetic()
    {
        var read = LiveBidIncrement.Read(
            Priced(bid: 45m, maxBid: 70m, fee: 8m, shipping: 12m), breakEvenAllIn: 120m, stated: null);

        Assert.Equal(50m, read.Amount);
        Assert.Equal(LiveBidAdvisor.LandedCost(50m, 8m, 12m), read.Landed);
        Assert.Equal(Math.Round(120m - LiveBidAdvisor.LandedCost(50m, 8m, 12m), 2), read.Profit);
    }

    /// <summary>
    /// Deliberately not clamped at zero, unlike the profit at the ceiling. A negative here is the
    /// figure's whole job: it says this press loses money, and $0.00 would say it makes none.
    /// </summary>
    [Fact]
    public void A_press_that_loses_money_reports_a_negative()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 95m, maxBid: 200m), breakEvenAllIn: 90m, stated: null);

        Assert.Equal(100m, read.Amount);
        Assert.Equal(-10m, read.Profit);
    }

    /// <summary>
    /// An assumption nobody can see is one nobody can correct, so the figure is stated on every
    /// readable card along with the way to overrule it.
    /// </summary>
    [Fact]
    public void The_assumed_step_is_stated_back_with_the_way_to_overrule_it()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 70m), breakEvenAllIn: 120m, stated: null);

        Assert.Equal(LiveBidIncrement.SourceAssumed, read.IncrementSource);
        Assert.Contains("Assuming", read.IncrementNote, StringComparison.Ordinal);
        Assert.Contains(5m.ToString("C"), read.IncrementNote, StringComparison.Ordinal);
        Assert.Contains("Bid step", read.IncrementNote, StringComparison.Ordinal);
    }

    /// <summary>The seller's own figure is echoed instead — a number they typed that quietly
    /// outranks the app's, shown where they can see it did.</summary>
    [Fact]
    public void A_stated_step_is_echoed_as_theirs()
    {
        var read = LiveBidIncrement.Read(Priced(bid: 45m, maxBid: 70m), breakEvenAllIn: 120m, stated: 1m);

        Assert.Equal(LiveBidIncrement.SourceSeller, read.IncrementSource);
        Assert.Equal(46m, read.Amount);
        Assert.Contains("as you typed it", read.IncrementNote, StringComparison.Ordinal);
        Assert.DoesNotContain("Assuming", read.IncrementNote, StringComparison.Ordinal);
    }
}
