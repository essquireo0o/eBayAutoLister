using System.Net.Http.Headers;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

// ── The Amazon connection ─────────────────────────────────────────────────────
// The counterpart of EbayService, and deliberately readable alongside it: obtain a token, renew it
// before it dies, attach it to every request, and say plainly when none of that is possible. What
// is NOT mirrored is where the credentials live, and that difference is the whole of this file's
// design — see AmazonOptions.

/// <summary>
/// The Amazon application this deployment runs as, read once from configuration at startup.
/// </summary>
/// <remarks>
/// <para>
/// Read from the same <c>Credentials</c> section as everything else — so the environment variables
/// are <c>Credentials__AmazonClientId</c> and friends, matching <c>Credentials__EbayClientId</c> —
/// but held here rather than on <see cref="Credentials"/> or <see cref="ServerCredentials"/>, and
/// that is on purpose.
/// </para>
/// <para>
/// <b>The Amazon refresh token must never go near <see cref="ServerCredentials"/>.</b> That type is
/// overlaid onto every signed-in user's record on the hosted build, so a seller grant sitting on it
/// would be one Amazon account shared by everybody who signs up — the exact failure
/// <c>HostedEbayCredentialsTests</c> exists to prevent on the eBay side, which is why the eBay
/// tokens are absent from it. Keeping the Amazon credentials in their own options object means the
/// per-user machinery has nothing to spread: Phase 1 is the owner's own sandbox application, used
/// for diagnostics, and when a real seller authorisation arrives it will arrive per user, through
/// the same store the eBay grant already uses.
/// </para>
/// <para>
/// Nothing here is defaulted to a real-looking value. An unset marketplace is an unset marketplace,
/// reported as such by <see cref="AmazonConfigCheck"/> — a plausible-looking default would produce
/// calls made confidently against the wrong seller's marketplace.
/// </para>
/// </remarks>
public sealed class AmazonOptions
{
    /// <summary>The configuration section, shared with <see cref="ServerCredentials.Section"/>.</summary>
    public const string Section = "Credentials";

    /// <summary>Turns the sandbox off. Named here because it is the one setting that can cost real money.</summary>
    public const string SandboxSetting = "Credentials:AmazonSandbox";

    public string ClientId      { get; init; } = "";
    public string ClientSecret  { get; init; } = "";
    public string RefreshToken  { get; init; } = "";
    public string MarketplaceId { get; init; } = "";
    public string SellerId      { get; init; } = "";

    /// <summary>
    /// Which Amazon this build talks to. <b>Sandbox unless something explicitly says otherwise.</b>
    /// </summary>
    /// <remarks>
    /// Defaulted to sandbox rather than to production because the two mistakes are not the same
    /// size. A sandbox default that was wrong lists nothing and costs nothing; a production default
    /// that was wrong publishes a real listing to a real Selling Partner account, and Amazon
    /// suspends accounts over listings that should not have existed. So a missing, blank or
    /// unparseable setting is sandbox, and only the literal string <c>false</c> moves it.
    /// </remarks>
    public bool Sandbox { get; init; } = true;

    public AmazonRegion Region { get; init; } = AmazonRegion.NorthAmerica;

    public AmazonEnvironment Environment => Sandbox ? AmazonEnvironment.Sandbox : AmazonEnvironment.Production;

    public string BaseUrl => AmazonEndpoints.BaseUrl(Region, Environment);

    /// <summary>True when this deployment supplies the Amazon application half of the credentials.</summary>
    public bool HasApplication =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);

    /// <summary>True when a token can be asked for: the application, plus a seller's grant.</summary>
    public bool CanObtainToken =>
        AmazonConfigCheck.CheckToken(ClientId, ClientSecret, RefreshToken) is null;

    /// <summary>True when a call that names a seller and a marketplace can be made.</summary>
    public bool CanCall =>
        AmazonConfigCheck.CheckCalls(ClientId, ClientSecret, RefreshToken, MarketplaceId, SellerId) is null;

    public AmazonConfigProblem? TokenProblem =>
        AmazonConfigCheck.CheckToken(ClientId, ClientSecret, RefreshToken);

    public AmazonConfigProblem? CallProblem =>
        AmazonConfigCheck.CheckCalls(ClientId, ClientSecret, RefreshToken, MarketplaceId, SellerId);

    public static AmazonOptions FromConfiguration(IConfiguration configuration) => new()
    {
        ClientId      = Read(configuration, "AmazonClientId"),
        ClientSecret  = Read(configuration, "AmazonClientSecret"),
        RefreshToken  = Read(configuration, "AmazonRefreshToken"),
        MarketplaceId = Read(configuration, "AmazonMarketplaceId"),
        SellerId      = Read(configuration, "AmazonSellerId"),
        Sandbox       = ReadSandbox(configuration),
        Region        = ReadRegion(configuration),
    };

    /// <summary>
    /// Sandbox unless configuration says the literal <c>false</c>. A typo reads as sandbox, which is
    /// the harmless half of the mistake — see <see cref="Sandbox"/>.
    /// </summary>
    private static bool ReadSandbox(IConfiguration configuration) =>
        !bool.TryParse(Read(configuration, "AmazonSandbox"), out var sandbox) || sandbox;

    private static AmazonRegion ReadRegion(IConfiguration configuration) =>
        Read(configuration, "AmazonRegion").ToLowerInvariant() switch
        {
            "eu" or "europe"   => AmazonRegion.Europe,
            "fe" or "fareast"  => AmazonRegion.FarEast,
            _                  => AmazonRegion.NorthAmerica,
        };

    private static string Read(IConfiguration configuration, string name) =>
        (configuration[$"{Section}:{name}"] ?? "").Trim();
}

/// <summary>
/// What <see cref="AmazonService.GetStatusAsync"/> found. Carries no token and no secret — booleans,
/// lengths, hostnames and this app's own sentences, for the reason <c>/api/ebay/status</c> does.
/// </summary>
/// <param name="TokenObtainable">
/// Null when it was not attempted, because the configuration could not support a token in the first
/// place. Distinct from false, which means it was attempted and Amazon refused.
/// </param>
public sealed record AmazonStatus(
    bool Configured,
    bool ApplicationConfigured,
    bool? TokenObtainable,
    string Environment,
    string Region,
    string ApiHost,
    string TokenEndpoint,
    string? Code,
    string Message,
    string NextAction,
    DateTimeOffset? TokenExpiresAt = null,
    int? HttpStatus = null);

/// <summary>
/// Talks to Amazon's Selling Partner API: one token, renewed before it expires, on every request.
/// </summary>
/// <remarks>
/// <para>
/// The access token is held in memory and never written down. An LWA token lasts an hour and a
/// restart can simply ask for another, so persisting it would add a stale-token case
/// (<see cref="EbayTokenExpiry"/> exists largely to handle eBay's) in exchange for saving one
/// request an hour.
/// </para>
/// <para>
/// The refresh token is only ever read, never destroyed here, and not even when Amazon says
/// <c>invalid_grant</c> — unlike the eBay path, which has somewhere to record that verdict for the
/// seller to see. This one comes from the host's own environment, so there is nothing for the app
/// to revoke: it says so, loudly, and whoever runs the deployment fixes the variable.
/// </para>
/// </remarks>
public sealed class AmazonService(AmazonOptions options, IHttpClientFactory httpClientFactory, ActionLog log)
{
    /// <summary>Ceiling on any single call to LWA. Mirrors eBay's token endpoint timeout.</summary>
    private static readonly TimeSpan TokenEndpointTimeout = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private AmazonAccessToken? _token;

    public AmazonOptions Options => options;

    /// <summary>The token this process is currently holding, if any. For diagnostics only.</summary>
    public AmazonAccessToken? CachedToken => _token;

    // ── Getting a token ───────────────────────────────────────────────────────

    /// <summary>
    /// The access token to call Amazon with, obtained or renewed first if it is spent.
    /// </summary>
    /// <remarks>
    /// Serialised on <see cref="_tokenLock"/> so a burst of concurrent calls arriving on an expired
    /// token produces one exchange rather than one each — LWA throttles, and a stampede against it
    /// answers with 429s that look exactly like a broken credential.
    /// </remarks>
    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        if (options.TokenProblem is { } problem)
            throw new AmazonTokenException(null, problem.Reason, invalidGrant: false, errorCode: problem.Code);

        var now = DateTimeOffset.UtcNow;
        if (_token is { } cached && !cached.IsExpiredAt(now)) return cached.Value;

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            // Re-checked inside the lock: whoever was ahead in the queue has already fetched one.
            now = DateTimeOffset.UtcNow;
            if (_token is { } fresh && !fresh.IsExpiredAt(now)) return fresh.Value;

            var token = await ExchangeRefreshTokenAsync(cancellationToken);
            _token = token;
            return token.Value;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>Trades the configured refresh token for an access token. See <see cref="AmazonLwaClassifier"/>.</summary>
    private async Task<AmazonAccessToken> ExchangeRefreshTokenAsync(CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TokenEndpointTimeout;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // LWA takes the client credentials in the form body, not in a Basic header the way eBay's
        // token endpoint does. Sending them as Basic here is answered with invalid_client.
        var body = new FormUrlEncodedContent([
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", options.RefreshToken),
            new KeyValuePair<string, string>("client_id", options.ClientId),
            new KeyValuePair<string, string>("client_secret", options.ClientSecret),
        ]);

        HttpResponseMessage response;
        string responseBody;
        try
        {
            response = await client.PostAsync(AmazonEndpoints.LwaTokenUrl, body, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Nothing answered. This says nothing whatever about the grant.
            log.Add("Warning", "Amazon token request could not reach Login with Amazon", Shorten(ex.Message));
            throw new AmazonTokenException(null,
                "Amazon's Login with Amazon service couldn't be reached, so no access token could be obtained. " +
                "The stored credentials have been left alone.",
                invalidGrant: false);
        }

        var status = (int)response.StatusCode;

        if (!response.IsSuccessStatusCode)
        {
            var failure   = AmazonLwaClassifier.Classify(status, responseBody);
            var errorCode = AmazonLwaClassifier.ErrorCode(responseBody);

            // The error CODE only — never the body, which on some LWA failures echoes the request.
            log.Add("Warning", $"Amazon token request HTTP {status}",
                failure == AmazonLwaFailure.InvalidGrant
                    ? "Amazon reported invalid_grant — the seller's authorisation has been revoked or has expired."
                    : $"Amazon refused the token request{(errorCode is null ? "" : $" ({errorCode})")} without revoking the authorisation.");

            throw new AmazonTokenException(status,
                failure == AmazonLwaFailure.InvalidGrant
                    ? "Amazon revoked this authorisation (invalid_grant) — the seller removed the app in Seller Central, " +
                      "or the refresh token was replaced. Nothing but a fresh authorisation restores it."
                    : errorCode == "invalid_client"
                        ? "Amazon rejected the Client ID or Client Secret (invalid_client). The credentials are for a " +
                          "different application, or one of them was truncated when it was set."
                        : $"Amazon refused to issue an access token (HTTP {status}" +
                          $"{(errorCode is null ? "" : $", {errorCode}")}) without saying the authorisation is revoked, " +
                          "so it will be retried.",
                invalidGrant: failure == AmazonLwaFailure.InvalidGrant,
                errorCode: errorCode);
        }

        if (!AmazonLwaResponse.TryRead(responseBody, DateTimeOffset.UtcNow, out var token) || token is null)
            throw new AmazonTokenException(status,
                "Amazon accepted the token request but returned no access token, so there is nothing to call the API with.",
                invalidGrant: false);

        log.Add("Info", "Amazon access token obtained",
            $"Environment: {options.Environment}; Host: {AmazonEndpoints.ApiHost(options.Region, options.Environment)}; " +
            $"Expires at: {token.ExpiresAt:u}");

        return token;
    }

    // ── Calling SP-API ────────────────────────────────────────────────────────

    /// <summary>
    /// Sends one SP-API request with the access token attached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="path"/> is relative — <c>/listings/2021-08-01/items/...</c> — and is resolved
    /// against the host for this environment and region, so no caller anywhere picks a hostname and
    /// no caller can accidentally address production while the app reports sandbox.
    /// </para>
    /// <para>
    /// There is no request signing here. SP-API dropped the AWS SigV4 requirement in 2023 and the
    /// LWA token in <c>x-amz-access-token</c> is now the whole of the authentication; the older
    /// literature describing an IAM role and a signed canonical request is describing a scheme
    /// Amazon has since retired.
    /// </para>
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method, string path, HttpContent? content = null, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken);

        var client = httpClientFactory.CreateClient();
        client.BaseAddress = new Uri(options.BaseUrl);

        using var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.TryAddWithoutValidation("x-amz-access-token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Amazon asks every application to identify itself, and answers a request that does not
        // with a 403 that names nothing.
        request.Headers.UserAgent.ParseAdd("ING-AutoLister/1.0 (Language=C#; Platform=.NET)");

        return await client.SendAsync(request, cancellationToken);
    }

    // ── Diagnostics ───────────────────────────────────────────────────────────

    /// <summary>
    /// Whether this deployment can talk to Amazon, and if not, which half is missing.
    /// </summary>
    /// <remarks>
    /// Attempts a real token exchange when — and only when — the configuration could support one.
    /// Where it could not, <see cref="AmazonStatus.TokenObtainable"/> is null rather than false:
    /// "we never asked" and "we asked and were refused" are different states needing different
    /// fixes, and collapsing them into one boolean is how a missing environment variable comes to
    /// be read as a rejected credential.
    /// </remarks>
    public async Task<AmazonStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var environment = options.Environment.ToString();
        var host        = AmazonEndpoints.ApiHost(options.Region, options.Environment);

        AmazonStatus Build(bool configured, bool? obtainable, string? code, string message, string next,
            DateTimeOffset? expiresAt = null, int? httpStatus = null) =>
            new(configured, options.HasApplication, obtainable, environment, options.Region.ToString(),
                host, AmazonEndpoints.LwaTokenUrl, code, message, next, expiresAt, httpStatus);

        if (options.TokenProblem is { } problem)
            return Build(false, null, problem.Code, problem.Reason, problem.NextAction);

        try
        {
            var token = await GetAccessTokenAsync(cancellationToken);
            var callProblem = options.CallProblem;

            // A token proves the application and the grant. It does not prove there is a marketplace
            // or a seller to name in a call, so a deployment can be genuinely half-configured and
            // this reports exactly that rather than a flat "connected".
            return Build(
                configured: callProblem is null,
                obtainable: !string.IsNullOrWhiteSpace(token),
                code: callProblem?.Code,
                message: callProblem is null
                    ? $"Connected to the Amazon SP-API {environment.ToLowerInvariant()} at {host}."
                    : $"An access token was obtained, so the application and the seller authorisation are good. {callProblem.Reason}",
                next: callProblem?.NextAction ?? "Nothing — Amazon is reachable and this deployment is configured.",
                expiresAt: _token?.ExpiresAt);
        }
        catch (AmazonTokenException ex)
        {
            return Build(false, false, ex.ErrorCode ?? (ex.InvalidGrant ? "invalid_grant" : "token_refused"),
                ex.Message,
                ex.InvalidGrant
                    ? "Re-authorise this application against the seller account in Seller Central and set the new " +
                      "Credentials__AmazonRefreshToken."
                    : "Check Credentials__AmazonClientId and Credentials__AmazonClientSecret, then try again.",
                httpStatus: ex.StatusCode);
        }
    }

    private static string Shorten(string value) =>
        value.Length <= 200 ? value : value[..200] + "…";
}
