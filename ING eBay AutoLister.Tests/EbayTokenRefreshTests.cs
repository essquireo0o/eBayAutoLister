using System.Net;
using System.Text;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>Answers each request from a queue, so a retry can be given a different answer.</summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpResponseMessage>> _answers = new();

    public int Calls { get; private set; }

    public ScriptedHttpHandler Then(HttpStatusCode status, string body)
    {
        _answers.Enqueue(() => new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    /// <summary>A connection that never happened — no status, no body, no verdict on anything.</summary>
    public ScriptedHttpHandler ThenNetworkFailure(string message = "No such host is known.")
    {
        _answers.Enqueue(() => throw new HttpRequestException(message));
        return this;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Calls++;
        if (_answers.Count == 0) throw new InvalidOperationException("Unexpected extra HTTP call.");
        return Task.FromResult(_answers.Dequeue()());
    }
}

// The most expensive judgement in the app, exercised end to end through the real refresh path.
//
// The stored refresh token IS the connection: it lasts 18 months, and the only thing that can
// replace it is the seller personally going through eBay's consent screen again. Discarding it
// because the Wi-Fi dropped, or because eBay returned a 500, turns "try again in a minute" into a
// re-login — and there is no way back from that. So exactly one thing may destroy it: eBay
// answering a 4xx that explicitly says invalid_grant. These tests pin both halves of that.
public class EbayTokenRefreshTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ebay_refresh_{Guid.NewGuid():N}.json");

    private const string StoredRefreshToken = "v1.1-stored-refresh-token";

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".bak" })
            if (File.Exists(p)) File.Delete(p);
        GC.SuppressFinalize(this);
    }

    /// <summary>A store that has been connected: app credentials plus a refresh token, on disk.</summary>
    private CredentialsStore ConnectedStore()
    {
        var store = new CredentialsStore(_path);
        store.Save(new CredentialsPatch
        {
            EbayClientId     = "ING-PRD-clientid",
            EbayClientSecret = "PRD-clientsecret",
            EbayRefreshToken = StoredRefreshToken,
            EbayUserToken    = "old-access-token",
        });
        return store;
    }

    private static EbayService Service(CredentialsStore store, ScriptedHttpHandler handler) =>
        new(store, new StubHttpClientFactory(handler), new ActionLog());

    /// <summary>Re-reads credentials.json from scratch — what the next app start would see.</summary>
    private CredentialsStore Reloaded() => new(_path);

    // ── Transient failures keep the connection ───────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":\"server_error\"}")]      // eBay is unwell
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>maintenance</html>")]           // ...and not even in JSON
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_request\"}")]            // a bad request, not a dead grant
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}")]           // the Client Secret, not the grant
    public async Task A_failure_that_is_not_invalid_grant_leaves_the_refresh_token_alone(
        HttpStatusCode status, string body)
    {
        var store = ConnectedStore();
        var result = await Service(store, new ScriptedHttpHandler().Then(status, body)).TryProactiveRefreshAsync();

        Assert.False(result.Succeeded);
        Assert.Equal((int)status, result.HttpStatus);

        // The whole point: still connected, in memory and on disk.
        Assert.Equal(StoredRefreshToken, store.GetRefreshToken());
        Assert.Equal(StoredRefreshToken, Reloaded().GetRefreshToken());
        Assert.False(store.IsEbayReauthRequired);
        Assert.False(Reloaded().IsEbayReauthRequired);
    }

    [Fact]
    public async Task A_network_failure_leaves_the_refresh_token_alone()
    {
        var store = ConnectedStore();
        var result = await Service(store, new ScriptedHttpHandler().ThenNetworkFailure()).TryProactiveRefreshAsync();

        Assert.False(result.Succeeded);
        // Nothing answered, so there is no status to report — and no grounds to blame the account.
        Assert.Null(result.HttpStatus);
        Assert.Equal(StoredRefreshToken, Reloaded().GetRefreshToken());
        Assert.False(Reloaded().IsEbayReauthRequired);
    }

    // A 5xx body that happens to contain the words is an error page quoting itself, not eBay
    // adjudicating this grant. Believing it would throw away a working connection over an outage.
    [Fact]
    public async Task Invalid_grant_wording_in_a_server_error_does_not_count()
    {
        var store = ConnectedStore();
        await Service(store, new ScriptedHttpHandler()
            .Then(HttpStatusCode.BadGateway, "{\"error\":\"invalid_grant\"}")).TryProactiveRefreshAsync();

        Assert.Equal(StoredRefreshToken, Reloaded().GetRefreshToken());
        Assert.False(Reloaded().IsEbayReauthRequired);
    }

    // The retry that the keeping is for: a failure followed by a success ends connected, with no
    // seller involvement at all.
    [Fact]
    public async Task A_kept_refresh_token_still_works_on_the_next_attempt()
    {
        var store = ConnectedStore();
        var handler = new ScriptedHttpHandler()
            .Then(HttpStatusCode.ServiceUnavailable, "{\"error\":\"server_error\"}")
            .Then(HttpStatusCode.OK, """{"access_token":"fresh-access","expires_in":7200,"token_type":"User Access Token"}""");
        var ebay = Service(store, handler);

        Assert.False((await ebay.TryProactiveRefreshAsync()).Succeeded);
        Assert.True((await ebay.TryProactiveRefreshAsync()).Succeeded);

        Assert.Equal("fresh-access", Reloaded().GetUserToken());
        Assert.Equal(StoredRefreshToken, Reloaded().GetRefreshToken());
    }

    // ── invalid_grant is the one thing that ends it ──────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\",\"error_description\":\"the user has revoked access\"}")]
    [InlineData(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_grant\"}")]
    // eBay's gateway sometimes wraps the payload on the way through; the marker still decides it.
    [InlineData(HttpStatusCode.BadRequest, "<html><body>error: invalid_grant</body></html>")]
    public async Task Invalid_grant_clears_the_tokens_and_records_that_a_new_sign_in_is_needed(
        HttpStatusCode status, string body)
    {
        var store = ConnectedStore();
        var result = await Service(store, new ScriptedHttpHandler().Then(status, body)).TryProactiveRefreshAsync();

        Assert.False(result.Succeeded);
        Assert.Equal("", store.GetRefreshToken());
        Assert.Equal("", store.GetUserToken());
        Assert.True(store.IsEbayReauthRequired);

        // Persisted, because "restart it" is the first thing anyone tries, and the reason has to
        // still be there afterwards — otherwise the app comes back looking merely unconnected.
        var reloaded = Reloaded();
        Assert.True(reloaded.IsEbayReauthRequired);
        Assert.Equal("", reloaded.GetRefreshToken());
        Assert.Contains("invalid_grant", reloaded.Get().EbayReauthReason);
    }

    // What the seller actually sees. A revoked grant has to reach the Settings panel as "sign in
    // again", not as the "no refresh token stored" that revoking one necessarily leaves behind.
    [Fact]
    public async Task A_revoked_grant_reaches_the_ConnectionDoctor_as_reconnect_not_as_never_connected()
    {
        var store = ConnectedStore();
        await Service(store, new ScriptedHttpHandler()
            .Then(HttpStatusCode.BadRequest, "{\"error\":\"invalid_grant\"}")).TryProactiveRefreshAsync();

        var c = store.Get();
        var check = ConnectionDoctor.ClassifyEbay(new EbayConnectionFacts(
            HasAppCredentials: true, HasRefreshToken: false, RefreshTokenExpiresAt: null,
            RefreshAttempted: false, RefreshSucceeded: false, RefreshHttpStatus: null, RefreshError: null,
            SellApiStatus: null, SellApiError: null, Now: DateTimeOffset.UtcNow,
            ReauthRequiredAt: c.EbayReauthRequiredAt, ReauthReason: c.EbayReauthReason));

        Assert.Equal(ConnectionState.AuthRejected, check.State);
        Assert.False(check.Connected);
        Assert.Contains("Connect eBay Account", check.NextAction);
        Assert.Contains("revoked", check.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── A successful refresh ─────────────────────────────────────────────────

    [Fact]
    public async Task A_successful_refresh_stores_the_new_token_with_its_expiry()
    {
        var store = ConnectedStore();
        var before = DateTimeOffset.UtcNow;

        var result = await Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.OK,
            """{"access_token":"fresh-access","expires_in":7200,"token_type":"User Access Token"}"""))
            .TryProactiveRefreshAsync();

        Assert.True(result.Succeeded);

        var reloaded = Reloaded();
        Assert.Equal("fresh-access", reloaded.GetUserToken());
        Assert.NotNull(reloaded.Get().EbayTokenExpiresAt);
        Assert.InRange(reloaded.Get().EbayTokenExpiresAt!.Value,
            before.AddSeconds(7200 - 60), DateTimeOffset.UtcNow.AddSeconds(7200 + 60));
        Assert.False(reloaded.IsAccessTokenExpired());
    }

    // eBay saying yes is proof the grant is alive, whatever an earlier failure recorded about it.
    [Fact]
    public async Task A_successful_refresh_retires_an_earlier_reauth_warning()
    {
        var store = ConnectedStore();
        store.MarkEbayReauthRequired("recorded earlier");
        store.Save(new CredentialsPatch { EbayRefreshToken = StoredRefreshToken });

        await Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.OK,
            """{"access_token":"fresh-access","expires_in":7200}""")).TryProactiveRefreshAsync();

        Assert.False(Reloaded().IsEbayReauthRequired);
    }

    [Fact]
    public async Task Nothing_is_asked_of_eBay_when_there_is_no_refresh_token()
    {
        var store = new CredentialsStore(_path);
        store.Save(new CredentialsPatch { EbayClientId = "id", EbayClientSecret = "secret" });

        // An empty script throws on any call at all, which is the assertion.
        var handler = new ScriptedHttpHandler();
        await Service(store, handler).ProactiveTokenRefreshAsync();

        Assert.Equal(0, handler.Calls);
    }

    // ── The tokens survive a restart ─────────────────────────────────────────

    // Everything above depends on credentials.json actually being the same file next time. The
    // fixed data home exists because it wasn't: bin\Debug, a copied build and an installed app were
    // three different homes, so a seller who ran a different build found eBay disconnected.
    [Fact]
    public void Tokens_round_trip_through_the_file_with_both_expiries_intact()
    {
        var store = new CredentialsStore(_path);
        store.SaveOAuthTokensFull("access-abc", "refresh-xyz", 7200, 47304000, "User Access Token");

        var access  = store.Get().EbayTokenExpiresAt;
        var refresh = store.Get().EbayRefreshTokenExpiresAt;

        var reloaded = Reloaded();
        Assert.Equal("access-abc",  reloaded.GetUserToken());
        Assert.Equal("refresh-xyz", reloaded.GetRefreshToken());
        Assert.Equal("User Access Token", reloaded.Get().EbayTokenType);
        Assert.Equal(access,  reloaded.Get().EbayTokenExpiresAt);
        Assert.Equal(refresh, reloaded.Get().EbayRefreshTokenExpiresAt);

        // And it is usable on the other side, which is the only thing the seller notices.
        Assert.False(reloaded.IsAccessTokenExpired());
        Assert.True(reloaded.HasValidRefreshToken());
        Assert.False(reloaded.ShouldRefreshAccessToken());
    }

    // A refresh token with no recorded expiry is the shape of every connection made before eBay's
    // refresh_token_expires_in was being stored. It must survive as a connection, not as a blank.
    [Fact]
    public void A_refresh_token_saved_without_an_expiry_still_reloads_as_connected()
    {
        var store = new CredentialsStore(_path);
        store.SaveOAuthTokens("access-abc", "refresh-xyz");

        var reloaded = Reloaded();
        Assert.Equal("refresh-xyz", reloaded.GetRefreshToken());
        Assert.Null(reloaded.Get().EbayRefreshTokenExpiresAt);
        Assert.True(reloaded.HasValidRefreshToken());
        // Unknown access-token age plus something to renew it with: top it up rather than trust it.
        Assert.True(reloaded.ShouldRefreshAccessToken());
    }

    [Fact]
    public void Disconnecting_leaves_nothing_behind_for_the_next_start_to_find()
    {
        var store = new CredentialsStore(_path);
        store.SaveOAuthTokensFull("access-abc", "refresh-xyz", 7200, 47304000, "User Access Token");
        store.ClearEbayTokens();

        var reloaded = Reloaded();
        Assert.Equal("", reloaded.GetUserToken());
        Assert.Equal("", reloaded.GetRefreshToken());
        Assert.Null(reloaded.Get().EbayTokenExpiresAt);
        Assert.False(reloaded.IsEbayReauthRequired);
    }
}
