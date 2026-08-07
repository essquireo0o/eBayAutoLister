namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The night's money reaches the live card through eight links, and most of them are the sort of
/// thing a later tidy-up removes without reading why: one box on the form, the browser sending it on
/// every path a lot is costed BEFORE the hammer, the buy sheet handing over what has already gone,
/// the advisor reading both, the ceiling coming down to what the cash lands, the badge saying so in
/// cash rather than in comps, the strip drawing it, and the win deliberately NOT carrying it. Break
/// any one and the feature silently does nothing on every card forever, which looks exactly like
/// working.
/// </summary>
/// <remarks>
/// <para>
/// Three of these are decisions rather than plumbing. An empty box <b>caps nothing</b> whatever is on
/// tonight's sheet — a ceiling that quietly fell 70% for a limit nobody set is a ceiling the seller
/// cannot check. The cut lands on the <b>ceiling and never on the resale price</b>, because where the
/// seller's money went is not a fact about what the thing fetches. And a recorded win carries every
/// other cost on the card and <b>not this one</b>: the ceiling written on a buy-sheet row is what the
/// seller's discipline is measured against, and a lot won cheap on a night the cash ran out is not an
/// overpay.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched, and this is purely additive to it.
/// </para>
/// </remarks>
public class WhatsNotBudgetAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Budget = ReadSource("Services/LiveBudget.cs");
    private static readonly string BudgetModels = ReadSource("Models/LiveBudgetModels.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string BuyModels = ReadSource("Models/LiveBuyModels.cs");
    private static readonly string Sheet = ReadSource("Services/LiveBuySheet.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");

    // ── The box ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One box, last on the row, with a dash for a placeholder. A figure there would read as a
    /// default that is being enforced, and nothing here caps a ceiling for a number nobody typed.
    /// </summary>
    [Fact]
    public void The_form_has_a_budget_box_that_suggests_nothing()
    {
        Assert.Contains("id=\"wn-budget\"", Html, StringComparison.Ordinal);

        var box = Html[Html.IndexOf("id=\"wn-budget\"", StringComparison.Ordinal)..];
        Assert.Contains("placeholder=\"—\"", box[..140], StringComparison.Ordinal);
    }

    /// <summary>
    /// It reaches every path a lot is costed on before the hammer — the fresh price, the instant
    /// re-price and the show's lot list — through one helper. A second assembly of the same field is
    /// how one path ends up capping a ceiling the others do not.
    /// </summary>
    [Fact]
    public void It_reaches_every_endpoint_that_prices_a_lot_before_the_hammer()
    {
        Assert.Contains("return { nightBudget: wnNumber('wn-budget') };", Js, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(Js, "...wnBudgetFields(),"));
    }

    /// <summary>
    /// And it deliberately does NOT reach the win. Everything else on that payload is carried so the
    /// row is costed at the card's own terms; this one is left behind so
    /// <c>WonLot.CeilingAtWin</c> — the sheet's one discipline column — stays the market's answer.
    /// </summary>
    [Fact]
    public void A_recorded_win_does_not_carry_the_budget()
    {
        var template = Js.Replace("\r\n", "\n");
        var at = template.IndexOf("'/api/whatsnot/won'", StringComparison.Ordinal);
        Assert.True(at > 0, "the win still posts to its own endpoint");

        var payload = template[at..(at + 700)];
        Assert.Contains("...wnTaxFields(),", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("wnBudgetFields", payload, StringComparison.Ordinal);

        // The request object has no field for it to arrive in, and the conversion to a bid — which
        // carries every other cost on the card — does not set one.
        Assert.DoesNotContain("public decimal? NightBudget { get; set; }", BuyModels, StringComparison.Ordinal);
        Assert.DoesNotContain("NightBudget =", BuyModels, StringComparison.Ordinal);

        // Nor does the endpoint hand the win a spend to be measured against.
        var won = Program[Program.IndexOf("app.MapPost(\"/api/whatsnot/won\"", StringComparison.Ordinal)..];
        won = won[..won.IndexOf("var result = sheet.Record(card);", StringComparison.Ordinal)];
        Assert.DoesNotContain("sheet.Committed()", won, StringComparison.Ordinal);
    }

    /// <summary>
    /// Typing a budget re-prices off the held comps with no eBay in it — which is the undo for a
    /// limit set too low on the one lot of the night the seller actually came for.
    /// </summary>
    [Fact]
    public void It_reprices_without_reading_ebay()
    {
        var template = Js.Replace("\r\n", "\n");
        var at = template.IndexOf("$(id)?.addEventListener('input', wnScheduleRebid)", StringComparison.Ordinal);
        Assert.True(at > 0, "the re-price list is still a literal list of box ids");

        var ids = template[..at];
        ids = ids[ids.LastIndexOf("['wn-bid'", StringComparison.Ordinal)..];
        Assert.Contains("'wn-budget'", ids, StringComparison.Ordinal);
    }

    /// <summary>
    /// Remembered, because a live show runs for hours and an app restarted in the middle of one must
    /// come back to the same limit rather than to an unlimited card. What has been spent against it
    /// is never remembered here — that is the buy sheet's, on the server.
    /// </summary>
    [Fact]
    public void The_budget_is_remembered_and_the_spend_is_not()
    {
        Assert.Contains("budget: $('wn-budget')?.value ?? ''", Js, StringComparison.Ordinal);
        Assert.Contains("if (saved.budget != null) setVal('wn-budget', saved.budget);", Js, StringComparison.Ordinal);

        // Nothing in the browser carries a running spend, and nothing subtracts one.
        Assert.DoesNotContain("wnSpentTonight", Js, StringComparison.Ordinal);
    }

    // ── The read ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_request_carries_a_budget_and_the_card_carries_the_read()
    {
        Assert.Contains("public decimal? NightBudget { get; set; }", BidModels, StringComparison.Ordinal);
        Assert.Contains("public LiveBudgetRead Budget { get; set; } = new();", BidModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spend comes off tonight's buy sheet — the same rows and the same landed cost
    /// <c>BuySheet.Spent</c> adds up, which is the figure the bank statement agrees with. Both
    /// endpoints that price a lot ask for it, and the re-price asks again rather than holding it:
    /// the seller changes it themselves, mid-lot, by pressing Won it on the one before.
    /// </summary>
    [Fact]
    public void The_spend_comes_off_the_buy_sheet_on_both_pricing_paths()
    {
        Assert.Contains("public LiveBudgetTonight Committed()", Sheet, StringComparison.Ordinal);
        Assert.Contains("l.LandedCost", Sheet, StringComparison.Ordinal);

        Assert.Contains("var spent = sheet.Committed();", Program, StringComparison.Ordinal);
        Assert.Contains("cash: spent", Program, StringComparison.Ordinal);
        Assert.Contains("sheet.ShippingOnShow(req.ShowName), sheet.Committed(),", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_advisor_asks_the_read_and_only_then_lowers_the_ceiling()
    {
        Assert.Contains(
            "LiveBudget.Read(request.NightBudget, cash, maxBid, feePercent, shipping, taxPercent)",
            Advisor, StringComparison.Ordinal);
        Assert.Contains("card.Budget = budget;", Advisor, StringComparison.Ordinal);
        Assert.Contains("boundBy = LiveBudget.CeilingByBudget;", Advisor, StringComparison.Ordinal);
    }

    /// <summary>Four states, spelled once, so the strip, the speech, the CSS, the log and these
    /// tests cannot drift apart.</summary>
    [Fact]
    public void The_four_states_are_spelled_once()
    {
        foreach (var state in new[] { "none", "clear", "capped", "spent" })
            Assert.Contains($"= \"{state}\";", BudgetModels, StringComparison.Ordinal);
    }

    // ── The money ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One arithmetic for "what bid fits inside this much money". The remaining cash has to cover the
    /// bid, the premium on it, the tax on both and the freight — which is exactly what the card's own
    /// walk-away line already computes, so it is called rather than restated. A second one here is
    /// how the badge and the ladder end up disagreeing by the size of the premium.
    /// </summary>
    [Fact]
    public void The_cash_ceiling_runs_through_the_cards_own_conversion()
    {
        Assert.Contains(
            "LiveBidAdvisor.BreakEvenBid(read.Remaining, buyerFeePercent, shipping, salesTaxPercent)",
            Budget, StringComparison.Ordinal);

        // And nothing in this file divides by the premium and the tax a second time.
        Assert.DoesNotContain("/ 100m)", Budget.Replace("read.SpentPercent", ""), StringComparison.Ordinal);
    }

    /// <summary>
    /// The ceiling is lowered on exactly one line, and it is guarded by <c>Applied</c> — which is
    /// false whenever the market itself refused the lot. That is what makes "a card with no budget is
    /// priced as it was before this file existed" a property of the code rather than a promise.
    /// </summary>
    [Fact]
    public void The_ceiling_is_lowered_on_exactly_one_line_behind_one_guard()
    {
        Assert.Equal(1, Occurrences(Advisor, "maxBid = budget.Ceiling;"));
        Assert.Contains("if (budget.Applied)", Advisor, StringComparison.Ordinal);
        Assert.Contains(
            "MarketCeiling > 0m && Verdict is LiveBudgetVerdicts.Capped or LiveBudgetVerdicts.Spent",
            BudgetModels, StringComparison.Ordinal);
    }

    /// <summary>
    /// It never touches the market. Where the seller's money went is not a fact about what the thing
    /// fetches, so the read takes no comps at all — a cash limit that could see the sold history
    /// would be a cash limit that could re-rate it.
    /// </summary>
    [Fact]
    public void The_read_never_sees_a_comp()
    {
        Assert.DoesNotContain("Comparable", Budget, StringComparison.Ordinal);
        Assert.DoesNotContain("MarketAnalysisResult", Budget, StringComparison.Ordinal);
        Assert.DoesNotContain("ResalePricing", Budget, StringComparison.Ordinal);
    }

    /// <summary>
    /// The budget never reaches what eBay is asked. What the seller can afford is no part of what the
    /// thing IS, and a dollar figure in the sold search would return nothing at all.
    /// </summary>
    [Fact]
    public void The_budget_never_reaches_what_ebay_is_asked()
    {
        var query = ReadSource("Services/LiveSearchQuery.cs");

        Assert.DoesNotContain("LiveBudget", query, StringComparison.Ordinal);
        Assert.DoesNotContain("NightBudget", query, StringComparison.Ordinal);
    }

    // ── The words ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A lot refused for money is never described as a lot the market refused. The badge has its own
    /// label and its own sentence, and both come from the read rather than from the ceiling code.
    /// </summary>
    [Fact]
    public void The_badge_refuses_in_cash_rather_than_in_comps()
    {
        Assert.Contains("\"OUT OF CASH\"", Advisor, StringComparison.Ordinal);
        Assert.Contains("card.Budget is { Exhausted: true } spent", Advisor, StringComparison.Ordinal);
        Assert.Contains("your budget stopping you, not the item", Advisor, StringComparison.Ordinal);

        // And the market's own refusals are still reached first, on their own terms.
        var judge = Advisor[Advisor.IndexOf("public static (string Call, string Label, string Reason) Judge(",
            StringComparison.Ordinal)..];
        var marketAt = judge.IndexOf("card.BreakEvenBid > 0m", StringComparison.Ordinal);
        var cashAt = judge.IndexOf("\"OUT OF CASH\"", StringComparison.Ordinal);
        Assert.True(marketAt > 0 && marketAt < cashAt,
            "a lot the market refused has to be refused for the market's reason");
    }

    /// <summary>
    /// The one line says whose ceiling it is, with both figures — a seller who hears BID UP TO $62 on
    /// something they know fetches $200 assumes the app misread the item, and bids past it.
    /// </summary>
    [Fact]
    public void The_spoken_line_names_the_cash_and_the_comps()
    {
        Assert.Contains("WhoseCeilingThatIs(card)", Speech, StringComparison.Ordinal);
        Assert.Contains("card.Budget is not { Capped: true } budget", Speech, StringComparison.Ordinal);
        Assert.Contains("the comps back", Speech, StringComparison.Ordinal);
    }

    // ── The strip ────────────────────────────────────────────────────────────────────────────

    /// <summary>Drawn on every card, immediately above the ladder it can lower, and the browser adds
    /// nothing up: every word and every dollar on it is the server's.</summary>
    [Fact]
    public void The_strip_is_rendered_from_the_servers_own_words()
    {
        Assert.Contains("const bg = c.budget || {};", Js, StringComparison.Ordinal);
        Assert.Contains("esc(bg.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(bg.note)", Js, StringComparison.Ordinal);

        var template = Js.Replace("\r\n", "\n");
        Assert.Contains("${taxStrip}\n      ${budgetStrip}\n      ${ladder}", template, StringComparison.Ordinal);

        // The three figures are carried, not subtracted here.
        Assert.Contains("moneyExact(bg.budget)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(bg.committed)", Js, StringComparison.Ordinal);
        Assert.Contains("moneyExact(bg.remaining)", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("bg.budget -", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Four states, four edges. Only the spent one is drawn as a refusal: a budget nobody set and a
    /// budget with room left are both states of an accurate card, and so is the capped one — that is
    /// the card being right about a limit the seller chose.
    /// </summary>
    [Fact]
    public void Every_state_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var rule in new[]
                 {
                     ".wn-budget", ".wn-budget-none", ".wn-budget-clear", ".wn-budget-capped",
                     ".wn-budget-spent", ".wn-budget-line", ".wn-budget-tag", ".wn-budget-bar",
                     ".wn-budget-bar-spent", ".wn-budget-box", ".wn-budget-cell", ".wn-budget-note",
                     ".wn-field-budget",
                 })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        var spent = Css[Css.IndexOf(".wn-budget-spent {", StringComparison.Ordinal)..];
        Assert.Contains("var(--danger", spent[..spent.IndexOf('}')], StringComparison.Ordinal);

        var none = Css[Css.IndexOf(".wn-budget-none {", StringComparison.Ordinal)..];
        Assert.Contains("var(--border", none[..none.IndexOf('}')], StringComparison.Ordinal);

        Assert.Contains("style.css?v=125", Html, StringComparison.Ordinal);
        Assert.Contains("app.js?v=142", Html, StringComparison.Ordinal);
    }

    /// <summary>It folds on a narrow card, like every strip above it — this screen is used as a
    /// column down the side of a live stream.</summary>
    [Fact]
    public void The_strip_folds_on_a_narrow_card()
    {
        var narrow = Css[Css.IndexOf("@media", StringComparison.Ordinal)..];
        Assert.Contains(".wn-budget-line", narrow, StringComparison.Ordinal);
        Assert.Contains(".wn-field-budget", narrow, StringComparison.Ordinal);
    }

    /// <summary>The verdict, the money and both ceilings reach the seller's own action log, which is
    /// where the first real "the app said stop on a $200 lot because the night's budget was gone"
    /// will show up.</summary>
    [Fact]
    public void The_verdict_and_the_money_are_logged()
    {
        Assert.Contains("budget {card.Budget.Verdict}", Program, StringComparison.Ordinal);
        Assert.Contains("card.Budget.Remaining:C", Program, StringComparison.Ordinal);
        Assert.Contains("card.Budget.MarketCeiling:C", Program, StringComparison.Ordinal);
    }

    // ── Sold comps, untouched ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The constraint every WhatsNot session has worked under. The sold-comps path this whole screen
    /// stands on is untouched and this is purely additive to it.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
                     "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
                     "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
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
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
