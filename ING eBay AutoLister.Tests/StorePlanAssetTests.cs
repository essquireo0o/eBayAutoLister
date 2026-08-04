using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Store Plan's screen is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing in
/// C# notices when the section loses its tab registration, the plan picker stops saving, or the page
/// stops loading because a binding was dropped. These lock the wiring that
/// <see cref="ING_eBay_AutoLister.Services.StorePlanOptimizer"/> is useless without.
///
/// They also lock three things that are decisions rather than plumbing: the hero is the ANNUAL
/// saving rather than the monthly bill, the screen states in its own text that it changes nothing on
/// eBay, and eBay is asked for the listing count once a visit rather than on every click. All three
/// are easy to "tidy" into their opposite by somebody who has not read why.
/// </summary>
public class StorePlanAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    [Fact]
    public void The_screen_is_reachable_from_the_sidebar_and_opens_as_a_workspace_tab()
    {
        Assert.Contains("data-page=\"storeplan\"", Html, StringComparison.Ordinal);
        Assert.Contains("<section id=\"storeplan-section\" class=\"opportunity-overlay hidden\">", Html, StringComparison.Ordinal);

        // Registered as a page, or the sidebar button opens nothing and the ✕ closes the wrong tab.
        Assert.Matches(@"storeplan:\s*\{\s*section:\s*'storeplan-section',\s*open:\s*showStorePlanSection", Js);

        // Hidden with every other overlay, or opening a different screen leaves this one underneath it.
        var overlays = Regex.Match(Js, @"const OVERLAY_SECTIONS = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(overlays.Success, "OVERLAY_SECTIONS is no longer a single array literal");
        Assert.Contains("'storeplan-section'", overlays.Groups[1].Value, StringComparison.Ordinal);

        // And its handlers are actually attached at start-up.
        Assert.Contains("bindStorePlan();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void It_sits_with_the_other_two_screens_about_what_the_seller_actually_keeps()
    {
        // Money Made, the Tax Pack and this are three answers to one question. Filed away under a
        // tools heading this becomes the feature nobody opens — and it is the only one of the three
        // whose answer is a single click that then pays every month.
        var sell = Section(Html,
            "<p class=\"nav-group-label\">Sell</p>",
            "<p class=\"nav-group-label\">Grow</p>");

        var order = Regex.Matches(sell, "data-page=\"([a-z]+)\"")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Contains("storeplan", order);
        Assert.Equal(order.IndexOf("tax") + 1, order.IndexOf("storeplan"));
    }

    [Fact]
    public void The_hero_is_the_annual_saving_rather_than_the_monthly_bill()
    {
        // $17 a month reads as noise. The $204 it is worth over a year is the figure that gets
        // somebody to open eBay's subscription page, which is the only outcome this screen has.
        var hero = Section(Html, "<div class=\"er-hero sp-hero\">", "<div class=\"sp-controls\">");

        Assert.Contains("id=\"sp-hero-figure\"", hero, StringComparison.Ordinal);
        Assert.Contains("Worth switching, over a year", hero, StringComparison.Ordinal);
        Assert.Contains("setText('sp-hero-figure', moneyExact(worth));", Js, StringComparison.Ordinal);
        Assert.Contains("const worth = p.totalAnnualSaving || 0;", Js, StringComparison.Ordinal);

        // The monthly figure is on the side, where it belongs — it is context, not the instruction.
        Assert.Contains("id=\"sp-hero-now\"", hero, StringComparison.Ordinal);
    }

    [Fact]
    public void The_screen_says_in_its_own_text_that_it_changes_nothing_on_ebay()
    {
        // This recommends a subscription that runs to $299 a month at the top of the card. eBay has
        // no API to change it and the app must never look as though it might have.
        Assert.Contains("Nothing here changes anything on eBay.", Html, StringComparison.Ordinal);
        Assert.Contains("Nothing here changes anything on eBay.", Js, StringComparison.Ordinal);

        // And there is no write anywhere on the screen except the seller's own three answers.
        var section = Section(Js, "// ── The Store Plan ─", "// ── The Restock List ─");
        var posts = Regex.Matches(section, @"method: 'POST'").Count;
        Assert.Equal(1, posts);
        Assert.Contains("/api/store-plan/settings", section, StringComparison.Ordinal);
    }

    [Fact]
    public void Ebay_is_asked_for_the_listing_count_once_a_visit_and_not_on_every_click()
    {
        // The count is an eBay round trip and this screen's answer moves on a scale of months. A
        // refresh on every tab return would spend the seller's rate limit to redraw the same board.
        Assert.DoesNotMatch(@"storeplan:\s*\{[^}]*refresh:", Js);
        Assert.Contains("id=\"sp-refresh\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('sp-refresh', 'click', () => loadStorePlan());", Js, StringComparison.Ordinal);

        // Changing a plan re-costs from the count already on screen rather than fetching it again.
        Assert.Contains("activeListings: storePlan?.listingCountMeasured", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_count_ebay_never_gave_is_never_sent_as_though_it_had()
    {
        // Sending a 0 when eBay failed would have the server report a figure the seller typed as one
        // it measured, and put "eBay counted this" under the whole screen.
        Assert.Contains("? (storePlan.activeListings ?? 0) : null", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_tier_row_carries_the_arithmetic_and_the_band_it_wins_over()
    {
        // The rate card lives in code and eBay changes it. A row a seller cannot check against
        // eBay's own fee page is a row they are right not to trust — and the band is the half of
        // this screen that keeps working after they have switched.
        Assert.Contains("class=\"sp-basis\"", Js, StringComparison.Ordinal);
        Assert.Contains("class=\"sp-band\"", Js, StringComparison.Ordinal);
        Assert.Contains("function storePlanBand(o)", Js, StringComparison.Ordinal);
        Assert.Contains("Never the cheapest plan at any listing count", Js, StringComparison.Ordinal);

        // And the published card itself is one click away, with nothing computed from it.
        Assert.Contains("id=\"sp-rates-card\"", Html, StringComparison.Ordinal);
        Assert.Contains("/api/store-plan/rates", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_recommended_row_is_marked_in_gold_and_the_current_one_is_not()
    {
        // Two different questions — "where am I" and "where should I be" — so two different marks.
        // One mark for both is a board on which the answer disappears the moment it is followed.
        Assert.Matches(@"\.sp-plan\.is-best \{[^}]*border-color: var\(--gold\)", Css);
        Assert.Matches(@"\.sp-plan\.is-current \{[^}]*border-color: var\(--teal-700\)", Css);
        Assert.Contains("sp-tag-current", Js, StringComparison.Ordinal);
        Assert.Contains("sp-tag-best", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_hero_only_goes_gold_when_there_is_money_on_the_table()
    {
        // A screen that always looks urgent is one the seller stops reading.
        Assert.Contains("classList.toggle('sp-hero-win', worth > 0)", Js, StringComparison.Ordinal);
        Assert.Matches(@"\.sp-hero-win \{[^}]*var\(--gold\)", Css);
    }

    [Theory]
    // Every string on this screen that arrives from the server and lands in innerHTML.
    [InlineData("o.name")]
    [InlineData("o.basis")]
    [InlineData("o.unlocks")]
    [InlineData("t.name")]
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
        Assert.True(AssetVersion("app.js") >= 111, "app.js changed, so index.html's ?v= must move past 110");
        Assert.True(AssetVersion("style.css") >= 98, "style.css changed, so index.html's ?v= must move past 97");
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
