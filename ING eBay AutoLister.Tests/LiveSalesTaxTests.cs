using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The fourth part of what winning a live lot costs. A live marketplace is a facilitator: it collects
/// the buyer's combined state and local sales tax at checkout on the hammer and the premium, and
/// nobody gets to decline it. For fifteen sessions the live card's landed cost was three things and
/// this was not one of them, which made every ceiling on the screen too high by roughly the size of
/// the largest cost it did charge — in the one direction that costs money.
/// </summary>
/// <remarks>
/// The design these tests hold in place: <b>nothing is ever assumed</b>. Most resellers file a resale
/// certificate and pay no sales tax on anything they buy to resell, so a default rate would quietly
/// refuse good lots over a cost the seller does not pay. An empty box charges nothing and says how
/// big that silence is; a ticked certificate charges nothing and says why; a typed rate is charged
/// exactly as typed.
/// </remarks>
public class LiveSalesTaxTests
{
    // ── Nothing entered ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_entered_charges_nothing_and_says_how_big_the_silence_is()
    {
        var read = LiveSalesTax.Read(null, exempt: false, currentBid: 100m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.None, read.Verdict);
        Assert.False(read.Stated);
        Assert.Equal(0m, read.RatePercent);
        Assert.Equal(0m, read.Charged);
        Assert.False(read.Applied);

        // $108 taxable base at the 7.5% US average. Never charged to anything — the size of the
        // silence, so the seller can see whether filling the box in is worth the four seconds.
        Assert.Equal(8.10m, read.Exposure);
        Assert.Contains("$8.10", read.Warning, StringComparison.Ordinal);
        Assert.Contains("Resale cert", read.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_entered_before_the_bidding_starts_still_warns_without_a_figure()
    {
        var read = LiveSalesTax.Read(null, exempt: false, currentBid: 0m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.None, read.Verdict);
        Assert.Equal(0m, read.Exposure);
        Assert.NotEmpty(read.Warning);
        // No dollar figure invented out of a bid nobody has made.
        Assert.DoesNotContain("$", read.Warning, StringComparison.Ordinal);
    }

    // ── The certificate ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_resale_certificate_charges_nothing_and_is_not_a_warning()
    {
        var read = LiveSalesTax.Read(null, exempt: true, currentBid: 100m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.Exempt, read.Verdict);
        Assert.True(read.Exempt);
        Assert.Equal(0m, read.RatePercent);
        Assert.Equal(0m, read.Charged);
        // The card is RIGHT in this state. A warning here would be a warning about an accurate card.
        Assert.Empty(read.Warning);
        Assert.Equal(0m, read.Exposure);
    }

    /// <summary>
    /// The certificate outranks the rate box. A seller who filed one and then typed their state's
    /// rate is still exempt, and a card that charged them would refuse lots over a cost they do not
    /// pay — which is the expensive direction for a reseller who mostly buys to resell.
    /// </summary>
    [Fact]
    public void A_certificate_outranks_a_typed_rate()
    {
        var read = LiveSalesTax.Read(9.25m, exempt: true, currentBid: 200m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.Exempt, read.Verdict);
        Assert.Equal(0m, read.RatePercent);
        Assert.Equal(0m, read.Charged);
        Assert.False(read.Applied);
    }

    // ── A stated zero ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Five states levy no sales tax. A seller in one of them typing 0 is giving a real answer that
    /// happens to cost nothing, and it gets its own state rather than being folded into the silence
    /// — which carries a warning it does not deserve.
    /// </summary>
    [Fact]
    public void A_typed_zero_is_an_answer_and_not_a_silence()
    {
        var read = LiveSalesTax.Read(0m, exempt: false, currentBid: 100m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.Free, read.Verdict);
        Assert.True(read.Stated);
        Assert.Equal(0m, read.RatePercent);
        Assert.Empty(read.Warning);
        Assert.Equal(0m, read.Exposure);
        Assert.Contains("0%", read.Headline, StringComparison.Ordinal);
    }

    // ── A real rate ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Charged on the hammer PLUS the premium, which is <see cref="LotAnalyzer.CostOf"/>'s rule and
    /// the only sales-tax arithmetic this app has. $100 + 8% premium = $108, taxed at 9% = $9.72.
    /// </summary>
    [Fact]
    public void The_tax_is_charged_on_the_hammer_and_the_premium()
    {
        var read = LiveSalesTax.Read(9m, exempt: false, currentBid: 100m, buyerFeePercent: 8m);

        Assert.Equal(LiveTaxVerdicts.Charged, read.Verdict);
        Assert.Equal(9m, read.RatePercent);
        Assert.Equal(108m, read.TaxableBase);
        Assert.Equal(9.72m, read.Charged);
        Assert.True(read.Applied);
        Assert.Empty(read.Warning);
    }

    [Fact]
    public void With_no_premium_the_base_is_the_bid()
    {
        var read = LiveSalesTax.Read(7m, exempt: false, currentBid: 50m, buyerFeePercent: 0m);

        Assert.Equal(50m, read.TaxableBase);
        Assert.Equal(3.50m, read.Charged);
    }

    /// <summary>
    /// The tag beside the strip is what the tax takes off the CEILING, and that is not the rate. The
    /// tax multiplies the landed cost, so the highest affordable bid divides by (1 + rate): 7.5%
    /// takes 6.98% off, not 7.5%. Quoting the rate would overstate what this block costs the seller,
    /// on the one figure they use to judge whether to trust it.
    /// </summary>
    [Fact]
    public void The_cut_to_the_ceiling_is_smaller_than_the_rate()
    {
        var read = LiveSalesTax.Read(7.5m, exempt: false, currentBid: 100m, buyerFeePercent: 0m);

        Assert.Equal(7.0m, read.CutPercent);   // 7.5 / 107.5 = 6.976…, one decimal
        Assert.True(read.CutPercent < read.RatePercent);
    }

    [Fact]
    public void No_rate_means_no_cut_to_the_ceiling()
    {
        foreach (var read in new[]
                 {
                     LiveSalesTax.Read(null, exempt: false, 100m, 8m),
                     LiveSalesTax.Read(null, exempt: true, 100m, 8m),
                     LiveSalesTax.Read(0m, exempt: false, 100m, 8m),
                 })
        {
            Assert.Equal(0m, read.CutPercent);
            Assert.False(read.Applied);
        }
    }

    // ── What it refuses ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A typo of 75 for 7.5 would otherwise wipe out every lot on the screen and report a genuine
    /// one as a loss. Clamped rather than rejected, so a stray keystroke costs a wrong number and
    /// not the answer — the same trade the buyer's premium box makes.
    /// </summary>
    [Fact]
    public void An_absurd_rate_is_clamped_rather_than_refused()
    {
        Assert.Equal(LiveSalesTax.MaxRatePercent, LiveSalesTax.Sanitize(75m));
        Assert.Equal(15m, LiveSalesTax.MaxRatePercent);

        var read = LiveSalesTax.Read(75m, exempt: false, currentBid: 100m, buyerFeePercent: 0m);
        Assert.Equal(LiveTaxVerdicts.Charged, read.Verdict);
        Assert.Equal(15m, read.RatePercent);
    }

    [Fact]
    public void A_negative_rate_is_no_rate()
    {
        Assert.Equal(0m, LiveSalesTax.Sanitize(-4m));

        // And it reads as an answer, not a silence — the box was typed in.
        var read = LiveSalesTax.Read(-4m, exempt: false, currentBid: 100m, buyerFeePercent: 0m);
        Assert.Equal(LiveTaxVerdicts.Free, read.Verdict);
        Assert.Equal(0m, read.RatePercent);
    }

    [Fact]
    public void A_negative_bid_is_no_base()
    {
        var read = LiveSalesTax.Read(8m, exempt: false, currentBid: -25m, buyerFeePercent: 8m);

        Assert.Equal(0m, read.TaxableBase);
        Assert.Equal(0m, read.Charged);
        // Still the charged state: the rate is real and the ceiling below is still costed at it.
        Assert.Equal(LiveTaxVerdicts.Charged, read.Verdict);
        Assert.True(read.Applied);
    }

    /// <summary>
    /// The shipping is deliberately NOT in the base. It is <see cref="LotAnalyzer.CostOf"/>'s rule —
    /// one sales-tax arithmetic in this app — and the honest reading of a rule that genuinely differs
    /// by state: about half tax delivery charges and the rest do not.
    /// </summary>
    [Fact]
    public void The_shipping_is_not_taxed()
    {
        var read = LiveSalesTax.Read(10m, exempt: false, currentBid: 100m, buyerFeePercent: 0m);

        // Nothing about shipping reaches this read at all — the base is the bid, whatever the box
        // is costing to post.
        Assert.Equal(100m, read.TaxableBase);
        Assert.Equal(10m, read.Charged);
    }

    // ── The sentences ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_state_says_something()
    {
        foreach (var read in new[]
                 {
                     LiveSalesTax.Read(null, exempt: false, 100m, 8m),
                     LiveSalesTax.Read(null, exempt: true, 100m, 8m),
                     LiveSalesTax.Read(0m, exempt: false, 100m, 8m),
                     LiveSalesTax.Read(8m, exempt: false, 100m, 8m),
                 })
        {
            Assert.NotEmpty(read.Verdict);
            Assert.NotEmpty(read.Headline);
            Assert.NotEmpty(read.Note);
        }
    }

    /// <summary>Exactly one state warns: the one where the ceiling above it is wrong.</summary>
    [Fact]
    public void Only_the_silence_warns()
    {
        Assert.NotEmpty(LiveSalesTax.Read(null, false, 100m, 8m).Warning);
        Assert.Empty(LiveSalesTax.Read(null, true, 100m, 8m).Warning);
        Assert.Empty(LiveSalesTax.Read(0m, false, 100m, 8m).Warning);
        Assert.Empty(LiveSalesTax.Read(8m, false, 100m, 8m).Warning);
    }

    [Fact]
    public void The_charged_headline_carries_the_cash_and_the_rate()
    {
        var read = LiveSalesTax.Read(8.25m, exempt: false, currentBid: 200m, buyerFeePercent: 8m);

        Assert.Contains("8.25%", read.Headline, StringComparison.Ordinal);
        Assert.Contains(read.Charged.ToString("C"), read.Headline, StringComparison.Ordinal);
        // The base drops its cents when it has none, like every other dollar figure on these strips.
        Assert.Equal(216m, read.TaxableBase);
        Assert.Contains("$216 ", read.Note, StringComparison.Ordinal);
    }

    /// <summary>The app has one figure for the US average and one cap, and they are
    /// <see cref="RetailBuyCosts"/>'s. Two definitions of "typical sales tax" is how a live card and
    /// a deal row end up disagreeing about the same seller's state.</summary>
    [Fact]
    public void The_rate_bounds_are_the_apps_own()
    {
        Assert.Equal(RetailBuyCosts.MaxSalesTaxPercent, LiveSalesTax.MaxRatePercent);
        Assert.Equal(RetailBuyCosts.DefaultSalesTaxPercent, LiveSalesTax.TypicalRatePercent);
    }
}
