namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The buy sheet, held to the four things that make it worth having.
/// </summary>
/// <remarks>
/// <para>
/// <b>A won lot is the same card.</b> The win endpoint re-runs <c>LiveBidAdvisor.Build</c> against
/// comps already held; it never reaches for eBay. One line of "convenience" — recomputing the
/// resale price here, or letting the browser send it — turns the sheet into a second opinion about
/// money, and the seller acts on whichever of the two they read last. Nothing in C# would notice.
/// </para>
/// <para>
/// <b>The words about money are the server's.</b> The night's sentence and each row's are written
/// beside the arithmetic, and the browser paints them. A summary assembled in JavaScript out of
/// <c>spent</c> and <c>projectedProfit</c> is the one that would be on screen while the panel is
/// shut.
/// </para>
/// <para>
/// <b>It is additive.</b> Sold comps, the card, the re-price, the lot list and the embed check are
/// all asserted to still be registered — a session that adds a panel is exactly the kind of session
/// that quietly takes an endpoint off a screen.
/// </para>
/// </remarks>
public class WhatsNotBuySheetAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Sheet = ReadSource(Path.Combine("Services", "LiveBuySheet.cs"));

    // ── The win is the card, at the price it hammered at ──────────────────────

    [Fact]
    public void The_four_buy_sheet_endpoints_are_registered()
    {
        Assert.Contains("app.MapPost(\"/api/whatsnot/won\"", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapGet(\"/api/whatsnot/sheet\"", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/sheet/remove\"", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/sheet/clear\"", Program, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<LiveBuySheet>();", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The row's money comes back from the advisor. If this endpoint ever grew its own arithmetic
    /// the sheet and the card would disagree about one lot at one price.
    /// </summary>
    [Fact]
    public void A_win_is_priced_by_the_same_advisor_the_card_uses()
    {
        var won = WonEndpoint();

        Assert.Contains("advisor.Build(quote.Item, quote.Analysis, req.AsBid(), feeProfile, quote.Category)",
            won, StringComparison.Ordinal);
        Assert.Contains("sheet.Record(card)", won, StringComparison.Ordinal);
    }

    /// <summary>
    /// No eBay read. The hammer falls and the sheet has to move in milliseconds, which it can only
    /// do because the comps are already in hand — the same reason the re-price is instant.
    /// </summary>
    [Fact]
    public void Recording_a_win_reads_the_held_comps_and_never_ebay()
    {
        var won = WonEndpoint();

        Assert.Contains("board.Find(req.Token)", won, StringComparison.Ordinal);
        Assert.DoesNotContain("AnalyzeProductAsync", won, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplace", won, StringComparison.Ordinal);
        Assert.DoesNotContain("await", won, StringComparison.Ordinal);
    }

    /// <summary>
    /// A win with no sold history behind it would be a real spend sitting next to an invented
    /// resale price — and every total on the sheet would carry it.
    /// </summary>
    [Fact]
    public void A_win_with_no_held_comps_is_refused_rather_than_priced_off_nothing()
    {
        var won = WonEndpoint();

        Assert.Contains("if (quote is null)", won, StringComparison.Ordinal);
        Assert.Contains("Those comps have been let go", won, StringComparison.Ordinal);
        Assert.Contains("Press Price it to read eBay again, then record the win.", won, StringComparison.Ordinal);
    }

    /// <summary>Same guard the re-price has: a token records the lot it was issued for and no
    /// other, or one item's comps end up filed under another item's name.</summary>
    [Fact]
    public void A_token_records_the_lot_it_was_issued_for()
    {
        var won = WonEndpoint();

        Assert.Contains("!string.Equals(typed, quote.Item, StringComparison.OrdinalIgnoreCase)",
            won, StringComparison.Ordinal);
        Assert.Contains("That's a different item", won, StringComparison.Ordinal);
    }

    [Fact]
    public void A_win_with_no_price_on_it_is_refused()
    {
        var won = WonEndpoint();

        Assert.Contains("What did it go for?", won, StringComparison.Ordinal);
        Assert.Contains("if (bid <= 0m)", won, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_buy_sheet_endpoint_is_behind_the_trial_guard()
    {
        foreach (var route in new[] { "/api/whatsnot/won", "/api/whatsnot/sheet\"", "/api/whatsnot/sheet/remove", "/api/whatsnot/sheet/clear" })
        {
            var at = Program.IndexOf(route, StringComparison.Ordinal);
            Assert.True(at > 0, $"{route} is not registered");
            var block = Program.Substring(at, Math.Min(900, Program.Length - at));
            Assert.Contains("TrialGuard(store, license)", block, StringComparison.Ordinal);
        }
    }

    // ── A buy record, never a comp source ─────────────────────────────────────

    /// <summary>
    /// These are prices <i>this</i> seller paid at auction. Letting them anywhere near the pricing
    /// pipeline would be the app quoting itself, and nothing would fail loudly — every estimate
    /// would simply drift toward what one person paid on one night.
    /// </summary>
    [Fact]
    public void The_sheet_is_never_wired_into_anything_that_prices_an_item()
    {
        foreach (var forbidden in new[]
                 { "IMarketplaceRepository", "MarketplaceRepository", "SoldListings", "ComparableMatcher", "MarketPriceEstimator" })
        {
            Assert.DoesNotContain(forbidden, Sheet, StringComparison.Ordinal);
        }
    }

    /// <summary>A show runs for hours. An app restarted in the middle of one must come back to the
    /// money already spent, and through the write that cannot leave a half-file behind.</summary>
    [Fact]
    public void The_sheet_is_persisted_atomically()
    {
        Assert.Contains("AtomicFile.WriteAllText", Sheet, StringComparison.Ordinal);
        Assert.Contains("AtomicFile.ReadWithRecovery", Sheet, StringComparison.Ordinal);
        Assert.Contains("AppPaths.DataHome", Sheet, StringComparison.Ordinal);
    }

    // ── The words about money are the server's ────────────────────────────────

    /// <summary>
    /// The collapsed panel is the only part of this most sellers will read. It says the server's
    /// sentence and nothing the browser thought of.
    /// </summary>
    [Fact]
    public void The_nights_sentence_is_painted_verbatim_and_never_assembled()
    {
        Assert.Contains("head.textContent = lots.length ? (sheet.say || '')", Js, StringComparison.Ordinal);

        var render = Between(Js, "function wnRenderSheet(sheet)", "async function wnLoadSheet()");

        // No arithmetic on the sheet's money. Every figure here is one the server computed; this
        // formats them and does nothing else.
        Assert.DoesNotContain("* 100", render, StringComparison.Ordinal);
        Assert.DoesNotContain("sheet.projectedProfit /", render, StringComparison.Ordinal);
        Assert.DoesNotContain("sheet.spent -", render, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_is_announced_as_the_servers_own_sentence_about_it()
    {
        var render = Between(Js, "function wnRenderSheet(sheet)", "async function wnLoadSheet()");

        Assert.Contains("aria-label=\"${esc(l.say || l.item)}\"", render, StringComparison.Ordinal);
    }

    /// <summary>
    /// The seller pressed a button and never looked away from the stream. What was recorded and
    /// what the night now stands at both reach the one line above the card — and both halves are
    /// the server's sentences.
    /// </summary>
    [Fact]
    public void A_recorded_win_is_announced_through_the_screens_one_live_region()
    {
        var record = Between(Js, "async function wnRecordWin()", "async function wnRemoveWin(id)");

        Assert.Contains("wnSayLine(`${just?.say ? `${just.say} ` : ''}${body.say || ''}`.trim());",
            record, StringComparison.Ordinal);
    }

    /// <summary>The one line above the card stays the only live region on this screen. A second one
    /// down here would talk over the answer being read while the bidding runs.</summary>
    [Fact]
    public void The_buy_sheet_panel_is_not_a_second_live_region()
    {
        var panel = Between(Html, "<details id=\"wn-sheet\"", "</details>");

        Assert.DoesNotContain("aria-live", panel, StringComparison.Ordinal);
        Assert.DoesNotContain("role=\"status\"", panel, StringComparison.Ordinal);
    }

    // ── The button on the card ────────────────────────────────────────────────

    /// <summary>
    /// Offered only while the comps are held, because that is the only state the server will record
    /// a win in. A button that is always there and fails half the time is a button nobody presses
    /// during the two seconds after a hammer.
    /// </summary>
    [Fact]
    public void Won_it_is_offered_only_when_there_are_comps_to_cost_it_against()
    {
        Assert.Contains("const won = c.token ? `", Js, StringComparison.Ordinal);
    }

    /// <summary>The card is replaced whole every time the bid moves, so a handler bound to the
    /// button itself is a handler that stops existing two seconds after it was bound.</summary>
    [Fact]
    public void The_win_click_is_caught_on_the_card_rather_than_on_the_button()
    {
        Assert.Contains("if (e.target.closest('[data-won]')) wnRecordWin();", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// A lot can be won above the ceiling — that happens, and recording it is the entire point of
    /// the discipline half of the sheet. The button says so first rather than refusing.
    /// </summary>
    [Fact]
    public void Winning_above_the_ceiling_is_warned_about_and_never_refused()
    {
        Assert.Contains("wn-won-btn-over", Js, StringComparison.Ordinal);
        Assert.Contains("over your ceiling — it will be recorded as such", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-won-btn-over", Css, StringComparison.Ordinal);
    }

    // ── The panel ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_panel_and_its_parts_are_on_the_screen()
    {
        foreach (var id in new[] { "wn-sheet", "wn-sheet-head", "wn-sheet-totals", "wn-sheet-rows", "wn-sheet-clear" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.True(Html.IndexOf("id=\"wn-card\"", StringComparison.Ordinal)
                  < Html.IndexOf("id=\"wn-sheet\"", StringComparison.Ordinal),
            "the sheet belongs under the card it is filled from");
    }

    /// <summary>Rows are a real list, so a screen reader says "3 of 9" rather than reading nine
    /// unnumbered paragraphs, and the remove control is a real button.</summary>
    [Fact]
    public void The_rows_are_a_list_and_removing_one_is_a_real_button()
    {
        var render = Between(Js, "function wnRenderSheet(sheet)", "async function wnLoadSheet()");

        Assert.Contains("<ul class=\"wn-sheet-list\">", render, StringComparison.Ordinal);
        Assert.Contains("<li class=\"wn-sheet-row", render, StringComparison.Ordinal);
        Assert.Contains("<button type=\"button\" class=\"wn-sheet-remove\"", render, StringComparison.Ordinal);
        Assert.Contains(".wn-sheet-list", Css, StringComparison.Ordinal);
    }

    /// <summary>The only button on this screen that throws away something no other press can bring
    /// back, so it asks first — and quotes the night it is about to discard.</summary>
    [Fact]
    public void Clearing_the_sheet_asks_first()
    {
        var clear = Between(Js, "async function wnClearSheet()", "// ── WhatsNot: the show's lot list");

        Assert.Contains("confirm(", clear, StringComparison.Ordinal);
        Assert.Contains("wn-sheet-head", clear, StringComparison.Ordinal);
    }

    /// <summary>Read on every open, not once: the sheet outlives the session, and a blank panel
    /// reads as a night nobody has bought anything on.</summary>
    [Fact]
    public void The_sheet_is_read_when_the_tab_opens()
    {
        var open = Between(Js, "async function showWhatsNotSection()", "function closeWhatsNotSection()");

        Assert.Contains("wnLoadSheet();", open, StringComparison.Ordinal);
    }

    /// <summary>The way this screen is actually used is a narrow window down the side of a live
    /// stream, so the tiles fold like everything else here.</summary>
    [Fact]
    public void The_totals_fold_on_a_narrow_window()
    {
        // Anchored on this screen's own fold. There is more than one 620px block in the sheet.
        var narrow = Between(Css, "Narrower than this the app is a window down the side of a live stream", ".wn-frame {");

        Assert.Contains(".wn-sheet-tile", narrow, StringComparison.Ordinal);
        Assert.Contains(".wn-sheet-row", narrow, StringComparison.Ordinal);
    }

    // ── Additive, as every WhatsNot session has been ──────────────────────────

    [Fact]
    public void Sold_comps_and_every_earlier_whatsnot_endpoint_are_still_registered()
    {
        foreach (var route in new[]
                 {
                     "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid",
                     "/api/whatsnot/lots", "/api/whatsnot/embed-check",
                 })
        {
            Assert.Contains(route, Program, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_card_the_re_price_and_the_lot_list_are_untouched_by_this_pass()
    {
        Assert.Contains("const WN_REBID_DEBOUNCE_MS = 90;", Js, StringComparison.Ordinal);
        Assert.Contains("if (seq !== wnRebidSeq) return;", Js, StringComparison.Ordinal);
        Assert.Contains("function wnPriceLotList()", Js, StringComparison.Ordinal);
        Assert.Contains("wnSayLine(c.say, c.call);", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_assets_are_versioned_past_the_build_that_shipped_without_the_buy_sheet()
    {
        Assert.True(AssetVersion("app.js") >= 121, "app.js changed, so index.html's ?v= must move past 120");
        Assert.True(AssetVersion("style.css") >= 104, "style.css changed, so index.html's ?v= must move past 103");
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string WonEndpoint() =>
        Between(Program, "app.MapPost(\"/api/whatsnot/won\"", "// The sheet as it stands.");

    private static int AssetVersion(string file)
    {
        var marker = $"{file}?v=";
        var at = Html.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at > 0, $"{file} carries no cache-buster");
        var digits = new string(Html[(at + marker.Length)..].TakeWhile(char.IsDigit).ToArray());
        return int.Parse(digits);
    }

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find the end of \"{from}\"");
        return source[start..end];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", relativePath));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
