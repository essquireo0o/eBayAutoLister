using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// <c>/api/amazon/listing-fill</c> — an eBay draft, arranged the way Amazon wants it.
/// </summary>
/// <remarks>
/// <para>
/// Takes the same body the eBay listing endpoints take, so the thing being sent is literally the
/// draft the AI produced rather than a re-typed copy of it. The product type may be named outright;
/// where it is not, the draft's own title is what Amazon is searched with, which is the question a
/// seller is actually asking ("put THIS on Amazon") rather than its parts.
/// </para>
/// <para>
/// <b>It submits nothing.</b> The answer is a payload and a verdict; publishing is a later phase and
/// a different consent. A caller that wants the same thing to read rather than to parse asks for
/// <see cref="ReportPath"/>.
/// </para>
/// </remarks>
public static class AmazonListingFillEndpoints
{
    public const string FillPath   = "/api/amazon/listing-fill";
    public const string ReportPath = "/api/amazon/listing-fill/report";

    public static void Map(WebApplication app)
    {
        app.MapPost(FillPath, async (
            AmazonFillRequest request, AmazonProductTypeService productTypes,
            AmazonService amazon, CancellationToken cancellationToken) =>
        {
            var fill = await BuildAsync(request, productTypes, amazon, cancellationToken);
            return Results.Ok(Describe(fill));
        });

        app.MapPost(ReportPath, async (
            AmazonFillRequest request, AmazonProductTypeService productTypes,
            AmazonService amazon, CancellationToken cancellationToken) =>
        {
            var fill = await BuildAsync(request, productTypes, amazon, cancellationToken);
            return Results.Text(AmazonListingFillReport.Describe(fill), "text/plain");
        });
    }

    /// <summary>
    /// Finds the product type, reads its schema, and fills it from the draft.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two ways in differ in who chose the product type. A named one is taken as given — the
    /// seller picked it from the candidates and second-guessing that would be this app overruling
    /// them. An unnamed one goes through <see cref="AmazonProductTypeChooser"/>, which refuses when
    /// the words do not decide it, and the refusal is passed straight through as the status.
    /// </para>
    /// <para>
    /// Public because <see cref="AmazonListingSubmitService.SubmitProductAsync"/> calls it before
    /// submitting, and it has to be the SAME fill the seller reviewed here. A second implementation
    /// that agreed today is a second implementation that can disagree later, and the disagreement
    /// would be between what a report showed and what was actually sent to Amazon.
    /// </para>
    /// </remarks>
    public static async Task<AmazonListingFill> BuildAsync(
        AmazonFillRequest request, AmazonProductTypeService productTypes,
        AmazonService amazon, CancellationToken cancellationToken)
    {
        var marketplaceId = amazon.Options.MarketplaceId;

        if (!string.IsNullOrWhiteSpace(request.ProductType))
        {
            var named = await productTypes.GetDefinitionAsync(
                request.ProductType.Trim(), AmazonDefinitionsApi.RequirementsListing,
                AmazonDefinitionsApi.DefaultLocale, cancellationToken);

            return AmazonListingMapper.Map(request, named, marketplaceId);
        }

        var query = string.IsNullOrWhiteSpace(request.Query) ? request.Title ?? "" : request.Query;
        var answer = await productTypes.DescribeAsync(
            query, AmazonDefinitionsApi.RequirementsListing, cancellationToken);

        // No product type means no schema, and no schema means there is nothing to be missing from.
        // That is the search's answer, not a fill failure, so it is reported as the search phrased it.
        if (answer.Definition is null)
            return new AmazonListingFill
            {
                Status        = answer.Search.Status,
                Message       = answer.Search.Message,
                SourceTitle   = (request.Title ?? "").Trim(),
                MarketplaceId = marketplaceId,
                SandboxNotice = answer.SandboxNotice,
                Headline = answer.Search.Status == AmazonDefinitionStatus.Ambiguous
                    ? $"Amazon offered {answer.Search.Candidates.Count} product types for \"{query}\" and " +
                      "none is clearly the one. Name the product type and this will fill against it."
                    : $"No Amazon product type was chosen for \"{query}\", so there is nothing to fill in yet.",
            };

        return AmazonListingMapper.Map(request, answer.Definition, marketplaceId, answer.SandboxNotice);
    }

    private static object Describe(AmazonListingFill fill) => new
    {
        status        = fill.Status,
        message       = fill.Message,
        headline      = fill.Headline,
        canSubmit     = fill.CanSubmit,
        sandboxNotice = fill.SandboxNotice,
        sourceTitle   = fill.SourceTitle,
        productType   = fill.ProductType,
        displayName   = fill.DisplayName,
        marketplaceId = fill.MarketplaceId,
        locale        = fill.Locale,
        version       = fill.Version,
        counts = new
        {
            required        = fill.RequiredCount,
            requiredFilled  = fill.RequiredFilledCount,
            blocking        = fill.Blocking.Count(),
            filled          = fill.Filled.Count(),
            total           = fill.Attributes.Count,
        },
        choices = fill.Choices.Select(c => new
        {
            options     = c.Options,
            satisfied   = c.Satisfied,
            satisfiedBy = c.SatisfiedBy,
            note        = c.Note,
        }),
        required = fill.Attributes.Where(a => a.Required).Select(Describe),
        conditional = fill.Attributes.Where(a => a.ConditionallyRequired && !a.Required).Select(Describe),
        optional = fill.Attributes.Where(a => !a.Required && !a.ConditionallyRequired).Select(Describe),
        // The payload last: it is the largest thing here and the one a person reads least.
        payload = fill.Payload,
    };

    private static object Describe(AmazonFilledAttribute attribute) => new
    {
        name                  = attribute.Name,
        title                 = attribute.Title,
        state                 = attribute.State,
        required              = attribute.Required,
        conditionallyRequired = attribute.ConditionallyRequired,
        requirementNote       = attribute.RequirementNote,
        source                = attribute.Source,
        note                  = attribute.Note,
        values                = attribute.Values,
        payload               = attribute.Payload,
    };
}
