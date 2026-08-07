using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What does the queue in front of this lot cost?
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LiveStockDepth"/> counts the pile and deliberately prices nothing, on the grounds that
/// a "you already have three" haircut is a number nobody measured — and it was right. The fourth one
/// really does resell for what the comps say. <b>It just sells in April.</b>
/// </para>
/// <para>
/// April is the half nobody priced, and this card already measures it:
/// <see cref="LiveTrendRead.SlopePerMonth"/> is a Theil–Sen line in dollars per month across every
/// dated sale, which is the one kind of figure that can be multiplied by a number of months. So the
/// cut here is two measured numbers multiplied, and most of what is asserted below is what that
/// refuses to become — a charge for owning duplicates. A pile of a FLAT product costs nothing here
/// however deep it gets, and that is the property the whole file is built to keep.
/// </para>
/// </remarks>
public class LiveHoldCostTests
{
    // ── Builders ─────────────────────────────────────────────────────────────────────────────

    /// <summary>A confirmed, readable trend with a falling line — the state that is allowed to cut.</summary>
    private static LiveTrendRead Sliding(decimal dollarsPerMonth, string direction = LiveTrendDirections.Steady) =>
        new()
        {
            Readable = true,
            Reliability = "confirmed",
            Direction = direction,
            SlopePerMonth = -Math.Abs(dollarsPerMonth),
            PriceChangePercent = -2m,
            RecentSold = 6,
            PriorSold = 6,
        };

    private static OwnSalesEvidence Shelf(int held) => new() { UnitsHeld = held };

    // ── No queue at all, which is nearly every card ───────────────────────────────────────────

    /// <summary>
    /// One of it and nothing of it on the shelf. The commonest card there is, and the wait on it is
    /// genuinely zero rather than unmeasured — so it says so rather than going blank.
    /// </summary>
    [Fact]
    public void One_of_it_with_nothing_behind_it_is_charged_nothing()
    {
        var read = LiveHoldCost.Read(1, null, LiveStockTonight.Nothing, 4m, 200m, Sliding(40m));

        Assert.Equal(LiveHoldVerdicts.Solo, read.Verdict);
        Assert.True(read.Readable);
        Assert.Equal(0m, read.WaitMonths);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Empty(read.Warning);
        Assert.Contains("only one you'd have", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A steep slide and no queue is still nothing. This is the whole distinction between this file
    /// and a duplicate haircut: the charge is for the WAIT, and a first purchase does not wait.
    /// </summary>
    [Fact]
    public void The_steepest_slide_costs_nothing_when_there_is_no_wait()
    {
        var read = LiveHoldCost.Read(1, Shelf(0), LiveStockTonight.Nothing, 2m, 100m, Sliding(90m));

        Assert.Equal(LiveHoldVerdicts.Solo, read.Verdict);
        Assert.False(read.Discounted);
    }

    /// <summary>An unread sales book contributes nothing to the queue. Silence is not a shelf.</summary>
    [Fact]
    public void An_unread_sales_book_is_not_counted_as_stock()
    {
        var unread = LiveHoldCost.Read(1, null, LiveStockTonight.Nothing, 4m, 200m, Sliding(40m));
        var empty = LiveHoldCost.Read(1, Shelf(0), LiveStockTonight.Nothing, 4m, 200m, Sliding(40m));

        Assert.Equal(0, unread.UnitsAhead);
        Assert.Equal(unread.Verdict, empty.Verdict);
        Assert.Equal(unread.ResaleMultiplier, empty.ResaleMultiplier);
    }

    // ── The wait itself ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The wait is this LOT's, not the whole pile's. Seven on the shelf at four a month puts the one
    /// on screen 1.75 months back — it is the eighth out, not the first — reported at the one
    /// decimal place the strip prints.
    /// </summary>
    [Fact]
    public void The_wait_is_this_lots_own_place_in_the_queue()
    {
        var read = LiveHoldCost.Read(1, Shelf(7), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));

        Assert.Equal(7, read.UnitsAhead);
        Assert.Equal(8, read.UnitsAfter);
        Assert.Equal(1.8m, read.WaitMonths);
    }

    /// <summary>
    /// A multi-unit lot with nothing ahead of it still waits: its own units queue behind each other.
    /// Six at two a month averages 1.25 months across the lot — (0 + 5/2) / 2.
    /// </summary>
    [Fact]
    public void A_multi_unit_lot_queues_behind_itself()
    {
        var read = LiveHoldCost.Read(6, Shelf(0), LiveStockTonight.Nothing, 2m, 200m, Sliding(10m));

        Assert.Equal(0, read.UnitsAhead);
        Assert.Equal(6, read.UnitsAfter);
        Assert.Equal(1.2m, read.WaitMonths);
    }

    /// <summary>
    /// The wait the strip prints is the wait the money was worked out from. A cut computed off 1.75
    /// months while the screen says 1.8 is a cut the person being charged for it cannot reproduce,
    /// which on this card is the same as a cut with no reason given.
    /// </summary>
    [Fact]
    public void The_cut_is_worked_out_from_the_wait_the_seller_can_see()
    {
        var read = LiveHoldCost.Read(1, Shelf(7), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));

        Assert.Equal(1.8m, read.WaitMonths);
        // $10 a month × the 1.8 months on screen, not × the 1.75 behind it.
        Assert.Equal(18m, read.ErosionPerUnit);
        Assert.Equal(9m, read.CutPercent);
    }

    /// <summary>Tonight's wins count exactly as the shelf does — they are the units the stock strip
    /// can see and nothing else on the card can.</summary>
    [Fact]
    public void Tonights_wins_are_in_the_queue_too()
    {
        var shelfOnly = LiveHoldCost.Read(1, Shelf(6), LiveStockTonight.Nothing, 3m, 200m, Sliding(10m));
        var split = LiveHoldCost.Read(1, Shelf(2), new LiveStockTonight(4, 2), 3m, 200m, Sliding(10m));

        Assert.Equal(shelfOnly.UnitsAhead, split.UnitsAhead);
        Assert.Equal(shelfOnly.WaitMonths, split.WaitMonths);
        Assert.Equal(shelfOnly.ResaleMultiplier, split.ResaleMultiplier);
    }

    /// <summary>A queue the market clears inside a month is not priced. Below that bar a slide of
    /// the size this app can measure at all is inside the noise of the comps it came from.</summary>
    [Fact]
    public void A_short_queue_is_not_priced()
    {
        var read = LiveHoldCost.Read(1, Shelf(2), LiveStockTonight.Nothing, 8m, 200m, Sliding(40m));

        Assert.Equal(LiveHoldVerdicts.Quick, read.Verdict);
        Assert.True(read.Readable);
        Assert.True(read.WaitMonths < LiveHoldCost.MinWaitMonths);
        Assert.False(read.Discounted);
        Assert.Empty(read.Warning);
    }

    // ── What it refuses to charge for ────────────────────────────────────────────────────────

    /// <summary>
    /// <b>The property this file exists to keep.</b> Ten of something on a shelf, four months of
    /// queue, and a price that is not falling: nothing is charged. The pile is not the cost.
    /// </summary>
    [Fact]
    public void A_deep_pile_of_a_flat_product_is_charged_nothing()
    {
        var flat = new LiveTrendRead
        {
            Readable = true,
            Reliability = "confirmed",
            Direction = LiveTrendDirections.Steady,
            SlopePerMonth = 0m,
            PriceChangePercent = 0m,
        };

        var read = LiveHoldCost.Read(1, Shelf(12), LiveStockTonight.Nothing, 3m, 200m, flat);

        Assert.Equal(LiveHoldVerdicts.Steady, read.Verdict);
        Assert.Equal(4m, read.WaitMonths);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Empty(read.Warning);
    }

    /// <summary>
    /// A climbing price across a long wait does not raise the ceiling. Same asymmetry the trend and
    /// condition cuts are built around: waiting is not a way of making money, and paying today for a
    /// price that has not happened is how a good read loses cash on a purchase with no undo.
    /// </summary>
    [Fact]
    public void A_rising_price_never_raises_the_ceiling_for_a_longer_hold()
    {
        var rising = Sliding(20m, LiveTrendDirections.Rising);
        rising.SlopePerMonth = 20m;

        var read = LiveHoldCost.Read(1, Shelf(9), LiveStockTonight.Nothing, 3m, 200m, rising);

        Assert.Equal(LiveHoldVerdicts.Steady, read.Verdict);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.False(read.Discounted);
        Assert.Contains("never raises it", read.MoneyNote, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two readings of the same comps must not disagree. A falling line under a window
    /// comparison that says the price is climbing is the case where one of them is wrong, and a cut
    /// taken on it would refuse a good lot.
    /// </summary>
    [Fact]
    public void A_falling_line_under_a_rising_window_is_not_priced()
    {
        var read = LiveHoldCost.Read(
            1, Shelf(9), LiveStockTonight.Nothing, 3m, 200m, Sliding(20m, LiveTrendDirections.Rising));

        Assert.Equal(LiveHoldVerdicts.Steady, read.Verdict);
        Assert.False(read.Discounted);
    }

    /// <summary>A slide too thin to price backwards is certainly too thin to price forwards. The
    /// same <c>confirmed</c> bar the trend cut has to clear.</summary>
    [Fact]
    public void A_tentative_slide_warns_rather_than_cuts()
    {
        var thin = Sliding(20m);
        thin.Reliability = "tentative";

        var read = LiveHoldCost.Read(1, Shelf(9), LiveStockTonight.Nothing, 3m, 200m, thin);

        Assert.Equal(LiveHoldVerdicts.Unsure, read.Verdict);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Equal(20m, read.DeclinePerMonth);
        Assert.Contains("has NOT", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>No dated history to read a trend from is said, not guessed at. A wait with no price
    /// reading is a risk the seller is carrying and cannot see any other way.</summary>
    [Fact]
    public void A_wait_with_no_dated_trend_is_reported_and_not_priced()
    {
        var read = LiveHoldCost.Read(
            1, Shelf(9), LiveStockTonight.Nothing, 3m, 200m, new LiveTrendRead { Readable = false });

        Assert.Equal(LiveHoldVerdicts.Blind, read.Verdict);
        Assert.False(read.Discounted);
        Assert.Contains("nothing dated says at what price", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>No clearance rate means no way to turn units into months. It says that rather than
    /// reporting a wait of zero, which would read as "there is no queue".</summary>
    [Fact]
    public void No_clearance_rate_is_not_reported_as_no_wait()
    {
        var read = LiveHoldCost.Read(1, Shelf(9), LiveStockTonight.Nothing, 0m, 200m, Sliding(20m));

        Assert.Equal(LiveHoldVerdicts.None, read.Verdict);
        Assert.False(read.Readable);
        Assert.Null(read.WaitMonths);
        Assert.False(read.Discounted);
    }

    /// <summary>No resale price is nothing to erode, and it says so rather than dividing by it.</summary>
    [Fact]
    public void No_resale_price_is_nothing_to_erode()
    {
        var read = LiveHoldCost.Read(1, Shelf(9), LiveStockTonight.Nothing, 3m, null, Sliding(20m));

        Assert.Equal(LiveHoldVerdicts.None, read.Verdict);
        Assert.False(read.Discounted);
        Assert.Equal(3m, read.WaitMonths);
    }

    /// <summary>A slide too small to move the price by a percent leaves it alone rather than
    /// reporting a cut of nought point four.</summary>
    [Fact]
    public void A_slide_too_small_to_matter_is_not_taken()
    {
        var read = LiveHoldCost.Read(1, Shelf(6), LiveStockTonight.Nothing, 3m, 400m, Sliding(0.50m));

        Assert.Equal(LiveHoldVerdicts.Steady, read.Verdict);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        // Said with cents, because "$0 a month" is a figure that reads as nothing at all.
        Assert.Contains("$0.50", read.Note, StringComparison.Ordinal);
    }

    // ── The cut it does take ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The measured case, end to end. Eight on the shelf at four a month puts this lot two months
    /// back; the line has been falling $10 a month; so $20 a unit is gone by the time it sells, off
    /// a $200 price — a 10% cut.
    /// </summary>
    [Fact]
    public void A_confirmed_slide_across_a_real_wait_cuts_the_ceiling()
    {
        var read = LiveHoldCost.Read(1, Shelf(8), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));

        Assert.Equal(LiveHoldVerdicts.Priced, read.Verdict);
        Assert.Equal(2m, read.WaitMonths);
        Assert.Equal(2m, read.ProjectedMonths);
        Assert.Equal(10m, read.DeclinePerMonth);
        Assert.Equal(20m, read.ErosionPerUnit);
        Assert.Equal(0.90m, read.ResaleMultiplier);
        Assert.Equal(10m, read.CutPercent);
        Assert.False(read.Capped);
        Assert.False(read.Floored);
        Assert.Contains("cut 10%", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// The projection never runs further than the sales the line was fitted to.
    /// <see cref="PriceTrendAnalyzer.SlopePerMonth"/> measures across two thirty-day windows, so a
    /// fourteen-month queue is charged two months of slide — and the strip says the real exposure is
    /// longer than the figure on it.
    /// </summary>
    [Fact]
    public void The_slide_is_never_projected_past_the_evidence_behind_it()
    {
        var read = LiveHoldCost.Read(1, Shelf(42), LiveStockTonight.Nothing, 3m, 400m, Sliding(10m));

        Assert.Equal(14m, read.WaitMonths);
        Assert.Equal(LiveHoldCost.MaxProjectedMonths, read.ProjectedMonths);
        Assert.Equal(2m, LiveHoldCost.MaxProjectedMonths);
        Assert.True(read.Capped);
        // Two months of slide, not fourteen: $20 off $400, not $140.
        Assert.Equal(20m, read.ErosionPerUnit);
        Assert.Equal(5m, read.CutPercent);
        Assert.Contains("real exposure is longer", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A projection must never take more off a ceiling than a measurement of sales that really
    /// happened. The floor is below <see cref="LiveTrend.MaxHaircutPercent"/> and it holds.
    /// </summary>
    [Fact]
    public void The_cut_is_floored_below_what_the_trend_read_is_allowed()
    {
        var read = LiveHoldCost.Read(1, Shelf(8), LiveStockTonight.Nothing, 4m, 100m, Sliding(60m));

        Assert.True(read.Discounted);
        Assert.True(read.Floored);
        Assert.Equal(LiveHoldCost.MaxHaircutPercent, read.CutPercent);
        Assert.Equal(0.80m, read.ResaleMultiplier);
        Assert.True(LiveHoldCost.MaxHaircutPercent < LiveTrend.MaxHaircutPercent);
        Assert.Contains("stops at 20%", read.MoneyNote, StringComparison.Ordinal);
    }

    /// <summary>The cut scales with the wait, which is the whole claim: the same item and the same
    /// slide cost more the deeper the shelf behind it is.</summary>
    [Fact]
    public void A_longer_queue_costs_more_than_a_shorter_one()
    {
        var shallow = LiveHoldCost.Read(1, Shelf(4), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));
        var deep = LiveHoldCost.Read(1, Shelf(8), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));

        Assert.True(shallow.Discounted);
        Assert.True(deep.Discounted);
        Assert.True(deep.CutPercent > shallow.CutPercent);
        Assert.True(deep.ResaleMultiplier < shallow.ResaleMultiplier);
    }

    // ── What the cut is applied to ───────────────────────────────────────────────────────────

    /// <summary>
    /// Only the three prices the ceiling is built from move. Everything the comps DESCRIBE is
    /// untouched — most of all the clearance rate, which is the figure the wait was computed from.
    /// A cut that changed it would make the strip's own arithmetic unreproducible.
    /// </summary>
    [Fact]
    public void Only_the_three_prices_move_and_the_clearance_rate_never_does()
    {
        var resale = new ResalePricing
        {
            LookupTitle = "thing",
            Median = 200m,
            ExpectedSale = 200m,
            QuickSale = 160m,
            SoldCompCount = 14,
            EstimatedMonthlySales = 4m,
            EstimatedDaysToSell = 12,
            ConfidenceScore = 71,
            AvgCompShipping = 9m,
        };

        var read = LiveHoldCost.Read(1, Shelf(8), LiveStockTonight.Nothing, 4m, 200m, Sliding(10m));
        var cut = LiveHoldCost.Discount(resale, read);

        Assert.Equal(180m, cut.ExpectedSale);
        Assert.Equal(180m, cut.Median);
        Assert.Equal(144m, cut.QuickSale);

        Assert.Equal(resale.SoldCompCount, cut.SoldCompCount);
        Assert.Equal(resale.EstimatedMonthlySales, cut.EstimatedMonthlySales);
        Assert.Equal(resale.EstimatedDaysToSell, cut.EstimatedDaysToSell);
        Assert.Equal(resale.ConfidenceScore, cut.ConfidenceScore);
        Assert.Equal(resale.AvgCompShipping, cut.AvgCompShipping);
    }

    /// <summary>
    /// Nothing to charge returns the SAME object, which is what makes "a card with no queue behind
    /// it is priced exactly as it was before this file existed" a property of the code rather than
    /// a claim about it.
    /// </summary>
    [Fact]
    public void Nothing_charged_returns_the_same_instance()
    {
        var resale = new ResalePricing { LookupTitle = "thing", Median = 200m, ExpectedSale = 200m };

        Assert.Same(resale, LiveHoldCost.Discount(resale, null));
        Assert.Same(resale, LiveHoldCost.Discount(
            resale, LiveHoldCost.Read(1, null, LiveStockTonight.Nothing, 4m, 200m, Sliding(10m))));
    }

    // ── It costs no lookup and no clock ──────────────────────────────────────────────────────

    /// <summary>
    /// Pure and deterministic. The bid moves every two or three seconds during a live sale and this
    /// is recomputed on every one of them off comps already held, so a clock or a network call in
    /// here would make the ceiling disagree with itself between two presses of the same button.
    /// </summary>
    [Fact]
    public void It_reads_no_clock_and_makes_no_call()
    {
        var source = ReadSource("Services/LiveHoldCost.cs");

        // Inside code rather than in the prose above it, which mentions none of these.
        var code = source[source.IndexOf("public static class LiveHoldCost", StringComparison.Ordinal)..];

        Assert.DoesNotContain("DateTime", code, StringComparison.Ordinal);
        Assert.DoesNotContain("await", code, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same inputs give the same ceiling, twice, with nothing carried between the calls.
    /// </summary>
    [Fact]
    public void The_same_inputs_give_the_same_answer()
    {
        var a = LiveHoldCost.Read(2, Shelf(7), new LiveStockTonight(3, 2), 4m, 250m, Sliding(12m));
        var b = LiveHoldCost.Read(2, Shelf(7), new LiveStockTonight(3, 2), 4m, 250m, Sliding(12m));

        Assert.Equal(a.Verdict, b.Verdict);
        Assert.Equal(a.WaitMonths, b.WaitMonths);
        Assert.Equal(a.ResaleMultiplier, b.ResaleMultiplier);
        Assert.Equal(a.Headline, b.Headline);
        Assert.Equal(a.Warning, b.Warning);
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
