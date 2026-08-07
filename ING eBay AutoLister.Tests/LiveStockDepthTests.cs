using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// How many of these would the seller then own, and how long does eBay take to absorb that many?
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists to stop is not a mispriced lot. Every lot in it is priced correctly — the
/// host has a pallet of one product, puts one up every four minutes, and the card says
/// <c>BID UP TO $90</c> six times because six times it is true. What is not true is the implied
/// sixth sentence: that six of them is six times the profit. They queue behind each other in the
/// same demand, so it is one flip and five months of a shelf, and until this file existed nothing on
/// the card could see it.
/// </para>
/// <para>
/// So the assertions here are mostly about two things. <b>What the pile is made of</b> — and in
/// particular that a lot won twenty minutes ago and a lot listed last week are never counted twice,
/// which is the one arithmetic error that would talk a seller out of a good lot. And <b>what this
/// refuses to do</b>: it takes nothing off any price, ever, because saturation is a claim about a
/// calendar and not about what the object fetches.
/// </para>
/// </remarks>
public class LiveStockDepthTests
{
    private static OwnSalesEvidence Shelf(int units, bool loose = false) =>
        new() { UnitsHeld = units, IdentityIsLoose = loose };

    // ── The pile ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One of them, nothing behind it. The ordinary case, and it is still SAID: a strip that goes
    /// quiet here is a strip whose silence means both "you're clear" and "nothing looked", and only
    /// one of those is worth pressing bid on.
    /// </summary>
    [Fact]
    public void A_first_one_with_nothing_held_is_read_and_says_so()
    {
        var read = LiveStockDepth.Read(1, Shelf(0), LiveStockTonight.Nothing, 4m, 12, 0);

        Assert.True(read.Readable);
        Assert.Equal(LiveStockVerdicts.Single, read.Verdict);
        Assert.Equal(1, read.UnitsAfter);
        Assert.False(read.AlreadyStocked);
        Assert.Equal("Your only one", read.Headline);
        Assert.Contains("Nothing of this on your shelf", read.Note, StringComparison.Ordinal);
        Assert.Empty(read.Warning);
    }

    /// <summary>The three counts add up, and the total is what the rest of the read is about.</summary>
    [Fact]
    public void The_pile_is_the_shelf_plus_tonight_plus_this_lot()
    {
        var read = LiveStockDepth.Read(2, Shelf(3), new LiveStockTonight(4, 2), 6m, 20, 0);

        Assert.Equal(3, read.UnitsHeld);
        Assert.Equal(4, read.WonTonight);
        Assert.Equal(2, read.LotsWonTonight);
        Assert.Equal(2, read.LotUnits);
        Assert.Equal(9, read.UnitsAfter);
        Assert.True(read.AlreadyStocked);
    }

    /// <summary>
    /// The count nothing else on the card can see. The Deal Pipeline knows nothing about three
    /// identical lots won on this tab in the last twenty minutes, because they are still in their
    /// boxes — and the same host putting up the same product all night is the ordinary shape of a
    /// live show.
    /// </summary>
    [Fact]
    public void Lots_won_tonight_count_even_with_an_empty_shelf()
    {
        var read = LiveStockDepth.Read(1, Shelf(0), new LiveStockTonight(3, 3), 1m, 30, 0);

        Assert.True(read.AlreadyStocked);
        Assert.Equal(4, read.UnitsAfter);
        Assert.Equal(LiveStockVerdicts.Flooded, read.Verdict);
        Assert.Contains("3 won tonight", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>A lone item with nothing behind it gets no bars: one bar reading "This lot 1" is a
    /// picture of a number already in the sentence beside it.</summary>
    [Fact]
    public void A_lone_item_with_nothing_behind_it_draws_no_bars()
    {
        var read = LiveStockDepth.Read(1, Shelf(0), LiveStockTonight.Nothing, 4m, 10, 0);
        Assert.Empty(read.Sources);
    }

    /// <summary>Bars are drawn for the parts that exist, biggest question first, and never for a
    /// part that is zero.</summary>
    [Fact]
    public void The_bars_name_only_the_parts_that_exist_shelf_first()
    {
        var read = LiveStockDepth.Read(2, Shelf(3), new LiveStockTonight(1, 1), 6m, 20, 0);

        Assert.Equal(
            new[] { LiveStockSources.Shelf, LiveStockSources.Tonight, LiveStockSources.Lot },
            read.Sources.Select(s => s.Kind));
        Assert.Equal(new[] { 3, 1, 2 }, read.Sources.Select(s => s.Units));
    }

    [Fact]
    public void A_shelf_of_none_draws_no_shelf_bar()
    {
        var read = LiveStockDepth.Read(3, Shelf(0), LiveStockTonight.Nothing, 6m, 20, 0);

        Assert.DoesNotContain(read.Sources, s => s.Kind == LiveStockSources.Shelf);
        Assert.DoesNotContain(read.Sources, s => s.Kind == LiveStockSources.Tonight);
        Assert.Single(read.Sources);
        Assert.Equal(LiveStockSources.Lot, read.Sources[0].Kind);
    }

    /// <summary>Every bar carries the sentence that explains it, because the number on its own is
    /// three characters wide and the question behind it is not.</summary>
    [Fact]
    public void Every_bar_carries_a_label_and_a_detail()
    {
        var read = LiveStockDepth.Read(2, Shelf(3), new LiveStockTonight(1, 1), 6m, 20, 0);

        Assert.All(read.Sources, s =>
        {
            Assert.NotEmpty(s.Label);
            Assert.NotEmpty(s.Detail);
            Assert.True(s.Units >= 1);
        });
    }

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    /// <summary>Comfortably inside the bar: a stack the market eats without it queueing up.</summary>
    [Fact]
    public void A_stack_the_market_clears_quickly_is_clear()
    {
        // 3 units against 10 sales a month — under a third of a month.
        var read = LiveStockDepth.Read(1, Shelf(2), LiveStockTonight.Nothing, 10m, 6, 0);

        Assert.Equal(LiveStockVerdicts.Clear, read.Verdict);
        Assert.True(read.Readable);
        Assert.Empty(read.Warning);
        Assert.Equal(0.3m, read.MonthsToClear);
    }

    /// <summary>Months of stock. Real money, parked — and the first state that says so out loud.</summary>
    [Fact]
    public void Months_of_stock_is_deep_and_warns()
    {
        // 6 units against 2 sales a month — three months.
        var read = LiveStockDepth.Read(1, Shelf(5), LiveStockTonight.Nothing, 2m, 20, 0);

        Assert.Equal(LiveStockVerdicts.Deep, read.Verdict);
        Assert.Equal(3m, read.MonthsToClear);
        Assert.Contains("6 of these", read.Warning, StringComparison.Ordinal);
        Assert.Contains("5 on the shelf", read.Warning, StringComparison.Ordinal);
        Assert.Contains("one in this lot", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>Past the point where this is a flip at all.</summary>
    [Fact]
    public void A_quarter_of_a_year_of_stock_is_flooded()
    {
        // 10 units against 2 sales a month — five months.
        var read = LiveStockDepth.Read(2, Shelf(8), LiveStockTonight.Nothing, 2m, 20, 0);

        Assert.Equal(LiveStockVerdicts.Flooded, read.Verdict);
        Assert.Equal(5m, read.MonthsToClear);
        Assert.Contains("as if it were the last of them", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two bars are where they are said to be, on both sides of each. Ten sales a month makes
    /// the unit count a tenth of the month count, so the boundaries fall on whole units.
    /// </summary>
    [Theory]
    [InlineData(19, 1.9, LiveStockVerdicts.Clear)]
    [InlineData(20, 2.0, LiveStockVerdicts.Deep)]
    [InlineData(39, 3.9, LiveStockVerdicts.Deep)]
    [InlineData(40, 4.0, LiveStockVerdicts.Flooded)]
    public void The_bars_fall_where_they_are_documented(int pile, double months, string expected)
    {
        var read = LiveStockDepth.Read(1, Shelf(pile - 1), LiveStockTonight.Nothing, 10m, 5, 0);

        Assert.Equal(pile, read.UnitsAfter);
        Assert.Equal((decimal)months, read.MonthsToClear);
        Assert.Equal(expected, read.Verdict);
    }

    /// <summary>
    /// A pile and no dated sold history to measure it against. Unknown rather than bad — and the
    /// only state where the honest answer is that there is no answer.
    /// </summary>
    [Fact]
    public void A_pile_with_no_clearance_rate_is_blind()
    {
        var read = LiveStockDepth.Read(1, Shelf(3), LiveStockTonight.Nothing, 0m, null, 0);

        Assert.False(read.Readable);
        Assert.Equal(LiveStockVerdicts.Blind, read.Verdict);
        Assert.Null(read.MonthsToClear);
        Assert.Contains("nothing says how fast they clear", read.Headline, StringComparison.Ordinal);
        Assert.Contains("no dated sold history", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>One spare of an unmeasurable product is an ordinary risk; the warning is for a pile
    /// of them.</summary>
    [Fact]
    public void A_single_spare_with_no_rate_is_blind_but_does_not_warn()
    {
        var read = LiveStockDepth.Read(1, Shelf(1), LiveStockTonight.Nothing, 0m, null, 0);

        Assert.Equal(LiveStockVerdicts.Blind, read.Verdict);
        Assert.Equal(2, read.UnitsAfter);
        Assert.Empty(read.Warning);
    }

    // ── What it refuses to claim ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An unread book must never read as an empty shelf. This is the failure that turns "you already
    /// have four" into silence, and it is the one state where the strip's job is to say it knows
    /// less than it looks like it knows.
    /// </summary>
    [Fact]
    public void An_unread_shelf_is_never_reported_as_an_empty_one()
    {
        var read = LiveStockDepth.Read(1, own: null, LiveStockTonight.Nothing, 4m, 10, 0);

        Assert.False(read.Readable);
        Assert.False(read.ShelfRead);
        Assert.Equal(LiveStockVerdicts.None, read.Verdict);
        Assert.Contains("wasn't read", read.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing of this on your shelf", read.Note, StringComparison.Ordinal);
    }

    /// <summary>But a count that is partly there is still shown, with the missing half named. Half
    /// a pile is more use than no pile.</summary>
    [Fact]
    public void An_unread_shelf_still_counts_tonight_and_says_what_is_missing()
    {
        var read = LiveStockDepth.Read(1, own: null, new LiveStockTonight(4, 4), 1m, 30, 0);

        Assert.Equal(5, read.UnitsAfter);
        Assert.Equal(LiveStockVerdicts.Flooded, read.Verdict);
        Assert.Contains("could not be read", read.Note, StringComparison.Ordinal);
        Assert.Contains("tonight's buy sheet alone", read.Note, StringComparison.Ordinal);
    }

    /// <summary>The pile may be somebody else's product, and that qualifies the warning as well as
    /// the note — a caveat only the calm sentence carries is a caveat nobody reads.</summary>
    [Fact]
    public void A_loose_identity_is_said_on_the_note_and_on_the_warning()
    {
        var read = LiveStockDepth.Read(1, Shelf(9, loose: true), LiveStockTonight.Nothing, 2m, 20, 0);

        Assert.True(read.IdentityIsLoose);
        Assert.Contains("may be a different product", read.Note, StringComparison.Ordinal);
        Assert.Contains("may be a different product", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>A loose match with nothing held is not qualified, because there is no pile for the
    /// caveat to be about.</summary>
    [Fact]
    public void A_loose_identity_with_nothing_held_says_nothing_about_it()
    {
        var read = LiveStockDepth.Read(1, Shelf(0, loose: true), LiveStockTonight.Nothing, 4m, 10, 0);
        Assert.DoesNotContain("different product", read.Note, StringComparison.Ordinal);
    }

    /// <summary>Other people's listings are reported beside the pile and never inside it: the
    /// clearance rate is already measured against them.</summary>
    [Fact]
    public void Active_listings_are_reported_and_never_added_to_the_pile()
    {
        var read = LiveStockDepth.Read(1, Shelf(2), LiveStockTonight.Nothing, 10m, 6, 14);

        Assert.Equal(14, read.ActiveCompCount);
        Assert.Equal(3, read.UnitsAfter);
        Assert.Contains("14 of them are listed on eBay right now", read.Note, StringComparison.Ordinal);
    }

    /// <summary>Nonsense in, nothing invented: a zero-unit lot is still one thing, and negative
    /// counts are floored rather than subtracted from the pile.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_lot_is_never_fewer_than_one_thing(int lotUnits)
    {
        var read = LiveStockDepth.Read(lotUnits, Shelf(-3), new LiveStockTonight(-2, -1), 4m, 10, -7);

        Assert.Equal(1, read.LotUnits);
        Assert.Equal(0, read.UnitsHeld);
        Assert.Equal(0, read.WonTonight);
        Assert.Equal(0, read.ActiveCompCount);
        Assert.Equal(1, read.UnitsAfter);
    }

    /// <summary>
    /// The whole design, as a property of the file. This reads a shelf and a calendar; it has no
    /// price in it, no ceiling, no margin and no way to reach one. A "you already have three"
    /// haircut would be a number nobody measured, taken off the one figure on the card that comes
    /// from real sales.
    /// </summary>
    [Fact]
    public void Nothing_in_the_file_touches_a_price()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "LiveStockDepth.cs"));

        foreach (var forbidden in new[] { "Discount", "MaxBid", "ResalePrice", "BreakEven", "Median" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it costs no lookup and no clock. Everything it measures is a figure the card already
    /// computed, which is what lets it sit inside a re-price that has milliseconds to answer in.
    /// </summary>
    [Fact]
    public void Nothing_in_the_file_reads_a_network_or_a_clock()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services", "LiveStockDepth.cs"));

        foreach (var forbidden in new[] { "DateTime.UtcNow", "DateTime.Now", "HttpClient", "await ", "Task<" })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// The months arithmetic is the units block's own, called rather than re-derived — so the strip
    /// and the lot's absorption note can never disagree about the same stack.
    /// </summary>
    [Fact]
    public void The_months_are_the_same_arithmetic_the_units_block_uses()
    {
        var read = LiveStockDepth.Read(2, Shelf(3), new LiveStockTonight(1, 1), 3m, 15, 0);
        var (months, days, _) = LiveLotSize.Absorption(6, 3m, 15);

        Assert.Equal(months, read.MonthsToClear);
        Assert.Equal(days, read.DaysToClearAll);
        Assert.Contains(LiveLotSize.MonthsInWords(months!.Value), read.Headline, StringComparison.Ordinal);
    }

    /// <summary>Every state has something to say. A strip rendered on every card must never be a
    /// rendered empty box.</summary>
    [Theory]
    [InlineData(1, 0, 0, 4)]
    [InlineData(1, 2, 0, 10)]
    [InlineData(3, 5, 2, 2)]
    [InlineData(1, 3, 0, 0)]
    [InlineData(2, 0, 0, 0)]
    public void Every_state_carries_a_headline_and_a_note(int lot, int shelf, int won, double monthly)
    {
        var read = LiveStockDepth.Read(lot, Shelf(shelf), new LiveStockTonight(won, won), (decimal)monthly, 12, 3);

        Assert.NotEmpty(read.Headline);
        Assert.NotEmpty(read.Note);
        Assert.Contains(read.Verdict, new[]
        {
            LiveStockVerdicts.Single, LiveStockVerdicts.Clear, LiveStockVerdicts.Deep,
            LiveStockVerdicts.Flooded, LiveStockVerdicts.Blind, LiveStockVerdicts.None,
        });
    }

    /// <summary>Only the two states that cross a bar are allowed to spend a line on the card's
    /// warning list. This strip is on every card and one that warned on all of them would train
    /// the eye to skip the two that matter.</summary>
    [Theory]
    [InlineData(LiveStockVerdicts.Single, false)]
    [InlineData(LiveStockVerdicts.Clear, false)]
    [InlineData(LiveStockVerdicts.Deep, true)]
    [InlineData(LiveStockVerdicts.Flooded, true)]
    public void Only_a_crossed_bar_warns(string verdict, bool warns)
    {
        var read = verdict switch
        {
            LiveStockVerdicts.Single => LiveStockDepth.Read(1, Shelf(0), LiveStockTonight.Nothing, 8m, 8, 0),
            LiveStockVerdicts.Clear => LiveStockDepth.Read(1, Shelf(2), LiveStockTonight.Nothing, 10m, 6, 0),
            LiveStockVerdicts.Deep => LiveStockDepth.Read(1, Shelf(5), LiveStockTonight.Nothing, 2m, 20, 0),
            _ => LiveStockDepth.Read(2, Shelf(8), LiveStockTonight.Nothing, 2m, 20, 0),
        };

        Assert.Equal(verdict, read.Verdict);
        Assert.Equal(warns, read.Warning.Length > 0);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
