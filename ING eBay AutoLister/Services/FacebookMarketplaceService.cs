using System.Runtime.InteropServices;
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
/// </summary>
public class FacebookMarketplaceService(IWebHostEnvironment env, ActionLog log)
{
    private readonly string _sessionPath = Path.Combine(env.ContentRootPath, "facebook-session.json");
    private volatile bool _loginInProgress;

    // Same reason as TerapeakService: without a one-time foreground grant, the login window
    // Windows spawns for a background process can open behind the app with no sign it appeared.
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    public bool IsConnected => File.Exists(_sessionPath);
    public bool IsLoginInProgress => _loginInProgress;
    public string? LastLoginError { get; private set; }

    // ── One-time interactive login ────────────────────────────────────────────

    public (bool Started, string Message) StartLogin()
    {
        if (_loginInProgress)
            return (false, "A login window is already open — finish logging in there.");

        LastLoginError = null;
        _loginInProgress = true;
        _ = Task.Run(RunLoginProcessAsync);
        return (true, "A browser window just opened — log into Facebook there. It closes itself once you're in.");
    }

    private async Task RunLoginProcessAsync()
    {
        var script = LoginScript
            .Replace("%%PW%%", NodeRuntime.JsPath(NodeRuntime.PlaywrightDir))
            .Replace("%%SESSION%%", NodeRuntime.JsPath(_sessionPath))
            .Replace("%%LANDING%%", FacebookMarketplaceSelectors.LoginLandingUrl);

        try
        {
            var run = await NodeRuntime.RunAsync(script, TimeSpan.FromMinutes(7), "fbmarket_login",
                beforeStart: () => { try { AllowSetForegroundWindow(ASFW_ANY); } catch { } });

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
                LastLoginError = string.IsNullOrWhiteSpace(run.StdErr)
                    ? (run.StdOut.Length > 0 ? run.StdOut : "Login window was closed before signing in.")
                    : run.StdErr;
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
            _loginInProgress = false;
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
    public async Task<FacebookMarketplaceSearchResult> SearchAsync(string query, string zip, int radiusMiles)
    {
        var snappedRadius = FacebookMarketplaceParser.NearestSupportedRadius(radiusMiles);

        if (!IsConnected)
            return Fail("not_connected", query, zip, snappedRadius);

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
        catch (Exception ex)
        {
            return Fail("error", query, zip, snappedRadius, $"Couldn't launch the browser: {ex.Message}");
        }

        if (run.TimedOut)
            return Fail("error", query, zip, snappedRadius, "The Marketplace search timed out.");

        if (string.IsNullOrWhiteSpace(run.StdOut))
            return Fail("error", query, zip, snappedRadius,
                string.IsNullOrWhiteSpace(run.StdErr) ? "No output from the Marketplace search." : run.StdErr);

        ScrapePayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ScrapePayload>(run.StdOut,
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
            return Fail("session_expired", query, zip, snappedRadius);
        }

        var cards = payload.Cards ?? [];
        var result = FacebookMarketplaceParser.BuildResult(cards, query, zip ?? "", snappedRadius);

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(payload.Error))
            result.Error = payload.Error;

        // A zero-result search where the location dialog never opened is almost always a
        // selector drift, not an empty local market — say so instead of reporting "no supply".
        if (result.Count == 0 && !payload.LocationSet && !string.IsNullOrWhiteSpace(zip))
            result.Error ??= "No results, and the location couldn't be set from the zip code — Facebook may have changed its layout (see FacebookMarketplaceSelectors).";

        log.Add("Info", "Facebook Marketplace search",
            $"\"{query}\" within {snappedRadius} mi of {zip} — {result.Count} local listing(s).");

        return result;
    }

    private static FacebookMarketplaceSearchResult Fail(string status, string query, string? zip, int radius, string? error = null) => new()
    {
        Status      = status,
        Query       = query,
        ZipCode     = zip ?? "",
        RadiusMiles = radius,
        SearchUrl   = FacebookMarketplaceSelectors.BuildSearchUrl(string.IsNullOrWhiteSpace(query) ? " " : query, radius),
        Error       = error,
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
          let cdp = null;
          async function raise() {
            try {
              await page.bringToFront().catch(() => {});
              if (!cdp) cdp = await ctx.newCDPSession(page);
              const { windowId } = await cdp.send('Browser.getWindowForTarget');
              await cdp.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'minimized' } });
              await cdp.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'normal' } });
            } catch (_) {}
          }

          await raise();
          try { await page.goto('%%LANDING%%', { waitUntil: 'domcontentloaded', timeout: 30000 }); } catch (_) {}
          await raise();

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
            // Gentle tab focus only — the minimize/normal cycle here would visibly flash the
            // window every few seconds and interrupt someone mid-login.
            sinceFocus++;
            if (sinceFocus >= 8) { sinceFocus = 0; await page.bringToFront().catch(() => {}); }
          }

          if (ok && browser.isConnected()) {
            await page.waitForTimeout(1500);
            const state = await ctx.storageState();
            require('fs').writeFileSync('%%SESSION%%', JSON.stringify(state));
            process.stdout.write('SAVED');
          } else {
            process.stdout.write('CANCELLED');
          }
          try { await browser.close(); } catch (_) {}
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
