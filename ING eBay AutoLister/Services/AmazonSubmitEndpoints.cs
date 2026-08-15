using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// <c>/api/amazon/offer</c>, <c>/api/amazon/product</c>, <c>/api/amazon/listing-state</c> — the
/// three routes of Phase 4: send an offer, send a product, and ask what became of either.
/// </summary>
/// <remarks>
/// <para>
/// The third route is not an afterthought and is the reason the other two can be honest. Amazon
/// answers a submission before it has judged it, so an app with only the first two routes has no way
/// to ever learn that a listing it called successful was refused ten minutes later — and would have
/// to either say "published" and be wrong, or say nothing and be useless.
/// </para>
/// <para>
/// Every route answers 200 with a state rather than an HTTP error, the same as the fill endpoints. A
/// refused submission, an unconfigured deployment and a rejection by Amazon need three different
/// things done about them, and an HTTP status flattens all three into the same red box.
/// </para>
/// </remarks>
public static class AmazonSubmitEndpoints
{
    public const string OfferPath       = "/api/amazon/offer";
    public const string OfferReportPath = "/api/amazon/offer/report";
    public const string ProductPath     = "/api/amazon/product";
    public const string StatePath       = "/api/amazon/listing-state";
    public const string StateReportPath = "/api/amazon/listing-state/report";

    public static void Map(WebApplication app)
    {
        // (a) An offer on a product Amazon's catalogue already has. The common case.
        app.MapPost(OfferPath, async (
            AmazonOfferRequest request, AmazonListingSubmitService submit, CancellationToken cancellationToken) =>
            Results.Ok(Describe(await submit.SubmitOfferAsync(request, cancellationToken))));

        app.MapPost(OfferReportPath, async (
            AmazonOfferRequest request, AmazonListingSubmitService submit, CancellationToken cancellationToken) =>
            Results.Text(
                AmazonSubmissionReport.Describe(await submit.SubmitOfferAsync(request, cancellationToken)),
                "text/plain"));

        // (b) A whole new catalogue product, from the draft the AI wrote.
        app.MapPost(ProductPath, async (
            AmazonProductSubmitRequest request, AmazonListingSubmitService submit, CancellationToken cancellationToken) =>
            Results.Ok(Describe(await submit.SubmitProductAsync(request, cancellationToken))));

        // What became of it. The only call that can answer "did it work?".
        app.MapGet(StatePath, async (
            string? sku, AmazonListingSubmitService submit, CancellationToken cancellationToken) =>
            Results.Ok(Describe(await submit.GetStateAsync(sku ?? "", cancellationToken))));

        app.MapGet(StateReportPath, async (
            string? sku, AmazonListingSubmitService submit, CancellationToken cancellationToken) =>
            Results.Text(
                AmazonSubmissionReport.Describe(await submit.GetStateAsync(sku ?? "", cancellationToken)),
                "text/plain"));
    }

    /// <summary>
    /// A submission as JSON.
    /// </summary>
    /// <remarks>
    /// <c>awaitingAmazon</c> is the field a UI binds to, and it is named for what is true rather than
    /// for what a caller wants to know. There is no <c>published</c> or <c>success</c> here to bind to
    /// by mistake — see <see cref="AmazonSubmissionWords"/>.
    /// <para>
    /// Public for the reason <see cref="AmazonListingFillEndpoints.Describe"/> is: no token can be
    /// obtained here, so the screen that renders a submission is photographed against this method's
    /// own output rather than against JSON somebody typed to look plausible.
    /// </para>
    /// </remarks>
    public static object Describe(AmazonSubmission submission) => new
    {
        state          = submission.State,
        amazonStatus   = submission.AmazonStatus,
        awaitingAmazon = submission.AwaitingAmazon,
        headline       = submission.Headline,
        nextAction     = submission.NextAction,
        sku            = submission.Sku,
        submissionId   = submission.SubmissionId,
        environment    = submission.Environment,
        productType    = submission.ProductType,
        marketplaceId  = submission.MarketplaceId,
        counts = new
        {
            issues   = submission.Issues.Count,
            errors   = submission.Errors.Count(),
            warnings = submission.Warnings.Count(),
        },
        issues = submission.Issues.Select(Describe),
        call   = Describe(submission.Call),
    };

    private static object Describe(AmazonListingState state) => new
    {
        status           = state.Status,
        headline         = state.Headline,
        message          = state.Message,
        sku              = state.Sku,
        asin             = state.Asin,
        itemName         = state.ItemName,
        productType      = state.ProductType,
        // Amazon's own words, plural, not folded into a boolean — DISCOVERABLE without BUYABLE is a
        // listing a shopper can find and cannot buy.
        amazonStatuses   = state.Statuses,
        amazonSaysBuyable = state.AmazonSaysBuyable,
        hasErrors        = state.HasErrors,
        counts = new
        {
            issues   = state.Issues.Count,
            errors   = state.Errors.Count(),
            warnings = state.Warnings.Count(),
        },
        issues = state.Issues.Select(Describe),
        call   = Describe(state.Call),
    };

    private static object Describe(AmazonSubmissionIssue issue) => new
    {
        code           = issue.Code,
        severity       = issue.Severity,
        message        = issue.Message,
        attributeNames = issue.AttributeNames,
    };

    /// <summary>The exchange, so a rejection can be checked against what was actually sent.</summary>
    private static object? Describe(AmazonCall? call) => call is null ? null : new
    {
        method       = call.Method,
        url          = call.Url,
        requestBody  = call.RequestBody,
        httpStatus   = call.HttpStatus,
        responseBody = call.ResponseBody,
        requestId    = call.RequestId,
    };
}
