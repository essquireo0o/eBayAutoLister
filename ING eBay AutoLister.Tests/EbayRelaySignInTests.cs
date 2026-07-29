using System.Net;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.WebUtilities;

namespace ING_eBay_AutoLister.Tests;

// The last hop of the sign-in, driven through the real service: eBay has sent the seller back
// through the relay, and this app has a few seconds to turn that into stored tokens or into a
// sentence explaining why it couldn't. Every path here used to end in one of three ways —
// "state_mismatch", "pickup_failed", or a request that simply never returned — and the browser tab
// showed the seller whichever of those it got.
public class EbayRelaySignInTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"ebay_relay_{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        foreach (var p in new[] { _path, _path + ".bak" })
            if (File.Exists(p)) File.Delete(p);
        GC.SuppressFinalize(this);
    }

    private const string Tokens =
        """{"access_token":"access-abc","refresh_token":"refresh-xyz","expires_in":7200,"refresh_token_expires_in":47304000}""";

    private CredentialsStore ConfiguredStore()
    {
        var store = new CredentialsStore(_path);
        store.Save(new CredentialsPatch
        {
            EbayClientId     = "ING-PRD-id",
            EbayClientSecret = "PRD-secret",
            EbayRuName       = "ING_Mining-INGPRDid-abc123",
        });
        return store;
    }

    /// <summary>Starts a sign-in the way the seller does, and returns the session eBay will hand back.</summary>
    private static string StartSignIn(EbayService ebay)
    {
        var result = ebay.CreateAuthorizationUrl();
        Assert.True(result.Ok);
        var query = QueryHelpers.ParseQuery(new Uri(result.Url!).Query);
        return query["state"].ToString();
    }

    private static EbayService Service(CredentialsStore store, ScriptedHttpHandler handler) =>
        new(store, new StubHttpClientFactory(handler), new ActionLog());

    [Fact]
    public async Task A_sign_in_the_relay_answers_first_time_ends_connected_and_stored()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.OK, Tokens));
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal(EbaySignInStage.Connected, status.Stage);
        Assert.Equal("connected", status.Code);

        // On disk, so the next start is still connected.
        var reloaded = new CredentialsStore(_path);
        Assert.Equal("access-abc",  reloaded.GetUserToken());
        Assert.Equal("refresh-xyz", reloaded.GetRefreshToken());
    }

    // The redirect routinely beats the relay's own write, so the first read finding nothing is
    // normal — and used to be fatal.
    [Fact]
    public async Task A_relay_that_is_merely_slow_still_completes()
    {
        var store = ConfiguredStore();
        var handler = new ScriptedHttpHandler()
            .Then(HttpStatusCode.NotFound, "")
            .Then(HttpStatusCode.OK, Tokens);
        var ebay = Service(store, handler);
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal(EbaySignInStage.Connected, status.Stage);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task A_relay_that_is_down_says_so_and_does_not_blame_the_account()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.BadGateway, "bad gateway"));
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal(EbaySignInStage.Failed, status.Stage);
        Assert.Equal("relay_unavailable", status.Code);
        Assert.Contains("relay", status.Message);
        // Nothing about the seller's setup is wrong, so nothing about it should be touched.
        Assert.DoesNotContain("Client", status.NextAction);
    }

    [Fact]
    public async Task A_relay_that_refuses_the_pickup_stops_immediately_rather_than_retrying()
    {
        var store = ConfiguredStore();
        var handler = new ScriptedHttpHandler().Then(HttpStatusCode.Forbidden, "{\"error\":\"unknown session\"}");
        var ebay = Service(store, handler);
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal("pickup_rejected", status.Code);
        Assert.Equal(1, handler.Calls);
    }

    // The same link opened twice — back button, a double redirect, a refresh. The first attempt
    // worked, so there is nothing wrong to report and nothing to undo.
    [Fact]
    public async Task Replaying_a_completed_sign_in_reports_it_as_already_connected()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.OK, Tokens));
        var session = StartSignIn(ebay);

        await ebay.CompleteRelaySignInAsync(session, "pickup-1");
        var second = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal(EbaySignInStage.Connected, second.Stage);
        Assert.Equal("already_connected", second.Code);
        // Still connected: a replay must never disturb the tokens the first attempt stored.
        Assert.Equal("refresh-xyz", new CredentialsStore(_path).GetRefreshToken());
    }

    [Fact]
    public async Task A_session_this_app_never_issued_is_named_as_such()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler());

        var status = await ebay.CompleteRelaySignInAsync("not-a-session-we-issued", "pickup-1");

        Assert.Equal("state_mismatch", status.Code);
        Assert.Contains("restarted", status.Message);
    }

    [Fact]
    public async Task A_callback_with_no_pickup_reference_is_not_left_hanging()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler());
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "");

        Assert.Equal("no_pickup", status.Code);
        Assert.Equal(EbaySignInStage.Failed, status.Stage);
    }

    // Every failure has to still be answerable afterwards: the redirect that caused it carries only
    // a code, and the seller reads the sentence from /api/ebay/status once the page has loaded.
    [Fact]
    public async Task The_reason_a_sign_in_failed_outlives_the_redirect_that_reported_it()
    {
        var store = ConfiguredStore();
        var ebay = Service(store, new ScriptedHttpHandler().Then(HttpStatusCode.BadGateway, "bad gateway"));
        var session = StartSignIn(ebay);

        var status = await ebay.CompleteRelaySignInAsync(session, "pickup-1");

        Assert.Equal(status.Code, ebay.SignInStatus.Code);
        Assert.Equal(status.Message, ebay.SignInStatus.Message);
        Assert.NotEmpty(ebay.SignInStatus.NextAction);
    }

    // Asking for a URL that cannot be built is not a sign-in attempt, and must not leave the app
    // reporting one in progress.
    [Fact]
    public void An_unconfigured_app_gets_the_reason_instead_of_a_url()
    {
        var store = new CredentialsStore(_path);
        var ebay = Service(store, new ScriptedHttpHandler());

        var result = ebay.CreateAuthorizationUrl();

        Assert.False(result.Ok);
        Assert.Null(result.Url);
        Assert.Equal("no_client_id", result.Problem!.Code);
        Assert.Equal(EbaySignInStage.Failed, ebay.SignInStatus.Stage);
    }

    [Fact]
    public void Starting_a_sign_in_records_that_one_is_in_flight()
    {
        var ebay = Service(ConfiguredStore(), new ScriptedHttpHandler());
        StartSignIn(ebay);

        Assert.Equal(EbaySignInStage.AwaitingConsent, ebay.SignInStatus.Stage);
        Assert.NotNull(ebay.SignInStatus.StartedAt);
    }
}
