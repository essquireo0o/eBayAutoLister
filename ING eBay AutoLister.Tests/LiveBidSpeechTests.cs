using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The one line the WhatsNot screen reads out loud, and the one a lot row is announced as.
///
/// It is a sentence about money, so the risk it carries is not being ugly — it is saying a number
/// the card underneath it does not have, or saying the optimistic version of one it does. What is
/// pinned here is that every figure rounds AGAINST the bidder, that a missing number is a missing
/// clause rather than a zero, that a sell-through rate with no denominator is never spoken, and that
/// the ceiling in the line is the badge's own and not a second rendering of it.
/// </summary>
public class LiveBidSpeechTests
{
    private static LiveBidCard Card(
        string call = LiveBidCalls.Bid, string label = "BID UP TO $240",
        decimal maxBid = 240m, decimal currentBid = 120m, bool bidKnown = true,
        decimal headroom = 120m, decimal? resale = 310m, decimal? sellThrough = 72m,
        bool unbounded = false, int comps = 14) => new()
    {
        Call = call,
        CallLabel = label,
        MaxBid = maxBid,
        CurrentBid = currentBid,
        BidWasKnown = bidKnown,
        Headroom = headroom,
        ResalePrice = resale,
        SellThroughRate = sellThrough,
        SellThroughUnbounded = unbounded,
        CompCount = comps,
    };

    private static string Cash(decimal v) => v.ToString("C0");

    // ── The whole line ────────────────────────────────────────────────────────

    [Fact]
    public void The_line_is_the_call_then_the_bidding_then_the_resale()
    {
        var said = LiveBidSpeech.Say(Card());

        Assert.Equal(
            $"BID UP TO $240. At {Cash(120)}, {Cash(120)} of room. Resells around {Cash(310)}, 72% sell-through, on 14 comps.",
            said);
    }

    /// <summary>
    /// The ceiling in the line is <see cref="LiveBidCard.CallLabel"/> verbatim, never re-rendered
    /// from <c>MaxBid</c>. Two renderings of one number is how a badge and the sentence under it end
    /// up a dollar apart, and the bidder acts on whichever they read.
    /// </summary>
    [Fact]
    public void The_ceiling_is_the_badges_own_words()
    {
        // A badge that floors (as LiveBidAdvisor.Badge does) against a MaxBid that does not.
        var card = Card(label: "BID UP TO $56", maxBid: 56.57m, currentBid: 10m, headroom: 46.57m);

        var said = LiveBidSpeech.Say(card);

        Assert.StartsWith("BID UP TO $56.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("$57", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_badge_that_already_ends_in_a_full_stop_does_not_get_a_second_one()
    {
        var said = LiveBidSpeech.Say(Card(label: "STOP.", call: LiveBidCalls.Stop));

        Assert.DoesNotContain("..", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LiveBidCalls.Bid, "BID")]
    [InlineData(LiveBidCalls.Risky, "RISKY")]
    [InlineData(LiveBidCalls.Stop, "STOP")]
    [InlineData(LiveBidCalls.NoData, "CAN'T PRICE IT")]
    public void A_card_with_no_badge_still_says_which_of_the_four_it_is(string call, string word)
    {
        var said = LiveBidSpeech.Say(Card(call: call, label: ""));

        Assert.StartsWith(word + ".", said, StringComparison.Ordinal);
    }

    [Fact]
    public void No_card_is_no_line()
    {
        Assert.Equal("", LiveBidSpeech.Say(null));
    }

    // ── Rounding against the bidder ───────────────────────────────────────────

    /// <summary>
    /// Read at a glance in the two seconds before a hand goes up, so every figure is the pessimistic
    /// one: the bid up, the room down. A line that rounds $119.60 of room up to $120 is a line that
    /// says there is more there than there is.
    /// </summary>
    [Fact]
    public void The_room_rounds_down_and_the_bid_rounds_up()
    {
        var said = LiveBidSpeech.Say(Card(currentBid: 120.40m, headroom: 119.60m));

        Assert.Contains($"At {Cash(121)}, {Cash(119)} of room.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void An_overshoot_rounds_up_so_it_is_never_understated()
    {
        var said = LiveBidSpeech.Say(Card(
            call: LiveBidCalls.Stop, label: "STOP", currentBid: 260m, headroom: -20.10m));

        Assert.Contains($"At {Cash(260)} — {Cash(21)} past the ceiling.", said, StringComparison.Ordinal);
    }

    /// <summary>Forty cents of room is not a dollar of room. It rounds to nothing, which is the
    /// honest reading of a bid sitting on the ceiling.</summary>
    [Fact]
    public void Change_left_under_the_ceiling_is_not_rounded_up_into_a_dollar()
    {
        var said = LiveBidSpeech.Say(Card(currentBid: 239m, headroom: 0.40m));

        Assert.Contains($"{Cash(0)} of room.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resale_price_rounds_down_too()
    {
        var said = LiveBidSpeech.Say(Card(resale: 310.90m));

        Assert.Contains($"Resells around {Cash(310)}", said, StringComparison.Ordinal);
    }

    // ── Where the bidding is ──────────────────────────────────────────────────

    [Fact]
    public void A_lot_nobody_has_bid_on_says_so_rather_than_saying_zero()
    {
        var said = LiveBidSpeech.Say(Card(bidKnown: false, currentBid: 0m, headroom: 240m));

        Assert.Contains("Bidding hasn't started.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("of room", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// "DON'T BID" is not "you are $180 over" — there is no ceiling to be over. Speaking one would
    /// invite the reading that some smaller bid works, which is the exact opposite of the call.
    /// </summary>
    [Fact]
    public void With_no_ceiling_there_is_nothing_said_about_room()
    {
        var said = LiveBidSpeech.Say(Card(
            call: LiveBidCalls.Stop, label: "DON'T BID", maxBid: 0m, currentBid: 180m, headroom: -180m));

        Assert.DoesNotContain("of room", said, StringComparison.Ordinal);
        Assert.DoesNotContain("past the ceiling", said, StringComparison.Ordinal);
        Assert.DoesNotContain("At ", said, StringComparison.Ordinal);
        Assert.StartsWith("DON'T BID.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void A_bid_exactly_on_the_ceiling_has_no_room_and_is_not_past_it()
    {
        var said = LiveBidSpeech.Say(Card(currentBid: 240m, headroom: 0m));

        Assert.Contains($"{Cash(0)} of room.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("past the ceiling", said, StringComparison.Ordinal);
    }

    // ── What it refuses to claim ──────────────────────────────────────────────

    /// <summary>
    /// A rate with no active listings under it has no denominator. The card shows "—" for it; the
    /// line has no dash to show, so it says nothing at all rather than the two of them disagreeing
    /// about whether 100% happened.
    /// </summary>
    [Fact]
    public void A_sell_through_with_no_denominator_is_never_spoken()
    {
        var said = LiveBidSpeech.Say(Card(unbounded: true, sellThrough: 100m));

        Assert.DoesNotContain("sell-through", said, StringComparison.Ordinal);
        Assert.DoesNotContain("100%", said, StringComparison.Ordinal);
        Assert.Contains("Resells around", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void A_rate_that_is_not_there_is_not_spoken(int? rate)
    {
        var said = LiveBidSpeech.Say(Card(sellThrough: rate));

        Assert.DoesNotContain("sell-through", said, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void No_resale_price_means_no_resale_clause_rather_than_a_zero(int? resale)
    {
        var said = LiveBidSpeech.Say(Card(resale: resale));

        Assert.DoesNotContain("Resells", said, StringComparison.Ordinal);
        Assert.DoesNotContain(Cash(0), said, StringComparison.Ordinal);
    }

    [Fact]
    public void With_no_comps_counted_the_evidence_clause_is_dropped()
    {
        var said = LiveBidSpeech.Say(Card(comps: 0));

        Assert.DoesNotContain("comp", said, StringComparison.Ordinal);
        Assert.Contains("Resells around", said, StringComparison.Ordinal);
    }

    [Fact]
    public void One_comp_is_a_comp_and_not_one_comps()
    {
        Assert.Contains("on 1 comp.", LiveBidSpeech.Say(Card(comps: 1)), StringComparison.Ordinal);
        Assert.Contains("on 2 comps.", LiveBidSpeech.Say(Card(comps: 2)), StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing was priced, so every clause below the call would be a number this card does not have.
    /// A spoken "$0 of room" on an item with no sold history is the failure the whole screen exists
    /// to avoid.
    /// </summary>
    [Fact]
    public void A_card_that_could_not_be_priced_says_only_that()
    {
        var card = Card(call: LiveBidCalls.NoData, label: "CAN'T PRICE IT",
                        maxBid: 0m, resale: null, sellThrough: null, comps: 0);

        Assert.Equal("CAN'T PRICE IT. No eBay sold history to bid against.", LiveBidSpeech.Say(card));
    }

    /// <summary>Even when stale figures are left on a no-data card by something upstream, none of
    /// them reaches the line.</summary>
    [Fact]
    public void A_no_data_card_carrying_leftover_numbers_still_speaks_none_of_them()
    {
        var card = Card(call: LiveBidCalls.NoData, label: "CAN'T PRICE IT");

        var said = LiveBidSpeech.Say(card);

        Assert.DoesNotContain("room", said, StringComparison.Ordinal);
        Assert.DoesNotContain("Resells", said, StringComparison.Ordinal);
        Assert.DoesNotContain("sell-through", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// The line says nothing about how old the comps are or whether they were re-read. That belongs
    /// to the card's held-comps line — and keeping it out of here is what makes the sentence
    /// identical before and after a re-price that changed nothing, so a screen reader is not handed
    /// a fresh announcement every two seconds for an answer that did not move.
    /// </summary>
    [Fact]
    public void A_reprice_that_changed_nothing_says_exactly_what_it_said_before()
    {
        var fresh = Card();
        var reheld = Card();
        reheld.RepricedFromHeldComps = true;
        reheld.CompsAgeSeconds = 340;
        reheld.Token = "held-token";

        Assert.Equal(LiveBidSpeech.Say(fresh), LiveBidSpeech.Say(reheld));
    }

    // ── Whether there is a press left ─────────────────────────────────────────

    private static LiveBidCard WithPress(string verdict)
    {
        var card = Card(currentBid: 235m, headroom: 5m);
        card.NextBid = new LiveNextBid { Verdict = verdict };
        return card;
    }

    /// <summary>
    /// The one clause that can contradict the one before it. "At $235, $5 of room" is true and is
    /// not permission, because pressing costs $10 more than the room there is.
    /// </summary>
    [Fact]
    public void No_press_left_is_said_immediately_after_the_room()
    {
        var said = LiveBidSpeech.Say(WithPress(LiveNextBidVerdicts.Stop));

        Assert.Contains($"of room. The next bid is past the ceiling — don't press.", said, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_press_is_said_as_the_last_one()
    {
        var said = LiveBidSpeech.Say(WithPress(LiveNextBidVerdicts.Last));

        Assert.Contains("Last bid you can make.", said, StringComparison.Ordinal);
    }

    /// <summary>
    /// Silent in every other state. A count spoken on every card would be a clause on every lot in
    /// exchange for something the room figure already carried — and this line is the only part of
    /// the screen a seller can keep up with during a live sale.
    /// </summary>
    [Theory]
    [InlineData(LiveNextBidVerdicts.Press)]
    [InlineData(LiveNextBidVerdicts.Over)]
    [InlineData(LiveNextBidVerdicts.Unreadable)]
    public void Every_other_state_says_nothing_about_the_press(string verdict)
    {
        var said = LiveBidSpeech.Say(WithPress(verdict));

        Assert.DoesNotContain("press", said, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Last bid", said, StringComparison.Ordinal);
    }

    /// <summary>A card built by something that never set the block at all still speaks.</summary>
    [Fact]
    public void A_card_with_no_press_block_still_produces_a_line()
    {
        var card = Card();
        card.NextBid = null!;

        Assert.Contains("BID UP TO $240.", LiveBidSpeech.Say(card), StringComparison.Ordinal);
    }

    // ── What kind of one it is ────────────────────────────────────────────────

    private static LiveConditionRead Cond(
        string band = LiveConditionBands.Used, bool readable = true, bool discounted = false,
        decimal cut = 0m, int matched = 5, string dominant = LiveConditionBands.New) => new()
    {
        Band = band,
        BandLabel = LiveCondition.Label(band),
        Readable = readable,
        Discounted = discounted,
        CutPercent = cut,
        MatchedComps = matched,
        DominantBand = dominant,
    };

    /// <summary>
    /// The first of the two states this clause speaks in: the badge the seller just heard is lower
    /// than the comps under it, and they are owed the reason in the same breath.
    /// </summary>
    [Fact]
    public void A_ceiling_cut_for_condition_is_said_straight_after_the_trend()
    {
        var card = Card();
        card.Condition = Cond(discounted: true, cut: 38.4m);

        var said = LiveBidSpeech.Say(card);

        Assert.Contains("Priced as used — ceiling already cut 38% for it.", said, StringComparison.Ordinal);
        // Before the bidding, because it changes what the numbers after it mean.
        Assert.True(said.IndexOf("Priced as used", StringComparison.Ordinal)
                    < said.IndexOf("At $120", StringComparison.Ordinal));
    }

    /// <summary>
    /// The sharper state, and the only moment on this screen where the number in the badge is
    /// knowingly optimistic: the lot is in worse shape than nearly everything it was priced off and
    /// there were too few matching sales to correct it by.
    /// </summary>
    [Fact]
    public void A_ceiling_that_is_a_better_conditions_price_says_so()
    {
        var card = Card();
        card.Condition = Cond(matched: 1);

        Assert.Contains("That ceiling is a new price and this one is used — bid under it.",
            LiveBidSpeech.Say(card), StringComparison.Ordinal);
    }

    /// <summary>
    /// Silent everywhere else. Mixed comps under an unstated lot are the ordinary case, and a clause
    /// on every lot in exchange for a caveat the card's own strip already carries would cost this
    /// line the only thing it is for.
    /// </summary>
    [Theory]
    [InlineData(LiveConditionBands.Unstated, true, 5, LiveConditionBands.New)]   // nothing said
    [InlineData(LiveConditionBands.Used, false, 1, LiveConditionBands.New)]      // comps unclassified
    [InlineData(LiveConditionBands.Used, true, 6, LiveConditionBands.New)]       // enough, and no cut earned
    [InlineData(LiveConditionBands.New, true, 1, LiveConditionBands.Used)]       // better than the comps
    public void Every_other_condition_state_says_nothing(
        string band, bool readable, int matched, string dominant)
    {
        var card = Card();
        card.Condition = Cond(band, readable, matched: matched, dominant: dominant);

        var said = LiveBidSpeech.Say(card);

        Assert.DoesNotContain("Priced as", said, StringComparison.Ordinal);
        Assert.DoesNotContain("That ceiling is a", said, StringComparison.Ordinal);
    }

    /// <summary>A card built by something that never set the block at all still speaks.</summary>
    [Fact]
    public void A_card_with_no_condition_block_still_produces_a_line()
    {
        var card = Card();
        card.Condition = null!;

        Assert.Contains("BID UP TO $240.", LiveBidSpeech.Say(card), StringComparison.Ordinal);
    }

    [Fact]
    public void The_line_never_runs_on_or_ends_in_a_hanging_space()
    {
        foreach (var card in new[]
        {
            Card(),
            Card(bidKnown: false),
            Card(resale: null),
            Card(call: LiveBidCalls.NoData, label: "CAN'T PRICE IT"),
            Card(maxBid: 0m, label: "DON'T BID", call: LiveBidCalls.Stop),
        })
        {
            var said = LiveBidSpeech.Say(card);
            Assert.Equal(said.Trim(), said);
            Assert.DoesNotContain("  ", said, StringComparison.Ordinal);
            Assert.EndsWith(".", said, StringComparison.Ordinal);
        }
    }
}
