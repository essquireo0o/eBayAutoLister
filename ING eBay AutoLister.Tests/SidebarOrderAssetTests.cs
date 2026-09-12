using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The sidebar is ordered by how often a seller opens each screen, not by when each feature was
/// built. The owner, circling the Sell group (2026-08-21): "Organize these and put the most
/// important stuff towards the top — think about the user, what they want." The user's day is one
/// item's journey — photograph it, let the AI write and price it, publish it, see what it sold for —
/// and that loop runs many times a day, while the Tax Pack and the Store plan are read once a
/// month. Nothing here checks that the screens work; WorkspaceTabsAssetTests does that. This checks
/// that the order keeps telling the truth about what a seller does most.
/// </summary>
public class SidebarOrderAssetTests
{
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void The_sell_group_runs_from_the_camera_to_the_bank_in_the_order_a_day_does()
    {
        var order = Pages(Group("Sell", "<p class=\"nav-group-label\">Grow</p>"));

        // Home first, then the daily loop in the order it happens: photograph -> AI writes the
        // listing -> the photos it drew on -> what is live -> improving what is live.
        AssertBefore(order, "dashboard", "photobox");
        AssertBefore(order, "photobox", "ebay");
        AssertBefore(order, "ebay", "photos");
        AssertBefore(order, "photos", "listings");
        AssertBefore(order, "listings", "copilot");

        // Then the money already made, in the order the month-end questions come: what came in,
        // what of it is tax, and whether the Store plan is earning its subscription.
        AssertBefore(order, "copilot", "earnings");
        AssertBefore(order, "earnings", "tax");
        AssertBefore(order, "tax", "storeplan");
    }

    [Fact]
    public void Nothing_from_the_daily_loop_sits_below_the_monthly_screens()
    {
        // The monthly screens are the floor of the group. A new daily screen added at the end of
        // the list — the natural place to paste a new button — would land under the Tax Pack, and
        // this is the test that says so.
        var order = Pages(Group("Sell", "<p class=\"nav-group-label\">Grow</p>"));
        var firstMonthly = order.IndexOf("earnings");
        Assert.True(firstMonthly >= 0, "Money Made is no longer in the Sell group");

        Assert.Equal(new[] { "earnings", "tax", "storeplan" }, order.Skip(firstMonthly).ToArray());
    }

    [Fact]
    public void The_account_group_puts_the_door_that_gets_used_above_the_one_read_when_something_broke()
    {
        // Settings is where eBay gets reconnected and a key gets changed, so it is opened whenever
        // something needs fixing; Logs is where the cause is read afterwards. License stays last:
        // its status dot is what a seller is looking for at the bottom of the rail.
        var order = Pages(Group("Account", "</nav>"));

        AssertBefore(order, "settings", "logs");
        Assert.Equal("license", order.Last());
    }

    private static void AssertBefore(List<string> order, string first, string second)
    {
        Assert.Contains(first, order);
        Assert.Contains(second, order);
        Assert.True(order.IndexOf(first) < order.IndexOf(second),
            $"expected the sidebar entry '{first}' above '{second}', but the order is: {string.Join(" > ", order)}");
    }

    private static List<string> Pages(string section) =>
        Regex.Matches(section, "data-page=\"([a-z]+)\"").Select(m => m.Groups[1].Value).ToList();

    /// <summary>The nav markup from a group's label up to the given end marker.</summary>
    private static string Group(string label, string endMarker)
    {
        var from = $"<p class=\"nav-group-label\">{label}</p>";
        var start = Html.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, "sidebar group label not found: " + label);
        var end = Html.IndexOf(endMarker, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, "sidebar group has no end marker: " + label);
        return Html.Substring(start, end - start);
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
