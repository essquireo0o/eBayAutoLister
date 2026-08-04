using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The autofill engine's whole value is that a seller can press one button and trust the result.
// That trust rests on two rules, and every test here exists to hold one of them:
//
//   1. It never writes over a fact. A field the seller filled in is not offered a value, so
//      "fill everything" can never be the click that lost their measurements.
//   2. It never passes a guess off as a reading. Anything inferred is flagged as an estimate and
//      carries the estimator's own basis, and an estimate for an item nothing recognised is held
//      back from the bulk fill entirely.
public class ListingAutofillTests
{
    private static readonly PackageEstimator Packages = new();
    private static readonly ProductIdentityExtractor Identities = new();

    private static ListingData Draft(string title = "Bitmain Antminer S19 Pro 110TH Bitcoin Miner") => new()
    {
        Title = title,
        Price = 1200m,
        CategoryId = "175673",
    };

    private static List<FieldSuggestion> SuggestFor(ListingData listing, string zip = "")
        => ListingAutofill.Suggest(listing, Identities.Extract(listing.Title),
                                   Packages.EstimateFromListing(listing), zip);

    private static FieldSuggestion? Field(IEnumerable<FieldSuggestion> all, string field)
        => all.FirstOrDefault(s => s.Field == field);

    // ── What it can fill ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_brand_in_the_sellers_own_title_fills_the_brand_field()
    {
        var s = Field(SuggestFor(Draft()), "brand");
        Assert.NotNull(s);
        Assert.Equal("Bitmain", s.Display);
        Assert.Equal("nl-brand", s.FieldId);
        Assert.Equal("Bitmain", s.Set["nl-brand"]);

        // Read, not inferred — so it is offered at full confidence and is not marked a guess.
        Assert.Equal(ListingAutofill.High, s.Confidence);
        Assert.False(s.IsEstimate);
        Assert.NotEqual("", s.Source);
    }

    [Fact]
    public void A_weight_is_offered_as_pounds_and_ounces_together()
    {
        // Half a weight is not a weight: pounds without ounces silently rounds the parcel down.
        var s = Field(SuggestFor(Draft()), "weight");
        Assert.NotNull(s);
        Assert.Equal(2, s.Set.Count);
        Assert.Contains("nl-weight-lbs", s.Set.Keys);
        Assert.Contains("nl-weight-oz", s.Set.Keys);

        // The ASIC profile is 704 oz shipped — 44 lb 0 oz.
        Assert.Equal("44", s.Set["nl-weight-lbs"]);
        Assert.Equal("0", s.Set["nl-weight-oz"]);
        Assert.Equal("44 lb 0 oz", s.Display);
    }

    [Fact]
    public void A_size_is_offered_as_all_three_sides_or_not_at_all()
    {
        var s = Field(SuggestFor(Draft()), "dimensions");
        Assert.NotNull(s);
        Assert.Equal(3, s.Set.Count);
        Assert.Equal("22", s.Set["nl-length"]);
        Assert.Equal("16", s.Set["nl-width"]);
        Assert.Equal("14", s.Set["nl-height"]);
    }

    [Fact]
    public void The_saved_zip_is_offered_when_the_draft_arrived_without_one()
    {
        var s = Field(SuggestFor(Draft(), zip: "89101"), "postalCode");
        Assert.NotNull(s);
        Assert.Equal("89101", s.Display);
        Assert.False(s.IsEstimate);   // it is the seller's own saved setting, not a guess

        Assert.Null(Field(SuggestFor(Draft()), "postalCode"));   // and nothing when none is saved
    }

    [Fact]
    public void The_part_number_fills_the_MPN_only_while_the_listing_has_no_identifier_at_all()
    {
        var withPart = Draft("Allen-Bradley 1756-L73 ControlLogix Processor Module");
        var suggested = Field(SuggestFor(withPart), "mpn");
        Assert.NotNull(suggested);
        Assert.Equal("nl-mpn", suggested.FieldId);

        // A listing that already carries a UPC is finished on this point; offering an MPN beside
        // it is a second thing to do that buys the seller nothing.
        withPart.Upc = "012345678905";
        Assert.Null(Field(SuggestFor(withPart), "mpn"));
    }

    // ── What it refuses to fill ───────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_the_seller_typed_is_ever_offered_over()
    {
        var listing = Draft();
        listing.Brand = "My own brand";
        listing.Mpn = "MINE-1";
        listing.WeightLbs = 12m;
        listing.PackageLengthIn = 10m;
        listing.PackageWidthIn = 9m;
        listing.PackageHeightIn = 8m;
        listing.ItemLocationPostalCode = "10001";

        var all = SuggestFor(listing, zip: "89101");
        Assert.Null(Field(all, "brand"));
        Assert.Null(Field(all, "mpn"));
        Assert.Null(Field(all, "weight"));
        Assert.Null(Field(all, "dimensions"));
        Assert.Null(Field(all, "postalCode"));
    }

    [Fact]
    public void Two_measured_sides_do_not_count_as_a_measured_box()
    {
        // A half-entered size quotes nothing, and treating it as done leaves the seller with a
        // listing that silently can't calculate shipping.
        var listing = Draft();
        listing.PackageLengthIn = 10m;
        listing.PackageWidthIn = 9m;
        Assert.NotNull(Field(SuggestFor(listing), "dimensions"));
    }

    [Fact]
    public void A_listing_with_no_title_is_offered_nothing_it_would_have_to_invent()
    {
        var blank = new ListingData();
        var all = ListingAutofill.Suggest(blank, Identities.Extract(blank.Title),
                                          Packages.EstimateFromListing(blank));
        Assert.Null(Field(all, "brand"));
        Assert.Null(Field(all, "mpn"));

        // The package is still offered — the estimator's fallback is an honest small parcel — but
        // at low confidence, so the one-click fill leaves it alone.
        var weight = Field(all, "weight");
        Assert.NotNull(weight);
        Assert.Equal(ListingAutofill.Low, weight.Confidence);
        Assert.False(ListingAutofill.IsBulkFillable(weight));
    }

    // ── Saying which numbers are guesses ──────────────────────────────────────────────────────

    [Fact]
    public void Every_inferred_package_figure_is_marked_as_an_estimate_and_says_why()
    {
        foreach (var field in new[] { "weight", "dimensions" })
        {
            var s = Field(SuggestFor(Draft()), field);
            Assert.NotNull(s);
            Assert.True(s.IsEstimate, $"{field} is inferred and must be labelled so");
            Assert.False(string.IsNullOrWhiteSpace(s.Source), $"{field} must carry the estimator's basis");
        }
    }

    [Fact]
    public void A_recognised_item_is_confident_enough_for_the_one_click_fill_and_an_unknown_one_is_not()
    {
        var known = Field(SuggestFor(Draft("Dell Latitude 7420 Laptop i7 16GB")), "weight");
        Assert.NotNull(known);
        Assert.True(ListingAutofill.IsBulkFillable(known));

        var unknown = Field(SuggestFor(Draft("Assorted lot, see photos")), "weight");
        Assert.NotNull(unknown);
        Assert.False(ListingAutofill.IsBulkFillable(unknown));
    }

    [Fact]
    public void Every_offer_says_where_it_came_from()
    {
        // A value with no stated origin is the app making a claim about the item on the seller's
        // behalf, which is the one thing this feature must never do.
        var all = SuggestFor(Draft(), zip: "89101");
        Assert.NotEmpty(all);
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Source)));
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
        Assert.All(all, s => Assert.False(string.IsNullOrWhiteSpace(s.Display)));
        Assert.All(all, s => Assert.NotEmpty(s.Set));
        Assert.All(all, s => Assert.Contains(s.FieldId, s.Set.Keys));
    }

    // ── Package type ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_forty_four_pound_miner_is_not_left_booked_as_a_thick_envelope()
    {
        var s = Field(SuggestFor(Draft()), "packageType");
        Assert.NotNull(s);
        Assert.Equal("BULKY_GOODS", s.Set["nl-package-type"]);

        // The label has to be the words on the dropdown, or the seller goes looking for an option
        // that isn't there.
        Assert.Equal("Bulky Goods", s.Display);
    }

    [Fact]
    public void The_package_type_is_only_ever_suggested_upward()
    {
        // The seller may well know something packs flatter than the estimator expects. Nobody
        // benefits from a parcel booked smaller than it is, so only the too-small direction is
        // corrected.
        var listing = Draft();
        listing.PackageType = "VERY_LARGE_PACKAGE";
        Assert.Null(Field(SuggestFor(listing), "packageType"));

        var flat = Draft("Pokemon card PSA 10 Charizard");
        flat.PackageType = "MAILING_BOX";
        Assert.Null(Field(SuggestFor(flat), "packageType"));
    }

    [Fact]
    public void A_package_type_is_never_changed_on_an_item_the_estimator_did_not_recognise()
    {
        // The fallback box is a deliberate over-estimate for pricing safety. Writing a package
        // class off it would be the app editing a field it has no information about.
        Assert.Null(Field(SuggestFor(Draft("Assorted lot, see photos")), "packageType"));
    }

    [Theory]
    [InlineData(2, 6, 4, 0.2, "LETTER")]                          // a graded card
    [InlineData(14, 8, 5, 3, "LARGE_ENVELOPE_OR_FLAT_PACK")]      // a phone: under a pound
    [InlineData(96, 18, 14, 5, "PACKAGE_THICK_ENVELOPE")]         // a laptop: 6 lb, 18in
    [InlineData(704, 22, 16, 14, "BULKY_GOODS")]                  // a miner: 44 lb
    [InlineData(640, 48, 30, 8, "VERY_LARGE_PACKAGE")]            // a television: 48in long
    public void The_class_is_the_smallest_one_the_box_actually_fits(
        double weightOz, double l, double w, double h, string expected)
    {
        var spec = new PackageSpec
        {
            WeightOz = (decimal)weightOz,
            LengthIn = (decimal)l, WidthIn = (decimal)w, HeightIn = (decimal)h,
        };
        Assert.Equal(expected, ListingAutofill.SmallestFitting(spec));
    }

    [Fact]
    public void An_unknown_package_type_never_reads_as_bigger_than_a_known_one()
    {
        // Rank is what decides "too small". An unrecognised string ranking high would let a typo
        // in saved settings suppress every correction.
        Assert.Equal(-1, ListingAutofill.Rank("SOMETHING_ELSE"));
        Assert.True(ListingAutofill.Rank("MAILING_BOX") > ListingAutofill.Rank("PACKAGE_THICK_ENVELOPE"));
    }

    // ── The category ──────────────────────────────────────────────────────────────────────────
    //
    // The one blocker on the readiness list the app could never answer, and the one whose absence
    // stopped the rest of the check happening at all — eBay defines required Item Specifics per
    // category, so an empty category means nothing below it can be checked either.

    private static CategoryMatch Miners(string confidence = ListingAutofill.High, int timesUsed = 5) => new()
    {
        CategoryId = "179171",
        CategoryName = "Miners",
        Confidence = confidence,
        Source = "where you put 5 listings like this",
        TimesUsed = timesUsed,
        Score = 0.9,
    };

    [Fact]
    public void The_category_fills_the_name_and_the_id_that_publishes_together()
    {
        // Two boxes: the one the seller searches in, and the hidden one carrying the ID. Writing
        // the name alone leaves a category that looks chosen and publishes as nothing.
        var listing = Draft();
        listing.CategoryId = "";

        var s = Field(ListingAutofill.Suggest(listing, null, null, "", Miners()), "category");

        Assert.NotNull(s);
        Assert.Equal("Miners", s.Display);
        Assert.Equal("nl-category", s.FieldId);
        Assert.Equal("Miners", s.Set["nl-category"]);
        Assert.Equal("179171", s.Set["nl-category-id"]);
        Assert.Equal(ListingAutofill.High, s.Confidence);
        Assert.NotEqual("", s.Source);
    }

    [Fact]
    public void A_category_the_seller_already_picked_is_never_suggested_over()
    {
        // The rule the whole engine rests on. The seller chose 175673; the app does not get a vote.
        var listing = Draft();
        Assert.Equal("175673", listing.CategoryId);

        Assert.Null(Field(ListingAutofill.Suggest(listing, null, null, "", Miners()), "category"));
    }

    [Fact]
    public void The_category_is_offered_first_because_it_is_asked_for_first()
    {
        // It is the top field on the form and the only one holding up the rest of the check.
        var listing = Draft();
        listing.CategoryId = "";

        var all = ListingAutofill.Suggest(listing, Identities.Extract(listing.Title),
                                          Packages.EstimateFromListing(listing), "89044", Miners());

        Assert.Equal("category", all[0].Field);
    }

    [Fact]
    public void The_sellers_own_past_choice_is_a_reading_and_eBays_guess_is_marked_as_one()
    {
        var listing = Draft();
        listing.CategoryId = "";

        var mine = Field(ListingAutofill.Suggest(listing, null, null, "", Miners()), "category");
        Assert.NotNull(mine);
        Assert.False(mine.IsEstimate);

        // TimesUsed of zero means nobody here has listed one before — this came from eBay reading
        // the same string the app did, and a category decides which searches the listing is in.
        var theirs = Field(ListingAutofill.Suggest(listing, null, null, "",
            CategorySuggester.FromEbay("179171", "Miners")), "category");
        Assert.NotNull(theirs);
        Assert.True(theirs.IsEstimate);
        Assert.Equal(ListingAutofill.Medium, theirs.Confidence);
    }

    [Fact]
    public void A_category_with_no_name_still_publishes_and_says_so()
    {
        // Rows recorded before the display name was carried through have an ID and nothing else.
        // The ID is what eBay needs; the seller still has to be shown something readable.
        var listing = Draft();
        listing.CategoryId = "";
        var match = Miners();
        match.CategoryName = "";

        var s = Field(ListingAutofill.Suggest(listing, null, null, "", match), "category");

        Assert.NotNull(s);
        Assert.Equal("Category 179171", s.Display);
        Assert.Equal("179171", s.Set["nl-category-id"]);
    }

    [Fact]
    public void No_category_hint_leaves_the_form_exactly_as_it_was()
    {
        var listing = Draft();
        listing.CategoryId = "";

        Assert.Null(Field(ListingAutofill.Suggest(listing, null, null, "", null), "category"));
    }

    [Fact]
    public void Weight_splits_into_whole_pounds_and_ounces()
    {
        Assert.Equal((0, 0), ListingAutofill.SplitWeight(0m));
        Assert.Equal((0, 4), ListingAutofill.SplitWeight(4m));
        Assert.Equal((1, 0), ListingAutofill.SplitWeight(16m));
        Assert.Equal((2, 3), ListingAutofill.SplitWeight(35m));
        Assert.Equal((0, 1), ListingAutofill.SplitWeight(0.6m));    // rounds, never to zero-and-nothing
        Assert.Equal((0, 0), ListingAutofill.SplitWeight(-5m));     // a negative is not a weight
    }
}
