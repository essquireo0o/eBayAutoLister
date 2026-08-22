namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// No board in this app asks the seller to press a button to see what the board is for.
/// </summary>
/// <remarks>
/// <para>
/// The app is signed into eBay, holds the token, and imported the listings when the account
/// connected — and then drew an empty page with a button in the middle of it. That button is a
/// question with exactly one possible answer, asked again every time the screen is opened.
/// </para>
/// <para>
/// So every board that reads the seller's own account goes and gets its own data on open, and the
/// result is kept for the session — coming back to a tab is not asking for another scan, and the
/// button in the header stays as the way to ask for a fresh one. Two rules keep that from being
/// expensive or dishonest: nothing scans an account that cannot answer, and nothing scans at
/// sign-in for a board the seller has not opened.
/// </para>
/// <para>
/// These pin the wiring. A board added later with a scan button and no autoScan call is the
/// regression, and it looks exactly like the thing this was written to remove.
/// </para>
/// </remarks>
public class AccountBoardsFillThemselvesTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");

    /// <summary>Every board that reads the account, and the results box each one fills.</summary>
    public static TheoryData<string, string> Boards => new()
    {
        { "inventory", "inv-results" },
        { "offers", "wo-results" },
        { "rescue", "rsc-results" },
        { "relist", "rl-results" },
        { "shipping", "ship-results" },
        { "promoted", "ad-results" },
        // Added 2026-08-22 — the last two that still opened as a button on an empty page.
        { "trends", "tr-results" },
        { "snipe", "sn-results" },
    };

    [Theory]
    [MemberData(nameof(Boards))]
    public void Every_account_board_fills_itself_on_open(string key, string resultsId)
    {
        Assert.Contains($"autoScan('{key}', '{resultsId}'", Js);
    }

    [Fact]
    public void A_board_is_never_scanned_twice_for_one_session()
    {
        // The scan is remembered, not the tab. Switching away and back is not a new question, and
        // re-running these on every tab switch would be an eBay round trip per glance.
        Assert.Contains("if (autoScanned.has(key)) return;", Js);
        Assert.Contains("autoScanned.add(key);", Js);
    }

    [Fact]
    public void Nothing_is_scanned_against_an_account_that_cannot_answer()
    {
        Assert.Contains("if (!isConnected || ebayLinkIsBroken()) {", Js);
        // And the board says which of the two it is, because they are different problems with
        // different fixes — one is Settings, the other is a sign-in that needs attention.
        Assert.Contains("Connect your eBay account in Settings and this board fills itself in when you open it.", Js);
        Assert.Contains("Your eBay sign-in needs attention", Js);
    }

    [Fact]
    public void Connecting_fills_the_board_already_on_screen()
    {
        // The gap this closes: open a board, read "connect your eBay account", go and connect it,
        // come back — and find the same empty board with the same instruction, because the scan
        // only ever fired on open and the board was already open.
        Assert.Contains("const autoScanPending = new Map();", Js);
        Assert.Contains("autoScanPending.set(key, { resultsId, run });", Js);
        Assert.Contains("function autoScanOnConnect()", Js);
        Assert.Contains("if (justConnected) autoScanOnConnect();", Js);
    }

    [Fact]
    public void Connecting_does_not_scan_boards_nobody_has_opened()
    {
        // The whole reason these run on open rather than at sign-in. Every one of them reads live
        // listings and prices them against sold comps, and two spend a lookup budget on top:
        // running the lot the instant a token arrives would spend that on boards the seller may
        // never look at, in front of the one thing they came to do.
        Assert.Contains("if (!section || $(section)?.classList.contains('hidden')) continue;", Js);
    }

    [Fact]
    public void A_pending_board_is_cleared_once_it_has_actually_scanned()
    {
        // Otherwise a later reconnect re-scans a board that has had its data all along.
        Assert.Contains("autoScanPending.delete(key);", Js);
    }

    [Fact]
    public void The_screens_that_cannot_guess_still_ask()
    {
        // Not everything should self-fill, and saying which is half the rule. These three need
        // something only the seller has — a number, an item, a manifest — and a board that
        // invented one would be answering a question nobody asked.
        Assert.Contains("id=\"bud-plan-btn\"", Html);    // Spend My Budget: how much have you got
        Assert.Contains("id=\"wts-run-btn\"", Html);     // Where to Sell: which item
        Assert.Contains("id=\"lot-sample-btn\"", Html);  // Lot Analyzer: which manifest

        Assert.DoesNotContain("autoScan('budget'", Js);
        Assert.DoesNotContain("autoScan('wts'", Js);
        Assert.DoesNotContain("autoScan('lots'", Js);
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
