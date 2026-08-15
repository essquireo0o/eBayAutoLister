using System.Text;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── The network half ──────────────────────────────────────────────────────────────────────────
//
// Thin on purpose. Every judgement — what to send, whether to send it, what the answer means — was
// made next door in AmazonListingSubmission, where it can be proven without a network. What is left
// here is the part that cannot be: making the request, and recording exactly what was sent and what
// came back so that a person can check the judgement against the evidence.
//
// That recording is not diagnostics dressing. This deployment cannot obtain an Amazon access token,
// so the acceptance evidence for the phase IS the exchange, and an exchange nobody kept is a claim.

/// <summary>
/// Puts listings to Amazon's Listings Items API, and asks afterwards what became of them.
/// </summary>
/// <remarks>
/// Answers rather than throws, the same as <see cref="AmazonProductTypeService"/>. A missing
/// credential, a refused submission and an unreachable Amazon are three different next actions, and
/// all three are more useful as a state and a sentence than as an exception.
/// </remarks>
public sealed class AmazonListingSubmitService(
    AmazonService amazon,
    AmazonProductTypeService productTypes,
    ActionLog log)
{
    /// <summary>Content type for a Listings Items body.</summary>
    private const string Json = "application/json";

    // ── (a) An offer on a product Amazon already has ──────────────────────────

    /// <summary>
    /// Offers an existing ASIN at a price, in a condition, with a quantity.
    /// </summary>
    /// <remarks>
    /// The common case and the safe one. It cannot create a catalogue entry, cannot duplicate a
    /// product, and touches nothing except the seller's own offer — which is why it is the path this
    /// phase does first, and the one a seller should reach for unless the product genuinely is not on
    /// Amazon yet.
    /// </remarks>
    public async Task<AmazonSubmission> SubmitOfferAsync(
        AmazonOfferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Refuse(AmazonSubmitGuard.Check(amazon.Options)) is { } refused) return refused;
        if (Refuse(AmazonOfferCheck.Check(request), AmazonSubmissionState.Blocked) is { } blocked) return blocked;

        var body = AmazonOfferPayload.BuildOffer(request, amazon.Options.MarketplaceId);

        return await PutAsync(request.Sku.Trim(), body, AmazonOfferPayload.GenericProductType, cancellationToken);
    }

    // ── (b) A product Amazon does not have yet ────────────────────────────────

    /// <summary>
    /// Creates a new catalogue product from the draft the AI wrote, plus the seller's own stock facts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Runs Phase 3 first and <b>will not send a fill that Phase 3 says cannot go</b>. That check is
    /// the whole safeguard on this path: <c>AmazonListingFill.CanSubmit</c> is false whenever a
    /// required attribute has no value, a value falls outside a closed list, or an either/or
    /// requirement is unmet — and submitting anyway would ask Amazon to adjudicate something this app
    /// already knows the answer to, at the cost of a rejection against the seller's account.
    /// </para>
    /// <para>
    /// Second for a reason. An offer on an existing ASIN is reversible and additive; a new catalogue
    /// entry that duplicates a product Amazon already lists is a merge case, a suppressed listing and
    /// a support ticket.
    /// </para>
    /// </remarks>
    public async Task<AmazonSubmission> SubmitProductAsync(
        AmazonProductSubmitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (Refuse(AmazonSubmitGuard.Check(amazon.Options)) is { } refused) return refused;

        var sku = (request.Sku ?? "").Trim();
        if (sku.Length == 0 || sku.Length > AmazonOfferCheck.MaxSkuLength)
            return Refuse(new AmazonConfigProblem("no_sku",
                sku.Length == 0
                    ? "No SKU was given, so there is nowhere to put the listing."
                    : $"The SKU is {sku.Length} characters and Amazon's limit is {AmazonOfferCheck.MaxSkuLength}.",
                "Choose a SKU you are not already using. This app will not invent one — a submission replaces " +
                "whatever is at that SKU."), AmazonSubmissionState.Blocked)!;

        var fill = await AmazonListingFillEndpoints.BuildAsync(
            request.Draft ?? new AmazonFillRequest(), productTypes, amazon, cancellationToken);

        if (!fill.CanSubmit)
        {
            var submission = Blank(AmazonSubmissionState.Blocked, sku);
            submission.ProductType = fill.ProductType;
            submission.Headline    = "Not submitted. " + fill.Headline;
            submission.NextAction  = "Answer what is missing and try again. Nothing was invented to close the gap, " +
                                     "and nothing was sent — a submission Amazon would reject costs the same round " +
                                     "trip as one it accepts and leaves an error against the account.";
            return submission;
        }

        var body = AmazonOfferPayload.BuildProduct(fill, request, amazon.Options.MarketplaceId);
        return await PutAsync(sku, body, fill.ProductType, cancellationToken);
    }

    // ── The submission itself ─────────────────────────────────────────────────

    private async Task<AmazonSubmission> PutAsync(
        string sku, System.Text.Json.Nodes.JsonObject body, string productType, CancellationToken cancellationToken)
    {
        var options = amazon.Options;
        var path    = AmazonListingsApi.ItemPath(options.SellerId, sku, options.MarketplaceId);
        var json    = AmazonOfferPayload.ToJson(body);

        var call = new AmazonCall
        {
            Method      = "PUT",
            Url         = AmazonListingsApi.Redact(options.BaseUrl + path, options.SellerId),
            RequestBody = json,
        };

        try
        {
            using var content  = new StringContent(json, Encoding.UTF8, Json);
            using var response = await amazon.SendAsync(HttpMethod.Put, path, content, cancellationToken);

            call.HttpStatus   = (int)response.StatusCode;
            call.ResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            call.RequestId    = RequestId(response);

            var submission = AmazonSubmissionResponse.Parse(call.ResponseBody, call.HttpStatus.Value, sku);
            Finish(submission, call, productType);

            // The state, not the HTTP code — a rejection arrives inside a 200 and would otherwise be
            // logged as a success. See AmazonSubmissionResponse.
            log.Add(submission.State == AmazonSubmissionState.Submitted ? "Info" : "Warning",
                $"Amazon listing submission {submission.State}",
                $"SKU {sku}; {options.Environment}; HTTP {call.HttpStatus}; " +
                $"Amazon status: {Or(submission.AmazonStatus, "none")}; " +
                $"{submission.Errors.Count()} errors, {submission.Warnings.Count()} warnings" +
                (string.IsNullOrWhiteSpace(call.RequestId) ? "" : $"; RequestId {call.RequestId}"));

            return submission;
        }
        catch (AmazonTokenException ex)
        {
            // No token, so no request was made. Nothing on Amazon changed, and saying so plainly
            // matters more here than anywhere else in the app — this is the write path.
            var submission = Blank(AmazonSubmissionState.NotConfigured, sku);
            submission.ProductType = productType;
            submission.Headline    = "Not submitted — " + ex.Message;
            submission.NextAction  = ex.InvalidGrant
                ? "Re-authorise the application against the seller account and set Credentials__AmazonRefreshToken."
                : "Set the Amazon credentials this deployment is missing, then try again. Nothing was sent.";
            return submission;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Add("Warning", "Amazon listing submission could not reach SP-API", Shorten(ex.Message));

            var submission = Blank(AmazonSubmissionState.Error, sku);
            submission.ProductType = productType;
            submission.Call        = call;
            submission.Headline    = "Amazon could not be reached, so whether this submission arrived is unknown.";
            submission.NextAction  = $"Ask what became of the SKU before submitting again: " +
                                     $"GET {AmazonSubmitEndpoints.StatePath}?sku={Uri.EscapeDataString(sku)}. " +
                                     "A request that timed out may still have been processed.";
            return submission;
        }
    }

    // ── What became of it ─────────────────────────────────────────────────────

    /// <summary>
    /// Asks Amazon what it did with a SKU — the only call that can answer "did it work?".
    /// </summary>
    /// <remarks>
    /// A 404 here is information rather than a failure, and is reported as <c>not_found</c>: a
    /// submission Amazon rejected outright leaves no listing behind, so an absent SKU is what a
    /// rejection looks like once the submission response has been thrown away.
    /// </remarks>
    public async Task<AmazonListingState> GetStateAsync(string sku, CancellationToken cancellationToken = default)
    {
        var options = amazon.Options;
        sku = (sku ?? "").Trim();

        if (options.CallProblem is { } problem)
            return new AmazonListingState
            {
                Status = AmazonDefinitionStatus.NotConfigured, Sku = sku,
                Message = problem.Reason + " " + problem.NextAction,
                Headline = "This deployment cannot ask Amazon about this SKU.",
            };

        if (sku.Length == 0)
            return new AmazonListingState
            {
                Status = "error", Message = "No SKU was given, so there is nothing to ask about.",
                Headline = "No SKU was given, so there is nothing to ask about.",
            };

        var path = AmazonListingsApi.StatePath(options.SellerId, sku, options.MarketplaceId);
        var call = new AmazonCall
        {
            Method = "GET",
            Url    = AmazonListingsApi.Redact(options.BaseUrl + path, options.SellerId),
        };

        try
        {
            using var response = await amazon.SendAsync(HttpMethod.Get, path, cancellationToken: cancellationToken);

            call.HttpStatus   = (int)response.StatusCode;
            call.ResponseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            call.RequestId    = RequestId(response);

            if (call.HttpStatus == 404)
            {
                var missing = new AmazonListingState { Status = "not_found", Sku = sku, Call = call };
                missing.Headline = AmazonSubmissionWords.Describe(missing);
                missing.Message  = missing.Headline;
                return missing;
            }

            if (!response.IsSuccessStatusCode)
            {
                var failed = new AmazonListingState
                {
                    Status = "error", Sku = sku, Call = call,
                    Message = AmazonProductTypeService.DescribeFailure(
                        call.ResponseBody, call.HttpStatus.Value, $"the listing at SKU {sku}"),
                };
                failed.Headline = failed.Message;
                log.Add("Warning", $"Amazon listing state HTTP {call.HttpStatus}", $"SKU {sku}");
                return failed;
            }

            var state = AmazonListingStateResponse.Parse(call.ResponseBody, sku);
            state.Call     = call;
            state.Headline = AmazonSubmissionWords.Describe(state);

            log.Add(state.HasErrors ? "Warning" : "Info", "Amazon listing state read",
                $"SKU {sku}; status: {(state.Statuses.Count == 0 ? "none yet" : string.Join(",", state.Statuses))}; " +
                $"{state.Errors.Count()} errors, {state.Warnings.Count()} warnings");

            return state;
        }
        catch (AmazonTokenException ex)
        {
            return new AmazonListingState
            {
                Status = AmazonDefinitionStatus.NotConfigured, Sku = sku,
                Message = ex.Message, Headline = ex.Message,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Add("Warning", "Amazon listing state could not reach SP-API", Shorten(ex.Message));
            return new AmazonListingState
            {
                Status = "error", Sku = sku, Call = call,
                Message = "Amazon could not be reached, so what became of this SKU is unknown.",
                Headline = "Amazon could not be reached, so what became of this SKU is unknown.",
            };
        }
    }

    // ── Small shared pieces ───────────────────────────────────────────────────

    private void Finish(AmazonSubmission submission, AmazonCall call, string productType)
    {
        submission.Call          = call;
        submission.ProductType   = productType;
        submission.Environment   = amazon.Options.Environment.ToString();
        submission.MarketplaceId = amazon.Options.MarketplaceId;
        submission.Headline      = AmazonSubmissionWords.Describe(submission);
        submission.NextAction    = AmazonSubmissionWords.NextAction(submission, submission.Sku);
    }

    /// <summary>A refusal turned into a submission that names why nothing was sent.</summary>
    private AmazonSubmission? Refuse(AmazonConfigProblem? problem, string state = AmazonSubmissionState.NotConfigured)
    {
        if (problem is null) return null;

        var submission = Blank(state, "");
        submission.Headline   = problem.Reason;
        submission.NextAction = problem.NextAction;
        return submission;
    }

    private AmazonSubmission Blank(string state, string sku) => new()
    {
        State         = state,
        Sku           = sku,
        Environment   = amazon.Options.Environment.ToString(),
        MarketplaceId = amazon.Options.MarketplaceId,
    };

    /// <summary>Amazon's request ID. The one thing Selling Partner Support will act on.</summary>
    private static string RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-amzn-RequestId", out var values)
            ? values.FirstOrDefault() ?? ""
            : "";

    private static string Or(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static string Shorten(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
