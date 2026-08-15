using System.Text.Json;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Facebook Marketplace is where local used inventory actually turns over, and it has no
/// public search API of any kind — not a restricted one, none. It's a regular logged-in
/// website authenticated by browser cookies, exactly like Terapeak, so this service takes
/// exactly the Terapeak approach: pop ONE visible browser window for the seller to log into
/// their OWN Facebook account, save that session to disk, then reuse it headlessly to read
/// local search results off the rendered page.
///
/// Posture — identical to TerapeakService, deliberately:
///   • User-driven only. Nothing here runs on a timer, in the background, or as a side effect
///     of another feature. A search happens because a person clicked Search.
///   • The seller's own account, their own credentials, typed into the real Facebook login in
///     a real browser. This app never sees, handles or stores a password.
///   • One search = one page load, human-paced. No crawling, no enumeration, no bulk export.
///   • A dead session is reported, never silently re-authenticated (see SearchAsync).
///
/// Every selector and URL lives in FacebookMarketplaceSelectors; all text interpretation lives
/// in FacebookMarketplaceParser. This file is only browser plumbing, so when Facebook reshuffles
/// its DOM the fix is a one-line string edit next door.
///
/// As an <see cref="ILocalSupplySource"/> this is the expensive, session-based end of the range —
/// CraigslistService is the same interface with no login and a plain HTTPS GET behind it. The
/// arbitrage pipeline treats both identically.
/// </summary>
public class FacebookMarketplaceService : ILocalSupplySource
{
    private readonly string _sessionPath;
    private readonly ActionLog log;
    private readonly FacebookSoldStore? soldStore;

    /// <summary>
    /// Where the session lives, and it is named through <see cref="AppPaths"/> rather than composed
    /// from <c>ContentRootPath</c>.
    /// </summary>
    /// <remarks>
    /// Those two resolve to the same folder today — <c>ContentRootPath</c> is set to
    /// <see cref="AppPaths.DataHome"/> at startup — but only by arrangement, and a hosting property
    /// is exactly the kind of thing that gets changed for an unrelated reason. When it moved before,
    /// bin\Debug, a copied build and the installed app each looked in their own folder, and a seller
    /// who ran a different build was told to connect Facebook again for a session that was sitting
    /// on disk the whole time. Saying the path outright removes that possibility.
    /// </remarks>
    public FacebookMarketplaceService(ActionLog log, FacebookSoldStore? soldStore = null)
        : this(AppPaths.FacebookSessionPath, log, soldStore) { }

    /// <summary>Explicit-path constructor, for tests and for anything that keeps its session elsewhere.</summary>
    public FacebookMarketplaceService(string sessionPath, ActionLog log, FacebookSoldStore? soldStore = null)
    {
        _sessionPath  = sessionPath;
        this.log      = log;
        this.soldStore = soldStore;
    }

    // Interlocked, not a volatile bool: "check the flag, then set it" is two steps, and the
    // connect button is bound in several places at once (Settings card, sourcing banner, the
    // inline chip on a search result). Clicks that land together all read "not running" and
    // each launch a browser — which is how one click could put five login windows on screen.
    private int _loginInProgress;

    /// <summary>
    /// What the saved session on disk actually is — five states, not <c>File.Exists</c>. A zero-byte
    /// or truncated file used to count as connected, which turned "your login is broken" into a
    /// Marketplace search that quietly found nothing. See <see cref="FacebookSessionFile"/>.
    /// </summary>
    public FacebookSessionStatus Session => FacebookSessionFile.Inspect(_sessionPath);

    public bool IsConnected => Session.CanSearch;

    /// <summary>Where the saved storageState lives. Exposed so ConnectionDoctor can load it into a
    /// headless context and actually test it, rather than settling for "the file is there".</summary>
    public string SessionPath => _sessionPath;

    public bool IsLoginInProgress => Volatile.Read(ref _loginInProgress) == 1;
    public string? LastLoginError { get; private set; }

    // ── ILocalSupplySource ────────────────────────────────────────────────────
    public string Id => FacebookMarketplaceParser.SourceId;
    public string Label => FacebookMarketplaceParser.SourceLabel;
    public bool RequiresConnection => true;

    /// <summary>
    /// True for a live session AND for an expired one — which is not the same thing as
    /// <see cref="IsConnected"/>, deliberately.
    /// </summary>
    /// <remarks>
    /// LocalSupplyGuard answers an unavailable source with <c>not_connected</c> before the search
    /// runs, which is right for a machine that has never been connected and wrong for one whose
    /// login has died: those are different sentences and different buttons. Saying "available" here
    /// lets <see cref="SearchAsync"/> give the expired session its own answer — instantly, without
    /// launching a browser, because the verdict was reached by reading a file.
    /// </remarks>
    public bool IsAvailable => Session.State is FacebookSessionState.Valid or FacebookSessionState.Expired;

    public string AvailabilityNote => Session.State switch
    {
        FacebookSessionState.Valid => "Connected — searches run in a headless browser, so give them a minute.",
        FacebookSessionState.Expired => "The saved login has expired — reconnect in Settings.",
        _ => "Needs a one-time login to your own Facebook account (Settings).",
    };

    // ── One-time interactive login ─────────────────────────────────

    public (bool Started, string Message) StartLogin()
    {
        // Claim the slot and test the claim in one operation, so a second caller loses the race
        // outright rather than both deciding they won it.
        if (Interlocked.CompareExchange(ref _loginInProgress, 1, 0) == 1)
            return (false, "A login window is already open — finish logging in there.");

        LastLoginError = null;
        _ = Task.Run(RunLoginProcessAsync);
        return (true, "A browser window just opened — log into Facebook there. If you don't see it, Alt+Tab or check the taskbar for it. It closes itself once you're in.");
    }

    /// <summary>Ceiling on the whole login process. Above the script's own six-minute wait, so the
    /// script is always what ends it and the outcome is a named token rather than a killed process.</summary>
    public static TimeSpan LoginProcessTimeout => TimeSpan.FromMinutes(7);

    private async Task RunLoginProcessAsync()
    {
        var script = BuildLoginScript(NodeRuntime.PlaywrightDir, _sessionPath);

        try
        {
            // The session's own folder, made before node is asked to write into it: on a machine
            // where the data home had never been created, the save threw at the last instant of a
            // completed login and reported it as a cancelled one.
            var directory = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var run = await NodeRuntime.RunAsync(script, LoginProcessTimeout, "fbmarket_login",
                beforeStart: LoginWindowFocus.PrepareForLoginWindow);

            if (run.TimedOut)
            {
                LastLoginError = $"No login completed within {LoginProcessTimeout.TotalMinutes:0} minutes.";
                log.Add("Warning", "Facebook login timed out", LastLoginError);
                return;
            }

            if (run.StdOut == "SAVED")
            {
                // A fresh session starts clean: leaving the marker behind would have the very next
                // search report the login that just succeeded as expired.
                FacebookSessionFile.ClearExpiredMarker(_sessionPath);
                log.Add("Info", "Facebook Marketplace connected", "Session saved — local Marketplace search is now available.");
            }
            else
            {
                // The script's outcome tokens are for this method, not for a human — surfacing
                // them raw is how the UI ended up reporting "Facebook connect failed: CANCELLED".
                LastLoginError = run.StdOut switch
                {
                    "CANCELLED" => "Login window was closed before signing in — click Connect and stay in the window until it closes itself.",
                    var s when s.StartsWith("NAVFAIL:") =>
                        $"Facebook's login page wouldn't load ({s["NAVFAIL:".Length..].Trim()}). "
                        + "This is Facebook's end, not your account — try again in a minute.",
                    // No Chrome, no Playwright, or a Chrome already holding the profile. Three
                    // machine problems with three different fixes, and none of them is "you closed
                    // the window", which is what all three used to be reported as.
                    var s when FailureTranslator.IsBrowserLaunchFailure(s) => DescribeBrowserFailure(s),
                    var s when !string.IsNullOrWhiteSpace(s) => s,
                    _ => string.IsNullOrWhiteSpace(run.StdErr)
                        ? "Login window was closed before signing in."
                        : run.StdErr,
                };
                log.Add("Warning", "Facebook login not completed", LastLoginError);
            }
        }
        catch (Exception ex)
        {
            // Most commonly a missing/stale Node.js on PATH — surfaced rather than left as a
            // UI that just keeps claiming a browser window is open somewhere.
            LastLoginError = $"Couldn't launch the login browser: {ex.Message}";
            log.Add("Error", "Facebook login failed to start", LastLoginError);
        }
        finally
        {
            Volatile.Write(ref _loginInProgress, 0);
        }
    }

    /// <summary>One sentence for a browser that would not start, from the label the script emitted.</summary>
    private static string DescribeBrowserFailure(string raw)
    {
        var failure = FailureTranslator.Translate(new InvalidOperationException(raw), FailureDomain.Browser);
        return $"{failure.Headline}. {failure.WhatToDo}";
    }

    public void Disconnect()
    {
        // The backup and the expired marker go too. Leaving the .bak behind means the next
        // inspection recovers from it and reports the account the seller just disconnected as
        // connected — see FacebookSessionFile.Delete.
        FacebookSessionFile.Delete(_sessionPath);
        log.Add("Info", "Facebook Marketplace disconnected", "Saved session removed.");
    }

    // ── Headless search using the saved session ────────────────────────────────

    /// <summary>
    /// Searches local Marketplace supply for <paramref name="query"/> around
    /// <paramref name="zip"/> within <paramref name="radiusMiles"/>. One page load per call,
    /// only ever from a user action.
    /// </summary>
    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default)
    {
        var snappedRadius = FacebookMarketplaceParser.NearestSupportedRadius(radiusMiles);

        // Read the file before spending a browser launch on it. Every state below is decided in
        // microseconds, and each one used to end up as either "connect Facebook" (wrong for a login
        // that has died) or a scrape that replayed nothing and reported no local supply.
        if (GateOnSession(query, zip, snappedRadius) is { } blocked) return blocked;

        if (string.IsNullOrWhiteSpace(query))
            return Fail("error", query, zip, snappedRadius, "Enter something to search for.");

        var script = BuildSearchScript(
            NodeRuntime.PlaywrightDir, _sessionPath,
            FacebookMarketplaceSelectors.ToJson(query, snappedRadius, zip ?? ""));

        NodeRunResult run;
        try
        {
            // Setting the location drives a real dialog and Facebook's grid loads lazily, so
            // this is a slower scrape than Terapeak's single page read.
            run = await NodeRuntime.RunAsync(script, SearchProcessTimeout, "fbmarket_search", ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail("error", query, zip, snappedRadius, $"Couldn't launch the browser: {ex.Message}", retryable: true);
        }

        if (run.TimedOut)
            return Fail("error", query, zip, snappedRadius,
                "The Marketplace search timed out — Facebook loads a real page, so a busy machine can miss the window. Try again.",
                retryable: true);

        if (string.IsNullOrWhiteSpace(run.StdOut))
            return Fail("error", query, zip, snappedRadius,
                string.IsNullOrWhiteSpace(run.StdErr) ? "No output from the Marketplace search." : run.StdErr);

        return InterpretPayload(run.StdOut, query, zip ?? "", snappedRadius);
    }

    /// <summary>
    /// Answers a search from the session file alone when the file already settles it, and returns
    /// null when there is a session worth launching a browser for.
    /// </summary>
    /// <remarks>
    /// The split that matters is Connect versus Reconnect. A machine that never had a session needs
    /// setting up; one whose session died needs a person to sign in again, and being told to
    /// "connect Facebook" reads as the app having lost something. An unreadable file is its own
    /// third case: nothing the seller did caused it, and it is not something a retry fixes.
    /// </remarks>
    private LocalSupplySearchResult? GateOnSession(string query, string? zip, int snappedRadius)
    {
        var session = Session;
        return session.State switch
        {
            FacebookSessionState.Valid => null,

            FacebookSessionState.Expired => Fail("session_expired", query, zip, snappedRadius,
                $"{session.Reason} Reconnect to search Marketplace again.", fixAction: ConnectFixAction),

            // Said in words, not just in a status: this is the one local-sourcing failure the seller
            // fixes in a single click, and "not_connected" with an empty message renders as a bare
            // chip that explains nothing.
            FacebookSessionState.Missing => Fail("not_connected", query, zip, snappedRadius,
                "Connect Facebook first — it needs a one-time login to your own account. Craigslist needs no login.",
                fixAction: ConnectFixAction),

            // Empty and Malformed: the login was made and the file did not survive being written.
            _ => Fail("not_connected", query, zip, snappedRadius,
                $"{session.Reason} Reconnect to save a fresh one.", fixAction: ConnectFixAction),
        };
    }

    /// <summary>
    /// Turns a raw scrape payload into a result.
    /// </summary>
    private LocalSupplySearchResult InterpretPayload(
        string json, string query, string zip, int snappedRadius)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Fail("error", query, zip, snappedRadius, "No output from the Marketplace search.");

        ScrapePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ScrapePayload>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException ex)
        {
            return Fail("error", query, zip, snappedRadius, $"Couldn't read the search output: {ex.Message}");
        }

        if (payload is null)
            return Fail("error", query, zip, snappedRadius, "Empty search output.");

        // A browser that never started is a machine problem, not an empty local market. Reported
        // through FailureTranslator so no Chrome, no Playwright and a Chrome already holding the
        // profile each get their own sentence and their own answer on whether a retry is worth it.
        if (FailureTranslator.IsBrowserLaunchFailure(payload.Error))
        {
            var failure = FailureTranslator.Translate(
                new InvalidOperationException(payload.Error), FailureDomain.Browser);
            log.Add("Warning", "Facebook Marketplace browser failed to start", failure.Headline);
            return Fail("error", query, zip, snappedRadius,
                $"{failure.Headline}. {failure.WhatToDo}", retryable: failure.Retryable,
                fixAction: failure.FixAction);
        }

        // The case this whole file exists for: Facebook handed the replayed session a login,
        // checkpoint or two-factor page. There are no /marketplace/item/ tiles on any of those, so
        // without this check the scrape reports zero results — indistinguishable from a thin local
        // market, and the seller is never told the one thing they can act on.
        var wall = FacebookMarketplaceParser.DetectLoginWall(payload.Url, payload.PageSignature);
        if (payload.LoggedOut || wall != FacebookLoginWall.None)
        {
            // Same rule as Terapeak: never pop a login window as a side effect. Facebook
            // expires sessions on password change, new-device checks and security challenges —
            // all of which need the person, not the app.
            //
            // Marked rather than deleted: deleting made the next screen say "never connected", so
            // the seller went looking for a setting they had already set. See FacebookSessionFile.
            FacebookSessionFile.MarkExpired(_sessionPath,
                wall == FacebookLoginWall.None ? "the page carried a sign-in form" : $"bounced to {wall}");
            var reason = FacebookMarketplaceParser.DescribeLoginWall(wall);
            if (reason.Length == 0)
                reason = "Your saved Facebook session expired — reconnect to search Marketplace again.";

            log.Add("Warning", "Facebook session expired", "Reconnect in Settings to search Marketplace again.");
            return Fail("session_expired", query, zip, snappedRadius, reason, fixAction: ConnectFixAction);
        }

        var cards = payload.Cards ?? [];
        var result = FacebookMarketplaceParser.BuildResult(cards, query, zip ?? "", snappedRadius);

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(payload.Error))
            result.Error = payload.Error;

        // A zero-result search where the location dialog never opened is almost always a
        // selector drift, not an empty local market — say so instead of reporting "no supply".
        if (result.Count == 0 && !payload.LocationSet && !string.IsNullOrWhiteSpace(zip))
            result.Error ??= "No results, and the location couldn't be set from the zip code — Facebook may have changed its layout (see FacebookMarketplaceSelectors).";

        // Sold/pending tiles this pass happened to surface. Recorded here so it costs no extra
        // Marketplace traffic against the seller's logged-in account — a dedicated sweep for sold
        // items is exactly the kind of automated browsing that gets an account checkpointed.
        // These are asking prices, never sale prices; FacebookSoldStore says so at length, and
        // nothing that prices anything is allowed to read them.
        if (soldStore is not null && result.SoldItems.Count > 0)
        {
            try
            {
                var added = soldStore.Record(result.SoldItems, query);
                log.Add("Info", "Facebook sold-marked items recorded",
                    $"{result.SoldItems.Count} seen for \"{query}\" ({added} new). Asking prices, not sale prices.");
            }
            catch (Exception ex)
            {
                // A search that found supply must not fail because a side-record didn't write.
                log.Add("Warning", "Could not record Facebook sold-marked items", ex.Message);
            }
        }

        log.Add("Info", "Facebook Marketplace search",
            $"\"{query}\" within {snappedRadius} mi of {zip} — {result.Count} local listing(s).");

        // Every Facebook read (search, browse, picks) funnels through here, so this is the one place
        // to make the listing photos load. Facebook's CDN URLs are signed, short-lived and reject a
        // cross-origin referrer, so a browser on the hosted site renders them blank; FbPhotoProxy
        // swaps each for a same-origin /api/fb-photo URL the server fetches and caches. No-op on
        // desktop, where the app's own browser loads them directly. See FbPhotoProxy.
        FbPhotoProxy.RewriteItems(result.Items);

        return result;
    }

    /// <summary>
    /// Reads Marketplace's own front page — "Today's picks" — using the saved session.
    ///
    /// Different in kind from <see cref="SearchAsync"/>, and that is the point: a search only ever
    /// finds what the seller already thought to type. This is the feed Facebook builds for THIS
    /// account near its own saved location, so it surfaces local supply nobody would have searched
    /// for — the golf cart, the trailer, the pallet of solar panels down the road.
    ///
    /// Same posture as everything else here: one page load, only ever from a person asking, no
    /// login attempted as a side effect, and an expired session reported rather than fixed.
    /// </summary>
    public Task<LocalSupplySearchResult> BrowsePicksAsync(CancellationToken ct = default) =>
        BrowseAsync(new FacebookMarketplaceSelectors.BrowseFilters(), ct);

    /// <summary>
    /// Browses Marketplace with any combination of its own filters — keyword, category board,
    /// price band, condition, how recently it was listed, delivery method, sort order, radius.
    ///
    /// This exists because Facebook cannot be embedded: every Marketplace URL is served with
    /// <c>X-Frame-Options: DENY</c>, so no browser will render their page inside this one. What
    /// their page does, though, is drive all of its filters through the query string — so offering
    /// the same controls and building the same URL gets to the same results, in this app's own
    /// table, priced against sold comps. See FacebookMarketplaceSelectors.BuildBrowseUrl.
    /// </summary>
    public async Task<LocalSupplySearchResult> BrowseAsync(
        FacebookMarketplaceSelectors.BrowseFilters filters, CancellationToken ct = default)
    {
        if (GateOnSession(filters.Query, "", filters.RadiusMiles) is { } blocked) return blocked;

        var script = BuildPicksScript(
            NodeRuntime.PlaywrightDir, _sessionPath,
            FacebookMarketplaceSelectors.ToJson(filters.Query, filters.RadiusMiles, ""),
            FacebookMarketplaceSelectors.BuildBrowseUrl(filters));

        NodeRunResult run;
        try
        {
            run = await NodeRuntime.RunAsync(script, PicksProcessTimeout, "fbmarket_picks", ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail("error", "", "", 0, $"Couldn't launch the browser: {ex.Message}", retryable: true);
        }

        if (run.TimedOut)
            return Fail("error", "", "", 0,
                "Marketplace didn't finish loading — Facebook renders a real page, so a busy machine can miss the window. Try again.",
                retryable: true);

        var result = InterpretPayload(run.StdOut, filters.Query, "", filters.RadiusMiles);
        result.SearchUrl  = FacebookMarketplaceSelectors.BuildBrowseUrl(filters);
        result.ScopeLabel = DescribeBrowse(filters);
        if (result.Status == "ok")
            log.Add("Info", "Facebook Marketplace browse", $"{result.Count} listing(s) — {result.ScopeLabel}.");
        return result;
    }

    /// <summary>What was actually asked for, in the seller's words — so a thin result reads as the filters being tight rather than the market being empty.</summary>
    private static string DescribeBrowse(FacebookMarketplaceSelectors.BrowseFilters f)
    {
        if (f.IsPicks && f.MinPrice is null && f.MaxPrice is null &&
            string.IsNullOrWhiteSpace(f.Condition) && string.IsNullOrWhiteSpace(f.DaysListed) &&
            string.IsNullOrWhiteSpace(f.Delivery) && string.IsNullOrWhiteSpace(f.SortBy))
            return "Today's picks — your own Marketplace feed";

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(f.Query)) parts.Add($"\"{f.Query}\"");
        if (!string.IsNullOrWhiteSpace(f.CategorySlug))
            parts.Add(OptionLabel(FacebookMarketplaceSelectors.CategoryOptions, f.CategorySlug));
        if (f.MinPrice is > 0 || f.MaxPrice is > 0)
            parts.Add($"{(f.MinPrice is > 0 ? $"${f.MinPrice:0}" : "any")}–{(f.MaxPrice is > 0 ? $"${f.MaxPrice:0}" : "any")}");
        if (!string.IsNullOrWhiteSpace(f.Condition)) parts.Add(OptionLabel(FacebookMarketplaceSelectors.ConditionOptions, f.Condition));
        if (!string.IsNullOrWhiteSpace(f.DaysListed)) parts.Add(OptionLabel(FacebookMarketplaceSelectors.DateListedOptions, f.DaysListed));
        if (!string.IsNullOrWhiteSpace(f.Delivery)) parts.Add(OptionLabel(FacebookMarketplaceSelectors.DeliveryOptions, f.Delivery));
        if (!string.IsNullOrWhiteSpace(f.SortBy)) parts.Add(OptionLabel(FacebookMarketplaceSelectors.SortOptions, f.SortBy));
        parts.Add($"within {FacebookMarketplaceParser.NearestSupportedRadius(f.RadiusMiles)} mi");
        return string.Join(" · ", parts);
    }

    private static string OptionLabel((string Value, string Label)[] options, string value) =>
        options.FirstOrDefault(o => o.Value == value).Label ?? value;

    /// <summary>The button the UI puts on a Facebook failure. Recognised by FIX_ACTIONS in app.js.</summary>
    public const string ConnectFixAction = "connect-facebook";

    private static LocalSupplySearchResult Fail(
        string status, string query, string? zip, int radius, string? error = null,
        bool retryable = false, string fixAction = "") => new()
    {
        SourceId    = FacebookMarketplaceParser.SourceId,
        SourceLabel = FacebookMarketplaceParser.SourceLabel,
        Status      = status,
        Query       = query,
        ZipCode     = zip ?? "",
        RadiusMiles = radius,
        SearchUrl   = FacebookMarketplaceSelectors.BuildSearchUrl(string.IsNullOrWhiteSpace(query) ? " " : query, radius),
        Error       = error,
        Retryable   = retryable,
        FixAction   = fixAction,
    };

    private sealed class ScrapePayload
    {
        public bool LoggedOut { get; set; }
        public bool LocationSet { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }

        /// <summary>
        /// The page's title, whether it carries a login form, and the first of its visible text —
        /// built in the page and capped there, so a few hundred bytes cross the process boundary
        /// rather than a Marketplace page's several megabytes of markup. Read only by
        /// <see cref="FacebookMarketplaceParser.DetectLoginWall"/>, never logged and never stored.
        /// </summary>
        public string? PageSignature { get; set; }

        public List<FacebookRawCard>? Cards { get; set; }
    }

    // ── Node/Playwright scripts ────────────────────────────────────────────────
    // Raw string literals with %%PLACEHOLDER%% substitution, so the JavaScript below reads as
    // JavaScript — no C#-level brace doubling or backslash escaping to get wrong.
    //
    // Each script is built through a public method rather than substituted at its call site,
    // because this is JavaScript embedded in C#: nothing in the build type-checks a word of it, so
    // a typo ships and the seller sees "Facebook keeps disconnecting". FacebookScrapeScriptTests
    // asserts the properties a working connection actually depends on.

    /// <summary>
    /// Ceiling on one search process. Sits ABOVE the script's own watchdog so the script is what
    /// ends a stuck run — a killed process has no payload, and no payload has no reason in it.
    /// </summary>
    public static TimeSpan SearchProcessTimeout => TimeSpan.FromSeconds(120);
    public static TimeSpan PicksProcessTimeout  => TimeSpan.FromSeconds(90);

    /// <summary>The script's own deadline. Under the matching process timeout, by design.</summary>
    public const int SearchWatchdogMs = 100_000;
    public const int PicksWatchdogMs  = 70_000;

    public static string BuildLoginScript(string playwrightDir, string sessionPath) =>
        LoginScript
            .Replace("%%PW%%", NodeRuntime.JsPath(playwrightDir))
            .Replace("%%RAISE%%", NodeRuntime.RaiseToFrontJs)
            .Replace("%%LAUNCHGUARD%%", LaunchGuardJs)
            .Replace("%%LANDING%%", FacebookMarketplaceSelectors.LoginLandingUrl)
            .Replace("%%FALLBACK%%", FacebookMarketplaceSelectors.LoginFallbackUrl)
            // Temp-file-then-rename rather than a straight write: a crash mid-save used to leave a
            // truncated session file, which reads as "not connected" and costs the seller the login
            // they just completed. The temp name carries the pid, so a second login window racing
            // this one cannot land inside its rename — see AtomicFile.NodeWriteJs.
            .Replace("%%SAVESESSION%%",
                AtomicFile.NodeWriteJs(NodeRuntime.JsPath(sessionPath), "JSON.stringify(state)"));

    // The config JSON is substituted LAST in both scrapes below: it carries the seller's own search
    // text, and a query that happened to contain a placeholder would otherwise be substituted into.
    // How the headless search/picks browser is launched. Desktop uses the seller's real installed
    // Google Chrome (channel:'chrome') because it carries their fingerprint and cookies best. The
    // hosted worker container has no system Chrome — only Playwright's bundled Chromium — and runs
    // as a non-root user on a 2 GB box, so it needs --no-sandbox and --disable-dev-shm-usage or the
    // browser will not start. The login script never runs on the server, so it is left untouched.
    private const string SearchLaunchOptions =
#if HOSTED
        "{ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'] }";
#else
        "{ channel: 'chrome', headless: true }";
#endif

    public static string BuildSearchScript(string playwrightDir, string sessionPath, string configJson) =>
        SearchScript
            .Replace("%%PW%%", NodeRuntime.JsPath(playwrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(sessionPath))
            .Replace("%%LAUNCHGUARD%%", LaunchGuardJs)
            .Replace("%%SIGNATURE%%", PageSignatureJs)
            .Replace("%%EMIT%%", EmitJs)
            .Replace("%%WATCHDOG%%", SearchWatchdogMs.ToString())
            .Replace("%%LAUNCHOPTS%%", SearchLaunchOptions)
            .Replace("%%CFG%%", configJson);

    public static string BuildPicksScript(string playwrightDir, string sessionPath, string configJson, string url) =>
        PicksScript
            .Replace("%%PW%%", NodeRuntime.JsPath(playwrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(sessionPath))
            .Replace("%%LAUNCHGUARD%%", LaunchGuardJs)
            .Replace("%%SIGNATURE%%", PageSignatureJs)
            .Replace("%%EMIT%%", EmitJs)
            .Replace("%%WATCHDOG%%", PicksWatchdogMs.ToString())
            .Replace("%%URL%%", url)
            .Replace("%%LAUNCHOPTS%%", SearchLaunchOptions)
            .Replace("%%CFG%%", configJson);

    /// <summary>
    /// Everything that can fail before a page ever loads, named instead of left as raw Playwright
    /// text. Shared by all three scripts; the labels are matched in FailureTranslator.
    /// </summary>
    /// <remarks>
    /// The three that actually happen on a seller's machine have three different fixes — install
    /// the npm package, install Chrome, close the Chrome that is already open — and all three used
    /// to arrive as one indistinguishable "couldn't launch the browser", or worse, as a scrape that
    /// returned zero listings. Naming them here rather than pattern-matching the raw text later
    /// means a Playwright wording change breaks one regex in one place instead of silently
    /// reclassifying a seller's problem as "no listings found".
    /// </remarks>
    private const string LaunchGuardJs = """
          function firstLine(e) { return String((e && e.message) || e).split('\n')[0]; }

          function requirePlaywright(dir) {
            try { return require(dir).chromium; }
            catch (e) { throw new Error('PLAYWRIGHT_MISSING: ' + firstLine(e)); }
          }

          async function launchChrome(chromium, options) {
            try { return await chromium.launch(options); }
            catch (e) {
              const m = firstLine(e);
              if (/Chromium distribution|is not found at|executable doesn't exist|playwright install/i.test(m))
                throw new Error('CHROME_MISSING: ' + m);
              // Chrome refusing to open a second copy against a profile another Chrome holds. Its
              // own words for this vary by version and by Windows locale, so match on the parts
              // that don't: the singleton lock Chrome names directly.
              if (/ProcessSingleton|SingletonLock|already in use|already running|profile is in use|cannot create default profile/i.test(m))
                throw new Error('CHROME_BUSY: ' + m);
              throw new Error('BROWSER_LAUNCH_FAILED: ' + m);
            }
          }
        """;

    /// <summary>
    /// Builds the few hundred bytes that answer "did Facebook serve a login wall?" — title, whether
    /// a password box is present, and the start of the visible text.
    /// </summary>
    /// <remarks>
    /// Built and capped inside the page on purpose. A rendered Marketplace page is megabytes of
    /// obfuscated markup, and none of it needs to cross a process boundary to answer one yes/no
    /// question. What comes back is read by FacebookMarketplaceParser.DetectLoginWall and by nothing
    /// else: it is never logged, never stored and never returned to the browser.
    /// </remarks>
    /// <summary>
    /// One payload, always — declared before the scrape starts so every way out of the script goes
    /// through it.
    /// </summary>
    /// <remarks>
    /// A hang, a crash and a clean run all have to produce a JSON object with a reason in it. When
    /// they did not, a stuck page was killed from the C# side with nothing written, which reached
    /// the seller as "No output from the Marketplace search" — a sentence that names no cause and
    /// suggests no action. The watchdog is deliberately shorter than the process timeout that backs
    /// it up, so the script is what ends a stuck run and the payload survives.
    /// </remarks>
    private const string EmitJs = """
        let browser = null;
        const out = { loggedOut: false, locationSet: false, url: '', pageSignature: '', cards: [], error: null };
        let emitted = false;
        function emit(exit) {
          if (emitted) return;
          emitted = true;
          // The callback matters: stdout to a pipe is asynchronous, and exiting before the flush
          // truncates the payload — which is the same "no output" failure by another route.
          try { process.stdout.write(JSON.stringify(out), () => { if (exit) process.exit(0); }); }
          catch (_) { if (exit) process.exit(0); }
        }
        const watchdog = setTimeout(() => {
          out.error = out.error || 'WATCHDOG: Marketplace did not finish loading in time.';
          try { if (browser) browser.close().catch(() => {}); } catch (_) {}
          emit(true);
        }, %%WATCHDOG%%);
        """;

    private const string PageSignatureJs = """
          async function pageSignature(page) {
            try {
              return await page.evaluate(() => {
                const parts = [];
                parts.push('<title>' + (document.title || '') + '</title>');
                if (document.querySelector("input[name='pass']")) parts.push('name="pass"');
                if (document.querySelector('#login_form, form[action*="login"]')) parts.push('id="login_form"');
                const text = document.body ? (document.body.innerText || '') : '';
                return parts.join('\n') + '\n' + text.slice(0, 1500);
              });
            } catch (_) { return ''; }
          }
        """;

    private const string LoginScript = """
        (async () => {
        %%LAUNCHGUARD%%

          // The real installed Chrome, not Playwright's bundled build: Facebook's login flow
          // challenges the test browser fingerprint far more aggressively, and a challenge the
          // user can't clear means no session at all.
          let browser;
          try {
            const chromium = requirePlaywright('%%PW%%');
            browser = await launchChrome(chromium, { channel: 'chrome', headless: false, args: ['--disable-blink-features=AutomationControlled', '--start-maximized'] });
          } catch (e) {
            // A named launch failure, not a silent exit: without this the window never appeared,
            // the app went on claiming one was open, and the seller was eventually told they had
            // closed it.
            process.stdout.write(firstLine(e));
            return;
          }
          const ctx = await browser.newContext({ viewport: null });
          await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });
          const page = await ctx.newPage();

          // Lift the actual OS window, not just the tab — bringToFront() alone can leave a
          // security check sitting invisibly behind this app's own window.
        %%RAISE%%

          // Not awaited: the burst keeps lifting the window for its first few seconds while the
          // login page loads underneath it, which is exactly when the window loses the focus
          // race and ends up buried.
          raiseBurst().catch(() => {});

          // A navigation failure here used to be swallowed, which produced the worst possible
          // outcome: a blank window the seller stares at for six minutes, then a "CANCELLED"
          // that blamed them for closing it. Try the fallback entry point, and if neither
          // serves a login form, say so immediately instead of waiting out the clock.
          async function tryGoto(url) {
            try {
              await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 30000 });
              return null;
            } catch (e) { return String((e && e.message) || e).split('\n')[0]; }
          }

          let navError = await tryGoto('%%LANDING%%');
          if (navError) navError = await tryGoto('%%FALLBACK%%');

          // A reachable page is not necessarily the login form — a checkpoint or an outage page
          // loads fine and still can't be signed into.
          const haveForm = await page.$("input[name='pass']").catch(() => null);
          if (navError && !haveForm) {
            process.stdout.write('NAVFAIL: ' + navError);
            try { await browser.close(); } catch (_) {}
            return;
          }

          // c_user is Facebook's own signed-in marker. Waiting on the cookie rather than a URL
          // avoids saving a half-finished session mid-2FA, when the URL already looks fine.
          async function signedIn() {
            try {
              const cookies = await ctx.cookies('https://www.facebook.com');
              return cookies.some(c => c.name === 'c_user' && c.value);
            } catch (_) { return false; }
          }

          const deadline = Date.now() + 6 * 60 * 1000;
          let sinceFocus = 0;
          let ok = false;
          while (Date.now() < deadline) {
            if (!browser.isConnected()) break;
            if (await signedIn()) { ok = true; break; }
            await page.waitForTimeout(1000).catch(() => {});
            // Gentle tab focus only — the hard raise here would visibly flash the window every
            // few seconds and interrupt someone mid-login. The burst above already did the
            // attention-grabbing, once, while nobody was typing yet.
            sinceFocus++;
            if (sinceFocus >= 8) { sinceFocus = 0; await raise(false); }
          }

          if (ok && browser.isConnected()) {
            await page.waitForTimeout(1500);
            const state = await ctx.storageState();
        %%SAVESESSION%%
            process.stdout.write('SAVED');
          } else {
            process.stdout.write('CANCELLED');
          }
          try { await browser.close(); } catch (_) {}
        })();
        """;

    // Deliberately the simplest script here: no location dialog, no query, no filters. Open the
    // page the seller's own account sees and read the tiles. Everything it produces is the same
    // payload shape as the search scrape, so InterpretPayload handles both.
    private const string PicksScript = """
        %%LAUNCHGUARD%%
        %%SIGNATURE%%
        %%EMIT%%
        const CFG = %%CFG%%;

        (async () => {
          // No location dialog on this one — the feed is whatever Facebook shows this account near
          // its own saved location — so nothing is left to set.
          out.locationSet = true;
          try {
            const chromium = requirePlaywright('%%PW%%');
            browser = await launchChrome(chromium, %%LAUNCHOPTS%%);
            const ctx = await browser.newContext({
              storageState: '%%SESSION%%',
              viewport: { width: 1400, height: 1200 },
              locale: 'en-US'
            });
            // Nothing waits forever: an unbounded default is how one stuck selector turned a
            // 30-second scrape into a process the app had to kill from outside.
            ctx.setDefaultTimeout(15000);
            ctx.setDefaultNavigationTimeout(35000);
            await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });
            const page = await ctx.newPage();

            await page.goto('%%URL%%', { waitUntil: 'domcontentloaded', timeout: 35000 });
            await page.waitForTimeout(4000);
            out.url = page.url();
            out.pageSignature = await pageSignature(page);

            out.loggedOut = new RegExp(CFG.loggedOutUrlPattern).test(page.url());
            if (!out.loggedOut) {
              for (const sel of CFG.loggedOutSelectors || []) {
                if (await page.$(sel)) { out.loggedOut = true; break; }
              }
            }

            if (!out.loggedOut) {
              // The picks grid is virtualised like the search grid, so a couple of scrolls are
              // what turn six visible tiles into a screenful worth looking at.
              for (let i = 0; i < 3; i++) {
                await page.mouse.wheel(0, 2200).catch(() => {});
                await page.waitForTimeout(1100);
              }
              out.cards = await page.evaluate((selectors) => {
                const seen = new Set();
                const found = [];
                for (const sel of selectors) {
                  document.querySelectorAll(sel).forEach(a => {
                    const href = (a.href || '').split('?')[0];
                    if (!href || seen.has(href)) return;
                    seen.add(href);
                    const img = a.querySelector('img');
                    const lines = (a.innerText || '').split('\n').map(s => s.trim()).filter(Boolean).slice(0, 12);
                    found.push({ href, imageUrl: img ? (img.src || '') : '', lines });
                  });
                  if (found.length) break;
                }
                // A look at the feed, not a harvest of it.
                return found.slice(0, 60);
              }, CFG.cardSelectors);
            }
          } catch (e) {
            out.error = String((e && e.message) || e);
          }
          try { if (browser) await browser.close(); } catch (_) {}
          clearTimeout(watchdog);
          emit(false);
        })();
        """;

    private const string SearchScript = """
        %%LAUNCHGUARD%%
        %%SIGNATURE%%
        %%EMIT%%
        const CFG = %%CFG%%;

        // Every selector arrives as a candidate list — first one that resolves wins, the rest
        // are tolerated misses. Facebook ships several layouts at once, so "the selector" is
        // never a single string.
        async function firstVisible(page, selectors, timeout) {
          for (const sel of selectors || []) {
            try {
              const el = await page.waitForSelector(sel, { timeout: timeout || 2500, state: 'visible' });
              if (el) return el;
            } catch (_) {}
          }
          return null;
        }

        async function setLocation(page) {
          const opener = await firstVisible(page, CFG.locationOpen, 4000);
          if (!opener) return false;
          await opener.click().catch(() => {});
          await page.waitForTimeout(1500);

          const input = await firstVisible(page, CFG.locationInput, 4000);
          if (!input) return false;
          await input.click().catch(() => {});
          await input.fill('').catch(() => {});
          // Typed with a delay: Facebook's location box only queries its suggestion service on
          // real keystrokes, and a pasted value silently produces no suggestions at all.
          await input.type(String(CFG.zip), { delay: 120 }).catch(() => {});
          await page.waitForTimeout(2500);

          const suggestion = await firstVisible(page, CFG.locationSuggestion, 4000);
          if (suggestion) await suggestion.click().catch(() => {});
          else { await page.keyboard.press('ArrowDown').catch(() => {}); await page.keyboard.press('Enter').catch(() => {}); }
          await page.waitForTimeout(1500);

          const label = CFG.radiusMiles + ' miles';
          const radius = await firstVisible(page, CFG.radiusOpen, 3000);
          if (radius) {
            const tag = await radius.evaluate(n => n.tagName.toLowerCase()).catch(() => '');
            if (tag === 'select') {
              await radius.selectOption({ label }).catch(() => {});
            } else {
              await radius.click().catch(() => {});
              await page.waitForTimeout(1000);
              const opt = page.locator("div[role='option']", { hasText: label }).first();
              await opt.click({ timeout: 3000 }).catch(() => {});
            }
          }
          await page.waitForTimeout(800);

          const apply = await firstVisible(page, CFG.apply, 3000);
          if (apply) await apply.click().catch(() => {});
          await page.waitForTimeout(4000);
          return true;
        }

        async function readCards(page) {
          return await page.evaluate((selectors) => {
            const seen = new Set();
            const out = [];
            for (const sel of selectors) {
              document.querySelectorAll(sel).forEach(a => {
                const href = (a.href || '').split('?')[0];
                if (!href || seen.has(href)) return;
                seen.add(href);
                const img = a.querySelector('img');
                const lines = (a.innerText || '').split('\n').map(s => s.trim()).filter(Boolean).slice(0, 12);
                out.push({ href, imageUrl: img ? (img.src || '') : '', lines });
              });
              if (out.length) break;
            }
            // Cap the haul: this is a look at local supply, not a crawl of the whole market.
            return out.slice(0, 120);
          }, CFG.cardSelectors);
        }

        (async () => {
          try {
            const chromium = requirePlaywright('%%PW%%');
            browser = await launchChrome(chromium, %%LAUNCHOPTS%%);
            const ctx = await browser.newContext({
              storageState: '%%SESSION%%',
              viewport: { width: 1400, height: 1200 },
              locale: 'en-US'
            });
            // Nothing waits forever: an unbounded default is how one stuck selector turned a
            // 30-second scrape into a process the app had to kill from outside.
            ctx.setDefaultTimeout(15000);
            ctx.setDefaultNavigationTimeout(35000);
            await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });
            const page = await ctx.newPage();

            await page.goto(CFG.searchUrl, { waitUntil: 'domcontentloaded', timeout: 35000 });
            await page.waitForTimeout(4000);
            out.url = page.url();
            out.pageSignature = await pageSignature(page);

            out.loggedOut = new RegExp(CFG.loggedOutUrlPattern).test(page.url());
            if (!out.loggedOut) {
              for (const sel of CFG.loggedOutSelectors || []) {
                if (await page.$(sel)) { out.loggedOut = true; break; }
              }
            }

            if (!out.loggedOut) {
              if (CFG.zip) out.locationSet = await setLocation(page);
              // Facebook virtualises the result grid, so the first screen is all that exists
              // in the DOM until it's scrolled.
              for (let i = 0; i < (CFG.scrollPasses || 4); i++) {
                await page.mouse.wheel(0, 2400).catch(() => {});
                await page.waitForTimeout(1200);
              }
              out.cards = await readCards(page);
            }
          } catch (e) {
            out.error = String((e && e.message) || e);
          }
          try { if (browser) await browser.close(); } catch (_) {}
          clearTimeout(watchdog);
          emit(false);
        })();
        """;
}
