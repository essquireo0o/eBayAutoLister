using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// eBay sign-in is four hops — this app, eBay's consent page, the relay on inglisting.com, and this
// app again — and three of them happen in a browser tab this process does not control. Every one of
// them can end without anyone calling back, and they all used to end the same way: nothing
// happened, the UI waited, and the log said "state_mismatch" or nothing at all.
//
// No network in this file. Each ending is decided by a pure function precisely so each ending can
// be pinned here rather than reproduced by hand against a live eBay account.
public class EbaySignInFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    // ── The sessions this app hands out ──────────────────────────────────────

    [Fact]
    public void A_session_that_was_just_issued_is_valid()
    {
        var ledger = new EbayOAuthSessionLedger();
        ledger.Issue("abc", Now);

        Assert.Equal(EbaySessionCheck.Valid, ledger.Check("abc", Now.AddMinutes(2)));
        Assert.True(ledger.HasPending(Now.AddMinutes(2)));
    }

    [Fact]
    public void A_session_nobody_here_issued_is_unknown()
    {
        Assert.Equal(EbaySessionCheck.Unknown, new EbayOAuthSessionLedger().Check("abc", Now));
        Assert.Equal(EbaySessionCheck.Unknown, new EbayOAuthSessionLedger().Check(null, Now));
    }

    // The seller opened the consent page, went to lunch, and came back to it. eBay's code is long
    // dead by then, so this has to end as a timeout with a fresh start — not as a token exchange
    // that fails for reasons nobody can act on.
    [Fact]
    public void A_session_older_than_its_lifetime_is_expired()
    {
        var ledger = new EbayOAuthSessionLedger();
        ledger.Issue("abc", Now);

        Assert.Equal(EbaySessionCheck.Expired,
            ledger.Check("abc", Now + EbayOAuthSessionLedger.Lifetime + TimeSpan.FromMinutes(1)));
        Assert.False(ledger.HasPending(Now + EbayOAuthSessionLedger.Lifetime + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void A_session_that_already_completed_reports_that_it_was_reused()
    {
        var ledger = new EbayOAuthSessionLedger();
        ledger.Issue("abc", Now);
        ledger.Consume("abc", Now.AddSeconds(30));

        Assert.Equal(EbaySessionCheck.AlreadyUsed, ledger.Check("abc", Now.AddSeconds(31)));
        Assert.False(ledger.HasPending(Now.AddSeconds(31)));
    }

    // The bug a single pending-session string had: asking for the auth URL twice — open Settings,
    // go back, open it again — invalidated the first sign-in, so finishing it was rejected as
    // tampering. Both are legitimate, and both have to be able to complete.
    [Fact]
    public void Issuing_a_second_session_does_not_invalidate_the_first()
    {
        var ledger = new EbayOAuthSessionLedger();
        ledger.Issue("first", Now);
        ledger.Issue("second", Now.AddSeconds(5));

        Assert.Equal(EbaySessionCheck.Valid, ledger.Check("first", Now.AddMinutes(1)));
        Assert.Equal(EbaySessionCheck.Valid, ledger.Check("second", Now.AddMinutes(1)));
    }

    // ── A sign-in nobody came back from ──────────────────────────────────────

    [Fact]
    public void A_pending_sign_in_stays_pending_while_it_could_still_come_back()
    {
        var status = new EbaySignInStatus(
            EbaySignInStage.AwaitingConsent, "awaiting_consent", "Waiting.", "Finish on eBay.", Now, Now);

        Assert.Equal(EbaySignInStage.AwaitingConsent, status.AgedAt(Now.AddMinutes(5)).Stage);
    }

    // The browser tab was closed on eBay's consent screen. Nothing will ever call back, and no
    // amount of waiting changes that — so "waiting" has to become an answer.
    [Fact]
    public void A_pending_sign_in_that_nobody_finished_becomes_a_stated_failure()
    {
        var status = new EbaySignInStatus(
            EbaySignInStage.AwaitingConsent, "awaiting_consent", "Waiting.", "Finish on eBay.", Now, Now);

        var aged = status.AgedAt(Now + EbayOAuthSessionLedger.Lifetime + TimeSpan.FromMinutes(1));

        Assert.Equal(EbaySignInStage.Failed, aged.Stage);
        Assert.Equal("sign_in_abandoned", aged.Code);
        Assert.Contains("closed", aged.Message);
        Assert.Contains("Connect eBay Account", aged.NextAction);
    }

    [Fact]
    public void A_finished_sign_in_is_never_aged_out_from_under_the_seller()
    {
        var connected = new EbaySignInStatus(
            EbaySignInStage.Connected, "connected", "Connected.", "Nothing to do.", Now, Now);

        Assert.Equal(EbaySignInStage.Connected, connected.AgedAt(Now.AddDays(30)).Stage);
    }

    // ── The URL, before it is handed out ─────────────────────────────────────

    private static EbayAuthUrlProblem? Check(
        string? clientId = "ING-PRD-id", string? clientSecret = "PRD-secret",
        string? ruName = "ING_Mining-INGPRDid-abc123", bool sandbox = false, string? binding = null) =>
        EbayAuthUrlCheck.Check(clientId, clientSecret, ruName, sandbox, binding);

    [Fact]
    public void A_fully_configured_app_on_the_right_port_gets_a_url()
    {
        Assert.Null(Check());

        var url = EbayAuthUrlCheck.Build(
            "https://auth.ebay.com/oauth2/authorize", "ING-PRD-id", "ING_Mining-INGPRDid-abc123", "state123");

        Assert.StartsWith("https://auth.ebay.com/oauth2/authorize?", url);
        Assert.Contains("client_id=ING-PRD-id", url);
        Assert.Contains("redirect_uri=ING_Mining-INGPRDid-abc123", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("state=state123", url);
        // Every scope the app actually uses. A missing one is a permission the seller is never
        // asked for, which surfaces months later as a single feature returning 403.
        foreach (var scope in EbayAuthUrlCheck.Scopes)
            Assert.Contains(Uri.EscapeDataString(scope), url);
    }

    [Theory]
    // Each of these produced a URL that opened, showed eBay's consent screen, and failed only after
    // the seller had approved everything — on eBay's side, with wording that blames the app.
    [InlineData(null, "secret", "runame", "Client ID")]
    [InlineData("id", null, "runame", "Client Secret")]
    [InlineData("id", "secret", null, "RuName")]
    public void Anything_missing_is_returned_as_the_reason_rather_than_as_a_dead_end_url(
        string? clientId, string? clientSecret, string? ruName, string expectedInAction)
    {
        var problem = Check(clientId, clientSecret, ruName);

        Assert.NotNull(problem);
        Assert.Contains(expectedInAction, problem!.NextAction);
        Assert.NotEmpty(problem.Code);
        Assert.NotEmpty(problem.Reason);
    }

    // The relay redirects to localhost:9332 and has no idea what this app bound. Handing out a URL
    // while on another port buys the seller the whole consent flow and then a blank tab.
    [Fact]
    public void An_app_on_the_wrong_port_is_refused_a_url_and_told_why()
    {
        var problem = Check(binding: "ING AutoLister is serving on http://localhost:5000 instead of port 9332.");

        Assert.NotNull(problem);
        Assert.Equal("wrong_port", problem!.Code);
        Assert.Contains("5000", problem.Reason);
        Assert.Contains(AppPaths.BaseUrl, problem.NextAction);
    }

    // Sandbox never goes through the relay — its RuName points wherever its owner registered it —
    // so the local port is not part of that round trip and must not block it.
    [Fact]
    public void Sandbox_is_not_blocked_by_the_local_port()
    {
        Assert.Null(Check(sandbox: true, binding: "serving on http://localhost:5000"));
    }

    // ── What the relay answers ───────────────────────────────────────────────

    [Fact]
    public void Tokens_in_the_body_are_ready_to_use()
    {
        const string body = """{"access_token":"a","refresh_token":"r","expires_in":7200,"refresh_token_expires_in":47304000}""";

        Assert.Equal(EbayPickupOutcome.Ready, EbayRelayPickup.Classify(200, body));
        Assert.True(EbayRelayPickup.TryReadTokens(body, out var tokens));
        Assert.Equal("a", tokens!.AccessToken);
        Assert.Equal("r", tokens.RefreshToken);
        Assert.Equal(7200, tokens.ExpiresIn);
        Assert.Equal(47304000, tokens.RefreshTokenExpiresIn);
    }

    // eBay redirects the browser here at the same moment the relay is writing the tokens down, so
    // the first read legitimately finds nothing. Failing on that made a race into a broken sign-in.
    [Theory]
    [InlineData(404, "")]
    [InlineData(202, "")]
    [InlineData(425, "")]
    [InlineData(200, "")]
    [InlineData(200, "{\"status\":\"pending\"}")]
    [InlineData(200, "{\"access_token\":\"\"}")]
    public void An_answer_that_has_no_tokens_yet_is_worth_asking_again(int status, string body)
    {
        Assert.Equal(EbayPickupOutcome.NotReady, EbayRelayPickup.Classify(status, body));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(403)]
    [InlineData(410)]
    public void A_relay_that_refuses_the_pickup_is_an_answer_not_a_retry(int status)
    {
        Assert.Equal(EbayPickupOutcome.Rejected, EbayRelayPickup.Classify(status, "{\"error\":\"unknown session\"}"));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void A_broken_relay_is_reported_as_the_relay_not_as_the_account(int status)
    {
        Assert.Equal(EbayPickupOutcome.Unavailable, EbayRelayPickup.Classify(status, "server error"));
    }

    [Fact]
    public void Nothing_answering_at_all_is_unreachable()
    {
        Assert.Equal(EbayPickupOutcome.Unreachable, EbayRelayPickup.Classify(null, null));
    }

    // A hosting provider's HTML error page served with a 200 is the classic one: it is neither
    // tokens nor a failure status, and reading it as either says something untrue.
    [Fact]
    public void A_success_that_is_not_the_token_payload_is_malformed()
    {
        Assert.Equal(EbayPickupOutcome.Malformed, EbayRelayPickup.Classify(200, "<html>502 Bad Gateway</html>"));
    }

    [Fact]
    public void Missing_lifetimes_fall_back_to_eBays_own_rather_than_to_no_expiry()
    {
        // Recording "no expiry" here would stop the proactive refresh from ever firing for this
        // connection, which is the quiet version of the same failure.
        Assert.True(EbayRelayPickup.TryReadTokens("""{"access_token":"a","refresh_token":"r"}""", out var tokens));
        Assert.Equal(7200, tokens!.ExpiresIn);
        Assert.Equal(47304000, tokens.RefreshTokenExpiresIn);
        Assert.Equal("User Access Token", tokens.TokenType);
    }

    // ── Which refusals are allowed to end a connection ───────────────────────

    [Theory]
    [InlineData(400, "{\"error\":\"invalid_grant\",\"error_description\":\"revoked\"}")]
    [InlineData(401, "{\"error\":\"invalid_grant\"}")]
    [InlineData(400, "invalid_grant")]
    public void Only_eBay_saying_invalid_grant_counts_as_a_dead_grant(int status, string body)
    {
        Assert.Equal(EbayRefreshFailure.InvalidGrant, EbayRefreshClassifier.Classify(status, body));
    }

    [Theory]
    [InlineData(400, "{\"error\":\"invalid_request\"}")]
    [InlineData(401, "{\"error\":\"invalid_client\"}")]      // the Client Secret, not the grant
    [InlineData(500, "{\"error\":\"invalid_grant\"}")]       // an error page quoting itself
    [InlineData(503, "maintenance")]
    [InlineData(null, null)]                                  // nothing answered
    public void Everything_else_keeps_the_connection(int? status, string? body)
    {
        Assert.Equal(EbayRefreshFailure.Transient, EbayRefreshClassifier.Classify(status, body));
    }

    // ── The address the relay comes back to ──────────────────────────────────

    [Theory]
    [InlineData("http://localhost:9332")]
    [InlineData("http://[::]:9332")]
    [InlineData("http://127.0.0.1:9332/")]
    public void A_server_on_the_expected_port_has_nothing_to_report(string address)
    {
        Assert.Null(ServerBinding.Check([address], 9332));
    }

    [Fact]
    public void A_server_on_another_port_fails_with_the_ports_named_and_a_fix()
    {
        var problem = ServerBinding.Check(["http://localhost:5000"], 9332);

        Assert.NotNull(problem);
        Assert.Contains("5000", problem);
        Assert.Contains("9332", problem);
        // "Something is wrong" is not a message anyone can act on; the fix has to be in it.
        Assert.Contains("ASPNETCORE_URLS", problem);
    }

    // http://localhost with no port is port 80 — a mismatch that reads, at a glance, as the right
    // address. It is exactly the kind that would otherwise ship.
    [Fact]
    public void A_default_port_url_is_not_mistaken_for_the_expected_one()
    {
        Assert.NotNull(ServerBinding.Check(["http://localhost"], 9332));
        Assert.Equal(80, ServerBinding.PortOf("http://localhost"));
    }

    [Fact]
    public void A_server_that_reports_no_address_is_a_failure_not_a_pass()
    {
        var problem = ServerBinding.Check([], 9332);

        Assert.NotNull(problem);
        Assert.Contains("9332", problem);
    }

    [Fact]
    public void Recording_the_addresses_is_what_makes_the_verdict_usable()
    {
        var binding = new ServerBinding();
        // Before the server has started there is no verdict — which is not the same as a good one.
        Assert.False(binding.Verified);
        Assert.False(binding.IsCorrect);
        Assert.Null(binding.Problem);

        binding.Record(["http://localhost:9332"], 9332);
        Assert.True(binding.Verified);
        Assert.True(binding.IsCorrect);

        binding.Record(["http://localhost:5000"], 9332);
        Assert.True(binding.Verified);
        Assert.False(binding.IsCorrect);
        Assert.NotNull(binding.Problem);
    }
}
