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

    /// <summary>
    /// The Chrome profile this app keeps for eBay. Login and scrape both open THIS directory.
    /// </summary>
    /// <remarks>
    /// The cookies live here now rather than being replayed from a JSON file into a fresh browser on
    /// every scrape. eBay treats its own session cookie arriving from a browser it has never seen as
    /// a stolen one - new fingerprint, no history, no localStorage - and challenges or kills it. A
    /// profile directory is the same browser coming back, which is the truth.
    ///
    /// Separate from the seller's own Chrome profile on purpose: this app must never touch the
    /// browser they use themselves.
    /// </remarks>
    private readonly string _profileDir = Path.Combine(env.ContentRootPath, "terapeak-profile");

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

    /// <summary>Where the saved storageState lives. Exposed so ConnectionDoctor can load it into a
    /// headless context and actually test it, rather than settling for "the file is there".</summary>
    public string SessionPath => _sessionPath;

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

    // ── How long the seller gets ──────────────────────────────────────────────
    // Named, and named HERE, because "connect keeps timing out" is unanswerable while the numbers
    // are buried in a JavaScript string. They are quoted in the failure messages and asserted by
    // TerapeakLoginScriptTests.

    /// <summary>Quiet time that ends the wait. Typing, clicking and moving pages all reset it.</summary>
    public const int IdleMinutes = 6;

    /// <summary>Granted fresh the first time eBay shows a bot check, from when it appeared.</summary>
    public const int CaptchaMinutes = 10;

    /// <summary>Ceiling on a walked-away-from window, however busy it looks.</summary>
    public const int HardCapMinutes = 20;

    /// <summary>
    /// How often the session is tested over HTTP while the window is open. This is what catches a
    /// sign-in that finished somewhere the window watcher cannot see.
    /// </summary>
    public const int ProbeSeconds = 8;

    /// <summary>Kills the process. Above <see cref="HardCapMinutes"/> so the script always ends the wait itself.</summary>
    public static TimeSpan ProcessTimeout => TimeSpan.FromMinutes(HardCapMinutes + 2);

    private async Task RunLoginProcessAsync()
    {
        Directory.CreateDirectory(_profileDir);
        var script = BuildLoginScript(PlaywrightDir.Replace("\\", "\\\\"), _sessionPath.Replace("\\", "\\\\"), _profileDir.Replace("\\", "\\\\"));

        try
        {
            // Grant foreground rights before launch so the Chrome window this spawns can raise
            // itself above whatever the user is currently looking at, instead of opening
            // silently behind it. Only needed here — ScrapeAsync's browser is headless.
            // Above the script's own HARD_CAP_MS on purpose: the loop should always be
            // what ends the wait, so the outcome is a named token rather than a killed process.
            var run = await NodeRuntime.RunAsync(script, ProcessTimeout, "terapeak_login",
                beforeStart: () =>
                {
                    LoginWindowFocus.Grant();
                    LoginWindowFocus.PinNewBrowserWindowBriefly();
                });

            if (run.TimedOut)
            {
                LastLoginError = $"No login completed within {ProcessTimeout.TotalMinutes:0} minutes.";
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
                    "CANCELLED" => "The login window was closed before the sign-in finished — click Connect and leave the window open; it closes itself the moment you're in.",
                    // Named separately from CANCELLED because they are different failures with
                    // different fixes, and telling a seller who sat there for six minutes that they
                    // "closed the window" is how this ends up reported as broken.
                    "TIMEOUT" => $"Nothing happened in the login window for {IdleMinutes} minutes, so it gave up. "
                        + "Click Connect and finish signing in — the clock only runs while the window is idle, "
                        + "so typing, clicking and page changes all keep it alive.",
                    "CAPTCHA" => "eBay showed a \"verify you're human\" check instead of the sign-in form. "
                        + $"Click Connect again and complete that check in the window — you get {CaptchaMinutes} minutes "
                        + "from when it appears, and the window closes itself once you're signed in.",
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

    /// <summary>
    /// The interactive-login browser script. Built here rather than inline so the behaviour a
    /// seller depends on can be asserted — see TerapeakLoginScriptTests. It is JavaScript embedded
    /// in C#, so nothing else in the build type-checks a word of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three things used to end a login that had actually succeeded, all reported to the seller as
    /// "it timed out":
    /// </para>
    /// <list type="number">
    /// <item>The idle clock only reset on a URL CHANGE. eBay's sign-in is one URL through password,
    /// two-factor and "check your phone" — a seller hunting for a texted code sat on a single page
    /// while a six-minute timer ran out underneath them.</item>
    /// <item>Success was only ever read off the FIRST tab. A sign-in that finished in a popup or a
    /// second tab left this watching a page that would never change.</item>
    /// <item>Success required landing on /sh/research, and the session was written only at the very
    /// end. Sign in, get dropped on the eBay home page, close the window — and the login was thrown
    /// away, which reads as "Terapeak keeps disconnecting".</item>
    /// </list>
    /// <para>
    /// So now: typing counts as progress, every live tab is watched, and the real question — "is
    /// this browser signed in yet?" — is asked over HTTP with the context's own cookies every few
    /// seconds. That probe costs no visible tab, cannot steal focus mid-CAPTCHA, and lets the
    /// session be saved the INSTANT it exists rather than at the end of a wait the seller may
    /// never sit through.
    /// </para>
    /// </remarks>
    public static string BuildLoginScript(string playwrightDir, string sessionPath, string profileDirEscaped) =>
        $"const {{ chromium }} = require('{playwrightDir}');\n" +
        "(async () => {\n" +
        $"  const RESEARCH_URL = '{ResearchUrl}';\n" +
        // The real installed Chrome (not Playwright's bundled "Chrome for Testing" build)
        // reports a normal, self-consistent fingerprint — eBay's bot detection flags the
        // bundled test browser much more readily, especially after repeated automated hits.
        // launchPersistentContext, not launch+newContext: the profile directory keeps eBay's cookies,
        // localStorage and this browser's identity on disk between runs. The scrape below opens the
        // SAME directory, so eBay sees one browser that keeps coming back rather than its cookies
        // turning up in a brand-new browser every time - which is what a stolen session looks like,
        // and why the connection kept being challenged and dropped.
        $"  const ctx = await chromium.launchPersistentContext('{profileDirEscaped}', {{ channel: 'chrome', headless: false, viewport: null, args: ['--disable-blink-features=AutomationControlled', '--start-maximized'] }});\n" +
        "  const browser = ctx.browser() || { close: () => ctx.close(), isConnected: () => true };\n" +
        "  await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });\n" +
        // The activity beacon. A seller typing a password or a two-factor code is making progress
        // even though the URL has not moved once — without this, they are racing a clock they
        // cannot see and have no way to reset.
        "  await ctx.addInitScript(() => {\n" +
        "    try {\n" +
        "      window.__ingActivity = Date.now();\n" +
        "      const bump = () => { try { window.__ingActivity = Date.now(); } catch (_) {} };\n" +
        "      for (const t of ['keydown','pointerdown','click','input','change','scroll'])\n" +
        "        window.addEventListener(t, bump, true);\n" +
        "    } catch (_) {}\n" +
        "  });\n" +
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
        // just looks broken while it waits. The first sighting grants a fresh CAPTCHA_MS from when
        // the challenge actually appeared, because a seller who gets the challenge two minutes in
        // was otherwise solving it against a timer that was already half gone.
        $"  const IDLE_MS = {IdleMinutes} * 60 * 1000, CAPTCHA_MS = {CaptchaMinutes} * 60 * 1000, " +
        $"HARD_CAP_MS = {HardCapMinutes} * 60 * 1000, PROBE_MS = {ProbeSeconds} * 1000;\n" +
        "  const startedAt = Date.now();\n" +
        "  let lastUrl = page.url();\n" +
        "  let sawCaptcha = lastUrl.includes('/splashui/captcha');\n" +
        "  let deadline = Date.now() + (sawCaptcha ? CAPTCHA_MS : IDLE_MS);\n" +
        "  let sinceFocus = 0, lastActivity = 0, lastProbe = Date.now(), saved = false, closed = false;\n" +
        "  const livePages = () => ctx.pages().filter(p => !p.isClosed());\n" +
        // Temp-file-then-rename rather than a straight write: a crash mid-save used to leave a
        // truncated session file, which reads as "not connected" and costs the seller the eBay
        // login they just finished.
        "  async function saveSession() {\n" +
        "    const state = await ctx.storageState();\n" +
        "    " + AtomicFile.NodeWriteJs(sessionPath, "JSON.stringify(state)") + "\n" +
        "    saved = true;\n" +
        "  }\n" +
        // The only question that actually matters, asked the only way that cannot be fooled by
        // which tab is in front: fetch the research page with this browser's cookies and see
        // whether eBay serves it or bounces us to sign-in.
        "  async function signedIn() {\n" +
        "    try {\n" +
        "      const res = await ctx.request.get(RESEARCH_URL, { timeout: 15000 });\n" +
        "      const u = String(res.url() || '');\n" +
        "      return res.ok() && u.includes('/sh/research') && !u.includes('signin');\n" +
        "    } catch (_) { return false; }\n" +
        "  }\n" +
        "  while (Date.now() < deadline && (Date.now() - startedAt) < HARD_CAP_MS) {\n" +
        "    if (!browser.isConnected()) { closed = true; break; }\n" +
        "    const pages = livePages();\n" +
        "    if (!pages.length) { closed = true; break; }\n" +
        // Any tab reaching the research page is the fast path — no probe needed.
        "    if (pages.some(p => String(p.url() || '').includes('/sh/research'))) { await saveSession(); break; }\n" +
        "    const url = String(pages[pages.length - 1].url() || '');\n" +
        // Moving between pages is the seller working, so give them the full window again.
        "    if (url !== lastUrl) { lastUrl = url; deadline = Date.now() + IDLE_MS; }\n" +
        // Only on the FIRST sighting: re-arming every tick would pin the wait to the hard cap
        // whenever a challenge is left on screen.
        "    if (url.includes('/splashui/captcha') && !sawCaptcha) {\n" +
        "      sawCaptcha = true; deadline = Date.now() + CAPTCHA_MS;\n" +
        "    }\n" +
        // Typing on a page that never changes URL is the commonest way to sign in. It counts.
        "    for (const p of pages) {\n" +
        "      const seen = await p.evaluate(() => window.__ingActivity || 0).catch(() => 0);\n" +
        "      if (seen > lastActivity) { lastActivity = seen; deadline = Math.max(deadline, seen + IDLE_MS); }\n" +
        "    }\n" +
        // Have they finished signing in somewhere this loop cannot see? Save the moment they have,
        // so closing the window a second later cannot throw the login away.
        "    if (Date.now() - lastProbe >= PROBE_MS) {\n" +
        "      lastProbe = Date.now();\n" +
        "      if (await signedIn()) { await saveSession(); break; }\n" +
        "    }\n" +
        "    await page.waitForTimeout(1000).catch(() => {});\n" +
        // Re-assert the window to the front every ~8s for the whole wait, not just once at
        // page load — a CAPTCHA/"verify you're human" interstitial can appear well after the
        // initial load, and by then the window may have lost focus (alt-tab, this app's own
        // window stealing it back, etc.), silently burning the whole timeout with the challenge
        // never seen. AllowSetForegroundWindow only grants one "pass" up front, but
        // bringToFront() is Playwright's own in-browser focus call and keeps working regardless —
        // that's why it's the thing re-run here, not a second native win32 call.
        // Gentle tab-focus only during the wait — NOT the hard minimize/normal cycle, which would
        // visibly refresh the window every few seconds and interrupt the user mid-CAPTCHA.
        "    sinceFocus++;\n" +
        "    if (sinceFocus >= 8) { sinceFocus = 0; await raise(false); }\n" +
        "  }\n" +
        // Last look before giving up: the wait may have run out at the exact moment the sign-in
        // landed, and a session that exists is a session worth keeping.
        "  if (!saved && browser.isConnected() && await signedIn()) await saveSession();\n" +
        "  process.stdout.write(saved ? 'SAVED' : (closed ? 'CANCELLED' : (sawCaptcha ? 'CAPTCHA' : 'TIMEOUT')));\n" +
        "  try { await browser.close(); } catch (_) {}\n" +
        "})();\n";

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
        Directory.CreateDirectory(_profileDir);
        var profileDirEscaped = _profileDir.Replace("\\", "\\\\");
        var debugShotPath = Path.Combine(env.ContentRootPath, "generated-photos", $"terapeak_debug_{Guid.NewGuid():N}.png");
        var debugShotEscaped = debugShotPath.Replace("\\", "\\\\");
        var url = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords=" + Uri.EscapeDataString(query);

        var script =
            $"const {{ chromium }} = require('{pwPath}');\n" +
            "(async () => {\n" +
            // The SAME profile directory the login wrote to. Replaying storageState into a fresh
            // browser handed eBay its own cookies from a browser it had never seen, every single
            // scrape; reopening the profile is the same browser returning, which is what actually
            // happened. storageState is still written at login as the connected-marker this service
            // and ConnectionDoctor read, but it is no longer how the session travels.
            $"  const ctx = await chromium.launchPersistentContext('{profileDirEscaped}', {{ channel: 'chrome', headless: true, viewport: {{ width: 1400, height: 1000 }} }});\n" +
            "  const browser = ctx.browser() || { close: () => ctx.close() };\n" +
            "  const page = ctx.pages()[0] || await ctx.newPage();\n" +
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
