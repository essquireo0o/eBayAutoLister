using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// When the metal in a lot is allowed to overrule the comps, and — more importantly — when it is not.
/// </summary>
/// <remarks>
/// <para>
/// The Opportunity Finder priced a "1 OZ Gold USA 100 Dollar Bullion Bar" at <b>$6.99</b>, off two
/// sold comps that were novelty replicas. <see cref="MeltAnchor"/> exists to stop that: an ounce of
/// gold has a published price and does not need a comp.
/// </para>
/// <para>
/// But the fix has a far worse failure available to it than the bug does. If a title is taken at its
/// word, that same $6.99 replica becomes a $4,600 goldmine at the top of the board, and the owner
/// drives somewhere with cash. So the refusals are the first tests in this file and the ones that
/// decide whether any of the rest may ship.
/// </para>
/// </remarks>
public class MeltAnchorTests
{
    // Gold at $4,604/ozt — the price the morning the bug was found — is $148.02/g.
    private const decimal GoldPerGram = 148.02m;

    private static MetalMelt Melt(decimal grams, decimal purityLow, decimal purityHigh, string note = "as stated")
    {
        var content = new MetalContent("XAU", "Gold", grams, purityLow, purityHigh, note);
        return new MetalMelt(content, GoldPerGram,
            Math.Round(grams * purityLow * GoldPerGram, 2),
            Math.Round(grams * purityHigh * GoldPerGram, 2),
            DateTimeOffset.UtcNow);
    }

    /// <summary>One troy ounce of .999 gold: 31.1034768 g x .999 x $148.02 = $4,599.33 of metal.</summary>
    private static MetalMelt OunceBar() => Melt(PreciousMetalPricer.GramsPerTroyOunce, 0.999m, 0.999m, "fine bullion (.999)");

    private static ResalePricing Comps(decimal expected, int pricedComps = 4) => new()
    {
        LookupTitle = "1 OZ Gold USA 100 Dollar Bullion Bar",
        Median = expected,
        ExpectedSale = expected,
        QuickSale = expected,
        PricedCompCount = pricedComps,
        SoldCompCount = pricedComps,
        ConfidenceScore = 70,
    };

    // ── The refusals, which are the whole safety argument ────────────────────────────────────

    [Fact]
    public void A_seven_dollar_ounce_of_gold_is_a_replica_and_is_never_repriced_at_melt()
    {
        var verdict = MeltAnchor.Decide("1 OZ Gold USA 100 Dollar Bullion Bar", OunceBar(), Comps(6.99m), lowestAsk: 6.99m);

        Assert.NotNull(verdict);
        Assert.Equal(MeltOutcome.Contradicted, verdict!.Outcome);
        // The comps price stands. It is almost certainly the right price for what this actually is.
        Assert.False(verdict.SetsPrice);
        Assert.Equal(6.99m, verdict.Pricing!.ExpectedSale);
        // And the row says the contradiction out loud, in both numbers, so a person can judge it.
        Assert.Contains("$6.99", verdict.Note);
        Assert.Contains("almost always the title", verdict.Note);
    }

    [Theory]
    [InlineData(0.01)]    // a picture of a bar
    [InlineData(25)]      // a plated blank
    [InlineData(1609.7)]  // a penny under a third of melt — still nobody's real gold
    public void Anything_far_under_its_own_metal_is_refused(double ask)
    {
        var verdict = MeltAnchor.Decide("1 oz .999 fine gold bar", OunceBar(), comps: null, lowestAsk: (decimal)ask);

        Assert.Equal(MeltOutcome.Contradicted, verdict!.Outcome);
        Assert.False(verdict.SetsPrice);
    }

    [Fact]
    public void A_free_listing_has_no_ask_to_check_the_title_against_so_it_is_refused_too()
    {
        var verdict = MeltAnchor.Decide("FREE 1 oz .999 fine gold bar", OunceBar(), comps: null, lowestAsk: 0m);

        Assert.Equal(MeltOutcome.Contradicted, verdict!.Outcome);
        Assert.Contains("nobody gives metal away", verdict.Note);
    }

    [Fact]
    public void Nothing_to_say_about_a_lot_that_is_not_metal()
    {
        Assert.Null(MeltAnchor.Decide("Antminer S19 95TH/s", melt: null, comps: Comps(2400m), lowestAsk: 1800m));
    }

    // ── The bug it was written for ───────────────────────────────────────────────────────────

    [Fact]
    public void Comps_that_price_gold_under_its_own_weight_are_overruled_by_the_metal()
    {
        // A real scrap lot: 1 ozt of .999, honestly asked at $3,000 — 65% of melt, the band this
        // board exists to find — but the comps still matched novelty bars at $6.99.
        var verdict = MeltAnchor.Decide("1 oz .999 fine gold bullion bar", OunceBar(), Comps(6.99m), lowestAsk: 3000m);

        Assert.Equal(MeltOutcome.Raised, verdict!.Outcome);
        Assert.True(verdict.SetsPrice);
        Assert.Equal(4599.33m, verdict.Pricing!.ExpectedSale);
        // A stated purity makes this arithmetic rather than an opinion, so it is allowed to be
        // confident — this is the app's most checkable price, not its least.
        Assert.Equal(LocalArbitrageEvidence.Confident, verdict.Tier);
        Assert.Contains("below what the metal alone is worth", verdict.Note);
    }

    [Fact]
    public void A_metal_lot_nothing_could_price_gets_priced_by_the_metal()
    {
        var verdict = MeltAnchor.Decide("14k gold scrap lot 40 grams", Melt(40m, 14m / 24m, 14m / 24m, "14k as stated"),
            comps: null, lowestAsk: 1500m);

        Assert.Equal(MeltOutcome.Priced, verdict!.Outcome);
        Assert.Equal(LocalArbitrageEvidence.Confident, verdict.Tier);
        // 40 g x 14/24 x $148.02 = $3,453.80.
        Assert.Equal(3453.80m, verdict.Pricing!.ExpectedSale);
        // Metal sells at metal: there is no spread between what it fetches and what it fetches fast.
        Assert.Equal(verdict.Pricing.ExpectedSale, verdict.Pricing.QuickSale);
    }

    [Fact]
    public void An_assumed_purity_is_an_estimate_however_good_the_arithmetic()
    {
        // The nugget: 2.53 g, no purity stated, so 80-95% and the LOW end is the price.
        var nugget = Melt(2.53m, 0.80m, 0.95m, "purity not stated — natural gold is usually 80-95%");
        var verdict = MeltAnchor.Decide("natural gold nugget 2.53 gram", nugget, Comps(260m, pricedComps: 19), lowestAsk: 125m);

        Assert.Equal(MeltOutcome.Raised, verdict!.Outcome);
        Assert.Equal(LocalArbitrageEvidence.Low, verdict.Tier);
        Assert.Equal(299.59m, verdict.Pricing!.ExpectedSale);   // 2.53 x 0.80 x 148.02
        Assert.Contains("the figure is the low end", verdict.Note);
    }

    // ── Melt is a floor, never a ceiling ─────────────────────────────────────────────────────

    [Fact]
    public void A_coin_worth_more_than_its_gold_keeps_the_comps_price()
    {
        // A graded Saint-Gaudens: the comps are right and the gold in it is merely the downside.
        var verdict = MeltAnchor.Decide("1908 $20 Saint Gaudens Double Eagle MS64", OunceBar(), Comps(9500m), lowestAsk: 8000m);

        Assert.Equal(MeltOutcome.Floor, verdict!.Outcome);
        Assert.False(verdict.SetsPrice);
        Assert.Equal(9500m, verdict.Pricing!.ExpectedSale);
        Assert.Contains("floor under this buy", verdict.Note);
    }

    // ── What it does to a built row ──────────────────────────────────────────────────────────

    [Fact]
    public void A_repriced_row_stops_claiming_the_comps_that_were_overruled()
    {
        var row = new LocalArbitrageOpportunity
        {
            Title = "1 oz .999 fine gold bullion bar",
            EvidenceTier = LocalArbitrageEvidence.Confident,
            EvidenceNote = "Backed by 2 sold comps that match this item.",
            PricedCompCount = 2,
            Valuation = new ResaleValuation
            {
                Status = ValuationStatuses.Comps,
                ProviderId = ResaleValuationProviders.EbayComps,
                LookupUrl = "https://www.ebay.com/sch/i.html?_nkw=gold+bar&LH_Sold=1",
                LookupQuery = "gold bar",
            },
        };

        MeltAnchor.Apply(row, MeltAnchor.Decide(row.Title, OunceBar(), Comps(6.99m), lowestAsk: 3000m)!);

        // The two comps did not price this row and the row must not say they did.
        Assert.Equal(0, row.PricedCompCount);
        Assert.Equal(ValuationStatuses.Melt, row.Valuation!.Status);
        Assert.Equal(ResaleValuationProviders.MetalMelt, row.Valuation.ProviderId);
        Assert.Equal(LocalArbitrageEvidence.Confident, row.EvidenceTier);
        Assert.DoesNotContain("Backed by 2 sold comps", row.EvidenceNote);
        // The sold search survives: a price the seller can check by hand beats one they must trust.
        Assert.Equal("https://www.ebay.com/sch/i.html?_nkw=gold+bar&LH_Sold=1", row.Valuation.LookupUrl);
    }

    [Fact]
    public void A_floor_and_a_contradiction_add_a_sentence_and_change_nothing_else()
    {
        foreach (var (verdict, ask) in new[]
                 {
                     (MeltAnchor.Decide("1908 $20 Saint Gaudens", OunceBar(), Comps(9500m), 8000m)!, 8000m),
                     (MeltAnchor.Decide("1 OZ Gold Bullion Bar", OunceBar(), Comps(6.99m), 6.99m)!, 6.99m),
                 })
        {
            var row = new LocalArbitrageOpportunity
            {
                Title = "row",
                EvidenceTier = LocalArbitrageEvidence.Confident,
                EvidenceNote = "Backed by 4 sold comps that match this item.",
                PricedCompCount = 4,
                Valuation = new ResaleValuation { Status = ValuationStatuses.Comps },
            };

            MeltAnchor.Apply(row, verdict);

            Assert.Equal(LocalArbitrageEvidence.Confident, row.EvidenceTier);
            Assert.Equal(4, row.PricedCompCount);
            Assert.Equal(ValuationStatuses.Comps, row.Valuation!.Status);
            Assert.StartsWith("Backed by 4 sold comps that match this item.", row.EvidenceNote);
            Assert.True(row.EvidenceNote.Length > "Backed by 4 sold comps that match this item.".Length,
                $"the melt sentence should have been appended (ask {ask:C2})");
        }
    }

    // ── The band the board exists to find must price normally ────────────────────────────────

    [Theory]
    [InlineData(1609.8)] // a penny over a third of melt — the refusal must not overreach
    [InlineData(2300)]   // a pawn-counter offer
    [InlineData(3700)]   // an estate lot at 80%
    [InlineData(4599)]   // at melt
    public void A_real_bargain_on_real_metal_is_priced_not_refused(double ask)
    {
        var verdict = MeltAnchor.Decide("1 oz .999 fine gold bar", OunceBar(), comps: null, lowestAsk: (decimal)ask);

        Assert.Equal(MeltOutcome.Priced, verdict!.Outcome);
        Assert.Equal(4599.33m, verdict.Pricing!.ExpectedSale);
    }
}
