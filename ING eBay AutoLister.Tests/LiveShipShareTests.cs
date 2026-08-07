using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What does winning THIS lot really add to the shipping bill?
/// </summary>
/// <remarks>
/// <para>
/// A live seller posts one box per show, not one parcel per lot: a first-item rate, plus a much
/// smaller rate for every extra thing in it. Every ceiling this app produced before this file
/// existed charged the full first-item rate to every lot of the night — so the fourth win of a show
/// was costed as though it were being posted on its own, and the error ran entirely one way. It made
/// the cheap lots look worst, which are exactly the lots where eleven dollars is most of the margin.
/// </para>
/// <para>
/// This is the only read on the live card that can <b>raise</b> a ceiling, so most of what is
/// asserted here is what it refuses to do. It never assumes a show combines, never assumes a rate,
/// never carries a box across two shows, and fails closed to the full first-item rate in every state
/// where any one of its three gates is missing.
/// </para>
/// </remarks>
public class LiveShipShareTests
{
    private const string Show = "ingmining";

    // ── The three gates ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing entered. The most expensive silence on the card: shipping on a live show is real
    /// money and it comes off the bid, not off the profit afterwards.
    /// </summary>
    [Fact]
    public void No_shipping_entered_is_said_out_loud_and_warned_about()
    {
        var read = LiveShipShare.Read(Show, 0m, 1m, new LiveShipTonight(3, 30m));

        Assert.False(read.Readable);
        Assert.Equal(LiveShipVerdicts.None, read.Verdict);
        Assert.Equal(0m, read.Marginal);
        Assert.Equal(0m, read.Saved);
        Assert.False(read.Applied);
        Assert.Equal("Shipping not entered", read.Headline);
        Assert.Contains("No shipping cost entered", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// A show nobody named combines nothing, however many lots are on the sheet. Tonight's sheet can
    /// hold lots from three different sellers, and a box carried across two of them would be a
    /// ceiling raised on evidence that does not exist.
    /// </summary>
    [Fact]
    public void An_unnamed_show_is_charged_full_freight()
    {
        var read = LiveShipShare.Read("", 12m, 1m, new LiveShipTonight(4, 15m));

        Assert.Equal(LiveShipVerdicts.Alone, read.Verdict);
        Assert.Equal(12m, read.Marginal);
        Assert.False(read.Applied);
        Assert.False(read.ShowNamed);
        Assert.Contains("Name the show", read.Note, StringComparison.Ordinal);
        Assert.Empty(read.Warning);
    }

    /// <summary>
    /// A named show with no extra-item rate and nothing won from it yet. Nothing has gone wrong and
    /// nothing has been proved, so the full rate stands and the note says what would change it.
    /// </summary>
    [Fact]
    public void A_named_show_with_no_extra_rate_and_no_wins_is_charged_full_freight()
    {
        var read = LiveShipShare.Read(Show, 12m, null, LiveShipTonight.Nothing);

        Assert.Equal(LiveShipVerdicts.Alone, read.Verdict);
        Assert.Equal(12m, read.Marginal);
        Assert.False(read.AdditionalStated);
        Assert.Contains(Show, read.Note, StringComparison.Ordinal);
        Assert.Contains("each extra lot", read.Note, StringComparison.Ordinal);
        Assert.Empty(read.Warning);
    }

    /// <summary>
    /// Both rates known and this is the first lot from the show. It really does pay full freight —
    /// and the note says the ones after it will not, because that is what makes the first win of a
    /// show the expensive one.
    /// </summary>
    [Fact]
    public void The_first_lot_of_a_show_pays_the_whole_rate_and_says_the_rest_will_not()
    {
        var read = LiveShipShare.Read(Show, 12m, 1m, LiveShipTonight.Nothing);

        Assert.Equal(LiveShipVerdicts.First, read.Verdict);
        Assert.Equal(12m, read.Marginal);
        Assert.False(read.Applied);
        Assert.Contains("First lot from ingmining", read.Headline, StringComparison.Ordinal);
        Assert.Contains("$11", read.Note, StringComparison.Ordinal);   // what the next ones save
        Assert.Empty(read.Warning);
    }

    // ── The one state that moves money ───────────────────────────────────────────────────────

    /// <summary>
    /// All three gates open: the show is named, its extra-item rate is stated, and a lot from that
    /// same show is already on tonight's sheet. This lot rides in a box that is already going out.
    /// </summary>
    [Fact]
    public void A_second_lot_from_the_same_show_is_charged_the_extra_item_rate()
    {
        var read = LiveShipShare.Read(Show, 12m, 1m, new LiveShipTonight(3, 14m));

        Assert.Equal(LiveShipVerdicts.Combined, read.Verdict);
        Assert.Equal(1m, read.Marginal);
        Assert.Equal(11m, read.Saved);
        Assert.True(read.Applied);
        Assert.Equal(3, read.LotsWonFromShow);
        Assert.Contains("your other 3 lots", read.Headline, StringComparison.Ordinal);
        Assert.Contains("$1", read.Headline, StringComparison.Ordinal);
        Assert.Contains("$12", read.Headline, StringComparison.Ordinal);
        Assert.Empty(read.Warning);
    }

    /// <summary>
    /// Free combined shipping — the commonest live-selling arrangement there is. A typed zero is a
    /// real answer and has to reach the ceiling as one, which is why the read carries a separate
    /// "was it stated" flag rather than testing the rate for zero.
    /// </summary>
    [Fact]
    public void A_stated_zero_means_the_extras_ride_free()
    {
        var read = LiveShipShare.Read(Show, 9m, 0m, new LiveShipTonight(1, 9m));

        Assert.Equal(LiveShipVerdicts.Combined, read.Verdict);
        Assert.True(read.AdditionalStated);
        Assert.Equal(0m, read.Marginal);
        Assert.Equal(9m, read.Saved);
        Assert.Equal("Ships free with your other lot", read.Headline);
    }

    /// <summary>A blank box is not a zero. The whole distinction the feature stands on.</summary>
    [Fact]
    public void A_blank_extra_rate_is_not_a_free_one()
    {
        var free = LiveShipShare.Read(Show, 9m, 0m, new LiveShipTonight(1, 9m));
        var blank = LiveShipShare.Read(Show, 9m, null, new LiveShipTonight(1, 9m));

        Assert.Equal(0m, free.Marginal);
        Assert.Equal(9m, blank.Marginal);
        Assert.NotEqual(free.Verdict, blank.Verdict);
    }

    /// <summary>
    /// A show that charges no less for an extra lot. The state is still "combined" — the lots really
    /// do go in one box — but nothing was saved, and the note says so rather than dressing an
    /// unchanged ceiling up as a win.
    /// </summary>
    [Fact]
    public void An_extra_rate_that_saves_nothing_says_it_saved_nothing()
    {
        var read = LiveShipShare.Read(Show, 10m, 10m, new LiveShipTonight(2, 20m));

        Assert.Equal(LiveShipVerdicts.Combined, read.Verdict);
        Assert.Equal(10m, read.Marginal);
        Assert.Equal(0m, read.Saved);
        Assert.False(read.Applied);
        Assert.Contains("nothing to be gained", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A show whose extra-item rate is HIGHER than its first-item rate. Odd, and it is used exactly
    /// as stated rather than clamped: the marginal cost of this lot is what the seller says it is,
    /// and quietly charging the lower of the two would be an optimistic ceiling nobody asked for.
    /// </summary>
    [Fact]
    public void A_dearer_extra_rate_is_charged_as_stated_and_never_clamped_down()
    {
        var read = LiveShipShare.Read(Show, 8m, 20m, new LiveShipTonight(1, 8m));

        Assert.Equal(20m, read.Marginal);
        Assert.Equal(0m, read.Saved);
        Assert.False(read.Applied);
    }

    /// <summary>
    /// Repeated wins from one show with no extra-item rate. The ceiling is knowingly charging full
    /// freight for a box already on its way — nothing is assumed on the seller's behalf, and the
    /// sentence is loud instead.
    /// </summary>
    [Fact]
    public void Repeated_wins_with_no_extra_rate_warn_and_move_no_money()
    {
        var read = LiveShipShare.Read(Show, 12m, null, new LiveShipTonight(3, 36m));

        Assert.Equal(LiveShipVerdicts.Unstated, read.Verdict);
        Assert.Equal(12m, read.Marginal);
        Assert.Equal(0m, read.Saved);
        Assert.False(read.Applied);
        Assert.Contains("already won 3 lots", read.Warning, StringComparison.Ordinal);
        Assert.Contains(Show, read.Warning, StringComparison.Ordinal);
        Assert.Contains("one box per show", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>One lot reads as one lot, not "1 lots". Every sentence, in every state.</summary>
    [Fact]
    public void One_lot_is_said_in_the_singular()
    {
        var combined = LiveShipShare.Read(Show, 12m, 1m, new LiveShipTonight(1, 12m));
        Assert.Contains("your other lot", combined.Headline, StringComparison.Ordinal);
        Assert.Contains("1 lot from ingmining is already", combined.Note, StringComparison.Ordinal);

        var unstated = LiveShipShare.Read(Show, 12m, null, new LiveShipTonight(1, 12m));
        Assert.Contains("won 1 lot from", unstated.Warning, StringComparison.Ordinal);
        Assert.DoesNotContain("1 lots", unstated.Warning, StringComparison.Ordinal);
    }

    // ── The box ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What tonight's shipment costs, and what it becomes. Both are the sheet's own figures plus the
    /// one number this read decided; the browser adds nothing up.
    /// </summary>
    [Fact]
    public void The_box_so_far_plus_this_lot_is_the_box_with_it()
    {
        var read = LiveShipShare.Read(Show, 12m, 1.5m, new LiveShipTonight(3, 15m));

        Assert.Equal(15m, read.ShippingSoFar);
        Assert.Equal(1.5m, read.Marginal);
        Assert.Equal(16.5m, read.ShippingWithThisLot);
    }

    /// <summary>An extra-item rate is routinely $1.50, and rounding the cents away would misstate
    /// the one figure this whole block exists to introduce.</summary>
    [Fact]
    public void Cents_survive_into_the_sentences()
    {
        var read = LiveShipShare.Read(Show, 12m, 1.5m, new LiveShipTonight(2, 13.5m));

        Assert.Contains("$1.50", read.Headline, StringComparison.Ordinal);
        // And a whole-dollar figure keeps its clean form beside it.
        Assert.Contains("$12", read.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("$12.00", read.Headline, StringComparison.Ordinal);
    }

    /// <summary>Nonsense in, full freight out. Negative rates and negative bills are not answers.</summary>
    [Fact]
    public void Negative_figures_are_floored_rather_than_believed()
    {
        var read = LiveShipShare.Read(Show, -5m, -3m, new LiveShipTonight(-2, -9m));

        Assert.False(read.Readable);
        Assert.Equal(LiveShipVerdicts.None, read.Verdict);
        Assert.Equal(0m, read.Marginal);
        Assert.Equal(0m, read.ShippingSoFar);
        Assert.Equal(0, read.LotsWonFromShow);
    }

    // ── Which shows are one show ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("IngMining", "ingmining")]
    [InlineData("  ingmining  ", "ingmining")]
    [InlineData("@IngMining", "ingmining")]
    [InlineData("https://www.whatnot.com/live/abc-123", "abc-123")]
    [InlineData("https://whatnot.com/live/abc-123?ref=share", "abc-123")]
    [InlineData("whatnot.com/user/ingmining", "ingmining")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void Show_names_normalise_the_way_a_seller_varies_them(string raw, string expected) =>
        Assert.Equal(expected, LiveShipShare.NormalizeShow(raw));

    /// <summary>
    /// The address in the panel and the handle typed by hand are the same show — which is what lets
    /// the browser fill the box in from the URL it is already reading the show from.
    /// </summary>
    [Fact]
    public void A_shows_address_and_its_slug_are_one_show()
    {
        Assert.Equal(
            LiveShipShare.NormalizeShow("abc-123"),
            LiveShipShare.NormalizeShow("https://www.whatnot.com/live/abc-123"));
    }

    /// <summary>
    /// It does not stem, split or fuzzy-match. Two different shows sharing a word must never share a
    /// box: the cost of a wrong match here is a ceiling that is too high on somebody else's freight.
    /// </summary>
    [Fact]
    public void Two_different_shows_are_never_folded_into_one()
    {
        Assert.NotEqual(
            LiveShipShare.NormalizeShow("mining depot"),
            LiveShipShare.NormalizeShow("mining"));

        Assert.NotEqual(
            LiveShipShare.NormalizeShow("whatnot.com/live/abc-123"),
            LiveShipShare.NormalizeShow("whatnot.com/live/abc-124"));
    }

    /// <summary>A pasted page title is cut rather than refused, so a long paste still matches
    /// itself on the next lot.</summary>
    [Fact]
    public void A_very_long_name_is_cut_and_still_matches_itself()
    {
        var long1 = new string('a', LiveShipShare.MaxShowKeyLength + 40);
        Assert.Equal(LiveShipShare.MaxShowKeyLength, LiveShipShare.NormalizeShow(long1).Length);
        Assert.Equal(LiveShipShare.NormalizeShow(long1), LiveShipShare.NormalizeShow(long1 + "zzz"));
    }

    /// <summary>The name on the strip is the seller's own wording, not the lower-cased key it is
    /// matched by. One is for reading and one is for comparing.</summary>
    [Fact]
    public void The_name_on_screen_keeps_the_sellers_own_case()
    {
        var read = LiveShipShare.Read("  @IngMining  ", 12m, 1m, new LiveShipTonight(1, 12m));

        Assert.Equal("IngMining", read.ShowName);
        Assert.Contains("IngMining", read.Note, StringComparison.Ordinal);
    }

    // ── What it costs to run ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// No clock, no network, no waiting. This sits inside a re-price that has to answer in the
    /// milliseconds a climbing bid leaves, and it re-answers identically on held comps and on a
    /// fresh read because it depends on neither.
    /// </summary>
    [Fact]
    public void It_costs_no_lookup_and_no_clock()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "ING eBay AutoLister", "Services", "LiveShipShare.cs"));

        Assert.DoesNotContain("DateTime", source, StringComparison.Ordinal);
        Assert.DoesNotContain("await", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Task<", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// It knows about freight and about nothing else. A resale price, a ceiling or a break-even in
    /// here would be a second opinion about money — this decides one input to the ceiling and hands
    /// it back.
    /// </summary>
    [Fact]
    public void It_prices_nothing()
    {
        var source = File.ReadAllText(Path.Combine(
            RepoRoot(), "ING eBay AutoLister", "Services", "LiveShipShare.cs"));

        Assert.DoesNotContain("MaxBid", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BreakEven", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResalePrice", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Median", source, StringComparison.Ordinal);
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
