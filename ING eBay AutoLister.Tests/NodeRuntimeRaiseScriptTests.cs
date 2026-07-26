using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The login-window raise snippet is JavaScript embedded in C#, so nothing else type-checks it.
/// These lock the two properties the fix depends on: it raises hard several times at startup
/// (one attempt loses the focus race and the window ends up buried behind the app), and it
/// stops doing that quickly (a window that keeps forcing itself forward eats the password the
/// user is typing into it).
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
    public void EveryHardRaiseLandsInsideTheBurstWindow()
    {
        var offsets = HardRaiseOffsetsMs();

        Assert.True(offsets.Count >= 3, $"expected several startup raises, found {offsets.Count}");
        Assert.All(offsets, offset => Assert.InRange(offset, 0, BurstWindowMs()));
    }

    private static int BurstWindowMs()
    {
        var match = Regex.Match(Js, @"RAISE_BURST_MS\s*=\s*(\d+)");
        Assert.True(match.Success, "burst duration is no longer a named constant");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static List<int> HardRaiseOffsetsMs()
    {
        var match = Regex.Match(Js, @"hardAt\s*=\s*\[([^\]]*)\]");
        Assert.True(match.Success, "hard-raise schedule is no longer a literal array");
        return match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, CultureInfo.InvariantCulture))
            .ToList();
    }
}
