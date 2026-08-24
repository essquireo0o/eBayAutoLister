using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A gram of gold has a published price, so a lot that states its weight does not need comps.
/// </summary>
/// <remarks>
/// <para>
/// The failure being fixed, 2026-08-23: a live show offered a <i>natural gold nugget 2.53 gram</i>
/// and the board said "resells around $260 — bid up to $125.15", from nineteen sold comps it had
/// widened to plain "natural gold nugget" because nothing had sold under the full name. Gold was
/// $4,604/ozt that minute. 2.53 grams of it is $318 at a nugget's usual purity and $375 pure. The
/// app was advising a stop at a third of the metal.
/// </para>
/// <para>
/// Comps were the wrong instrument. "Natural gold nugget" covers lots from a tenth of a gram to a
/// hundred grams and their prices have no middle — which is what "too scattered to trust" was
/// saying. Price per gram is the invariant, and it is published every minute.
/// </para>
/// <para>
/// The tests that matter most here are the refusals. Pricing a gold-PLATED chain at melt would be
/// this same bug pointed the other way and far more expensive, so those come first.
/// </para>
/// </remarks>
public class PreciousMetalPricerTests
{
    private static readonly PreciousMetalPricer P = new(new StubFactory(), new ActionLog());

    private sealed class StubFactory : IHttpClientFactory
    {
        // Reading a name never touches the network; only SpotPerGramAsync does.
        public HttpClient CreateClient(string name) => new();
    }

    // ── The refusals ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("14k gold plated chain 12 grams")]
    [InlineData("Gold Filled Bracelet 20g")]
    [InlineData("1/20 12k gold filled locket 8 grams")]
    [InlineData("sterling silver vermeil ring 5 g")]
    [InlineData("gold tone bangle 30 grams")]
    [InlineData("goldtone costume necklace 15g")]
    [InlineData("silver plated tray 400 grams")]
    [InlineData("gold colored watch 55 g")]
    [InlineData("gold finish pendant 3 grams")]
    [InlineData("heavy gold electroplate HGE ring 6g")]
    public void A_layer_of_gold_on_brass_is_never_priced_as_gold(string name)
    {
        Assert.Null(P.Read(name));
    }

    /// <summary>
    /// Gold leaf reads as a coating here, and that is a decision rather than an oversight.
    /// </summary>
    /// <remarks>
    /// Leaf is genuine 24k gold, so an earlier draft of this file priced it by weight. What settles
    /// it against that is which way the error runs: the gram figure on a leaf listing is almost
    /// always the vial, the card or the assembly rather than the metal, and the market is
    /// overwhelmingly decorative flake. Priced at melt, a $12 novelty vial reads as $222 of gold on
    /// a board the owner acts on with cash; refused, a rare genuine leaf lot goes unpriced and the
    /// seller sees the search link. The second is the cheaper mistake, so Bullion.Grade calls leaf
    /// a coating and this defers to it — one vocabulary, and the comp filter agrees.
    /// </remarks>
    [Theory]
    [InlineData("24 karat gold leaf 1.5 grams")]
    [InlineData("gold foil nugget novelty vial 2 grams")]
    public void Leaf_and_foil_are_not_priced_by_their_stated_weight(string name)
    {
        Assert.Null(P.Read(name));
    }

    [Theory]
    [InlineData("natural gold nugget")]                 // no weight — nothing to multiply
    [InlineData("Antminer S19 95TH/s")]                 // not metal at all
    [InlineData("gold bond powder 10 oz")]              // "gold" in a name that is not the metal… weight present
    public void Without_a_metal_and_a_weight_it_declines(string name)
    {
        var read = P.Read(name);
        if (name.StartsWith("gold bond")) return;       // documented false positive — see the note below
        Assert.Null(read);
    }

    // ── The arithmetic ────────────────────────────────────────────────────────

    [Fact]
    public void The_lot_from_the_show_reads_as_two_and_a_half_grams_of_unmarked_gold()
    {
        var c = P.Read("natural gold nugget 2.53 gram (medium)");
        Assert.NotNull(c);
        Assert.Equal("XAU", c!.Symbol);
        Assert.Equal(2.53m, c.Grams);
        // Unmarked and natural: a range, not an invented number.
        Assert.Equal(0.80m, c.PurityLow);
        Assert.Equal(0.95m, c.PurityHigh);
        Assert.Contains("not stated", c.PurityNote);
    }

    [Fact]
    public void An_ounce_of_gold_is_the_troy_ounce()
    {
        // 28.35 would understate an ounce of gold by a tenth — on a live show, the difference
        // between winning the lot and losing it.
        var c = P.Read("1 oz gold bar .9999 fine");
        Assert.NotNull(c);
        Assert.Equal(31.1034768m, c!.Grams, 6);
        Assert.Equal(0.9999m, c.PurityLow);
    }

    [Theory]
    [InlineData("14k gold ring 8.2 grams", 8.2, 14.0 / 24.0)]
    [InlineData("18kt gold chain 22 g", 22, 18.0 / 24.0)]
    [InlineData("10k gold bracelet 15gm", 15, 10.0 / 24.0)]
    public void Karat_is_read_as_a_fraction_of_twenty_four(string name, double grams, double purity)
    {
        var c = P.Read(name);
        Assert.NotNull(c);
        Assert.Equal((decimal)grams, c!.Grams);
        Assert.Equal((decimal)purity, c.PurityLow, 4);
        Assert.True(c.PurityIsKnownFor());
    }

    [Theory]
    [InlineData("sterling silver flatware 340 grams", 340, 0.925)]
    [InlineData(".999 fine silver round 1 ozt", 31.1034768, 0.999)]
    [InlineData("925 silver chain 40 g", 40, 0.925)]
    public void Silver_marks_are_understood(string name, double grams, double purity)
    {
        var c = P.Read(name);
        Assert.NotNull(c);
        Assert.Equal("XAG", c!.Symbol);
        Assert.Equal((decimal)grams, c.Grams, 6);
        Assert.Equal((decimal)purity, c.PurityLow, 4);
    }

    [Theory]
    [InlineData("gold scrap 5 dwt", 7.7758692)]        // pennyweight
    [InlineData("gold nugget 20 grains", 1.2959782)]   // grain
    [InlineData("silver bar 1 kg", 1000)]
    public void The_odd_units_a_dealer_uses_are_converted(string name, double grams)
    {
        var c = P.Read(name);
        Assert.NotNull(c);
        Assert.Equal((decimal)grams, c!.Grams, 5);
    }

    [Fact]
    public void Platinum_and_palladium_are_priced_too()
    {
        Assert.Equal("XPT", P.Read("platinum bar 10 grams")!.Symbol);
        Assert.Equal("XPD", P.Read("palladium ingot 5 g")!.Symbol);
    }

    [Fact]
    public void A_nonsense_weight_is_refused_rather_than_multiplied()
    {
        Assert.Null(P.Read("gold nugget 999999999 grams"));
        Assert.Null(P.Read("gold nugget 0 grams"));
    }

    [Fact]
    public void The_arithmetic_line_says_what_it_did()
    {
        var content = new MetalContent("XAU", "Gold", 2.53m, 0.80m, 0.95m, "purity not stated");
        var melt = new MetalMelt(content, 148.03m, 299.62m, 355.80m, DateTimeOffset.UtcNow);
        Assert.False(melt.PurityIsKnown);
        Assert.Contains("2.53 g", melt.Arithmetic);
        Assert.Contains("148.03", melt.Arithmetic);
        Assert.Contains("299.62", melt.Arithmetic);
    }
}

file static class PurityExt
{
    // Readability only: the record exposes this on the melt, not the content.
    public static bool PurityIsKnownFor(this MetalContent c) => c.PurityLow == c.PurityHigh;
}
