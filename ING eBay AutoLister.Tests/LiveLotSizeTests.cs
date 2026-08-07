using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// How many things is the lot on screen?
/// </summary>
/// <remarks>
/// <para>
/// Most of these are refusals, and that is the shape of the problem rather than an accident of what
/// was easy to test. The two mistakes this reader can make are not symmetrical: reading a count that
/// is not there multiplies the ceiling by a number nobody wrote down and the seller overpays on a
/// live show with seconds to check; missing one leaves the ceiling exactly where it has always been
/// and the seller passes on a lot. So every ambiguous case in here is asserted to come back as
/// <c>1</c> — usually with a prompt attached, because the host is standing there and can be asked.
/// </para>
/// <para>
/// The one that matters most is <see cref="A_sealed_trading_card_box_is_one_box"/>. It is the case
/// where this reader deliberately parts company with
/// <see cref="LiquidationParser.ReadUnits"/>, and getting it wrong would have this feature telling
/// somebody to bid four figures on a $200 item.
/// </para>
/// </remarks>
public class LiveLotSizeTests
{
    // ── The counts a live lot's name actually states ─────────────────────────────────────────

    [Theory]
    [InlineData("3x Bitmain Antminer S9", 3)]
    [InlineData("3X BITMAIN ANTMINER S9", 3)]
    [InlineData("Bitmain Antminer S9 x3", 3)]
    [InlineData("Bitmain Antminer S9 X 4", 4)]
    [InlineData("(4) Goldshell Mini Doge II", 4)]
    [InlineData("LOT OF 5 GPU risers", 5)]
    [InlineData("Set of 3 Antminer power supplies", 3)]
    [InlineData("Bundle of 6 Raspberry Pi 4", 6)]
    [InlineData("Antminer control boards 8 pcs", 8)]
    [InlineData("12 pieces PCIe cables", 12)]
    [InlineData("Whatsminer hashboards - 2 units", 2)]
    public void A_stated_count_is_read(string title, int expected)
    {
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(expected, units.Count);
        Assert.True(units.IsLot);
        Assert.Equal(LiveLotSize.SourceTitle, units.Source);
        Assert.NotEqual("", units.Evidence);
        Assert.False(units.CountUnstated);
        Assert.Equal("", units.Refused);
    }

    [Fact]
    public void What_stated_the_count_is_quoted_back()
    {
        // The seller has two seconds to decide whether the app read their lot right, and "3x" is
        // the whole of the evidence. A count with no evidence beside it is a number to be argued
        // with and nothing to argue about.
        var units = LiveLotSize.Read("3x Bitmain Antminer S9", null);

        Assert.Equal("3x", units.Evidence);
        Assert.Contains("3x", units.Note, StringComparison.Ordinal);
        Assert.Contains("whole lot", units.Note, StringComparison.OrdinalIgnoreCase);
        // And the way out of a wrong read is on the card, not in a manual.
        Assert.Contains("quantity to 1", units.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_first_count_wins_rather_than_the_largest()
    {
        // "(2) of the product, which has 8 something in it" is two things. Taking the biggest
        // number in the name would price a pair as eight.
        var units = LiveLotSize.Read("(2) Antminer S19 hashboards 8 pcs spares", null);

        Assert.Equal(2, units.Count);
    }

    // ── The counts that are not counts ───────────────────────────────────────────────────────

    /// <summary>
    /// The reason this reader exists separately from the liquidation one.
    /// </summary>
    /// <remarks>
    /// A pallet listing that says "36 pack" is 36 things and
    /// <see cref="LiquidationSelectors.CountUnits"/> is right to read it. On a live show, <i>pack</i>,
    /// <i>box</i> and <i>case</i> are the names of the products — a sealed booster box IS one item,
    /// and its sold comps are comps for that box. Multiplying it by 36 would put a $7,000 ceiling on
    /// a $200 lot, at the exact moment somebody is deciding whether to bid.
    /// </remarks>
    [Theory]
    [InlineData("Pokemon 151 booster box 36 packs sealed")]
    [InlineData("Case of 12 booster boxes")]
    [InlineData("Box of 24 packs Prizm basketball")]
    [InlineData("Topps Chrome 50 count blaster")]
    public void A_sealed_trading_card_box_is_one_box(string title)
    {
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
    }

    [Theory]
    [InlineData("Bitmain Antminer S19j Pro 104TH")]   // a hashrate
    [InlineData("iPhone 12 Pro 128GB unlocked")]      // a capacity
    [InlineData("RTX 3080 Ti Founders Edition")]      // a model
    [InlineData("Dell monitor 1080x1920 IPS")]        // a resolution: the x has a digit after it
    [InlineData("Pressure treated 2x4 lumber bundle")] // ditto
    [InlineData("Goldshell Mini Doge II")]
    [InlineData("Whatsminer M30S++ 112T")]
    public void A_specification_is_never_a_count(string title)
    {
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
        Assert.Equal(LiveLotSize.SourceSingle, units.Source);
    }

    [Theory]
    [InlineData("16x PCIe riser cable")]
    [InlineData("Canon lens 10x optical zoom")]
    [InlineData("24x DVD burner drive")]
    public void An_x_next_to_a_specification_word_is_not_read_at_all(string title)
    {
        // Read-and-warn would be a warning printed on a card that had already multiplied the money
        // by sixteen. The count is not taken in the first place.
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(1, units.Count);
        Assert.Equal("", units.Refused);
    }

    [Fact]
    public void A_count_too_large_to_be_a_live_lot_is_refused_out_loud()
    {
        var units = LiveLotSize.Read("lot of 400 assorted connectors", null);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
        Assert.Contains("400", units.Refused, StringComparison.Ordinal);
        Assert.Contains("set the quantity", units.Refused, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Silence_about_a_refusal_would_be_indistinguishable_from_not_having_looked()
    {
        // Priced as one, like an ordinary single item — but the seller is told a number WAS found
        // and thrown away, because that is the case where they might disagree.
        var units = LiveLotSize.Read("lot of 400 assorted connectors", null);

        Assert.NotEqual("", units.Refused);
        Assert.Equal("Priced as a single item.", units.Note);
    }

    // ── "Several" without a number ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("MYSTERY MINER LOT")]
    [InlineData("Antminer bundle")]
    [InlineData("Multiple GPU risers")]
    [InlineData("Assorted hashboards")]
    [InlineData("Pair of Antminer S9s")]
    [InlineData("Grab bag of ASIC parts")]
    public void Bulk_wording_with_no_number_is_priced_as_one_and_asks(string title)
    {
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
        Assert.True(units.CountUnstated);
        Assert.Contains("priced as ONE", units.UnstatedNote, StringComparison.Ordinal);
        Assert.Contains("quantity", units.UnstatedNote, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// "Pair" is bulk wording rather than a count of two, on purpose.
    /// </summary>
    /// <remarks>
    /// A pair of speakers is two things and a pair of AirPods is one thing, and nothing in a lot's
    /// name tells them apart. The host can, and they are talking right now — so this asks instead of
    /// halving the ceiling on every product that is sold as a pair.
    /// </remarks>
    [Fact]
    public void A_bracketed_count_inside_bulk_wording_is_a_count()
    {
        // "LOT OF (2) Antminer S9" states its size perfectly clearly, in the one place a bracketed
        // number is unambiguous: inside wording that already said this is several things. Same
        // rule, same regex, as LiquidationParser.ReadUnits reaches it by.
        var units = LiveLotSize.Read("LOT OF (2) Antminer S9 miners", null);

        Assert.Equal(2, units.Count);
        Assert.Equal(LiveLotSize.SourceTitle, units.Source);
        Assert.False(units.CountUnstated);
    }

    [Fact]
    public void A_lot_number_is_not_a_lot()
    {
        // "Lot 12: Antminer S9" is one miner and the twelfth thing to come up tonight. Reading the
        // word as bulk wording here would put an "how many is it?" prompt on every line of a pasted
        // show list.
        var units = LiveLotSize.Read("Lot 12: Bitmain Antminer S9", null);

        Assert.Equal(1, units.Count);
        Assert.False(units.CountUnstated);
    }

    [Fact]
    public void A_pair_is_asked_about_rather_than_assumed_to_be_two()
    {
        var units = LiveLotSize.Read("Pair of Apple AirPods Pro", null);

        Assert.Equal(1, units.Count);
        Assert.True(units.CountUnstated);
    }

    [Fact]
    public void An_ordinary_single_item_says_so_and_asks_nothing()
    {
        var units = LiveLotSize.Read("Bitmain Antminer S19j Pro 104TH", null);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
        Assert.False(units.CountUnstated);
        Assert.Equal("", units.Refused);
        Assert.Equal("Priced as a single item.", units.Note);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_to_read_is_one_thing(string? title)
    {
        var units = LiveLotSize.Read(title, null);

        Assert.Equal(1, units.Count);
        Assert.False(units.CountUnstated);
    }

    // ── The seller's own answer ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_quantity_box_outranks_the_lots_name()
    {
        var units = LiveLotSize.Read("3x Bitmain Antminer S9", 5);

        Assert.Equal(5, units.Count);
        Assert.Equal(LiveLotSize.SourceSeller, units.Source);
    }

    [Fact]
    public void A_typed_one_is_the_undo_for_a_count_read_wrong()
    {
        // The whole safety valve. A name this reader read as three, said by the host to be one,
        // has to be priceable as one — and the sentence has to say the box won.
        var units = LiveLotSize.Read("3x Bitmain Antminer S9", 1);

        Assert.Equal(1, units.Count);
        Assert.False(units.IsLot);
        Assert.Equal(LiveLotSize.SourceSeller, units.Source);
        Assert.Contains("quantity box says 1", units.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_typed_count_never_claims_the_seller_typed_it()
    {
        // The box is also where the screen carries a count forward when a lot's NAME changes under
        // it (taking the photo's name for a lot the title had counted). A sentence saying "because
        // you set the quantity" would be a small lie on the one screen that cannot afford one.
        var units = LiveLotSize.Read("Bitmain Antminer S9", 3);

        Assert.Contains("quantity box says so", units.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("you set", units.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void A_nonsense_quantity_falls_back_to_the_name(int typed)
    {
        var units = LiveLotSize.Read("3x Bitmain Antminer S9", typed);

        Assert.Equal(3, units.Count);
        Assert.Equal(LiveLotSize.SourceTitle, units.Source);
    }

    [Fact]
    public void A_typed_quantity_past_the_cap_is_capped_and_says_so()
    {
        var units = LiveLotSize.Read("Antminer S9", 4000);

        Assert.Equal(LiveLotSize.MaxCredibleUnits, units.Count);
        Assert.Contains("4,000", units.Refused, StringComparison.Ordinal);
        Assert.Contains("Liquidation Lot Analyzer", units.Refused, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cap_is_far_below_the_liquidation_one()
    {
        // A live seller holds the lot up to a camera; a liquidation lot arrives on a dock. Reading
        // 400 units off a name somebody typed one-handed between lots is not the same claim.
        Assert.True(LiveLotSize.MaxCredibleUnits < LiquidationSelectors.MaxCredibleUnits);
    }

    // ── What N of them costs in time ─────────────────────────────────────────────────────────

    [Fact]
    public void One_of_something_has_no_absorption_sentence()
    {
        var (months, days, note) = LiveLotSize.Absorption(1, 4m, 21);

        Assert.Null(months);
        Assert.Equal(21, days);
        Assert.Equal("", note);
    }

    [Fact]
    public void Several_of_something_queue_behind_each_other()
    {
        // Four a month, five of them: the first sells at the market's ordinary speed and the last
        // one waits for the demand the four before it used up.
        var (months, days, note) = LiveLotSize.Absorption(5, 4m, 21);

        Assert.Equal(1.2m, months);                   // 5 / 4, to one place
        Assert.Equal(51, days);                       // 21 + (4 / 4) * 30
        Assert.Contains("5 of them", note, StringComparison.Ordinal);
        Assert.Contains("51 days", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lot_bigger_than_the_market_is_measured_in_months()
    {
        var (months, days, note) = LiveLotSize.Absorption(12, 2m, 30);

        Assert.Equal(6m, months);
        Assert.True(months >= LiveLotSize.SlowClearanceMonths);
        Assert.Equal(195, days);                      // 30 + (11 / 2) * 30
        Assert.Contains("months", note, StringComparison.Ordinal);
    }

    [Fact]
    public void No_sold_history_means_no_estimate_rather_than_a_flattering_one()
    {
        var (months, days, note) = LiveLotSize.Absorption(5, 0m, null);

        Assert.Null(months);
        Assert.Null(days);
        Assert.Contains("no way to say", note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("queue behind each other", note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lot_never_gets_a_haircut_off_its_resale_price()
    {
        // The absorption answer is a TIME, in days and months, and carries no money at all. A
        // "multi-unit discount" would be a number nobody measured, quietly shading the one figure on
        // the card that comes from real sales.
        var (_, _, note) = LiveLotSize.Absorption(5, 4m, 21);

        Assert.DoesNotContain("$", note, StringComparison.Ordinal);
        Assert.DoesNotContain("%", note, StringComparison.Ordinal);
    }
}
