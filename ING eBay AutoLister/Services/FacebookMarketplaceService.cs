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
public class FacebookMarketplaceService(IWebHostEnvironment env, ActionLog log, FacebookSoldStore? soldStore = null) : ILocalSupplySource
{
    private readonly string _sessionPath = Path.Combine(env.ContentRootPath, "facebook-session.json");

    // Interlocked, not a volatile bool: "check the flag, then set it" is two steps, and the
    // connect button is bound in several places at once (Settings card, sourcing banner, the
    // inline chip on a search result). Clicks that land together all read "not running" and
    // each launch a browser — which is how one click could put five login windows on screen.
    private int _loginInProgress;

    public bool IsConnected => File.Exists(_sessionPath);

    /// <summary>Where the saved storageState lives. Exposed so ConnectionDoctor can load it into a
    /// headless context and actually test it, rather than settling for "the file is there".</summary>
    public string SessionPath => _sessionPath;

    public bool IsLoginInProgress => Volatile.Read(ref _loginInProgress) == 1;
    public string? LastLoginError { get; private set; }

    // ── ILocalSupplySource ────────────────────────────────────────────────────
    public string Id => FacebookMarketplaceParser.SourceId;
    public string Label => FacebookMarketplaceParser.SourceLabel;
    public bool RequiresConnection => true;
    public bool IsAvailable => IsConnected;
    public string AvailabilityNote => IsConnected
        ? "Connected — searches run in a headless browser, so give them a minute."
        : "Needs a one-time login to your own Facebook account (Settings).";

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

    private async Task RunLoginProcessAsync()
    {
        var script = LoginScript
            .Replace("%%PW%%", NodeRuntime.JsPath(NodeRuntime.PlaywrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(_sessionPath))
            .Replace("%%RAISE%%", NodeRuntime.RaiseToFrontJs)
            .Replace("%%LANDING%%", FacebookMarketplaceSelectors.LoginLandingUrl)
            .Replace("%%FALLBACK%%", FacebookMarketplaceSelectors.LoginFallbackUrl)
            // Temp-file-then-rename rather than a straight write: a crash mid-save used to leave a
            // truncated session file, which reads as "not connected" and costs the seller the login
            // they just completed.
            .Replace("%%SAVESESSION%%",
                AtomicFile.NodeWriteJs(NodeRuntime.JsPath(_sessionPath), "JSON.stringify(state)"));

        try
        {
            var run = await NodeRuntime.RunAsync(script, TimeSpan.FromMinutes(7), "fbmarket_login",
                beforeStart: () =>
                {
                    LoginWindowFocus.Grant();
                    LoginWindowFocus.PinNewBrowserWindowBriefly();
                });

            if (run.TimedOut)
            {
                LastLoginError = "No login completed within 7 minutes.";
                log.Add("Warning", "Facebook login timed out", LastLoginError);
                return;
            }

            if (run.StdOut == "SAVED")
                log.Add("Info", "Facebook Marketplace connected", "Session saved — local Marketplace search is now available.");
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

    public void Disconnect()
    {
        try { File.Delete(_sessionPath); } catch { }
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

        // Said in words, not just in a status: this is the one local-sourcing failure the seller
        // fixes in a single click, and "not_connected" with an empty message renders as a bare
        // chip that explains nothing.
        if (!IsConnected)
            return Fail("not_connected", query, zip, snappedRadius,
                "Connect Facebook first — it needs a one-time login to your own account. Craigslist needs no login.");

        if (string.IsNullOrWhiteSpace(query))
            return Fail("error", query, zip, snappedRadius, "Enter something to search for.");

        var script = SearchScript
            .Replace("%%PW%%", NodeRuntime.JsPath(NodeRuntime.PlaywrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(_sessionPath))
            .Replace("%%CFG%%", FacebookMarketplaceSelectors.ToJson(query, snappedRadius, zip ?? ""));

        NodeRunResult run;
        try
        {
            // Setting the location drives a real dialog and Facebook's grid loads lazily, so
            // this is a slower scrape than Terapeak's single page read.
            run = await NodeRuntime.RunAsync(script, TimeSpan.FromSeconds(120), "fbmarket_search");
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

        return InterpretPayload(run.StdOut, query, zip, snappedRadius);
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

        if (payload.LoggedOut)
        {
            // Same rule as Terapeak: never pop a login window as a side effect. Facebook
            // expires sessions on password change, new-device checks and security challenges —
            // all of which need the person, not the app.
            Disconnect();
            log.Add("Warning", "Facebook session expired", "Reconnect in Settings to search Marketplace again.");
            return Fail("session_expired", query, zip, snappedRadius,
                "Your saved Facebook session expired — reconnect to search Marketplace again.");
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
        if (!IsConnected)
            return Fail("not_connected", filters.Query, "", filters.RadiusMiles,
                "Connect Facebook first — this reads Marketplace as your own account.");

        var script = PicksScript
            .Replace("%%PW%%", NodeRuntime.JsPath(NodeRuntime.PlaywrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(_sessionPath))
            .Replace("%%CFG%%", FacebookMarketplaceSelectors.ToJson(filters.Query, filters.RadiusMiles, ""))
            .Replace("%%URL%%", FacebookMarketplaceSelectors.BuildBrowseUrl(filters));

        NodeRunResult run;
        try
        {
            run = await NodeRuntime.RunAsync(script, TimeSpan.FromSeconds(90), "fbmarket_picks");
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

    private static LocalSupplySearchResult Fail(
        string status, string query, string? zip, int radius, string? error = null, bool retryable = false) => new()
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
    };

    private sealed class ScrapePayload
    {
        public bool LoggedOut { get; set; }
        public bool LocationSet { get; set; }
        public string? Url { get; set; }
        public string? Error { get; set; }
        public List<FacebookRawCard>? Cards { get; set; }
    }

    // ── Node/Playwright scripts ────────────────────────────────────────────────
    // Raw string literals with %%PLACEHOLDER%% substitution, so the JavaScript below reads as
    // JavaScript — no C#-level brace doubling or backslash escaping to get wrong.

    private const string LoginScript = """
        const { chromium } = require('%%PW%%');
        (async () => {
          // The real installed Chrome, not Playwright's bundled build: Facebook's login flow
          // challenges the test browser fingerprint far more aggressively, and a challenge the
          // user can't clear means no session at all.
          const browser = await chromium.launch({ channel: 'chrome', headless: false, args: ['--disable-blink-features=AutomationControlled', '--start-maximized'] });
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
        const { chromium } = require('%%PW%%');
        const CFG = %%CFG%%;

        (async () => {
          const out = { loggedOut: false, locationSet: true, url: '', cards: [], error: null };
          let browser = null;
          try {
            browser = await chromium.launch({ channel: 'chrome', headless: true });
            const ctx = await browser.newContext({
              storageState: '%%SESSION%%',
              viewport: { width: 1400, height: 1200 },
              locale: 'en-US'
            });
            await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });
            const page = await ctx.newPage();

            await page.goto('%%URL%%', { waitUntil: 'domcontentloaded', timeout: 35000 });
            await page.waitForTimeout(4000);
            out.url = page.url();

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
          process.stdout.write(JSON.stringify(out));
        })();
        """;

    private const string SearchScript = """
        const { chromium } = require('%%PW%%');
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
          const out = { loggedOut: false, locationSet: false, url: '', cards: [], error: null };
          let browser = null;
          try {
            browser = await chromium.launch({ channel: 'chrome', headless: true });
            const ctx = await browser.newContext({
              storageState: '%%SESSION%%',
              viewport: { width: 1400, height: 1200 },
              locale: 'en-US'
            });
            await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });
            const page = await ctx.newPage();

            await page.goto(CFG.searchUrl, { waitUntil: 'domcontentloaded', timeout: 35000 });
            await page.waitForTimeout(4000);
            out.url = page.url();

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
          process.stdout.write(JSON.stringify(out));
        })();
        """;
}
