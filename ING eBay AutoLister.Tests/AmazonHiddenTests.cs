namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Amazon is out of sight, not out of the code. On 2026-09-10 the owner asked for the Amazon door
/// in the sidebar and the "Send to" switch in the listing editor to go: Amazon is not connected
/// (the sandbox serves canned data and production waits on credentials), so both were only things
/// to read past. They are hidden with the attribute, everything behind them stays wired, and these
/// make sure neither quietly comes back — or quietly gets deleted — before Amazon actually works.
/// </summary>
public class AmazonHiddenTests
{
    private static readonly string Html = ReadAsset("index.html");

    [Fact]
    public void The_amazon_sidebar_door_is_hidden_but_still_there()
    {
        var door = Slice(Html, "data-page=\"amazon\"", "</button>");
        Assert.Contains(" hidden", door);
        Assert.Contains("AI Listing Amazon", door);
    }

    [Fact]
    public void The_send_to_switch_is_hidden_so_ebay_is_the_only_destination()
    {
        var open = Slice(Html, "<div class=\"nl-market\"", ">");
        Assert.Contains(" hidden", open);
        // Both tabs are still inside it, ready for the day the switch is shown again.
        Assert.Contains("id=\"nl-market-ebay\"", Html);
        Assert.Contains("id=\"nl-market-amazon\"", Html);
    }

    private static string Slice(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{from}' is gone from index.html");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"'{to}' never closes '{from}' in index.html");
        return text[start..end];
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing asset: " + path);
        return File.ReadAllText(path);
    }
}
