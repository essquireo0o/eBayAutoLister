using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The login-window raise snippet is JavaScript embedded in C#, so nothing else type-checks it.
///
/// These lock the properties the fix depends on. The hard raise is a minimize->normal cycle, which
/// is the only in-browser call that actually moves the OS window — and it is VISIBLE. Doing it
/// repeatedly made the login window strobe for five seconds while eBay's CAPTCHA page loaded, so
/// it now happens exactly ONCE, while the window is still blank. Holding the window in front after
/// that is LoginWindowFocus's job, done natively and invisibly.
///
/// The "exactly once" is the whole point of these tests: it is a one-character edit to turn the
/// burst back into a strobe, and nothing else in the build would notice.
/// </summary>
public class NodeRuntimeRaiseScriptTests
{
    private static readonly string Js = NodeRuntime.RaiseToFrontJs;

    [Fact]
    public void DefinesBothRaiseHelpers()
    {
        Assert.Contains("async function raise(hard)", Js);
        Assert.Contains("async function raiseBurst()", Js);
    }

    [Fact]
    public void HardRaiseUsesTheMinimizeNormalCycle()
    {
        // bringToFront() alone only focuses the tab inside Chrome — it does not move the OS
        // window, which is the whole bug.
        Assert.Contains("windowState: 'minimized'", Js);
        Assert.Contains("windowState: 'normal'", Js);
    }

    [Fact]
    public void BurstIsBoundedToAFewSeconds()
    {
        Assert.InRange(BurstWindowMs(), 3000, 8000);
    }

    [Fact]
    public void TheWindowIsLiftedExactlyOnce()
    {
        // More than one visible minimize->normal is the strobe this fixed: the CAPTCHA window
        // flashing over and over before it finally settled.
        var hardRaises = Regex.Matches(Code, @"raise\(\s*true\s*\)").Count;
        Assert.True(hardRaises == 1,
            $"the hard raise is visible, so it must happen exactly once — found {hardRaises}");
    }

    [Fact]
    public void TheRepeatingLoopNeverRaisesHard()
    {
        // The single lift has to sit BEFORE the loop. Inside it, every tick would flash.
        var loopStart = Code.IndexOf("while", StringComparison.Ordinal);
        Assert.True(loopStart >= 0, "the burst is no longer a bounded loop");
        Assert.DoesNotContain("raise(true)", Code[loopStart..].Replace(" ", ""));
    }

    [Fact]
    public void TheSingleHardRaiseHappensBeforeTheLoop()
    {
        var hard = Code.IndexOf("raise(true)", StringComparison.Ordinal);
        var loop = Code.IndexOf("while", StringComparison.Ordinal);
        Assert.True(hard >= 0 && loop >= 0, "expected one hard raise and a bounded loop");
        Assert.True(hard < loop,
            "the hard raise must run while the window is still blank, not once the page is loading");
    }

    /// <summary>
    /// The snippet with its <c>//</c> comments removed. These tests are about what the script
    /// DOES — and the comments explaining the one-raise rule naturally quote <c>raise(true)</c>,
    /// which would otherwise be counted as a second call.
    /// </summary>
    private static string Code =>
        Regex.Replace(Js, @"//[^\n]*", string.Empty);

    private static int BurstWindowMs()
    {
        var match = Regex.Match(Js, @"RAISE_BURST_MS\s*=\s*(\d+)");
        Assert.True(match.Success, "burst duration is no longer a named constant");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
