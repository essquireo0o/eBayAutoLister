using System.Text.Json;
using System.Text.Json.Serialization;

namespace ING_eBay_AutoLister.Services;

// ── Getting into Amazon at all ────────────────────────────────────────────────
// Amazon's Selling Partner API is reached in two hops, and this file is the first one. Login with
// Amazon (LWA) trades a long-lived refresh token for an hour-long access token; SP-API then takes
// that token in a header. Neither hop involves a browser at runtime — unlike eBay, where the seller
// personally sits through a consent screen (see EbayAuthFlow) — so there is no relay, no session
// ledger and no pending state to age out here. What replaces all of it is a single question that
// has to be answered honestly before any request is worth making: is this deployment configured?
//
// That question is the whole reason this file is separate from AmazonService. Phase 1 of the Amazon
// work has the owner's SANDBOX client id and secret and nothing else — no seller refresh token, no
// marketplace, no seller id — and the difference between "not configured yet" and "configured and
// broken" is the difference between a setup instruction and a bug report. Everything below is pure
// so AmazonAuthFlowTests can pin both without a network.

/// <summary>Which Amazon this build talks to. Sandbox is the default everywhere; see <see cref="AmazonOptions"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AmazonEnvironment
{
    /// <summary>Amazon's SP-API sandbox. Canned responses, no real seller account, nothing listed.</summary>
    Sandbox,
    /// <summary>A real Selling Partner account. Nothing in this app selects this on its own.</summary>
    Production
}

/// <summary>
/// The SP-API region a marketplace lives in. Picking the wrong one is a 403, not a redirect.
/// </summary>
/// <remarks>
/// Amazon shards SP-API by region and a seller's credentials only work against the one their
/// marketplace belongs to — a North America refresh token presented to the Europe host is refused,
/// with an error that names neither the region nor the marketplace. Defaulted rather than required
/// because North America is where this app's seller is, and a wrong default that is documented
/// beats a required setting nobody knows to set.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AmazonRegion
{
    /// <summary>US, Canada, Mexico, Brazil.</summary>
    NorthAmerica,
    /// <summary>Europe, plus India, Egypt, Turkey, Saudi Arabia.</summary>
    Europe,
    /// <summary>Japan, Australia, Singapore.</summary>
    FarEast
}

/// <summary>
/// The hostnames each hop is made against.
/// </summary>
/// <remarks>
/// Kept as one table, in one place, for the reason <see cref="EbayAuthUrlCheck.Scopes"/> is: the
/// host a request is sent to and the host a diagnostic *claims* it was sent to must not be able to
/// drift apart. A status endpoint reporting "sandbox" while the client quietly talked to production
/// is worse than no status endpoint at all — and on the publish path, that is a live listing.
/// </remarks>
public static class AmazonEndpoints
{
    /// <summary>
    /// Where refresh tokens are exchanged. One host for every environment and every region —
    /// LWA is not sharded, and there is no sandbox variant of it.
    /// </summary>
    /// <remarks>
    /// Worth stating explicitly because it looks like a mistake: <see cref="AmazonEnvironment.Sandbox"/>
    /// changes the SP-API host below but NOT this one. Sandbox credentials are real LWA credentials
    /// and are exchanged at the real LWA endpoint; only the API they are then presented to is fake.
    /// </remarks>
    public const string LwaTokenUrl = "https://api.amazon.com/auth/o2/token";

    /// <summary>The SP-API host for one region and environment.</summary>
    public static string ApiHost(AmazonRegion region, AmazonEnvironment environment)
    {
        var host = region switch
        {
            AmazonRegion.Europe  => "sellingpartnerapi-eu.amazon.com",
            AmazonRegion.FarEast => "sellingpartnerapi-fe.amazon.com",
            _                    => "sellingpartnerapi-na.amazon.com",
        };

        return environment == AmazonEnvironment.Sandbox ? $"sandbox.{host}" : host;
    }

    /// <summary>The base URL <see cref="AmazonService"/> resolves every relative path against.</summary>
    public static string BaseUrl(AmazonRegion region, AmazonEnvironment environment) =>
        $"https://{ApiHost(region, environment)}";
}

// ── Is this deployment configured? ────────────────────────────────────────────

/// <summary>Why an SP-API call cannot be made, in the operator's terms. Mirrors <see cref="EbayAuthUrlProblem"/>.</summary>
public sealed record AmazonConfigProblem(string Code, string Reason, string NextAction);

/// <summary>
/// Everything that has to be set before a request to Amazon is worth sending.
/// </summary>
/// <remarks>
/// <para>
/// Split into two checks on purpose, because Phase 1 sits between them. <see cref="CheckToken"/> is
/// what it takes to obtain an access token — client id, secret, refresh token — and
/// <see cref="CheckCalls"/> is what it takes to then say something useful with it, which additionally
/// needs a marketplace and a seller. A deployment can pass the first and fail the second, and that
/// is exactly the state this app is in today: the owner's sandbox keyset is here, the seller half
/// is not.
/// </para>
/// <para>
/// Each problem names the environment variable to set rather than a screen to visit, because unlike
/// eBay's Client ID — which a seller pastes into Settings → eBay → Advanced — these are the
/// deployment's own and are set once, in the host's environment, by whoever runs it.
/// </para>
/// </remarks>
public static class AmazonConfigCheck
{
    /// <summary>
    /// The Selling Partner scope this app operates under. Not sent on the refresh_token grant —
    /// the scopes are fixed to the ones the seller authorised when they installed the application —
    /// so it is recorded here for the diagnostic to report rather than for a URL to carry.
    /// </summary>
    public const string ListingsScope = "sellingpartnerapi::listings";

    /// <summary>What it takes to obtain an access token, or null when nothing is missing.</summary>
    public static AmazonConfigProblem? CheckToken(string? clientId, string? clientSecret, string? refreshToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            return new("no_client_id",
                "The Amazon LWA Client ID isn't set, so there is no application for Amazon to issue a token to.",
                "Set Credentials__AmazonClientId to the client identifier from Seller Central → Apps & Services → Develop Apps.");

        if (string.IsNullOrWhiteSpace(clientSecret))
            return new("no_client_secret",
                "The Amazon LWA Client Secret isn't set. Amazon refuses the token exchange without it, so no SP-API call can be signed.",
                "Set Credentials__AmazonClientSecret to the client secret from the same Develop Apps screen.");

        if (string.IsNullOrWhiteSpace(refreshToken))
            return new("no_refresh_token",
                "No Amazon refresh token is stored. The Client ID and Secret identify the application, not the seller — " +
                "the refresh token is what says which Selling Partner account the calls are made on behalf of, and it is issued " +
                "once, when that seller authorises the app.",
                "Authorise the application against the seller account and set Credentials__AmazonRefreshToken to the " +
                "refresh token Amazon returns (it begins Atzr|).");

        return null;
    }

    /// <summary>
    /// What it takes to make a call that names a seller and a marketplace. Assumes
    /// <see cref="CheckToken"/> has been run first and reports its problem if it has not.
    /// </summary>
    public static AmazonConfigProblem? CheckCalls(
        string? clientId, string? clientSecret, string? refreshToken, string? marketplaceId, string? sellerId)
    {
        if (CheckToken(clientId, clientSecret, refreshToken) is { } tokenProblem) return tokenProblem;

        if (string.IsNullOrWhiteSpace(marketplaceId))
            return new("no_marketplace_id",
                "No Amazon marketplace ID is set. Every Listings and Catalog call is scoped to one marketplace, and Amazon " +
                "rejects the request rather than assuming one.",
                "Set Credentials__AmazonMarketplaceId to the marketplace this seller lists in — " +
                "amazon.com is ATVPDKIKX0DER; the full list is in Amazon's marketplace ID reference.");

        if (string.IsNullOrWhiteSpace(sellerId))
            return new("no_seller_id",
                "No Amazon seller ID is set. The Listings API addresses a listing as (seller, SKU), so without the seller " +
                "there is no address to put or read a listing at.",
                "Set Credentials__AmazonSellerId to the Merchant Token from Seller Central → Settings → Account Info.");

        return null;
    }
}

// ── Refusals from the token endpoint ──────────────────────────────────────────

/// <summary>What a failed LWA exchange means for the stored refresh token.</summary>
public enum AmazonLwaFailure
{
    /// <summary>Amazon's end, the network, or a configuration problem this end. The stored refresh
    /// token is untouched, because nothing about this says it is bad.</summary>
    Transient,
    /// <summary>Amazon said <c>invalid_grant</c>: this grant is dead and no retry will revive it.</summary>
    InvalidGrant
}

/// <summary>
/// Decides whether an LWA failure is allowed to condemn the stored refresh token.
/// </summary>
/// <remarks>
/// <para>
/// The same judgement as <see cref="EbayRefreshClassifier"/>, and it is here for the same reason:
/// the refresh token is the entire connection, and it cannot be re-issued without the seller
/// personally re-authorising the application in Seller Central. Treating a dropped connection or a
/// 500 as a revoked grant turns a wait-and-retry into a re-authorisation.
/// </para>
/// <para>
/// Amazon's wrinkle is that <c>invalid_client</c> — a wrong client id or secret — also arrives as a
/// 401 with an error code, and it says nothing whatever about the grant. So only <c>invalid_grant</c>
/// counts, exactly as on the eBay side; a bare 400 does not, and neither does anything at 5xx, where
/// a body mentioning invalid_grant is an error page quoting itself rather than Amazon adjudicating
/// this token.
/// </para>
/// </remarks>
public static class AmazonLwaClassifier
{
    public static AmazonLwaFailure Classify(int? httpStatus, string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return AmazonLwaFailure.Transient;
        if (httpStatus is not (>= 400 and < 500)) return AmazonLwaFailure.Transient;

        return MentionsInvalidGrant(responseBody) ? AmazonLwaFailure.InvalidGrant : AmazonLwaFailure.Transient;
    }

    /// <summary>
    /// The <c>error</c> field, when Amazon sent one. Reported by the diagnostic so a refused
    /// exchange names its own cause — <c>invalid_client</c> and <c>invalid_grant</c> need
    /// completely different fixes and are indistinguishable from the HTTP status alone.
    /// </summary>
    public static string? ErrorCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;

        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var err) &&
                err.ValueKind == JsonValueKind.String)
            {
                var code = err.GetString();
                return string.IsNullOrWhiteSpace(code) ? null : code;
            }
        }
        catch (JsonException) { /* Not JSON — no code to report, and the status still stands. */ }

        return null;
    }

    private static bool MentionsInvalidGrant(string body)
    {
        if (ErrorCode(body) is { } code)
            return string.Equals(code, "invalid_grant", StringComparison.OrdinalIgnoreCase);

        // Not JSON, or JSON without an error field. Amazon's gateway occasionally wraps the payload
        // in HTML on the way through, and the marker is still the only thing being looked for.
        return body.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase);
    }
}

// ── The token itself ──────────────────────────────────────────────────────────

/// <summary>One access token, and when it stops working.</summary>
public sealed record AmazonAccessToken(string Value, string TokenType, DateTimeOffset ExpiresAt)
{
    /// <summary>
    /// True when this token must not be used as-is. Skewed early by
    /// <see cref="AmazonTokenExpiry.Skew"/> so a token does not expire mid-flight.
    /// </summary>
    public bool IsExpiredAt(DateTimeOffset now) => now >= ExpiresAt - AmazonTokenExpiry.Skew;
}

/// <summary>
/// When an LWA access token is too old to use.
/// </summary>
/// <remarks>
/// Far simpler than <see cref="EbayTokenExpiry"/>, and the difference is worth naming: eBay stores
/// its access token in credentials.json and has to reason about one whose expiry was never recorded.
/// An LWA token is never persisted — it lives an hour, it is held in memory by
/// <see cref="AmazonService"/>, and a restart simply fetches another. So there is no unknown-expiry
/// case to get wrong, because a token this app did not itself just receive does not exist.
/// </remarks>
public static class AmazonTokenExpiry
{
    /// <summary>Treat a token as spent this long before Amazon does, to cover a slow request.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    /// <summary>What LWA issues when it does not say: one hour.</summary>
    public const int DefaultLifetimeSeconds = 3600;

    /// <summary>Seconds-from-now to an absolute instant, falling back to the documented hour.</summary>
    public static DateTimeOffset ExpiryFrom(int expiresInSeconds, DateTimeOffset now) =>
        now.AddSeconds(expiresInSeconds > 0 ? expiresInSeconds : DefaultLifetimeSeconds);
}

/// <summary>
/// Reads LWA's token response. Separated from the request so the interesting part — what counts as
/// a usable token — is testable without a network.
/// </summary>
public static class AmazonLwaResponse
{
    /// <summary>
    /// Pulls the access token out of LWA's JSON.
    /// </summary>
    /// <remarks>
    /// Returns false for a success status carrying no <c>access_token</c>, which is the case that
    /// would otherwise be cached as a valid empty token and then sent on every SP-API call for the
    /// next hour, producing a string of 403s that name nothing.
    /// </remarks>
    public static bool TryRead(string? body, DateTimeOffset now, out AmazonAccessToken? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(body)) return false;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            var access = Str(doc.RootElement, "access_token");
            if (string.IsNullOrWhiteSpace(access)) return false;

            token = new AmazonAccessToken(
                access,
                Str(doc.RootElement, "token_type") is { Length: > 0 } t ? t : "bearer",
                AmazonTokenExpiry.ExpiryFrom(Int(doc.RootElement, "expires_in"), now));
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string Str(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static int Int(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : 0;
}

/// <summary>
/// Raised when an LWA exchange fails. Typed for the reason
/// <see cref="EbayTokenRefreshException"/> is: a caller has to tell "Amazon refused this grant"
/// apart from "Amazon was not reachable" without parsing a message.
/// </summary>
public sealed class AmazonTokenException(int? statusCode, string message, bool invalidGrant = false, string? errorCode = null)
    : Exception(message)
{
    public int? StatusCode { get; } = statusCode;
    public bool InvalidGrant { get; } = invalidGrant;

    /// <summary>Amazon's own <c>error</c> field, when it sent one — <c>invalid_client</c> and friends.</summary>
    public string? ErrorCode { get; } = errorCode;
}
