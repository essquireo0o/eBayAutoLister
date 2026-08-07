using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The second thing on the live arbitrage card allowed to take money off the ceiling: what kind of
/// one is on screen, against what kind the comps behind it were.
/// </summary>
/// <remarks>
/// As with the trend read, most of what is pinned here is a <b>refusal</b>, because that is where
/// the money is. A cut taken off two sold rows talks a seller out of a lot that was fine; a cut not
/// taken on a used item priced off sealed ones costs real cash on a purchase with no undo. So the
/// gates are the tests: the comps have to state a condition often enough to be read at all, the
/// lot's own band needs enough sales of its own to be priced against, a better condition never
/// raises anything, the cut is floored, and silence in a lot's name is never read as evidence of
/// what it is.
/// </remarks>
public class LiveConditionTests
{
    private static MarketplaceComparableResult Sale(string condition, decimal price, string title = "Bitmain Antminer S19j Pro") => new()
    {
        ItemId = $"{condition}-{price}",
        Title = title,
        Condition = condition,
        SoldPrice = price,
        TotalPrice = price,
    };

    /// <summary>N sealed comps at one price and M used ones at another — the shape this whole file
    /// exists for.</summary>
    private static List<MarketplaceComparableResult> Mixed(int newCount, decimal newPrice, int usedCount, decimal usedPrice)
    {
        var comps = new List<MarketplaceComparableResult>();
        for (var i = 0; i < newCount; i++) comps.Add(Sale("Brand New", newPrice));
        for (var i = 0; i < usedCount; i++) comps.Add(Sale("Pre-Owned", usedPrice));
        return comps;
    }

    // ── Reading a condition string ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("New", LiveConditionBands.New)]
    [InlineData("Brand New", LiveConditionBands.New)]
    [InlineData("New with tags", LiveConditionBands.New)]
    [InlineData("Factory sealed", LiveConditionBands.New)]
    [InlineData("Open box", LiveConditionBands.LikeNew)]
    [InlineData("New (other)", LiveConditionBands.LikeNew)]
    [InlineData("Like New", LiveConditionBands.LikeNew)]
    [InlineData("Pre-Owned", LiveConditionBands.Used)]
    [InlineData("Used", LiveConditionBands.Used)]
    [InlineData("Seller refurbished", LiveConditionBands.Used)]
    [InlineData("Very Good - Refurbished", LiveConditionBands.Used)]
    [InlineData("Good", LiveConditionBands.Used)]
    [InlineData("For parts or not working", LiveConditionBands.Broken)]
    [InlineData("Untested", LiveConditionBands.Broken)]
    public void A_comps_condition_field_reads_as_a_band(string condition, string expected)
    {
        Assert.Equal(expected, LiveCondition.FromCompCondition(condition));
    }

    /// <summary>
    /// The ordering that matters most. "Like new" and "New (other)" both contain the word "new",
    /// and a table checked in the wrong order calls an opened item sealed — which is the exact
    /// mistake this file exists to stop, made by the file itself.
    /// </summary>
    [Fact]
    public void Like_new_and_new_other_are_never_read_as_sealed()
    {
        Assert.Equal(LiveConditionBands.LikeNew, LiveCondition.FromCompCondition("Like New"));
        Assert.Equal(LiveConditionBands.LikeNew, LiveCondition.FromCompCondition("New (other) — open box"));
        Assert.Equal(LiveConditionBands.New, LiveCondition.FromCompCondition("Brand New"));
    }

    /// <summary>Word boundaries, not substrings. "Unused" is not "used".</summary>
    [Fact]
    public void A_word_inside_another_word_is_not_a_condition()
    {
        Assert.Null(LiveCondition.FromCompCondition("Unusedd"));
        Assert.Null(LiveCondition.FromCompCondition(""));
        Assert.Null(LiveCondition.FromCompCondition(null));
        Assert.Null(LiveCondition.FromCompCondition("Ships from Ohio"));
    }

    // ── Reading a lot's name ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Bitmain Antminer S19j Pro SEALED", LiveConditionBands.New)]
    [InlineData("iPhone 12 Pro 128GB NIB", LiveConditionBands.New)]
    [InlineData("Goldshell Mini Doge II open box", LiveConditionBands.LikeNew)]
    [InlineData("Antminer S9 used, runs fine", LiveConditionBands.Used)]
    [InlineData("Antminer S9 FOR PARTS", LiveConditionBands.Broken)]
    [InlineData("Antminer S9 untested", LiveConditionBands.Broken)]
    [InlineData("Bitmain Antminer S19j Pro 104TH", LiveConditionBands.Unstated)]
    public void A_lots_name_reads_as_a_band(string title, string expected)
    {
        Assert.Equal(expected, LiveCondition.FromTitle(title).Band);
    }

    /// <summary>
    /// The rounding rule this whole card is built on, applied to the name: when a lot's name states
    /// more than one condition, the worst one wins. "Tested working, screen cracked" is a cracked
    /// screen, and a card that read it as working would price scrap at working-unit money.
    /// </summary>
    [Fact]
    public void When_a_name_says_two_things_the_worse_one_wins()
    {
        Assert.Equal(LiveConditionBands.Broken, LiveCondition.FromTitle("Tested working — screen cracked").Band);
        Assert.Equal(LiveConditionBands.Used, LiveCondition.FromTitle("Brand new box, used unit inside").Band);
        Assert.Equal(LiveConditionBands.Broken, LiveCondition.FromTitle("SEALED lot, one untested").Band);
    }

    /// <summary>
    /// A comp's condition is a field, near enough a controlled vocabulary. A lot's name is prose
    /// shouted by an auctioneer, where "good" and "excellent" are as likely to be describing the
    /// deal as the item — so the graded adjectives are read off the field and never off the name.
    /// </summary>
    [Fact]
    public void Graded_adjectives_are_read_off_a_condition_field_and_never_off_a_name()
    {
        Assert.Equal(LiveConditionBands.Used, LiveCondition.FromCompCondition("Good"));
        Assert.Equal(LiveConditionBands.Unstated, LiveCondition.FromTitle("Excellent deal — Antminer S19").Band);
        Assert.Equal(LiveConditionBands.Unstated, LiveCondition.FromTitle("Fair price, good buy").Band);
    }

    [Fact]
    public void The_name_reports_the_words_that_said_it()
    {
        Assert.Equal("sealed", LiveCondition.FromTitle("Pokemon 151 booster box SEALED").Evidence);
        Assert.Equal("", LiveCondition.FromTitle("Pokemon 151 booster box").Evidence);
    }

    // ── The seller outranks the name ─────────────────────────────────────────────────────────

    [Fact]
    public void What_the_seller_picked_outranks_what_the_name_says()
    {
        var read = LiveCondition.Read("Antminer S19j Pro SEALED", LiveConditionBands.Used, Mixed(6, 200m, 6, 100m));

        Assert.Equal(LiveConditionBands.Used, read.Band);
        Assert.Equal(LiveConditionSources.Seller, read.Source);
        // The name's word is not reported as the source of an answer it did not give.
        Assert.Equal("", read.Evidence);
    }

    [Fact]
    public void An_empty_pick_hands_the_question_back_to_the_name()
    {
        var read = LiveCondition.Read("Antminer S19j Pro SEALED", "", Mixed(6, 200m, 6, 100m));

        Assert.Equal(LiveConditionBands.New, read.Band);
        Assert.Equal(LiveConditionSources.Title, read.Source);
        Assert.Equal("sealed", read.Evidence);
    }

    [Fact]
    public void Nothing_stated_anywhere_is_unstated_and_not_a_guess()
    {
        var read = LiveCondition.Read("Bitmain Antminer S19j Pro 104TH", null, Mixed(6, 200m, 6, 100m));

        Assert.Equal(LiveConditionBands.Unstated, read.Band);
        Assert.Equal(LiveConditionSources.Unstated, read.Source);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
    }

    [Theory]
    [InlineData("used", LiveConditionBands.Used)]
    [InlineData("Used", LiveConditionBands.Used)]
    [InlineData("likenew", LiveConditionBands.LikeNew)]
    [InlineData("open box", LiveConditionBands.LikeNew)]
    [InlineData("new", LiveConditionBands.New)]
    [InlineData("broken", LiveConditionBands.Broken)]
    [InlineData("for parts", LiveConditionBands.Broken)]
    [InlineData("mint condition maybe", LiveConditionBands.Unstated)]
    [InlineData("", LiveConditionBands.Unstated)]
    [InlineData(null, LiveConditionBands.Unstated)]
    public void The_picker_normalises_to_a_band(string? picked, string expected)
    {
        Assert.Equal(expected, LiveCondition.FromSeller(picked));
    }

    // ── The bands, off the comps ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_comps_are_split_by_the_condition_they_stated()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, Mixed(9, 200m, 3, 100m));

        Assert.True(read.Readable);
        Assert.Equal(12, read.TotalComps);
        Assert.Equal(12, read.ClassifiedComps);
        Assert.Equal(100m, read.CoveragePercent);
        Assert.Equal(2, read.Bands.Count);
        Assert.True(read.Mixed);

        // Best band first, so the eye lands on the price the ceiling was probably built out of.
        Assert.Equal(LiveConditionBands.New, read.Bands[0].Band);
        Assert.Equal(9, read.Bands[0].Count);
        Assert.Equal(200m, read.Bands[0].Median);
        Assert.Equal(75m, read.Bands[0].SharePercent);
        Assert.False(read.Bands[0].IsThisLot);

        Assert.Equal(LiveConditionBands.Used, read.Bands[1].Band);
        Assert.Equal(100m, read.Bands[1].Median);
        Assert.True(read.Bands[1].IsThisLot);

        Assert.Equal(LiveConditionBands.New, read.DominantBand);
        Assert.Equal(3, read.MatchedComps);
        Assert.Equal(100m, read.MatchedMedian);
    }

    [Fact]
    public void Rows_with_no_price_are_not_counted_in_any_band()
    {
        var comps = Mixed(6, 200m, 6, 100m);
        comps.Add(Sale("Pre-Owned", 0m));

        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, comps);

        Assert.Equal(13, read.TotalComps);
        Assert.Equal(12, read.ClassifiedComps);
        Assert.Equal(6, read.MatchedComps);
    }

    // ── The gates ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Plenty of sold rows carry no condition at all. "The comps are 80% new" off three rows out of
    /// twenty is a claim about three rows, and this refuses to make it — the money note says so
    /// instead of pretending the blend was a single-condition price.
    /// </summary>
    [Fact]
    public void Comps_that_mostly_state_no_condition_are_not_read_as_bands()
    {
        var comps = new List<MarketplaceComparableResult>
        {
            Sale("Brand New", 200m), Sale("Pre-Owned", 100m),
        };
        for (var i = 0; i < 10; i++) comps.Add(Sale("", 150m));

        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, comps);

        Assert.False(read.Readable);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Contains("whatever condition", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", read.Warning);
    }

    /// <summary>Coverage can be perfect and the set still be too small to split.</summary>
    [Fact]
    public void A_handful_of_classified_rows_is_not_enough_to_split()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, Mixed(2, 200m, 1, 100m));

        Assert.Equal(100m, read.CoveragePercent);
        Assert.True(read.ClassifiedComps < LiveCondition.MinClassifiedComps);
        Assert.False(read.Readable);
        Assert.False(read.Discounted);
    }

    /// <summary>
    /// The gate that stops a haircut being taken off two sold rows. Under it there is no band
    /// median worth pricing against — and saying so out loud is the whole value in this state,
    /// because the badge above it is knowingly a better-condition price.
    /// </summary>
    [Fact]
    public void Too_few_sales_in_the_lots_own_band_cuts_nothing_and_says_so()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, Mixed(11, 200m, 2, 100m));

        Assert.True(read.Readable);
        Assert.Equal(2, read.MatchedComps);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Contains("nothing was cut", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
        // The badge is optimistic and only this sentence says so.
        Assert.Contains("bid well under it", read.Warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Not one used sale in the set. The sentence has to read as English, not as "0 used
    /// sales are in here".</summary>
    [Fact]
    public void No_matching_sale_at_all_is_said_in_words_rather_than_as_a_zero()
    {
        var comps = new List<MarketplaceComparableResult>();
        for (var i = 0; i < 8; i++) comps.Add(Sale("Brand New", 200m));

        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, comps);

        Assert.Equal(0, read.MatchedComps);
        Assert.False(read.Discounted);
        Assert.DoesNotContain("0 used", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("No used sale is", read.MoneyNote, StringComparison.Ordinal);
        Assert.Contains("no used sale to price off", read.Warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The asymmetry, and the reason for it: a better condition is a claim about an object being
    /// held up to a camera by the person selling it. Refusing to bid up on it costs a lot somebody
    /// else wins; believing it costs cash on a purchase with no undo.
    /// </summary>
    [Fact]
    public void A_better_condition_than_the_comps_never_raises_the_ceiling()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.New, Mixed(6, 200m, 6, 100m));

        Assert.True(read.Readable);
        Assert.Equal(6, read.MatchedComps);
        Assert.Equal(200m, read.MatchedMedian);
        Assert.True(read.MatchedMedian > read.AllMedian);

        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Equal(0m, read.CutPercent);
        Assert.Contains("never raises it", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
        // Good news withheld is not a warning. The strip carries it; the warning list does not.
        Assert.Equal("", read.Warning);
    }

    /// <summary>Every classified sale is already the right condition. Nothing to cut, and worth
    /// saying — it is the one state where the ceiling is provably the right kind of price.</summary>
    [Fact]
    public void Comps_that_are_all_the_right_condition_are_reported_as_such_and_cut_nothing()
    {
        var comps = new List<MarketplaceComparableResult>();
        for (var i = 0; i < 8; i++) comps.Add(Sale("Pre-Owned", 100m + i));

        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, comps);

        Assert.False(read.Mixed);
        Assert.False(read.Discounted);
        Assert.Contains("already priced on the right condition", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("", read.Warning);
    }

    // ── The cut ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The measured gap, and nothing else. Twelve comps: nine sealed at $200, three used at $100.
    /// The median across all twelve is $200; the used median is $100; the ratio is 0.5.
    /// </summary>
    [Fact]
    public void The_cut_is_the_ratio_between_the_bands_own_median_and_the_mixed_one()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, Mixed(9, 200m, 3, 100m));

        Assert.True(read.Discounted);
        Assert.Equal(200m, read.AllMedian);
        Assert.Equal(100m, read.MatchedMedian);
        Assert.Equal(0.5m, read.ResaleMultiplier);
        Assert.Equal(50m, read.CutPercent);
        Assert.False(read.Floored);
    }

    /// <summary>
    /// Past the floor the gap has stopped looking like the same item in worse shape. A card that
    /// quietly priced a $400 item at $30 would be refusing lots on the strength of one odd row.
    /// </summary>
    [Fact]
    public void The_cut_stops_at_the_floor_however_far_apart_the_bands_are()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Broken, Mixed(9, 400m, 0, 0m)
            .Concat(new[] { Sale("For parts", 20m), Sale("For parts", 22m), Sale("For parts", 24m) }).ToList());

        Assert.True(read.Discounted);
        Assert.True(read.Floored);
        Assert.Equal(1m - LiveCondition.MaxHaircutPercent / 100m, read.ResaleMultiplier);
        Assert.Equal(LiveCondition.MaxHaircutPercent, read.CutPercent);
        Assert.Contains("stops at", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The cut is applied to the three prices the ceiling is built from and to nothing
    /// else — the spread, the comp counts and the confidence describe sales that really happened,
    /// and scaling them would be inventing sales nobody made.</summary>
    [Fact]
    public void The_discount_scales_the_prices_and_leaves_every_description_of_the_comps_alone()
    {
        var resale = new ResalePricing
        {
            LookupTitle = "antminer s19j pro",
            Median = 200m, ExpectedSale = 210m, QuickSale = 180m,
            SoldCompCount = 12, PricedCompCount = 12, ConfidenceScore = 71,
            ConfidenceLevel = "High", AvgCompShipping = 14m, EstimatedMonthlySales = 6m,
        };
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, Mixed(9, 200m, 3, 100m));

        var cut = LiveCondition.Discount(resale, read);

        Assert.Equal(100m, cut.Median);
        Assert.Equal(105m, cut.ExpectedSale);
        Assert.Equal(90m, cut.QuickSale);

        Assert.Equal(12, cut.SoldCompCount);
        Assert.Equal(12, cut.PricedCompCount);
        Assert.Equal(71, cut.ConfidenceScore);
        Assert.Equal("High", cut.ConfidenceLevel);
        Assert.Equal(14m, cut.AvgCompShipping);
        Assert.Equal(6m, cut.EstimatedMonthlySales);
        Assert.Equal("antminer s19j pro", cut.LookupTitle);
    }

    /// <summary>
    /// The same INSTANCE back when nothing was cut. That is what makes "a card with no condition
    /// read is priced exactly as it was before this file existed" a property of the code rather
    /// than a claim about it.
    /// </summary>
    [Fact]
    public void Nothing_cut_returns_the_very_same_pricing_object()
    {
        var resale = new ResalePricing { Median = 200m, ExpectedSale = 210m };

        Assert.Same(resale, LiveCondition.Discount(resale, null));
        Assert.Same(resale, LiveCondition.Discount(resale, new LiveConditionRead()));
        Assert.Same(resale, LiveCondition.Discount(resale,
            LiveCondition.Read("Antminer S19j Pro", null, Mixed(6, 200m, 6, 100m))));
    }

    // ── What it says ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The most common state on a live show and the most actionable one: nothing said what this is,
    /// and the comps behind the ceiling run from used prices to sealed ones. The card asks rather
    /// than assuming, and names both ends in dollars so the question is worth answering.
    /// </summary>
    [Fact]
    public void An_unstated_lot_over_mixed_comps_asks_and_names_both_ends()
    {
        var read = LiveCondition.Read("Bitmain Antminer S19j Pro 104TH", null, Mixed(6, 200m, 6, 100m));

        Assert.Equal(LiveConditionBands.Unstated, read.Band);
        Assert.False(read.Discounted);
        Assert.Contains("Condition not stated", read.Headline, StringComparison.Ordinal);
        Assert.Contains("Set Condition", read.MoneyNote, StringComparison.Ordinal);
        Assert.Contains("$100", read.Warning, StringComparison.Ordinal);
        Assert.Contains("$200", read.Warning, StringComparison.Ordinal);
    }

    /// <summary>Unstated, but every comp is the same condition anyway. There is no question to ask
    /// and the ceiling is already the right kind of price.</summary>
    [Fact]
    public void An_unstated_lot_over_single_band_comps_is_told_it_is_already_priced_right()
    {
        var comps = new List<MarketplaceComparableResult>();
        for (var i = 0; i < 8; i++) comps.Add(Sale("Pre-Owned", 100m + i));

        var read = LiveCondition.Read("Bitmain Antminer S19j Pro 104TH", null, comps);

        Assert.Equal("", read.Warning);
        Assert.Contains("already a used price", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every priced card carries a headline and a money sentence, including the ones with
    /// nothing to report. A block that only speaks when it took money off is a block whose silence
    /// means both "the comps are the right condition" and "nothing looked".</summary>
    [Theory]
    [InlineData(LiveConditionBands.Used)]
    [InlineData(LiveConditionBands.New)]
    [InlineData("")]
    public void Every_read_says_something(string picked)
    {
        foreach (var comps in new[]
                 {
                     Mixed(9, 200m, 3, 100m), Mixed(2, 200m, 1, 100m), Mixed(0, 0m, 0, 0m),
                     Mixed(6, 200m, 6, 100m),
                 })
        {
            var read = LiveCondition.Read("Antminer S19j Pro", picked, comps);
            Assert.False(string.IsNullOrWhiteSpace(read.Headline));
            Assert.False(string.IsNullOrWhiteSpace(read.MoneyNote));
        }
    }

    /// <summary>No comps at all is a read that says so, not an exception and not a cut.</summary>
    [Fact]
    public void Nothing_to_read_is_answered_rather_than_thrown()
    {
        var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used, null);

        Assert.False(read.Readable);
        Assert.Equal(0, read.TotalComps);
        Assert.False(read.Discounted);
        Assert.Empty(read.Bands);
        Assert.False(string.IsNullOrWhiteSpace(read.MoneyNote));
    }

    // ── The ladder ───────────────────────────────────────────────────────────────────────────

    /// <summary>Worst to best, with unstated below all of them. The order is what lets "the comps
    /// are in a better condition than this lot" be a question with an answer.</summary>
    [Fact]
    public void The_ladder_runs_worst_to_best()
    {
        Assert.True(LiveCondition.Rank(LiveConditionBands.Broken) < LiveCondition.Rank(LiveConditionBands.Used));
        Assert.True(LiveCondition.Rank(LiveConditionBands.Used) < LiveCondition.Rank(LiveConditionBands.LikeNew));
        Assert.True(LiveCondition.Rank(LiveConditionBands.LikeNew) < LiveCondition.Rank(LiveConditionBands.New));
        Assert.True(LiveCondition.Rank(LiveConditionBands.Unstated) < LiveCondition.Rank(LiveConditionBands.Broken));
        Assert.True(LiveCondition.Rank("nonsense") < 0);
    }

    /// <summary>
    /// Every reported cut lands the resale price at or above the matching band's own median, and
    /// never above the mixed one. Walked over a grid rather than asserted once, because the whole
    /// point of the floor is the case nobody wrote a test for.
    /// </summary>
    [Fact]
    public void No_cut_ever_prices_above_the_mixed_median_or_below_the_floor()
    {
        var checks = 0;

        foreach (var newCount in new[] { 4, 6, 9, 12 })
        foreach (var usedCount in new[] { 3, 4, 6 })
        foreach (var newPrice in new[] { 50m, 120m, 400m, 2000m })
        foreach (var usedPrice in new[] { 5m, 25m, 90m, 380m })
        {
            if (usedPrice >= newPrice) continue;

            var read = LiveCondition.Read("Antminer S19j Pro", LiveConditionBands.Used,
                Mixed(newCount, newPrice, usedCount, usedPrice));
            checks++;

            Assert.InRange(read.ResaleMultiplier, 1m - LiveCondition.MaxHaircutPercent / 100m, 1m);

            if (!read.Discounted) continue;

            // The cut is never deeper than the floor and never deeper than the measured gap.
            var priced = 1000m * read.ResaleMultiplier;
            Assert.True(priced <= 1000m, $"a cut raised the price: {read.ResaleMultiplier}");
            Assert.True(read.ResaleMultiplier >= read.MatchedMedian / read.AllMedian - 0.0001m,
                $"cut deeper than the measured gap: {read.ResaleMultiplier} vs {read.MatchedMedian / read.AllMedian}");
        }

        Assert.True(checks > 100, $"the grid did not run: {checks}");
    }
}
