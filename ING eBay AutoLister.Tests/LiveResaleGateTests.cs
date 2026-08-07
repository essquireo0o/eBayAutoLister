using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What eBay's own selling policies say about the lot on screen, read off its name.
/// </summary>
/// <remarks>
/// The card has always priced an eBay listing and has never asked whether that listing is allowed
/// to exist. These cover the three things the read has to get right to be worth having: it refuses
/// the lots eBay refuses, it stays quiet on the ordinary ones, and it never claims more than a name
/// can tell it.
/// </remarks>
public class LiveResaleGateTests
{
    // ── What cannot be listed ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Louis Vuitton Neverfull replica bag")]
    [InlineData("Gucci knockoff belt")]
    [InlineData("Knock-off Yeezy 350")]
    [InlineData("Counterfeit Rolex Submariner")]
    [InlineData("Bootleg Grateful Dead tee 1987")]
    public void A_replica_cannot_be_listed_at_all(string name)
    {
        var read = LiveResaleGate.Read(name, 400m);

        Assert.Equal(LiveGateVerdicts.Blocked, read.Verdict);
        Assert.True(read.Stops);
        Assert.Equal("Replicas and counterfeits", read.RuleName);
        Assert.Contains("eBay", read.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The empty container is a legitimate collectible and a live-show staple. Carved out by hand
    /// rather than by dropping the word, because loose rounds really are refused.
    /// </summary>
    [Theory]
    [InlineData("Vintage 30 cal ammo can, empty", false)]
    [InlineData("Wooden ammo crate 1943", false)]
    [InlineData("Federal 9mm ammunition 50 rounds", true)]
    [InlineData("1lb black powder", true)]
    public void The_empty_ammo_can_is_a_collectible_and_the_rounds_are_not(string name, bool blocked)
    {
        Assert.Equal(blocked, LiveResaleGate.Read(name, 40m).Stops);
    }

    /// <summary>
    /// Swatched, tested and opened cosmetics are refused by eBay, and beauty is one of the biggest
    /// categories on a live feed.
    /// </summary>
    [Fact]
    public void Used_cosmetics_cannot_be_listed()
    {
        var read = LiveResaleGate.Read("Charlotte Tilbury swatched lipstick bundle", 60m);

        Assert.True(read.Stops);
        Assert.Equal("Used cosmetics", read.RuleName);
    }

    [Fact]
    public void Vapes_and_nicotine_cannot_be_listed()
    {
        Assert.True(LiveResaleGate.Read("Elf Bar vape 5000 puff", 12m).Stops);
        Assert.True(LiveResaleGate.Read("Juul starter kit", 20m).Stops);
    }

    /// <summary>The cigar BOX is the collectible, and it is legal. A rule that stopped it would be
    /// a rule the seller learns to ignore, which is the one failure a stop-the-call read cannot
    /// survive.</summary>
    [Fact]
    public void An_empty_cigar_box_is_not_a_tobacco_lot()
    {
        Assert.False(LiveResaleGate.Read("Antique wooden cigar box, empty", 30m).Stops);
    }

    // ── What is allowed on a condition ───────────────────────────────────────────────────────

    [Fact]
    public void A_sealed_bottle_is_restricted_and_never_blocked()
    {
        var read = LiveResaleGate.Read("Pappy Van Winkle 15 year bourbon, sealed", 900m);

        Assert.Equal(LiveGateVerdicts.Restricted, read.Verdict);
        Assert.False(read.Stops);
        // Because only the seller knows whether they are approved, and the sentence has to say so.
        Assert.Contains("empty", read.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(read.Reason);
    }

    /// <summary>A roll of tape is not a bottle of whisky.</summary>
    [Fact]
    public void Scotch_tape_is_not_alcohol()
    {
        Assert.Equal(LiveGateVerdicts.Clear, LiveResaleGate.Read("Scotch tape dispenser lot", 9m).Verdict);
    }

    [Fact]
    public void Recalled_goods_are_restricted()
    {
        Assert.Equal(LiveGateVerdicts.Restricted, LiveResaleGate.Read("Recalled Peloton seat post", 80m).Verdict);
    }

    // ── What goes through the authenticator ──────────────────────────────────────────────────

    [Fact]
    public void Sneakers_over_the_bar_route_through_ebays_authenticator()
    {
        var read = LiveResaleGate.Read("Air Jordan 1 Retro High Chicago", 420m);

        Assert.Equal(LiveGateVerdicts.Authenticated, read.Verdict);
        Assert.True(read.OverThreshold);
        Assert.Equal(150m, read.ThresholdPrice);
        Assert.Equal(LiveResaleGate.AuthenticationDays, read.ExtraDaysToCash);
        Assert.False(read.Stops);
        Assert.NotEmpty(read.Warning);
    }

    /// <summary>
    /// Under its own threshold the category is not a finding. The rule is still named — "sneakers
    /// go through the hub over $150 and these price at $60" is a useful thing to have read and a
    /// useless thing to be interrupted about.
    /// </summary>
    [Fact]
    public void The_same_category_under_the_bar_is_clear_and_still_says_the_rule()
    {
        var read = LiveResaleGate.Read("Nike Dunk Low panda sneakers", 60m);

        Assert.Equal(LiveGateVerdicts.Clear, read.Verdict);
        Assert.Equal("Sneakers", read.RuleName);
        Assert.False(read.OverThreshold);
        Assert.Empty(read.Warning);
        Assert.Contains("$150", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The threshold is a sale price, and a card nothing priced has no sale price. The category is
    /// reported and the bar is left explicitly unchecked rather than assumed either way.
    /// </summary>
    [Fact]
    public void An_unpriced_lot_reports_the_category_and_refuses_to_guess_the_bar()
    {
        var read = LiveResaleGate.Read("PSA 10 Charizard holo", null);

        Assert.Equal(LiveGateVerdicts.Authenticated, read.Verdict);
        Assert.False(read.OverThreshold);
        Assert.Equal(0m, read.PricedAt);
        Assert.Contains("Nothing priced this one", read.Note, StringComparison.Ordinal);
    }

    /// <summary>
    /// The authenticated rules run cheapest bar first, so a Cartier bracelet is measured against
    /// jewellery's $500 rather than silently cleared at the watch bar of $2,000.
    /// </summary>
    [Fact]
    public void A_category_in_two_rules_is_measured_against_the_lower_bar()
    {
        var read = LiveResaleGate.Read("Cartier Love bracelet 18k gold", 900m);

        Assert.Equal(LiveGateVerdicts.Authenticated, read.Verdict);
        Assert.True(read.OverThreshold);
        Assert.Equal(500m, read.ThresholdPrice);
    }

    [Fact]
    public void Graded_cards_over_the_bar_are_authenticated()
    {
        var read = LiveResaleGate.Read("2018 Panini Prizm Luka Doncic rookie card BGS 9.5", 1_200m);

        Assert.Equal(LiveGateVerdicts.Authenticated, read.Verdict);
        Assert.Equal(250m, read.ThresholdPrice);
        Assert.True(read.OverThreshold);
    }

    /// <summary>Remote-control everything is a live-show staple, so "RC" is deliberately not a
    /// rookie card.</summary>
    [Fact]
    public void An_rc_car_is_not_a_rookie_card()
    {
        Assert.Equal(LiveGateVerdicts.Clear, LiveResaleGate.Read("Traxxas RC car roller", 300m).Verdict);
    }

    // ── The ordinary lot ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Nearly every lot lands here, and the read still says so out loud. A block that only appeared
    /// once a policy was tripped would be a block whose silence means both "eBay is fine with this"
    /// and "nothing ever looked", and the second is the expensive reading.
    /// </summary>
    [Fact]
    public void An_ordinary_lot_is_clear_and_says_that_it_looked()
    {
        var read = LiveResaleGate.Read("Bitmain Antminer S19 Pro 110TH", 900m);

        Assert.True(read.Readable);
        Assert.Equal(LiveGateVerdicts.Clear, read.Verdict);
        Assert.NotEmpty(read.Headline);
        Assert.NotEmpty(read.Note);
        Assert.Empty(read.Warning);
        Assert.Empty(read.Reason);
        Assert.Empty(read.Tag);
    }

    [Fact]
    public void No_name_is_unreadable_and_says_nothing()
    {
        foreach (var name in new[] { null, "", "   " })
        {
            var read = LiveResaleGate.Read(name, 100m);
            Assert.False(read.Readable);
            Assert.Equal(LiveGateVerdicts.Unreadable, read.Verdict);
            Assert.Empty(read.Headline);
            Assert.Empty(read.Warning);
        }
    }

    // ── What it refuses to claim ─────────────────────────────────────────────────────────────

    /// <summary>
    /// It reads a NAME. A replica advertised as genuine matches nothing here, and the read never
    /// claims an item IS authentic — only that eBay's authenticator will be the one to decide.
    /// </summary>
    [Fact]
    public void It_never_claims_a_lot_is_genuine()
    {
        var read = LiveResaleGate.Read("Louis Vuitton Speedy 30, 100% authentic guaranteed", 700m);

        Assert.Equal(LiveGateVerdicts.Authenticated, read.Verdict);
        Assert.DoesNotContain("is genuine", read.Note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is real", read.Note, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The matched words go on the read, because a rule that fires wrongly is fixed by retyping the
    /// name — and the seller cannot do that if they cannot see what fired.
    /// </summary>
    [Fact]
    public void Every_finding_names_the_words_that_fired_it()
    {
        foreach (var name in new[]
        {
            "Chanel classic flap replica", "sealed bourbon decanter", "Air Jordan 4 Bred",
        })
        {
            var read = LiveResaleGate.Read(name, 800m);
            Assert.NotEqual(LiveGateVerdicts.Clear, read.Verdict);
            Assert.NotEmpty(read.Matched);
            Assert.Contains(read.Matched, name, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Pure: no clock, no state, no order dependence. The same name is the same answer
    /// however many times a climbing bid re-prices the card.</summary>
    [Fact]
    public void The_same_name_is_always_the_same_answer()
    {
        var first = LiveResaleGate.Read("Supreme box logo hoodie FW17", 380m);
        var again = LiveResaleGate.Read("Supreme box logo hoodie FW17", 380m);

        Assert.Equal(first.Verdict, again.Verdict);
        Assert.Equal(first.Headline, again.Headline);
        Assert.Equal(first.Warning, again.Warning);
        Assert.Equal(first.Tag, again.Tag);
    }

    /// <summary>The tag is the strip's glance version, and only the states worth glancing at get
    /// one. A badge reading OK on every ordinary card is how a seller learns to stop reading.</summary>
    [Fact]
    public void Only_the_states_worth_flagging_carry_a_tag()
    {
        Assert.Equal("CAN'T LIST", LiveResaleGate.Read("replica Birkin", 900m).Tag);
        Assert.Equal("CHECK FIRST", LiveResaleGate.Read("sealed bottle of tequila", 90m).Tag);
        Assert.Contains("DAYS TO CASH", LiveResaleGate.Read("Air Jordan 1 Bred", 400m).Tag,
            StringComparison.Ordinal);
        Assert.Empty(LiveResaleGate.Read("Milwaukee M18 drill kit", 120m).Tag);
    }
}
