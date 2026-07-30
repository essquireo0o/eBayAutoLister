using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Terapeak (eBay Seller Hub's sold-comps research tool) has no public API — it's a regular
/// logged-in website, authenticated by browser cookies, not the OAuth tokens used elsewhere in
/// this app. This service pops a real (visible) browser window once for the seller to log into
/// eBay normally, saves that session to disk, then reuses it headlessly to read real sold-comp
/// data straight off the rendered Seller Hub page.
/// </summary>
public class TerapeakService
{
    private readonly string _sessionPath;
    private readonly string _profileDir;
    private readonly ActionLog log;

    /// <summary>
    /// Where the session and the browser profile live, and both are named through
    /// <see cref="AppPaths"/> rather than composed from <c>ContentRootPath</c>.
    /// </summary>
    /// <remarks>
    /// Those resolve to the same folder today — <c>ContentRootPath</c> is set to
    /// <see cref="AppPaths.DataHome"/> at startup — but only by arrangement, and a hosting property
    /// is exactly the kind of thing that gets changed for an unrelated reason. When it moved before,
    /// bin\Debug, a copied build and the installed app each looked in their own folder. For the
    /// session file that meant being told to connect Terapeak again for a login sitting on disk the
    /// whole time; for the profile directory it is worse, because the cookies actually travel in
    /// there — a build looking one folder over hands eBay a browser it has never seen, which is what
    /// a stolen session looks like. Saying both paths outright removes the possibility.
    /// </remarks>
    public TerapeakService(ActionLog log)
        : this(AppPaths.TerapeakSessionPath, AppPaths.TerapeakProfilePath, log) { }

    /// <summary>Explicit-path constructor, for tests and for anything that keeps its session elsewhere.</summary>
    public TerapeakService(string sessionPath, string profileDir, ActionLog log)
    {
        _sessionPath = sessionPath;
        _profileDir  = profileDir;
        this.log     = log;
    }

    /// <summary>
    /// The Chrome profile this app keeps for eBay. Login, verification and scrape all open THIS
    /// directory.
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
    public string ProfileDirectory => _profileDir;

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
    public const string ResearchUrl = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD";

    /// <summary>
    /// eBay's CURRENT sign-in page. The legacy <c>ws/eBayISAPI.dll?SignIn</c> entry point this used
    /// to open no longer serves a sign-in form at all: measured 2026-07-30 in real Chrome, it
    /// redirects to <c>/splashui/captcha</c> with no username or password field anywhere on the
    /// page, with or without the <c>ru=</c> parameter. The seller was being handed a bot check and
    /// an eBay error page and told their login had failed — nothing to do with their password.
    ///
    /// The same test against <c>signin.ebay.com/signin</c> returned HTTP 200 with the real sign-in
    /// form, both bare and with <c>ru=</c>. So this is the door that is still open.
    /// </summary>
    private static string LoginUrl =>
        "https://signin.ebay.com/signin?ru=" + Uri.EscapeDataString(ResearchUrl);

    /// <summary>The button the UI puts on a dead Terapeak session. Recognised by FIX_ACTIONS in app.js.</summary>
    public const string ConnectFixAction = "connect-terapeak";

    // Windows blocks a background process from stealing focus by default — without a foreground
    // grant the login browser can open behind the app window with no visible indication it
    // appeared at all. See LoginWindowFocus for that grant and the rest of the raise story.

    // node.exe resolution, the Playwright package directory and the run-a-throwaway-script
    // plumbing all live in NodeRuntime now — FacebookMarketplaceService needs the identical
    // setup for the same reason (no public search API, so drive a real logged-in browser).
    private static string PlaywrightDir => NodeRuntime.PlaywrightDir;

    /// <summary>
    /// What the saved session on disk actually is — five states, not <c>File.Exists</c>. A zero-byte
    /// or truncated file used to count as connected, which turned "your login is broken" into a
    /// sold-comps lookup that quietly found nothing. See <see cref="TerapeakSessionFile"/>.
    /// </summary>
    public TerapeakSessionStatus Session => TerapeakSessionFile.Inspect(_sessionPath);

    public bool IsConnected => Session.CanSearch;

    /// <summary>True when the button to offer is Reconnect rather than Connect.</summary>
    public bool NeedsReconnect => Session.NeedsReconnect;

    /// <summary>Where the saved storageState lives. Exposed so ConnectionDoctor can load it into a
    /// headless context and actually test it, rather than settling for "the file is there".</summary>
    public string SessionPath => _sessionPath;

    public bool IsLoginInProgress => Volatile.Read(ref _loginInProgress) == 1;
    public string? LastLoginError { get; private set; }

    /// <summary>
    /// What happened the last time a fresh login was tested against the live research page. Null
    /// until a login has completed in this process.
    /// </summary>
    public TerapeakVerification? LastVerification { get; private set; }

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

    /// <summary>Ceiling on one headless page load — the post-login check and every research call.</summary>
    /// <remarks>
    /// Every wait underneath it is bounded too (see <see cref="BuildPageScript"/>). This is the
    /// backstop, not the mechanism: a script that always ends by itself reports a named reason,
    /// where a killed process only ever reports "timed out".
    /// </remarks>
    public static TimeSpan PageTimeout => TimeSpan.FromSeconds(60);

    private async Task RunLoginProcessAsync()
    {
        try
        {
            // Both folders, made before node is asked to write into them: on a machine where the
            // data home had never been created, the save threw at the last instant of a completed
            // login and reported it as a cancelled one.
            Directory.CreateDirectory(_profileDir);
            var sessionDir = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(sessionDir)) Directory.CreateDirectory(sessionDir);

            var script = BuildLoginScript(NodeRuntime.JsPath(PlaywrightDir), NodeRuntime.JsPath(_sessionPath),
                                          NodeRuntime.JsPath(_profileDir));

            // Grant foreground rights before launch so the Chrome window this spawns can raise
            // itself above whatever the user is currently looking at, instead of opening
            // silently behind it. Only needed here — the headless runs below have no window.
            // Above the script's own HARD_CAP_MS on purpose: the loop should always be
            // what ends the wait, so the outcome is a named token rather than a killed process.
            var run = await NodeRuntime.RunAsync(script, ProcessTimeout, "terapeak_login",
                beforeStart: LoginWindowFocus.PrepareForLoginWindow);

            if (run.TimedOut)
            {
                LastLoginError = $"No login completed within {ProcessTimeout.TotalMinutes:0} minutes.";
                log.Add("Warning", "Terapeak login timed out", LastLoginError);
                return;
            }

            var stdout = run.StdOut;
            var stderr = run.StdErr;

            if (stdout == "SAVED")
            {
                await ConfirmFreshLoginAsync();
                return;
            }

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
                // Two different situations wear this token, and only one of them is solvable by
                // trying harder. A check that APPEARS is a check a person can clear. A check that
                // replaces the sign-in form every time means eBay has flagged this browser profile,
                // and no amount of retyping a password reaches a password box — so the way out has
                // to be named here, or the seller retries forever.
                "CAPTCHA" => "eBay showed a \"verify you're human\" check instead of the sign-in form. "
                    + $"Complete it in the window — you get {CaptchaMinutes} minutes from when it appears, "
                    + "and the window closes itself once you're signed in. "
                    + "If you keep landing on that check and never see a sign-in form, eBay has flagged "
                    + "this app's browser profile: click Disconnect, then Connect, which starts a new "
                    + "one eBay has not seen before.",
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
    /// Tests the session that was just saved by loading the research page headlessly, and only then
    /// reports the connection as working.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The old success condition was "the login script wrote a file". That is not the same claim,
    /// and the gap between them is where "Terapeak says connected but every lookup is empty" lived.
    /// A storageState gets written for a browser that reached /sh/research once; whether the profile
    /// still works from a headless launch a second later is a separate question, and it is the only
    /// one the seller cares about. eBay answers no to it routinely — a subscription the account
    /// doesn't have, a challenge the headless browser draws that the visible one didn't, a sign-in
    /// that only half-completed.
    /// </para>
    /// <para>
    /// A verdict of "couldn't tell" is kept distinct from "no". The session is left in place and the
    /// seller is told what happened, because deleting a login over an inconclusive probe costs them
    /// the six minutes it took to make and fixes nothing.
    /// </para>
    /// </remarks>
    private async Task ConfirmFreshLoginAsync()
    {
        var verification = await VerifySessionAsync();
        LastVerification = verification;

        if (verification.Verified)
        {
            // A fresh, confirmed session starts clean: leaving a marker from the login that died
            // would have the very next lookup report the one that just succeeded as expired.
            TerapeakSessionFile.ClearExpiredMarker(_sessionPath);
            log.Add("Info", "Terapeak connected",
                "Session saved and checked against the live research page — sold comps will now use real Terapeak data.");
            return;
        }

        if (TerapeakResearchPage.NeedsReconnect(verification.Wall))
        {
            TerapeakSessionFile.MarkExpired(_sessionPath, $"post-login check: {verification.Wall}");
            LastLoginError = verification.Detail;
            log.Add("Warning", "Terapeak login didn't take", verification.Detail);
            return;
        }

        // Inconclusive: a bot check, a page that never rendered, no Node runtime. The session stays.
        LastLoginError = verification.Detail;
        log.Add("Warning", "Terapeak connected but unconfirmed", verification.Detail);
    }

    /// <summary>
    /// Loads the research page in a headless browser on the saved profile and says what eBay served.
    /// Never throws — an inconclusive answer is a result, not an error.
    /// </summary>
    public async Task<TerapeakVerification> VerifySessionAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var page = await LoadPageAsync(ResearchUrl, screenshotPath: null, ct);
        if (page.Error is not null)
            return new TerapeakVerification(false, TerapeakPageWall.None,
                $"The saved session couldn't be tested — the headless browser didn't finish ({page.Error}). "
                + "That says nothing about whether the login is good; try a sold-comps lookup, or use Check connections in Settings.",
                now);

        var wall = TerapeakResearchPage.DetectWall(page.Url, page.BodyText);
        if (wall != TerapeakPageWall.None)
            return new TerapeakVerification(false, wall, TerapeakResearchPage.Describe(wall), now);

        if (!TerapeakResearchPage.LooksLikeResearchPage(page.Url, page.BodyText))
            return new TerapeakVerification(false, TerapeakPageWall.None,
                "eBay served neither the research page nor a sign-in page when the new session was tested, "
                + "so there's no verdict to report. Try a sold-comps lookup — if it comes back empty, reconnect.",
                now);

        return new TerapeakVerification(true, TerapeakPageWall.None,
            "The saved session was replayed in a headless browser and eBay served the research page.", now);
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
    /// <para>
    /// Every await in the loop is bounded, including the ones that look instant. <c>page.evaluate</c>
    /// against a tab whose renderer has wedged, and <c>ctx.storageState()</c> on a context mid-crash,
    /// both hang indefinitely with no timeout of their own — and an await that never returns inside
    /// the wait loop outlives every deadline the loop is checking, because the loop is not running
    /// any more. That is a login window that sits open forever with a status that never changes.
    /// </para>
    /// </remarks>
    public static string BuildLoginScript(string playwrightDir, string sessionPath, string profileDirEscaped) =>
        $"const {{ chromium }} = require('{playwrightDir}');\n" +
        "(async () => {\n" +
        $"  const RESEARCH_URL = '{ResearchUrl}';\n" +
        // Nothing in here may wait forever. Every await below is either given an explicit timeout by
        // Playwright or wrapped in this, which resolves to the fallback rather than hanging.
        BoundedAwaitJs + "\n" +
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
        // Bounded: storageState() has no timeout of its own, and a context caught mid-crash never
        // resolves it — which stalls the loop past every deadline it is checking.
        "    const state = await bounded(ctx.storageState(), 20000, null);\n" +
        "    if (!state) return false;\n" +
        "    " + AtomicFile.NodeWriteJs(sessionPath, "JSON.stringify(state)") + "\n" +
        "    saved = true;\n" +
        "    return true;\n" +
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
        // Bounded: evaluate() against a wedged renderer never settles, and the .catch() below only
        // covers a rejection — it does nothing at all for a promise that simply never resolves.
        "    for (const p of pages) {\n" +
        "      const seen = await bounded(p.evaluate(() => window.__ingActivity || 0).catch(() => 0), 5000, 0);\n" +
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

    /// <summary>
    /// <c>bounded(promise, ms, fallback)</c> — resolves to <c>fallback</c> if the promise has not
    /// settled in time, instead of waiting on it forever.
    /// </summary>
    /// <remarks>
    /// Playwright gives most calls a timeout option, but not all of them: <c>storageState()</c> and
    /// <c>evaluate()</c> have none, and neither does a <c>close()</c> on a browser that has stopped
    /// answering. A <c>.catch()</c> is not a substitute — it handles a promise that rejects, and the
    /// failure here is a promise that never settles at all. The timer is unref'd so a script that
    /// has finished its work exits immediately rather than idling until the last timeout fires.
    /// </remarks>
    public const string BoundedAwaitJs = """
          function bounded(promise, ms, fallback) {
            let timer = null;
            const guard = new Promise((resolve) => {
              timer = setTimeout(() => resolve(fallback), ms);
              if (timer && typeof timer.unref === 'function') timer.unref();
            });
            return Promise.race([
              Promise.resolve(promise).catch(() => fallback),
              guard,
            ]).then((value) => { try { clearTimeout(timer); } catch (_) {} return value; });
          }
        """;

    public void Disconnect()
    {
        // The backup and the expired marker go too. Leaving the .bak behind means the next
        // inspection recovers from it and reports the session the seller just disconnected as
        // connected — see TerapeakSessionFile.Delete.
        TerapeakSessionFile.Delete(_sessionPath);
        LastVerification = null;

        // And the browser profile, which is where the login actually lives now.
        //
        // Deleting only the session file left a seller with no way out of the one failure they
        // cannot fix by trying again: eBay flags the PROFILE. Measured 2026-07-30 — the same
        // sign-in URL, opened from this app's profile, redirected to /splashui/captcha with no
        // sign-in form on the page, while a fresh profile got the real form. Once that happens,
        // Disconnect → Connect reuses the flagged profile and lands on the bot check forever, and
        // the seller is told their password did not work.
        //
        // Disconnecting means "forget my login". The cookies are in here, so this is the half that
        // makes that true.
        ResetProfile();

        log.Add("Info", "Terapeak disconnected", "Saved session and browser profile removed.");
    }

    /// <summary>
    /// Throws away the Chrome profile so the next login starts as a browser eBay has never seen.
    /// Best-effort: a locked file is not worth failing a disconnect over, and the next launch
    /// recreates whatever is missing.
    /// </summary>
    public void ResetProfile()
    {
        try
        {
            if (Directory.Exists(_profileDir)) Directory.Delete(_profileDir, recursive: true);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Terapeak profile not fully cleared",
                $"{ex.Message} — close any leftover login window and disconnect again.");
        }
    }

    // ── Headless research using the saved session ─────────────────────────────

    public async Task<TerapeakScrapeResult> ScrapeAsync(string query, CancellationToken ct = default)
    {
        // The verdict reached by reading a file, before a browser is launched for it. An expired
        // session answers instantly and says which button to press, rather than spending 40 seconds
        // to be handed the same sign-in page it was handed last time.
        var session = Session;
        switch (session.State)
        {
            case TerapeakSessionState.Valid:
                break;

            case TerapeakSessionState.Expired:
                return Expired($"{session.Reason} Reconnect Terapeak in Settings to use its sold data again.",
                               TerapeakPageWall.SignIn);

            case TerapeakSessionState.Missing:
                return new TerapeakScrapeResult
                {
                    Status = "not_connected",
                    Reason = "Terapeak has never been connected on this machine — it needs a one-time eBay sign-in.",
                    FixAction = ConnectFixAction,
                };

            // Empty and Malformed: the login was made and the file did not survive being written.
            default:
                return new TerapeakScrapeResult
                {
                    Status = "not_connected",
                    Reason = $"{session.Reason} Reconnect Terapeak in Settings to save a fresh one.",
                    FixAction = ConnectFixAction,
                };
        }

        var debugShotPath = Path.Combine(AppPaths.DataHome, "generated-photos", $"terapeak_debug_{Guid.NewGuid():N}.png");
        var url = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords="
                + Uri.EscapeDataString(query);

        var page = await LoadPageAsync(url, debugShotPath, ct);
        if (page.Error is not null)
            return new TerapeakScrapeResult { Status = "error", Error = page.Error };

        var wall = TerapeakResearchPage.DetectWall(page.Url, page.BodyText);

        if (TerapeakResearchPage.NeedsReconnect(wall))
        {
            // No auto-reconnect here (removed 2026-07-15 along with the background scanner —
            // see Program.cs) — popping a login window as a side effect of any lookup,
            // including a passive on-demand one, is exactly the unattended behavior that
            // was turned off. Reconnecting is the user's call, made explicitly in Settings.
            //
            // Marked, not deleted: deleting made the next screen say "never connected", so the
            // seller went looking for a setting they had already set. See TerapeakSessionFile.
            TerapeakSessionFile.MarkExpired(_sessionPath, $"research call bounced to {wall}");
            log.Add("Warning", "Terapeak session expired", TerapeakResearchPage.Describe(wall));
            return Expired(TerapeakResearchPage.Describe(wall), wall);
        }

        // A bot check is not a logged-out page, and reconnecting does not clear one. Saying
        // "expired" here sends the seller through an interactive re-login to fix nothing — so this
        // is its own status, and the saved session is left exactly as it was.
        if (wall == TerapeakPageWall.Challenge)
        {
            log.Add("Warning", "Terapeak blocked by a bot check", TerapeakResearchPage.Describe(wall));
            return new TerapeakScrapeResult
            {
                Status = "challenged",
                Wall = wall,
                Reason = TerapeakResearchPage.Describe(wall),
                Url = page.Url,
                DebugScreenshotPath = debugShotPath,
            };
        }

        return new TerapeakScrapeResult
        {
            Status = "ok",
            BodyText = page.BodyText,
            Url = page.Url,
            DebugScreenshotPath = debugShotPath,
        };
    }

    private TerapeakScrapeResult Expired(string reason, TerapeakPageWall wall) => new()
    {
        Status = "session_expired",
        Wall = wall,
        Reason = reason,
        FixAction = ConnectFixAction,
    };

    /// <summary>One headless page load on the saved profile: where it ended up, and what it said.</summary>
    private sealed record PageLoad(string Url, string BodyText, string? Error);

    private async Task<PageLoad> LoadPageAsync(string url, string? screenshotPath, CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(_profileDir);
            if (screenshotPath is not null)
            {
                var shotDir = Path.GetDirectoryName(screenshotPath);
                if (!string.IsNullOrEmpty(shotDir)) Directory.CreateDirectory(shotDir);
            }
        }
        catch (Exception ex)
        {
            return new PageLoad("", "", $"Couldn't prepare the browser profile folder: {ex.Message}");
        }

        var script = BuildPageScript(NodeRuntime.JsPath(PlaywrightDir), NodeRuntime.JsPath(_profileDir), url,
                                     screenshotPath is null ? null : NodeRuntime.JsPath(screenshotPath));

        NodeRunResult run;
        try
        {
            run = await NodeRuntime.RunAsync(script, PageTimeout, "terapeak_page", ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            return new PageLoad("", "", $"The headless browser wouldn't start: {ex.Message}");
        }

        if (run.TimedOut)
            return new PageLoad("", "", $"The research page didn't load within {PageTimeout.TotalSeconds:0} seconds.");

        if (string.IsNullOrWhiteSpace(run.StdOut))
            return new PageLoad("", "", string.IsNullOrWhiteSpace(run.StdErr)
                ? "No output from the research page load."
                : run.StdErr.Trim());

        try
        {
            var root = JsonDocument.Parse(run.StdOut).RootElement;
            string Str(string prop) => root.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

            var error = Str("error");
            var pageUrl = Str("url");
            var body = Str("bodyText");

            // An error alongside a page that rendered is a detail, not a failure — a slow sub-resource
            // times out on eBay's research page routinely while the stats the parser wants are already
            // there. Only an error with nothing to show for it is a failed load.
            if (error.Length > 0 && pageUrl.Length == 0 && body.Length == 0)
                return new PageLoad("", "", error);

            return new PageLoad(pageUrl, body, null);
        }
        catch (JsonException)
        {
            return new PageLoad("", "", "The research page load returned output that wasn't valid JSON.");
        }
    }

    /// <summary>
    /// The headless page-load script, shared by the post-login check and every research call.
    /// Public, like <see cref="BuildLoginScript"/>, because it is JavaScript embedded in C# —
    /// nothing in the build type-checks a word of it, so TerapeakLoginScriptTests pins it instead.
    /// </summary>
    /// <remarks>
    /// Emits JSON on stdout and never throws out of the script: a load that failed is "we don't
    /// know", which is a different verdict from "the session is dead". Every wait is bounded — the
    /// navigation and the settle by Playwright's own timeouts, the rest by <c>bounded()</c> — so the
    /// outcome is always a reported reason rather than a node process killed at the ceiling.
    /// </remarks>
    public static string BuildPageScript(string playwrightDir, string profileDirEscaped, string url, string? screenshotPath) =>
        $"const {{ chromium }} = require('{playwrightDir}');\n" +
        "(async () => {\n" +
        "  const out = { url: '', bodyText: '', error: '' };\n" +
        BoundedAwaitJs + "\n" +
        "  let ctx = null, browser = null;\n" +
        "  try {\n" +
        // The SAME profile directory the login wrote to. Replaying storageState into a fresh
        // browser handed eBay its own cookies from a browser it had never seen, every single
        // scrape; reopening the profile is the same browser returning, which is what actually
        // happened. storageState is still written at login as the connected-marker this service
        // and ConnectionDoctor read, but it is no longer how the session travels.
        $"    ctx = await bounded(chromium.launchPersistentContext('{profileDirEscaped}', {{ channel: 'chrome', headless: true, viewport: {{ width: 1400, height: 1000 }} }}), 30000, null);\n" +
        "    if (!ctx) { process.stdout.write(JSON.stringify({ url: '', bodyText: '', error: 'The browser did not start within 30s.' })); return; }\n" +
        "    browser = ctx.browser() || { close: () => ctx.close() };\n" +
        // Belt and braces: any Playwright call that takes a timeout and was not given one here
        // still gets a ceiling, so a future edit cannot reintroduce an unbounded wait by omission.
        "    ctx.setDefaultTimeout(25000);\n" +
        "    ctx.setDefaultNavigationTimeout(25000);\n" +
        "    const page = ctx.pages()[0] || await ctx.newPage();\n" +
        "    try {\n" +
        $"      await page.goto('{url}', {{ waitUntil: 'domcontentloaded', timeout: 25000 }});\n" +
        // The stat tiles the parser reads are rendered by script after domcontentloaded, so a fixed
        // settle is still needed. Short and capped: a page that has not painted in this long is not
        // going to, and every extra second is spent on every single lookup.
        "      await page.waitForTimeout(3500);\n" +
        "    } catch (e) { out.error = String((e && e.message) || e).split('\\n')[0]; }\n" +
        "    out.url = String(page.url() || '');\n" +
        (screenshotPath is null
            ? ""
            : $"    await bounded(page.screenshot({{ path: '{screenshotPath}', fullPage: true, timeout: 15000 }}), 20000, null);\n") +
        // Bounded for the same reason it is in the login loop: evaluate() has no timeout of its own,
        // and a wedged renderer turns "read the page text" into a wait that outlives the process.
        "    out.bodyText = String(await bounded(page.evaluate(() => document.body.innerText), 15000, '') || '').slice(0, 15000);\n" +
        "  } catch (e) {\n" +
        "    if (!out.error) out.error = String((e && e.message) || e).split('\\n')[0];\n" +
        "  }\n" +
        "  try { await bounded(browser ? browser.close() : null, 10000, null); } catch (_) {}\n" +
        "  process.stdout.write(JSON.stringify(out));\n" +
        "})();\n";
}

/// <summary>
/// What a live check of the saved session found. <see cref="Verified"/> is the only thing that
/// justifies telling the seller they are connected.
/// </summary>
/// <param name="Wall">What eBay served instead, when it served something. <see cref="TerapeakPageWall.None"/> when nothing was in the way.</param>
/// <param name="Detail">One sentence for the seller.</param>
public sealed record TerapeakVerification(
    bool Verified, TerapeakPageWall Wall, string Detail, DateTimeOffset AtUtc)
{
    /// <summary>True when the right button is Reconnect — eBay said no, rather than "not now".</summary>
    public bool NeedsReconnect => !Verified && TerapeakResearchPage.NeedsReconnect(Wall);
}

public class TerapeakScrapeResult
{
    /// <summary>ok | not_connected | session_expired | challenged | error</summary>
    public string Status { get; set; } = "";
    public string BodyText { get; set; } = "";
    public string? DebugScreenshotPath { get; set; }
    public string? Error { get; set; }

    /// <summary>Where the page load actually ended up. Empty when nothing loaded.</summary>
    public string Url { get; set; } = "";

    /// <summary>What eBay put in front of the research page, when it put something there.</summary>
    public TerapeakPageWall Wall { get; set; } = TerapeakPageWall.None;

    /// <summary>The sentence to show a seller for a non-ok status.</summary>
    public string Reason { get; set; } = "";

    /// <summary>The button to offer with <see cref="Reason"/>. See TerapeakService.ConnectFixAction.</summary>
    public string FixAction { get; set; } = "";

    /// <summary>
    /// True when this lookup produced no sold data for a reason that is about the connection, not
    /// about the item. The distinction is the whole point: a caller that treats these as "no recent
    /// sales" prices the item off a sample of zero and reports it with a straight face.
    /// </summary>
    public bool IsConnectionProblem => Status is "not_connected" or "session_expired" or "challenged" or "error";
}
