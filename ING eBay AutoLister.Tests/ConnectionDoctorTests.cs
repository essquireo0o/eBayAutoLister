using System.Text.Json;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// ConnectionDoctor exists to replace "disconnected" with a cause, so the thing worth pinning down
// is the mapping from evidence to cause. Every one of these is a case that used to be reported
// identically to a completely different failure with a completely different fix: a revoked eBay
// grant vs. eBay being down, a dead Facebook login vs. Node not being installed, a bad comps key
// vs. a comps host that never answered. Sending someone to re-enter a key that is perfectly fine —
// or to wait out an outage that will never end — is the whole cost of getting these wrong.
//
// No network anywhere in this file: the probes are separated from the judgement precisely so the
// judgement can be tested against hand-built evidence.
public class ConnectionDoctorTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    // ── eBay OAuth ────────────────────────────────────────────────────────────

    private static EbayConnectionFacts Ebay(
        bool hasApp = true,
        bool hasRefresh = true,
        DateTimeOffset? refreshExpiry = null,
        bool refreshAttempted = true,
        bool refreshSucceeded = true,
        int? refreshHttpStatus = null,
        string? refreshError = null,
        int? sellApiStatus = 200,
        string? sellApiError = null) =>
        new(hasApp, hasRefresh, refreshExpiry ?? Now.AddDays(400),
            refreshAttempted, refreshSucceeded, refreshHttpStatus, refreshError,
            sellApiStatus, sellApiError, Now);

    [Fact]
    public void Ebay_without_app_credentials_is_NotConfigured()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(hasApp: false, hasRefresh: false));

        Assert.Equal(ConnectionState.NotConfigured, check.State);
        Assert.False(check.Connected);
        // The fix is a developer-account paste, not a sign-in — saying "connect your account"
        // here sends someone round a loop that cannot complete.
        Assert.Contains("Client ID", check.NextAction);
    }

    [Fact]
    public void Ebay_configured_but_never_signed_in_is_NoSession()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(hasRefresh: false));

        Assert.Equal(ConnectionState.NoSession, check.State);
        Assert.Contains("Connect eBay Account", check.NextAction);
    }

    [Fact]
    public void Ebay_refresh_token_past_its_expiry_is_SessionExpired()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(refreshExpiry: Now.AddDays(-3)));

        Assert.Equal(ConnectionState.SessionExpired, check.State);
        Assert.False(check.Connected);
        Assert.Contains("3 day(s) ago", check.Detail);
    }

    // eBay answering "no" is a revoked grant; a re-login fixes it and nothing else will.
    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    public void Ebay_refresh_refused_by_ebay_is_AuthRejected(int status)
    {
        var check = ConnectionDoctor.ClassifyEbay(
            Ebay(refreshSucceeded: false, refreshHttpStatus: status, sellApiStatus: null));

        Assert.Equal(ConnectionState.AuthRejected, check.State);
        Assert.Contains($"HTTP {status}", check.Reason);
    }

    // The same failed refresh, for the opposite reason. Telling someone to re-authenticate through
    // an outage is how a five-minute wait turns into a re-connect that also fails.
    [Theory]
    [InlineData(503, null)]
    [InlineData(null, "No such host is known.")]
    public void Ebay_refresh_that_never_got_a_verdict_is_Unreachable(int? status, string? error)
    {
        var check = ConnectionDoctor.ClassifyEbay(
            Ebay(refreshSucceeded: false, refreshHttpStatus: status, refreshError: error, sellApiStatus: null));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.DoesNotContain("Connect eBay Account", check.NextAction);
    }

    [Fact]
    public void Ebay_token_that_refreshes_but_is_rejected_by_the_Sell_API_is_AuthRejected()
    {
        // A refresh can succeed while the granted scopes are wrong — the token is real, the
        // account just cannot do what this app asks of it.
        var check = ConnectionDoctor.ClassifyEbay(Ebay(sellApiStatus: 403));

        Assert.Equal(ConnectionState.AuthRejected, check.State);
        Assert.Contains("permission", check.Reason);
    }

    [Fact]
    public void Ebay_Sell_API_that_never_answered_is_Unreachable()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(sellApiStatus: null, sellApiError: "connection timed out"));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("timed out", check.Detail);
    }

    [Fact]
    public void Ebay_Sell_API_server_error_is_Unreachable_not_an_auth_problem()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(sellApiStatus: 500));

        Assert.Equal(ConnectionState.Unreachable, check.State);
    }

    [Fact]
    public void Ebay_live_refresh_plus_200_is_Ok()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay());

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
        Assert.Equal("Nothing to do.", check.NextAction);
    }

    // Still Ok, still connected — but an 18-month token that nobody is warned about expires in the
    // middle of a listing run, and the first symptom is listings failing.
    [Fact]
    public void Ebay_Ok_but_expiring_within_the_warning_window_says_so()
    {
        var check = ConnectionDoctor.ClassifyEbay(Ebay(refreshExpiry: Now.AddDays(5)));

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
        Assert.Contains("expires", check.Reason);
        Assert.Contains("Reconnect", check.NextAction);
    }

    [Fact]
    public void Ebay_expiry_well_outside_the_warning_window_is_not_flagged()
    {
        var check = ConnectionDoctor.ClassifyEbay(
            Ebay(refreshExpiry: Now.AddDays(ConnectionDoctor.RefreshTokenWarnDays + 1)));

        Assert.Equal("Nothing to do.", check.NextAction);
    }

    // ── Saved browser sessions (Facebook + Terapeak) ──────────────────────────

    private static BrowserSessionFacts Session(
        bool runtime = true,
        bool present = true,
        bool readable = true,
        bool probed = true,
        bool cookie = true,
        bool loggedInPage = true,
        bool signIn = false,
        bool challenge = false,
        string? detail = null,
        string? error = null) =>
        new("Facebook session", "Open Settings → Facebook and click Connect.",
            runtime, present, readable, probed, cookie, "Facebook's c_user sign-in cookie",
            loggedInPage, signIn, challenge, detail, error);

    // Without Node/Playwright, "click Connect" cannot work either — offering it is a dead end.
    [Fact]
    public void Session_without_a_browser_runtime_is_NotConfigured()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(runtime: false, present: false));

        Assert.Equal(ConnectionState.NotConfigured, check.State);
        Assert.Contains("Node.js", check.NextAction);
    }

    // Runtime is checked before the session file: with no browser, a present session file is
    // equally unusable, and the fix is the install, not the login.
    [Fact]
    public void Session_missing_runtime_wins_over_a_present_session_file()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(runtime: false, present: true));

        Assert.Equal(ConnectionState.NotConfigured, check.State);
    }

    [Fact]
    public void Session_never_saved_is_NoSession()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(present: false));

        Assert.Equal(ConnectionState.NoSession, check.State);
        Assert.Contains("Connect", check.NextAction);
    }

    [Fact]
    public void Session_file_that_will_not_parse_is_NoSession_and_says_it_is_the_file()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(readable: false));

        Assert.Equal(ConnectionState.NoSession, check.State);
        Assert.Contains("truncated", check.Reason);
    }

    [Fact]
    public void Session_that_lost_its_login_cookie_is_SessionExpired()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(cookie: false));

        Assert.Equal(ConnectionState.SessionExpired, check.State);
        Assert.Contains("c_user", check.Reason);
    }

    // A probe that crashed proves nothing. Reporting it as an expired login sends someone through
    // a six-minute interactive re-login they did not need.
    [Fact]
    public void Session_probe_that_did_not_finish_is_Unreachable_not_expired()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(
            Session(probed: false, loggedInPage: false, error: "Probe exceeded 45s."));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("says nothing", check.Reason);
        Assert.Contains("45s", check.Detail);
    }

    [Fact]
    public void Session_bounced_to_a_sign_in_page_is_SessionExpired()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(
            Session(loggedInPage: false, signIn: true, detail: "Navigated to /login (HTTP 200)."));

        Assert.Equal(ConnectionState.SessionExpired, check.State);
        Assert.False(check.Connected);
    }

    [Fact]
    public void Session_that_serves_the_logged_in_page_is_Ok()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(
            Session(detail: "Navigated to /marketplace/ (HTTP 200)."));

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
    }

    // Measured against the live eBay session: a headless navigation drew /splashui/challenge while
    // the very same cookies fetched /sh/research successfully. A bot check is not a logged-out
    // page, and no amount of reconnecting clears one — calling it "expired" would send the seller
    // through an interactive re-login that fixes nothing.
    [Fact]
    public void Session_answered_with_a_bot_check_is_Unreachable_not_SessionExpired()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(
            Session(loggedInPage: false, challenge: true, detail: "Navigated to /splashui/challenge (HTTP 200)."));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("bot detection", check.Reason);
        Assert.DoesNotContain("expired", check.Reason);
    }

    // The two probes ask the same question in the styles the two sites refuse in opposite
    // directions, so a "yes" from either settles it — otherwise Facebook's 400 to a bare fetch and
    // eBay's bot check to a real navigation each condemn a session that is perfectly alive.
    [Fact]
    public void One_probe_reaching_the_logged_in_page_outweighs_the_other_being_turned_away()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(
            Session(loggedInPage: true, challenge: true, detail: "Navigated to /splashui/challenge (HTTP 200); fetched /sh/research (HTTP 200)."));

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
    }

    // Neither probe said anything usable. That is still not "expired".
    [Fact]
    public void Session_with_no_verdict_from_either_probe_is_Unreachable()
    {
        var check = ConnectionDoctor.ClassifyBrowserSession(Session(loggedInPage: false));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("no verdict", check.Reason);
    }

    // ── The storageState cookie signal ────────────────────────────────────────

    [Fact]
    public void A_named_cookie_with_an_empty_value_does_not_count_as_signed_in()
    {
        // Facebook leaves c_user behind with no value after a logout, so presence alone is not a
        // login — this is the difference between "connected" and a search that silently returns
        // nothing.
        using var doc = JsonDocument.Parse(
            """{"cookies":[{"name":"c_user","value":"","domain":".facebook.com"}]}""");

        Assert.False(ConnectionDoctor.HasLoginCookie(doc.RootElement, "c_user", "facebook.com"));
    }

    [Fact]
    public void A_named_cookie_with_a_value_on_the_right_domain_counts()
    {
        using var doc = JsonDocument.Parse(
            """{"cookies":[{"name":"c_user","value":"100000000000000","domain":".facebook.com"}]}""");

        Assert.True(ConnectionDoctor.HasLoginCookie(doc.RootElement, "c_user", "facebook.com"));
    }

    [Fact]
    public void A_login_cookie_for_a_different_site_does_not_count()
    {
        using var doc = JsonDocument.Parse(
            """{"cookies":[{"name":"c_user","value":"123","domain":".example.com"}]}""");

        Assert.False(ConnectionDoctor.HasLoginCookie(doc.RootElement, "c_user", "facebook.com"));
    }

    // eBay has no single durable sign-in cookie, so Terapeak's file-level signal is only "there
    // are eBay cookies here at all" — the verdict that counts is the page probe.
    [Fact]
    public void With_no_cookie_name_any_cookie_on_the_domain_counts()
    {
        using var doc = JsonDocument.Parse(
            """{"cookies":[{"name":"dp1","value":"abc","domain":".ebay.com"}]}""");

        Assert.True(ConnectionDoctor.HasLoginCookie(doc.RootElement, null, "ebay.com"));
        Assert.False(ConnectionDoctor.HasLoginCookie(doc.RootElement, null, "facebook.com"));
    }

    [Fact]
    public void A_storage_state_with_no_cookies_array_is_not_signed_in()
    {
        using var doc = JsonDocument.Parse("""{"origins":[]}""");

        Assert.False(ConnectionDoctor.HasLoginCookie(doc.RootElement, "c_user", "facebook.com"));
    }

    // ── Hosted comps API ──────────────────────────────────────────────────────

    private static HostedCompsFacts Comps(
        bool hasUrl = true,
        bool hasKey = true,
        int? status = 200,
        int? resultCount = 5,
        bool timedOut = false,
        string? host = "inglisting.com",
        string? error = null) =>
        new(hasUrl, hasKey, status, resultCount, timedOut, host, error);

    [Fact]
    public void Comps_with_nothing_configured_is_NotConfigured()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(hasUrl: false, hasKey: false, status: null, resultCount: null, host: null));

        Assert.Equal(ConnectionState.NotConfigured, check.State);
        Assert.Contains("local database", check.Reason);
    }

    // A half-configured API is worth naming separately: it looks configured on the settings screen
    // and fails on every request.
    [Fact]
    public void Comps_with_a_url_but_no_key_is_NotConfigured_and_names_the_missing_key()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(hasKey: false, status: null, resultCount: null));

        Assert.Equal(ConnectionState.NotConfigured, check.State);
        Assert.Contains("key isn't", check.Reason);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Comps_rejecting_the_key_is_AuthRejected(int status)
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(status: status, resultCount: null));

        Assert.Equal(ConnectionState.AuthRejected, check.State);
        Assert.Contains("re-enter the comps API key", check.NextAction);
    }

    [Fact]
    public void Comps_that_timed_out_is_Unreachable_and_does_not_blame_the_key()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(status: null, resultCount: null, timedOut: true));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.DoesNotContain("key", check.NextAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Comps_host_that_never_answered_is_Unreachable()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(
            Comps(status: null, resultCount: null, error: "No such host is known. (comps.example)"));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("No such host", check.Detail);
    }

    [Fact]
    public void Comps_server_error_is_Unreachable()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(status: 500, resultCount: null));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.Contains("500", check.Reason);
    }

    // A 200 carrying a hosting provider's error page is not a working API, however cheerful the
    // status code is.
    [Fact]
    public void Comps_200_with_an_unparseable_body_is_Unreachable()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(
            Comps(resultCount: null, error: "Response wasn't the expected JSON."));

        Assert.Equal(ConnectionState.Unreachable, check.State);
        Assert.False(check.Connected);
    }

    [Fact]
    public void Comps_returning_rows_is_Ok()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(resultCount: 5));

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
        Assert.Contains("5 row(s)", check.Detail);
    }

    // Zero rows for one probe keyword is a working API with nothing matching it. Calling that
    // broken sends someone to re-check a key that is perfectly fine.
    [Fact]
    public void Comps_answering_normally_with_zero_rows_is_still_Ok()
    {
        var check = ConnectionDoctor.ClassifyHostedComps(Comps(resultCount: 0));

        Assert.Equal(ConnectionState.Ok, check.State);
        Assert.True(check.Connected);
        Assert.Equal("Nothing to do.", check.NextAction);
    }

    // ── The probe script ──────────────────────────────────────────────────────
    // JavaScript embedded in C#, so nothing else in the build checks a word of it — and the one
    // thing it got wrong was only visible when run against a live session.

    private static string ProbeScript(string? challenge = "splashui|captcha") =>
        ConnectionDoctor.BuildSessionProbeScript(
            @"C:\pw", @"C:\data\facebook-session.json",
            "https://www.facebook.com/marketplace/", "login|checkpoint", challenge, "input[name='pass']");

    // The regression this exists for, measured both ways against live sessions: Facebook answers a
    // bare ctx.request.get with HTTP 400 while a real navigation gets 200 and the Marketplace page;
    // eBay does the exact reverse, serving the request fine and bot-checking the navigation. Either
    // probe alone therefore condemns a working session, so both have to be here.
    [Fact]
    public void Probe_asks_both_ways_a_real_navigation_and_a_bare_fetch()
    {
        var script = ProbeScript();

        Assert.Contains("page.goto(", script);
        Assert.Contains("ctx.request.get(", script);
    }

    // Some sites serve the sign-in form in place without ever changing the URL, so the URL alone
    // is not enough to say a session survived.
    [Fact]
    public void Probe_treats_a_login_form_on_the_page_as_a_dead_session()
    {
        var script = ProbeScript();

        Assert.Contains("input[name='pass']", script);
        Assert.Contains("loginForm", script);
    }

    [Fact]
    public void Probe_substitutes_every_placeholder_and_escapes_windows_paths()
    {
        var script = ProbeScript();

        Assert.DoesNotContain("%%", script);
        // A single backslash would be an escape sequence inside the JS string literal, and the
        // session path would silently point somewhere that does not exist.
        Assert.Contains(@"C:\\data\\facebook-session.json", script);
    }

    // A site with no bot-check page must get a literal null, never an empty regex: new RegExp('')
    // matches every URL there is, which would report every single session as challenged.
    [Fact]
    public void Probe_with_no_challenge_pattern_emits_null_not_an_empty_regex()
    {
        var script = ProbeScript(challenge: null);

        Assert.Contains("const CHALLENGE = null;", script);
        Assert.DoesNotContain("new RegExp('')", script);
    }

    [Fact]
    public void Probe_with_a_challenge_pattern_compiles_it_into_a_regex()
    {
        Assert.Contains("const CHALLENGE = new RegExp('splashui|captcha');", ProbeScript());
    }

    // A probe that crashes must produce a verdict of "we don't know", not a silent empty result —
    // that silence is what this whole service was written to remove.
    [Fact]
    public void Probe_always_writes_a_json_verdict_even_when_it_throws()
    {
        var script = ProbeScript();

        Assert.Contains("catch (e)", script);
        Assert.Contains("process.stdout.write(JSON.stringify(out))", script);
    }

    // ── The payload itself ────────────────────────────────────────────────────

    // The endpoint is read by a person, and a bare "3" tells them nothing. The enum carries a
    // JsonStringEnumConverter for exactly this.
    [Fact]
    public void State_serialises_as_its_name_not_a_number()
    {
        var json = JsonSerializer.Serialize(ConnectionDoctor.ClassifyHostedComps(Comps(status: 401, resultCount: null)));

        Assert.Contains("\"AuthRejected\"", json);
    }

    // Every state must be reachable and every check must carry the two strings a person acts on —
    // an empty Reason or NextAction is the generic failure this service replaced.
    [Fact]
    public void Every_state_is_reachable_and_always_carries_a_reason_and_a_next_action()
    {
        ConnectionCheck[] checks =
        [
            ConnectionDoctor.ClassifyEbay(Ebay(hasApp: false, hasRefresh: false)),
            ConnectionDoctor.ClassifyEbay(Ebay(hasRefresh: false)),
            ConnectionDoctor.ClassifyEbay(Ebay(refreshExpiry: Now.AddDays(-1))),
            ConnectionDoctor.ClassifyEbay(Ebay(refreshSucceeded: false, refreshHttpStatus: 400, sellApiStatus: null)),
            ConnectionDoctor.ClassifyEbay(Ebay(sellApiStatus: null)),
            ConnectionDoctor.ClassifyEbay(Ebay()),
            ConnectionDoctor.ClassifyBrowserSession(Session(runtime: false)),
            ConnectionDoctor.ClassifyBrowserSession(Session(present: false)),
            ConnectionDoctor.ClassifyBrowserSession(Session(cookie: false)),
            ConnectionDoctor.ClassifyBrowserSession(Session(probed: false, loggedInPage: false)),
            ConnectionDoctor.ClassifyBrowserSession(Session(loggedInPage: false, signIn: true)),
            ConnectionDoctor.ClassifyBrowserSession(Session(loggedInPage: false, challenge: true)),
            ConnectionDoctor.ClassifyBrowserSession(Session()),
            ConnectionDoctor.ClassifyHostedComps(Comps(hasUrl: false, hasKey: false)),
            ConnectionDoctor.ClassifyHostedComps(Comps(status: 401, resultCount: null)),
            ConnectionDoctor.ClassifyHostedComps(Comps(status: null, resultCount: null)),
            ConnectionDoctor.ClassifyHostedComps(Comps()),
        ];

        foreach (var check in checks)
        {
            Assert.False(string.IsNullOrWhiteSpace(check.Name));
            Assert.False(string.IsNullOrWhiteSpace(check.Reason), $"{check.Name}/{check.State} has no reason");
            Assert.False(string.IsNullOrWhiteSpace(check.NextAction), $"{check.Name}/{check.State} has no next action");
            // Connected and a non-Ok state would put a green light on a broken connection.
            Assert.Equal(check.State == ConnectionState.Ok, check.Connected);
        }

        Assert.Equal(
            Enum.GetValues<ConnectionState>().ToHashSet(),
            checks.Select(c => c.State).ToHashSet());
    }
}
