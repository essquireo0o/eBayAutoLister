using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// <c>/api/amazon/product-types</c> — what Amazon requires for a product, before anything is written.
/// </summary>
/// <remarks>
/// <para>
/// Mapped from its own file for the reason <see cref="AmazonStatusEndpoint"/> is: the handler the
/// tests exercise has to be the handler the app serves.
/// </para>
/// <para>
/// Both routes answer 200 with a status field rather than throwing an HTTP error at a caller.
/// "Amazon requires nothing", "we could not ask Amazon" and "this deployment has no Amazon
/// credentials" are three different answers needing three different next actions, and an HTTP 500
/// flattens all of them into one — the same reasoning as <c>CategoryAspectsResult.Status</c>.
/// </para>
/// <para>
/// <b>What it never carries:</b> any of the five Amazon credentials, the access token, or the
/// pre-signed schema URL — that last one is a capability, not a description, and anyone holding it
/// can fetch the document without a token until it expires.
/// </para>
/// </remarks>
public static class AmazonProductTypeEndpoints
{
    public const string SearchPath     = "/api/amazon/product-types";
    public const string DefinitionPath = "/api/amazon/product-types/{productType}";

    /// <summary>Plain-text rendering of the same answer, for reading rather than parsing.</summary>
    public const string ReportPath = "/api/amazon/product-types/{productType}/report";

    public static void Map(WebApplication app)
    {
        // Search, choose, and describe the chosen product type in one call — the question a seller
        // actually has ("what does Amazon need for a bluetooth speaker?") rather than its three
        // constituent requests.
        app.MapGet(SearchPath, async (
            string? query, string? requirements,
            AmazonProductTypeService productTypes, CancellationToken cancellationToken) =>
        {
            var answer = await productTypes.DescribeAsync(
                query ?? "", Requirements(requirements), cancellationToken);

            return Results.Ok(new
            {
                query         = answer.Search.Query,
                status        = answer.Search.Status,
                message       = answer.Search.Message,
                sandboxNotice = answer.SandboxNotice,
                candidates    = answer.Search.Candidates.Select(c => new
                {
                    name        = c.Name,
                    displayName = c.Label,
                }),
                chosen = answer.Search.Chosen is null ? null : new
                {
                    name        = answer.Search.Chosen.ProductType.Name,
                    displayName = answer.Search.Chosen.ProductType.Label,
                    confidence  = answer.Search.Chosen.Confidence,
                    why         = answer.Search.Chosen.Why,
                },
                definition = answer.Definition is null ? null : Describe(answer.Definition),
            });
        });

        // One named product type, for when the seller has picked from the candidates themselves.
        app.MapGet(DefinitionPath, async (
            string productType, string? requirements,
            AmazonProductTypeService productTypes, CancellationToken cancellationToken) =>
        {
            var definition = await productTypes.GetDefinitionAsync(
                productType, Requirements(requirements), AmazonDefinitionsApi.DefaultLocale, cancellationToken);

            return Results.Ok(Describe(definition));
        });

        // The same thing as text. Exists because the required-attribute list is something an
        // operator reads to decide whether this works at all, and reading it as JSON is a chore.
        app.MapGet(ReportPath, async (
            string productType, string? requirements, int? optional,
            AmazonProductTypeService productTypes, CancellationToken cancellationToken) =>
        {
            var definition = await productTypes.GetDefinitionAsync(
                productType, Requirements(requirements), AmazonDefinitionsApi.DefaultLocale, cancellationToken);

            var text = definition.Status is AmazonDefinitionStatus.Ok or AmazonDefinitionStatus.Stale
                ? AmazonAttributeReport.Describe(definition, optional ?? 0)
                : $"{definition.Status}: {definition.Message}";

            return Results.Text(text, "text/plain");
        });
    }

    /// <summary>
    /// Which half of a listing to describe. Anything unrecognised is the whole listing — the
    /// superset, so a typo asks for more than is needed rather than quietly hiding a requirement.
    /// </summary>
    private static string Requirements(string? requested) => (requested ?? "").ToUpperInvariant() switch
    {
        AmazonDefinitionsApi.RequirementsProductOnly => AmazonDefinitionsApi.RequirementsProductOnly,
        AmazonDefinitionsApi.RequirementsOfferOnly   => AmazonDefinitionsApi.RequirementsOfferOnly,
        _                                            => AmazonDefinitionsApi.RequirementsListing,
    };

    private static object Describe(AmazonProductTypeDefinition definition) => new
    {
        status               = definition.Status,
        message              = definition.Message,
        productType          = definition.ProductType,
        displayName          = definition.DisplayName,
        version              = definition.Version,
        locale               = definition.Locale,
        marketplaceId        = definition.MarketplaceId,
        requirements         = definition.Requirements,
        requirementsEnforced = definition.RequirementsEnforced,
        fromCache            = definition.FromCache,
        // The counts first: this is the number an operator wants and the whole point of the
        // required/optional split. 9 of 174 is a different job from 174.
        counts = new
        {
            total                 = definition.Attributes.Count,
            required              = definition.RequiredAttributes.Count(),
            conditionallyRequired = definition.ConditionallyRequiredAttributes.Count(),
            optional              = definition.OptionalAttributes.Count(),
        },
        required              = definition.RequiredAttributes.Select(Describe),
        conditionallyRequired = definition.ConditionallyRequiredAttributes.Select(Describe),
        optional              = definition.OptionalAttributes.Select(Describe),
    };

    private static object Describe(AmazonAttribute attribute) => new
    {
        name            = attribute.Name,
        title           = attribute.Title,
        description     = attribute.Description,
        type            = attribute.Type,
        rawType         = attribute.RawType,
        typeDescription = attribute.TypeDescription,
        required        = attribute.Required,
        conditionallyRequired = attribute.ConditionallyRequired,
        requirementNote = attribute.RequirementNote,
        selectionOnly   = attribute.SelectionOnly,
        multiSelect     = attribute.MultiSelect,
        maxLength       = attribute.MaxLength,
        editable        = attribute.Editable,
        hidden          = attribute.Hidden,
        group           = attribute.Group,
        values          = attribute.Values,
        valueLabels     = attribute.ValueLabels,
        examples        = attribute.Examples,
        children        = attribute.Children.Select(Describe),
    };
}
