using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Category is the one field on the listing form that is expensive to get wrong. A wrong brand is a
// wrong word; a wrong category is the listing sitting in the wrong aisle with the wrong required
// Item Specifics hanging off it, and the seller never sees why nobody found it.
//
// So the whole of this suggester is a set of refusals, and these tests are mostly about the cases
// where it must say nothing at all:
//
//   * words two unrelated listings share ("New", "Lot", "Free Shipping") are not evidence
//   * one word in common is not evidence
//   * two categories the seller has filed items like this under is not an answer, it is a coin flip
//
// The cases where it does answer are the ones where the seller has already made this exact
// decision, sometimes dozens of times, and was being asked to make it again.
public class CategorySuggesterTests
{
    private static CategoryUse Use(string title, string categoryId, string name = "", int count = 1) => new()
    {
        Title = title,
        CategoryId = categoryId,
        CategoryName = name,
        UseCount = count,
        LastUsedUtc = DateTimeOffset.UtcNow,
    };

    // ── Reading a title ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Words_that_appear_in_every_listing_title_are_not_words()
    {
        // If these counted, "New Sealed Lot — Free Fast Shipping" would match everything the
        // seller has ever listed, and the app would confidently file a phone case under miners.
        Assert.Empty(CategorySuggester.Tokens("New Sealed Lot — Free Fast Shipping USA"));
    }

    [Fact]
    public void A_model_number_counts_for_more_than_an_adjective()
    {
        // Two titles sharing "s19j" are the same product line. Two sharing "black" are not.
        Assert.Equal(3, CategorySuggester.Weight("s19j"));
        Assert.Equal(2, CategorySuggester.Weight("3090"));
        Assert.Equal(1, CategorySuggester.Weight("bitmain"));
    }

    [Fact]
    public void Short_bare_numbers_are_dropped_and_short_words_are_kept()
    {
        // "13" is a size, a quantity and a model year. "hp" is a brand.
        var tokens = CategorySuggester.Tokens("HP EliteBook 13 x 840 G8");
        Assert.Contains("hp", tokens);
        Assert.Contains("840", tokens);
        Assert.DoesNotContain("13", tokens);
        Assert.DoesNotContain("x", tokens);
    }

    [Fact]
    public void The_same_words_in_a_different_order_are_the_same_listing()
    {
        // What collapses forty listings of one model onto one remembered row.
        Assert.Equal(
            CategorySuggester.Key("Bitmain Antminer S19 Pro — 110TH"),
            CategorySuggester.Key("antminer s19 (110th) bitmain pro"));
    }

    // ── What it will answer ───────────────────────────────────────────────────────────────────

    [Fact]
    public void The_same_item_listed_again_gets_the_category_it_got_last_time()
    {
        var match = CategorySuggester.FromHistory(
            "Bitmain Antminer S19 Pro 110TH Bitcoin Miner",
            [Use("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", "179171", "Miners", count: 6)]);

        Assert.NotNull(match);
        Assert.Equal("179171", match.CategoryId);
        Assert.Equal("Miners", match.CategoryName);
        Assert.Equal(ListingAutofill.High, match.Confidence);
        Assert.Equal(6, match.TimesUsed);

        // The source quotes the seller's own past listing back at them, so the suggestion can be
        // checked rather than taken on faith.
        Assert.Contains("Bitmain Antminer S19 Pro", match.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_model_from_the_same_line_is_offered_but_not_as_a_certainty()
    {
        // Same brand and family, different chip and hash rate. Almost certainly the same category,
        // and "almost certainly" is what medium confidence is for.
        var match = CategorySuggester.FromHistory(
            "Bitmain Antminer S19j Pro 104TH Bitcoin Miner",
            [Use("Bitmain Antminer S19 Pro 110TH Bitcoin Miner ASIC", "179171", "Miners")]);

        Assert.NotNull(match);
        Assert.Equal("179171", match.CategoryId);
        Assert.Equal(ListingAutofill.Medium, match.Confidence);
    }

    [Fact]
    public void One_shared_model_number_is_enough_on_its_own()
    {
        // Nothing else in these two titles matches. The part number is the item.
        var match = CategorySuggester.FromHistory(
            "RTX4090 Founders Edition",
            [Use("NVIDIA GeForce RTX4090 24GB Graphics Card", "27386", "Graphics/Video Cards")]);

        Assert.NotNull(match);
        Assert.Equal("27386", match.CategoryId);
    }

    [Fact]
    public void The_closest_past_listing_decides_not_the_most_used_category()
    {
        // A seller who has published two hundred miner listings and four camera listings is still
        // listing a camera when they type one. Volume must not drown out the match.
        var history = new List<CategoryUse>
        {
            Use("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", "179171", "Miners", count: 200),
            Use("Canon EOS R6 Mirrorless Camera Body", "31388", "Digital Cameras", count: 4),
        };

        var match = CategorySuggester.FromHistory("Canon EOS R6 Mark II Mirrorless Camera Body", history);

        Assert.NotNull(match);
        Assert.Equal("31388", match.CategoryId);
    }

    // ── What it refuses to answer ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_item_the_seller_has_never_listed_gets_no_answer()
    {
        var match = CategorySuggester.FromHistory(
            "Nike Air Max 90 Mens Size 10 Sneakers",
            [Use("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", "179171", "Miners")]);

        Assert.Null(match);
    }

    [Fact]
    public void One_word_in_common_is_not_evidence()
    {
        // Both are Apple. One is a phone and one is a watch, and they do not share a category.
        var match = CategorySuggester.FromHistory(
            "Apple iPhone 15 Pro Max 256GB Unlocked",
            [Use("Apple Watch Series 8 45mm GPS", "178893", "Smart Watches")]);

        Assert.Null(match);
    }

    [Fact]
    public void A_category_the_seller_has_filed_this_both_ways_is_not_an_answer()
    {
        // The seller themselves has not settled this. Picking one for them would be the app
        // inventing a decision that was never made.
        var history = new List<CategoryUse>
        {
            Use("Antminer S19 Pro Hash Board Repair Part", "179171", "Miners"),
            Use("Antminer S19 Pro Hash Board Repair Part", "64800", "Electronic Components"),
        };

        Assert.Null(CategorySuggester.FromHistory("Antminer S19 Pro Hash Board Repair Part", history));
    }

    [Fact]
    public void A_title_of_nothing_but_filler_gets_no_answer()
    {
        var match = CategorySuggester.FromHistory(
            "New Sealed Lot Free Shipping",
            [Use("New Sealed Lot Fast Shipping Bitcoin Miner", "179171", "Miners")]);

        Assert.Null(match);
    }

    [Fact]
    public void No_history_and_no_title_are_both_silence_rather_than_a_guess()
    {
        Assert.Null(CategorySuggester.FromHistory("Bitmain Antminer S19", []));
        Assert.Null(CategorySuggester.FromHistory("Bitmain Antminer S19", null));
        Assert.Null(CategorySuggester.FromHistory("", [Use("Bitmain Antminer S19", "179171")]));
        Assert.Null(CategorySuggester.FromHistory(null, [Use("Bitmain Antminer S19", "179171")]));
    }

    [Fact]
    public void A_remembered_row_with_no_category_id_is_ignored_rather_than_offered()
    {
        Assert.Null(CategorySuggester.FromHistory(
            "Bitmain Antminer S19 Pro 110TH Bitcoin Miner",
            [Use("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", "")]));
    }

    // ── eBay's answer, held to a lower standard than the seller's own ─────────────────────────

    [Fact]
    public void Ebays_own_suggestion_is_never_offered_at_full_confidence()
    {
        // eBay is guessing from the same string the app is, without knowing what this seller
        // sells. It is the answer for a first-ever listing, not a fact about the item.
        var match = CategorySuggester.FromEbay("179171", "Miners", "Business & Industrial › Miners");

        Assert.NotNull(match);
        Assert.Equal(ListingAutofill.Medium, match.Confidence);
        Assert.Equal(0, match.TimesUsed);
        Assert.Contains("eBay", match.Source, StringComparison.Ordinal);
    }

    [Fact]
    public void An_ebay_answer_with_no_category_id_is_no_answer()
    {
        Assert.Null(CategorySuggester.FromEbay("", "Miners"));
        Assert.Null(CategorySuggester.FromEbay(null, "Miners"));
    }
}
