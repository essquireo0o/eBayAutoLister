using System.Diagnostics;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Terapeak login watcher is JavaScript embedded in C#, so nothing else in the build reads a
/// word of it — a typo ships, and the seller sees "connect keeps timing out".
///
/// These lock the three properties that make a successful login actually stick. Each one was a real
/// way a completed sign-in got thrown away and reported as a timeout:
///   1. the idle clock resets on TYPING, not only on a URL change — eBay's sign-in is one URL
///      through password, two-factor and "check your phone";
///   2. every live tab is watched, not just the first — sign-in can finish in a popup;
///   3. the session is saved the moment the browser is signed in, not only on reaching
///      /sh/research at the end of the wait.
/// </summary>
public class TerapeakLoginScriptTests
{
    private static readonly string Js =
        TerapeakService.BuildLoginScript("C:\\\\pw\\\\playwright", "C:\\\\app\\\\terapeak-session.json",
                                         "C:\\\\app\\\\terapeak-profile");

    [Fact]
    public void TypingKeepsTheLoginAlive()
    {
        // The beacon every page installs...
        Assert.Contains("__ingActivity", Js);
        Assert.Contains("'keydown'", Js);
        // ...and the deadline extension that reads it. Without this second half the beacon is
        // decoration and the six-minute clock still runs out under a seller typing a 2FA code.
        Assert.Matches(new Regex(@"deadline\s*=\s*Math\.max\(deadline,\s*seen\s*\+\s*IDLE_MS\)"), Js);
    }

    [Fact]
    public void EveryOpenTabIsWatchedNotJustTheFirst()
    {
        Assert.Contains("ctx.pages()", Js);
        Assert.Contains("pages.some(p => String(p.url() || '').includes('/sh/research'))", Js);
    }

    [Fact]
    public void SignedInStateIsProbedOverHttpRatherThanGuessedFromTheVisibleTab()
    {
        // ctx.request carries the context's own cookies, so this asks eBay the real question
        // without opening a tab that could steal focus mid-CAPTCHA.
        Assert.Contains("ctx.request.get(RESEARCH_URL", Js);
        Assert.Contains("async function signedIn()", Js);
    }

    [Fact]
    public void TheSessionIsSavedAsSoonAsTheSellerIsSignedIn()
    {
        // Saving only at the end is how "sign in, close the window" lost the login.
        Assert.Contains("if (await signedIn()) { await saveSession(); break; }", Js);
        // And one last look after the wait ends, in case it landed on the final tick.
        Assert.Contains("if (!saved && browser.isConnected() && await signedIn()) await saveSession();", Js);
    }

    [Fact]
    public void RunningOutOfTimeIsReportedAsATimeoutNotAsAClosedWindow()
    {
        // Telling a seller who sat there for six minutes that they "closed the window" is how a
        // timeout gets reported as something they did wrong.
        Assert.Contains("'CANCELLED'", Js);
        Assert.Contains("'TIMEOUT'", Js);
        Assert.Contains("closed = true", Js);
    }

    [Fact]
    public void TheWaitIsBoundedByTheDocumentedLimits()
    {
        Assert.Equal(TerapeakService.IdleMinutes, MinutesOf("IDLE_MS"));
        Assert.Equal(TerapeakService.CaptchaMinutes, MinutesOf("CAPTCHA_MS"));
        Assert.Equal(TerapeakService.HardCapMinutes, MinutesOf("HARD_CAP_MS"));

        // The process timeout must sit ABOVE the script's own ceiling, or node gets killed
        // mid-wait and the outcome is a dead process instead of a named reason.
        Assert.True(TerapeakService.ProcessTimeout.TotalMinutes > TerapeakService.HardCapMinutes);
    }

    [Fact]
    public void TheProbeRunsOftenEnoughToCatchASignInBeforeTheWindowIsClosed()
    {
        // A seller who signs in and immediately closes the window has only this long to be caught.
        Assert.InRange(TerapeakService.ProbeSeconds, 1, 15);
    }

    [Fact]
    public void TheScriptIsValidJavaScript()
    {
        // The whole point: this file is a string until node parses it.
        var node = NodeRuntime.NodeExe;
        var file = Path.Combine(Path.GetTempPath(), $"terapeak_login_syntax_{Guid.NewGuid():N}.cjs");
        File.WriteAllText(file, Js);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(node, $"--check \"{file}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return; // no node on this machine — nothing to check against
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            Assert.True(proc.ExitCode == 0, $"login script is not valid JavaScript:\n{stderr}");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // node isn't installed here; the app surfaces that separately when a login is started.
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static int MinutesOf(string constName)
    {
        var match = Regex.Match(Js, constName + @"\s*=\s*(\d+)\s*\*\s*60\s*\*\s*1000");
        Assert.True(match.Success, $"{constName} is no longer a named minutes constant");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void TheLoginWindowOpensOnAPageThatActuallyHasASignInForm()
    {
        // Measured in real Chrome 2026-07-30: the legacy ws/eBayISAPI.dll?SignIn entry point
        // redirects to /splashui/captcha with no username or password field on the page, with or
        // without ru=. signin.ebay.com/signin returns the real form. A login window pointed at the
        // dead one shows a bot check and an eBay error page, and the seller is told their password
        // failed.
        Assert.Contains("signin.ebay.com/signin?ru=", Js);
        Assert.DoesNotContain("eBayISAPI.dll", Js);

        // ru= is not decoration: it is what lands the seller on /sh/research after sign-in, which
        // is the URL the wait loop watches for.
        Assert.Contains("sh%2Fresearch", Js);
    }
}
