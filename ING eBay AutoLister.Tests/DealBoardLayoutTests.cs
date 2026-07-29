using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Opportunity Finder's deals board is the screen a seller reads while deciding where their
/// money goes, and it had grown fourteen columns. Fourteen fit no window: the wrapper carried a
/// horizontal scrollbar, the table carried a 1040px floor to force one, the results block was
/// capped at 1320px on a monitor with room to spare, and the whole box scrolled vertically inside
/// a page that was already scrolling. The right-hand columns arrived cut off mid-word.
///
/// Nothing in C# renders this table, so nothing in C# fails when a fifteenth column is added and
/// the scrollbar comes back — which is exactly how it got to fourteen. These lock the three things
/// that keep it fitting: the column set, the promise that every figure taken out of a column is
/// still on the row, and the stylesheet not reintroducing a scroll in either axis.
/// </summary>
public class DealBoardLayoutTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");

    /// <summary>
    /// The buying decision and nothing else: what it is, where it is, what it costs, what it
    /// nets, how long the money is gone, the ceiling, and the two things you can do about it.
    /// Adding to this list is adding width the window does not have — if a column really has to
    /// join them, another one has to leave.
    /// </summary>
    private static readonly string[] TheDecision =
    [
        "#", "Deal", "Source", "You pay", "Net profit", "Days to cash", "Max to pay", "Offer them", "Track",
    ];

    /// <summary>The columns folded into the row detail. None of them may come back as a column.</summary>
    private static readonly string[] Folded =
        ["Resell on eBay", "Fees", "ROI", "Margin", "Evidence"];

    [Fact]
    public void TheBoardShowsOnlyTheColumnsABuyingDecisionNeeds()
    {
        Assert.Equal(TheDecision, BoardColumns());
    }

    [Fact]
    public void TheColumnsThatWereFoldedAwayAreNotBackAsColumns()
    {
        var columns = BoardColumns().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var returned = Folded.Where(columns.Contains).ToList();

        Assert.True(returned.Count == 0,
            "these were moved into the row detail because they do not fit across a window, and " +
            $"they are columns again: {string.Join(", ", returned)}");
    }

    [Fact]
    public void EveryFigureTakenOutOfAColumnIsStillOnTheRow()
    {
        // The bargain the cut was made under: fewer columns, not fewer numbers. Each of these is a
        // figure the board used to show behind a scrollbar and now shows one click down.
        var detail = FunctionBody("arbitrageRowDetailHtml");

        foreach (var (figure, expression) in new[]
        {
            ("the resale price every profit figure was computed from", "row.ebayExpectedSale"),
            ("the fees taken back out of it", "row.estimatedFees"),
            ("ROI", "row.roiPercent"),
            ("margin", "row.marginPercent"),
            ("the shipping-and-warranty line on the resale price", "warrantyResaleLine(row)"),
            ("the title and transport costs a parcel row has no line for", "categoryCostLine(row)"),
            ("the estimate-unavailable answer on an unpriced row", "manualResaleCell(row)"),
        })
        {
            Assert.True(detail.Contains(expression, StringComparison.Ordinal),
                $"{figure} is no longer rendered anywhere on the row — it was moved out of a column, " +
                $"not deleted (looked for {expression} in arbitrageRowDetailHtml)");
        }

        // Every figure carries the label it had as a column heading, or it is a number with no
        // name on a screen someone spends money from.
        foreach (var label in new[] { "Resell on eBay", "eBay fees", "ROI", "Margin", "Evidence" })
            Assert.Contains(label, detail, StringComparison.Ordinal);

        // The evidence prose — where the price came from and how far to believe it — is built by
        // the row and handed to the detail. It is the one "figure" that is a sentence.
        Assert.Contains("arbitrageRowDetailHtml(row, detailId, guessed, evidence)", FunctionBody("arbitrageRowHtml"));
    }

    [Fact]
    public void TheRankedRowStillCarriesTheFiguresItIsRankedBy()
    {
        // ROI is the board's default sort. A ranking key nobody can see is a ranking nobody can
        // check, so it stays visible on the row itself as well as in the detail.
        var row = FunctionBody("arbitrageRowHtml");
        Assert.Contains("roiLine", row, StringComparison.Ordinal);
        Assert.Contains("fb-arb-roi-line", row, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCellSaysWhichColumnItIsSoTheRowCanStack()
    {
        // Below the width the columns need, the header row is hidden and each cell shows its own
        // label instead (the @container block in style.css). A cell with no data-label stacks as a
        // bare number with nothing saying what it is.
        // Line by line rather than by tag: a cell's class attribute holds template expressions
        // ("${row.netProfit > 0 ...}"), so there is no honest "end of the opening tag" to match on.
        var unlabelled = FunctionBody("arbitrageRowHtml")
            .Split('\n')
            .Where(line => line.Contains("<td", StringComparison.Ordinal)
                        && !line.Contains("data-label=", StringComparison.Ordinal))
            .Select(line => line.Trim())
            .ToList();

        Assert.True(unlabelled.Count == 0,
            $"cells with no data-label to stack under: {string.Join(" | ", unlabelled)}");
    }

    [Fact]
    public void TheDetailAndTheEmptyStateSpanTheColumnsThatExist()
    {
        var columns = BoardColumns().Count;

        foreach (Match m in Regex.Matches(Js, @"colspan=""(\d+)"" class=""fb-arb-empty"""))
            Assert.Equal(columns, int.Parse(m.Groups[1].Value));

        var detail = Regex.Match(FunctionBody("arbitrageRowDetailHtml"), @"class=""fb-arb-detail"" colspan=""(\d+)""");
        Assert.True(detail.Success, "the row detail no longer spans the table");
        Assert.Equal(columns, int.Parse(detail.Groups[1].Value));
    }

    [Fact]
    public void NothingLeftInTheStylesheetCanBringTheHorizontalScrollbackBack()
    {
        // The two rules that produced the scrollbar the seller drew over: overflow-x on the
        // wrapper, and the min-width on the table that made the overflow necessary in the first
        // place. Read as "what wins for the board", in source order, because the wrapper still
        // shares a class with the budget basket — which is a modal table and does still scroll.
        var overflow = WinningValue(BoardWrapRules(), "overflow", "overflow-x");
        Assert.False(overflow is "auto" or "scroll",
            $"the deals board is scrollable again (overflow: {overflow})");

        var floor = WinningValue(BoardTableRules(), "min-width");
        Assert.True(floor is null,
            $"the deals table has a min-width again ({floor}) — that is what forced the overflow");
    }

    [Fact]
    public void TheBoardDoesNotScrollInsideThePage()
    {
        // A box scrolling inside a scrolling page: the seller's screenshot had two scrollbars on
        // one table. The page scrolls; the results do not.
        var height = WinningValue(BoardWrapRules(), "max-height");
        Assert.True(height is null or "none",
            $"the deals board is bounded again (max-height: {height}), so it scrolls inside the page");
    }

    [Fact]
    public void TheBoardIsNotSqueezedIntoTheOverlaysProseColumn()
    {
        // Every overlay page shares a 1320px column, which is right for prose and forms and wrong
        // for a ranking — that cap is why the table felt small on a wide monitor. The board breaks
        // out of it and is measured against the overlay body instead.
        var breakout = RuleBody("#fb-arb-results");
        Assert.NotNull(breakout);
        Assert.Contains("max-width: none", breakout!, StringComparison.Ordinal);
        Assert.Contains("cqw", breakout!, StringComparison.Ordinal);
        Assert.Contains("container-type: inline-size", RuleBody("#opportunity-section > .opportunity-overlay-body") ?? "",
            StringComparison.Ordinal);
    }

    [Fact]
    public void ANarrowWindowStacksTheRowRatherThanScrollingSideways()
    {
        Assert.Matches(@"@container dealboard \(max-width: \d+px\)", Css);
        Assert.Contains("container-name: dealboard", Css, StringComparison.Ordinal);
        // The label half of each stacked pair comes from the cell's own data-label.
        Assert.Contains("content: attr(data-label)", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowStatesSurviveBeingTwoRows()
    {
        // A deal is a row plus its folded detail now, so the stripe and the top-three rank colour
        // can no longer count table rows — every second one is a detail panel. The renderer marks
        // them instead, and both halves of that have to exist or the board loses its zebra.
        var row = FunctionBody("arbitrageRowHtml");
        Assert.Contains("is-alt", row, StringComparison.Ordinal);
        Assert.Contains("is-top3", row, StringComparison.Ordinal);

        Assert.Contains(".fb-arb-table tbody tr.is-alt", Css, StringComparison.Ordinal);
        Assert.Contains(".fb-arb-table tbody tr.is-top3 > td.fb-arb-th-rank", Css, StringComparison.Ordinal);

        // The states that were there before the cut and must read the same after it.
        Assert.Contains("fb-arb-row-goldmine", Css, StringComparison.Ordinal);
        Assert.Contains("tbody tr:hover", Css, StringComparison.Ordinal);
        Assert.Contains("dt-money", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFoldOpensFromTheRowAndSaysSoToAScreenReader()
    {
        var row = FunctionBody("arbitrageRowHtml");
        Assert.Contains("aria-expanded=\"false\"", row, StringComparison.Ordinal);
        Assert.Contains("aria-controls=\"${detailId}\"", row, StringComparison.Ordinal);
        Assert.Contains("aria-expanded", FunctionBody("toggleArbitrageDetail"), StringComparison.Ordinal);
        // Bound after every render — the body is replaced wholesale by the sort and filter controls.
        Assert.Contains("querySelectorAll('.fb-arb-more')", Js, StringComparison.Ordinal);
    }

    // ── Reading the assets ────────────────────────────────────────────────

    private static List<string> BoardColumns()
    {
        var thead = Regex.Match(Html,
            @"<table class=""fb-arb-table fb-arb-board"">.*?<thead>(.*?)</thead>", RegexOptions.Singleline);
        Assert.True(thead.Success, "the deals board table is no longer in index.html");

        return Regex.Matches(thead.Groups[1].Value, @"<th\b[^>]*>(.*?)</th>", RegexOptions.Singleline)
                    .Select(m => m.Groups[1].Value.Trim())
                    .ToList();
    }

    /// <summary>A top-level <c>function name(...) { ... }</c> body from app.js, brace-matched.</summary>
    private static string FunctionBody(string name)
    {
        var start = Js.IndexOf($"function {name}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{name}() is gone from app.js");

        var depth = 0;
        for (var i = Js.IndexOf('{', start); i < Js.Length; i++)
        {
            if (Js[i] == '{') depth++;
            else if (Js[i] == '}' && --depth == 0) return Js[start..(i + 1)];
        }

        Assert.Fail($"{name}() is never closed");
        return "";
    }

    /// <summary>Every declaration block in the stylesheet, in source order, comments stripped.</summary>
    private static IEnumerable<(string Selector, string Body)> Rules()
    {
        var css = Regex.Replace(Css, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}")
                    .Select(m => (m.Groups[1].Value.Trim(), m.Groups[2].Value));
    }

    /// <summary>Rules landing on the board's wrapper itself — including ones it shares with other tables.</summary>
    private static List<(string Selector, string Body)> BoardWrapRules() =>
        Rules().Where(r => TargetsElement(r.Selector, "fb-arb-table-wrap", "fb-arb-board-wrap")).ToList();

    /// <summary>
    /// Rules landing on the board's table itself — not on a cell inside it, and not on the budget
    /// basket, which is a modal table and does still scroll.
    /// </summary>
    private static List<(string Selector, string Body)> BoardTableRules() =>
        Rules().Where(r => TargetsElement(r.Selector, "fb-arb-table", "fb-arb-board")
                        && !r.Selector.Contains(".bud-table", StringComparison.Ordinal))
               .ToList();

    /// <summary>
    /// True when a selector list styles one of these elements directly rather than a descendant:
    /// the last compound of some selector in the list carries the class. `.fb-arb-table td` styles
    /// a cell, `.fb-arb-table` styles the table.
    /// </summary>
    private static bool TargetsElement(string selectorList, params string[] classes) =>
        selectorList.Split(',')
            .Select(s => s.Split([' ', '>', '+', '~'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "")
            .Any(compound => classes.Any(c => Regex.IsMatch(compound, $@"\.{Regex.Escape(c)}(?![\w-])")));

    /// <summary>
    /// The value the cascade lands on for a property, given rules of equal weight in source order.
    /// Later wins, which is how the board's own rules override the ones it shares.
    /// </summary>
    private static string? WinningValue(IEnumerable<(string Selector, string Body)> rules, params string[] properties)
    {
        string? winner = null;

        foreach (var (_, body) in rules)
            foreach (var property in properties)
            {
                var m = Regex.Match(body, $@"(?:^|;)\s*{Regex.Escape(property)}\s*:\s*([^;]+)");
                if (m.Success) winner = m.Groups[1].Value.Trim();
            }

        return winner;
    }

    private static string? RuleBody(string selector) =>
        Rules().Where(r => r.Selector == selector).Select(r => (string?)r.Body).LastOrDefault();

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
