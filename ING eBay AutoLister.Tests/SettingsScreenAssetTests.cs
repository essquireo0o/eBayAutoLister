using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Settings screen is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing in
/// C# notices when a rewrite of the markup drops a field id and a seller's fees silently stop
/// saving. Every id <c>app.js</c> reads on this page is pinned here.
///
/// The rest lock decisions rather than plumbing, because each of them is easy to "tidy" back into
/// the thing it replaced by someone who has not read why:
/// the rail is buttons and not anchors (the hash is the app's router); fees and listing defaults
/// are two cards and not one (one card meant two Save buttons in it); the read-only diagnostics sit
/// at the bottom and not above the two required steps; and units are attached to their fields as
/// real labels rather than printed in a parenthesis at the end of the label text.
/// </summary>
public class SettingsScreenAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    private static string SettingsHtml => Section(Html,
        "<section id=\"settings-section\"",
        "<section id=\"logs-section\"");

    /// <summary>
    /// Every element id the page's JavaScript reads or writes. A redesign that loses one of these
    /// does not throw — <c>$(id)</c> returns null, the optional chaining swallows it, and the
    /// seller's number is quietly never saved.
    /// </summary>
    public static readonly string[] BoundIds =
    [
        // the connection doctor
        "cd-summary", "cd-recheck", "cd-rows",
        // required setup
        "pg-required-card", "pg-required-key", "pg-required-key-state",
        "pg-required-policies", "pg-required-policies-state", "pg-open-required", "pg-required-msg",
        // image generation
        "pg-imggen-card", "pg-imggen-state", "pg-imggen-mode", "pg-imggen-endpoint-wrap",
        "pg-imggen-endpoint", "pg-imggen-model-wrap", "pg-imggen-model", "pg-imggen-load-models",
        "pg-image-prompt", "pg-imggen-save", "pg-imggen-test", "pg-imggen-msg", "pg-imggen-guide",
        // saved-session connections
        "pg-terapeak-state", "pg-terapeak-connect", "pg-terapeak-disconnect", "pg-terapeak-status",
        "pg-facebook-state", "pg-facebook-connect", "pg-facebook-disconnect", "pg-facebook-status",
        // fees & costs — every one of these is a term in a profit calculation
        "pg-fee-fvf", "pg-fee-fixed", "pg-fee-promoted", "pg-fee-payment", "pg-fee-shipping",
        "pg-fee-packaging", "pg-fee-labor", "pg-fee-returns", "pg-fee-testing",
        "pg-fee-min-profit", "pg-fee-min-margin", "pg-fees-summary", "pg-fees-save", "pg-fees-msg",
        // listing defaults
        "pg-default-zip", "pg-default-country", "pg-default-package-type", "pg-default-handling",
        "pg-default-weight-lbs", "pg-default-weight-oz", "pg-default-length", "pg-default-width",
        "pg-default-height", "pg-default-fulfillment", "pg-default-fulfillment-name",
        "pg-default-best-offer", "pg-defaults-save", "pg-defaults-msg",
        // local diagnostics
        "settings-status",
    ];

    [Fact]
    public void Every_control_the_page_binds_is_still_on_the_page()
    {
        var settings = SettingsHtml;

        foreach (var id in BoundIds)
            Assert.True(settings.Contains($"id=\"{id}\"", StringComparison.Ordinal),
                $"#{id} is bound by app.js but no longer exists inside #settings-section");
    }

    [Fact]
    public void No_id_is_declared_twice_on_the_page()
    {
        // Two elements with the same id is the failure mode a copy-paste redesign produces, and
        // the one the browser hides: getElementById answers with whichever came first.
        var ids = Regex.Matches(Html, "id=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
        var duplicated = ids.GroupBy(id => id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        Assert.True(duplicated.Count == 0, "duplicate element ids: " + string.Join(", ", duplicated));
    }

    [Fact]
    public void The_rail_is_buttons_because_the_hash_is_the_router()
    {
        // Comments stripped first: the markup explains this rule to the next reader by quoting the
        // anchor it must not be, and the rule should not fire on its own explanation.
        var settings = StripComments(SettingsHtml);

        Assert.Contains("<nav id=\"settings-nav\" class=\"settings-nav\"", settings, StringComparison.Ordinal);

        // An <a href="#pg-fees-card"> would set location.hash, and handleNav would take the whole
        // workspace somewhere else on the way to a card six inches down the page.
        Assert.DoesNotContain("<a class=\"settings-nav-link\"", settings, StringComparison.Ordinal);
        foreach (Match m in Regex.Matches(settings, "<a [^>]*href=\"#([^\"]*)\""))
            Assert.Fail($"the settings rail must not use an in-page anchor (found href=\"#{m.Groups[1].Value}\")");

        Assert.Contains("initSettingsNav();", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("location.hash", Section(Js, "function initSettingsNav()", "// The header chip"), StringComparison.Ordinal);
    }

    [Fact]
    public void Every_rail_entry_points_at_a_card_that_exists_in_the_same_order()
    {
        var settings = SettingsHtml;

        var railTargets = Regex.Matches(settings, "data-settings-target=\"([^\"]+)\"")
            .Select(m => m.Groups[1].Value).ToList();

        Assert.Equal(
            ["pg-connections-card", "pg-required-card", "pg-optional-group", "pg-fees-card", "pg-defaults-card", "pg-system-card"],
            railTargets);

        // A rail whose order disagrees with the page's order tells the reader the page is
        // organised one way while scrolling it proves the opposite.
        var positions = railTargets.Select(id => settings.IndexOf($"id=\"{id}\"", StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(-1, positions);
        Assert.Equal(positions.OrderBy(p => p).ToList(), positions);

        // Landing under the sticky rail puts the heading you asked for off screen.
        Assert.Contains("scroll-margin-top", Section(Css, "/* ── The Settings screen", "@media (max-width: 900px)"), StringComparison.Ordinal);
    }

    [Fact]
    public void The_sticky_rail_is_offset_by_the_workspace_tab_bar_rather_than_pinned_flat()
    {
        var block = Section(Css, "/* ── The Settings screen", "/* ── Connections: the connection doctor");

        // .ws-tab-bar is position:fixed at z-index 950 and is 0px tall until a SECOND workspace tab
        // is opened. A flat `top` therefore looks perfect for as long as the seller has one tab and
        // eats the rail's first entry the moment they have two — which is always, by the time they
        // have reached Settings from somewhere. Every offset on this screen goes through the token.
        foreach (var rule in new[] { "top: calc(var(--ws-tabbar-h)", "top: var(--ws-tabbar-h)" })
            Assert.Contains(rule, block, StringComparison.Ordinal);

        Assert.DoesNotContain("scroll-margin-top: var(--s5)", block, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(block, @"scroll-margin-top: calc\(var\(--ws-tabbar-h\)").Count);

        // The stacked rail sets width:100%. A flex item that keeps it is one viewport wide, six
        // times over, in a row that then scrolls — showing exactly one name at a time.
        var horizontal = Section(block, "@media (max-width: 1280px)", "@media (max-width: 900px)");
        Assert.Contains("width: auto;", horizontal, StringComparison.Ordinal);
    }

    [Fact]
    public void Fees_and_listing_defaults_are_two_cards_with_one_save_button_each()
    {
        var fees = Section(SettingsHtml, "id=\"pg-fees-card\"", "id=\"pg-defaults-card\"");
        var defaults = Section(SettingsHtml, "id=\"pg-defaults-card\"", "id=\"pg-system-card\"");

        // Sharing one card put two Save buttons inside it. Pressing the first one saved neither
        // half of what had just been typed, and nothing on screen said so.
        Assert.Contains("id=\"pg-fees-save\"", fees, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"pg-defaults-save\"", fees, StringComparison.Ordinal);

        Assert.Contains("id=\"pg-defaults-save\"", defaults, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"pg-fees-save\"", defaults, StringComparison.Ordinal);

        // Each fee field belongs to a named group, or eleven number boxes are a wall again.
        Assert.True(Regex.Matches(fees, "class=\"settings-subhead\"").Count >= 4,
            "the eleven fee fields are grouped under named subheads, not listed flat");
    }

    [Fact]
    public void The_diagnostics_sit_below_the_two_steps_the_app_cannot_run_without()
    {
        var settings = SettingsHtml;

        var required = settings.IndexOf("id=\"pg-required-card\"", StringComparison.Ordinal);
        var status = settings.IndexOf("id=\"settings-status\"", StringComparison.Ordinal);

        Assert.True(required >= 0 && status >= 0);
        Assert.True(required < status,
            "read-only local diagnostics must not sit above the required setup steps");

        // And the required card's own promise has to stay true: nothing below it is a step.
        Assert.Contains("Nothing else on this page has to be done before you can list.",
            settings, StringComparison.Ordinal);
    }

    [Fact]
    public void A_unit_is_a_second_label_on_its_field_rather_than_a_symbol_only_sighted_users_get()
    {
        var settings = SettingsHtml;

        // 13.25 is dollars or percent depending on a bracket four words away, and the wrong
        // reading writes a wrong number into every profit figure the app shows. The mark is a
        // <label for> so it joins the accessible name and so clicking it focuses the field.
        foreach (var (id, mark) in new[]
                 {
                     ("pg-fee-fvf", "%"), ("pg-fee-promoted", "%"), ("pg-fee-payment", "%"),
                     ("pg-fee-returns", "%"), ("pg-fee-testing", "%"), ("pg-fee-min-margin", "%"),
                     ("pg-fee-fixed", "$"), ("pg-fee-shipping", "$"), ("pg-fee-packaging", "$"),
                     ("pg-fee-labor", "$"), ("pg-fee-min-profit", "$"),
                     ("pg-default-weight-lbs", "lb"), ("pg-default-weight-oz", "oz"),
                     ("pg-default-length", "in"), ("pg-default-width", "in"), ("pg-default-height", "in"),
                 })
            Assert.True(
                settings.Contains($"<label for=\"{id}\" class=\"unit-mark\">{mark}</label>", StringComparison.Ordinal),
                $"#{id} has lost its \"{mark}\" unit label");

        // The suffix sits exactly where a number input draws its spinner arrows.
        Assert.Contains(".unit-field > input[type=\"number\"]::-webkit-inner-spin-button", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_is_styled_by_stylesheet_and_not_by_attribute()
    {
        // Settings was the only screen in the app that styled itself with style="" — around twenty
        // of them, which is how its type sizes and greys drifted away from every other page.
        var inline = Regex.Matches(StripComments(SettingsHtml), "style=\"([^\"]*)\"")
            .Select(m => m.Groups[1].Value)
            .ToList();

        // The two survivors are not decoration: applyPgImggenVisibility writes element.style.display
        // directly, so these carry the initial state that JavaScript then toggles.
        Assert.Equal(["display:none", "display:none"], inline);
        Assert.Contains("ep.style.display", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_cards_and_the_rail_are_built_from_theme_roles_so_the_dark_theme_follows()
    {
        var block = Section(Css, "/* ── The Settings screen", "/* ── Connections: the connection doctor");

        // A literal colour here is a hole in the dark page — the same rule the theme pass set.
        foreach (Match m in Regex.Matches(block, @"#[0-9a-fA-F]{3,8}\b"))
            Assert.Fail("the Settings screen must use theme roles, not literal colours: " + m.Value);
        Assert.DoesNotContain("rgba(255", block, StringComparison.Ordinal);

        Assert.Contains("background: var(--card)", block, StringComparison.Ordinal);
    }

    private static string StripComments(string html) =>
        Regex.Replace(html, "<!--.*?-->", string.Empty, RegexOptions.Singleline);

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
