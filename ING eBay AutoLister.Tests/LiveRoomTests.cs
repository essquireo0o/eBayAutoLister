using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Every other read on the live card answers "what is this worth". This one answers the question
// none of them can: whether the room will let anybody buy it at that price. It is measured off the
// hammer prices of the lots the seller priced on this show and did not win, pooled with the ones
// they did.
//
// What is pinned here is that the measurement stays a measurement:
//
//   · the rate is a median of the per-lot RATIOS, so one big lot cannot decide a whole room;
//   · a rate is REFUSED under three rated lots, and the count is still reported;
//   · a lot with no ceiling is watched and never rated — there is no line to measure it by;
//   · the wins count on equal terms, because a rate built off the losses alone reports every room
//     as hotter than it is;
//   · nothing here moves a price, and only the hot room interrupts.
public class LiveRoomTests
{
    private const string Show = "@bitminer_bill";

    private static LiveRoomTonight Room(params LiveRoomLot[] lots) =>
        lots.Length == 0 ? LiveRoomTonight.Nothing : new LiveRoomTonight(lots);

    /// <summary>A lot that got away at <paramref name="hammer"/> against a ceiling.</summary>
    private static LiveRoomLot Lost(decimal hammer, decimal ceiling) => new(hammer, ceiling, Won: false);

    private static LiveRoomLot Won(decimal hammer, decimal ceiling) => new(hammer, ceiling, Won: true);

    // ── The rate ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole feature in one assertion. Three lots hammered at 60%, 50% and 70% of the ceilings
    /// this app gave; the middle one is 60%, and a $200 ceiling on the lot now on screen therefore
    /// lands around $120 with $80 of daylight over it.
    /// </summary>
    [Fact]
    public void It_reports_what_the_room_clears_at_and_where_this_lot_lands()
    {
        var read = LiveRoom.Read(Show, Room(Lost(60m, 100m), Lost(50m, 100m), Lost(70m, 100m)), 200m);

        Assert.True(read.Readable);
        Assert.Equal(LiveRoomVerdicts.Cheap, read.Verdict);
        Assert.Equal(3, read.Watched);
        Assert.Equal(3, read.Rated);
        Assert.Equal(60, read.ClearingPercent);
        Assert.Equal(120m, read.ExpectedHammer);
        Assert.Equal(80m, read.RoomOverExpected);
    }

    /// <summary>
    /// A median of the RATIOS and not the ratio of the medians. One $900 lot among four $20 ones
    /// would otherwise decide the whole room off a single bidder's evening.
    /// </summary>
    [Fact]
    public void One_big_lot_cannot_decide_a_whole_room()
    {
        // Four cheap lots at half the ceiling, one huge one that went well over.
        var read = LiveRoom.Read(Show, Room(
            Lost(10m, 20m), Lost(10m, 20m), Lost(10m, 20m), Lost(10m, 20m), Lost(1_800m, 900m)), 100m);

        // Median ratio is 0.5. A ratio-of-medians read would have been dragged up by the $1,800 row.
        Assert.Equal(50, read.ClearingPercent);
        Assert.Equal(LiveRoomVerdicts.Cheap, read.Verdict);
        // And the row that went over is still counted and still reported.
        Assert.Equal(1, read.OverCeiling);
    }

    /// <summary>The rate is the middle of an even count, averaged — the median every other screen
    /// in this app means by the word.</summary>
    [Fact]
    public void An_even_count_averages_the_two_middles()
    {
        var read = LiveRoom.Read(Show, Room(
            Lost(40m, 100m), Lost(60m, 100m), Lost(80m, 100m), Lost(100m, 100m)), 100m);

        Assert.Equal(70, read.ClearingPercent);
    }

    // ── What it refuses ───────────────────────────────────────────────────────

    /// <summary>
    /// Two lots are an anecdote about two bidders, and one of them moves the median forty points.
    /// The count is still reported — "both lots here went over your ceiling" is a true sentence —
    /// and the claim that it is a rate is not.
    /// </summary>
    [Fact]
    public void Under_three_rated_lots_it_reports_the_count_and_refuses_the_rate()
    {
        var read = LiveRoom.Read(Show, Room(Lost(150m, 100m), Lost(160m, 100m)), 200m);

        Assert.False(read.Readable);
        Assert.Equal(LiveRoomVerdicts.Thin, read.Verdict);
        Assert.Equal(2, read.Watched);
        Assert.Equal(2, read.Rated);
        Assert.Equal(2, read.OverCeiling);
        // No projection off a rate it refused to state.
        Assert.Equal(0m, read.ExpectedHammer);
        Assert.Contains("too few to be a rate", read.Headline, StringComparison.Ordinal);
        // And nothing interrupts: a count the seller can read is not a warning.
        Assert.Equal("", read.Warning);
    }

    /// <summary>
    /// A lot the comps refused has a hammer price and no line to measure it against. It is watched
    /// and never rated, and a room made entirely of those says so rather than reporting nothing.
    /// </summary>
    [Fact]
    public void A_lot_with_no_ceiling_is_watched_and_never_rated()
    {
        var read = LiveRoom.Read(Show, Room(Lost(80m, 0m), Lost(90m, 0m), Lost(60m, 100m)), 100m);

        Assert.Equal(3, read.Watched);
        Assert.Equal(1, read.Rated);
        Assert.False(read.Readable);

        var none = LiveRoom.Read(Show, Room(Lost(80m, 0m), Lost(90m, 0m)), 100m);
        Assert.Equal(LiveRoomVerdicts.Thin, none.Verdict);
        Assert.Equal(0, none.Rated);
        Assert.Contains("none of them had a ceiling", none.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// A room is one host's audience. With no show named there is nothing to pool and the read says
    /// so plainly — the fix is one keystroke, and combining every stream the seller has ever watched
    /// would be a confident claim about a room that does not exist.
    /// </summary>
    [Fact]
    public void An_unnamed_show_is_never_measured()
    {
        var read = LiveRoom.Read("", Room(Lost(60m, 100m), Lost(50m, 100m), Lost(70m, 100m)), 200m);

        Assert.Equal(LiveRoomVerdicts.Unread, read.Verdict);
        Assert.False(read.Readable);
        Assert.Equal(0, read.ClearingPercent);
        Assert.Contains("No show named", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>The state every show starts in, and it says what the button is for rather than
    /// showing a dash.</summary>
    [Fact]
    public void A_named_show_with_nothing_recorded_says_what_to_press()
    {
        var read = LiveRoom.Read(Show, LiveRoomTonight.Nothing, 200m);

        Assert.Equal(LiveRoomVerdicts.Unread, read.Verdict);
        Assert.Equal(0, read.Watched);
        Assert.Contains("Nothing recorded yet", read.Headline, StringComparison.Ordinal);
        Assert.Contains("Went for", read.Note, StringComparison.Ordinal);
    }

    /// <summary>A card with no ceiling still gets the room's own record — "the last six lots here
    /// went over the app's ceilings" is true whatever this particular lot is — and no projection,
    /// because there is nothing for an expected hammer price to be a share of.</summary>
    [Fact]
    public void With_no_ceiling_on_this_lot_the_rate_is_still_read_and_nothing_is_projected()
    {
        var read = LiveRoom.Read(Show, Room(Lost(60m, 100m), Lost(50m, 100m), Lost(70m, 100m)), 0m);

        Assert.True(read.Readable);
        Assert.Equal(60, read.ClearingPercent);
        Assert.Equal(0m, read.ExpectedHammer);
        Assert.Equal(0m, read.Ceiling);
    }

    // ── The three rated states ────────────────────────────────────────────────

    [Fact]
    public void A_room_clearing_at_the_ceiling_is_tight_and_does_not_interrupt()
    {
        var read = LiveRoom.Read(Show, Room(Lost(95m, 100m), Lost(100m, 100m), Lost(92m, 100m)), 100m);

        Assert.Equal(LiveRoomVerdicts.Tight, read.Verdict);
        Assert.Equal("", read.Warning);
        Assert.Contains("right at them", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>
    /// The one state worth interrupting for, and the sentence has to say the lot is fine. A seller
    /// who reads this as "bad item" has learned exactly the wrong thing and will walk past the next
    /// good one.
    /// </summary>
    [Fact]
    public void A_room_clearing_above_the_ceilings_warns_and_says_the_lot_is_not_the_problem()
    {
        var read = LiveRoom.Read(Show, Room(Lost(130m, 100m), Lost(120m, 100m), Lost(140m, 100m)), 200m);

        Assert.Equal(LiveRoomVerdicts.Hot, read.Verdict);
        Assert.Equal(3, read.OverCeiling);
        Assert.Equal(130, read.ClearingPercent);
        Assert.Contains("outbids it", read.Warning, StringComparison.Ordinal);
        Assert.Contains("Nothing is wrong with", read.Warning, StringComparison.Ordinal);
        // And the projection is honest about which side of the ceiling it lands on.
        Assert.Equal(260m, read.ExpectedHammer);
        Assert.Equal(-60m, read.RoomOverExpected);
        Assert.Contains("past your", read.Note, StringComparison.Ordinal);
    }

    /// <summary>The good state is good news, and good news belongs on the strip rather than on the
    /// warning list — the same rule the combined-shipping saving follows.</summary>
    [Fact]
    public void A_cheap_room_never_interrupts()
    {
        var read = LiveRoom.Read(Show, Room(Lost(50m, 100m), Lost(55m, 100m), Lost(45m, 100m)), 100m);

        Assert.Equal(LiveRoomVerdicts.Cheap, read.Verdict);
        Assert.Equal("", read.Warning);
        Assert.Contains("daylight here", read.Note, StringComparison.Ordinal);
    }

    /// <summary>The bar between cheap and tight is on the ratio itself, and it is checked at the
    /// boundary rather than near it — 85% is still cheap and 86% is not.</summary>
    [Fact]
    public void The_cheap_bar_is_checked_at_its_boundary()
    {
        Assert.Equal(LiveRoomVerdicts.Cheap,
            LiveRoom.Read(Show, Room(Lost(85m, 100m), Lost(85m, 100m), Lost(85m, 100m)), 100m).Verdict);
        Assert.Equal(LiveRoomVerdicts.Tight,
            LiveRoom.Read(Show, Room(Lost(86m, 100m), Lost(86m, 100m), Lost(86m, 100m)), 100m).Verdict);
    }

    /// <summary>And a room clearing at exactly the ceiling is tight, not hot: a lot that can be won
    /// by bidding to the number on screen is a lot that can be won.</summary>
    [Fact]
    public void Clearing_exactly_at_the_ceiling_is_tight_and_not_hot()
    {
        var read = LiveRoom.Read(Show, Room(Lost(100m, 100m), Lost(100m, 100m), Lost(100m, 100m)), 100m);

        Assert.Equal(LiveRoomVerdicts.Tight, read.Verdict);
        Assert.Equal("", read.Warning);
    }

    // ── The wins count, and that is not a detail ──────────────────────────────

    /// <summary>
    /// A seller wins the lots that go cheap. A rate built only from the lots that got away is a rate
    /// computed off the top tail of its own distribution, and it would report every room as hotter
    /// than it is — on the one screen where "too hot" means "go and do something else tonight".
    /// </summary>
    [Fact]
    public void The_lots_that_were_won_are_counted_on_equal_terms()
    {
        var lossesOnly = LiveRoom.Read(Show, Room(
            Lost(120m, 100m), Lost(130m, 100m), Lost(110m, 100m)), 100m);
        Assert.Equal(LiveRoomVerdicts.Hot, lossesOnly.Verdict);

        // The same night, with the four cheap lots the seller actually won put back in.
        var everything = LiveRoom.Read(Show, Room(
            Lost(120m, 100m), Lost(130m, 100m), Lost(110m, 100m),
            Won(40m, 100m), Won(50m, 100m), Won(45m, 100m), Won(55m, 100m)), 100m);

        Assert.Equal(LiveRoomVerdicts.Cheap, everything.Verdict);
        Assert.Equal(7, everything.Watched);
        Assert.Equal(4, everything.Won);
        Assert.Contains("4 you won, 3 to the room", everything.Note, StringComparison.Ordinal);
    }

    /// <summary>The two stores are pooled here and nowhere else, and either side may be missing.</summary>
    [Fact]
    public void Tonight_pools_the_two_books_and_survives_either_being_absent()
    {
        Assert.Empty(LiveRoom.Tonight(null, null).Watched);
        Assert.Single(LiveRoom.Tonight([Lost(10m, 20m)], null).Watched);
        Assert.Single(LiveRoom.Tonight(null, [Won(10m, 20m)]).Watched);
        Assert.Equal(2, LiveRoom.Tonight([Lost(10m, 20m)], [Won(10m, 20m)]).Watched.Count);
    }

    /// <summary>A <c>default</c> struct is the state every card built without a room book is in,
    /// and it must be enumerable rather than a null reference waiting for the first live show.</summary>
    [Fact]
    public void A_default_room_is_empty_and_not_null()
    {
        LiveRoomTonight none = default;
        Assert.Empty(none.Watched);
        Assert.Equal(LiveRoomVerdicts.Unread, LiveRoom.Read(Show, none, 100m).Verdict);
    }
}
