using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The SKU is the only key that survives a flip. eBay mints the listing ID at publish and mints a
// NEW one on every relist; CostBasisStore falls back to the SKU precisely so a relisted item keeps
// the cost the seller already recorded. That fallback has never fired for a listing this app
// published, because the publish path minted a random code and threw the seller's away.
//
// What is pinned here is the two halves of getting that right:
//
//   · the seller's code reaches eBay recognisably — cut to what eBay accepts, and not silently
//     rewritten into a different code;
//   · nothing is invented. A listing with no SKU publishes with no SKU, because a random string in
//     somebody's Seller Hub is a key they cannot look anything up by.
public class SellerSkuTests
{
    // ── Nothing is invented ───────────────────────────────────────────────────

    /// <summary>
    /// The rule the whole file exists for. A blank SKU is an answer — "this listing does not have
    /// one" — and the publish path reads the empty string as "send no SKU element at all".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Nothing_usable_in_means_an_empty_string_out_rather_than_a_minted_code(string? requested)
        => Assert.Equal("", SellerSku.Sanitize(requested));

    /// <summary>Punctuation alone is not a SKU. Left as a code, it would be a key nobody could
    /// retype and every profit figure downstream would hang off it.</summary>
    [Fact]
    public void A_code_that_is_only_punctuation_survives_as_nothing()
        => Assert.Equal("", SellerSku.Sanitize("///###"));

    // ── The seller's own code, intact ─────────────────────────────────────────

    [Fact]
    public void A_clean_code_passes_through_character_for_character()
        => Assert.Equal("WN-20260806-A1B2C3", SellerSku.Sanitize("WN-20260806-A1B2C3"));

    /// <summary>Case is the seller's. eBay treats SKUs as the seller's own strings, and a shelf
    /// label that reads <c>lot-14b</c> should be findable as <c>lot-14b</c>.</summary>
    [Fact]
    public void The_sellers_own_casing_is_left_alone()
        => Assert.Equal("lot-14b", SellerSku.Sanitize("lot-14b"));

    [Fact]
    public void Surrounding_space_is_not_part_of_the_code()
        => Assert.Equal("WN-1", SellerSku.Sanitize("  WN-1  "));

    /// <summary>
    /// A space becomes a hyphen rather than vanishing. "S19J PRO" collapsing to "S19JPRO" is a
    /// different code from the one written on the box, and a key that changed shape between the
    /// shelf and eBay is not a key.
    /// </summary>
    [Fact]
    public void Spaces_become_hyphens_rather_than_closing_up()
        => Assert.Equal("S19J-PRO-104TH", SellerSku.Sanitize("S19J PRO 104TH"));

    /// <summary>The three separators every warehouse code already uses survive; the punctuation a
    /// pasted title drags in does not.</summary>
    [Fact]
    public void Warehouse_separators_survive_and_stray_punctuation_does_not()
        => Assert.Equal("A_B-C.1", SellerSku.Sanitize("A_B-C.1"));

    [Theory]
    [InlineData("WN/2026", "WN2026")]
    [InlineData("A\"B", "AB")]
    [InlineData("lot#14", "lot14")]
    public void Characters_ebay_will_not_take_are_dropped_rather_than_transliterated(string typed, string sent)
        => Assert.Equal(sent, SellerSku.Sanitize(typed));

    // ── eBay's limit ──────────────────────────────────────────────────────────

    /// <summary>
    /// Fifty characters, in both the Trading and the Inventory API. A SKU over the limit is not a
    /// long SKU, it is a rejected publish — and a rejected publish for a reason the seller cannot
    /// see anywhere on the form.
    /// </summary>
    [Fact]
    public void A_code_longer_than_ebay_accepts_is_cut_to_the_limit()
    {
        var sku = SellerSku.Sanitize(new string('A', 80));

        Assert.Equal(SellerSku.MaxLength, sku.Length);
        Assert.Equal(new string('A', 50), sku);
    }

    /// <summary>A cut that lands on a separator leaves a code ending in a dash, which reads as a
    /// truncation and is one.</summary>
    [Fact]
    public void The_cut_never_leaves_a_trailing_separator()
    {
        var sku = SellerSku.Sanitize(new string('A', 49) + "-BBBB");

        Assert.Equal(49, sku.Length);
        Assert.EndsWith("A", sku, StringComparison.Ordinal);
    }

    [Fact]
    public void A_code_exactly_at_the_limit_is_untouched()
    {
        var exact = new string('B', SellerSku.MaxLength);

        Assert.Equal(exact, SellerSku.Sanitize(exact));
    }

    // ── The Inventory API path, where a SKU is not optional ───────────────────

    /// <summary>
    /// <c>For</c> is used only where the call is ADDRESSED to the SKU — the Inventory API's
    /// item/offer endpoints — so there it cannot be blank. It still prefers the seller's.
    /// </summary>
    [Fact]
    public void For_prefers_the_sellers_code_when_there_is_one()
        => Assert.Equal("WN-20260806-A1B2C3", SellerSku.For("WN-20260806-A1B2C3"));

    [Fact]
    public void For_mints_one_only_when_there_is_nothing_to_use()
    {
        var minted = SellerSku.For("   ");

        Assert.StartsWith("SKU-", minted, StringComparison.Ordinal);
        Assert.Equal(20, minted.Length);
    }

    [Fact]
    public void Two_minted_codes_are_never_the_same()
        => Assert.NotEqual(SellerSku.Mint(), SellerSku.Mint());

    /// <summary>A minted code has to survive its own fence — otherwise the app would publish under
    /// one string and look the cost up under another.</summary>
    [Fact]
    public void A_minted_code_is_already_a_legal_one()
    {
        var minted = SellerSku.Mint();

        Assert.Equal(minted, SellerSku.Sanitize(minted));
    }

    /// <summary>Sanitising twice changes nothing. The cost lookup cleans the deal's SKU before
    /// comparing it, so a code that shifted on the second pass would stop matching itself.</summary>
    [Fact]
    public void Cleaning_a_cleaned_code_leaves_it_alone()
    {
        var once = SellerSku.Sanitize("Live show / lot 14b — “won”");

        Assert.Equal(once, SellerSku.Sanitize(once));
    }
}
