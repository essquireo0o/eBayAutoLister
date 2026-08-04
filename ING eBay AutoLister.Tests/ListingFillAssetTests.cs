using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The one-click fill only exists if the button is on the page and wired to the code that fills.
/// Nothing in C# renders this screen, so nothing in C# notices when the button is renamed, moved
/// back inside the collapsed fix list, or quietly disconnected — the readiness result would still
/// carry every suggestion and the seller would still be typing them in by hand.
///
/// These lock the wiring, and the two decisions in it that are easy to "tidy" into their opposite:
/// the button lives outside the disclosure, and applying an offer never writes over a field that
/// already has something in it.
/// </summary>
public class ListingFillAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");

    [Fact]
    public void The_fill_button_is_on_the_page_and_bound_to_the_code_that_fills()
    {
        Assert.Contains("id=\"nl-rd-fill\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-rd-fill-row\"", Html, StringComparison.Ordinal);
        Assert.Contains("on('nl-rd-fill', 'click', nlFillEverything)", Js, StringComparison.Ordinal);
        Assert.Contains("function nlFillEverything(", Js, StringComparison.Ordinal);
        Assert.Contains("function nlApplySuggestion(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fill_button_sits_outside_the_collapsed_fix_list()
    {
        // An action buried behind a disclosure triangle is an action nobody takes. The row has to
        // come before the list, and must not be inside it.
        var row  = Html.IndexOf("id=\"nl-rd-fill-row\"", StringComparison.Ordinal);
        var list = Html.IndexOf("id=\"nl-rd-list\"", StringComparison.Ordinal);
        Assert.True(row > 0 && list > 0, "the readiness bar lost its fill row or its fix list");
        Assert.True(row < list, "the fill button has been moved inside the collapsed fix list");
    }

    [Fact]
    public void The_fill_only_applies_what_the_server_marked_confident()
    {
        // Low-confidence offers — a package guessed for an item the estimator did not recognise —
        // are meant to be read and clicked one at a time, not swept in by a button labelled with
        // a count.
        Assert.Contains("s.confidence === 'high' || s.confidence === 'medium'", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Applying_an_offer_refuses_a_field_that_already_has_something_in_it()
    {
        // The server only attaches an offer to a field its own check found empty, but a result
        // rendered a moment before the seller typed is stale by the time the button is pressed.
        // The second check is what stops that click undoing the typing.
        Assert.Contains("if (!isCorrection && targets.some(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void An_estimated_value_says_so_where_the_seller_accepts_it()
    {
        // This number prices a shipping label. It has to be marked at the point of acceptance,
        // not in a tooltip somewhere — and marked once, not twice: the estimator's own basis
        // already opens by saying it estimated.
        Assert.Contains("s.isEstimate && !/^estimat/i.test(s.source) ? 'estimated — ' + s.source : s.source",
                        Js, StringComparison.Ordinal);
        Assert.Contains("rd-fix-est", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_package_types_the_app_offers_are_the_options_the_dropdown_actually_has()
    {
        // A suggestion that names an option the dropdown doesn't have sends the seller looking
        // for something that isn't on the list — and writes a value the form can't display.
        foreach (var type in new[]
                 {
                     "LETTER", "LARGE_ENVELOPE_OR_FLAT_PACK", "PACKAGE_THICK_ENVELOPE",
                     "MAILING_BOX", "BULKY_GOODS", "VERY_LARGE_PACKAGE",
                 })
        {
            Assert.Contains($"<option value=\"{type}\"", Html, StringComparison.Ordinal);
            Assert.True(ListingAutofill.Rank(type) >= 0, $"{type} is on the dropdown but unknown to the ranking");
        }
    }

    // ── The category ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_filled_category_writes_the_id_that_publishes_not_just_the_name()
    {
        // The visible box is a search field; the hidden one carries the ID eBay lists against.
        // Both ids have to exist for the offer's two-box write to land.
        Assert.Contains("id=\"nl-category\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"nl-category-id\"", Html, StringComparison.Ordinal);
        Assert.Contains("const isCategory = s.field === 'category';", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_category_counts_as_answered_by_its_id_and_not_by_the_search_box_beside_it()
    {
        // Half-typed search text is not a chosen category. Testing the visible box the way every
        // other field is tested would refuse to fill a category that was never picked — which is
        // exactly the seller this is for.
        Assert.Contains("if (String($('nl-category-id')?.value || '').trim()) return false;",
                        Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Filling_a_category_finishes_the_way_picking_one_from_the_list_does()
    {
        // The name moves onto the chip and the search box goes back to being a search box.
        // Without this the category reads as typed-but-unpicked, which is the state the seller
        // has just been saved from.
        Assert.Contains("if (isCategory) nlSyncCategoryDisplay();", Js, StringComparison.Ordinal);
        Assert.Contains("function nlSyncCategoryDisplay(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_chosen_categorys_name_reaches_the_server_so_it_can_be_remembered()
    {
        // The picker empties the search input and moves the name to the chip, so reading the
        // input alone sent a blank name on every finished listing — and a blank name is what
        // turns tomorrow's suggestion into "Category 179171".
        Assert.Contains("category: $('nl-category')?.value || nlSelectedCategoryName()",
                        Js, StringComparison.Ordinal);
        Assert.Contains("function nlSelectedCategoryName(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_fill_row_is_hidden_again_when_the_check_could_not_answer()
    {
        // Leaving the button up after a failed check would promise values nobody has.
        Assert.Contains("const fillRow = $('nl-rd-fill-row'); if (fillRow) fillRow.hidden = true;",
                        Js, StringComparison.Ordinal);
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
