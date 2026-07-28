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

    // Interlocked, not a volatile bool: "check the flag, then set it" is two separate steps, so
    // clicks that land together all read "not running" and each launch a browser window. The same
    // bug in the Facebook service put five login windows on screen from one click.
    private int _loginInProgress;

    /// <summary>
    /// Where the login window opens, and it has to be eBay's sign-in form — NOT the research page.
    /// Sending a cookie-less browser to /sh/research gets it bounced to eBay's bot-check splash
    /// (measured: two redirects, ending on /splashui/captcha with no sign-in form anywhere), so the
    /// window showed a CAPTCHA instead of a login and the six-minute wait could never end.
    ///
    /// The ru= return parameter is doing real work, not decoration: it makes eBay land the seller
    /// on /sh/research immediately after sign-in, which is exactly the URL the wait loop below
    /// watches for. Without it the success condition is only ever met by accident.
    /// </summary>
    private const string ResearchUrl = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD";

    private static string LoginUrl =>
        "https://signin.ebay.com/ws/eBayISAPI.dll?SignIn&ru=" + Uri.EscapeDataString(ResearchUrl);

    // Windows blocks a background process from stealing focus by default — without a foreground
    // grant the login browser can open behind the app window with no visible indication it
    // appeared at all. See LoginWindowFocus for that grant and the rest of the raise story.

    // node.exe resolution, the Playwright package directory and the run-a-throwaway-script
    // plumbing all live in NodeRuntime now — FacebookMarketplaceService needs the identical
    // setup for the same reason (no public search API, so drive a real logged-in browser).
    private static string PlaywrightDir => NodeRuntime.PlaywrightDir;

    public bool IsConnected => File.Exists(_sessionPath);
    public bool IsLoginInProgress => Volatile.Read(ref _loginInProgress) == 1;
    public string? LastLoginError { get; private set; }

    // ── One-time interactive login ────────────────────────────────────────────

    public (bool Started, string Message) StartLogin()
    {
        // Claim the slot and test the claim in one operation, so a second caller loses the race
        // outright rather than both deciding they won it.
        if (Interlocked.CompareExchange(ref _loginInProgress, 1, 0) == 1)
            return (false, "A login window is already open — finish logging in there.");

        LastLoginError = null;
        _ = Task.Run(RunLoginProcessAsync);
        return (true, "A browser window just opened — log into eBay there. If you don't see it, Alt+Tab or check the taskbar for it. It closes itself once you're in.");
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
            // raise()/raiseBurst(): bring the actual Chrome OS window to the foreground, not just
            // the tab. page.bringToFront() only focuses the tab within Chrome; it does NOT lift
            // the window above the user's other windows, so a CAPTCHA can sit behind this app
            // unseen. Shared with FacebookMarketplaceService — see NodeRuntime.RaiseToFrontJs for
            // why it's a repeated burst over the first few seconds rather than a single call.
            NodeRuntime.RaiseToFrontJs + "\n" +
            // Not awaited: the burst keeps lifting the window while the page loads underneath it,
            // which is exactly when the window loses the focus race and ends up buried.
            "  raiseBurst().catch(() => {});\n" +
            // A swallowed navigation failure is how the Facebook version of this produced a blank
            // window the seller watched for six minutes before being told they had cancelled it.
            // Report it instead.
            "  let navError = null;\n" +
            "  try {\n" +
            $"    await page.goto('{LoginUrl}', {{ waitUntil: 'domcontentloaded', timeout: 30000 }});\n" +
            "  } catch (e) { navError = String((e && e.message) || e).split('\\n')[0]; }\n" +
            "  const haveForm = await page.$('#userid, input[name=\"userid\"], #pass, input[name=\"pass\"]').catch(() => null);\n" +
            "  if (navError && !haveForm) {\n" +
            "    process.stdout.write('NAVFAIL: ' + navError);\n" +
            "    try { await browser.close(); } catch (_) {}\n" +
            "    return;\n" +
            "  }\n" +
            // eBay answers automated-looking traffic with a bot-check splash. It is solvable by the
            // person sitting there, so this is not a failure — but it must be NAMED, or the window
            // just looks broken while it waits.
            // The clock is measured from the last sign of progress, not from launch. A fixed
            // six-minute budget started counting before eBay had even decided to show a bot check,
            // so a seller who got the challenge two minutes in was solving it against a timer that
            // was already half gone — and the completed login was thrown away at the end. The
            // seller reads that as "Terapeak keeps disconnecting", because the file is only ever
            // written on reaching /sh/research.
            //
            // So: IDLE_MS of no movement ends it, any URL change restarts it, and the first sight
            // of a CAPTCHA grants a fresh CAPTCHA_MS from when the challenge actually appeared.
            // HARD_CAP_MS is the ceiling that keeps a walked-away-from window from living forever;
            // NodeRuntime's own timeout sits above it so this loop is always what ends the wait.
            "  const IDLE_MS = 6 * 60 * 1000, CAPTCHA_MS = 10 * 60 * 1000, HARD_CAP_MS = 20 * 60 * 1000;\n" +
            "  const startedAt = Date.now();\n" +
            "  let lastUrl = page.url();\n" +
            "  let sawCaptcha = lastUrl.includes('/splashui/captcha');\n" +
            "  let deadline = Date.now() + (sawCaptcha ? CAPTCHA_MS : IDLE_MS);\n" +
            "  let sinceFocus = 0;\n" +
            "  while (Date.now() < deadline && (Date.now() - startedAt) < HARD_CAP_MS) {\n" +
            "    if (!browser.isConnected()) break;\n" + // user closed the window manually
            "    const url = page.url();\n" +
            "    if (url.includes('/sh/research')) break;\n" +
            // Moving between pages is the seller working, so give them the full window again.
            "    if (url !== lastUrl) { lastUrl = url; deadline = Date.now() + IDLE_MS; }\n" +
            // Only on the FIRST sighting: re-arming every tick would pin the wait to the hard cap
            // whenever a challenge is left on screen.
            "    if (url.includes('/splashui/captcha') && !sawCaptcha) {\n" +
            "      sawCaptcha = true; deadline = Date.now() + CAPTCHA_MS;\n" +
            "    }\n" +
            "    await page.waitForTimeout(1000).catch(() => {});\n" +
            // Re-assert the window to the front every ~5s for the whole wait, not just once at
            // page load — a CAPTCHA/"verify you're human" interstitial can appear well after the
            // initial load, and by then the window may have lost focus (alt-tab, this app's own
            // window stealing it back, etc.), silently burning the whole 6-minute timeout with
            // the challenge never seen. AllowSetForegroundWindow only grants one "pass" up front,
            // but bringToFront() is Playwright's own in-browser focus call and keeps working
            // regardless — that's why it's the thing re-run here, not a second native win32 call.
            // Gentle tab-focus only during the wait — NOT the hard minimize/normal cycle, which
            // would visibly refresh the window every few seconds and interrupt the user mid-
            // CAPTCHA. The startup burst already brought the window to the front, while nobody
            // was typing yet.
            "    sinceFocus++;\n" +
            "    if (sinceFocus >= 8) { sinceFocus = 0; await raise(false); }\n" +
            "  }\n" +
            "  if (browser.isConnected() && page.url().includes('/sh/research')) {\n" +
            "    await page.waitForTimeout(1500);\n" +
            "    const state = await ctx.storageState();\n" +
            // Temp-file-then-rename rather than a straight write: a crash mid-save used to leave a
            // truncated session file, which reads as "not connected" and costs the seller the eBay
            // login they just finished.
            "    " + AtomicFile.NodeWriteJs(sessionPathEscaped, "JSON.stringify(state)") + "\n" +
            "    process.stdout.write('SAVED');\n" +
            "  } else {\n" +
            "    process.stdout.write(sawCaptcha ? 'CAPTCHA' : 'CANCELLED');\n" +
            "  }\n" +
            "  try { await browser.close(); } catch (_) {}\n" +
            "})();\n";

        try
        {
            // Grant foreground rights before launch so the Chrome window this spawns can raise
            // itself above whatever the user is currently looking at, instead of opening
            // silently behind it. Only needed here — ScrapeAsync's browser is headless.
            // Above the script's own HARD_CAP_MS (20 min) on purpose: the loop should always be
            // what ends the wait, so the outcome is a named token rather than a killed process.
            var run = await NodeRuntime.RunAsync(script, TimeSpan.FromMinutes(22), "terapeak_login",
                beforeStart: () =>
                {
                    LoginWindowFocus.Grant();
                    LoginWindowFocus.PinNewBrowserWindowBriefly();
                });

            if (run.TimedOut)
            {
                LastLoginError = "No login completed within 22 minutes.";
                log.Add("Warning", "Terapeak login timed out", LastLoginError);
                return;
            }

            var stdout = run.StdOut;
            var stderr = run.StdErr;

            if (stdout == "SAVED")
                log.Add("Info", "Terapeak connected", "Session saved — sold comps will now use real Terapeak data.");
            else
            {
                // The script's outcome tokens are for this method, not for a human. Passing them
                // through raw is how the UI ends up reporting "connect failed: CANCELLED".
                LastLoginError = stdout switch
                {
                    "CANCELLED" => "Login window was closed before signing in — click Connect and stay in the window until it closes itself.",
                    "CAPTCHA" => "eBay showed a \"verify you're human\" check instead of the sign-in form. "
                        + "Click Connect again and complete that check in the window — it closes itself once you're signed in.",
                    var s when s.StartsWith("NAVFAIL:") =>
                        $"eBay's sign-in page wouldn't load ({s["NAVFAIL:".Length..].Trim()}). "
                        + "This is eBay's end, not your account — try again in a minute.",
                    var s when !string.IsNullOrWhiteSpace(s) => s,
                    _ => string.IsNullOrWhiteSpace(stderr)
                        ? "Login window was closed before signing in."
                        : stderr,
                };
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
            Volatile.Write(ref _loginInProgress, 0);
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
