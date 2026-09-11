namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The sourcing tools — the eBay deal scanner, the local/Facebook search, the auction sniper, the
/// want-to-sell check and the earnings filter — are not miner-branded.
/// </summary>
/// <remarks>
/// 2026-09-11, the owner, after an eBay presentation: "on the scraper - scrap anything to do with
/// miners cryptocurrency miners bitcoin miners." The product is a general listing engine, and the
/// first thing a new seller saw in every search box was an Antminer. The examples are ordinary
/// retail now. The pricing and accessory logic that happens to be TESTED with miner titles is
/// untouched — that is behaviour, not branding — so this pins only what the seller reads.
/// </remarks>
public class SourcingToolsAreNotMinerBrandedTests
{
    private static readonly string Html = ReadWebAsset("index.html");

    [Theory]
    [InlineData("ebay-scan-query")]
    [InlineData("fb-query-input")]
    [InlineData("wts-query")]
    [InlineData("sn-terms")]
    [InlineData("er-f-title")]
    public void The_search_boxes_do_not_suggest_miners(string id)
    {
        var tag = TagWithId(id);
        Assert.DoesNotMatch("(?i)antminer|whatsminer|bitmain|bitcoin|crypto|\\bminer", tag);
        Assert.Contains("placeholder=\"", tag, StringComparison.Ordinal);
    }

    [Fact]
    public void The_example_search_chips_do_not_suggest_miners()
    {
        var start = Html.IndexOf("<div class=\"opp-quick-searches\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "the example-search chips are gone from the eBay scanner");
        var end = Html.IndexOf("</div>", start, StringComparison.Ordinal);
        var chips = Html[start..end];

        Assert.Contains("opp-query-chip", chips, StringComparison.Ordinal);
        Assert.DoesNotMatch("(?i)antminer|whatsminer|bitmain|bitcoin|crypto|\\bminer", chips);
    }

    private static string TagWithId(string id)
    {
        var at = Html.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
        Assert.True(at >= 0, $"#{id} is gone from index.html");
        var open = Html.LastIndexOf('<', at);
        var close = Html.IndexOf('>', at);
        return Html[open..(close + 1)];
    }

    private static string ReadWebAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister", "wwwroot")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name));
    }
}
