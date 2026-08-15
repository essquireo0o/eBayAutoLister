using System.Reflection;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Amazon auth layer: what counts as configured, which failures may condemn a grant, and which
/// Amazon a given set of settings actually points at.
/// </summary>
/// <remarks>
/// <para>
/// Phase 1 of the Amazon work has half the credentials — the owner's sandbox application, and no
/// seller authorisation at all — so the state these tests care most about is the honest report of a
/// half-configured deployment. "Not configured" has to name the missing piece and stay clearly
/// apart from "configured and refused"; the two need opposite responses, and a diagnostic that
/// blurs them sends whoever is on the other end looking for a broken credential that was never set.
/// </para>
/// <para>
/// Everything here is pure. No network, no Amazon, no clock — the times are passed in.
/// </para>
/// </remarks>
public class AmazonAuthFlowTests
{
    // Shaped like the real thing, obviously not the real thing. The sandbox keyset this app is
    // actually configured with never appears in a test, a log line or a status response.
    private const string ClientId     = "amzn1.application-oa2-client.notarealclientid";
    private const string ClientSecret = "amzn1.oa2-cs.v1.notarealsecret";
    private const string RefreshToken = "Atzr|IwEBIExampleRefreshTokenThatIsNotReal0000000000";
    private const string UsMarketplace = "ATVPDKIKX0DER";
    private const string SellerId      = "A0000000000000";

    // ── What "configured" means ──────────────────────────────────────────────────────────────

    [Fact]
    public void A_deployment_with_nothing_set_is_told_to_start_with_the_client_id()
    {
        var problem = AmazonConfigCheck.CheckToken(null, null, null);

        Assert.Equal("no_client_id", problem?.Code);
        Assert.Contains("Credentials__AmazonClientId", problem!.NextAction);
    }

    [Fact]
    public void The_application_half_alone_cannot_obtain_a_token_and_the_refresh_token_is_named()
    {
        // This is exactly where the app stands today: the owner's sandbox client id and secret are
        // configured, and no seller has authorised anything.
        var problem = AmazonConfigCheck.CheckToken(ClientId, ClientSecret, refreshToken: "");

        Assert.Equal("no_refresh_token", problem?.Code);
        Assert.Contains("Credentials__AmazonRefreshToken", problem!.NextAction);
        // The distinction that makes the message worth reading: the application is not the seller.
        Assert.Contains("identify the application, not the seller", problem.Reason);
    }

    [Fact]
    public void A_secret_that_was_never_set_is_not_reported_as_a_missing_refresh_token()
    {
        Assert.Equal("no_client_secret", AmazonConfigCheck.CheckToken(ClientId, "   ", RefreshToken)?.Code);
    }

    [Fact]
    public void A_full_token_credential_set_has_no_problem()
    {
        Assert.Null(AmazonConfigCheck.CheckToken(ClientId, ClientSecret, RefreshToken));
    }

    [Fact]
    public void A_token_is_not_enough_to_make_a_call_the_marketplace_and_seller_are_named_in_turn()
    {
        var noMarketplace = AmazonConfigCheck.CheckCalls(ClientId, ClientSecret, RefreshToken, null, SellerId);
        Assert.Equal("no_marketplace_id", noMarketplace?.Code);

        var noSeller = AmazonConfigCheck.CheckCalls(ClientId, ClientSecret, RefreshToken, UsMarketplace, null);
        Assert.Equal("no_seller_id", noSeller?.Code);

        Assert.Null(AmazonConfigCheck.CheckCalls(ClientId, ClientSecret, RefreshToken, UsMarketplace, SellerId));
    }

    [Fact]
    public void The_call_check_reports_a_missing_token_credential_before_a_missing_marketplace()
    {
        // Otherwise a deployment with nothing set is told to go and find a marketplace ID first.
        var problem = AmazonConfigCheck.CheckCalls(null, null, null, null, null);

        Assert.Equal("no_client_id", problem?.Code);
    }

    // ── Which Amazon ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Sandbox_and_production_are_different_hosts()
    {
        Assert.Equal("sandbox.sellingpartnerapi-na.amazon.com",
            AmazonEndpoints.ApiHost(AmazonRegion.NorthAmerica, AmazonEnvironment.Sandbox));
        Assert.Equal("sellingpartnerapi-na.amazon.com",
            AmazonEndpoints.ApiHost(AmazonRegion.NorthAmerica, AmazonEnvironment.Production));
    }

    [Theory]
    [InlineData(AmazonRegion.NorthAmerica, "sellingpartnerapi-na.amazon.com")]
    [InlineData(AmazonRegion.Europe,       "sellingpartnerapi-eu.amazon.com")]
    [InlineData(AmazonRegion.FarEast,      "sellingpartnerapi-fe.amazon.com")]
    public void Each_region_has_its_own_host(AmazonRegion region, string expected)
    {
        Assert.Equal(expected, AmazonEndpoints.ApiHost(region, AmazonEnvironment.Production));
    }

    [Fact]
    public void The_token_endpoint_is_the_same_one_in_sandbox()
    {
        // It looks like an oversight and is not: sandbox credentials are real LWA credentials, and
        // there is no sandbox LWA. Only the API they are then presented to is fake.
        Assert.Equal("https://api.amazon.com/auth/o2/token", AmazonEndpoints.LwaTokenUrl);
    }

    // ── The sandbox flag, which is the one that can cost money ───────────────────────────────

    [Fact]
    public void Sandbox_is_on_when_nothing_says_otherwise()
    {
        var options = AmazonOptions.FromConfiguration(Build());

        Assert.True(options.Sandbox);
        Assert.Equal(AmazonEnvironment.Sandbox, options.Environment);
        Assert.StartsWith("https://sandbox.", options.BaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("FALSE!")]
    public void An_unparseable_sandbox_setting_stays_in_the_sandbox(string value)
    {
        // The harmless half of the mistake. A typo must never be the thing that publishes a real
        // listing to a real Selling Partner account.
        Assert.True(AmazonOptions.FromConfiguration(Build(("Credentials__AmazonSandbox", value))).Sandbox);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    public void Only_an_explicit_false_leaves_the_sandbox(string value)
    {
        var options = AmazonOptions.FromConfiguration(Build(("Credentials__AmazonSandbox", value)));

        Assert.False(options.Sandbox);
        Assert.Equal(AmazonEnvironment.Production, options.Environment);
    }

    // ── Reading the configuration ────────────────────────────────────────────────────────────

    [Fact]
    public void Every_credential_is_read_from_the_name_that_goes_in_the_env_file()
    {
        var options = AmazonOptions.FromConfiguration(Build(
            ("Credentials__AmazonClientId",      ClientId),
            ("Credentials__AmazonClientSecret",  ClientSecret),
            ("Credentials__AmazonRefreshToken",  RefreshToken),
            ("Credentials__AmazonMarketplaceId", UsMarketplace),
            ("Credentials__AmazonSellerId",      SellerId)));

        Assert.Equal(ClientId,      options.ClientId);
        Assert.Equal(ClientSecret,  options.ClientSecret);
        Assert.Equal(RefreshToken,  options.RefreshToken);
        Assert.Equal(UsMarketplace, options.MarketplaceId);
        Assert.Equal(SellerId,      options.SellerId);
        Assert.True(options.CanObtainToken);
        Assert.True(options.CanCall);
    }

    [Fact]
    public void The_application_alone_is_reported_as_an_application_without_a_seller()
    {
        var options = AmazonOptions.FromConfiguration(Build(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret)));

        Assert.True(options.HasApplication);   // the owner's sandbox keyset is here…
        Assert.False(options.CanObtainToken);  // …and no seller has authorised it.
        Assert.Equal("no_refresh_token", options.TokenProblem?.Code);
    }

    [Fact]
    public void A_marketplace_is_never_invented()
    {
        // The US marketplace ID is a well-known constant and defaulting to it would be the easy
        // thing to do. It would also mean a seller in Canada silently listing into amazon.com.
        var options = AmazonOptions.FromConfiguration(Build(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret),
            ("Credentials__AmazonRefreshToken", RefreshToken)));

        Assert.Equal("", options.MarketplaceId);
        Assert.Equal("", options.SellerId);
        Assert.Equal("no_marketplace_id", options.CallProblem?.Code);
    }

    [Theory]
    [InlineData("eu", AmazonRegion.Europe)]
    [InlineData("europe", AmazonRegion.Europe)]
    [InlineData("fe", AmazonRegion.FarEast)]
    [InlineData("", AmazonRegion.NorthAmerica)]
    [InlineData("nonsense", AmazonRegion.NorthAmerica)]
    public void The_region_defaults_to_north_america(string value, AmazonRegion expected)
    {
        Assert.Equal(expected, AmazonOptions.FromConfiguration(Build(("Credentials__AmazonRegion", value))).Region);
    }

    // ── What may condemn a refresh token ─────────────────────────────────────────────────────

    [Fact]
    public void Only_amazon_saying_invalid_grant_condemns_the_authorisation()
    {
        Assert.Equal(AmazonLwaFailure.InvalidGrant,
            AmazonLwaClassifier.Classify(400, """{"error":"invalid_grant","error_description":"..."}"""));
    }

    [Fact]
    public void A_rejected_client_secret_does_not_condemn_the_authorisation()
    {
        // invalid_client is the application being wrong. The seller's grant is untouched by it, and
        // treating this as a revocation would send somebody to re-authorise over a typo.
        var body = """{"error":"invalid_client","error_description":"Client authentication failed"}""";

        Assert.Equal(AmazonLwaFailure.Transient, AmazonLwaClassifier.Classify(401, body));
        Assert.Equal("invalid_client", AmazonLwaClassifier.ErrorCode(body));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(503)]
    public void A_server_error_quoting_invalid_grant_is_still_an_outage(int status)
    {
        // A 5xx body mentioning the marker is an error page quoting itself, not Amazon adjudicating
        // this token. Same rule as EbayRefreshClassifier, and for the same reason.
        Assert.Equal(AmazonLwaFailure.Transient,
            AmazonLwaClassifier.Classify(status, """{"error":"invalid_grant"}"""));
    }

    [Fact]
    public void An_unreachable_endpoint_and_an_empty_body_are_transient()
    {
        Assert.Equal(AmazonLwaFailure.Transient, AmazonLwaClassifier.Classify(null, null));
        Assert.Equal(AmazonLwaFailure.Transient, AmazonLwaClassifier.Classify(400, ""));
    }

    [Fact]
    public void A_bare_four_hundred_with_no_marker_is_transient()
    {
        Assert.Equal(AmazonLwaFailure.Transient, AmazonLwaClassifier.Classify(400, """{"message":"Bad Request"}"""));
    }

    [Fact]
    public void A_non_json_body_carrying_the_marker_is_still_read()
    {
        // Amazon's gateway occasionally wraps the payload in HTML on the way through.
        Assert.Equal(AmazonLwaFailure.InvalidGrant,
            AmazonLwaClassifier.Classify(400, "<html><body>error: invalid_grant</body></html>"));
        Assert.Null(AmazonLwaClassifier.ErrorCode("<html>not json</html>"));
    }

    // ── The token that comes back ────────────────────────────────────────────────────────────

    [Fact]
    public void A_token_response_is_read_with_its_expiry()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(AmazonLwaResponse.TryRead(
            """{"access_token":"Atza|abc","token_type":"bearer","expires_in":3600}""", now, out var token));

        Assert.Equal("Atza|abc", token!.Value);
        Assert.Equal("bearer", token.TokenType);
        Assert.Equal(now.AddHours(1), token.ExpiresAt);
    }

    [Fact]
    public void A_success_carrying_no_access_token_is_not_a_token()
    {
        // Otherwise this is cached as a valid empty token and sent on every call for an hour,
        // producing a run of 403s that name nothing.
        var now = DateTimeOffset.UtcNow;

        Assert.False(AmazonLwaResponse.TryRead("""{"token_type":"bearer","expires_in":3600}""", now, out _));
        Assert.False(AmazonLwaResponse.TryRead("""{"access_token":""}""", now, out _));
        Assert.False(AmazonLwaResponse.TryRead("not json at all", now, out _));
        Assert.False(AmazonLwaResponse.TryRead("", now, out _));
    }

    [Fact]
    public void A_response_that_omits_the_lifetime_gets_amazons_documented_hour()
    {
        var now = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.True(AmazonLwaResponse.TryRead("""{"access_token":"Atza|abc"}""", now, out var token));
        Assert.Equal(now.AddSeconds(AmazonTokenExpiry.DefaultLifetimeSeconds), token!.ExpiresAt);
    }

    [Fact]
    public void A_token_is_spent_before_amazon_says_so()
    {
        var now   = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
        var token = new AmazonAccessToken("Atza|abc", "bearer", now.AddHours(1));

        Assert.False(token.IsExpiredAt(now));
        Assert.False(token.IsExpiredAt(now.AddMinutes(58)));
        // Inside the skew: still valid to Amazon, not worth starting a request with.
        Assert.True(token.IsExpiredAt(now.AddHours(1) - AmazonTokenExpiry.Skew));
        Assert.True(token.IsExpiredAt(now.AddHours(2)));
    }

    // ── The line the hosted build must not cross ─────────────────────────────────────────────

    [Fact]
    public void No_amazon_seller_grant_can_reach_ServerCredentials()
    {
        // The same guard HostedEbayCredentialsTests puts on the eBay tokens, for the same reason:
        // ServerCredentials is overlaid onto every signed-in user's record, so a seller grant on it
        // would be one Amazon account shared by the whole userbase. The Amazon credentials live on
        // AmazonOptions precisely so there is nothing here to spread.
        var present = typeof(ServerCredentials)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Where(name => name.StartsWith("Amazon", StringComparison.Ordinal))
            .ToArray();

        Assert.True(present.Length == 0,
            $"ServerCredentials must not carry {string.Join(", ", present)}. Anything on that type is read from " +
            "the host's environment and copied onto every user's row — which is one Amazon seller account for everybody.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration built the way a host builds it — <c>__</c> for the <c>:</c> separator — so that
    /// what is proved here is the name that goes in <c>/etc/ing-listing-engine/.env</c>.
    /// </summary>
    private static IConfiguration Build(params (string Key, string Value)[] variables) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(variables.ToDictionary(v => v.Key.Replace("__", ":"), v => (string?)v.Value))
            .Build();
}
