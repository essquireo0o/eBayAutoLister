using System.Text.Json;
using System.Text.Json.Nodes;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Phase 4 sends a listing to Amazon. The dangerous part is not the sending — it is the reading of
// what comes back, because Amazon answers a submission BEFORE it has judged it and then answers a
// refusal with an HTTP 200. Both of those turn ordinary, reasonable code into an app that tells a
// seller their listing is up when it is not.
//
// So the cases below are mostly about that: what a 200 means, what an ACCEPTED means, and what this
// app is and is not allowed to say about either. The payload cases are the other half — what goes
// into a submission has to be what somebody stated, because a wrong offer sells the wrong thing at
// the wrong price under their name.
public class AmazonSubmissionTests
{
    // ── The verdict, which is not the HTTP status ─────────────────────────────

    [Fact]
    public void An_HTTP_200_carrying_INVALID_is_a_rejection()
    {
        // The trap this phase exists for. Amazon refuses a listing with a SUCCESSFUL http response
        // and puts the refusal in a field, so anything that branches on IsSuccessStatusCode and stops
        // there reports a rejected listing as a published one.
        var submission = AmazonSubmissionResponse.Parse("""
            {"sku":"ING-TEST-001","status":"INVALID","submissionId":"a1b2c3",
             "issues":[{"code":"4000001","message":"The value for condition_type is not valid.",
                        "severity":"ERROR","attributeNames":["condition_type"]}]}
            """, 200, "ING-TEST-001");

        Assert.Equal(AmazonSubmissionState.Rejected, submission.State);
        Assert.False(submission.AwaitingAmazon);
        Assert.Single(submission.Errors);
        Assert.Equal("a1b2c3", submission.SubmissionId);
    }

    [Fact]
    public void An_ACCEPTED_is_queued_and_never_reported_as_published()
    {
        var submission = AmazonSubmissionResponse.Parse(
            """{"sku":"ING-TEST-001","status":"ACCEPTED","submissionId":"a1b2c3","issues":[]}""",
            200, "ING-TEST-001");

        Assert.Equal(AmazonSubmissionState.Submitted, submission.State);
        Assert.True(submission.AwaitingAmazon);

        // The sentence a seller reads. It has to say Amazon has it, and it has to not say it is up.
        var headline = AmazonSubmissionWords.Describe(submission);
        Assert.Contains("Submitted, awaiting Amazon", headline, StringComparison.Ordinal);
        Assert.Contains("has not yet said what became of it", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Nothing_this_phase_says_claims_a_listing_is_live()
    {
        // The rule stated once, in AmazonSubmissionWords, and checked here against every sentence the
        // phase can produce — including the good-news ones, which are the ones that would drift.
        var sentences = new List<string>();

        foreach (var state in new[]
                 {
                     AmazonSubmissionState.Submitted, AmazonSubmissionState.Rejected,
                     AmazonSubmissionState.Blocked, AmazonSubmissionState.NotConfigured,
                     AmazonSubmissionState.Error,
                 })
        {
            var submission = new AmazonSubmission { State = state, Sku = "ING-TEST-001" };
            sentences.Add(AmazonSubmissionWords.Describe(submission));
            sentences.Add(AmazonSubmissionWords.NextAction(submission, "ING-TEST-001"));
        }

        // Including the best case there is: Amazon itself saying the SKU is buyable.
        sentences.Add(AmazonSubmissionWords.Describe(new AmazonListingState
        {
            Sku = "ING-TEST-001", Statuses = ["BUYABLE", "DISCOVERABLE"],
        }));

        foreach (var sentence in sentences)
            foreach (var forbidden in AmazonSubmissionWords.ForbiddenWords)
                Assert.DoesNotContain(forbidden, sentence, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Amazon_reporting_a_SKU_buyable_is_quoted_rather_than_asserted()
    {
        var state = AmazonListingStateResponse.Parse("""
            {"sku":"ING-TEST-001",
             "summaries":[{"marketplaceId":"ATVPDKIKX0DER","asin":"B08N5WRWNW","productType":"PRODUCT",
                           "conditionType":"new_new","status":["BUYABLE","DISCOVERABLE"],
                           "itemName":"Echo Dot (4th Gen)"}],
             "issues":[]}
            """, "ING-TEST-001");

        Assert.True(state.AmazonSaysBuyable);
        Assert.Equal("B08N5WRWNW", state.Asin);

        // "Amazon reports X" survives being wrong; "the listing is live" is this app vouching for
        // something it never observed.
        Assert.Contains("Amazon reports", AmazonSubmissionWords.Describe(state), StringComparison.Ordinal);
    }

    [Fact]
    public void Discoverable_without_buyable_is_not_folded_into_a_yes_or_a_no()
    {
        // A real Amazon state: a shopper can find the listing and cannot buy from it. Both a boolean
        // "live" and a boolean "failed" describe it wrongly.
        var state = AmazonListingStateResponse.Parse("""
            {"sku":"S1","summaries":[{"status":["DISCOVERABLE"],"asin":"B08N5WRWNW"}],"issues":[]}
            """, "S1");

        Assert.False(state.AmazonSaysBuyable);
        Assert.False(state.HasErrors);
        Assert.Contains("not", AmazonSubmissionWords.Describe(state), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DISCOVERABLE", AmazonSubmissionWords.Describe(state), StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejection_that_arrives_after_processing_surfaces_against_the_SKU()
    {
        // This is the whole asynchronous half. The submission came back ACCEPTED with no issues; the
        // listing failed minutes later, and the ONLY place that fact exists is the SKU.
        var state = AmazonListingStateResponse.Parse("""
            {"sku":"ING-TEST-001",
             "summaries":[{"marketplaceId":"ATVPDKIKX0DER","productType":"PRODUCT","status":[]}],
             "issues":[{"code":"90220",
                        "message":"'merchant_suggested_asin' does not match a product in the Amazon catalog.",
                        "severity":"ERROR","attributeNames":["merchant_suggested_asin"]},
                       {"code":"18027","message":"Images are recommended for this product type.",
                        "severity":"WARNING","attributeNames":["main_product_image_locator"]}]}
            """, "ING-TEST-001");

        Assert.True(state.HasErrors);
        Assert.False(state.AmazonSaysBuyable);
        Assert.Single(state.Errors);
        Assert.Single(state.Warnings);

        var headline = AmazonSubmissionWords.Describe(state);
        Assert.Contains("rejected this listing after processing", headline, StringComparison.Ordinal);
        Assert.Contains("90220", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void An_error_severity_outranks_an_ACCEPTED_that_came_with_it()
    {
        // Amazon contradicting itself. The safe reading of a contradiction about whether a listing
        // exists is that it does not — the opposite reading tells a seller to stop looking.
        var submission = AmazonSubmissionResponse.Parse("""
            {"sku":"S1","status":"ACCEPTED",
             "issues":[{"code":"99001","message":"Fatal.","severity":"ERROR"}]}
            """, 200, "S1");

        Assert.Equal(AmazonSubmissionState.Rejected, submission.State);
    }

    [Fact]
    public void A_warning_does_not_turn_an_acceptance_into_a_rejection()
    {
        var submission = AmazonSubmissionResponse.Parse("""
            {"sku":"S1","status":"ACCEPTED",
             "issues":[{"code":"18027","message":"Images are recommended.","severity":"WARNING"}]}
            """, 200, "S1");

        Assert.Equal(AmazonSubmissionState.Submitted, submission.State);
        Assert.Single(submission.Warnings);
        Assert.Empty(submission.Errors);
    }

    [Fact]
    public void An_answer_with_no_status_is_unknown_rather_than_either_verdict()
    {
        var submission = AmazonSubmissionResponse.Parse("""{"sku":"S1"}""", 200, "S1");

        Assert.Equal(AmazonSubmissionState.Error, submission.State);
        Assert.Contains("could not be read", AmazonSubmissionWords.Describe(submission),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_403_this_deployment_actually_gets_is_read_as_a_refusal_not_a_listing()
    {
        // Measured, not imagined: this is the verbatim body Amazon's sandbox returned on 2026-08-15
        // to the exact PUT this app builds, sent without a token. See the Phase 4 verification file.
        const string body = """
            {"errors":[{"code":"Unauthorized","message":"Access to requested resource is denied.",
                        "details":"Access token is missing in the request header."}]}
            """;

        var submission = AmazonSubmissionResponse.Parse(body, 403, "ING-TEST-001");

        Assert.Equal(AmazonSubmissionState.Rejected, submission.State);
        Assert.False(submission.AwaitingAmazon);

        // Amazon's error array is not its issues array, and reading one as the other would report
        // this as a listing problem — sending a seller to fix an attribute when the fix is a token.
        Assert.Empty(submission.Issues);
    }

    // ── The payload ───────────────────────────────────────────────────────────

    [Fact]
    public void An_offer_asks_Amazon_to_judge_the_offer_and_not_the_product()
    {
        var body = AmazonOfferPayload.BuildOffer(Offer(), "ATVPDKIKX0DER");

        // The expensive mistake is LISTING here: Amazon would then demand the whole product schema —
        // title, bullets, dimensions, country of origin — for a product it already has.
        Assert.Equal(AmazonDefinitionsApi.RequirementsOfferOnly, body["requirements"]!.GetValue<string>());
        Assert.Equal("PRODUCT", body["productType"]!.GetValue<string>());
    }

    [Fact]
    public void The_offer_payload_is_the_shape_Amazon_documents()
    {
        var attributes = AmazonOfferPayload.OfferAttributes(Offer(), "ATVPDKIKX0DER");

        Assert.Equal("B08N5WRWNW",
            attributes["merchant_suggested_asin"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("new_new", attributes["condition_type"]![0]!["value"]!.GetValue<string>());

        // A price is a currency wrapping a named price wrapping a schedule, because the same offer
        // can carry a sale price with a start and an end date. A plain number is rejected.
        var offer = attributes["purchasable_offer"]![0]!;
        Assert.Equal("USD", offer["currency"]!.GetValue<string>());
        Assert.Equal(549.99m,
            offer["our_price"]![0]!["schedule"]![0]!["value_with_tax"]!.GetValue<decimal>());

        var availability = attributes["fulfillment_availability"]![0]!;
        Assert.Equal("DEFAULT", availability["fulfillment_channel_code"]!.GetValue<string>());
        Assert.Equal(3, availability["quantity"]!.GetValue<int>());
    }

    [Fact]
    public void Stock_carries_no_marketplace_or_language_because_it_is_not_a_presentation()
    {
        var attributes = AmazonOfferPayload.OfferAttributes(Offer(), "ATVPDKIKX0DER");
        var availability = attributes["fulfillment_availability"]![0]!.AsObject();

        // Stamping the envelope selectors onto it would be sending Amazon attributes it did not ask
        // for on that field — the same rule AmazonListingMapper.Envelope follows per attribute.
        Assert.False(availability.ContainsKey("marketplace_id"));
        Assert.False(availability.ContainsKey("language_tag"));

        // While everything that IS marketplace-scoped carries it.
        Assert.Equal("ATVPDKIKX0DER",
            attributes["condition_type"]![0]!["marketplace_id"]!.GetValue<string>());
    }

    [Fact]
    public void An_unwritten_condition_note_is_absent_rather_than_empty()
    {
        var attributes = AmazonOfferPayload.OfferAttributes(Offer(), "ATVPDKIKX0DER");
        Assert.False(attributes.ContainsKey("condition_note"));

        var withNote = Offer();
        withNote.ConditionNote = "Sealed, one corner scuffed.";
        var present = AmazonOfferPayload.OfferAttributes(withNote, "ATVPDKIKX0DER");
        Assert.Equal("Sealed, one corner scuffed.", present["condition_note"]![0]!["value"]!.GetValue<string>());
    }

    // ── What this app refuses to send ─────────────────────────────────────────

    [Theory]
    [InlineData("", "ING-1", "new_new", 10.0, 1, "no_asin")]
    [InlineData("B08N5W", "ING-1", "new_new", 10.0, 1, "bad_asin")]
    [InlineData("B08N5WRWNW", "", "new_new", 10.0, 1, "no_sku")]
    [InlineData("B08N5WRWNW", "ING-1", "", 10.0, 1, "no_condition")]
    [InlineData("B08N5WRWNW", "ING-1", "used_god", 10.0, 1, "unknown_condition")]
    [InlineData("B08N5WRWNW", "ING-1", "new_new", 0.0, 1, "bad_price")]
    public void Each_missing_fact_is_named_rather_than_defaulted(
        string asin, string sku, string condition, double price, int quantity, string expected)
    {
        var problem = AmazonOfferCheck.Check(new AmazonOfferRequest
        {
            Asin = asin, Sku = sku, Condition = condition,
            Price = price == 0 ? 0m : (decimal)price, Quantity = quantity,
        });

        Assert.NotNull(problem);
        Assert.Equal(expected, problem.Code);

        // Each refusal has to say what to do about it. "no_asin" alone is not something a seller acts on.
        Assert.False(string.IsNullOrWhiteSpace(problem.NextAction));
    }

    [Fact]
    public void A_quantity_of_zero_is_a_fact_and_an_absent_one_is_not()
    {
        // The reason Quantity is nullable. Zero means out of stock — a real thing to submit — so a
        // plain int could not tell it from "nobody said", and defaulting to 1 would publish a stock
        // level this app made up.
        var stated = Offer();
        stated.Quantity = 0;
        Assert.Null(AmazonOfferCheck.Check(stated));

        var unstated = Offer();
        unstated.Quantity = null;
        Assert.Equal("no_quantity", AmazonOfferCheck.Check(unstated)!.Code);
    }

    [Fact]
    public void A_price_is_never_carried_over_from_anywhere()
    {
        var offer = Offer();
        offer.Price = null;
        Assert.Equal("no_price", AmazonOfferCheck.Check(offer)!.Code);
    }

    [Fact]
    public void A_SKU_over_Amazons_limit_is_refused_rather_than_truncated()
    {
        var offer = Offer();
        offer.Sku = new string('S', AmazonOfferCheck.MaxSkuLength + 1);

        // A truncated SKU is a different SKU, and a submission REPLACES whatever is at it — so
        // shortening one for the seller can overwrite a listing they already have.
        Assert.Equal("sku_too_long", AmazonOfferCheck.Check(offer)!.Code);
    }

    [Fact]
    public void Production_is_refused_outright_even_when_fully_configured()
    {
        var options = new AmazonOptions
        {
            ClientId = "amzn1.application-oa2-client.x", ClientSecret = "secret",
            RefreshToken = "Atzr|x", MarketplaceId = "ATVPDKIKX0DER", SellerId = "A1B2C3",
            Sandbox = false,
        };

        // Everything a call needs is present. That is precisely the state where a guard matters:
        // this is the only Amazon path that writes, and a wrong one is a real listing on a real
        // account that a shopper can buy from before anybody notices.
        Assert.True(options.CanCall);

        var problem = AmazonSubmitGuard.Check(options);
        Assert.NotNull(problem);
        Assert.Equal("production_refused", problem.Code);
    }

    [Fact]
    public void The_sandbox_is_allowed_through_when_the_credentials_are_there()
    {
        Assert.Null(AmazonSubmitGuard.Check(new AmazonOptions
        {
            ClientId = "amzn1.application-oa2-client.x", ClientSecret = "secret",
            RefreshToken = "Atzr|x", MarketplaceId = "ATVPDKIKX0DER", SellerId = "A1B2C3",
        }));
    }

    // ── The address ───────────────────────────────────────────────────────────

    [Fact]
    public void A_SKU_with_a_slash_in_it_addresses_one_resource()
    {
        var path = AmazonListingsApi.ItemPath("A1B2C3", "ING/2026+A", "ATVPDKIKX0DER");

        // Unescaped, the slash turns one SKU into a path that addresses something else entirely and
        // the plus silently becomes a space.
        Assert.Contains("ING%2F2026%2BA", path, StringComparison.Ordinal);
        Assert.DoesNotContain("ING/2026+A", path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_state_call_asks_for_the_issues_that_the_submission_could_not_carry()
    {
        var path = AmazonListingsApi.StatePath("A1B2C3", "S1", "ATVPDKIKX0DER");

        // Without includedData Amazon returns a summary with no issues on it, which reads as a
        // listing with nothing wrong rather than as a question that was never asked.
        Assert.Contains("includedData=issues%2Csummaries", path, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seller_id_is_taken_out_of_anything_meant_to_be_read()
    {
        var url = "https://sandbox.sellingpartnerapi-na.amazon.com" +
                  AmazonListingsApi.ItemPath("A1B2C3D4E5F6G7", "S1", "ATVPDKIKX0DER");

        var redacted = AmazonListingsApi.Redact(url, "A1B2C3D4E5F6G7");

        Assert.DoesNotContain("A1B2C3D4E5F6G7", redacted, StringComparison.Ordinal);
        Assert.Contains("{sellerId}", redacted, StringComparison.Ordinal);

        // The host stays. It is the thing that proves this went to the sandbox rather than to a real
        // seller account, which is the single most important fact in the whole record.
        Assert.Contains("sandbox.sellingpartnerapi-na.amazon.com", redacted, StringComparison.Ordinal);
    }

    // ── The product path reuses the fill rather than rebuilding it ────────────

    [Fact]
    public void A_new_product_submits_the_payload_the_fill_report_showed()
    {
        var fill = new AmazonListingFill
        {
            ProductType = "BLUETOOTH_SPEAKER",
            Payload = new JsonObject
            {
                ["item_name"] = new JsonArray { new JsonObject { ["value"] = "A speaker" } },
            },
        };

        var body = AmazonOfferPayload.BuildProduct(
            fill, new AmazonProductSubmitRequest { Sku = "S1", Quantity = 2 }, "ATVPDKIKX0DER");

        Assert.Equal("BLUETOOTH_SPEAKER", body["productType"]!.GetValue<string>());
        Assert.Equal(AmazonDefinitionsApi.RequirementsListing, body["requirements"]!.GetValue<string>());

        // The draft's attributes arrive unaltered: what the seller reviewed is what gets sent.
        var attributes = body["attributes"]!.AsObject();
        Assert.Equal("A speaker", attributes["item_name"]![0]!["value"]!.GetValue<string>());

        // Plus the one fact the fill deliberately refuses to produce — see NeverInvent.
        Assert.Equal(2, attributes["fulfillment_availability"]![0]!["quantity"]!.GetValue<int>());
    }

    [Fact]
    public void The_fills_own_payload_is_not_mutated_by_building_a_submission_from_it()
    {
        var fill = new AmazonListingFill
        {
            ProductType = "BLUETOOTH_SPEAKER",
            Payload = new JsonObject { ["item_name"] = new JsonArray() },
        };

        AmazonOfferPayload.BuildProduct(
            fill, new AmazonProductSubmitRequest { Sku = "S1", Quantity = 2 }, "ATVPDKIKX0DER");

        // The fill is the artefact a seller reviewed. A submission that edited it in place would
        // leave the report describing something other than what was sent.
        Assert.False(fill.Payload.ContainsKey("fulfillment_availability"));
    }

    // ── Reading issues ────────────────────────────────────────────────────────

    [Fact]
    public void An_ungraded_issue_is_left_ungraded()
    {
        var issues = AmazonIssueReader.Parse("""
            {"issues":[{"code":"1","message":"Something."}]}
            """);

        // Guessing it upward invents a rejection; guessing it downward hides one.
        Assert.Single(issues);
        Assert.False(issues[0].IsError);
        Assert.False(issues[0].IsWarning);
        Assert.Equal("", issues[0].Severity);
    }

    [Fact]
    public void An_unreadable_body_yields_no_issues_rather_than_an_exception()
    {
        Assert.Empty(AmazonIssueReader.Parse("<html>gateway error</html>"));
        Assert.Empty(AmazonIssueReader.Parse(""));
        Assert.Empty(AmazonIssueReader.Parse(null));
    }

    [Fact]
    public void The_report_quotes_the_exchange_because_a_verdict_alone_cannot_be_checked()
    {
        var submission = AmazonSubmissionResponse.Parse(
            """{"sku":"S1","status":"ACCEPTED","submissionId":"a1","issues":[]}""", 200, "S1");

        submission.Call = new AmazonCall
        {
            Method = "PUT",
            Url = "https://sandbox.sellingpartnerapi-na.amazon.com/listings/2021-08-01/items/{sellerId}/S1",
            RequestBody = AmazonOfferPayload.ToJson(AmazonOfferPayload.BuildOffer(Offer(), "ATVPDKIKX0DER")),
            HttpStatus = 200,
            ResponseBody = """{"sku":"S1","status":"ACCEPTED","submissionId":"a1","issues":[]}""",
            RequestId = "1a2b3c4d-0000-0000-0000-000000000000",
        };
        submission.Headline = AmazonSubmissionWords.Describe(submission);

        var text = AmazonSubmissionReport.Describe(submission);

        Assert.Contains("PUT https://sandbox.", text, StringComparison.Ordinal);
        Assert.Contains("merchant_suggested_asin", text, StringComparison.Ordinal);
        Assert.Contains("1a2b3c4d", text, StringComparison.Ordinal);
        Assert.Contains("This is not a published listing", text, StringComparison.Ordinal);

        // An empty issue list must not read as a clean bill of health — Amazon attaches most of them
        // after this response was sent.
        Assert.Contains("not a promise", text, StringComparison.Ordinal);
    }

    private static AmazonOfferRequest Offer() => new()
    {
        Asin = "B08N5WRWNW",
        Sku = "ING-TEST-001",
        Condition = "new_new",
        Price = 549.99m,
        Quantity = 3,
    };
}
