using System.Runtime.InteropServices;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Terapeak (eBay Seller Hub's sold-comps research tool) has no public API — it's a regular
/// logged-in website, authenticated by browser cookies, not the OAuth tokens used elsewhere in
/// this app. This service pops a real (visible) browser window once for the seller to log into
/// eBay normally, saves that session to disk, then reuses it headlessly to read real sold-comp
/// data straight off the rendered Seller Hub page.
/// </summary>
public class TerapeakService(IWebHostEnvironment env, ActionLog log)
{
    private readonly string _sessionPath = Path.Combine(env.ContentRootPath, "terapeak-session.json");
    private volatile bool _loginInProgress;

    // Windows blocks a background process from stealing focus by default — without this, the
    // login browser can open behind the app window/taskbar with no visible indication it
    // appeared at all. AllowSetForegroundWindow(ASFW_ANY) grants ANY process a one-time pass to
    // call SetForegroundWindow before the next user input event, which is exactly what the
    // freshly-spawned Chrome window needs to do to raise itself.
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    // node.exe resolution, the Playwright package directory and the run-a-throwaway-script
    // plumbing all live in NodeRuntime now — FacebookMarketplaceService needs the identical
    // setup for the same reason (no public search API, so drive a real logged-in browser).
    private static string PlaywrightDir => NodeRuntime.PlaywrightDir;

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
        return (true, "A browser window just opened — log into eBay there. It closes itself once you're in.");
    }

    private async Task RunLoginProcessAsync()
    {
        var pwPath = PlaywrightDir.Replace("\\", "\\\\");
        var sessionPathEscaped = _sessionPath.Replace("\\", "\\\\");
        var script =
            $"const {{ chromium }} = require('{pwPath}');\n" +
            "(async () => {\n" +
            // The real installed Chrome (not Playwright's bundled "Chrome for Testing" build)
            // reports a normal, self-consistent fingerprint — eBay's bot detection flags the
            // bundled test browser much more readily, especially after repeated automated hits.
            "  const browser = await chromium.launch({ channel: 'chrome', headless: false, args: ['--disable-blink-features=AutomationControlled', '--start-maximized'] });\n" +
            "  const ctx = await browser.newContext({ viewport: null });\n" +
            "  await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });\n" +
            "  const page = await ctx.newPage();\n" +
            // raise(): bring the actual Chrome OS window to the foreground, not just the tab.
            // page.bringToFront() only focuses the tab within Chrome; it does NOT lift the window
            // above the user's other windows, so a CAPTCHA can sit behind this app unseen. The
            // CDP minimize->normal cycle forces Windows to re-raise and refocus the real window.
            "  let cdp = null;\n" +
            "  async function raise() {\n" +
            "    try {\n" +
            "      await page.bringToFront().catch(() => {});\n" +
            "      if (!cdp) cdp = await ctx.newCDPSession(page);\n" +
            "      const { windowId } = await cdp.send('Browser.getWindowForTarget');\n" +
            "      await cdp.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'minimized' } });\n" +
            "      await cdp.send('Browser.setWindowBounds', { windowId, bounds: { windowState: 'normal' } });\n" +
            "    } catch (_) {}\n" +
            "  }\n" +
            "  await raise();\n" +
            "  try {\n" +
            "    await page.goto('https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD', { waitUntil: 'domcontentloaded', timeout: 30000 });\n" +
            "  } catch (_) {}\n" +
            "  await raise();\n" +
            "  const deadline = Date.now() + 6 * 60 * 1000;\n" +
            "  let sinceFocus = 0;\n" +
            "  while (Date.now() < deadline) {\n" +
            "    if (!browser.isConnected()) break;\n" + // user closed the window manually
            "    if (page.url().includes('/sh/research')) break;\n" +
            "    await page.waitForTimeout(1000).catch(() => {});\n" +
            // Re-assert the window to the front every ~5s for the whole wait, not just once at
            // page load — a CAPTCHA/"verify you're human" interstitial can appear well after the
            // initial load, and by then the window may have lost focus (alt-tab, this app's own
            // window stealing it back, etc.), silently burning the whole 6-minute timeout with
            // the challenge never seen. AllowSetForegroundWindow only grants one "pass" up front,
            // but bringToFront() is Playwright's own in-browser focus call and keeps working
            // regardless — that's why it's the thing re-run here, not a second native win32 call.
            // Gentle tab-focus only during the wait — NOT the minimize/normal cycle, which
            // would visibly refresh the window every few seconds and interrupt the user mid-
            // CAPTCHA. The one-time raise() on load already brought the window to the front.
            "    sinceFocus++;\n" +
            "    if (sinceFocus >= 8) { sinceFocus = 0; await page.bringToFront().catch(() => {}); }\n" +
            "  }\n" +
            "  if (browser.isConnected() && page.url().includes('/sh/research')) {\n" +
            "    await page.waitForTimeout(1500);\n" +
            "    const state = await ctx.storageState();\n" +
            $"    require('fs').writeFileSync('{sessionPathEscaped}', JSON.stringify(state));\n" +
            "    process.stdout.write('SAVED');\n" +
            "  } else {\n" +
            "    process.stdout.write('CANCELLED');\n" +
            "  }\n" +
            "  try { await browser.close(); } catch (_) {}\n" +
            "})();\n";

        try
        {
            // Grant foreground rights before launch so the Chrome window this spawns can raise
            // itself above whatever the user is currently looking at, instead of opening
            // silently behind it. Only needed here — ScrapeAsync's browser is headless.
            var run = await NodeRuntime.RunAsync(script, TimeSpan.FromMinutes(7), "terapeak_login",
                beforeStart: () => { try { AllowSetForegroundWindow(ASFW_ANY); } catch { } });

            if (run.TimedOut)
            {
                LastLoginError = "No login completed within 7 minutes.";
                log.Add("Warning", "Terapeak login timed out", LastLoginError);
                return;
            }

            var stdout = run.StdOut;
            var stderr = run.StdErr;

            if (stdout == "SAVED")
                log.Add("Info", "Terapeak connected", "Session saved — sold comps will now use real Terapeak data.");
            else
            {
                LastLoginError = string.IsNullOrWhiteSpace(stderr) ? (stdout.Length > 0 ? stdout : "Login window was closed before signing in.") : stderr;
                log.Add("Warning", "Terapeak login not completed", LastLoginError);
            }
        }
        catch (Exception ex)
        {
            // Previously unguarded — a failure here (most commonly the app process inheriting a
            // stale PATH that predates a Node.js install, so "node" can't be found) used to vanish
            // silently: _loginInProgress still reset to false in the finally below, the UI kept
            // saying "browser window open", and nothing ever told the user why no window appeared.
            LastLoginError = $"Couldn't launch the login browser: {ex.Message}";
            log.Add("Error", "Terapeak login failed to start", LastLoginError);
        }
        finally
        {
            _loginInProgress = false;
        }
    }

    public void Disconnect()
    {
        try { File.Delete(_sessionPath); } catch { }
        log.Add("Info", "Terapeak disconnected", "Saved session removed.");
    }

    // ── Headless scrape using the saved session ────────────────────────────────

    public async Task<TerapeakScrapeResult> ScrapeAsync(string query)
    {
        if (!IsConnected)
            return new TerapeakScrapeResult { Status = "not_connected" };

        var pwPath = PlaywrightDir.Replace("\\", "\\\\");
        var sessionPathEscaped = _sessionPath.Replace("\\", "\\\\");
        var debugShotPath = Path.Combine(env.ContentRootPath, "generated-photos", $"terapeak_debug_{Guid.NewGuid():N}.png");
        var debugShotEscaped = debugShotPath.Replace("\\", "\\\\");
        var url = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords=" + Uri.EscapeDataString(query);

        var script =
            $"const {{ chromium }} = require('{pwPath}');\n" +
            "(async () => {\n" +
            "  const browser = await chromium.launch({ channel: 'chrome', headless: true });\n" +
            $"  const ctx = await browser.newContext({{ storageState: '{sessionPathEscaped}', viewport: {{ width: 1400, height: 1000 }} }});\n" +
            "  const page = await ctx.newPage();\n" +
            "  let loggedOut = false;\n" +
            "  try {\n" +
            $"    await page.goto('{url}', {{ waitUntil: 'domcontentloaded', timeout: 25000 }});\n" +
            "    await page.waitForTimeout(3500);\n" +
            "    loggedOut = /signin\\.ebay\\.com|\\/signin/.test(page.url());\n" +
            "  } catch (_) {}\n" +
            $"  await page.screenshot({{ path: '{debugShotEscaped}', fullPage: true }}).catch(()=>{{}});\n" +
            "  const bodyText = await page.evaluate(() => document.body.innerText).catch(() => '');\n" +
            "  process.stdout.write(JSON.stringify({ loggedOut, url: page.url(), bodyText: bodyText.slice(0, 15000) }));\n" +
            "  await browser.close();\n" +
            "})();\n";

        var run = await NodeRuntime.RunAsync(script, TimeSpan.FromSeconds(40), "terapeak_scrape");
        if (run.TimedOut)
            return new TerapeakScrapeResult { Status = "error", Error = "Scrape timed out." };

        if (string.IsNullOrWhiteSpace(run.StdOut))
            return new TerapeakScrapeResult { Status = "error", Error = string.IsNullOrWhiteSpace(run.StdErr) ? "No output from scrape." : run.StdErr };

        using var doc = JsonDocument.Parse(run.StdOut);
        var loggedOut = doc.RootElement.TryGetProperty("loggedOut", out var lo) && lo.GetBoolean();
        var bodyText  = doc.RootElement.TryGetProperty("bodyText", out var bt) ? bt.GetString() ?? "" : "";

        if (loggedOut)
        {
            // No auto-reconnect here (removed 2026-07-15 along with the background scanner —
            // see Program.cs) — popping a login window as a side effect of any scrape,
            // including a passive on-demand lookup, is exactly the unattended behavior that
            // was turned off. Reconnecting is the user's call, made explicitly in Settings.
            Disconnect();
            return new TerapeakScrapeResult { Status = "session_expired" };
        }

        return new TerapeakScrapeResult { Status = "ok", BodyText = bodyText, DebugScreenshotPath = debugShotPath };
    }
}

public class TerapeakScrapeResult
{
    public string Status { get; set; } = ""; // ok | not_connected | session_expired | error
    public string BodyText { get; set; } = "";
    public string? DebugScreenshotPath { get; set; }
    public string? Error { get; set; }
}
