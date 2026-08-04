namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The browser half of the keyword panel. Nothing in C# renders this screen, so nothing in C#
/// notices when the button is renamed, the endpoint is repointed, or a guard is "tidied" into its
/// opposite — and every failure on this path is silent. A miner that answers perfectly into a panel
/// nobody can open is a feature that does not exist, and a chip that writes an 81-character title
/// gets the seller's own last word truncated by eBay without telling them.
///
/// These lock the wiring and the three refusals in it that look like fussiness and are not:
/// the 80-character ceiling, never writing over an answer the seller gave, and never writing
/// anything at all without a click.
/// </summary>
public class KeywordGapAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js   = ReadAsset("app.js");
    private static readonly string Css  = ReadAsset("style.css");

    [Fact]
    public void The_panel_is_on_the_page_and_bound_to_the_code_that_fills_it()
    {
        foreach (var id in new[]
                 {
                     "nl-kw-run", "nl-kw-panel", "nl-kw-headline", "nl-kw-source", "nl-kw-message",
                     "nl-kw-missing", "nl-kw-missing-wrap", "nl-kw-suggest-wrap", "nl-kw-suggest-title",
                     "nl-kw-apply", "nl-kw-specifics", "nl-kw-fill-specifics", "nl-kw-shared",
                     "nl-kw-free", "nl-kw-close",
                 })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains("on('nl-kw-run', 'click', nlRunKeywordGap)", Js, StringComparison.Ordinal);
        Assert.Contains("on('nl-kw-apply', 'click', nlApplySuggestedTitle)", Js, StringComparison.Ordinal);
        Assert.Contains("function nlRunKeywordGap(", Js, StringComparison.Ordinal);
        Assert.Contains("initKeywordGap();", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void It_asks_the_endpoint_that_reads_the_market()
    {
        Assert.Contains("'/api/listing/search-terms'", Js, StringComparison.Ordinal);

        // The category and the seller's existing specifics are what make the Item Specifics half
        // possible at all: without the category there is no list of legal values to check against,
        // and without the current answers it would offer values for fields already filled in.
        Assert.Contains("categoryId:", Js, StringComparison.Ordinal);
        Assert.Contains("itemSpecifics: nlCollectAspectValues()", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_panel_sits_with_the_title_it_is_about()
    {
        // Advice about a title, filed under Shipping, is advice nobody reads. The run button has
        // to come after the title input and before the category picker below it.
        var title = Html.IndexOf("id=\"nl-title\"", StringComparison.Ordinal);
        var run   = Html.IndexOf("id=\"nl-kw-run\"", StringComparison.Ordinal);
        var cat   = Html.IndexOf("id=\"nl-category\"", StringComparison.Ordinal);
        Assert.True(title > 0 && run > 0 && cat > 0, "the title field, the keyword button or the category picker is gone");
        Assert.True(title < run && run < cat, "the keyword panel has drifted away from the title it is about");
    }

    [Fact]
    public void A_word_that_would_not_fit_is_refused_rather_than_truncated()
    {
        // eBay cuts a title at 80 characters. A chip that pushes it to 81 does not add a keyword;
        // it silently deletes whatever the seller had at the end to make room for one.
        Assert.Contains("if (next.length > 80)", Js, StringComparison.Ordinal);
        Assert.Contains("function nlAddTerm(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_specific_is_never_written_over_an_answer_the_seller_gave()
    {
        // The server only offers a value for a field it saw empty. That answer is stale the moment
        // the seller types, and this is the check that stops the click undoing the typing.
        Assert.Contains("function nlWriteSpecific(", Js, StringComparison.Ordinal);
        Assert.Contains("if (String(el.value || '').trim()) return false;", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_term_carries_the_counts_that_are_the_reason_to_believe_it()
    {
        // "31 of 50 top results" is a reason. "Recommended keyword" is a brand of advice, and the
        // seller cannot tell a good one from a bad one without the number.
        Assert.Contains("top results", Js, StringComparison.Ordinal);
        Assert.Contains("sold`", Js, StringComparison.Ordinal);
        Assert.Contains("kw-chip-why", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seller_is_told_these_are_the_neighbours_words_not_facts_about_their_item()
    {
        // This is the whole safety story of the feature. The miner does not know what the item is;
        // it knows what the items around it are called. "Bluetooth" ticked onto a title for a thing
        // with no Bluetooth is a not-as-described case, paid for by the seller.
        Assert.Contains("kw-caveat", Html, StringComparison.Ordinal);
        Assert.Contains("not facts about your item", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_eBay_outage_reads_as_not_now_rather_than_your_title_is_fine()
    {
        // Two eBay calls sit behind this. A failure that renders as an empty missing list tells the
        // seller their title already carries every word the market uses, which is the one wrong
        // answer this panel can give.
        Assert.Contains("Couldn’t read eBay just now", Js, StringComparison.Ordinal);
        Assert.Contains("nothing is claimed about your title either way", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void A_suggestion_for_a_field_that_is_not_on_screen_is_not_offered()
    {
        // The category can change after the search was run, and then the aspect the value belongs
        // to no longer exists on the form. Offering it would be a button that does nothing.
        Assert.Contains("const usable = list.filter(s => !!nlSpecificField(s.name));", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void An_aspect_name_with_a_quote_in_it_cannot_break_the_panel()
    {
        // The names are eBay's, not ours — "Manufacturer's Part Number" is a real one. An unescaped
        // name in an attribute selector throws, and the throw takes the whole render with it.
        Assert.Contains("function cssEscape(", Js, StringComparison.Ordinal);
        Assert.Contains("replace(/[\"\\\\]/g, '\\\\$&')", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void Opening_a_fresh_listing_does_not_inherit_the_last_ones_answer()
    {
        // The panel is about one specific title. Left on screen across a reset it would be reporting
        // the previous item's market against a blank form.
        Assert.Contains("nlResetKeywordGap();", Js, StringComparison.Ordinal);
        Assert.Contains("function nlResetKeywordGap(", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_styles_the_panel_renders_with_exist()
    {
        foreach (var cls in new[]
                 {
                     ".kw-panel", ".kw-chip", ".kw-chip.is-add", ".kw-chip.is-have",
                     ".kw-spec", ".kw-suggest-title", ".kw-caveat", ".kw-free",
                 })
            Assert.Contains(cls, Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_is_told_to_fetch_the_changed_assets()
    {
        // wwwroot files are embedded resources served with a long cache. A seller running the old
        // app.js against the new endpoint gets a button that is not there.
        Assert.Contains("app.js?v=104", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=92", Html, StringComparison.Ordinal);
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
