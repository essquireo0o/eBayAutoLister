using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Amazon's product types and the attribute schemas behind them.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is pure — no network, no token, no seller account — and that is not a
/// convenience, it is the only ground available. This deployment cannot obtain an Amazon access
/// token at all: the stored LWA client secret is a placeholder note and no seller has authorised
/// the application, so nothing below could have been proven against the live API even if it were
/// acceptable to make a test depend on it.
/// </para>
/// <para>
/// What that means for these tests is worth being precise about. They prove the app reads Amazon's
/// GRAMMAR correctly — the array-of-object envelope, the closed value lists, the conditional
/// requirements, the local <c>$ref</c>s. They do not prove any particular product type's real
/// required list, which only Amazon can state and only in production. See
/// <see cref="AmazonProductTypeFixtures"/>, and see the sandbox tests at the bottom for why that
/// distinction has to travel all the way into the answer a seller is shown.
/// </para>
/// </remarks>
public class AmazonProductTypeTests
{
    private const string UsMarketplace = "ATVPDKIKX0DER";

    // ── The request ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_multi_word_query_is_sent_as_comma_delimited_keywords()
    {
        // The one that silently returns nothing: Amazon's keywords parameter is a comma-delimited
        // LIST, so "bluetooth speaker" sent as a single keyword containing a space matches nothing
        // and reads exactly like Amazon having no product type for it.
        var path = AmazonDefinitionsApi.SearchPath("bluetooth speaker", UsMarketplace);

        Assert.Contains("keywords=bluetooth%2Cspeaker", path);
        Assert.Contains($"marketplaceIds={UsMarketplace}", path);
        Assert.StartsWith("/definitions/2020-09-01/productTypes?", path);
    }

    [Fact]
    public void Punctuation_and_repeats_are_dropped_from_the_keywords()
    {
        // The seller's own casing is kept — Amazon matches keywords case-insensitively, so mangling
        // them buys nothing. The de-duplication is what is case-insensitive.
        Assert.Equal(["Bluetooth", "speaker", "JBL"],
            AmazonDefinitionsApi.Keywords("Bluetooth speaker, bluetooth — JBL!"));
    }

    [Fact]
    public void The_definition_request_asks_for_the_latest_version_and_the_whole_listing()
    {
        var path = AmazonDefinitionsApi.DefinitionPath("BLUETOOTH_SPEAKER", UsMarketplace);

        Assert.StartsWith("/definitions/2020-09-01/productTypes/BLUETOOTH_SPEAKER?", path);
        Assert.Contains("productTypeVersion=LATEST", path);
        Assert.Contains("requirements=LISTING", path);
        Assert.Contains("locale=en_US", path);
    }

    [Fact]
    public void A_product_type_name_cannot_escape_the_path()
    {
        // The name is echoed from a response, so it is escaped rather than trusted.
        var path = AmazonDefinitionsApi.DefinitionPath("../../catalog/items", UsMarketplace);

        Assert.DoesNotContain("/../", path);
        Assert.Contains("%2F", path);
    }

    // ── The search response ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_search_response_is_read_in_amazons_order()
    {
        var types = AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SearchResponse);

        Assert.Equal(3, types.Count);
        Assert.Equal("SPEAKERS", types[0].Name);
        Assert.Equal("Bluetooth Speaker", types[1].DisplayName);
        Assert.Equal([UsMarketplace], types[1].MarketplaceIds);
    }

    [Fact]
    public void A_product_type_without_a_display_name_still_reads()
    {
        // Amazon omits displayName in some locales. The identifier is the only field that is
        // always there, so the label falls back to a readable form of it.
        var types = AmazonProductTypeSearchResponse.Parse(
            """{"productTypes":[{"name":"BLUETOOTH_SPEAKER","marketplaceIds":["ATVPDKIKX0DER"]}]}""");

        Assert.Single(types);
        Assert.Equal("Bluetooth Speaker", types[0].Label);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("[]")]
    [InlineData("""{"productTypes":"nope"}""")]
    [InlineData("""{"productTypes":[{"displayName":"No name"}]}""")]
    public void An_unusable_search_response_is_no_product_types_rather_than_an_exception(string body) =>
        Assert.Empty(AmazonProductTypeSearchResponse.Parse(body));

    // ── Choosing one ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_exact_product_type_wins_over_the_broader_one()
    {
        var types = AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SearchResponse);

        var chosen = AmazonProductTypeChooser.Choose("bluetooth speaker", types);

        Assert.Equal("BLUETOOTH_SPEAKER", chosen?.ProductType.Name);
        Assert.Equal("high", chosen!.Confidence);
        Assert.Contains("exactly", chosen.Why);
    }

    [Fact]
    public void A_singular_query_reaches_amazons_plural_product_type()
    {
        // Amazon names most product types in the plural. A seller does not.
        var chosen = AmazonProductTypeChooser.Choose("speaker",
            [new AmazonProductType("SPEAKERS", "Speakers", [UsMarketplace]),
             new AmazonProductType("HOME_THEATER_SYSTEM", "Home Theater System", [UsMarketplace])]);

        Assert.Equal("SPEAKERS", chosen?.ProductType.Name);
    }

    [Fact]
    public void The_display_name_can_be_what_matches()
    {
        // The identifier and the label disagree often enough to matter: the seller's words have to
        // reach both.
        var chosen = AmazonProductTypeChooser.Choose("headphones",
            [new AmazonProductType("HEADPHONES_AND_HEADSETS", "Headphones", [UsMarketplace]),
             new AmazonProductType("SPEAKERS", "Speakers", [UsMarketplace])]);

        Assert.Equal("HEADPHONES_AND_HEADSETS", chosen?.ProductType.Name);
    }

    [Fact]
    public void Two_product_types_the_words_cannot_separate_are_refused()
    {
        // The failure this prevents: a listing built against the wrong product type is not caught
        // here, it is caught by Amazon rejecting a submission for missing attributes belonging to
        // a schema the seller was never shown.
        var chosen = AmazonProductTypeChooser.Choose("speaker cable",
            [new AmazonProductType("SPEAKER_CABLE", "Speaker Cable", [UsMarketplace]),
             new AmazonProductType("CABLE_SPEAKER", "Cable Speaker", [UsMarketplace])]);

        Assert.Null(chosen);
    }

    [Fact]
    public void A_query_sharing_nothing_with_several_candidates_is_refused()
    {
        var chosen = AmazonProductTypeChooser.Choose("antminer s19",
            [new AmazonProductType("LUGGAGE", "Luggage", [UsMarketplace]),
             new AmazonProductType("SHOES", "Shoes", [UsMarketplace])]);

        Assert.Null(chosen);
    }

    [Fact]
    public void Amazons_only_answer_is_taken_but_labelled_low_confidence()
    {
        // Amazon ran its own match to return this at all, so it is Amazon's answer rather than a
        // choice between alternatives — used, and honestly labelled.
        var chosen = AmazonProductTypeChooser.Choose("antminer s19",
            [new AmazonProductType("COMPUTER_COMPONENT", "Computer Component", [UsMarketplace])]);

        Assert.Equal("COMPUTER_COMPONENT", chosen?.ProductType.Name);
        Assert.Equal("low", chosen!.Confidence);
    }

    [Fact]
    public void Nothing_offered_is_nothing_chosen() =>
        Assert.Null(AmazonProductTypeChooser.Choose("bluetooth speaker", []));

    [Fact]
    public void The_choice_does_not_depend_on_the_order_amazon_returned_them()
    {
        var types = AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SearchResponse);
        var reversed = types.AsEnumerable().Reverse().ToList();

        Assert.Equal(AmazonProductTypeChooser.Choose("bluetooth speaker", types)?.ProductType.Name,
                     AmazonProductTypeChooser.Choose("bluetooth speaker", reversed)?.ProductType.Name);
    }

    // ── The definition response ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_definition_carries_the_version_the_cache_is_keyed_by()
    {
        var definition = AmazonDefinitionResponse.Parse(AmazonProductTypeFixtures.DefinitionResponse);

        Assert.Equal("BLUETOOTH_SPEAKER", definition.ProductType);
        Assert.Equal("U8L4z4Ud95N16tZlR7rsmbQ==", definition.Version);
        Assert.Equal("TBr8ubaxXrUyay9hmxUXUw==", definition.SchemaChecksum);
        Assert.Equal("LISTING", definition.Requirements);
        Assert.Equal("ENFORCED", definition.RequirementsEnforced);
        Assert.Equal("en_US", definition.Locale);
        Assert.Equal(UsMarketplace, definition.MarketplaceId);
        Assert.Equal(AmazonDefinitionStatus.Ok, definition.Status);
    }

    [Fact]
    public void The_schema_link_is_read_from_the_schema_and_not_the_meta_schema()
    {
        // Both are links to a JSON document and they sit next to each other. Reading the meta-schema
        // by mistake produces a schema that parses and describes Amazon's own vocabulary rather
        // than the product's attributes — valid, and about the wrong thing entirely.
        var definition = AmazonDefinitionResponse.Parse(AmazonProductTypeFixtures.DefinitionResponse);

        Assert.Contains("BLUETOOTH_SPEAKER.json", definition.SchemaUrl);
        Assert.DoesNotContain("/meta/", definition.SchemaUrl);
    }

    [Fact]
    public void A_definition_with_no_schema_link_is_an_error_and_says_why()
    {
        var definition = AmazonDefinitionResponse.Parse(
            """{"productType":"LUGGAGE","productTypeVersion":{"version":"abc"}}""");

        Assert.Equal(AmazonDefinitionStatus.Error, definition.Status);
        Assert.Contains("no link to the schema", definition.Message);
    }

    [Fact]
    public void The_property_groups_are_flattened_to_a_group_per_attribute()
    {
        var groups = AmazonDefinitionResponse.ParseGroups(AmazonProductTypeFixtures.DefinitionResponse);

        Assert.Equal("Product Identity", groups["item_name"]);
        Assert.Equal("Offer", groups["purchasable_offer"]);
        Assert.Equal("Product Details", groups["bullet_point"]);
    }

    // ── The schema: required versus optional ─────────────────────────────────────────────────

    private static List<AmazonAttribute> ParseFixture() =>
        AmazonSchemaParser.Parse(
            AmazonProductTypeFixtures.BluetoothSpeakerSchema,
            AmazonDefinitionResponse.ParseGroups(AmazonProductTypeFixtures.DefinitionResponse));

    private static AmazonAttribute Attribute(string name) =>
        ParseFixture().Single(a => a.Name == name);

    [Fact]
    public void The_required_attributes_are_exactly_the_ones_amazon_listed()
    {
        var required = ParseFixture().Where(a => a.Required).Select(a => a.Name).Order().ToList();

        Assert.Equal(
            ["batteries_required", "brand", "bullet_point", "condition_type", "country_of_origin",
             "item_name", "list_price", "product_description", "purchasable_offer",
             "supplier_declared_dg_hz_regulation"],
            required);
    }

    [Fact]
    public void Required_attributes_come_first_so_the_nine_that_matter_are_not_buried()
    {
        var attributes = ParseFixture();
        var lastRequired  = attributes.FindLastIndex(a => a.Required);
        var firstOptional = attributes.FindIndex(a => !a.IsRequiredSomehow);

        Assert.True(lastRequired < firstOptional,
            "An optional attribute appeared before a required one.");
    }

    [Fact]
    public void An_optional_attribute_is_not_reported_as_required()
    {
        var color = Attribute("color");

        Assert.False(color.Required);
        Assert.False(color.ConditionallyRequired);
    }

    // ── The schema: conditional requirements ─────────────────────────────────────────────────

    [Fact]
    public void An_attribute_required_only_by_an_anyOf_branch_is_neither_required_nor_optional()
    {
        // Amazon's "a product identifier OR an ASIN" is a third state. Called required, a seller
        // holding an ASIN is sent to find a UPC they are exempt from; called optional, a seller
        // with neither submits and is rejected.
        var identifier = Attribute("externally_assigned_product_identifier");

        Assert.False(identifier.Required);
        Assert.True(identifier.ConditionallyRequired);
        Assert.Contains("merchant_suggested_asin", identifier.RequirementNote);
    }

    [Fact]
    public void Both_sides_of_the_alternative_name_the_other()
    {
        var asin = Attribute("merchant_suggested_asin");

        Assert.True(asin.ConditionallyRequired);
        Assert.Contains("externally_assigned_product_identifier", asin.RequirementNote);
    }

    [Fact]
    public void A_single_branch_anyOf_is_a_plain_requirement_written_the_long_way()
    {
        // One branch is not an alternative to anything, and reporting it as conditional would tell
        // a seller they had a choice they do not have.
        var attributes = AmazonSchemaParser.Parse("""
        {
          "type": "object",
          "required": [],
          "anyOf": [ { "required": ["item_name"] } ],
          "properties": { "item_name": { "type": "array", "items": { "type": "object",
              "properties": { "value": { "type": "string" } } } } }
        }
        """);

        Assert.False(attributes.Single().ConditionallyRequired);
    }

    // ── The schema: unwrapping the envelope ──────────────────────────────────────────────────

    [Fact]
    public void A_title_is_reported_as_a_string_not_as_an_array_of_objects()
    {
        // Amazon declares nearly every attribute as an array of objects so a value can differ per
        // marketplace and per language. What the seller types is the string inside, and a form
        // built on the literal declaration asks for an array.
        var itemName = Attribute("item_name");

        Assert.Equal("string", itemName.Type);
        Assert.Equal("array", itemName.RawType);
        Assert.Equal("Title", itemName.Title);
    }

    [Fact]
    public void The_tighter_of_the_two_length_limits_is_the_one_reported()
    {
        // maxLength counts characters, maxUtf8ByteLength counts bytes, and for anything outside
        // ASCII the byte limit binds first. Reporting the larger would overstate what Amazon takes.
        Assert.Equal(180, Attribute("item_name").MaxLength);
    }

    [Fact]
    public void A_boolean_and_an_integer_survive_the_unwrapping_as_themselves()
    {
        // Amazon rejects the string "2" where it wants an integer, which eBay never cared about.
        Assert.Equal("boolean", Attribute("batteries_required").Type);
        Assert.Equal("integer", Attribute("number_of_items").Type);
    }

    [Fact]
    public void A_closed_list_is_reported_with_amazons_values_and_amazons_labels()
    {
        var condition = Attribute("condition_type");

        Assert.True(condition.SelectionOnly);
        Assert.Equal(["new_new", "used_like_new", "used_very_good", "used_good", "used_acceptable"],
            condition.Values);
        Assert.Equal("New", condition.ValueLabels[0]);
        Assert.Equal("Used - Like New", condition.ValueLabels[1]);
    }

    [Fact]
    public void A_ref_into_defs_is_followed_so_the_values_are_not_lost()
    {
        var country = Attribute("country_of_origin");

        Assert.Equal("string", country.Type);
        Assert.True(country.SelectionOnly);
        Assert.Contains("US", country.Values);
        Assert.Equal("United States", country.ValueLabels[0]);
    }

    [Fact]
    public void The_unique_cap_decides_whether_more_than_one_value_is_allowed()
    {
        // maxItems: 20 with maxUniqueItems: 1 allows ONE title said twenty ways — per marketplace,
        // per language — not twenty titles. Reading maxItems alone offers the seller twenty boxes.
        Assert.False(Attribute("item_name").MultiSelect);
        Assert.True(Attribute("bullet_point").MultiSelect);
    }

    [Fact]
    public void The_selectors_are_stripped_so_a_single_value_is_a_single_field()
    {
        // Left in, every one-value attribute would present as a three-field form asking the seller
        // for a marketplace and a language they never chose.
        var brand = Attribute("brand");

        Assert.Empty(brand.Children);
        Assert.Equal("string", brand.Type);
    }

    [Fact]
    public void Amazons_own_examples_are_carried_because_they_state_the_format()
    {
        Assert.Contains("JBL Flip 6 Portable Bluetooth Speaker, Black", Attribute("item_name").Examples);
    }

    [Fact]
    public void An_attribute_amazon_sets_itself_is_marked_rather_than_dropped()
    {
        // Kept, not hidden: something has to build the payload, and an attribute that silently
        // vanished from the app's view of the schema is one nothing can account for.
        var keyword = Attribute("item_type_keyword");

        Assert.True(keyword.Hidden);
        Assert.False(keyword.Editable);
    }

    // ── The schema: genuine composites ───────────────────────────────────────────────────────

    [Fact]
    public void A_price_is_reported_as_an_object_with_its_parts()
    {
        // Two properties left after the selectors are stripped, so this is a real composite rather
        // than a single value in an envelope — an amount without a currency is not a price.
        var listPrice = Attribute("list_price");

        Assert.Equal("object", listPrice.Type);
        Assert.Equal(2, listPrice.Children.Count);
        Assert.All(listPrice.Children, c => Assert.True(c.Required));
        Assert.Equal("number", listPrice.Children.Single(c => c.Name == "value").Type);
        Assert.Contains("USD", listPrice.Children.Single(c => c.Name == "currency").Values);
    }

    [Fact]
    public void A_selector_the_schema_names_itself_is_stripped_even_when_it_is_an_unusual_one()
    {
        // purchasable_offer's selectors are marketplace_id AND audience — a price is chosen by who
        // is buying as well as by where. The schema says so, so the schema is believed rather than
        // the hardcoded pair, and the seller is not asked to fill in an audience as if it were a
        // property of the offer.
        var offer = Attribute("purchasable_offer");

        Assert.Equal("object", offer.Type);
        Assert.DoesNotContain(offer.Children, c => c.Name == "audience");
        Assert.DoesNotContain(offer.Children, c => c.Name == "marketplace_id");
        Assert.Equal(["currency", "our_price"], offer.Children.Select(c => c.Name).Order().ToList());
    }

    [Fact]
    public void A_required_child_is_distinguished_from_an_optional_one_inside_the_same_object()
    {
        var schedule = Attribute("purchasable_offer")
            .Children.Single(c => c.Name == "our_price")
            .Children.Single(c => c.Name == "schedule");

        Assert.True(schedule.Children.Single(c => c.Name == "value_with_tax").Required);
        Assert.False(schedule.Children.Single(c => c.Name == "start_at").Required);
    }

    [Fact]
    public void A_scheduled_price_is_followed_all_the_way_down_to_the_number()
    {
        var schedule = Attribute("purchasable_offer")
            .Children.Single(c => c.Name == "our_price")
            .Children.Single(c => c.Name == "schedule");

        Assert.Equal("number", schedule.Children.Single(c => c.Name == "value_with_tax").Type);
        Assert.True(schedule.Children.Single(c => c.Name == "value_with_tax").Required);
    }

    [Fact]
    public void A_dimension_keeps_its_unit_beside_its_number()
    {
        // The failure this prevents is a listing that says the speaker is 7 of something.
        var length = Attribute("item_dimensions").Children.Single(c => c.Name == "length");

        Assert.Equal("number", length.Children.Single(c => c.Name == "value").Type);
        Assert.Contains("inches", length.Children.Single(c => c.Name == "unit").Values);
    }

    [Fact]
    public void The_group_amazon_files_an_attribute_under_is_carried()
    {
        Assert.Equal("Product Identity", Attribute("item_name").Group);
        Assert.Equal("Offer", Attribute("purchasable_offer").Group);
    }

    // ── The schema: surviving a bad one ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"type":"object"}""")]
    [InlineData("""{"properties":"not an object"}""")]
    public void An_unreadable_schema_is_no_attributes_rather_than_an_exception(string schema) =>
        Assert.Empty(AmazonSchemaParser.Parse(schema));

    [Fact]
    public void A_ref_that_points_at_nothing_leaves_the_attribute_untyped_rather_than_failing()
    {
        var attributes = AmazonSchemaParser.Parse("""
        {
          "type": "object", "required": ["mystery"],
          "properties": { "mystery": { "type": "array", "items": { "type": "object",
              "properties": { "value": { "$ref": "#/$defs/not_here" } } } } }
        }
        """);

        Assert.Single(attributes);
        Assert.True(attributes[0].Required);
        Assert.Equal("", attributes[0].Type);
    }

    [Fact]
    public void A_ref_that_points_at_itself_terminates()
    {
        // A cyclic $ref is a document a hostile or simply broken schema can contain, and following
        // it without a bound fills the stack.
        var attributes = AmazonSchemaParser.Parse("""
        {
          "type": "object",
          "$defs": { "a": { "$ref": "#/$defs/b" }, "b": { "$ref": "#/$defs/a" } },
          "properties": { "looping": { "$ref": "#/$defs/a" } }
        }
        """);

        Assert.Single(attributes);
    }

    [Fact]
    public void An_enum_declared_through_a_branch_is_still_found()
    {
        var attributes = AmazonSchemaParser.Parse("""
        {
          "type": "object",
          "properties": { "size": { "type": "array", "items": { "type": "object",
              "properties": { "value": { "anyOf": [ { "type": "string", "enum": ["small"] },
                                                    { "type": "string", "enum": ["large"] } ] } } } } }
        }
        """);

        Assert.Equal(["small", "large"], attributes[0].Values);
        Assert.Equal("string", attributes[0].Type);
    }

    [Fact]
    public void Mismatched_enum_labels_are_left_out_rather_than_applied_to_the_wrong_values()
    {
        // A label list shorter than its value list would name every value after its neighbour, and
        // a wrong label on a closed list is worse than no label at all.
        var attributes = AmazonSchemaParser.Parse("""
        {
          "type": "object",
          "properties": { "grade": { "type": "array", "items": { "type": "object",
              "properties": { "value": { "type": "string", "enum": ["a","b","c"],
                                         "enumNames": ["Ay","Bee"] } } } } }
        }
        """);

        Assert.Equal(["a", "b", "c"], attributes[0].Values);
        Assert.Empty(attributes[0].ValueLabels);
    }

    // ── The report ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_report_names_the_product_type_and_every_required_attribute_with_its_type()
    {
        var definition = BuiltDefinition();
        var report = AmazonAttributeReport.Describe(definition);

        Assert.Contains("BLUETOOTH_SPEAKER", report);
        Assert.Contains("REQUIRED ATTRIBUTES (10)", report);
        Assert.Contains("CONDITIONALLY REQUIRED (2)", report);

        foreach (var attribute in definition.RequiredAttributes)
        {
            Assert.Contains(attribute.Name, report);
            Assert.Contains(attribute.TypeDescription, report);
        }

        // The optional ones are counted, not listed — 150 attributes with 10 that decide acceptance
        // is a different job from 160 attributes.
        Assert.Contains("OPTIONAL ATTRIBUTES: ", report);
        Assert.DoesNotContain("  color ", report);
    }

    [Fact]
    public void The_report_states_a_length_limit_and_a_closed_list_rather_than_just_the_type()
    {
        var report = AmazonAttributeReport.Describe(BuiltDefinition());

        Assert.Contains("string (max 180)", report);
        Assert.Contains("new_new", report);

        // A closed list a seller picks ONE of, and a closed list they may tick several of, are two
        // different controls. Amazon has both in the same required set here.
        Assert.Contains("one of 5 values", report);   // condition_type
        Assert.Contains("any of 5 values", report);   // supplier_declared_dg_hz_regulation
    }

    /// <summary>
    /// The whole pipeline, minus the network: definition response → groups → schema → attributes.
    /// Also writes the printout to <c>verification/</c>, because the readable answer is the point
    /// of the exercise and a number in a test log is not it.
    /// </summary>
    private static AmazonProductTypeDefinition BuiltDefinition()
    {
        var definition = AmazonDefinitionResponse.Parse(AmazonProductTypeFixtures.DefinitionResponse);
        definition.Attributes = ParseFixture();
        return definition;
    }

    [Fact]
    public void The_printout_is_written_where_it_can_be_read()
    {
        var search = AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SearchResponse);
        var chosen = AmazonProductTypeChooser.Choose("bluetooth speaker", search);
        var definition = BuiltDefinition();

        var text = string.Join(Environment.NewLine,
            "Query        : \"bluetooth speaker\"",
            $"Candidates   : {string.Join(", ", search.Select(t => t.Name))}",
            $"Chosen       : {chosen!.ProductType.Name} ({chosen.Confidence} confidence) — {chosen.Why}",
            "",
            AmazonAttributeReport.Describe(definition, optionalToList: 4));

        var path = Path.Combine(Path.GetTempPath(), "amazon-product-type-report.txt");
        File.WriteAllText(path, text);

        Assert.Contains("BLUETOOTH_SPEAKER", File.ReadAllText(path));
    }

    // ── The sandbox ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Production_answers_carry_no_sandbox_notice() =>
        Assert.Equal("", AmazonSandboxNotice.For("bluetooth speaker",
            AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SearchResponse), sandbox: false));

    [Fact]
    public void The_sandbox_answering_a_speaker_query_with_luggage_says_so()
    {
        // This is the failure that looks exactly like success: a 200 OK, a well-formed response, a
        // real product type and a real schema with real required attributes — belonging to a piece
        // of luggage. The sandbox does not search; it replays a fixed set.
        var notice = AmazonSandboxNotice.For("bluetooth speaker",
            AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SandboxSearchResponse),
            sandbox: true);

        Assert.Contains("static", notice);
        Assert.Contains("LUGGAGE", notice);
        Assert.Contains("production", notice);
    }

    [Fact]
    public void A_sandbox_answer_that_happens_to_match_is_still_flagged_as_canned()
    {
        var notice = AmazonSandboxNotice.For("luggage",
            AmazonProductTypeSearchResponse.Parse(AmazonProductTypeFixtures.SandboxSearchResponse),
            sandbox: true);

        Assert.Contains("canned", notice);
    }

    [Fact]
    public void An_empty_sandbox_answer_is_not_read_as_amazon_having_no_product_type()
    {
        var notice = AmazonSandboxNotice.For("bluetooth speaker", [], sandbox: true);

        Assert.Contains("says nothing about whether Amazon has", notice);
    }

    // ── Following a link out of a response ───────────────────────────────────────────────────

    [Theory]
    [InlineData("https://definitions.s3.us-east-1.amazonaws.com/BLUETOOTH_SPEAKER.json?X-Amz-Expires=300", true)]
    [InlineData("https://sellingpartnerapi-na.amazon.com/thing.json", true)]
    [InlineData("http://definitions.s3.amazonaws.com/x.json", false)]   // not TLS
    [InlineData("https://evil.example.com/x.json", false)]
    [InlineData("https://amazonaws.com.evil.example/x.json", false)]     // the suffix trick
    [InlineData("", false)]
    [InlineData("not a url", false)]
    public void Only_an_amazon_https_link_is_followed(string url, bool expected) =>
        Assert.Equal(expected, AmazonProductTypeService.IsAmazonUrl(url));

    // ── What a refusal means ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_403_with_a_token_attached_points_at_the_role_rather_than_at_the_token()
    {
        // The one that wastes a day: a 403 with a good token is almost never the token. It is the
        // application not carrying the Product Listing role, which no re-issuing of a token fixes.
        var message = AmazonProductTypeService.DescribeFailure(
            """{"errors":[{"code":"Unauthorized","message":"Access to requested resource is denied."}]}""",
            403, "the BLUETOOTH_SPEAKER product type definition");

        Assert.Contains("Product Listing role", message);
        Assert.Contains("re-authorise", message);
    }

    [Fact]
    public void A_403_over_a_missing_token_is_not_reported_as_a_missing_role()
    {
        // Amazon's verbatim answer to exactly the URL this app builds, sent to the real sandbox
        // host with no token. Same status as the case above, opposite fix — and the only thing
        // that tells them apart is the "details" field, so the whole body is what gets read.
        var body = """
        {"errors":[{"code":"Unauthorized","message":"Access to requested resource is denied.",
                    "details":"Access token is missing in the request header."}]}
        """;

        var message = AmazonProductTypeService.DescribeFailure(body, 403, "the product type search");

        Assert.True(AmazonProductTypeService.MentionsMissingToken(body));
        Assert.Contains("no usable access token", message);
        Assert.Contains("Login with Amazon side", message);
        Assert.DoesNotContain("Product Listing role", message);
    }

    [Fact]
    public void A_400_names_the_marketplace_as_the_usual_cause()
    {
        var message = AmazonProductTypeService.DescribeFailure(
            """{"errors":[{"code":"InvalidInput","message":"productType is not valid."}]}""",
            400, "the ANTMINER product type definition");

        Assert.Contains("productType is not valid.", message);
        Assert.Contains("may not exist in another", message);
    }

    [Fact]
    public void Rate_limiting_is_reported_as_frequency_rather_than_as_a_broken_request()
    {
        var message = AmazonProductTypeService.DescribeFailure(null, 429, "the product type search");

        Assert.Contains("rate-limiting", message);
        Assert.Contains("Nothing is wrong with the request", message);
    }

    [Fact]
    public void A_server_error_is_worth_retrying_and_says_so() =>
        Assert.Contains("retrying", AmazonProductTypeService.DescribeFailure("", 503, "the product type search"));

    [Fact]
    public void An_error_body_that_is_not_json_does_not_take_the_message_down() =>
        Assert.Null(AmazonProductTypeService.FirstErrorMessage("<html>Gateway Timeout</html>"));

    // ── The schema cache ─────────────────────────────────────────────────────────────────────

    private static AmazonSchemaCache TempCache() =>
        new(Path.Combine(Path.GetTempPath(), "amazon-schema-cache-" + Guid.NewGuid().ToString("N")));

    private static AmazonCachedSchema Entry(string version, DateTimeOffset? fetchedAt = null) =>
        new("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", version, "checksum",
            fetchedAt ?? DateTimeOffset.UtcNow, AmazonProductTypeFixtures.BluetoothSpeakerSchema);

    [Fact]
    public void A_schema_written_to_the_cache_comes_back_out_of_it()
    {
        var cache = TempCache();
        try
        {
            Assert.True(cache.Write(Entry("v1")));

            var read = cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", "v1");

            Assert.NotNull(read);
            Assert.Equal("v1", read!.Version);
            Assert.Equal(10, AmazonSchemaParser.Parse(read.Schema).Count(a => a.Required));
        }
        finally { Cleanup(cache); }
    }

    [Fact]
    public void A_schema_from_a_different_version_is_a_miss_however_recent_it_is()
    {
        // The whole point of keying on version rather than aging on a clock: Amazon changed the
        // requirements, and a listing built from yesterday's copy is a listing built to be rejected.
        var cache = TempCache();
        try
        {
            cache.Write(Entry("v1"));
            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", "v2"));
        }
        finally { Cleanup(cache); }
    }

    [Fact]
    public void With_no_version_to_compare_the_time_limit_decides()
    {
        // The only case the clock is used for: the definition call failed, so the choice is between
        // a schema from disk and no answer at all.
        var cache = TempCache();
        try
        {
            var now = DateTimeOffset.UtcNow;
            cache.Write(Entry("v1", now - AmazonSchemaCache.Ttl + TimeSpan.FromHours(1)));
            Assert.NotNull(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", null, now));

            cache.Write(Entry("v1", now - AmazonSchemaCache.Ttl - TimeSpan.FromHours(1)));
            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", null, now));
        }
        finally { Cleanup(cache); }
    }

    [Fact]
    public void Each_marketplace_locale_and_requirement_set_is_a_different_schema()
    {
        // The same product type genuinely has different attributes per marketplace and a different
        // set for an offer than for a whole listing. One file for all of them would serve a US
        // seller Germany's requirements.
        var cache = TempCache();
        try
        {
            cache.Write(Entry("v1"));

            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", "A1PA6795UKMFR9", "LISTING", "en_US", "v1"));
            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING_OFFER_ONLY", "en_US", "v1"));
            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "de_DE", "v1"));
        }
        finally { Cleanup(cache); }
    }

    [Fact]
    public void A_corrupt_cache_file_is_a_miss_and_never_a_failure()
    {
        var cache = TempCache();
        try
        {
            cache.Write(Entry("v1"));
            var path = cache.PathFor("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US");
            File.WriteAllText(path, "{ this is not json");

            Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", "v1"));
        }
        finally { Cleanup(cache); }
    }

    [Fact]
    public void Reading_a_cache_that_was_never_written_is_simply_nothing()
    {
        var cache = TempCache();
        Assert.Null(cache.Read("BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", "v1"));
        Assert.Equal(0, cache.Count());
    }

    [Fact]
    public void A_product_type_name_out_of_a_response_cannot_name_a_path()
    {
        // The name is echoed from Amazon. Nothing from a response gets to pick where a file lands.
        var cache = TempCache();
        var path = cache.PathFor("../../../windows/system32/x", UsMarketplace, "LISTING", "en_US");

        Assert.Equal(cache.Root, Path.GetDirectoryName(path));
        Assert.DoesNotContain("..", Path.GetFileName(path));
    }

    [Fact]
    public void An_entry_with_no_schema_is_not_written()
    {
        var cache = TempCache();
        try
        {
            Assert.False(cache.Write(new AmazonCachedSchema(
                "BLUETOOTH_SPEAKER", UsMarketplace, "LISTING", "en_US", "v1", "c", DateTimeOffset.UtcNow, "")));
            Assert.Equal(0, cache.Count());
        }
        finally { Cleanup(cache); }
    }

    private static void Cleanup(AmazonSchemaCache cache)
    {
        try { if (Directory.Exists(cache.Root)) Directory.Delete(cache.Root, recursive: true); }
        catch (IOException) { }
    }
}
