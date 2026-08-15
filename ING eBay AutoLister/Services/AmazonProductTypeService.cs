using System.Net.Http.Headers;
using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// ── Asking Amazon what a product needs ────────────────────────────────────────────────────────
//
// The counterpart of EbayService.GetCategoryAspectsAsync, and the same call in spirit: before the
// seller writes anything, find out what the marketplace will insist on. What differs is that it
// takes three requests instead of one, and the third is not to Amazon.
//
//   1. SEARCH.     /definitions/2020-09-01/productTypes?keywords=…  — which product types exist
//                  for these words. Small. AmazonProductTypeChooser then picks one, or refuses.
//   2. DEFINITION. /definitions/2020-09-01/productTypes/{type}      — the metadata: version,
//                  checksum, property groups, and a LINK to the schema. Still small.
//   3. THE SCHEMA. A pre-signed URL from step 2, fetched WITHOUT the access token. Large.
//
// That third hop is the whole reason the cache is worth having, and the reason it can be keyed by
// version instead of aged by a clock: step 2 is cheap enough to make every time, and it says
// whether the large document already on disk is still the current one.

/// <summary>Everything one lookup found: which product types matched, which was chosen, and its schema.</summary>
public sealed class AmazonProductTypeAnswer
{
    public AmazonProductTypeSearchResult Search { get; set; } = new();

    /// <summary>Null when no product type was chosen — the search result says why.</summary>
    public AmazonProductTypeDefinition? Definition { get; set; }

    /// <summary>
    /// What the sandbox is doing to this answer, when it is doing something. See
    /// <see cref="AmazonSandboxNotice"/> — the sandbox serves static data and will happily answer a
    /// query about speakers with luggage.
    /// </summary>
    public string SandboxNotice { get; set; } = "";
}

/// <summary>
/// Searches Amazon's product types and reads their attribute schemas.
/// </summary>
/// <remarks>
/// Every method answers rather than throws. A missing credential, a refused request and an
/// unreadable schema are all states with a different next action, and all three are more useful as
/// a status and a sentence than as an exception a caller has to translate — which is the same
/// choice <c>CategoryAspectsResult.Status</c> made on the eBay side.
/// </remarks>
public sealed class AmazonProductTypeService(
    AmazonService amazon,
    AmazonSchemaCache cache,
    IHttpClientFactory httpClientFactory,
    ActionLog log)
{
    /// <summary>Ceiling on the pre-signed schema download. Amazon's largest schemas are ~1 MB.</summary>
    private static readonly TimeSpan SchemaFetchTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Most schema this app will read into memory.
    /// </summary>
    /// <remarks>
    /// A pre-signed URL is a redirect target named inside a response. Bounded because "read until
    /// the body ends" is a promise about someone else's server, and the honest failure here — a
    /// schema too large to be one — is a message, not an app that grows until it stops.
    /// </remarks>
    public const int MaxSchemaBytes = 16 * 1024 * 1024;

    public AmazonSchemaCache Cache => cache;

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The product types Amazon offers for a seller's words, and the one this app would use.
    /// </summary>
    public async Task<AmazonProductTypeSearchResult> SearchAsync(
        string query, CancellationToken cancellationToken = default)
    {
        var result = new AmazonProductTypeSearchResult { Query = query ?? "" };

        if (amazon.Options.CallProblem is { } problem)
        {
            result.Status  = AmazonDefinitionStatus.NotConfigured;
            result.Message = problem.Reason + " " + problem.NextAction;
            return result;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            result.Status  = AmazonDefinitionStatus.NoMatch;
            result.Message = "No search words were given, so there is nothing to look up.";
            return result;
        }

        var path = AmazonDefinitionsApi.SearchPath(query, amazon.Options.MarketplaceId);

        string body;
        int status;
        try
        {
            using var response = await amazon.SendAsync(HttpMethod.Get, path, cancellationToken: cancellationToken);
            status = (int)response.StatusCode;
            body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                result.Status  = AmazonDefinitionStatus.Error;
                result.Message = DescribeFailure(body, status, $"the product type search for \"{query}\"");
                log.Add("Warning", $"Amazon product type search HTTP {status}", ErrorSummary(body));
                return result;
            }
        }
        catch (AmazonTokenException ex)
        {
            // No token, so no request was made. That is a credential state, not a lookup failure,
            // and it is reported as the one the operator can act on.
            result.Status  = AmazonDefinitionStatus.NotConfigured;
            result.Message = ex.Message;
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            result.Status  = AmazonDefinitionStatus.Error;
            result.Message = "Amazon's Selling Partner API could not be reached, so the product types for " +
                             $"\"{query}\" are unknown.";
            log.Add("Warning", "Amazon product type search could not reach SP-API", Shorten(ex.Message));
            return result;
        }

        result.Candidates = AmazonProductTypeSearchResponse.Parse(body);

        if (result.Candidates.Count == 0)
        {
            result.Status  = AmazonDefinitionStatus.NoMatch;
            result.Message = $"Amazon has no product type matching \"{query}\" in this marketplace.";
            return result;
        }

        result.Chosen = AmazonProductTypeChooser.Choose(query, result.Candidates);

        if (result.Chosen is null)
        {
            result.Status  = AmazonDefinitionStatus.Ambiguous;
            result.Message = $"Amazon offered {result.Candidates.Count} product types for \"{query}\" and none is " +
                             "clearly the one. Pick from the list — the wrong product type asks for the wrong " +
                             "required attributes, which fails on submission rather than here.";
            return result;
        }

        result.Message = result.Chosen.Why;
        log.Add("Info", "Amazon product types searched",
            $"\"{query}\": {result.Candidates.Count} candidates, chose {result.Chosen.ProductType.Name} " +
            $"({result.Chosen.Confidence} confidence)");

        return result;
    }

    // ── One product type's schema ─────────────────────────────────────────────

    /// <summary>
    /// Everything one product type requires: the definition, then its schema, cache first.
    /// </summary>
    public async Task<AmazonProductTypeDefinition> GetDefinitionAsync(
        string productType,
        string requirements = AmazonDefinitionsApi.RequirementsListing,
        string locale = AmazonDefinitionsApi.DefaultLocale,
        CancellationToken cancellationToken = default)
    {
        var marketplaceId = amazon.Options.MarketplaceId;

        if (amazon.Options.CallProblem is { } problem)
            return Failed(productType, AmazonDefinitionStatus.NotConfigured,
                problem.Reason + " " + problem.NextAction);

        if (string.IsNullOrWhiteSpace(productType))
            return Failed(productType, AmazonDefinitionStatus.NoMatch,
                "No product type was named, so there is no schema to fetch.");

        // 1. The definition. Small, and it carries the version that decides whether the large
        //    document already on disk is still current.
        AmazonProductTypeDefinition definition;
        string definitionBody;
        try
        {
            var path = AmazonDefinitionsApi.DefinitionPath(productType, marketplaceId, requirements, locale);
            using var response = await amazon.SendAsync(HttpMethod.Get, path, cancellationToken: cancellationToken);
            var status = (int)response.StatusCode;
            definitionBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                log.Add("Warning", $"Amazon product type definition HTTP {status}", ErrorSummary(definitionBody));
                return ServeStaleOr(productType, marketplaceId, requirements, locale,
                    DescribeFailure(definitionBody, status, $"the {productType} product type definition"));
            }

            definition = AmazonDefinitionResponse.Parse(definitionBody);
        }
        catch (AmazonTokenException ex)
        {
            return Failed(productType, AmazonDefinitionStatus.NotConfigured, ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.Add("Warning", "Amazon product type definition could not reach SP-API", Shorten(ex.Message));
            return ServeStaleOr(productType, marketplaceId, requirements, locale,
                $"Amazon could not be reached for the {productType} product type definition.");
        }

        if (definition.Status != AmazonDefinitionStatus.Ok)
            return ServeStaleOr(productType, marketplaceId, requirements, locale, definition.Message);

        // Amazon echoes what it actually answered with, which is not always what was asked for.
        if (string.IsNullOrWhiteSpace(definition.ProductType)) definition.ProductType = productType;
        if (string.IsNullOrWhiteSpace(definition.MarketplaceId)) definition.MarketplaceId = marketplaceId;
        if (string.IsNullOrWhiteSpace(definition.Locale)) definition.Locale = locale;
        if (string.IsNullOrWhiteSpace(definition.Requirements)) definition.Requirements = requirements;

        var groups = AmazonDefinitionResponse.ParseGroups(definitionBody);

        // 2. The schema. Held on disk against Amazon's version, so an unchanged product type is
        //    never downloaded twice.
        var cached = cache.Read(definition.ProductType, definition.MarketplaceId,
                                definition.Requirements, definition.Locale, definition.Version);

        if (cached is not null)
        {
            definition.Attributes = AmazonSchemaParser.Parse(cached.Schema, groups);
            definition.FromCache  = true;
            return definition;
        }

        string schemaJson;
        try
        {
            schemaJson = await FetchSchemaAsync(definition.SchemaUrl, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            log.Add("Warning", "Amazon product type schema could not be fetched", Shorten(ex.Message));
            return ServeStaleOr(productType, marketplaceId, requirements, locale,
                $"Amazon's definition for {definition.ProductType} was read, but the schema document it points at " +
                "could not be fetched, so the attributes are unknown.");
        }

        definition.Attributes = AmazonSchemaParser.Parse(schemaJson, groups);

        if (definition.Attributes.Count == 0)
        {
            definition.Status  = AmazonDefinitionStatus.Error;
            definition.Message = $"Amazon's schema for {definition.ProductType} was fetched but carried no " +
                                 "attributes, so nothing can be said about what this product type requires.";
            return definition;
        }

        cache.Write(new AmazonCachedSchema(
            definition.ProductType, definition.MarketplaceId, definition.Requirements, definition.Locale,
            definition.Version, definition.SchemaChecksum, DateTimeOffset.UtcNow, schemaJson));

        log.Add("Info", "Amazon product type schema loaded",
            $"{definition.ProductType}: {definition.Attributes.Count} attributes, " +
            $"{definition.RequiredAttributes.Count()} required, version {definition.Version}");

        return definition;
    }

    /// <summary>
    /// Search, choose, and fetch the chosen product type's schema — the whole question in one call.
    /// </summary>
    public async Task<AmazonProductTypeAnswer> DescribeAsync(
        string query,
        string requirements = AmazonDefinitionsApi.RequirementsListing,
        CancellationToken cancellationToken = default)
    {
        var answer = new AmazonProductTypeAnswer { Search = await SearchAsync(query, cancellationToken) };

        // Only when a request actually reached Amazon. A notice describing what the sandbox
        // returned, attached to a lookup that never left the building because a credential is
        // missing, describes something that did not happen — and reads as "the sandbox had nothing
        // for these words" when the truth is that the sandbox was never asked.
        if (answer.Search.Status is AmazonDefinitionStatus.Ok
                                 or AmazonDefinitionStatus.NoMatch
                                 or AmazonDefinitionStatus.Ambiguous)
            answer.SandboxNotice = AmazonSandboxNotice.For(
                query, answer.Search.Candidates, amazon.Options.Sandbox);

        if (answer.Search.Chosen is { } chosen)
            answer.Definition = await GetDefinitionAsync(
                chosen.ProductType.Name, requirements, AmazonDefinitionsApi.DefaultLocale, cancellationToken);

        return answer;
    }

    // ── The pre-signed schema document ────────────────────────────────────────

    /// <summary>
    /// Fetches the schema document from the link Amazon's definition response gave.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Deliberately not sent through <see cref="AmazonService.SendAsync"/>.</b> This URL is
    /// pre-signed: the authorisation is already in the query string, and adding an
    /// <c>x-amz-access-token</c> or an <c>Authorization</c> header to it makes the request fail —
    /// with a signature error that reads like a broken credential, which is the confusing way to
    /// discover this. It is also a different host, so a token attached here would be an SP-API
    /// access token sent somewhere that is not SP-API.
    /// </para>
    /// <para>
    /// The host is checked before the request. The URL comes out of a response body, and following
    /// an arbitrary one because a remote document said so is a class of thing worth never doing.
    /// </para>
    /// </remarks>
    public async Task<string> FetchSchemaAsync(string schemaUrl, CancellationToken cancellationToken = default)
    {
        if (!IsAmazonUrl(schemaUrl))
            throw new InvalidOperationException(
                "The schema link in Amazon's response does not point at Amazon, so it was not followed.");

        var client = httpClientFactory.CreateClient();
        client.Timeout = SchemaFetchTimeout;

        using var request = new HttpRequestMessage(HttpMethod.Get, schemaUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"The pre-signed schema link answered HTTP {(int)response.StatusCode}. These links are short-lived, " +
                "so a stale one is refetched by asking for the definition again.");

        if (response.Content.Headers.ContentLength is > MaxSchemaBytes)
            throw new InvalidOperationException(
                $"Amazon's schema document is larger than this app will read ({MaxSchemaBytes / (1024 * 1024)} MB).");

        // Length-capped even when no Content-Length was sent — the header is the server's claim,
        // not a guarantee.
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxSchemaBytes)
                throw new InvalidOperationException(
                    $"Amazon's schema document exceeded {MaxSchemaBytes / (1024 * 1024)} MB while being read.");
            buffer.Write(chunk, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>True when a URL is one this app will follow: HTTPS, and an Amazon host.</summary>
    public static bool IsAmazonUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host;
        return host.EndsWith(".amazon.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("amazon.com", StringComparison.OrdinalIgnoreCase);
    }

    // ── When it does not work ─────────────────────────────────────────────────

    /// <summary>
    /// Amazon could not be asked, so serve what is on disk and say that is what happened — or, when
    /// there is nothing on disk, report the failure plainly.
    /// </summary>
    /// <remarks>
    /// Marked <see cref="AmazonDefinitionStatus.Stale"/> rather than ok. A month-old schema is
    /// almost certainly still right and is far better than nothing, but "these are Amazon's current
    /// requirements" and "these are the requirements as of the last time we could ask" are different
    /// claims, and only one of them is true here.
    /// </remarks>
    private AmazonProductTypeDefinition ServeStaleOr(
        string productType, string marketplaceId, string requirements, string locale, string message)
    {
        var cached = cache.Read(productType, marketplaceId, requirements, locale);
        if (cached is null) return Failed(productType, AmazonDefinitionStatus.Error, message);

        return new AmazonProductTypeDefinition
        {
            ProductType    = cached.ProductType,
            MarketplaceId  = cached.MarketplaceId,
            Requirements   = cached.Requirements,
            Locale         = cached.Locale,
            Version        = cached.Version,
            SchemaChecksum = cached.Checksum,
            Attributes     = AmazonSchemaParser.Parse(cached.Schema),
            FromCache      = true,
            Status         = AmazonDefinitionStatus.Stale,
            Message        = message + $" These attributes are the copy saved on {cached.FetchedAt:yyyy-MM-dd}, " +
                             "which may no longer be what Amazon requires.",
        };
    }

    private static AmazonProductTypeDefinition Failed(string productType, string status, string message) =>
        new() { ProductType = productType ?? "", Status = status, Message = message };

    /// <summary>
    /// Amazon's error body as a sentence. Mirrors <see cref="EbayService.DescribeAspectFailure"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The 403 is split because Amazon returns the same status for two unrelated problems and only
    /// says which in <c>details</c>. A request with no usable token gets "Access token is missing in
    /// the request header" — measured, by sending exactly the URL this app builds to the real
    /// sandbox host with no token. A request with a good token from an application that lacks the
    /// Product Listing role gets the same 403 with a different detail, and no amount of re-issuing
    /// a token fixes that one. Collapsing them sends whoever is reading it to the wrong screen.
    /// </para>
    /// <para>
    /// The 400 is worth naming too: a product type that exists in one marketplace need not exist in
    /// another, and that reads as a misspelling until you know.
    /// </para>
    /// </remarks>
    public static string DescribeFailure(string? body, int statusCode, string what)
    {
        var amazonMessage = FirstErrorMessage(body);

        return statusCode switch
        {
            400 => $"Amazon rejected {what}" +
                   (amazonMessage is null ? "." : $": {amazonMessage}") +
                   " A product type that exists in one marketplace may not exist in another.",
            403 when MentionsMissingToken(body) =>
                   $"Amazon refused {what} (403) because the request carried no usable access token. The URL and " +
                   "the marketplace were accepted — this is the Login with Amazon side, not the API side.",
            403 => $"Amazon refused {what} (403) with a token attached. That usually means the application is not " +
                   "authorised for the Product Listing role rather than that the token is bad — check the roles on " +
                   "the app in Seller Central, then re-authorise so the grant carries them." +
                   (amazonMessage is null ? "" : $" Amazon said: {amazonMessage}"),
            404 => $"Amazon has no such product type for {what}.",
            429 => $"Amazon is rate-limiting {what}. Nothing is wrong with the request; it needs to be made " +
                   "less often.",
            >= 500 => $"Amazon returned a server error for {what}, so it is worth retrying.",
            _ => $"Amazon returned HTTP {statusCode} for {what}" + (amazonMessage is null ? "." : $": {amazonMessage}"),
        };
    }

    /// <summary>The <c>message</c> of Amazon's first error, when it sent one.</summary>
    public static string? FirstErrorMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("errors", out var errors) ||
                errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() == 0) return null;

            var first = errors[0];
            if (first.ValueKind != JsonValueKind.Object) return null;
            if (!first.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String) return null;

            var text = message.GetString();
            return string.IsNullOrWhiteSpace(text) ? null : Shorten(text);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Whether Amazon's error is about the token being absent rather than about what it permits.
    /// </summary>
    /// <remarks>
    /// Amazon puts this in <c>details</c>, which is why the whole body is looked at rather than
    /// just the <c>message</c> — the message on a tokenless 403 is the generic "Access to requested
    /// resource is denied" and says nothing about which of the two problems it is.
    /// </remarks>
    public static bool MentionsMissingToken(string? body) =>
        !string.IsNullOrWhiteSpace(body) &&
        body.Contains("access token", StringComparison.OrdinalIgnoreCase);

    /// <summary>Amazon's error CODE for the log — never the body, which can echo the request.</summary>
    private static string ErrorSummary(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return "No error body.";

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0 &&
                errors[0].TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String)
                return $"Amazon error code: {code.GetString()}";
        }
        catch (JsonException) { }

        return "Amazon sent an error body with no code.";
    }

    private static string Shorten(string value) => value.Length <= 200 ? value : value[..200] + "…";
}
