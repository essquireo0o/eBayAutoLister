using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A live show is where pricing metal off comps goes wrong fastest.
/// </summary>
/// <remarks>
/// <para>
/// Lots go past at one a minute, the host's wording is auction talk rather than an eBay title, and
/// the seller has about twenty seconds to decide. The sold comps behind "1 oz gold bar" are
/// whatever else was called that &mdash; the plated souvenirs included &mdash; so the one lot on a
/// coin show whose value is not in doubt was the one the card was worst at.
/// </para>
/// <para>
/// Weight &times; purity &times; the published spot price does not depend on any of that being
/// right. It overrules the comps in ONE direction: when they came out BELOW the metal. Comps ABOVE
/// the metal are a numismatic premium the spot price knows nothing about, and are left alone.
/// </para>
/// </remarks>
public class LiveBidMeltTests
{
    private static readonly LiveBidAdvisor Advisor =
        new(new ProfitCalculator(), new JackpotHunter(new ProfitCalculator()));

    private static readonly FeeProfile Fees = new();

    /// <summary>Comps that priced the lot at <paramref name="each"/>, the way a thin sold search would.</summary>
    private static MarketAnalysisResult Comps(decimal each, int count = 8)
    {
        var rows = Enumerable.Range(0, count).Select(i => new MarketplaceComparableResult
        {
            Title = "1 oz gold bar", SoldPrice = each, SoldDate = DateTime.UtcNow.AddDays(-3 - i),
        }).ToList();

        return new MarketAnalysisResult
        {
            PriceEstimate = new PriceEstimate
            {
                MedianPrice = each,
                ExpectedSalePrice = each,
                QuickSalePrice = each * 0.85m,
                Percentile25 = each * 0.9m,
                Percentile75 = each * 1.1m,
                LocalMedianPrice = each,
                LocalExpectedSalePrice = each,
                LocalWeight = 1m,
                PricedOnCompCount = count,
                IdentityVerified = true,
                LocalOldestSoldAtUtc = DateTime.UtcNow.AddDays(-3 - count),
                LocalNewestSoldAtUtc = DateTime.UtcNow.AddDays(-3),
            },
            SellThrough = new SellThroughAnalysis
            {
                SoldComparableCount = count,
                ActiveComparableCount = 4,
                SellThroughRate = 0.8m,
                SellThroughScore = 72,
                Interpretation = "Very Strong",
                LiquidityLevel = "Fast Mover",
            },
            Confidence = new ConfidenceBreakdown { Score = 70, Level = "Good" },
            TopSoldComparables = rows,
            AllSoldComparables = rows,
        };
    }

    private static MetalMelt Gold(decimal grams = 31.103m, decimal spotPerGram = 148m) =>
        new(new MetalContent("XAU", "Gold", grams, 0.999m, 0.999m, ".999 fine"),
            spotPerGram, grams * 0.999m * spotPerGram, grams * 0.999m * spotPerGram, DateTimeOffset.UtcNow);

    private static LiveBidRequest Bid(decimal currentBid) => new() { Title = "1 oz gold bar", CurrentBid = currentBid };

    [Fact]
    public void Comps_below_the_metal_are_overruled_by_the_metal()
    {
        var melt = Gold();                                   // about $4,600 of gold
        var verdict = MeltAnchor.Decide("1 oz gold bar", melt, ResalePricing.From(Comps(90m), "1 oz gold bar"),
                                        lowestAsk: 60m, askIsFirm: false);
        Assert.NotNull(verdict);

        var card = Advisor.Build("1 oz gold bar", Comps(90m), Bid(60m), Fees, melt: verdict);

        // The comps said $90 for an ounce of gold. They were describing something else.
        Assert.True(card.ResalePrice > 1_000m,
            $"the card resold an ounce of gold at {card.ResalePrice:C}, which is the bug this exists to stop");
        Assert.True(card.Melt.Readable);
        Assert.True(card.Melt.SetPrice);
        Assert.Equal("Gold", card.Melt.Metal);
    }

    [Fact]
    public void Comps_above_the_metal_are_left_alone_because_a_premium_is_real()
    {
        // A graded coin sells for more than the gold in it, and the spot price cannot see why.
        var melt = Gold();
        var comps = Comps(6_000m);
        var verdict = MeltAnchor.Decide("1 oz gold bar", melt, ResalePricing.From(comps, "1 oz gold bar"), 3_000m);

        var card = Advisor.Build("1 oz gold bar", comps, Bid(3_000m), Fees, melt: verdict);

        Assert.True(card.ResalePrice >= 5_000m,
            $"a numismatic premium was cut back to melt: {card.ResalePrice:C}");
        // Stated either way — the seller should still be told what the metal alone is worth.
        Assert.True(card.Melt.Readable);
        Assert.False(card.Melt.SetPrice);
    }

    [Fact]
    public void A_lot_that_is_not_metal_says_so_rather_than_saying_nothing()
    {
        // The house rule for these blocks: silence must not mean both "not bullion" and "nothing
        // looked". See LiveMeltRead.
        var card = Advisor.Build("Antminer S19j Pro", Comps(1_800m), Bid(900m), Fees);

        Assert.False(card.Melt.Readable);
        Assert.False(card.Melt.SetPrice);
        Assert.Equal("none", card.Melt.Outcome);
        Assert.Equal("", card.Melt.Metal);
    }

    [Fact]
    public void The_card_carries_the_arithmetic_so_the_seller_can_check_it_mid_show()
    {
        var melt = Gold();
        var verdict = MeltAnchor.Decide("1 oz gold bar", melt, ResalePricing.From(Comps(90m), "1 oz gold bar"),
                                        lowestAsk: 60m, askIsFirm: false);

        var card = Advisor.Build("1 oz gold bar", Comps(90m), Bid(60m), Fees, melt: verdict);

        // Twenty seconds to decide means the number has to be checkable against a phone, not taken
        // on faith: the spot price used and the metal value both travel on the card.
        Assert.True(card.Melt.SpotPerGram > 0, "the spot price used is not on the card");
        Assert.True(card.Melt.MeltValue > 0, "the metal value is not on the card");
        Assert.False(string.IsNullOrWhiteSpace(card.Melt.Note), "the card does not say why the metal priced it");
    }

    [Fact]
    public void A_fixed_ask_that_contradicts_the_title_is_still_refused()
    {
        // The $6.99 "1 OZ gold bar" on a shelf: whichever of the two that lot really is, telling the
        // seller to bid four figures on it is the expensive direction to be wrong in. Unchanged.
        var verdict = MeltAnchor.Decide("1 oz gold bar", Gold(), ResalePricing.From(Comps(7m), "1 oz gold bar"), 6.99m);

        Assert.True(verdict is null || verdict.Outcome == MeltOutcome.Contradicted,
            $"a $6.99 ask for an ounce of gold produced {verdict?.Outcome.ToString() ?? "null"}, not a refusal");
    }

    [Fact]
    public void An_opening_bid_of_a_dollar_is_not_a_contradiction_because_it_is_not_an_ask()
    {
        // The guard above reads a price far under the metal as "the title is a lie". At an auction
        // that reasoning does not hold: bidding OPENS near zero on purpose. Reading the first bid as
        // a contradiction refused to price every real bullion lot for exactly the bids where the
        // seller needs the number, which is the whole point of a live card.
        var verdict = MeltAnchor.Decide("1 oz gold bar", Gold(), ResalePricing.From(Comps(90m), "1 oz gold bar"),
                                        lowestAsk: 1m, askIsFirm: false);

        Assert.NotNull(verdict);
        Assert.NotEqual(MeltOutcome.Contradicted, verdict!.Outcome);
        Assert.True(verdict.SetsPrice, "an opening bid on real bullion left the card on comps that priced gold at $90");

        // And the seller is told what the missing check cost: nothing here vouches for the title.
        Assert.Contains("corroborates the title", verdict.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_card_prices_from_the_very_first_bid()
    {
        var melt = Gold();
        var verdict = MeltAnchor.Decide("1 oz gold bar", melt, ResalePricing.From(Comps(90m), "1 oz gold bar"),
                                        lowestAsk: 1m, askIsFirm: false);

        var card = Advisor.Build("1 oz gold bar", Comps(90m), Bid(1m), Fees, melt: verdict);

        Assert.True(card.Melt.SetPrice);
        Assert.True(card.ResalePrice > 1_000m,
            $"a $1 opening bid on an ounce of gold still resold at {card.ResalePrice:C}");
    }
}
