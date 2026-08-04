using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The listing editor is HTML, CSS and JavaScript, and nothing in C# renders it. These pin the
/// four decisions that make the screen work, each of which is easy to undo by someone tidying:
///
/// 1. Every panel carries a chip, and app.js knows its id. A renamed id does not throw — the chip
///    is simply never written, and a collapsed panel goes back to saying nothing.
/// 2. The action bar is pinned and the draft preview sits above it. Moving the preview back below
///    puts it underneath the buttons that produced it.
/// 3. The chip thresholds are <see cref="ListingReadinessAnalyzer"/>'s, read from its constants
///    here, so the front page of the editor and the pre-publish check cannot start disagreeing
///    about what counts as a thin title or too few photos.
/// 4. The overview never calls descCommitText(). That function writes the plain-text tab into the
///    HTML field; calling it from a refresh would edit the listing because a chip was redrawn, and
///    the drawer would report unsaved changes nobody made.
/// </summary>
public class ListingEditorOverviewAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js   = ReadAsset("app.js");
    private static readonly string Css  = ReadAsset("style.css");

    /// <summary>The listing editor form only — not the New Listing overlay, which shares its classes.</summary>
    private static string EditorHtml => Section(Html, "<section id=\"form-section\"", "<div id=\"result\"");

    /// <summary>The overview module in app.js.</summary>
    private static string OverviewJs =>
        Section(Js, "// ── Editing a listing: what is in the panels you closed", "function loadListingIntoForm(");

    /// <summary>Every chip slot, and the panel each one describes.</summary>
    public static readonly string[] ChipIds =
    [
        "fp-title", "fp-condition", "fp-ids", "fp-specifics",
        "fp-photos", "fp-description", "fp-pricing", "fp-shipping", "fp-options",
    ];

    [Fact]
    public void Every_chip_app_js_writes_still_exists_on_the_editor()
    {
        var editor = EditorHtml;

        foreach (var id in ChipIds)
        {
            Assert.Contains($"id=\"{id}\"", editor, StringComparison.Ordinal);
            Assert.Contains($"'{id}'", OverviewJs, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void No_chip_id_is_declared_twice_anywhere_on_the_page()
    {
        foreach (var id in ChipIds)
            Assert.Equal(1, Occurrences(Html, $"id=\"{id}\""));
    }

    [Fact]
    public void Every_chip_sits_in_a_summary_that_opts_into_the_chip_layout()
    {
        // .fp-sum is what moves the caret's auto margin onto the chip. A chip in a plain summary
        // would be pushed to the middle of the header by the caret's own margin.
        foreach (var id in ChipIds)
        {
            var at = EditorHtml.IndexOf($"id=\"{id}\"", StringComparison.Ordinal);
            var summaryStart = EditorHtml.LastIndexOf("<summary", at, StringComparison.Ordinal);
            Assert.True(summaryStart >= 0, $"{id} is not inside a <summary>");
            Assert.Contains("fp-sum", EditorHtml[summaryStart..at], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_caret_override_is_scoped_so_the_new_listing_overlay_keeps_its_own()
    {
        // The New Listing overlay shares .form-panel and has plain summaries. Dropping the
        // .fp-sum qualifier here would pull the caret off the right edge on nine of its panels.
        Assert.Contains(".form-panel > summary.fp-sum::after", Css, StringComparison.Ordinal);

        var caret = Section(Css, ".form-panel > summary::after {", "}");
        Assert.Contains("margin-left: auto", caret, StringComparison.Ordinal);
    }

    [Fact]
    public void The_action_bar_is_pinned_and_still_holds_all_four_buttons()
    {
        var editor = EditorHtml;

        Assert.Contains("class=\"form-actions form-actions-sticky\"", editor, StringComparison.Ordinal);
        Assert.Contains("id=\"fa-state\"", editor, StringComparison.Ordinal);

        foreach (var id in new[] { "btn-post", "btn-create-ebay-draft", "btn-update", "btn-new-listing" })
            Assert.Contains($"id=\"{id}\"", Section(editor, "class=\"fa-buttons\"", "</div>"), StringComparison.Ordinal);

        var sticky = Section(Css, ".form-actions-sticky {", "}");
        Assert.Contains("position: sticky", sticky, StringComparison.Ordinal);
        Assert.Contains("bottom:", sticky, StringComparison.Ordinal);
    }

    [Fact]
    public void The_draft_preview_opens_above_the_pinned_bar_and_not_under_it()
    {
        var preview = EditorHtml.IndexOf("id=\"draft-preview-panel\"", StringComparison.Ordinal);
        var bar     = EditorHtml.IndexOf("id=\"form-actions-bar\"", StringComparison.Ordinal);

        Assert.True(preview >= 0 && bar >= 0);
        Assert.True(preview < bar,
            "the draft preview renders below the sticky action bar, so it opens underneath the buttons that produced it");
    }

    [Fact]
    public void The_unsaved_badge_is_driven_by_the_drawer_and_has_no_second_source_of_truth()
    {
        Assert.Contains("class=\"fa-unsaved\"", EditorHtml, StringComparison.Ordinal);
        Assert.Contains(".edit-drawer.dirty .fa-unsaved", Css, StringComparison.Ordinal);
        Assert.DoesNotContain("fa-unsaved", OverviewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_thin_thresholds_are_the_analyzers_own_numbers()
    {
        Assert.Contains($"EDITOR_TITLE_THIN  = {ListingReadinessAnalyzer.ThinTitleLength};",
            OverviewJs, StringComparison.Ordinal);
        Assert.Contains($"EDITOR_PHOTOS_THIN = {ListingReadinessAnalyzer.GoodPhotoCount};",
            OverviewJs, StringComparison.Ordinal);
        Assert.Contains($"`${{title.length}} / {ListingReadinessAnalyzer.MaxTitleLength}`",
            OverviewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_bar_names_exactly_the_four_things_the_analyzer_blocks_on()
    {
        // Same four, same order: title-missing, category-missing, price-missing, photos-missing.
        var blockers = Section(OverviewJs, "const EDITOR_BLOCKERS = [", "];");

        foreach (var label in new[] { "'Title'", "'Category'", "'Price'", "'Photos'" })
            Assert.Contains(label, blockers, StringComparison.Ordinal);

        Assert.Equal(4, Occurrences(blockers, "label:"));

        // And it must not overstate what it checked. The server check also reads eBay's required
        // Item Specifics for the category, which this one never asks for.
        Assert.DoesNotContain("Ready to publish", OverviewJs, StringComparison.Ordinal);
        Assert.Contains("The four things eBay requires are filled.", OverviewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Refreshing_a_chip_never_writes_to_the_listing()
    {
        // descCommitText() merges the plain-text tab into the HTML field. buildPayload() calls it
        // because it is about to publish; a chip refresh must not, or reading the form would edit
        // it and the drawer would report unsaved changes the seller never made.
        // The call form, not the name — the module's own comment explains why it doesn't call it.
        Assert.DoesNotContain("descCommitText('f')", OverviewJs, StringComparison.Ordinal);
        Assert.DoesNotContain("buildPayload()", OverviewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_a_blocker_marks_its_panel()
    {
        // A mark that also fires on the app's opinion is a mark sellers learn to scroll past.
        Assert.Contains("'is-incomplete', state === 'is-empty'", OverviewJs, StringComparison.Ordinal);
        Assert.Contains(".form-panel.is-incomplete", Css, StringComparison.Ordinal);
        Assert.Contains(".fp-chip.is-thin", Css, StringComparison.Ordinal);
        Assert.Contains(".fp-chip.is-empty", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_counts_are_scoped_to_the_editors_own_rows()
    {
        // The New Listing overlay has photo and specifics rows of its own; an unscoped selector
        // counts both forms at once and the chip reports a number from the wrong listing.
        Assert.Contains("'#photo-url-list .photo-url-row input'", OverviewJs, StringComparison.Ordinal);
        Assert.Contains("'#specifics-list .specific-row'", OverviewJs, StringComparison.Ordinal);
    }

    [Fact]
    public void The_overview_starts_after_the_form_has_been_moved_into_the_drawer()
    {
        var init    = Js.IndexOf("initEditDrawer();", StringComparison.Ordinal);
        var startup = Js.IndexOf("initEditorOverview();", StringComparison.Ordinal);

        Assert.True(init >= 0 && startup > init,
            "initEditorOverview() must run after initEditDrawer() relocates #form-section, or it binds to a node that is about to move");
    }

    [Fact]
    public void The_pinned_bars_shadow_is_defined_in_both_themes()
    {
        // An upward shadow token: the light-theme value would be invisible on a dark page.
        Assert.Contains("--e3-up:", Section(Css, ":root {", "\nbody {"), StringComparison.Ordinal);
        Assert.Contains("--e3-up:",
            Section(Css, ":root[data-theme=\"dark\"] {", ":root[data-theme=\"dark\"] .modal-overlay"),
            StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

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
