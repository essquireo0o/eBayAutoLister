namespace ING_eBay_AutoLister.Tests;

/// <summary>The two failures visible on the Opportunity/WhatsNot screens: hidden inbound freight
/// and a live comp lookup that stopped after the exact title missed.</summary>
public class OpportunityShippingAndSimilarCompsAssetTests
{
    private static readonly string Js = Read("wwwroot", "app.js");
    private static readonly string Html = Read("wwwroot", "index.html");

    [Fact]
    public void The_live_pricer_walks_the_bounded_similar_comp_queries_before_estimating()
    {
        Assert.Contains("body.search?.similarQueries", Js);
        Assert.Contains("for (let i = 0; i < queries.length; i++)", Js);
        Assert.Contains("await runLiveLookup(queries[i], 'wn')", Js);
        Assert.Contains("No exact sold history", Js);
        Assert.Contains(".slice(0, 5)", Js);
    }

    [Fact]
    public void The_board_shows_the_inbound_shipping_that_is_inside_delivered_cost()
    {
        Assert.Contains("row.purchaseShippingCost != null", Js);
        Assert.Contains("inbound shipping", Js);
        Assert.Contains("money(row.localAsk)", Js);
    }

    [Fact]
    public void The_changed_script_has_a_new_cache_stamp()
    {
        Assert.Contains("app.js?v=158", Html);
    }

    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine([dir!.FullName, "ING eBay AutoLister", .. parts]));
    }
}
