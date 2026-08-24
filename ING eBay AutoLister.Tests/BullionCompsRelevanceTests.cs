using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A gold-clad brass souvenir and an ounce of gold are written with the same words.
/// </summary>
/// <remarks>
/// <para>
/// The row that started this, off the Opportunity Finder board: <i>1 OZ Gold USA 100 Dollar
/// Bullion Bar</i>, priced at <b>$6.99</b> from two sold comps, on a day an ounce of gold was
/// $4,604. The comp search had matched the novelty bars — brass, stamped like a banknote, plated —
/// because every word of their titles is also in a real bar's.
/// </para>
/// <para>
/// The tests are written from both ends on purpose. Whether that particular row was the real bar
/// or the souvenir does not change the fix, and the expensive direction is the second one: a $7
/// bar priced off bullion tells the owner to bid two thousand dollars.
/// </para>
/// <para>
/// The refusals matter as much as the exclusions. A title that commits to nothing — "14k gold
/// ring", no weight, plain "silver coin" — must keep its comps, because a row with no comps is a
/// row with no price, and this board already has too many of those.
/// </para>
/// </remarks>
public class BullionCompsRelevanceTests
{
    private static (ProductNormalizer Normalizer, ComparableMatcher Matcher) CreateMatcher()
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        return (normalizer, new ComparableMatcher(normalizer));
    }

    private static MarketplaceComparableResult Candidate(string title) => new()
    {
        ItemId = "1", Title = title, SoldPrice = 6.99m, Quantity = 1,
    };

    // ── The board row ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_real_ounce_of_gold_is_not_priced_off_gold_clad_souvenirs()
    {
        var (normalizer, matcher) = CreateMatcher();
        var target = normalizer.Normalize("1 oz .9999 Fine Gold Bar Credit Suisse Sealed");

        var match = matcher.Match(target, Candidate("1 OZ Gold Clad USA 100 Dollar Bill Novelty Bullion Bar"));

        Assert.True(match.Excluded);
        Assert.Contains("novelty", match.ExclusionReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void And_a_seven_dollar_souvenir_is_never_priced_off_real_bullion()
    {
        // The direction that costs money. $4,604 of "resale" on a brass bar, and a bid to match.
        var (normalizer, matcher) = CreateMatcher();
        var target = normalizer.Normalize("24K Gold Plated USA 100 Dollar Bill Bar Novelty Ingot");

        var match = matcher.Match(target, Candidate("1 oz Gold Bar .9999 Fine PAMP Suisse"));

        Assert.True(match.Excluded);
        Assert.Contains("solid metal", match.ExclusionReason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("1 oz Silver Round .999 Fine", "1 oz Silver Plated Round Novelty Coin")]
    [InlineData("Sterling Silver Flatware Set 620 grams", "Silver Plate Flatware Set Rogers")]
    [InlineData("14k Gold Chain 12.4 grams Solid", "14k Gold Filled Chain Necklace")]
    [InlineData("Natural Gold Nugget 2.53 gram Placer", "Gold Foil Nugget Novelty Vial")]
    public void Solid_and_coated_are_never_each_others_comps(string target, string candidate)
    {
        var (normalizer, matcher) = CreateMatcher();

        var match = matcher.Match(normalizer.Normalize(target), Candidate(candidate));

        Assert.True(match.Excluded, $"expected '{candidate}' to be excluded from '{target}'");
    }

    // ── The refusals ──────────────────────────────────────────────────────────────

    [Fact]
    public void Two_real_bars_still_compare()
    {
        var (normalizer, matcher) = CreateMatcher();
        var target = normalizer.Normalize("1 oz Gold Bar .9999 Fine PAMP Suisse");

        var match = matcher.Match(target, Candidate("1 oz Gold Bar 999.9 Fine Credit Suisse Sealed Assay"));

        Assert.False(match.Excluded, match.ExclusionReason);
    }

    [Fact]
    public void Two_souvenirs_still_compare()
    {
        // Neither title says "replica" — that word is its own exclusion, older than this rule and
        // rightly so, and putting it on one side only would be testing that rule instead of this.
        var (normalizer, matcher) = CreateMatcher();
        var target = normalizer.Normalize("24K Gold Plated 100 Dollar Bill Bar Novelty");

        var match = matcher.Match(target, Candidate("Gold Plated USA 100 Dollar Banknote Bar Novelty Souvenir"));

        Assert.False(match.Excluded, match.ExclusionReason);
    }

    [Fact]
    public void A_vaguer_comp_is_still_a_comp()
    {
        // "Solid" against "says nothing" is not a conflict. It is the ordinary case, and dropping
        // it would leave the honest rows with no sold data at all.
        var (normalizer, matcher) = CreateMatcher();
        var target = normalizer.Normalize("1 oz Silver Round .999 Fine Buffalo");

        var match = matcher.Match(target, Candidate("Silver Buffalo Round"));

        Assert.False(match.Excluded, match.ExclusionReason);
    }

    // ── The grader on its own ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Apple iPhone 13 128GB Space Gray Unlocked")]
    [InlineData("Antminer S19j Pro 104TH Bitcoin Miner")]
    [InlineData("Silver Ford Focus Wheel Center Cap Set")]   // names a metal, states no metal
    public void A_title_that_is_not_about_metal_is_graded_out_of_the_way(string title)
    {
        Assert.NotEqual(BullionGrade.Solid, Bullion.Grade(title));
        Assert.NotEqual(BullionGrade.Novelty, Bullion.Grade(title));
    }

    [Theory]
    [InlineData("1 oz .999 Fine Silver Bar")]
    [InlineData("Sterling Silver Bracelet 24.1 g")]
    [InlineData("18kt Gold Wedding Band")]
    [InlineData("Platinum Bar 5 grams .9995")]
    public void A_stated_weight_or_hallmark_reads_as_solid(string title) =>
        Assert.Equal(BullionGrade.Solid, Bullion.Grade(title));

    [Theory]
    [InlineData("14k Gold Plated Cuban Link Chain")]         // a karat that describes the coating
    [InlineData("Gold Filled 1/20 12k Bangle")]
    [InlineData("Silver Tone Costume Brooch")]
    [InlineData("999 Gold Clad Buffalo Tribute Bar")]
    [InlineData("Sterling Silver Vermeil Ring")]
    public void A_coating_wins_over_anything_else_the_title_claims(string title) =>
        Assert.Equal(BullionGrade.Novelty, Bullion.Grade(title));

    [Fact]
    public void A_toned_coin_is_solid_silver_not_a_silver_tone_costume_piece()
    {
        // Bare "tone" would fail this. Toning is oxidation on solid silver and collectors pay up
        // for it — the compound forms are the marker, not the word.
        Assert.Equal(BullionGrade.Solid, Bullion.Grade("1881-S Morgan Silver Dollar Rainbow Toned MS64 .900"));
    }

    [Fact]
    public void A_hallmark_rescues_a_serving_plate_from_the_flatware_pile()
    {
        // "Silver plate" is silverplate far more often than it is a solid dish, so it is a marker
        // — but a stated hallmark outranks it, because sterling is sterling whatever shape it is.
        Assert.Equal(BullionGrade.Novelty, Bullion.Grade("Silver Plate Serving Tray Wm Rogers"));
        Assert.Equal(BullionGrade.Solid, Bullion.Grade("Sterling Silver Plate Tiffany & Co 310 grams"));
    }
}
