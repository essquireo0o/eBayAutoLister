using System.Diagnostics;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Facebook login and scrape scripts are JavaScript embedded in C#, so nothing else in the
/// build reads a word of them: a typo ships, and the seller sees "Facebook keeps disconnecting".
///
/// These lock the properties a connection that sticks actually depends on — the real Chrome, an
/// atomic session save, a bounded wait, and a browser that failed to start being reported as a
/// browser that failed to start rather than as an empty local market.
/// </summary>
public class FacebookScrapeScriptTests
{
    private static readonly string LoginJs =
        FacebookMarketplaceService.BuildLoginScript(@"C:\pw\playwright", @"C:\data\facebook-session.json");

    private static readonly string SearchJs = FacebookMarketplaceService.BuildSearchScript(
        @"C:\pw\playwright", @"C:\data\facebook-session.json",
        FacebookMarketplaceSelectors.ToJson("antminer", 40, "89101"));

    private static readonly string PicksJs = FacebookMarketplaceService.BuildPicksScript(
        @"C:\pw\playwright", @"C:\data\facebook-session.json",
        FacebookMarketplaceSelectors.ToJson("", 40, ""), FacebookMarketplaceSelectors.PicksUrl);

    // ── Capture ──────────────────────────────────────────────────────────────

    // Playwright's bundled "Chrome for Testing" build draws far more aggressive challenges from
    // Facebook, and a challenge the seller cannot clear means no session at all.
    [Fact]
    public void The_login_opens_the_real_installed_Chrome()
    {
        Assert.Contains("channel: 'chrome'", LoginJs);
        Assert.Contains("headless: false", LoginJs);
    }

    // c_user is Facebook's own signed-in marker. Waiting on the cookie rather than on a URL is what
    // stops a half-finished session being saved mid-2FA, when the URL already looks fine.
    [Fact]
    public void The_login_waits_for_the_sign_in_cookie_not_for_a_URL()
    {
        Assert.Contains("c_user", LoginJs);
        Assert.Contains("async function signedIn()", LoginJs);
    }

    // The failure this prevents: a crash — or a second login window — landing inside the write,
    // leaving a truncated session file and costing the seller the login they just completed.
    [Fact]
    public void The_session_is_written_to_a_temp_file_and_renamed_into_place()
    {
        Assert.Contains("renameSync", LoginJs);
        Assert.Contains(".bak", LoginJs);
        Assert.DoesNotContain("writeFileSync(target,", LoginJs.Replace(" ", ""));
        // Per-process temp: two windows open at once must not share one .tmp.
        Assert.Contains("process.pid", LoginJs);
        // Flushed to the device before the swap, or a power cut leaves a correctly-named file of
        // zeros — the same lost login by a slower route.
        Assert.Contains("fsyncSync", LoginJs);
    }

    [Fact]
    public void A_browser_that_will_not_start_is_named_rather_than_left_as_a_closed_window()
    {
        foreach (var js in new[] { LoginJs, SearchJs, PicksJs })
        {
            Assert.Contains(FailureTranslator.PlaywrightMissingLabel, js);
            Assert.Contains(FailureTranslator.ChromeMissingLabel, js);
            Assert.Contains(FailureTranslator.ChromeBusyLabel, js);
            // The require itself is guarded: a missing package throws at load, which used to kill
            // the script before it could say anything at all.
            Assert.Contains("function requirePlaywright", js);
        }
    }

    // ── Reuse ────────────────────────────────────────────────────────────────

    [Fact]
    public void Every_scrape_replays_the_saved_session()
    {
        Assert.Contains(@"storageState: 'C:\\data\\facebook-session.json'", SearchJs);
        Assert.Contains(@"storageState: 'C:\\data\\facebook-session.json'", PicksJs);
    }

    // Without this the only evidence of a login wall was "no tiles found", which is exactly what a
    // thin local market looks like.
    [Fact]
    public void Every_scrape_reports_the_page_it_actually_landed_on()
    {
        foreach (var js in new[] { SearchJs, PicksJs })
        {
            Assert.Contains("out.url = page.url()", js);
            Assert.Contains("out.pageSignature = await pageSignature(page)", js);
        }
    }

    // A rendered Marketplace page is megabytes of markup, and none of it needs to cross a process
    // boundary to answer one yes/no question.
    [Fact]
    public void The_page_signature_is_capped_inside_the_page()
    {
        // The cap is applied in page.evaluate, before anything crosses the boundary.
        Assert.Contains("text.slice(0, 1500)", SearchJs);
        Assert.Contains("await page.evaluate(", SearchJs);
        // And it is a signature, not the page: nothing here ships raw outerHTML back.
        Assert.DoesNotContain("outerHTML", SearchJs);
    }

    // ── Bounded ──────────────────────────────────────────────────────────────

    [Fact]
    public void Nothing_waits_forever()
    {
        foreach (var js in new[] { SearchJs, PicksJs })
        {
            Assert.Contains("ctx.setDefaultTimeout(", js);
            Assert.Contains("ctx.setDefaultNavigationTimeout(", js);
            Assert.Contains("const watchdog = setTimeout(", js);
            // The normal path must cancel it, or node sits on an idle timer after the work is done.
            Assert.Contains("clearTimeout(watchdog)", js);
        }
    }

    // The script's own deadline sits UNDER the process timeout on purpose: a killed process has no
    // payload, and no payload reaches the seller as "No output from the Marketplace search".
    [Fact]
    public void The_script_deadline_is_under_the_process_timeout_that_backs_it_up()
    {
        Assert.True(FacebookMarketplaceService.SearchWatchdogMs
                    < FacebookMarketplaceService.SearchProcessTimeout.TotalMilliseconds);
        Assert.True(FacebookMarketplaceService.PicksWatchdogMs
                    < FacebookMarketplaceService.PicksProcessTimeout.TotalMilliseconds);
        Assert.Contains($"}}, {FacebookMarketplaceService.SearchWatchdogMs});", SearchJs);
        Assert.Contains($"}}, {FacebookMarketplaceService.PicksWatchdogMs});", PicksJs);
    }

    // stdout to a pipe is asynchronous: exiting before the flush truncates the payload, which is the
    // same "no output" failure by another route.
    [Fact]
    public void A_watchdog_exit_still_flushes_its_payload()
    {
        Assert.Contains("process.stdout.write(JSON.stringify(out), () => { if (exit) process.exit(0); })", SearchJs);
    }

    // The config carries the seller's own search text, so it is substituted LAST — otherwise a
    // query containing a placeholder would be substituted into, and the session path is one of the
    // things it could be replaced with.
    [Fact]
    public void The_config_is_substituted_last_so_nothing_in_it_is_substituted_into()
    {
        var js = FacebookMarketplaceService.BuildSearchScript(
            @"C:\pw\playwright", @"C:\data\facebook-session.json", """{"note":"%%SESSION%% %%WATCHDOG%%"}""");

        Assert.Contains("""{"note":"%%SESSION%% %%WATCHDOG%%"}""", js);   // untouched
        Assert.Contains(@"storageState: 'C:\\data\\facebook-session.json'", js); // and the real one filled in
    }

    // ── The scripts are actually JavaScript ──────────────────────────────────

    [Theory]
    [InlineData("login")]
    [InlineData("search")]
    [InlineData("picks")]
    public void The_scripts_parse(string which)
    {
        var js = which switch { "login" => LoginJs, "search" => SearchJs, _ => PicksJs };
        var file = Path.Combine(Path.GetTempPath(), $"fbmarket_syntax_{Guid.NewGuid():N}.cjs");
        File.WriteAllText(file, js);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(NodeRuntime.NodeExe, $"--check \"{file}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return; // no node on this machine — nothing to check against
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            Assert.True(proc.ExitCode == 0, $"{which} script is not valid JavaScript:\n{stderr}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // node isn't installed here; the app surfaces that separately when a search is run.
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    // ── The three machine problems, each with its own message ────────────────

    // All three used to arrive as one indistinguishable "couldn't launch the browser" — or worse, as
    // a search that returned nothing — and they have three different fixes.
    [Fact]
    public void A_missing_Playwright_package_is_not_a_missing_Chrome()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException("PLAYWRIGHT_MISSING: Cannot find module 'C:\\pw\\playwright'"),
            FailureDomain.Browser);

        Assert.Equal(FailureKind.ToolMissing, failure.Kind);
        Assert.Contains("npm install", failure.WhatToDo);
        Assert.DoesNotContain("Chrome", failure.Headline);
        Assert.False(failure.Retryable);
    }

    [Fact]
    public void A_missing_Chrome_says_to_install_Chrome()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException(
                "CHROME_MISSING: Chromium distribution 'chrome' is not found at C:\\Program Files\\Google\\Chrome"),
            FailureDomain.Browser);

        Assert.Equal(FailureKind.ToolMissing, failure.Kind);
        Assert.Contains("Chrome", failure.Headline);
        Assert.False(failure.Retryable);
    }

    // The one browser failure that fixes itself, so the one that gets a Retry button.
    [Fact]
    public void A_Chrome_already_holding_the_profile_is_retryable()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException("CHROME_BUSY: Failed to launch: ProcessSingleton lock held"),
            FailureDomain.Browser);

        Assert.Equal(FailureKind.BrowserBusy, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("Close", failure.WhatToDo);
    }

    // A dead session is never retryable: no number of attempts replaces a sign-in only the seller
    // can complete, so it gets the button that opens one instead.
    [Fact]
    public void An_expired_session_offers_reconnect_rather_than_retry()
    {
        var failure = FailureTranslator.Translate(
            new InvalidOperationException("SESSION_EXPIRED: bounced to Login"), FailureDomain.Browser);

        Assert.Equal(FailureKind.SessionExpired, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Equal(FacebookMarketplaceService.ConnectFixAction, failure.FixAction);
    }

    [Fact]
    public void Only_a_launch_failure_counts_as_a_launch_failure()
    {
        Assert.True(FailureTranslator.IsBrowserLaunchFailure("CHROME_BUSY: ProcessSingleton"));
        Assert.True(FailureTranslator.IsBrowserLaunchFailure("PLAYWRIGHT_MISSING: Cannot find module"));
        Assert.True(FailureTranslator.IsBrowserLaunchFailure("CHROME_MISSING: not found at"));
        Assert.True(FailureTranslator.IsBrowserLaunchFailure("BROWSER_LAUNCH_FAILED: something else"));

        // A page that loaded and went wrong afterwards is a different story, and must not be
        // reported to the seller as "install Chrome".
        Assert.False(FailureTranslator.IsBrowserLaunchFailure("Timeout 15000ms exceeded waiting for selector"));
        Assert.False(FailureTranslator.IsBrowserLaunchFailure(null));
        Assert.False(FailureTranslator.IsBrowserLaunchFailure(""));
    }
}
