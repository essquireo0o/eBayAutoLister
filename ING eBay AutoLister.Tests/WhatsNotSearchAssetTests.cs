namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What the sold search actually asked eBay for reaches the live card through five links, and three
/// of them are outside C#: the endpoint cleans the name before it prices it, widens once when
/// nothing matched, holds what it asked with the comps, the browser renders it, and the browser can
/// overrule it. Break any one and the screen goes back to searching eBay for "🔥3x Antminer S9 NO
/// RESERVE" — which returns nothing, on a card that then says the item has no market.
/// </summary>
/// <remarks>
/// Four of these are decisions rather than plumbing, and each is the sort of thing a later tidy-up
/// undoes without reading why: the cleaning happens once and on the server, the widening happens at
/// most once and only when the first search found nothing worth pricing on, the browser computes no
/// query of its own, and the sold-comps path this whole screen stands on is untouched.
/// </remarks>
public class WhatsNotSearchAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Builder = ReadSource("Services/LiveSearchQuery.cs");
    private static readonly string Board = ReadSource("Services/LiveBidBoard.cs");

    // ── The endpoint asks eBay the cleaned question ──────────────────────────────────────────

    [Fact]
    public void The_lookup_runs_on_the_cleaned_query_and_not_on_the_typed_name()
    {
        var bid = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "app.MapPost(\"/api/whatsnot/rebid\"");

        Assert.Contains("LiveSearchQuery.Build(title)", bid, StringComparison.Ordinal);
        Assert.Contains("terms.Query, supplierUnitCost: null", bid, StringComparison.Ordinal);
        // The DISPLAY name, the count and the seller's own record still come off what was typed.
        Assert.Contains("advisor.Build(title, analysis, req", bid, StringComparison.Ordinal);
        Assert.Contains("ReadOwnTrackRecord(title,", bid, StringComparison.Ordinal);
    }

    [Fact]
    public void The_widening_only_happens_when_there_was_nothing_to_price_on_and_stops_the_moment_there_is()
    {
        var bid = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "app.MapPost(\"/api/whatsnot/rebid\"");

        // A second lookup on every lot would double the wait on the screen whose whole promise is
        // an answer in seconds — so the ladder is still only ever walked on the thin path.
        Assert.Contains("LiveBidAdvisor.CompCountOf(analysis) < LiveBidAdvisor.MinCompsToBid", bid,
            StringComparison.Ordinal);

        // It walks OUTWARD one identifying word at a time (2026-08-21) rather than jumping straight
        // to three words: "1884 CC Morgan Silver Dollar GSA Holder Uncirculated Carson City" priced
        // off nothing because the one jump stepped over "1884 CC Morgan Silver Dollar", the title
        // that coin actually sells under. See LiveSearchQuery.Ladder and CompsLadderTests.
        Assert.Contains("foreach (var rung in LiveSearchQuery.Ladder(terms))", bid, StringComparison.Ordinal);

        // And it stops at the first rung that can carry a ceiling: going broader from there trades
        // the closest sold history for a vaguer one.
        Assert.Contains("if (LiveBidAdvisor.CompCountOf(analysis) >= LiveBidAdvisor.MinCompsToBid) break;", bid,
            StringComparison.Ordinal);

        // Each rung is kept only if it actually found more, so a widening that changed nothing is
        // never reported as having happened.
        Assert.Contains("LiveBidAdvisor.CompCountOf(widened) > LiveBidAdvisor.CompCountOf(analysis)", bid,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_comp_count_that_decides_to_widen_is_the_one_the_card_prints()
    {
        // Two counts — one at the decision, one on the card — is how a card ends up saying "4 comps"
        // under a badge chosen because there were two.
        Assert.Contains("public static int CompCountOf(MarketAnalysisResult analysis)", Advisor, StringComparison.Ordinal);
        Assert.Contains("card.CompCount = CompCountOf(analysis);", Advisor, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_asked_is_held_with_the_comps_it_answered()
    {
        // A re-price that forgot the search would print a widened card as though the whole name had
        // matched — at a moment when the bid is climbing and nobody is re-reading the strip.
        Assert.Contains("public LiveSearchTerms? Search { get; init; }", Board, StringComparison.Ordinal);
        Assert.Contains("Search = search,", Board, StringComparison.Ordinal);
        Assert.Contains("board.Hold(title, analysis, category, nowUtc: null, own: own, search: terms)", Program,
            StringComparison.Ordinal);
        Assert.Contains("quote.Category, now, quote.Own, quote.Search", Program, StringComparison.Ordinal);
        Assert.Contains("quote.Category, null, quote.Own, quote.Search", Program, StringComparison.Ordinal);
    }

    // ── The builder decides, and it decides once ─────────────────────────────────────────────

    [Fact]
    public void The_query_is_built_in_one_place_and_the_browser_is_not_it()
    {
        // A second cleaner in JavaScript would be a second opinion about which comps belong under
        // this item's name, and only one of the two is tested. So the browser posts the typed name
        // untouched and paints back whatever the server says it asked for.
        var post = Section(Js, "safePost('/api/whatsnot/bid', {", "});");
        Assert.Contains("title: item,", post, StringComparison.Ordinal);

        var strip = Section(Js, "const searchStrip =", "</div>` : '';");
        Assert.Contains("esc(s.query)", strip, StringComparison.Ordinal);
        Assert.Contains("esc(d.text)", strip, StringComparison.Ordinal);
        Assert.Contains("esc(d.why)", strip, StringComparison.Ordinal);
        // No cutting, splitting or rewriting of any of it on the way to the screen.
        foreach (var editing in new[] { ".replace(", ".split(", ".slice(", "toLowerCase" })
            Assert.DoesNotContain(editing, strip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_count_the_ceiling_prices_is_the_count_the_search_stops_asking_for()
    {
        // One reader. If the search dropped "3x" on a rule of its own, a lot could be priced for
        // three and searched as one, or the reverse — and nothing on screen would show the split.
        Assert.Contains("LiveLotSize.Read(typed, null)", Builder, StringComparison.Ordinal);
        Assert.Contains("units.Source == LiveLotSize.SourceTitle", Builder, StringComparison.Ordinal);
    }

    [Fact]
    public void The_words_that_decide_the_price_are_not_in_the_vocabulary_that_gets_dropped()
    {
        // Condition, completeness and authenticity decide which end of the spread a thing lands on.
        // Dropping one is the SILENT failure; keeping a word that did not matter is the loud one.
        foreach (var keeper in new[]
                 {
                     "sealed", "graded", "psa", "mint", "nib", "refurb", "tested", "untested",
                     "broken", "vintage", "authentic", "genuine", "complete",
                 })
        {
            Assert.DoesNotContain($"|{keeper}", Builder, StringComparison.OrdinalIgnoreCase);
        }

        // And the two that were taken back OUT of the hype vocabulary once it was asked what a live
        // feed actually sells.
        Assert.DoesNotContain("|hot|", Builder, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("|fire|", Builder, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_builder_never_decides_anything_about_money()
    {
        foreach (var money in new[] { "ProfitCalculator", "FeeProfile", "MaxBid", "BreakEven" })
            Assert.DoesNotContain(money, Builder, StringComparison.Ordinal);
    }

    // ── What the screen shows, and the way back ──────────────────────────────────────────────

    [Fact]
    public void The_strip_is_on_every_card_rather_than_only_the_edited_ones()
    {
        // A line that only appears when the app changed the question is a line whose absence means
        // two different things.
        Assert.Contains("const searchStrip = s.query ?", Js, StringComparison.Ordinal);
        Assert.Contains("${searchStrip}", Js, StringComparison.Ordinal);
        Assert.Contains("Searched eBay for", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_seller_can_overrule_it_and_take_it_back()
    {
        Assert.Contains("data-exact=\"${s.askedForExactly ? 'off' : 'on'}\"", Js, StringComparison.Ordinal);
        Assert.Contains("searchExact: wnAskedForExactly(item)", Js, StringComparison.Ordinal);
        Assert.Contains("public bool? SearchExact { get; set; }", ReadSource("Models/LiveBidModels.cs"),
            StringComparison.Ordinal);
        Assert.Contains("req.SearchExact == true ? LiveSearchQuery.Exact(title) : LiveSearchQuery.Build(title)",
            Program, StringComparison.Ordinal);
    }

    [Fact]
    public void Asking_for_the_exact_words_cannot_leak_onto_the_next_lot()
    {
        // Held as the ITEM it was asked for rather than as a flag. Three lots later, a bare flag
        // would still be searching an unrelated name with somebody's "NO RESERVE" in it.
        var scoped = Section(Js, "function wnAskedForExactly(item) {", "\n  }");

        Assert.Contains("wnExactFor === (item || '').trim()", scoped, StringComparison.Ordinal);
        Assert.Contains("wnExactFor !== ''", scoped, StringComparison.Ordinal);
    }

    [Fact]
    public void Overruling_the_search_reads_ebay_again_rather_than_relabelling_held_comps()
    {
        // The question being asked is what changed, so the comps in hand are the answer to a
        // different one.
        var click = Section(Js, "const exact = e.target.closest('[data-exact]');", "\n    });");

        Assert.Contains("wnPriceItem();", click, StringComparison.Ordinal);
        Assert.DoesNotContain("wnRebid", click, StringComparison.Ordinal);
    }

    [Fact]
    public void A_widened_search_is_told_apart_by_the_stylesheet_and_said_in_the_warnings()
    {
        foreach (var rule in new[] { ".wn-search", ".wn-search-wide", ".wn-search-drop", ".wn-search-undo" })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        Assert.Contains("LiveSearchQuery.WidenedWarning(card.Search)", Advisor, StringComparison.Ordinal);
    }

    [Fact]
    public void The_strip_is_not_a_second_live_region()
    {
        var strip = Section(Js, "const searchStrip =", "</div>` : '';");

        Assert.DoesNotContain("aria-live", strip, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", strip, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_and_the_script_were_both_re_stamped()
    {
        Assert.True(AssetVersion(Html, "app.js?v=") >= 128, "app.js changed, so its stamp must move");
        Assert.True(AssetVersion(Html, "style.css?v=") >= 111, "style.css changed, so its stamp must move");
    }

    [Fact]
    public void The_screen_still_fits_a_window_down_the_side_of_a_stream()
    {
        var narrow = Section(Css.Replace("\r\n", "\n"), "@media (max-width: 620px) {\n  .wn-field,", "\n}");

        Assert.Contains(".wn-search-line", narrow, StringComparison.Ordinal);
    }

    // ── Additive, as every WhatsNot session has been ─────────────────────────────────────────

    [Fact]
    public void Sold_comps_and_every_endpoint_this_screen_already_had_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps",
                     "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
                     "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lot_list_gets_the_cleaning_for_free()
    {
        // Every pasted line goes back through /api/whatsnot/bid one at a time, which cleans that
        // line's own name. Nothing about the list needed changing, and nothing about it may quietly
        // stop working either.
        Assert.Contains("safePost('/api/whatsnot/bid', {", Js, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/lots\"", Program, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find \"{to}\" after \"{from}\"");
        return text[start..end];
    }

    private static int AssetVersion(string html, string prefix)
    {
        var at = html.IndexOf(prefix, StringComparison.Ordinal);
        Assert.True(at >= 0, $"\"{prefix}\" is no longer in index.html");
        var digits = new string(html[(at + prefix.Length)..].TakeWhile(char.IsDigit).ToArray());
        Assert.NotEqual("", digits);
        return int.Parse(digits);
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
