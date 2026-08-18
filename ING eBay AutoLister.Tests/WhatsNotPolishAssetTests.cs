namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot arbitrage screen, held to being usable without a mouse and without sight.
///
/// Most of what this pins is one decision away from its opposite and nothing in C# would notice:
/// the card is <b>not</b> a live region and one line is, the sentence in that line is the server's
/// and is never assembled in the browser, a priced lot row is a real <c>&lt;button&gt;</c> rather
/// than a div wearing <c>role="button"</c>, keyboard focus survives the list re-rendering under it,
/// and a failure shows the half of itself that says what to do.
///
/// Sold comps are asserted here too. This session was a polish pass and polish is exactly the kind
/// of change that quietly takes an endpoint off a screen.
/// </summary>
public class WhatsNotPolishAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource(Path.Combine("Services", "LiveBidAdvisor.cs"));

    // ── One live region, and it is not the card ───────────────────────────────

    /// <summary>
    /// The card is replaced whole every time the bid moves — every two or three seconds during a
    /// live sale. As a live region that is a ladder, five stat tiles and a comp table announced
    /// several times a lot, which is an announcement nobody can keep up with.
    /// </summary>
    [Fact]
    public void The_card_is_not_a_live_region()
    {
        var card = Between(Html, "<div id=\"wn-card\"", ">");

        Assert.DoesNotContain("aria-live", card, StringComparison.Ordinal);
    }

    [Fact]
    public void One_line_is_the_live_region_and_it_is_above_the_card()
    {
        var say = Between(Html, "<p id=\"wn-say\"", "</p>");

        Assert.Contains("role=\"status\"", say, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", say, StringComparison.Ordinal);
        // Atomic: the sentence changes as a whole, and half of it read on its own is a different
        // claim about money from the whole of it.
        Assert.Contains("aria-atomic=\"true\"", say, StringComparison.Ordinal);

        Assert.True(Html.IndexOf("id=\"wn-say\"", StringComparison.Ordinal)
                  < Html.IndexOf("id=\"wn-card\"", StringComparison.Ordinal),
            "the one line belongs above the card it summarises");
    }

    /// <summary>
    /// The sentence is <c>card.say</c>, painted verbatim. A line assembled in JavaScript out of
    /// <c>maxBid</c> and <c>headroom</c> would be a second opinion about money that nothing tests,
    /// and the thing it would disagree with is the badge two inches below it.
    /// </summary>
    [Fact]
    public void The_browser_paints_the_line_and_never_writes_it()
    {
        Assert.Contains("wnSayLine(c.say, c.call);", Js, StringComparison.Ordinal);

        var speech = Between(Js, "function wnSayLine(", "  function wnStat(");
        foreach (var phrase in new[] { "of room", "past the ceiling", "Resells around", "sell-through" })
            Assert.DoesNotContain(phrase, speech, StringComparison.Ordinal);

        // And it does not reach for the numbers behind the sentence either.
        foreach (var field in new[] { "maxBid", "headroom", "resalePrice", "sellThroughRate" })
            Assert.DoesNotContain(field, speech, StringComparison.Ordinal);
    }

    /// <summary>Both exits of <c>Build</c> set it, so no card that reaches a screen can arrive
    /// without the line that screen reads out loud.</summary>
    [Fact]
    public void Every_card_the_advisor_returns_carries_the_line()
    {
        Assert.Equal(2, Count(Advisor, "card.Say = LiveBidSpeech.Say(card);"));
        Assert.Equal(2, Count(Advisor, "return card;"));
    }

    [Fact]
    public void The_line_wears_the_same_four_call_colours_the_badge_does()
    {
        Assert.Contains(".wn-say {", Css, StringComparison.Ordinal);
        foreach (var call in new[] { "bid", "risky", "stop", "no_data" })
            Assert.Contains($".wn-say-{call}", Css, StringComparison.Ordinal);

        // Anything else that arrives is dropped rather than turned into a class name.
        Assert.Contains("['bid', 'risky', 'stop', 'no_data'].includes(call)", Js, StringComparison.Ordinal);
    }

    // ── The lot rows are real buttons ─────────────────────────────────────────

    /// <summary>
    /// Enter, Space, the focus ring and the word "button" a screen reader says all come free with a
    /// real button, and none of them is a handler that can be dropped. The div-with-role version had
    /// to hand-roll the first two and never had the last.
    /// </summary>
    [Fact]
    public void A_priced_lot_row_is_a_button_and_not_a_div_pretending()
    {
        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");
        var markup = Between(rows, "return `<button type=\"button\"", "</button>`;");

        Assert.Contains("class=\"wn-lot-row wn-lot-priced", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("role=", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("tabindex", markup, StringComparison.Ordinal);
        // The hand-rolled Enter/Space handler is gone with the div that needed it.
        Assert.DoesNotContain("e.key === 'Enter' || e.key === ' '", rows, StringComparison.Ordinal);
    }

    /// <summary>The row is announced as the card's own one-line answer, so hearing a row and opening
    /// it say the same thing about the same lot.</summary>
    [Fact]
    public void A_row_is_read_out_as_the_card_it_opens()
    {
        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");

        Assert.Contains("aria-label=\"${esc(`${c.item}. ${c.say || ''}`.trim())}\"", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// Rows are replaced wholesale as each answer arrives. Focus lives on a DOM node, so without
    /// this a seller who has tabbed to the third row loses the keyboard to <c>&lt;body&gt;</c> every
    /// time a later lot finishes — silently, ten times over one paste.
    /// </summary>
    [Fact]
    public void Keyboard_focus_survives_the_list_rebuilding_under_it()
    {
        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");

        Assert.Contains("host.contains(document.activeElement)", rows, StringComparison.Ordinal);
        Assert.Contains("wnLots.indexOf(focusedLot)", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// Restored by LOT, not by position. The last render of a run re-orders the list, and a
    /// position-based restore would move the keyboard onto a different lot at exactly the moment
    /// the seller stopped watching the screen.
    /// </summary>
    [Fact]
    public void Focus_follows_the_lot_and_not_the_row_number()
    {
        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");

        Assert.Contains("const focusedLot = ", rows, StringComparison.Ordinal);
        Assert.Contains("wnLots[parseInt(document.activeElement.dataset.lot, 10)]", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_still_filling_in_says_it_is_busy()
    {
        var button = Between(Js, "function wnLotButton(running)", "  function wnLotsNote(");

        Assert.Contains("$('wn-lots-rows')?.setAttribute('aria-busy', String(!!running));", button, StringComparison.Ordinal);
    }

    [Fact]
    public void The_progress_note_is_spoken()
    {
        var note = Between(Html, "<span id=\"wn-lots-note\"", "</span>");

        Assert.Contains("role=\"status\"", note, StringComparison.Ordinal);
        Assert.Contains("aria-live=\"polite\"", note, StringComparison.Ordinal);
    }

    // ── Focus, and where the keyboard goes ────────────────────────────────────

    /// <summary>Opening a lot moves the keyboard to the answer. Without it, Enter on a row leaves
    /// focus in the list and the next Tab walks further down the lots rather than into the card.
    /// </summary>
    [Fact]
    public void Opening_a_lot_moves_the_keyboard_to_the_card()
    {
        var open = Between(Js, "function wnOpenLot(index)", "  function wnClearLotList()");

        Assert.Contains("card?.focus({ preventScroll: true });", open, StringComparison.Ordinal);
        Assert.Contains("tabindex=\"-1\"", Between(Html, "<div id=\"wn-card\"", ">"), StringComparison.Ordinal);
    }

    /// <summary>A programmatic focus is not a tab stop and gets no ring; a seller who tabbed here on
    /// purpose still does.</summary>
    [Fact]
    public void The_card_only_shows_a_ring_when_the_keyboard_put_it_there()
    {
        Assert.Contains(".wn-card:focus {", Css, StringComparison.Ordinal);
        Assert.Contains(".wn-card:focus-visible {", Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// The previous focus style changed a border colour and set <c>outline: none</c> — an indicator
    /// you cannot find on a screen holding twelve bordered rows.
    /// </summary>
    [Fact]
    public void Everything_focusable_on_this_screen_has_a_ring_you_can_see()
    {
        foreach (var selector in new[]
        {
            ".wn-lot-row.wn-lot-priced:focus-visible",
            ".wn-step:focus-visible",
            ".wn-queue-summary:focus-visible",
            ".wn-nav-btn:focus-visible",
        })
            Assert.Contains(selector, Css, StringComparison.Ordinal);

        Assert.DoesNotContain(
            ".wn-lot-row.wn-lot-priced:focus-visible {\n  border-color: var(--accent, #d4a24a);\n  outline: none;",
            Css.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// Disabling the focused button drops the keyboard to <c>&lt;body&gt;</c>, which during a live
    /// sale is a seller pressing keys at nothing.
    /// </summary>
    [Fact]
    public void The_price_button_gives_the_keyboard_back_when_it_finishes()
    {
        var price = Between(Js, "async function wnPriceItem()", "  /// ── The one line ─");

        Assert.Contains("const btnHadFocus = document.activeElement === btn;", price, StringComparison.Ordinal);
        Assert.Contains("if (btnHadFocus) btn.focus();", price, StringComparison.Ordinal);
        Assert.Contains("btn.setAttribute('aria-busy', 'true');", price, StringComparison.Ordinal);
        Assert.Contains("btn.removeAttribute('aria-busy');", price, StringComparison.Ordinal);
    }

    /// <summary>A show starts and there are seconds. But coming back to a card being bid on must not
    /// pull the keyboard off whatever was being read.</summary>
    [Fact]
    public void Opening_the_tab_puts_the_cursor_in_the_item_box_only_when_nothing_is_priced()
    {
        var open = Between(Js, "async function showWhatsNotSection()", "  function closeWhatsNotSection()");

        Assert.Contains("if ($('wn-card')?.classList.contains('hidden')) $('wn-item')?.focus();",
            open, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every other overlay here closes on Escape; this one did not, which left it reachable by
    /// keyboard and leaveable only by mouse. It is also the one screen you type into with a stream
    /// running, and closing it blanks the frame and stops a lot run — so the first Escape leaves the
    /// field and the second closes the tab.
    /// </summary>
    [Fact]
    public void Escape_leaves_the_field_first_and_closes_the_tab_second()
    {
        var bind = Between(Js, "function bindWhatsNot()", "  function bindShipping()");

        Assert.Contains("if (e.key !== 'Escape') return;", bind, StringComparison.Ordinal);
        // SELECT joined the two field tags later: Escape is how a dropdown is dismissed, and the
        // condition control is one. See WhatsNotSteadyAssetTests.
        Assert.Contains("/^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName)", bind, StringComparison.Ordinal);
        Assert.Contains("active.blur();", bind, StringComparison.Ordinal);
        Assert.Contains("closeWhatsNotSection();", bind, StringComparison.Ordinal);
        // Not while the screen is closed — every overlay's Escape handler is on the document.
        Assert.Contains("section.classList.contains('hidden')", bind, StringComparison.Ordinal);
    }

    [Fact]
    public void Scrolling_to_the_opened_card_honours_reduced_motion()
    {
        var open = Between(Js, "function wnOpenLot(index)", "  function wnClearLotList()");

        Assert.Contains("prefersReducedMotion() ? 'auto' : 'smooth'", open, StringComparison.Ordinal);
    }

    // ── Error and empty states ────────────────────────────────────────────────

    /// <summary>
    /// The server sends a headline and a sentence saying what to do about it. The card was reading
    /// only the first — "Nothing to price", with the half that says what to type dropped — because
    /// it was looking for a <c>message</c> field the app has never sent.
    /// </summary>
    [Fact]
    public void A_failure_shows_the_half_that_says_what_to_do()
    {
        var price = Between(Js, "async function wnPriceItem()", "  /// ── The one line ─");

        Assert.Contains("body.failure?.whatToDo", price, StringComparison.Ordinal);
        Assert.DoesNotContain("body.message", price, StringComparison.Ordinal);
        Assert.Contains(".wn-empty-do", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lot_list_reads_the_same_half_of_a_failure()
    {
        var run = Between(Js, "async function wnPriceLotList()", "  function wnLotStateLabel");

        Assert.Contains("body.failure?.whatToDo", run, StringComparison.Ordinal);
        Assert.DoesNotContain("body.message", run, StringComparison.Ordinal);
    }

    /// <summary>Every dead end on this screen reaches the live region, because a message painted
    /// only into the card is silent to somebody who pressed the button from the keyboard and never
    /// looked away from the stream.</summary>
    [Fact]
    public void Nothing_typed_and_nothing_priced_are_both_said_out_loud()
    {
        var price = Between(Js, "async function wnPriceItem()", "  /// ── The one line ─");

        Assert.Contains("wnSayLine(\"Nothing to price — type what's on screen.\");", price, StringComparison.Ordinal);
        // Six, up from four: the live-comps fallback added two more dead-end sentences — "asking
        // eBay live…" and the lookup's own refusal — both said out loud for the same reason as
        // the original four (see WhatsNotLiveCompsFallbackAssetTests).
        Assert.Equal(6, Count(price, "wnSayLine("));
    }

    /// <summary>
    /// Two different nothings. After Clear the rows are empty because the seller emptied them; after
    /// a paste that yielded no lots they are empty because the paste said nothing readable — and a
    /// blank area there reads as a button that did not fire.
    /// </summary>
    [Fact]
    public void An_empty_result_is_told_apart_from_an_empty_screen()
    {
        Assert.Contains("let wnLotsTried = false;", Js, StringComparison.Ordinal);

        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");
        Assert.Contains("host.innerHTML = wnLotsTried", rows, StringComparison.Ordinal);
        Assert.Contains("No lots came off those lines.", rows, StringComparison.Ordinal);

        var clear = Between(Js, "function wnClearLotList()", "  function bindWhatsNot()");
        Assert.Contains("wnLotsTried = false;", clear, StringComparison.Ordinal);
        // Clearing hands the keyboard back to the box that was cleared.
        Assert.Contains("$('wn-lots')?.focus();", clear, StringComparison.Ordinal);

        Assert.Contains(".wn-lots-empty {", Css, StringComparison.Ordinal);
    }

    // ── Responsive ────────────────────────────────────────────────────────────

    /// <summary>
    /// The way this screen is actually used is a narrow window down the side of a live stream. At
    /// that width the five-across ladder and stat tiles have to drop to two, and the ceiling has to
    /// go under the lot rather than be squeezed beside it.
    /// </summary>
    [Fact]
    public void The_card_and_the_rows_fold_at_a_window_width()
    {
        // Anchored on this screen's own comment: the stylesheet already has a 620px block for the
        // toast stack, and the first one in the file is not the one being asserted about. The inner
        // rules close on an indented brace, so an unindented one is the block's own.
        var narrow = Between(Css.Replace("\r\n", "\n"), "how this screen is actually used", "\n}");

        Assert.Contains("@media (max-width: 620px) {", narrow, StringComparison.Ordinal);

        foreach (var rule in new[] { ".wn-field-wide", ".wn-rung", ".wn-stat", ".wn-lot-row", ".wn-lot-max", ".wn-say" })
            Assert.Contains(rule, narrow, StringComparison.Ordinal);

        // The wider fold is still there — this one is additive to it.
        Assert.Contains("@media (max-width: 860px) {\n  .wn-field {", Css.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ── Nothing was taken off the screen ──────────────────────────────────────

    /// <summary>Sold comps and all four WhatsNot routes still registered, and the lot list still has
    /// no pricing path of its own. Polish is exactly the kind of change that quietly removes one.
    /// </summary>
    [Fact]
    public void Sold_comps_and_every_whatsnot_route_are_still_there()
    {
        foreach (var route in new[]
        {
            "app.MapGet(\"/api/sold-comps\"",
            "app.MapPost(\"/api/whatsnot/bid\"",
            "app.MapPost(\"/api/whatsnot/rebid\"",
            "app.MapPost(\"/api/whatsnot/lots\"",
            "app.MapGet(\"/api/whatsnot/embed-check\"",
        })
            Assert.Contains(route, Program, StringComparison.Ordinal);

        Assert.Contains("safePost('/api/whatsnot/bid', {", Js, StringComparison.Ordinal);
        Assert.Contains("safePost('/api/whatsnot/rebid', {", Js, StringComparison.Ordinal);
    }

    /// <summary>The stepper, the held-comps line and the instant re-price are untouched by this
    /// pass — they are the live half of the feature and this session only put a line above them.
    /// </summary>
    [Fact]
    public void The_instant_reprice_still_works_the_way_it_did()
    {
        Assert.Contains("function wnScheduleRebid()", Js, StringComparison.Ordinal);
        Assert.Contains("const WN_REBID_DEBOUNCE_MS = 90;", Js, StringComparison.Ordinal);
        Assert.Contains("if (seq !== wnRebidSeq) return;", Js, StringComparison.Ordinal);
        Assert.Contains("function wnBidStep(bid)", Js, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static int Count(string source, string needle)
    {
        var n = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
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
