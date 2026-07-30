using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Tax Pack's screen is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing in
/// C# notices when the section loses its tab registration, the export links stop carrying a year,
/// or the whole page quietly stops loading because a binding was dropped. These lock the wiring
/// that <see cref="ING_eBay_AutoLister.Services.TaxPackCalculator"/> is useless without.
///
/// They also lock two things that are decisions rather than plumbing: the hero is the SET-ASIDE
/// rather than the bill, and the block that makes the bill smaller is rendered above everything it
/// explains. Both are easy to "tidy" into the opposite by someone who has not read why.
/// </summary>
public class TaxPackAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void The_screen_is_reachable_from_the_sidebar_and_opens_as_a_workspace_tab()
    {
        Assert.Contains("data-page=\"tax\"", Html, StringComparison.Ordinal);
        Assert.Contains("<section id=\"tax-section\" class=\"opportunity-overlay hidden\">", Html, StringComparison.Ordinal);

        // Registered as a page, or the sidebar button opens nothing and the ✕ closes the wrong tab.
        Assert.Matches(@"tax:\s*\{\s*section:\s*'tax-section',\s*open:\s*showTaxSection", Js);

        // Hidden with every other overlay, or opening a different screen leaves this one underneath it.
        var overlays = Regex.Match(Js, @"const OVERLAY_SECTIONS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(overlays.Success, "OVERLAY_SECTIONS is no longer a single array literal");
        Assert.Contains("'tax-section'", overlays.Groups[1].Value, StringComparison.Ordinal);

        // And its handlers are actually attached at start-up.
        Assert.Contains("bindTax();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Sitting_it_next_to_money_made_is_the_point_so_it_stays_in_the_sell_group()
    {
        // The two screens are two halves of one sentence — what you made, and how much of it was
        // never yours. Filed away under a tools heading it becomes the feature nobody opens.
        var sell = Section(Html,
            "<p class=\"nav-group-label\">Sell</p>",
            "<p class=\"nav-group-label\">Grow</p>");

        var order = Regex.Matches(sell, "data-page=\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Contains("tax", order);
        Assert.Equal(order.IndexOf("earnings") + 1, order.IndexOf("tax"));
    }

    [Fact]
    public void The_hero_is_the_set_aside_rather_than_the_bill()
    {
        // A bill is something to dread and put off; a set-aside is an instruction you can follow
        // today. The seller who moves the money monthly never has the bad February, so the number
        // in the largest type on the page is the one they can act on.
        var hero = Section(Html, "<div class=\"er-hero tx-hero\">", "id=\"tx-gap\"");

        Assert.Contains("Set aside for federal tax", hero, StringComparison.Ordinal);
        Assert.Contains("id=\"tx-hero-figure\"", hero, StringComparison.Ordinal);
        Assert.Contains("setText('tx-hero-figure', moneyExact(p.totalTax || 0));", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_block_that_makes_the_bill_smaller_is_rendered_above_everything_it_explains()
    {
        // Sales with no recorded cost are the one thing on this page the seller can fix today, and
        // fixing them makes the total go DOWN. Demoted below the form it becomes a footnote.
        var gap = Html.IndexOf("id=\"tx-gap\"", StringComparison.Ordinal);
        var quarters = Html.IndexOf("id=\"tx-quarters-card\"", StringComparison.Ordinal);
        var schedule = Html.IndexOf("id=\"tx-schedule-card\"", StringComparison.Ordinal);

        Assert.True(gap > 0, "the cost-basis gap block is gone from the page");
        Assert.True(quarters > gap && schedule > gap,
            "the cost-basis gap must be rendered before the quarters and the Schedule C");

        // Gold, not red: an opportunity dressed as an error is one the seller learns to dismiss.
        Assert.Matches(@"\.tx-gap \{[^}]*border-left: 4px solid var\(--gold\)", Css);

        // And it leads somewhere. A figure the seller can do nothing about is a figure that only
        // makes them feel worse.
        Assert.Contains("tx-gap-action", Js, StringComparison.Ordinal);
        Assert.Contains("navigateTo('earnings')", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_exports_hand_over_the_year_that_is_actually_on_screen()
    {
        // The year picker and the download links are the same control as far as the seller is
        // concerned. A link left pointing at the server's default silently exports the wrong year.
        Assert.Contains("summary.href = `/api/tax/export?kind=summary&year=${p.year}`", Js, StringComparison.Ordinal);
        Assert.Contains("ledger.href = `/api/tax/export?kind=ledger&year=${p.year}`", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"tx-year\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_payment_date_is_never_rendered_through_a_timezone()
    {
        // The server stamps a due date in the seller's offset at request time, so a January payment
        // computed in July is an hour out in standard time and a plain Date() renders the 15th as
        // the 14th. Telling somebody their estimated payment is due a day early is the kind of
        // wrong that only shows up in January.
        Assert.Contains("function taxDueLabel(value)", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("shortDate(q.dueDate)", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("shortDate(next.dueDate)", Js, StringComparison.Ordinal);
        Assert.Contains("taxDueLabel(q.dueDate)", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_re_reads_itself_on_every_return_to_the_tab()
    {
        // Everything on it is server state, and a sale imported since the last look changes the
        // bill. A stale tax figure is the one thing this screen must never show.
        Assert.Matches(@"tax:\s*\{[^}]*refresh:\s*loadTaxPack", Js);
    }

    [Theory]
    // Every string on this screen that arrives from the server and lands in innerHTML. Item titles
    // come from eBay, so an unescaped one is a listing name away from being script.
    [InlineData("q.name")]
    [InlineData("q.covers")]
    [InlineData("line.line")]
    [InlineData("line.label")]
    [InlineData("line.basis")]
    public void Server_supplied_text_never_reaches_innerHTML_unescaped(string field)
    {
        Assert.DoesNotContain("${" + field + "}", Js, StringComparison.Ordinal);
        Assert.Contains("${esc(" + field + ")}", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cached_assets_are_versioned_past_the_build_that_shipped_without_this_screen()
    {
        // wwwroot files are embedded resources served with far-future caching. A seller who updates
        // and gets yesterday's app.js gets a sidebar button that opens nothing at all.
        Assert.True(AssetVersion("app.js") >= 96, "app.js changed, so index.html's ?v= must move past 95");
        Assert.True(AssetVersion("style.css") >= 86, "style.css changed, so index.html's ?v= must move past 85");
    }

    private static int AssetVersion(string file)
    {
        var match = Regex.Match(Html, Regex.Escape(file) + @"\?v=(\d+)");
        Assert.True(match.Success, $"index.html no longer versions {file}");
        return int.Parse(match.Groups[1].Value);
    }

    /// <summary>The text between two markers, so a test reads one region rather than the whole file.</summary>
    private static string Section(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end < 0 ? source[start..] : source[start..end];
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
