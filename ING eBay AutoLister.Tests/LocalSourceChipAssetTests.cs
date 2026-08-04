using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The per-source chips above the local-sourcing tables are HTML and JavaScript, and nothing in C#
/// renders them — so nothing in C# notices when a site's money stops being shown, when the plain
/// local search starts claiming profit figures it never computed, or when a Craigslist seller's
/// name lands in innerHTML unescaped.
/// </summary>
/// <remarks>
/// Three of these lock decisions rather than plumbing, and each is the sort of thing a later tidy-up
/// undoes without knowing why it was that way: the money half of a chip belongs only to the board
/// that priced anything, the "best site" line is withheld when only one site was searched, and a
/// capped site is named rather than counted.
/// </remarks>
public class LocalSourceChipAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    // The goldmine board prices things, so its chips can say what each site was worth. The plain
    // local search prices nothing — passing the same flag there would render "none worth buying"
    // beside every site on a screen that never asked the question.
    [Fact]
    public void Only_the_priced_board_claims_what_a_site_was_worth()
    {
        Assert.Contains("renderSourceOutcomes(data.sources, { withProfit: true });", Js, StringComparison.Ordinal);

        // The plain search still calls it the old way, with no options object at all.
        Assert.Contains("renderSourceOutcomes(data.sources);", Js, StringComparison.Ordinal);

        // And the flag has to be asked for: a truthy-by-default would put the profit half on both.
        Assert.Contains("const withProfit = options.withProfit === true;", Js, StringComparison.Ordinal);
    }

    // "48 results · 12 priced" is what stops a seller unticking the site that was paying. Without
    // the second number the gap between the search and the board looks like the site's fault.
    [Fact]
    public void A_chip_reports_what_was_priced_as_well_as_what_was_found()
    {
        var fn = Function(Js, "function sourceYieldHtml(s) {");

        Assert.Contains("s.analyzed", fn, StringComparison.Ordinal);
        Assert.Contains("priced", fn, StringComparison.Ordinal);
        Assert.Contains("s.potentialProfit", fn, StringComparison.Ordinal);

        // But only where it differs from the count already on the chip. "6 results · 6 priced" is
        // the same fact twice, and a figure that shows up whether or not it means anything is a
        // figure nobody reads on the one site where it matters.
        Assert.Contains("s.analyzed !== s.count", fn, StringComparison.Ordinal);
    }

    // A site that earned nothing must still say so. Going quiet on zero reads as the site never
    // having been in the ranking, which is a different and more forgiving thing than being in it
    // and losing.
    [Fact]
    public void A_site_that_earned_nothing_says_so_rather_than_going_quiet()
    {
        var fn = Function(Js, "function sourceYieldHtml(s) {");
        Assert.Contains("none worth buying", fn, StringComparison.Ordinal);
    }

    // Every site-supplied string on a chip goes through esc(). A craigslist post title and a metro
    // label are both attacker-controlled as far as this app is concerned.
    [Fact]
    public void Everything_a_site_supplies_is_escaped()
    {
        var fn = Function(Js, "function renderSourceOutcomes(sources, options = {}) {");

        Assert.Contains("esc(s.label)", fn, StringComparison.Ordinal);
        Assert.Contains("esc(s.scopeLabel)", fn, StringComparison.Ordinal);
        Assert.Contains("esc(s.error)", fn, StringComparison.Ordinal);
        Assert.Contains("esc(s.searchUrl)", fn, StringComparison.Ordinal);

        // The board's own site names too — bestSourceLabel and the capped list come from the same
        // per-source records the chips do.
        Assert.Contains("esc(data.bestSourceLabel)", Js, StringComparison.Ordinal);
        Assert.Contains("capped.map(s => esc(s.label))", Js, StringComparison.Ordinal);
    }

    // "Most of it on Craigslist" when Craigslist was the only site searched is a sentence with no
    // information in it, and it implies a comparison that never happened.
    [Fact]
    public void The_best_site_line_is_withheld_when_only_one_site_answered()
    {
        Assert.Matches(
            @"data\.bestSourceLabel && \(data\.sources \|\| \[\]\)\.filter\(s => s\.status === 'ok' && s\.count\)\.length > 1",
            Js);
    }

    // Named, not counted: the fix for a capped site is to search that site on its own, and that is
    // an instruction about a particular site rather than a number.
    [Fact]
    public void A_capped_site_is_named_and_told_what_to_do_about_it()
    {
        Assert.Contains("s.status === 'ok' && s.capped", Js, StringComparison.Ordinal);
        Assert.Contains("search that site on its own to reach the rest", Js, StringComparison.Ordinal);
    }

    // The money on a chip carries the board's own headline colour, so the two read as one claim.
    // A bare <strong> would inherit .local-source-chip strong's ink and look like a label.
    [Fact]
    public void The_money_on_a_chip_is_coloured_like_the_boards_other_money()
    {
        Assert.Contains(".local-source-chip strong.local-chip-money", Css, StringComparison.Ordinal);
        Assert.Contains("local-chip-money", Js, StringComparison.Ordinal);
    }

    // A cached app.js is an app that renders the old chips against the new API shape — the profit
    // half simply never appears, and there is nothing on screen to say why.
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        Assert.Contains("app.js?v=108", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=96", Html, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    // From a function's opening line to the closing brace at its own indentation. Enough to keep an
    // assertion about one function from passing on a string that lives in another.
    private static string Function(string js, string signature)
    {
        var start = js.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find `{signature}` in app.js");

        // Taken from the file rather than from the argument, so a caller can pass the signature
        // without having to reproduce how deeply nested it happens to be.
        var lineStart = js.LastIndexOf('\n', start) + 1;
        var indent = js[lineStart..start];
        var end = js.IndexOf($"\n{indent}}}", start + signature.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of `{signature}`");
        return js[start..end];
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
