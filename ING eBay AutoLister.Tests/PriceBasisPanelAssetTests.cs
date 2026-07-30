namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The price/confidence trail is rendered in JavaScript, so nothing in C# notices when the panel
/// stops being drawn, or when one board keeps it and the other quietly loses it. Both call sites
/// are pinned here, along with the two rules the panel exists to keep.
///
/// <b>One renderer, two boards.</b> The deals board and the Opportunity Finder show figures from
/// the same pipeline. Two explanations of one price is how a seller ends up trusting whichever
/// screen they happened to open.
///
/// <b>The panel reads the server's arithmetic; it does not compute its own.</b> The blend sentence
/// is built and tested in C# (see PriceBasisTests). If the browser recomputed it, the two could
/// disagree — and the one on screen would be the one nothing tests.
/// </summary>
public class PriceBasisPanelAssetTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void The_panel_is_rendered_by_exactly_one_function()
    {
        Assert.Contains("function priceBasisHtml(basis)", Js);
        Assert.Equal(1, Occurrences(Js, "function priceBasisHtml"));
    }

    [Fact]
    public void Both_boards_that_show_a_resale_price_show_where_it_came_from()
    {
        // The deals board row detail, and the Opportunity Finder's scoring details.
        Assert.Contains("priceBasisHtml(row.priceBasis)", Js);
        Assert.Contains("priceBasisHtml(it.priceBasis)", Js);
    }

    /// <summary>
    /// The blend sentence comes off the server object. A second implementation in the browser is
    /// free to drift from the one under test, and the drift would only ever show on screen.
    /// </summary>
    [Fact]
    public void The_blend_sentence_is_the_servers_and_is_escaped()
    {
        Assert.Contains("esc(basis.arithmetic)", Js);
    }

    /// <summary>
    /// Every field the panel reads. The server names them in camelCase through the JSON serializer;
    /// a rename on the C# side is a silent blank in the browser, never an error.
    /// </summary>
    [Theory]
    [InlineData("basis.price")]
    [InlineData("basis.arithmetic")]
    [InlineData("basis.sources")]
    [InlineData("basis.medianPrice")]
    [InlineData("basis.recommendedListingPrice")]
    [InlineData("basis.confidenceScore")]
    [InlineData("basis.confidenceLevel")]
    [InlineData("basis.confidenceFactors")]
    [InlineData("basis.biggestGap")]
    [InlineData("basis.disagreementMessage")]
    [InlineData("s.valueLabel")]
    [InlineData("s.weightPercent")]
    [InlineData("s.compCount")]
    [InlineData("s.foundCount")]
    [InlineData("f.possible")]
    [InlineData("f.earned")]
    [InlineData("f.detail")]
    [InlineData("f.lever")]
    public void The_panel_reads_the_fields_the_server_sends(string field)
    {
        Assert.Contains(field, Js);
    }

    /// <summary>
    /// A row with no price has a reason instead, and the reason is not this panel's to give. The
    /// guard is the first line of the renderer.
    /// </summary>
    [Fact]
    public void An_unpriced_row_renders_nothing_at_all()
    {
        Assert.Contains("if (!basis || !(basis.price > 0)) return '';", Js);
    }

    /// <summary>
    /// The factor bars are the app's shared meter, drawn against each term's own ceiling. A bar
    /// drawn to 100 when the term is worth 5 would make every small term look catastrophic.
    /// </summary>
    [Fact]
    public void The_factor_bars_use_the_shared_meter_scaled_to_each_terms_own_ceiling()
    {
        Assert.Contains("vizMeter(f.earned, {", Js);
        Assert.Contains("max: f.possible", Js);
    }

    [Fact]
    public void The_panel_is_styled_and_the_browser_is_told_to_fetch_the_new_files()
    {
        Assert.Contains(".pb-basis {", Css);
        Assert.Contains(".pb-factor {", Css);
        Assert.Contains(".pb-src {", Css);

        // wwwroot is embedded, so a stale cached bundle is a real failure mode: the markup ships
        // and the browser keeps the version that can't render it.
        Assert.Matches(@"app\.js\?v=(\d+)", Html);
        Assert.True(Version(Html, @"app\.js\?v=(\d+)") >= 101, "app.js ?v= was not bumped for this panel");
        Assert.True(Version(Html, @"style\.css\?v=(\d+)") >= 89, "style.css ?v= was not bumped for this panel");
    }

    private static int Version(string html, string pattern) =>
        int.Parse(System.Text.RegularExpressions.Regex.Match(html, pattern).Groups[1].Value);

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
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
