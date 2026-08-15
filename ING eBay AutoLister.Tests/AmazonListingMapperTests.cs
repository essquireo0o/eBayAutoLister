using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The question this phase has to answer is not "can the app build an Amazon payload?" — anything can
// build a payload. It is: does the work the AI ALREADY DID for eBay carry over, and when it does not
// carry over, does the app say so instead of filling the hole?
//
// So the tests below are in two halves, and the second is the one that matters.
//
//   WHAT CARRIES. A real draft out of the seller's folder, with its real title, its real HTML
//   description and its real thirteen Item Specifics, read onto a product type schema. Eight of
//   Amazon's ten required attributes come out filled with nothing typed by hand — including a
//   country of origin the seller wrote as "China" that Amazon only accepts as "CN", and a price
//   folded into the three-deep offer shape Amazon charges from.
//
//   WHAT DOES NOT. That same draft has no UPC. Amazon will not create a listing without a product
//   identifier, the fastest way to make the field go green is to type twelve plausible digits, and
//   doing that costs sellers their accounts. Several tests here exist purely to prove the app
//   refuses: the barcode, the battery declaration, the dangerous-goods declaration, the number of
//   items in the package, and a for-parts item Amazon has no honest grade for.
//
// A test that asserted "the payload is complete" would be asserting the bug.
public class AmazonListingMapperTests(Xunit.Abstractions.ITestOutputHelper output)
{
    private const string Marketplace = "ATVPDKIKX0DER";

    // ── The acceptance case ──────────────────────────────────────────────────────────────────

    [Fact]
    public void A_real_eBay_draft_fills_Amazons_required_attributes_from_what_the_AI_already_extracted()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // Straight copies of fields the AI filled in.
        Assert.Equal("Bitaxe NerdQaxe++ 4.8TH/s BM1370 Bitcoin Solo Miner SHA-256 ASIC w/ Fan",
            Value(fill, "item_name"));
        Assert.Equal("NerdQaxe", Value(fill, "brand"));

        // The eBay description is HTML. Amazon's is not.
        var description = Attribute(fill, "product_description");
        Assert.Equal(AmazonFillState.Filled, description.State);
        Assert.DoesNotContain("<div", description.Values[0], StringComparison.Ordinal);

        // Derived, but only from things the seller wrote.
        Assert.Equal("new_new", Value(fill, "condition_type"));
        Assert.True(Attribute(fill, "bullet_point").Values.Count > 1);

        // Eight of the ten. The two that are not are the two nobody can honestly supply.
        Assert.Equal(10, fill.RequiredCount);
        Assert.Equal(8, fill.RequiredFilledCount);
        Assert.False(fill.CanSubmit);
    }

    [Fact]
    public void Every_filled_attribute_says_which_part_of_the_draft_it_came_from()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // A seller is answerable for what goes up under their account, so "where did that value come
        // from?" has to be answerable without reading the mapper.
        foreach (var attribute in fill.Filled)
            Assert.False(string.IsNullOrWhiteSpace(attribute.Source),
                $"{attribute.Name} was filled without saying where from.");

        Assert.Equal("the draft title", Attribute(fill, "item_name").Source);
        Assert.Equal("Item Specific \"Country of Manufacture\"", Attribute(fill, "country_of_origin").Source);
    }

    // ── What it refuses ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_draft_without_a_barcode_blocks_the_listing_rather_than_inventing_one()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        Assert.True(string.IsNullOrWhiteSpace(draft.Upc), "The captured draft is supposed to have no UPC.");

        var fill = Fill(draft);

        var identifier = Attribute(fill, "externally_assigned_product_identifier");
        Assert.Null(identifier.Payload);
        Assert.Empty(identifier.Values);
        Assert.Contains("suspension", identifier.Note, StringComparison.OrdinalIgnoreCase);

        // Reported as ONE unmet requirement with two doors, not two missing fields — a seller told
        // both are missing reasonably concludes they need both.
        var choice = Assert.Single(fill.Choices);
        Assert.False(choice.Satisfied);
        Assert.Equal(
            ["externally_assigned_product_identifier", "merchant_suggested_asin"],
            choice.Options.Order());

        Assert.False(fill.CanSubmit);
        Assert.DoesNotContain("externally_assigned_product_identifier", fill.Payload.Select(p => p.Key));
    }

    [Fact]
    public void A_UPC_on_the_draft_answers_the_requirement_and_the_ASIN_stops_being_missing()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        draft.Upc = "889296112273";

        var fill = Fill(draft);

        var identifier = Attribute(fill, "externally_assigned_product_identifier");
        Assert.Equal(AmazonFillState.Filled, identifier.State);
        Assert.Equal("889296112273", identifier.Payload![0]!["value"]!.GetValue<string>());
        Assert.Equal("upc", identifier.Payload[0]!["type"]!.GetValue<string>());

        var choice = Assert.Single(fill.Choices);
        Assert.True(choice.Satisfied);

        // The ASIN is not missing. It is not needed, and those are different instructions.
        Assert.Equal(AmazonFillState.SatisfiedByAlternative, Attribute(fill, "merchant_suggested_asin").State);
    }

    [Fact]
    public void The_two_declarations_only_the_seller_can_make_are_left_for_the_seller()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // Both are required, both are regulatory statements about lithium cells, and both have an
        // obvious-looking default. A default here is a false declaration to Amazon and to a carrier.
        foreach (var name in (string[])["batteries_required", "supplier_declared_dg_hz_regulation"])
        {
            var attribute = Attribute(fill, name);
            Assert.Equal(AmazonFillState.MissingRequired, attribute.State);
            Assert.Null(attribute.Payload);
            Assert.Contains("declaration", attribute.Note, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Stock_quantity_is_never_offered_as_the_number_of_items_in_the_package()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        Assert.Equal(20, draft.Quantity);

        var fill = Fill(draft);

        // Twenty miners on a shelf are not a twenty-pack, and the difference is a refund.
        var count = Attribute(fill, "number_of_items");
        Assert.Null(count.Payload);
        Assert.Contains("in stock", count.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_shipping_box_is_never_offered_as_the_products_own_dimensions()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        Assert.True(draft.PackageLengthIn > 0, "The captured draft is supposed to carry a box.");

        var fill = Fill(draft);

        // item_dimensions is the product. The draft has the box, which is bigger by whatever padding
        // the seller used — and the box IS accepted where Amazon asks for the box.
        Assert.Null(Attribute(fill, "item_dimensions").Payload);
        Assert.Equal(AmazonFillState.Filled, Attribute(fill, "item_package_weight").State);
    }

    [Fact]
    public void A_for_parts_draft_is_refused_rather_than_graded_into_Amazons_lowest_condition()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        draft.Condition = "FOR_PARTS_OR_NOT_WORKING";

        var fill = Fill(draft);

        // Amazon's lowest grade means a WORKING item with wear. Mapping a dead one into it is a
        // misdescription the buyer finds out about on arrival.
        var condition = Attribute(fill, "condition_type");
        Assert.Equal(AmazonFillState.MissingRequired, condition.State);
        Assert.Null(condition.Payload);
        Assert.Contains("no such condition", condition.Note, StringComparison.OrdinalIgnoreCase);
    }

    // ── Amazon's own vocabulary ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_seller_writes_the_label_and_Amazon_gets_the_token()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // The draft's Item Specific says "Country of Manufacture: China". Amazon publishes the enum
        // as ["US","CN",…] and only shows "China" as a label, so the payload has to carry CN.
        var country = Attribute(fill, "country_of_origin");
        Assert.Equal(AmazonFillState.Filled, country.State);
        Assert.Equal("CN", country.Payload![0]!["value"]!.GetValue<string>());
        Assert.Contains("China", country.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_Amazon_does_not_publish_is_withheld_rather_than_submitted()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        draft.ItemSpecifics["Country of Manufacture"] = "Taiwan";

        var fill = Fill(draft);

        // Amazon does not say which attribute failed until after submission, so an illegal value is
        // worth less than none: it costs the same rejection and hides which field caused it.
        var country = Attribute(fill, "country_of_origin");
        Assert.Equal(AmazonFillState.InvalidValue, country.State);
        Assert.Null(country.Payload);
        Assert.Contains("Taiwan", country.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("country_of_origin", fill.Payload.Select(p => p.Key));
    }

    // ── Amazon's envelope ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Each_value_carries_only_the_selectors_its_own_attribute_declares()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // item_name is per-marketplace AND per-language; condition_type is only per-marketplace.
        // Stamping a language tag on the second is an attribute Amazon did not ask for.
        var title = Attribute(fill, "item_name").Payload![0]!.AsObject();
        Assert.Equal(Marketplace, title["marketplace_id"]!.GetValue<string>());
        Assert.Equal("en_US", title["language_tag"]!.GetValue<string>());

        var condition = Attribute(fill, "condition_type").Payload![0]!.AsObject();
        Assert.Equal(Marketplace, condition["marketplace_id"]!.GetValue<string>());
        Assert.False(condition.ContainsKey("language_tag"));
    }

    [Fact]
    public void A_selector_this_app_cannot_answer_is_left_off_rather_than_guessed()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // purchasable_offer's selectors are marketplace_id AND audience. Audience chooses between
        // consumer and business pricing; omitted means all buyers, and guessing would publish the
        // wrong price to the wrong buyers.
        var offer = Attribute(fill, "purchasable_offer").Payload![0]!.AsObject();
        Assert.True(offer.ContainsKey("marketplace_id"));
        Assert.False(offer.ContainsKey("audience"));
    }

    [Fact]
    public void The_price_is_folded_into_the_nested_shape_Amazon_actually_charges_from()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // A currency wrapping a price list wrapping a schedule. An always-on offer is one schedule
        // entry with a price and no start date.
        var offer = Attribute(fill, "purchasable_offer").Payload![0]!;
        Assert.Equal("USD", offer["currency"]!.GetValue<string>());

        var schedule = offer["our_price"]![0]!["schedule"]![0]!;
        Assert.Equal(549.99m, schedule["value_with_tax"]!.GetValue<decimal>());
        Assert.Null(schedule["start_at"]);
    }

    [Fact]
    public void A_composite_is_built_against_the_schemas_own_child_names_and_units()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // 3 lb 8 oz is 3.5 lb, and "pounds" is Amazon's spelling only because the schema's enum says
        // so — a product type publishing a different set gets no value rather than a converted one.
        var weight = Attribute(fill, "item_package_weight").Payload![0]!;
        Assert.Equal(3.5m, weight["value"]!.GetValue<decimal>());
        Assert.Equal("pounds", weight["unit"]!.GetValue<string>());
    }

    [Fact]
    public void Amazon_takes_five_bullet_points_so_a_sixth_is_not_sent()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // maxUniqueItems is 5 on this attribute and the draft has far more to say. A sixth bullet is
        // a rejection, not a bullet Amazon quietly drops.
        var bullets = Attribute(fill, "bullet_point");
        Assert.Equal(5, bullets.Payload!.AsArray().Count);
        Assert.Contains("left off", bullets.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_attribute_Amazon_fills_itself_is_not_reported_as_a_gap()
    {
        var fill = Fill(AmazonListingFillFixtures.RealDraft());

        // item_type_keyword is hidden and non-editable: Amazon sets it and rejects a seller who does.
        var keyword = Attribute(fill, "item_type_keyword");
        Assert.Equal(AmazonFillState.Empty, keyword.State);
        Assert.Contains("Amazon sets this itself", keyword.Note, StringComparison.Ordinal);
    }

    // ── Lengths ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Prose_is_cut_to_Amazons_limit_and_says_so_but_a_name_never_is()
    {
        var draft = AmazonListingFillFixtures.RealDraft();
        draft.Description = "<p>" + new string('x', 2600) + "</p>";
        draft.Brand = new string('B', 80);                  // the schema's limit here is 50

        var fill = Fill(draft);

        // Cutting a description keeps it true. Cutting a brand produces a different brand.
        var description = Attribute(fill, "product_description");
        Assert.Equal(AmazonFillState.Filled, description.State);
        Assert.Equal(2000, description.Payload![0]!["value"]!.GetValue<string>().Length);
        Assert.Contains("Cut from", description.Note, StringComparison.Ordinal);

        var brand = Attribute(fill, "brand");
        Assert.Equal(AmazonFillState.TooLong, brand.State);
        Assert.Null(brand.Payload);
    }

    // ── When there is nothing to fill against ────────────────────────────────────────────────

    [Fact]
    public void A_product_type_that_could_not_be_read_fills_nothing_and_says_why()
    {
        var definition = new AmazonProductTypeDefinition
        {
            Status  = AmazonDefinitionStatus.NotConfigured,
            Message = "This deployment has no Amazon credentials.",
        };

        var fill = AmazonListingMapper.Map(AmazonListingFillFixtures.RealDraft(), definition);

        // "Amazon requires nothing of this draft" and "we could not ask Amazon" must never look
        // alike — an empty attribute list with CanSubmit true would say the first.
        Assert.Empty(fill.Attributes);
        Assert.False(fill.CanSubmit);
        Assert.Equal("This deployment has no Amazon credentials.", fill.Headline);
    }

    [Fact]
    public void The_sandbox_notice_survives_the_fill()
    {
        var fill = AmazonListingMapper.Map(
            AmazonListingFillFixtures.RealDraft(), AmazonListingFillFixtures.SpeakerDefinition(),
            Marketplace, "This is the SP-API sandbox, which replays static product type data.");

        // The sandbox answers a query about a Bitcoin miner with luggage, and every layer above it
        // succeeds. If the notice were dropped here, a seller would see nine attributes filled
        // against the requirements of a suitcase.
        Assert.Contains("sandbox", fill.SandboxNotice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SANDBOX:", AmazonListingFillReport.Describe(fill), StringComparison.Ordinal);
    }

    // ── The report ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_report_marks_every_required_attribute_filled_or_missing()
    {
        var report = AmazonListingFillReport.Describe(Fill(AmazonListingFillFixtures.RealDraft()));

        // Printed as well as asserted: this is the phase's acceptance artefact, and running
        //   dotnet test --filter The_report_marks --logger "console;verbosity=detailed"
        // is how anyone regenerates it without a token, an account or a live listing.
        output.WriteLine(report);

        Assert.Contains("REQUIRED ATTRIBUTES (8 of 10 filled)", report, StringComparison.Ordinal);
        Assert.Contains("[filled ] item_name", report, StringComparison.Ordinal);
        Assert.Contains("[MISSING] batteries_required", report, StringComparison.Ordinal);
        Assert.Contains("[BLOCKED]", report, StringComparison.Ordinal);
        Assert.Contains("no value was invented", report, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static AmazonListingFill Fill(ListingData draft) =>
        AmazonListingMapper.Map(draft, AmazonListingFillFixtures.SpeakerDefinition(), Marketplace);

    private static AmazonFilledAttribute Attribute(AmazonListingFill fill, string name) =>
        fill.Attributes.SingleOrDefault(a => a.Name == name)
        ?? throw new InvalidOperationException($"The product type has no attribute called {name}.");

    private static string Value(AmazonListingFill fill, string name)
    {
        var payload = Attribute(fill, name).Payload;
        return payload is JsonArray array && array.Count > 0
            ? array[0]!["value"]!.GetValue<string>()
            : "";
    }
}
