using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The matcher's whole job is to be right about which field is which and what its value should
// be, on the one screen where being wrong is expensive: a mislabelled Item Specific publishes a
// false claim about the item, and a missed match publishes nothing at all because eBay rejects it.
//
// So the cases below are mostly about *refusing*: near-misses that must not match, ambiguity that
// must come back as "I don't know", and inference that must not fire where nothing in the listing
// could honestly answer.
public class AspectMatcherTests
{
    private static CategoryAspect Aspect(
        string name, bool required = false, bool recommended = false,
        bool selectionOnly = false, bool multi = false, int maxLength = 0,
        params string[] values) =>
        new()
        {
            Name = name,
            Required = required,
            Recommended = recommended,
            SelectionOnly = selectionOnly,
            MultiSelect = multi,
            MaxLength = maxLength,
            ValuesAreComplete = selectionOnly,
            Values = values.ToList(),
        };

    private static AspectMatcher.ListingFacts Facts(
        string title = "", string description = "", string brand = "",
        string mpn = "", string upc = "", ProductIdentity? identity = null) =>
        new(title, description, brand, mpn, upc, "", "", "USED_EXCELLENT", identity);

    // ── Name matching ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NormalizeName_strips_case_spaces_and_punctuation()
    {
        Assert.Equal("chipsetgpumodel", AspectMatcher.NormalizeName("Chipset/GPU Model"));
        Assert.Equal("countryregionofmanufacture", AspectMatcher.NormalizeName("Country/Region of Manufacture"));
        Assert.Equal("", AspectMatcher.NormalizeName("   "));
    }

    [Fact]
    public void Exact_name_wins()
    {
        var aspects = new[] { Aspect("Brand"), Aspect("Compatible Brand") };
        Assert.Equal("Brand", AspectMatcher.MatchAspectName("brand", aspects)!.Name);
        Assert.Equal("Compatible Brand", AspectMatcher.MatchAspectName("Compatible Brand", aspects)!.Name);
    }

    [Fact]
    public void Alias_matches_a_different_word_for_the_same_field()
    {
        var aspects = new[] { Aspect("Brand"), Aspect("Model") };
        Assert.Equal("Brand", AspectMatcher.MatchAspectName("Manufacturer", aspects)!.Name);
        Assert.Equal("Model", AspectMatcher.MatchAspectName("Model Number", aspects)!.Name);
    }

    [Fact]
    public void Alias_refuses_when_the_category_has_more_than_one_candidate()
    {
        // "Capacity" is genuinely ambiguous where both exist. Picking one would put a battery
        // rating in a storage field, which reads as a real spec and is simply false.
        var aspects = new[] { Aspect("Storage Capacity"), Aspect("Total Capacity") };
        Assert.Null(AspectMatcher.MatchAspectName("Capacity", aspects));
    }

    [Fact]
    public void Token_overlap_matches_a_partial_name()
    {
        var aspects = new[] { Aspect("Chipset/GPU Model"), Aspect("Memory Size") };
        Assert.Equal("Chipset/GPU Model", AspectMatcher.MatchAspectName("GPU Model", aspects)!.Name);
    }

    [Fact]
    public void Compatible_Brand_is_not_Brand()
    {
        // The single most damaging near-miss in this whole feature: a phone case whose
        // "Compatible Brand" is Apple is not an Apple-brand product.
        var aspects = new[] { Aspect("Compatible Brand") };
        Assert.Null(AspectMatcher.MatchAspectName("Brand", aspects));
    }

    [Fact]
    public void An_unrelated_key_matches_nothing()
    {
        var aspects = new[] { Aspect("Brand"), Aspect("Model") };
        Assert.Null(AspectMatcher.MatchAspectName("Shipping Weight", aspects));
        Assert.Null(AspectMatcher.MatchAspectName("", aspects));
    }

    [Fact]
    public void A_tie_between_two_aspects_is_refused()
    {
        var aspects = new[] { Aspect("Left Size"), Aspect("Right Size") };
        Assert.Null(AspectMatcher.MatchAspectName("Shoe Size", aspects));
    }

    // ── Value canonicalisation ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Bitmain", "Bitmain")]
    [InlineData("bitmain", "Bitmain")]
    [InlineData("BITMAIN", "Bitmain")]
    [InlineData("  Bitmain  ", "Bitmain")]
    public void Value_matching_ignores_case_and_padding(string input, string expected)
    {
        string[] allowed = ["Bitmain", "MicroBT", "Canaan"];
        Assert.Equal(expected, AspectMatcher.CanonicalizeValue(input, allowed));
    }

    [Fact]
    public void Value_matching_ignores_punctuation_and_plurality()
    {
        string[] allowed = ["Wall Mount", "Speaker"];
        Assert.Equal("Wall Mount", AspectMatcher.CanonicalizeValue("wall-mount", allowed));
        Assert.Equal("Speaker", AspectMatcher.CanonicalizeValue("Speakers", allowed));
    }

    [Fact]
    public void Value_synonyms_reach_eBays_own_spelling()
    {
        string[] allowed = ["Does Not Apply", "Bitmain"];
        Assert.Equal("Does Not Apply", AspectMatcher.CanonicalizeValue("N/A", allowed));
        Assert.Equal("Does Not Apply", AspectMatcher.CanonicalizeValue("none", allowed));

        string[] brands = ["Unbranded", "Bitmain"];
        Assert.Equal("Unbranded", AspectMatcher.CanonicalizeValue("Generic", brands));
    }

    [Fact]
    public void A_value_eBay_does_not_have_comes_back_null()
    {
        string[] allowed = ["Bitmain", "MicroBT"];
        Assert.Null(AspectMatcher.CanonicalizeValue("Goldshell", allowed));
        Assert.Null(AspectMatcher.CanonicalizeValue("", allowed));
    }

    // ── Finding a value in the seller's own words ─────────────────────────────────────────────

    [Fact]
    public void Longest_match_wins_so_a_pro_model_is_not_read_as_the_base_model()
    {
        // "Antminer S19 Pro" matching "S19" would list a $2,000 machine as a $700 one.
        string[] values = ["S19", "S19 Pro", "S19j"];
        Assert.Equal("S19 Pro",
            AspectMatcher.FindValueInText("Bitmain Antminer S19 Pro 110TH Bitcoin Miner", values));
    }

    [Fact]
    public void Matching_is_whole_token_so_a_substring_does_not_count()
    {
        // Without token boundaries "Red" matches "Prepared" and the app reports a colour the
        // seller never mentioned.
        string[] values = ["Red", "Blue"];
        Assert.Null(AspectMatcher.FindValueInText("Fully prepared and tested unit", values));
        Assert.Equal("Red", AspectMatcher.FindValueInText("Red housing, tested", values));
    }

    [Fact]
    public void Two_equally_long_candidates_are_refused_rather_than_guessed()
    {
        string[] values = ["Red", "Blue"];
        Assert.Null(AspectMatcher.FindValueInText("Red and Blue pair", values));
    }

    [Fact]
    public void Short_values_are_not_pulled_out_of_free_text()
    {
        // A "Type" list containing "PC" would otherwise match the "pc" in "2 pc set".
        string[] values = ["PC", "Laptop"];
        Assert.Null(AspectMatcher.FindValueInText("2 pc set of brackets", values, 3));
    }

    [Fact]
    public void Empty_inputs_are_safe()
    {
        Assert.Null(AspectMatcher.FindValueInText("", ["Red"]));
        Assert.Null(AspectMatcher.FindValueInText("Red", []));
        Assert.Null(AspectMatcher.FindValueInText(null, ["Red"]));
    }

    // ── Suggestions ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Brand_comes_straight_off_the_Brand_field_at_high_confidence()
    {
        var s = AspectMatcher.Suggest(Aspect("Brand"), Facts(brand: "Bitmain"));
        Assert.NotNull(s);
        Assert.Equal("Bitmain", s!.Value);
        Assert.Equal("high", s.Confidence);
        Assert.Contains("Brand field", s.Source);
    }

    [Fact]
    public void A_selection_only_aspect_never_offers_a_value_eBay_would_reject()
    {
        // The seller typed a brand eBay's list doesn't contain. Offering it back produces a
        // publish failure; the app has nothing legal to suggest, so it suggests nothing.
        var aspect = Aspect("Brand", selectionOnly: true, values: ["Bitmain", "MicroBT"]);
        Assert.Null(AspectMatcher.Suggest(aspect, Facts(brand: "SomeUnlistedBrand")));
    }

    [Fact]
    public void A_direct_field_value_is_corrected_to_eBays_spelling()
    {
        var aspect = Aspect("Brand", selectionOnly: true, values: ["Bitmain", "MicroBT"]);
        var s = AspectMatcher.Suggest(aspect, Facts(brand: "bitmain"));
        Assert.Equal("Bitmain", s!.Value);
    }

    [Fact]
    public void A_value_is_found_in_the_title_before_the_description()
    {
        var aspect = Aspect("Model", values: ["S19 Pro", "S19j"]);
        var s = AspectMatcher.Suggest(aspect, Facts(title: "Antminer S19 Pro", description: "Also fits S19j"));
        Assert.Equal("S19 Pro", s!.Value);
        Assert.Equal("high", s.Confidence);
        Assert.Contains("title", s.Source);
    }

    [Fact]
    public void A_value_found_only_in_the_description_is_offered_at_lower_confidence()
    {
        var aspect = Aspect("Color", values: ["Black", "Silver"]);
        var s = AspectMatcher.Suggest(aspect, Facts(title: "Rack server", description: "Black steel chassis"));
        Assert.Equal("Black", s!.Value);
        Assert.Equal("medium", s.Confidence);
        Assert.Contains("description", s.Source);
    }

    [Fact]
    public void Country_of_manufacture_is_never_guessed()
    {
        // A country of origin is a legal claim about the item, not a convenience field. Nothing
        // in a listing establishes it, so the app leaves it to the seller however required it is.
        var aspect = Aspect("Country/Region of Manufacture", required: true, values: ["China", "United States"]);
        Assert.Null(AspectMatcher.Suggest(aspect, Facts(title: "Made in China sticker on the back", description: "China")));
    }

    [Fact]
    public void A_required_identifier_with_no_number_is_offered_as_Does_Not_Apply()
    {
        // eBay's own answer for an item with no part number. Blank fails the publish; this
        // passes it and is what the item actually is.
        var s = AspectMatcher.Suggest(Aspect("MPN", required: true), Facts(title: "Vintage brass lamp"));
        Assert.Equal("Does Not Apply", s!.Value);
    }

    [Fact]
    public void A_non_required_identifier_is_not_padded_with_Does_Not_Apply()
    {
        Assert.Null(AspectMatcher.Suggest(Aspect("MPN"), Facts(title: "Vintage brass lamp")));
    }

    [Fact]
    public void A_title_leftover_that_reads_like_prose_is_not_offered_as_a_model()
    {
        // Caught on a live run against real inventory. The identity extractor's Model is
        // "whatever words are left after every known field has been claimed", which on a real
        // title comes back as "S19 Pro ASIC with PSU" — and it was being offered at medium
        // confidence, so "Fill from my listing" would have written that into a live listing.
        var identity = new ProductIdentity { Model = "S19 Pro ASIC with PSU" };
        Assert.Null(AspectMatcher.Suggest(Aspect("Model"), Facts(title: "…", identity: identity)));
    }

    [Fact]
    public void A_real_model_number_from_the_title_is_still_offered()
    {
        var identity = new ProductIdentity { Model = "S19 Pro" };
        var s = AspectMatcher.Suggest(Aspect("Model"), Facts(title: "…", identity: identity));
        Assert.Equal("S19 Pro", s!.Value);
        Assert.Equal("medium", s.Confidence);
    }

    [Fact]
    public void Country_of_Origin_is_refused_under_that_name_too()
    {
        // eBay names this aspect differently across categories; the live crypto-miner category
        // calls it "Country of Origin" rather than "Country/Region of Manufacture".
        var aspect = Aspect("Country of Origin", selectionOnly: true, values: ["China", "United States"]);
        Assert.Null(AspectMatcher.Suggest(aspect, Facts(description: "Shipped from China warehouse")));
    }

    // ── Evaluate: the whole pass ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_missing_required_aspect_is_reported_with_a_suggestion()
    {
        var aspects = new[] { Aspect("Brand", required: true) };
        var (fields, custom) = AspectMatcher.Evaluate(
            aspects, new Dictionary<string, string>(), Facts(brand: "Bitmain"));

        var brand = Assert.Single(fields);
        Assert.Equal(AspectState.MissingRequired, brand.State);
        Assert.Equal("Bitmain", brand.SuggestedValue);
        Assert.Empty(custom);
    }

    [Fact]
    public void A_seller_key_under_a_different_name_fills_eBays_field_and_says_so()
    {
        var aspects = new[] { Aspect("Chipset/GPU Model", required: true) };
        var specifics = new Dictionary<string, string> { ["GPU Model"] = "RTX 3080" };

        var (fields, custom) = AspectMatcher.Evaluate(aspects, specifics, Facts());

        var f = Assert.Single(fields);
        Assert.Equal(AspectState.Filled, f.State);
        Assert.Equal("RTX 3080", f.Value);
        Assert.Equal("GPU Model", f.MatchedFromKey);
        // Claimed, so it must not also survive as a loose custom row and be sent twice.
        Assert.Empty(custom);
    }

    [Fact]
    public void A_key_no_aspect_claims_stays_a_custom_specific()
    {
        var aspects = new[] { Aspect("Brand") };
        var specifics = new Dictionary<string, string> { ["Rack Units"] = "4U" };

        var (_, custom) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        Assert.Equal("Rack Units", Assert.Single(custom));
    }

    [Fact]
    public void A_value_outside_a_selection_only_list_is_flagged_not_silently_sent()
    {
        var aspects = new[] { Aspect("Brand", required: true, selectionOnly: true, values: ["Bitmain", "MicroBT"]) };
        var specifics = new Dictionary<string, string> { ["Brand"] = "Goldshell" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        Assert.Equal(AspectState.InvalidValue, Assert.Single(fields).State);
    }

    [Fact]
    public void A_case_difference_against_a_selection_only_list_is_corrected_not_flagged()
    {
        var aspects = new[] { Aspect("Brand", selectionOnly: true, values: ["Bitmain"]) };
        var specifics = new Dictionary<string, string> { ["Brand"] = "bitmain" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        var f = Assert.Single(fields);
        Assert.Equal(AspectState.Filled, f.State);
        Assert.Equal("Bitmain", f.Value);
        Assert.Contains("spelling", f.Note);
    }

    [Fact]
    public void A_free_text_aspect_accepts_a_value_outside_eBays_sample_list()
    {
        // For FREE_TEXT, eBay's values are popular suggestions, not the whole set. Rejecting a
        // seller's real value here would be the app inventing a problem eBay doesn't have.
        var aspects = new[] { Aspect("Model", values: ["S19", "S19 Pro"]) };
        var specifics = new Dictionary<string, string> { ["Model"] = "S21 XP Hydro" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        Assert.Equal(AspectState.Filled, Assert.Single(fields).State);
    }

    [Fact]
    public void A_value_over_eBays_length_cap_is_reported()
    {
        var aspects = new[] { Aspect("Model", maxLength: 10) };
        var specifics = new Dictionary<string, string> { ["Model"] = "a much longer value than allowed" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        Assert.Equal(AspectState.TooLong, Assert.Single(fields).State);
    }

    [Fact]
    public void A_multi_value_specific_is_split_on_the_pipe_eBay_uses()
    {
        var aspects = new[] { Aspect("Features", multi: true, selectionOnly: true, values: ["Wi-Fi", "Bluetooth"]) };
        var specifics = new Dictionary<string, string> { ["Features"] = "wifi|bluetooth" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        var f = Assert.Single(fields);
        Assert.Equal(AspectState.Filled, f.State);
        Assert.Equal("Wi-Fi|Bluetooth", f.Value);
    }

    [Fact]
    public void One_seller_key_cannot_satisfy_two_aspects()
    {
        var aspects = new[] { Aspect("Model"), Aspect("Model Type") };
        var specifics = new Dictionary<string, string> { ["Model"] = "S19" };

        var (fields, _) = AspectMatcher.Evaluate(aspects, specifics, Facts());
        Assert.Equal("S19", fields.Single(f => f.Name == "Model").Value);
        Assert.Equal("", fields.Single(f => f.Name == "Model Type").Value);
    }

    [Fact]
    public void Recommended_and_optional_gaps_are_told_apart()
    {
        var aspects = new[] { Aspect("Type", recommended: true), Aspect("Style") };
        var (fields, _) = AspectMatcher.Evaluate(aspects, new Dictionary<string, string>(), Facts());

        Assert.Equal(AspectState.MissingRecommended, fields.Single(f => f.Name == "Type").State);
        Assert.Equal(AspectState.Optional, fields.Single(f => f.Name == "Style").State);
    }

    // ── AutoFillable ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Autofill_takes_high_and_medium_confidence_only()
    {
        var fields = new List<AspectField>
        {
            new() { Name = "Brand", SuggestedValue = "Bitmain", SuggestionConfidence = "high" },
            new() { Name = "Color", SuggestedValue = "Black",   SuggestionConfidence = "medium" },
            new() { Name = "Style", SuggestedValue = "Modern",  SuggestionConfidence = "low" },
        };

        var fill = AspectMatcher.AutoFillable(fields);
        Assert.Equal(2, fill.Count);
        Assert.False(fill.ContainsKey("Style"));
    }

    [Fact]
    public void Autofill_never_overwrites_what_the_seller_typed()
    {
        var fields = new List<AspectField>
        {
            new() { Name = "Brand", Value = "My Brand", SuggestedValue = "Bitmain",
                    SuggestionConfidence = "high", State = AspectState.Filled },
        };
        Assert.Empty(AspectMatcher.AutoFillable(fields));
    }

    [Fact]
    public void Autofill_does_replace_a_value_eBay_would_reject()
    {
        // The one exception: keeping it means a publish that fails, so replacing it is strictly
        // better than leaving it alone.
        var fields = new List<AspectField>
        {
            new() { Name = "Brand", Value = "Goldshell", SuggestedValue = "Bitmain",
                    SuggestionConfidence = "high", State = AspectState.InvalidValue },
        };
        Assert.Equal("Bitmain", AspectMatcher.AutoFillable(fields)["Brand"]);
    }

    // ── StripHtml ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StripHtml_leaves_readable_text_and_drops_markup()
    {
        var text = AspectMatcher.StripHtml("<p>Black <b>steel</b> chassis</p><ul><li>Tested</li></ul>");
        Assert.Equal("Black steel chassis Tested", text);
    }

    [Fact]
    public void StripHtml_decodes_the_entities_that_change_words()
    {
        Assert.Equal("Tom & Jerry", AspectMatcher.StripHtml("Tom &amp; Jerry"));
        Assert.Equal("a b", AspectMatcher.StripHtml("a&nbsp;b"));
    }

    [Fact]
    public void StripHtml_does_not_let_a_tag_name_become_a_matchable_word()
    {
        // The reason it exists: searching raw markup for a "Table" value finds "<table>".
        string[] values = ["Table", "Chair"];
        Assert.Null(AspectMatcher.FindValueInText(
            AspectMatcher.StripHtml("<table><tr><td>Oak chest</td></tr></table>"), values));
    }
}
