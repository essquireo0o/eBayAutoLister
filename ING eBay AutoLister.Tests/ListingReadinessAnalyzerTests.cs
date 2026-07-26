using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The readiness check replaces "press Publish and find out". Its value is entirely in the
// distinction it draws: a BLOCKER is eBay's rule and stops the publish; a WARNING is the app's
// opinion about what sells and must never stop anything. Blurring the two in either direction
// makes it useless — a nag people click past, or a wall in front of a listing eBay would accept.
public class ListingReadinessAnalyzerTests
{
    private static ListingData Complete() => new()
    {
        Title = "Bitmain Antminer S19 Pro 110TH Bitcoin Miner - Tested, PSU Included",
        CategoryId = "175673",
        Price = 1200m,
        Brand = "Bitmain",
        Mpn = "S19-PRO-110",
        Condition = "USED_EXCELLENT",
        ConditionDescription = "Fully tested, 30-day warranty, minor rack wear.",
        Description = new string('x', 400),
        ItemLocationPostalCode = "89101",
        WeightLbs = 30,
        ImageUrls = ["a.jpg", "b.jpg", "c.jpg", "d.jpg", "e.jpg", "f.jpg"],
    };

    private static AspectField Field(string name, string state, bool required = false,
                                     bool recommended = false, string suggested = "",
                                     string confidence = "") =>
        new()
        {
            Name = name, State = state, Required = required, Recommended = recommended,
            SuggestedValue = suggested, SuggestionConfidence = confidence,
        };

    private static List<ReadinessFix> Blockers(ListingReadinessResult r) =>
        r.Fixes.Where(f => f.Severity == FixSeverity.Blocker).ToList();

    // ── The happy path ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_finished_listing_scores_high_and_blocks_nothing()
    {
        var r = ListingReadinessAnalyzer.Analyze(Complete(), []);
        Assert.Equal(0, r.BlockerCount);
        Assert.True(r.Score >= 90, $"expected 90+, got {r.Score}: {string.Join("; ", r.Fixes.Select(f => f.Label))}");
        Assert.Equal("Ready to list", r.Grade);
    }

    [Fact]
    public void Every_finding_explains_what_it_costs()
    {
        // A checklist without the "why" gets clicked past, and then the listing goes out at 60%.
        var r = ListingReadinessAnalyzer.Analyze(new ListingData(), []);
        Assert.NotEmpty(r.Fixes);
        Assert.All(r.Fixes, f => Assert.False(string.IsNullOrWhiteSpace(f.Why)));
        Assert.All(r.Fixes, f => Assert.False(string.IsNullOrWhiteSpace(f.Label)));
    }

    // ── Blockers ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_four_things_eBay_refuses_a_listing_for_are_all_blockers()
    {
        var r = ListingReadinessAnalyzer.Analyze(new ListingData(), []);
        var ids = Blockers(r).Select(f => f.Id).ToList();
        Assert.Contains("title-missing", ids);
        Assert.Contains("price-missing", ids);
        Assert.Contains("category-missing", ids);
        Assert.Contains("photos-missing", ids);
    }

    [Fact]
    public void A_title_over_eightly_characters_blocks_and_a_short_one_only_warns()
    {
        var over = Complete();
        over.Title = new string('a', 81);
        Assert.Contains(Blockers(ListingReadinessAnalyzer.Analyze(over, [])), f => f.Id == "title-too-long");

        var thin = Complete();
        thin.Title = "Antminer S19";
        var r = ListingReadinessAnalyzer.Analyze(thin, []);
        Assert.Equal(0, r.BlockerCount);
        Assert.Contains(r.Fixes, f => f.Id == "title-thin" && f.Severity == FixSeverity.Warning);
    }

    [Fact]
    public void A_missing_required_aspect_blocks_the_publish()
    {
        var aspects = new[] { Field("Brand", AspectState.MissingRequired, required: true) };
        var r = ListingReadinessAnalyzer.Analyze(Complete(), aspects);

        var fix = Assert.Single(Blockers(r));
        Assert.Equal("aspect-required:Brand", fix.Id);
        Assert.Equal("Brand", fix.AspectName);
        Assert.Equal("nl-aspect-brand", fix.FieldId);
    }

    [Fact]
    public void A_blocker_that_the_app_can_fill_says_so_and_names_the_value()
    {
        var aspects = new[] { Field("Brand", AspectState.MissingRequired, required: true,
                                    suggested: "Bitmain", confidence: "high") };
        var fix = Assert.Single(Blockers(ListingReadinessAnalyzer.Analyze(Complete(), aspects)));
        Assert.True(fix.AutoFixable);
        Assert.Contains("Bitmain", fix.Why);
    }

    [Fact]
    public void A_low_confidence_suggestion_does_not_make_a_blocker_auto_fixable()
    {
        // "Fill from my listing" only applies high/medium. Advertising a one-click fix that the
        // button then declines to perform is worse than not offering it.
        var aspects = new[] { Field("Brand", AspectState.MissingRequired, required: true,
                                    suggested: "Maybe", confidence: "low") };
        Assert.False(Assert.Single(Blockers(ListingReadinessAnalyzer.Analyze(Complete(), aspects))).AutoFixable);
    }

    [Fact]
    public void An_invalid_selection_value_blocks_because_eBay_rejects_it()
    {
        var aspects = new[] { new AspectField { Name = "Brand", State = AspectState.InvalidValue, Value = "Nope" } };
        var fix = Assert.Single(Blockers(ListingReadinessAnalyzer.Analyze(Complete(), aspects)));
        Assert.Equal("aspect-invalid:Brand", fix.Id);
    }

    [Fact]
    public void A_recommended_aspect_never_blocks_however_many_are_empty()
    {
        // eBay lists dozens of recommended aspects in some categories. Treating them as blockers
        // would put a wall in front of a listing eBay would happily accept.
        var aspects = Enumerable.Range(0, 20)
            .Select(i => Field("Spec " + i, AspectState.MissingRecommended, recommended: true))
            .ToList();

        var r = ListingReadinessAnalyzer.Analyze(Complete(), aspects);
        Assert.Equal(0, r.BlockerCount);
        Assert.True(r.Score > 0);
    }

    // ── The score ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_category_full_of_required_aspects_does_not_zero_an_otherwise_finished_listing()
    {
        // Without the cap, a category with a dozen required aspects reports 0 for a listing that
        // is complete in every other respect, and the number stops meaning anything.
        var aspects = Enumerable.Range(0, 12)
            .Select(i => Field("Req " + i, AspectState.MissingRequired, required: true))
            .ToList();

        var r = ListingReadinessAnalyzer.Analyze(Complete(), aspects);
        Assert.True(r.Score > 0, "the cap should leave headroom");
        Assert.Equal(12, r.BlockerCount);
    }

    [Fact]
    public void Score_is_clamped_to_the_zero_to_hundred_range()
    {
        var empty = ListingReadinessAnalyzer.Analyze(new ListingData(), [
            Field("A", AspectState.MissingRequired, required: true),
            Field("B", AspectState.MissingRequired, required: true),
            Field("C", AspectState.MissingRequired, required: true),
            Field("D", AspectState.MissingRequired, required: true),
        ]);
        Assert.InRange(empty.Score, 0, 100);
        Assert.InRange(ListingReadinessAnalyzer.Analyze(Complete(), []).Score, 0, 100);
    }

    [Fact]
    public void A_high_score_with_a_blocker_is_not_graded_as_nearly_ready()
    {
        // The two failure modes aren't comparable: 94/100 and unpublishable is unpublishable.
        Assert.Equal("Won't publish", ListingReadinessAnalyzer.Grade(94, 1));
        Assert.Equal("Ready to list", ListingReadinessAnalyzer.Grade(94, 0));
        Assert.Equal("Needs work", ListingReadinessAnalyzer.Grade(60, 0));
    }

    // ── Warnings ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Photo_count_is_graded_in_three_bands()
    {
        var none = Complete(); none.ImageUrls = [];
        Assert.Contains(ListingReadinessAnalyzer.Analyze(none, []).Fixes,
            f => f.Id == "photos-missing" && f.Severity == FixSeverity.Blocker);

        var two = Complete(); two.ImageUrls = ["a", "b"];
        Assert.Contains(ListingReadinessAnalyzer.Analyze(two, []).Fixes,
            f => f.Id == "photos-thin" && f.Severity == FixSeverity.Warning);

        var five = Complete(); five.ImageUrls = ["a", "b", "c", "d", "e"];
        Assert.Contains(ListingReadinessAnalyzer.Analyze(five, []).Fixes,
            f => f.Id == "photos-more" && f.Severity == FixSeverity.Tip);

        Assert.DoesNotContain(ListingReadinessAnalyzer.Analyze(Complete(), []).Fixes,
            f => f.Id.StartsWith("photos"));
    }

    [Fact]
    public void Blank_photo_urls_do_not_count_as_photos()
    {
        var listing = Complete();
        listing.ImageUrls = ["", "   ", ""];
        Assert.Contains(ListingReadinessAnalyzer.Analyze(listing, []).Fixes, f => f.Id == "photos-missing");
    }

    [Fact]
    public void Having_any_one_product_identifier_settles_it()
    {
        var listing = Complete();
        listing.Mpn = ""; listing.Upc = ""; listing.Ean = ""; listing.Isbn = "";
        Assert.Contains(ListingReadinessAnalyzer.Analyze(listing, []).Fixes, f => f.Id == "identifiers-missing");

        listing.Upc = "012345678905";
        Assert.DoesNotContain(ListingReadinessAnalyzer.Analyze(listing, []).Fixes, f => f.Id == "identifiers-missing");
    }

    [Fact]
    public void Best_offer_is_only_mentioned_on_items_worth_negotiating_over()
    {
        var cheap = Complete(); cheap.Price = 40m;
        Assert.DoesNotContain(ListingReadinessAnalyzer.Analyze(cheap, []).Fixes, f => f.Id == "best-offer-off");

        var dear = Complete(); dear.Price = 900m;
        Assert.Contains(ListingReadinessAnalyzer.Analyze(dear, []).Fixes, f => f.Id == "best-offer-off");
    }

    [Fact]
    public void A_new_item_is_not_asked_for_a_condition_description()
    {
        var used = Complete(); used.ConditionDescription = "";
        Assert.Contains(ListingReadinessAnalyzer.Analyze(used, []).Fixes, f => f.Id == "condition-desc-missing");

        var brandNew = Complete(); brandNew.ConditionDescription = ""; brandNew.Condition = "NEW";
        Assert.DoesNotContain(ListingReadinessAnalyzer.Analyze(brandNew, []).Fixes, f => f.Id == "condition-desc-missing");
    }

    [Fact]
    public void An_html_description_is_measured_by_its_words_not_its_markup()
    {
        // Otherwise a page of empty <div>s reads as a full description.
        var listing = Complete();
        listing.Description = "<div class='x'></div><span style='color:red'></span>" + new string('=', 300);
        Assert.DoesNotContain(ListingReadinessAnalyzer.Analyze(listing, []).Fixes, f => f.Id == "description-missing");

        var markupOnly = Complete();
        markupOnly.Description = "<div class='wrapper'><span class='inner'></span></div>";
        Assert.Contains(ListingReadinessAnalyzer.Analyze(markupOnly, []).Fixes, f => f.Id == "description-missing");
    }

    // ── Ordering and reporting ────────────────────────────────────────────────────────────────

    [Fact]
    public void Blockers_are_listed_before_anything_else()
    {
        var aspects = new[]
        {
            Field("Type", AspectState.MissingRecommended, recommended: true),
            Field("Brand", AspectState.MissingRequired, required: true),
        };
        var listing = Complete();
        listing.ImageUrls = ["only-one"];

        var r = ListingReadinessAnalyzer.Analyze(listing, aspects);
        var firstWarning = r.Fixes.FindIndex(f => f.Severity != FixSeverity.Blocker);
        Assert.All(r.Fixes.Take(firstWarning), f => Assert.Equal(FixSeverity.Blocker, f.Severity));
        Assert.True(r.Fixes.Count > firstWarning);
    }

    [Fact]
    public void The_headline_leads_with_the_blockers_when_there_are_any()
    {
        var r = ListingReadinessAnalyzer.Analyze(new ListingData(), []);
        Assert.Contains("stop this publishing", r.Headline);
    }

    [Fact]
    public void A_listing_that_was_not_checked_against_eBay_does_not_claim_it_was()
    {
        // The most dangerous state this feature has: reporting "ready" on a listing whose
        // required specifics were never looked up because eBay wasn't connected.
        var r = ListingReadinessAnalyzer.Analyze(Complete(), [], "not_connected",
            "Connect eBay to check this category's required Item Specifics.");
        Assert.Equal(0, r.BlockerCount);
        Assert.Contains("weren't checked", r.Headline);
        Assert.Equal("not_connected", r.AspectStatus);
    }

    [Fact]
    public void No_category_yet_is_reported_as_the_next_step()
    {
        var listing = Complete();
        listing.CategoryId = "";
        var r = ListingReadinessAnalyzer.Analyze(listing, [], "no_category");
        Assert.Contains(Blockers(r), f => f.Id == "category-missing");
    }

    [Fact]
    public void Custom_specifics_are_carried_through_rather_than_dropped()
    {
        var r = ListingReadinessAnalyzer.Analyze(Complete(), [], "ok", "", ["Rack Units"]);
        Assert.Equal("Rack Units", Assert.Single(r.CustomAspectNames));
    }

    [Fact]
    public void The_autofillable_count_matches_what_the_button_would_actually_do()
    {
        var aspects = new[]
        {
            Field("Brand", AspectState.MissingRequired, required: true, suggested: "Bitmain", confidence: "high"),
            Field("Type",  AspectState.MissingRecommended, recommended: true, suggested: "Miner", confidence: "medium"),
            Field("Style", AspectState.Optional, suggested: "Modern", confidence: "low"),
        };
        Assert.Equal(2, ListingReadinessAnalyzer.Analyze(Complete(), aspects).AutoFillableCount);
    }

    // ── Field ids ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Brand", "nl-aspect-brand")]
    [InlineData("Chipset/GPU Model", "nl-aspect-chipset-gpu-model")]
    [InlineData("Country/Region of Manufacture", "nl-aspect-country-region-of-manufacture")]
    [InlineData("  Hash Rate (TH/s)  ", "nl-aspect-hash-rate-th-s")]
    public void Aspect_field_ids_are_stable_and_slug_safe(string name, string expected)
    {
        // The fix list generates these and the rendered fields carry them; a mismatch silently
        // breaks every "Go to it" button. app.js mirrors this function — the two must agree.
        Assert.Equal(expected, ListingReadinessAnalyzer.AspectFieldId(name));
    }
}
