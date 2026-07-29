using System.Text.Json;
using System.Text.Json.Serialization;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How a connection is broken, which is the part nobody could previously see. The distinction
/// that matters is between the four failures with four different fixes: never set up
/// (<see cref="NotConfigured"/>), set up but never signed in (<see cref="NoSession"/>), signed in
/// once and the sign-in has since died (<see cref="SessionExpired"/>), credentials refused right
/// now (<see cref="AuthRejected"/>), and nothing wrong with the credentials at all — the other end
/// did not answer (<see cref="Unreachable"/>). Collapsing those into one "disconnected" chip is
/// why a network blip and a dead login looked identical.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConnectionState
{
    NotConfigured,
    NoSession,
    SessionExpired,
    Unreachable,
    AuthRejected,
    Ok
}

/// <param name="Reason">One sentence, addressed to the seller, saying what is actually wrong.</param>
/// <param name="NextAction">The single thing to do about it — never "check your settings".</param>
/// <param name="Detail">
/// Non-secret corroboration: HTTP status codes, expiry timestamps, result counts. Never a token,
/// cookie, key or response body — this record is serialised straight out of an HTTP endpoint.
/// </param>
public sealed record ConnectionCheck(
    string Name,
    bool Connected,
    ConnectionState State,
    string Reason,
    string NextAction,
    string? Detail = null);

// ── Inputs to the classifiers ─────────────────────────────────────────────────
// The probes below are separated from the judgement they feed on purpose: every state in
// ConnectionState is reachable from a hand-built facts record, so ConnectionDoctorTests can pin
// down all six mappings for all four connections without a browser, a token or a socket.

/// <param name="RefreshHttpStatus">eBay's status when eBay answered and refused; null when nothing answered.</param>
/// <param name="SellApiStatus">Status of the authenticated Sell API GET; null when nothing answered.</param>
public sealed record EbayConnectionFacts(
    bool HasAppCredentials,
    bool HasRefreshToken,
    DateTimeOffset? RefreshTokenExpiresAt,
    bool RefreshAttempted,
    bool RefreshSucceeded,
    int? RefreshHttpStatus,
    string? RefreshError,
    int? SellApiStatus,
    string? SellApiError,
    DateTimeOffset Now);

/// <summary>
/// Shared by Facebook and Terapeak because they are the same mechanism — a saved browser
/// storageState replayed headlessly — and therefore have the same failure modes.
/// </summary>
/// <param name="CookieSignalPresent">The site's own signed-in marker was in the saved state.</param>
/// <param name="LoggedInPageServed">The real, logged-in page came back from at least one probe.</param>
/// <param name="SignInRequested">The site asked for a sign-in — a redirect to one, or the form itself.</param>
/// <param name="ChallengeServed">
/// The site served an automated-traffic check ("verify you're human") instead of the page. Not a
/// verdict on the login at all — treating it as one is how a working session gets thrown away.
/// </param>
public sealed record BrowserSessionFacts(
    string Name,
    string ConnectAction,
    bool RuntimeAvailable,
    bool SessionFilePresent,
    bool SessionFileReadable,
    bool ProbeCompleted,
    bool CookieSignalPresent,
    string CookieSignalLabel,
    bool LoggedInPageServed,
    bool SignInRequested,
    bool ChallengeServed,
    string? ProbeDetail,
    string? ProbeError);

/// <param name="HttpStatus">Null means no response at all — DNS failure, refused connection or timeout.</param>
public sealed record HostedCompsFacts(
    bool HasUrl,
    bool HasKey,
    int? HttpStatus,
    int? ResultCount,
    bool TimedOut,
    string? Host,
    string? Error);

/// <summary>
/// Answers, for each of the four connections this app depends on, "is it connected, and if not,
/// exactly why". It exists because every one of them previously failed either silently or
/// generically: a dead Terapeak session, an unreachable comps API and a never-configured key all
/// surfaced as the same empty result, so there was no way to tell which one to go and fix.
///
/// Every check here is a real one. "The session file exists" and "a URL is configured" are the
/// answers that made this necessary in the first place, so each connection is exercised end to
/// end: eBay does a live token refresh and an authenticated API call, the two browser sessions are
/// replayed in a headless context and asked whether the site still considers them signed in, and
/// the comps API is really queried. That makes this endpoint slow (two Chrome launches) and
/// unsuitable for polling — it is a "why is this broken" button, not a status light.
/// </summary>
public sealed class ConnectionDoctor(
    CredentialsStore creds,
    EbayService ebay,
    TerapeakService terapeak,
    FacebookMarketplaceService facebook,
    IHttpClientFactory httpFactory,
    ActionLog log)
{
    /// <summary>Ceiling on one headless session probe. A page load and one request, so generous already.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(45);

    /// <summary>The four checks run concurrently — they share nothing, and run serially they take
    /// close to two minutes, which is long enough that people stop pressing the button.</summary>
    public async Task<IReadOnlyList<ConnectionCheck>> CheckAllAsync(CancellationToken ct = default)
    {
        var results = await Task.WhenAll(
            CheckEbayAsync(ct),
            CheckFacebookAsync(ct),
            CheckTerapeakAsync(ct),
            CheckHostedCompsAsync(ct));

        foreach (var r in results.Where(r => !r.Connected))
            log.Add("Warning", $"Connection check: {r.Name} — {r.State}", r.Reason);

        return results;
    }

    // ── eBay OAuth ────────────────────────────────────────────────────────────

    public async Task<ConnectionCheck> CheckEbayAsync(CancellationToken ct = default)
    {
        var c = creds.Get();
        var hasApp = !string.IsNullOrWhiteSpace(c.EbayClientId) && !string.IsNullOrWhiteSpace(c.EbayClientSecret);
        var hasRefresh = !string.IsNullOrWhiteSpace(c.EbayRefreshToken);

        // Don't spend a live token refresh proving something already known from disk.
        if (!hasApp || !hasRefresh)
            return ClassifyEbay(new EbayConnectionFacts(
                hasApp, hasRefresh, c.EbayRefreshTokenExpiresAt,
                RefreshAttempted: false, RefreshSucceeded: false, RefreshHttpStatus: null, RefreshError: null,
                SellApiStatus: null, SellApiError: null, Now: DateTimeOffset.UtcNow));

        var refresh = await ebay.ProbeTokenRefreshAsync();

        // The API call is only meaningful on the token the refresh just stored — if the refresh
        // failed, the stale token's 401 would report the wrong cause.
        int? sellStatus = null;
        string? sellError = null;
        if (refresh.Succeeded)
            (sellStatus, sellError) = await ebay.ProbeSellApiAsync(ct);

        return ClassifyEbay(new EbayConnectionFacts(
            hasApp, hasRefresh, c.EbayRefreshTokenExpiresAt,
            refresh.Attempted, refresh.Succeeded, refresh.HttpStatus, refresh.Error,
            sellStatus, sellError, DateTimeOffset.UtcNow));
    }

    /// <summary>Refresh tokens last 18 months and do not renew themselves, so a seller who is not
    /// warned first finds out when listing stops working.</summary>
    public const int RefreshTokenWarnDays = 30;

    public static ConnectionCheck ClassifyEbay(EbayConnectionFacts f)
    {
        const string name = "eBay OAuth";
        const string reconnect = "Open Settings → eBay and click Connect eBay Account to sign in again.";

        if (!f.HasAppCredentials)
            return new(name, false, ConnectionState.NotConfigured,
                "The eBay app Client ID and Client Secret haven't been entered, so no token can be issued or refreshed.",
                "Open Settings → eBay → Advanced and paste the Client ID and Client Secret from your eBay developer account.");

        if (!f.HasRefreshToken)
            return new(name, false, ConnectionState.NoSession,
                "No eBay refresh token is stored — this account has never finished the OAuth sign-in, or it was disconnected.",
                reconnect);

        if (f.RefreshTokenExpiresAt is { } expired && expired <= f.Now)
            return new(name, false, ConnectionState.SessionExpired,
                $"The stored eBay refresh token expired on {expired:u} and eBay will not renew it — a fresh sign-in is the only way back.",
                reconnect,
                $"Expired {(f.Now - expired).TotalDays:0} day(s) ago.");

        if (f.RefreshAttempted && !f.RefreshSucceeded)
            // 4xx is eBay looking at the grant and refusing it; anything else (5xx, no answer at
            // all) is eBay's end being unwell, which no amount of re-authenticating fixes.
            return f.RefreshHttpStatus is >= 400 and < 500
                ? new(name, false, ConnectionState.AuthRejected,
                    $"eBay refused the stored refresh token with HTTP {f.RefreshHttpStatus} — the grant has been revoked or the app credentials no longer match it.",
                    reconnect,
                    $"Token endpoint returned HTTP {f.RefreshHttpStatus}.")
                : new(name, false, ConnectionState.Unreachable,
                    "The eBay token endpoint didn't give a usable answer, so the stored login couldn't be tested — this is eBay's end or the network, not your account.",
                    "Try again in a minute; if it keeps failing, check that this machine can reach api.ebay.com.",
                    f.RefreshHttpStatus is { } s ? $"Token endpoint returned HTTP {s}." : Truncate(f.RefreshError));

        if (f.SellApiStatus is null)
            return new(name, false, ConnectionState.Unreachable,
                "The token refreshed fine, but the authenticated Sell API call got no response at all, so eBay can't be reached right now.",
                "Try again in a minute; if it keeps failing, check that this machine can reach api.ebay.com.",
                Truncate(f.SellApiError));

        if (f.SellApiStatus is 401 or 403)
            return new(name, false, ConnectionState.AuthRejected,
                $"eBay issued a token but then rejected it with HTTP {f.SellApiStatus} — the connected account is missing the selling permissions this app asks for.",
                "Reconnect the eBay account and accept every permission on eBay's consent screen.",
                $"Sell API returned HTTP {f.SellApiStatus}.");

        if (f.SellApiStatus is < 200 or >= 300)
            return new(name, false, ConnectionState.Unreachable,
                $"The token is valid, but the Sell API answered HTTP {f.SellApiStatus} instead of 200 — that's an eBay-side problem, not a login one.",
                "Try again in a few minutes; eBay's Sell API is returning errors for a valid token.",
                $"Sell API returned HTTP {f.SellApiStatus}.");

        var expiringSoon = f.RefreshTokenExpiresAt is { } exp && exp - f.Now < TimeSpan.FromDays(RefreshTokenWarnDays);
        var detail = f.RefreshTokenExpiresAt is { } e
            ? $"Sell API HTTP {f.SellApiStatus}; refresh token valid until {e:u} ({(e - f.Now).TotalDays:0} days)."
            : $"Sell API HTTP {f.SellApiStatus}; no refresh-token expiry recorded.";

        return expiringSoon
            ? new(name, true, ConnectionState.Ok,
                "Connected and working, but the refresh token expires within a month and eBay will not renew it on its own.",
                "Reconnect the eBay account before it expires — once it does, listing stops until you sign in again.",
                detail)
            : new(name, true, ConnectionState.Ok,
                "Connected — a live token refresh succeeded and an authenticated Sell API call came back 200.",
                "Nothing to do.",
                detail);
    }

    // ── Facebook + Terapeak: saved browser sessions ───────────────────────────

    public async Task<ConnectionCheck> CheckFacebookAsync(CancellationToken ct = default) =>
        ClassifyBrowserSession(await ProbeBrowserSessionAsync(
            name: "Facebook session",
            connectAction: "Open Settings → Facebook and click Connect — it opens a browser window for a one-time login to your own account.",
            sessionPath: facebook.SessionPath,
            // c_user is Facebook's own signed-in marker, and it is what the login flow itself waits
            // for (see FacebookMarketplaceService) — a saved state without it never held a login.
            cookieName: "c_user",
            cookieDomainFilter: "facebook.com",
            cookieSignalLabel: "Facebook's c_user sign-in cookie",
            probeUrl: FacebookMarketplaceSelectors.PicksUrl,
            signInUrlPattern: "login|checkpoint|recover",
            // Facebook has no "verify you're human" interstitial in this flow; a checkpoint is a
            // real block on the account and belongs with the sign-in signals above.
            challengeUrlPattern: null,
            loginFormSelector: "input[name='pass']",
            ct));

    public async Task<ConnectionCheck> CheckTerapeakAsync(CancellationToken ct = default) =>
        ClassifyBrowserSession(await ProbeBrowserSessionAsync(
            name: "Terapeak session",
            connectAction: "Open Settings → Terapeak and click Connect — it opens a browser window for a one-time eBay sign-in.",
            sessionPath: terapeak.SessionPath,
            // eBay has no single durable "signed in" cookie the way Facebook does, so the file-level
            // signal is only "this state carries eBay cookies at all". The verdict that counts is
            // the page probe below: reaching /sh/research without being bounced to sign-in.
            cookieName: null,
            cookieDomainFilter: "ebay.com",
            cookieSignalLabel: "saved eBay cookies",
            probeUrl: "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD",
            signInUrlPattern: "signin",
            // eBay answers automated-looking traffic with a bot-check splash — the same one
            // TerapeakService's login script names CAPTCHA rather than treating as a failure. A
            // headless probe draws it readily, and it says nothing about the saved login.
            challengeUrlPattern: "splashui|captcha|challenge",
            loginFormSelector: "#userid, input[name='userid'], #pass, input[name='pass']",
            ct));

    public static ConnectionCheck ClassifyBrowserSession(BrowserSessionFacts f)
    {
        // Runtime first: without Node/Playwright neither testing nor creating a session is
        // possible, and telling someone to click Connect when Connect cannot work is a dead end.
        if (!f.RuntimeAvailable)
            return new(f.Name, false, ConnectionState.NotConfigured,
                $"{f.Name} needs a real browser to run, and Node.js with the Playwright package isn't installed on this machine.",
                "Install Node.js, then run \"npm install -g playwright\" and restart the app.");

        if (!f.SessionFilePresent)
            return new(f.Name, false, ConnectionState.NoSession,
                $"There's no saved {f.Name.Replace(" session", "")} login on this machine — it has never been connected, or it was disconnected.",
                f.ConnectAction);

        if (!f.SessionFileReadable)
            return new(f.Name, false, ConnectionState.NoSession,
                "The saved session file is there but isn't readable as a browser session, so it can't be replayed — it was most likely truncated by a crash mid-save.",
                f.ConnectAction);

        if (!f.CookieSignalPresent)
            return new(f.Name, false, ConnectionState.SessionExpired,
                $"The saved session no longer contains {f.CookieSignalLabel}, so whatever login it once held is gone.",
                f.ConnectAction);

        if (!f.ProbeCompleted)
            return new(f.Name, false, ConnectionState.Unreachable,
                "The saved session couldn't be tested — the headless browser didn't finish, so this says nothing about whether the login is still good.",
                "Try again; if it keeps failing, close any other automated browser windows and check this machine's connection.",
                Truncate(f.ProbeError));

        // Checked before the sign-in signal on purpose: the probes ask the same question two ways
        // and the sites disagree about which way they will answer, so one probe reaching the real
        // logged-in page settles it even when the other was turned away.
        if (f.LoggedInPageServed)
            return new(f.Name, true, ConnectionState.Ok,
                "Connected — the saved login was replayed in a headless browser and the site served the logged-in page.",
                "Nothing to do.",
                f.ProbeDetail);

        if (f.SignInRequested)
            return new(f.Name, false, ConnectionState.SessionExpired,
                "The saved session was replayed in a browser, but the site asked it to sign in again instead of serving the logged-in page — the login has expired or been revoked.",
                f.ConnectAction,
                f.ProbeDetail);

        // A bot check is not a logged-out page, and re-connecting does not clear one. Saying
        // "expired" here sends the seller through an interactive re-login to fix nothing.
        if (f.ChallengeServed)
            return new(f.Name, false, ConnectionState.Unreachable,
                "The site answered with an automated-traffic check instead of the page, so the saved login could be neither confirmed nor ruled out — that's the site's bot detection, not your account.",
                "Try again in a few minutes. If lookups keep coming back empty afterwards, reconnect from Settings.",
                f.ProbeDetail);

        return new(f.Name, false, ConnectionState.Unreachable,
            "The saved session was replayed, but the site returned neither the logged-in page nor a sign-in page, so there's no verdict to report.",
            "Try again in a few minutes; if it persists, reconnect from Settings.",
            f.ProbeDetail ?? Truncate(f.ProbeError));
    }

    /// <summary>
    /// Replays a saved storageState headlessly and asks the site itself whether it is still signed
    /// in. Deliberately not a cookie-expiry calculation: a cookie that has not expired is routinely
    /// dead anyway (password change, "log out everywhere", a security checkpoint), and that is
    /// precisely the case a file-presence check gets wrong.
    /// </summary>
    private async Task<BrowserSessionFacts> ProbeBrowserSessionAsync(
        string name, string connectAction, string sessionPath,
        string? cookieName, string cookieDomainFilter, string cookieSignalLabel,
        string probeUrl, string signInUrlPattern, string? challengeUrlPattern,
        string loginFormSelector, CancellationToken ct)
    {
        var runtimeOk = Directory.Exists(NodeRuntime.PlaywrightDir)
                     && (File.Exists(NodeRuntime.NodeExe) || NodeRuntime.NodeExe == "node");

        BrowserSessionFacts Facts(
            bool present, bool readable, bool completed, bool cookie,
            bool loggedIn = false, bool signIn = false, bool challenge = false,
            string? detail = null, string? error = null) =>
            new(name, connectAction, runtimeOk, present, readable, completed, cookie, cookieSignalLabel,
                loggedIn, signIn, challenge, detail, error);

        if (!runtimeOk || !File.Exists(sessionPath))
            return Facts(File.Exists(sessionPath), readable: false, completed: false, cookie: false);

        // Read the cookie signal in C# rather than in the script: a corrupt file must be reported
        // as a corrupt file, not as a browser launch failure, and those have different fixes.
        bool cookiePresent;
        try
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(sessionPath, ct));
            cookiePresent = HasLoginCookie(doc.RootElement, cookieName, cookieDomainFilter);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return Facts(present: true, readable: false, completed: false, cookie: false);
        }

        if (!cookiePresent)
            return Facts(present: true, readable: true, completed: false, cookie: false);

        var script = BuildSessionProbeScript(
            NodeRuntime.PlaywrightDir, sessionPath, probeUrl, signInUrlPattern, challengeUrlPattern, loginFormSelector);

        NodeRunResult run;
        try
        {
            run = await NodeRuntime.RunAsync(script, ProbeTimeout, "connection_probe");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return Facts(true, true, completed: false, cookie: true, error: ex.Message);
        }

        if (run.TimedOut)
            return Facts(true, true, false, true, error: $"Probe exceeded {ProbeTimeout.TotalSeconds:0}s.");
        if (string.IsNullOrWhiteSpace(run.StdOut))
            return Facts(true, true, false, true,
                error: string.IsNullOrWhiteSpace(run.StdErr) ? "No output from the session probe." : run.StdErr);

        try
        {
            var root = JsonDocument.Parse(run.StdOut).RootElement;
            bool Flag(string prop) => root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;
            int Num(string prop) => root.TryGetProperty(prop, out var v) && v.TryGetInt32(out var i) ? i : 0;
            // Paths only — a signed-in URL can carry identifiers in its query string.
            string Path_(string prop) => root.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

            var ran = Flag("ran");
            var error = root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

            // Both probes are reported, because when they disagree the disagreement is the finding.
            var detail = ran
                ? $"Navigated to {Path_("navPath")} (HTTP {Num("navStatus")})"
                  + (Path_("reqPath").Length > 0 ? $"; fetched {Path_("reqPath")} (HTTP {Num("reqStatus")})." : ".")
                : null;

            return Facts(true, true, ran, true,
                loggedIn: Flag("loggedInPage"), signIn: Flag("signIn"), challenge: Flag("challenge"),
                detail: detail, error: error);
        }
        catch (JsonException)
        {
            return Facts(true, true, false, true, error: "Session probe returned output that wasn't valid JSON.");
        }
    }

    /// <summary>
    /// True when the saved storageState carries the site's sign-in marker. With
    /// <paramref name="cookieName"/> null the test is only "are there cookies for this site at
    /// all", which is the most a site without a single durable login cookie can offer.
    /// </summary>
    public static bool HasLoginCookie(JsonElement storageState, string? cookieName, string domainFilter)
    {
        if (!storageState.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var cookie in cookies.EnumerateArray())
        {
            var domain = cookie.TryGetProperty("domain", out var d) ? d.GetString() ?? "" : "";
            if (!domain.Contains(domainFilter, StringComparison.OrdinalIgnoreCase)) continue;

            if (cookieName is null) return true;

            var cName = cookie.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            var value = cookie.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            if (cName == cookieName && value.Length > 0) return true;
        }
        return false;
    }

    /// <summary>
    /// Builds the headless session probe. Public, like TerapeakService.BuildLoginScript, because it
    /// is JavaScript embedded in C# — nothing in the build type-checks a word of it, and the one
    /// thing it got wrong (see the remarks below) was invisible until it was run against a live
    /// session. ConnectionDoctorTests pins the choices that matter.
    /// </summary>
    public static string BuildSessionProbeScript(
        string playwrightDir, string sessionPath, string probeUrl,
        string signInUrlPattern, string? challengeUrlPattern, string loginFormSelector) =>
        ProbeScript
            .Replace("%%PW%%", NodeRuntime.JsPath(playwrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(sessionPath))
            .Replace("%%URL%%", probeUrl)
            .Replace("%%SIGNIN%%", signInUrlPattern)
            // A site with no bot-check page gets a literal null, not a regex that matches nothing —
            // an empty pattern matches EVERY url, which would report every session as challenged.
            .Replace("%%CHALLENGE%%", string.IsNullOrWhiteSpace(challengeUrlPattern)
                ? "null"
                : $"new RegExp('{challengeUrlPattern}')")
            .Replace("%%LOGINFORM%%", loginFormSelector);

    /// <summary>
    /// Loads the saved session into a headless context and asks the site, two different ways,
    /// whether it still considers that session signed in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two ways because the two sites refuse opposite probes, and each refusal looked exactly like
    /// a dead login. Both of these were measured against live, working sessions:
    /// </para>
    /// <list type="bullet">
    /// <item>Facebook refuses <c>ctx.request.get</c> — a fetch carrying the session cookies but no
    /// browser fingerprint — with HTTP 400, while a real navigation to the same URL returns 200 and
    /// the Marketplace page.</item>
    /// <item>eBay does the reverse: <c>ctx.request.get</c> returns /sh/research and HTTP 200, while
    /// a headless navigation draws the /splashui/challenge bot check instead.</item>
    /// </list>
    /// <para>
    /// So neither probe alone can clear a session, and a "no" from either one proves nothing on its
    /// own. Only a "yes" is conclusive, which is why the request probe runs as a fallback and why
    /// the classifier checks <c>loggedInPage</c> before anything else.
    /// </para>
    /// <para>
    /// The bot check is reported separately from a sign-in for the same reason: it is not a verdict
    /// on the login, and reconnecting does not clear one. A false "expired" is the most expensive
    /// thing this service can say — it sends the seller through a six-minute interactive re-login
    /// to fix a connection that was never broken, which is worse than the generic failure it
    /// replaced.
    /// </para>
    /// <para>
    /// Emits JSON on stdout and never throws out of the script: a crashed probe is "we don't know",
    /// which is a different verdict again from "the session is dead".
    /// </para>
    /// </remarks>
    private const string ProbeScript = """
        const { chromium } = require('%%PW%%');
        (async () => {
          const out = { ran: false, loggedInPage: false, signIn: false, challenge: false,
                        navStatus: 0, navPath: '', reqStatus: 0, reqPath: '', error: null };
          const SIGNIN = new RegExp('%%SIGNIN%%');
          const CHALLENGE = %%CHALLENGE%%;
          const pathOf = (u) => { try { return new URL(u).pathname; } catch (_) { return String(u || ''); } };
          let browser = null;
          try {
            browser = await chromium.launch({ channel: 'chrome', headless: true });
            const ctx = await browser.newContext({ storageState: '%%SESSION%%' });

            // 1) Exactly what the real scrape does: a full page navigation.
            try {
              const page = await ctx.newPage();
              const resp = await page.goto('%%URL%%', { waitUntil: 'domcontentloaded', timeout: 30000 });
              const url = String(page.url() || '');
              out.navStatus = resp ? resp.status() : 0;
              out.navPath = pathOf(url);
              // A sign-in form on the page is the same answer as a redirect to one — some sites
              // serve the login in place without ever changing the URL.
              const loginForm = await page.$("%%LOGINFORM%%").catch(() => null);
              if (loginForm || SIGNIN.test(url)) out.signIn = true;
              else if (CHALLENGE && CHALLENGE.test(url)) out.challenge = true;
              else if (out.navStatus === 0 || out.navStatus < 400) out.loggedInPage = true;
            } catch (e) {
              out.error = String((e && e.message) || e).split('\n')[0];
            }

            // 2) Same cookies, no browser fingerprint. Only run when the navigation gave no
            //    answer, and only ever able to UPGRADE the verdict — a site that refuses this
            //    style of request has not told us anything about the login.
            if (!out.loggedInPage && !out.signIn) {
              try {
                const res = await ctx.request.get('%%URL%%', { timeout: 25000 });
                const url = String(res.url() || '');
                out.reqStatus = res.status();
                out.reqPath = pathOf(url);
                if (SIGNIN.test(url)) out.signIn = true;
                else if (res.ok() && !(CHALLENGE && CHALLENGE.test(url))) {
                  out.loggedInPage = true;
                  out.challenge = false;
                }
              } catch (_) {}
            }
            out.ran = true;
          } catch (e) {
            out.error = String((e && e.message) || e).split('\n')[0];
          }
          try { if (browser) await browser.close(); } catch (_) {}
          process.stdout.write(JSON.stringify(out));
        })();
        """;

    // ── Hosted comps API ──────────────────────────────────────────────────────

    public async Task<ConnectionCheck> CheckHostedCompsAsync(CancellationToken ct = default)
    {
        var c = creds.Get();
        var hasUrl = !string.IsNullOrWhiteSpace(c.MarketCompsApiUrl);
        var hasKey = !string.IsNullOrWhiteSpace(c.MarketCompsApiKey);
        if (!hasUrl || !hasKey)
            return ClassifyHostedComps(new HostedCompsFacts(hasUrl, hasKey, null, null, false, null, null));

        // Host only. The query URL carries the API key, so it must never reach a log or a payload.
        var host = Uri.TryCreate(c.MarketCompsApiUrl, UriKind.Absolute, out var parsed) ? parsed.Host : null;
        var url = $"{c.MarketCompsApiUrl.TrimEnd('/')}?key={Uri.EscapeDataString(c.MarketCompsApiKey)}&q=antminer&limit=1";

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            using var resp = await http.GetAsync(url, ct);
            var status = (int)resp.StatusCode;

            if (!resp.IsSuccessStatusCode)
                return ClassifyHostedComps(new HostedCompsFacts(true, true, status, null, false, host, null));

            int? count = null;
            try
            {
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                    count = results.GetArrayLength();
            }
            catch (JsonException)
            {
                // 200 with a body that isn't the documented shape is not a working API — most
                // often a hosting provider's error page served with a 200.
                return ClassifyHostedComps(new HostedCompsFacts(true, true, status, null, false, host,
                    "Response wasn't the expected JSON."));
            }

            return ClassifyHostedComps(new HostedCompsFacts(true, true, status, count, false, host, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (TaskCanceledException)
        {
            return ClassifyHostedComps(new HostedCompsFacts(true, true, null, null, TimedOut: true, host, null));
        }
        catch (HttpRequestException ex)
        {
            return ClassifyHostedComps(new HostedCompsFacts(true, true, null, null, false, host, ex.Message));
        }
    }

    public static ConnectionCheck ClassifyHostedComps(HostedCompsFacts f)
    {
        const string name = "Hosted comps API";
        var where = f.Host is null ? "the comps API" : f.Host;

        if (!f.HasUrl || !f.HasKey)
            return new(name, false, ConnectionState.NotConfigured,
                f is { HasUrl: true, HasKey: false }
                    ? "The hosted comps API URL is set but its key isn't, so every request would be refused."
                    : "No hosted comps API is configured, so sold-price lookups fall back to whatever is in the local database.",
                "Open Settings → Sold comps and enter the comps API URL and key.");

        if (f.HttpStatus is 401 or 403)
            return new(name, false, ConnectionState.AuthRejected,
                $"{where} answered HTTP {f.HttpStatus} — it's reachable, but it rejected the configured API key.",
                "Open Settings → Sold comps and re-enter the comps API key.",
                $"HTTP {f.HttpStatus} from {where}.");

        if (f.TimedOut)
            return new(name, false, ConnectionState.Unreachable,
                $"{where} didn't answer within the timeout, so nothing can be said about the key — the host or the network is the problem.",
                "Try again shortly; if it persists, check that the comps host is up and that this machine can reach it.",
                "Request timed out.");

        if (f.HttpStatus is null)
            return new(name, false, ConnectionState.Unreachable,
                $"{where} couldn't be contacted at all — the name didn't resolve or the connection was refused.",
                "Check the comps API URL is right and that this machine has a working connection.",
                Truncate(f.Error));

        if (f.HttpStatus is < 200 or >= 300 || f.ResultCount is null)
            return new(name, false, ConnectionState.Unreachable,
                $"{where} is reachable but answered HTTP {f.HttpStatus} instead of returning comps, so it isn't serving data right now.",
                "Try again shortly; if it persists, check the comps API host's own logs.",
                f.Error is null ? $"HTTP {f.HttpStatus} from {where}." : $"HTTP {f.HttpStatus}: {Truncate(f.Error)}");

        // Zero rows for a probe query is a working API with nothing matching that one keyword —
        // reporting it as broken would send someone to re-check a key that is perfectly fine.
        return new(name, true, ConnectionState.Ok,
            f.ResultCount > 0
                ? "Connected — a live query returned sold comps from the hosted database."
                : "Connected — the API answered normally, though the probe keyword matched no rows.",
            "Nothing to do.",
            $"HTTP {f.HttpStatus} from {where}; {f.ResultCount} row(s) for the probe query.");
    }

    private static string? Truncate(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Length <= 200 ? text : text[..200] + "…";
}
