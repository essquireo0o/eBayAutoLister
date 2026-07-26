using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

// The translator decides what the seller is told and whether they are offered a Retry button, so
// each of these is a case where getting it wrong costs them real time or real money: a Retry on a
// rejected API key wastes minutes, no Retry on an "overloaded" throws away a paid-for analysis, and
// a price misread as an HTTP status tells them to wait when they need to fix their listing.
public class FailureTranslatorTests
{
    // ── Transient AI failures: these must offer a retry ─────────────────────

    [Theory]
    [InlineData("Anthropic returned 529 overloaded_error")]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}")]
    public void Overloaded_is_retryable_and_blames_Anthropic_not_the_listing(string message)
    {
        var failure = FailureTranslator.Translate(new Exception(message), FailureDomain.Ai);

        Assert.Equal(FailureKind.Overloaded, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("Anthropic", failure.Headline);
        Assert.True(failure.WorkPreserved);
    }

    [Theory]
    [InlineData("HTTP 429 Too Many Requests")]
    [InlineData("rate_limit_error: number of requests")]
    public void Rate_limits_are_retryable(string message)
    {
        var failure = FailureTranslator.Translate(new Exception(message), FailureDomain.Ai);

        Assert.Equal(FailureKind.RateLimited, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void A_server_error_from_Anthropic_is_retryable()
    {
        var failure = FailureTranslator.Translate(new Exception("status 503 service unavailable"), FailureDomain.Ai);

        Assert.Equal(FailureKind.UpstreamServerError, failure.Kind);
        Assert.True(failure.Retryable);
    }

    // A truncated or prose-wrapped reply is a bad sample, not a bad request — a fresh attempt almost
    // always parses, so this has to be retryable or the seller redoes the work by hand.
    [Fact]
    public void An_unparseable_model_reply_is_retryable()
    {
        var failure = FailureTranslator.Translate(
            new System.Text.Json.JsonException("AI response did not contain a JSON object."), FailureDomain.Ai);

        Assert.Equal(FailureKind.AiUnreadableReply, failure.Kind);
        Assert.True(failure.Retryable);
    }

    // ── Permanent AI failures: a Retry button here is a lie ─────────────────

    [Fact]
    public void A_missing_api_key_is_not_retryable_and_points_at_Settings()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException("Anthropic API key is not configured. Open Settings to add it."),
            FailureDomain.Ai);

        Assert.Equal(FailureKind.AiKeyMissing, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal("ai-key", failure.FixAction);
    }

    [Theory]
    [InlineData("authentication_error: invalid x-api-key")]
    [InlineData("HTTP 401 Unauthorized")]
    public void A_rejected_api_key_is_not_retryable(string message)
    {
        var failure = FailureTranslator.Translate(new Exception(message), FailureDomain.Ai);

        Assert.Equal(FailureKind.AiKeyRejected, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal("ai-key", failure.FixAction);
    }

    [Fact]
    public void Running_out_of_credit_is_reported_as_billing_not_as_an_outage()
    {
        var failure = FailureTranslator.Translate(
            new Exception("Your credit balance is too low to access the Anthropic API"), FailureDomain.Ai);

        Assert.Equal(FailureKind.AiBilling, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void An_oversized_image_says_to_use_a_smaller_one()
    {
        var failure = FailureTranslator.Translate(
            new Exception("request_too_large: image exceeds maximum size"), FailureDomain.Ai);

        Assert.Equal(FailureKind.InputTooLarge, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("smaller", failure.WhatToDo, StringComparison.OrdinalIgnoreCase);
    }

    // Caught by pointing a real /api/analyze call at a file that wasn't an image. The SDK raises this
    // as an HttpRequestException, so it was landing in the network branch: the seller was told to check
    // their internet connection about a request Anthropic had answered instantly, and it was retried
    // three times over an input that could never work.
    [Fact]
    public void An_image_Anthropic_cannot_read_is_a_bad_input_not_a_network_failure()
    {
        var body = """{"type":"error","error":{"type":"invalid_request_error","message":"Could not process image"},"request_id":"req_011CdRP"}""";

        var failure = FailureTranslator.Translate(new HttpRequestException(body), FailureDomain.Ai);

        Assert.Equal(FailureKind.BadInput, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("image", failure.Headline, StringComparison.OrdinalIgnoreCase);
        // Anthropic's own sentence, surfaced rather than buried in the JSON.
        Assert.Contains("Could not process image", failure.WhatHappened);
        Assert.DoesNotContain("connection", failure.WhatToDo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_real_transport_failure_is_still_reported_as_a_network_problem()
    {
        var failure = FailureTranslator.Translate(
            new HttpRequestException("No such host is known. (api.anthropic.com:443)"), FailureDomain.Ai);

        Assert.Equal(FailureKind.Network, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void The_api_sentence_is_extracted_from_the_error_body()
    {
        Assert.Equal("Could not process image", FailureTranslator.ApiErrorMessage(
            """{"type":"error","error":{"type":"invalid_request_error","message":"Could not process image"}}"""));
        Assert.Equal("", FailureTranslator.ApiErrorMessage("a plain message with no json"));
        Assert.Equal("", FailureTranslator.ApiErrorMessage(""));
    }

    // ── The status-code false positive ──────────────────────────────────────

    // The defect this guards: a bare Contains("429") would read eBay's own rejection text — which
    // routinely quotes prices — as a rate limit, and tell a seller to wait a few seconds when what
    // they actually need to do is change the listing.
    [Theory]
    [InlineData("Item price 429.00 exceeds the maximum allowed for this category")]
    [InlineData("Quantity 500 with price 529.99 is invalid")]
    [InlineData("The item specific Model is missing. Listing 401 of 500 failed.")]
    public void A_price_that_happens_to_look_like_a_status_code_is_not_a_status_code(string message)
    {
        var failure = FailureTranslator.Translate(new Exception(message), FailureDomain.Ebay);

        Assert.Equal(FailureKind.EbayRejected, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Theory]
    [InlineData("HTTP 429", 429, true)]
    [InlineData("status 529", 529, true)]
    [InlineData("responded 503", 503, true)]
    [InlineData("(401)", 401, true)]
    [InlineData("429 Too Many Requests", 429, true)]
    [InlineData("price is 429.00", 429, false)]
    [InlineData("sold 429 units", 429, false)]
    public void MentionsHttpStatus_requires_the_number_to_be_used_as_a_status(string message, int code, bool expected)
        => Assert.Equal(expected, FailureTranslator.MentionsHttpStatus(message, code));

    // ── eBay ────────────────────────────────────────────────────────────────

    // eBay looked at this listing and said no. Retrying identical content gets the identical
    // refusal, so offering a Retry button just wastes the seller's time twice.
    [Fact]
    public void An_eBay_rejection_is_not_retryable()
    {
        var failure = FailureTranslator.Translate(
            new Exception("AddFixedPriceItem failed: The item specific Model is missing."), FailureDomain.Ebay);

        Assert.Equal(FailureKind.EbayRejected, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("Model is missing", failure.WhatHappened);
    }

    [Fact]
    public void The_eBay_call_name_and_status_are_stripped_from_the_sentence_shown_to_the_seller()
    {
        var sentence = FailureTranslator.EbaySentence(
            "AddFixedPriceItem failed (HTTP 500): The auction has been closed.");

        Assert.Equal("The auction has been closed.", sentence);
    }

    // A raw XML body is not a sentence. Pasting it at the seller is how a fixable problem reads as
    // a crash; the untouched original is still carried in Technical.
    [Fact]
    public void A_raw_error_document_with_no_sentence_in_it_is_described_rather_than_pasted()
    {
        var sentence = FailureTranslator.EbaySentence(
            "AddFixedPriceItem failed (HTTP 500): <?xml version=\"1.0\"?><root><Errors/></root>");

        Assert.DoesNotContain("<", sentence);
        Assert.Contains("raw error document", sentence);
    }

    // Caught live against the real eBay API. The prefix strip only matched a single word, so
    // "Inventory item failed (HTTP 400): {...}" kept its prefix — which meant the JSON check never
    // fired and the seller was shown the entire payload as the explanation.
    [Fact]
    public void A_multi_word_eBay_call_name_is_stripped_too()
    {
        var sentence = FailureTranslator.EbaySentence(
            "Inventory item failed (HTTP 400): {\"errors\":[{\"errorId\":2004,\"message\":\"Invalid request\","
          + "\"longMessage\":\"The request has errors. For help, see the documentation for this API.\"}]}");

        Assert.Equal("The request has errors. For help, see the documentation for this API.", sentence);
    }

    // eBay writes two sentences per error: `message` is a terse label, `longMessage` is the one meant
    // for a person. Prefer the readable one.
    [Fact]
    public void EBays_own_sentence_is_preferred_over_its_terse_label()
    {
        var sentence = FailureTranslator.EbaySentence(
            "Offer failed (HTTP 400): {\"errors\":[{\"message\":\"Invalid request\",\"longMessage\":\"The item "
          + "specific Model is missing.\"}]}");

        Assert.Equal("The item specific Model is missing.", sentence);
    }

    [Fact]
    public void The_terse_label_is_used_when_there_is_no_long_message()
    {
        var sentence = FailureTranslator.EbaySentence(
            "Offer failed (HTTP 400): {\"errors\":[{\"message\":\"Category 175673 is not a leaf category\"}]}");

        Assert.Equal("Category 175673 is not a leaf category", sentence);
    }

    [Fact]
    public void A_trading_api_xml_rejection_yields_its_LongMessage()
    {
        var sentence = FailureTranslator.EbaySentence(
            "AddFixedPriceItem failed: <Errors><ShortMessage>Bad</ShortMessage>"
          + "<LongMessage>The auction has been closed.</LongMessage></Errors>");

        Assert.Equal("The auction has been closed.", sentence);
    }

    [Fact]
    public void A_raw_document_is_still_kept_verbatim_as_evidence()
    {
        var failure = FailureTranslator.Translate(
            new Exception("AddFixedPriceItem failed (HTTP 500): <?xml version=\"1.0\"?><Errors/>"),
            FailureDomain.Ebay);

        Assert.Contains("<?xml", failure.Technical);
    }

    [Fact]
    public void Missing_business_policies_point_at_the_policies_screen()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException("eBay Seller Policies not configured: Payment Policy ID."),
            FailureDomain.Ebay);

        Assert.Equal(FailureKind.EbayPoliciesMissing, failure.Kind);
        Assert.Equal("ebay-policies", failure.FixAction);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void An_expired_eBay_signin_offers_the_login_button_not_a_retry()
    {
        var failure = FailureTranslator.Translate(
            new Exception("invalid_grant: the refresh token is expired"), FailureDomain.Ebay);

        Assert.Equal(FailureKind.EbayAuthExpired, failure.Kind);
        Assert.Equal("connect-ebay", failure.FixAction);
        Assert.False(failure.Retryable);
    }

    // A timeout on a publish is the one failure that may be reporting the opposite of the truth, so
    // it must not offer a plain retry — a retry is exactly what creates the duplicate listing.
    [Fact]
    public void A_publish_timeout_does_not_offer_a_retry_and_says_to_check_eBay_first()
    {
        var failure = FailureTranslator.Translate(new TaskCanceledException("A task was canceled."), FailureDomain.Ebay);

        Assert.Equal(FailureKind.Timeout, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("Check eBay", failure.WhatToDo);
        Assert.Contains("twice", failure.WhatToDo);
    }

    // The same timeout on the AI path has no such hazard: nothing was created, so retrying is free.
    [Fact]
    public void An_AI_timeout_does_offer_a_retry()
    {
        var failure = FailureTranslator.Translate(new TaskCanceledException("A task was canceled."), FailureDomain.Ai);

        Assert.Equal(FailureKind.Timeout, failure.Kind);
        Assert.True(failure.Retryable);
    }

    // ── Photos ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_truncated_image_paste_is_reported_as_a_bad_image_not_an_app_crash()
    {
        var failure = FailureTranslator.Translate(
            new FormatException("The input is not a valid Base-64 string"), FailureDomain.Photos);

        Assert.Equal(FailureKind.BadInput, failure.Kind);
        Assert.Contains("image", failure.Headline, StringComparison.OrdinalIgnoreCase);
    }

    // Background removal is optional. A seller whose machine has no Python must be told their photo
    // is usable as it is, not left believing the listing is broken.
    [Fact]
    public void Missing_Python_says_the_photo_is_still_fine()
    {
        var failure = FailureTranslator.Translate(
            new Exception("rembg failed (exit 1): No module named 'rembg'"), FailureDomain.Photos);

        Assert.Equal(FailureKind.ToolMissing, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("usable", failure.WhatToDo);
    }

    [Fact]
    public void A_hotlink_blocked_image_suggests_copy_and_paste_instead()
    {
        var failure = FailureTranslator.Translate(new Exception("Response status code 403 Forbidden"), FailureDomain.Photos);

        Assert.Equal(FailureKind.NotFound, failure.Kind);
        Assert.Contains("paste", failure.WhatToDo, StringComparison.OrdinalIgnoreCase);
    }

    // ── Storage ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_locked_database_is_retryable()
    {
        var failure = FailureTranslator.Translate(
            new Exception("SQLite Error 5: 'database is locked'."), FailureDomain.Storage);

        Assert.Equal(FailureKind.StorageBusy, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void A_full_disk_is_not_retryable()
    {
        var failure = FailureTranslator.Translate(
            new IOException("There is not enough space on the disk."), FailureDomain.Photos);

        Assert.Equal(FailureKind.DiskFull, failure.Kind);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void A_sqlite_busy_code_is_recognised_even_when_the_message_says_nothing_useful()
    {
        // SqliteErrorCode 5 is SQLITE_BUSY. Constructed with an unhelpful message on purpose: the
        // classification has to come from the code, not from the wording.
        var failure = FailureTranslator.Translate(new SqliteException("Error", 5), FailureDomain.Storage);

        Assert.Equal(FailureKind.StorageBusy, failure.Kind);
        Assert.True(failure.Retryable);
    }

    // ── Structure ───────────────────────────────────────────────────────────

    [Fact]
    public void An_already_classified_failure_passes_through_untouched()
    {
        var original = new FailureInfo { Kind = FailureKind.EbayRejected, Headline = "eBay refused this listing" };

        var failure = FailureTranslator.Translate(new AppFailureException(original), FailureDomain.Ai, attempts: 3);

        Assert.Equal(FailureKind.EbayRejected, failure.Kind);
        Assert.Equal("eBay refused this listing", failure.Headline);
        Assert.Equal(3, failure.Attempts);
    }

    // A faulted task hands back an AggregateException whose own message is "One or more errors
    // occurred" — useless. The real cause has to be dug out or every async failure reads the same.
    [Fact]
    public void An_aggregate_exception_is_unwrapped_to_the_cause()
    {
        var failure = FailureTranslator.Translate(
            new AggregateException(new Exception("overloaded_error")), FailureDomain.Ai);

        Assert.Equal(FailureKind.Overloaded, failure.Kind);
    }

    [Fact]
    public void Every_failure_says_what_to_do_about_it()
    {
        Exception[] cases =
        [
            new Exception("overloaded_error"),
            new Exception("authentication_error"),
            new Exception("something nobody has ever seen"),
            new TaskCanceledException(),
            new IOException("not enough space"),
            new FormatException("not a valid Base-64 string"),
        ];

        foreach (var domain in Enum.GetValues<FailureDomain>())
            foreach (var ex in cases)
            {
                var failure = FailureTranslator.Translate(ex, domain);
                Assert.False(string.IsNullOrWhiteSpace(failure.Headline));
                Assert.False(string.IsNullOrWhiteSpace(failure.WhatToDo));
            }
    }

    [Fact]
    public void An_unrecognised_error_keeps_its_raw_text_as_evidence()
    {
        var failure = FailureTranslator.Translate(new Exception("WIDGET_FAULT_0x51"), FailureDomain.App);

        Assert.Equal(FailureKind.Unknown, failure.Kind);
        Assert.Contains("WIDGET_FAULT_0x51", failure.Technical);
        Assert.Equal("open-logs", failure.FixAction);
    }

    [Fact]
    public void A_very_long_error_is_truncated_rather_than_pasted_whole()
    {
        var failure = FailureTranslator.Translate(new Exception(new string('x', 5000)), FailureDomain.App);

        Assert.True(failure.Technical.Length < 700);
    }

    // ── Retry-After ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("rate limited, retry-after: 30", 30)]
    [InlineData("Retry After 5 seconds", 5)]
    [InlineData("retry_after=120", 120)]
    public void A_server_specified_wait_is_read_back(string message, int expected)
        => Assert.Equal(expected, FailureTranslator.RetryAfterFrom(message));

    [Theory]
    [InlineData("no wait mentioned here")]
    [InlineData("retry-after: 99999")]   // implausible — treated as absent rather than obeyed
    [InlineData("retry-after: 0")]
    public void An_absent_or_implausible_wait_is_ignored(string message)
        => Assert.Null(FailureTranslator.RetryAfterFrom(message));

    [Fact]
    public void A_rate_limit_carries_its_requested_wait_through_to_the_failure()
    {
        var failure = FailureTranslator.Translate(
            new Exception("HTTP 429 rate_limit_error, retry-after: 12"), FailureDomain.Ai);

        Assert.Equal(12, failure.RetryAfterSeconds);
    }

    [Fact]
    public void IsTransient_agrees_with_what_the_failures_advertise()
    {
        Assert.True(FailureTranslator.IsTransient(FailureKind.Overloaded));
        Assert.True(FailureTranslator.IsTransient(FailureKind.RateLimited));
        Assert.True(FailureTranslator.IsTransient(FailureKind.AiUnreadableReply));
        Assert.False(FailureTranslator.IsTransient(FailureKind.AiKeyRejected));
        Assert.False(FailureTranslator.IsTransient(FailureKind.EbayRejected));
        Assert.False(FailureTranslator.IsTransient(FailureKind.DiskFull));
    }
}
