namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot arbitrage card, held to holding still.
///
/// It is replaced whole every time the bid moves — every two or three seconds during a live sale —
/// and everything pinned here is about what survives that. Keyboard focus goes back on the control
/// the seller's hand was on rather than to <c>&lt;body&gt;</c>, an opened comp table stays open, the
/// hammer button is looked up again rather than remembered across a request, Escape from the
/// condition dropdown means "never mind" rather than "close the tab", and the card still says which
/// band, which cell and how far along the meter when the operating system takes the colour away.
///
/// Sold comps are asserted here too, as in every WhatsNot session: a polish pass is exactly the kind
/// of change that quietly takes an endpoint off a screen.
/// </summary>
public class WhatsNotSteadyAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    // ── The redraw keeps what the seller put there ────────────────────────────

    /// <summary>
    /// Taken before the innerHTML assignment and put back after it. Either half on its own is
    /// nothing: state read after the redraw is state read off the new nodes, and state restored
    /// before it is state thrown away one line later.
    /// </summary>
    [Fact]
    public void The_card_reads_its_state_before_the_redraw_and_puts_it_back_after()
    {
        var render = Between(Js, "function wnRenderCard(c) {", "  // ── WhatsNot: the show's buy sheet");

        var taken = render.IndexOf("const keep = wnCardKeepState(card);", StringComparison.Ordinal);
        var wiped = render.IndexOf("card.innerHTML = `", StringComparison.Ordinal);
        var back = render.IndexOf("wnRestoreCardKeepState(card, keep);", StringComparison.Ordinal);

        Assert.True(taken >= 0, "the card never reads what the seller had under their hand");
        Assert.True(wiped > taken, "the state is read after the redraw has already destroyed it");
        Assert.True(back > wiped, "the state is put back before the redraw, which is no restore at all");
    }

    /// <summary>
    /// Keyed off <c>data-keep</c>, never off a position. Blocks appear and disappear between
    /// redraws — a gate strip arrives, a units strip stops applying — so an index would move the
    /// keyboard onto a different control at exactly the moment nobody is watching it.
    /// </summary>
    [Fact]
    public void Focus_and_the_open_tables_are_keyed_by_name_and_not_by_position()
    {
        var state = Between(Js, "function wnCardKeepState(card) {", "  function wnRenderCard(");

        Assert.Contains("details[data-keep][open]", state, StringComparison.Ordinal);
        Assert.Contains("active.closest('[data-keep]')", state, StringComparison.Ordinal);

        foreach (var byPosition in new[] { "children[", "nodeIndex", "indexOf(active)" })
            Assert.DoesNotContain(byPosition, state, StringComparison.Ordinal);
    }

    /// <summary>Restoring focus onto a <c>&lt;details&gt;</c> means its summary — the details
    /// element itself is not the thing that takes a tab stop.</summary>
    [Fact]
    public void A_restored_disclosure_hands_the_keyboard_to_its_summary()
    {
        var restore = Between(Js, "function wnRestoreCardKeepState(card, was) {", "  function wnRenderCard(");

        Assert.Contains("if (d) d.open = true;", restore, StringComparison.Ordinal);
        Assert.Contains("back.tagName === 'DETAILS' ? back.querySelector('summary') : back", restore,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every control on the card that can hold the keyboard carries a key. The hammer is the one
    /// that costs money to lose: a seller tabbed to it and waiting for the auctioneer loses it to
    /// <c>&lt;body&gt;</c> on the next re-price, and the Enter they press when the lot sells
    /// records nothing.
    /// </summary>
    [Fact]
    public void Everything_on_the_card_that_can_hold_the_keyboard_is_keyed()
    {
        Assert.Contains("data-won=\"1\" data-keep=\"won\"", Js, StringComparison.Ordinal);
        Assert.Contains("class=\"wn-search-undo\" data-keep=\"exact\"", Js, StringComparison.Ordinal);
        Assert.Contains("<details class=\"wn-comps\" data-keep=\"comps\">", Js, StringComparison.Ordinal);
        Assert.Contains("<details class=\"wn-own-sales\" data-keep=\"own-sales\">", Js, StringComparison.Ordinal);
        Assert.Contains("data-keep=\"sold-link\"", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The lot list has done this since it was written, and the card is now held to the same bar.
    /// Pinned together so neither can be dropped as an oddity of one function.
    /// </summary>
    [Fact]
    public void The_lot_list_still_puts_the_keyboard_back_after_its_own_redraw()
    {
        var rows = Between(Js, "function wnRenderLotRows()", "  /// The lot reached the block.");

        Assert.Contains("host.contains(document.activeElement)", rows, StringComparison.Ordinal);
        Assert.Contains("host.querySelector(`[data-lot=\"${moved}\"]`)?.focus();", rows, StringComparison.Ordinal);
    }

    /// <summary>
    /// A win does not re-render the card, but a re-price landing while the win is in flight does —
    /// and then the remembered button is a node no longer on the page. Re-enabling it does nothing
    /// and focusing it drops the keyboard to <c>&lt;body&gt;</c>, which is the exact failure the
    /// block was written to prevent.
    /// </summary>
    [Fact]
    public void The_hammer_button_is_looked_up_again_rather_than_remembered_across_the_request()
    {
        var win = Between(Js, "async function wnRecordWin() {", "  async function wnRemoveWin(");

        Assert.Contains("const live = $('wn-card')?.querySelector('[data-won]');", win, StringComparison.Ordinal);
        Assert.Contains("if (hadFocus) live.focus();", win, StringComparison.Ordinal);
        // The stale handle is never the thing put back on screen.
        Assert.DoesNotContain("if (hadFocus) btn.focus();", win, StringComparison.Ordinal);
    }

    // ── Escape ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Escape is how a dropdown is dismissed. Without SELECT in the field test, a seller who opened
    /// Condition and changed their mind closed the whole tab with it — blanking the frame and
    /// stopping a lot run, on the key that meant "never mind".
    /// </summary>
    [Fact]
    public void Escape_out_of_the_condition_dropdown_leaves_the_field_and_not_the_tab()
    {
        Assert.Contains("/^(INPUT|TEXTAREA|SELECT)$/.test(active.tagName)", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("/^(INPUT|TEXTAREA)$/.test(active.tagName)", Js, StringComparison.Ordinal);

        // Still a two-step: the second Escape, from outside a field, still closes the tab.
        var esc = Between(Js, "if (e.key !== 'Escape') return;", "    wnLoadSettings();");
        Assert.Contains("active.blur();", esc, StringComparison.Ordinal);
        Assert.Contains("closeWhatsNotSection();", esc, StringComparison.Ordinal);
    }

    /// <summary>The condition control really is a select — the test above is about this element and
    /// stops meaning anything if it becomes a set of buttons.</summary>
    [Fact]
    public void The_condition_control_is_a_select()
    {
        Assert.Contains("<select id=\"wn-cond\"", Html, StringComparison.Ordinal);
    }

    // ── The screen with the colour taken away ─────────────────────────────────

    /// <summary>
    /// Forced colours override every background and border colour the app chose. On this screen
    /// that costs the answer, not decoration: the fill along the meter, the length of the odds bar
    /// and which band this lot is in are all read at a glance and all carried by colour alone.
    /// </summary>
    [Fact]
    public void The_meters_keep_their_colour_and_their_track_gets_an_outline()
    {
        var hc = MediaBlock("@media (forced-colors: active) {", ".wn-meter-track");

        // Where the colour IS the datum, the author's colours are kept.
        foreach (var fill in new[]
        {
            ".wn-meter-good", ".wn-meter-edge", ".wn-meter-bid",
            ".wn-budget-bar-spent", ".wn-odds-bar-covered",
        })
            Assert.Contains(fill, hc, StringComparison.Ordinal);
        Assert.Contains("forced-color-adjust: none;",
            Between(hc, ".wn-odds-bar-covered {", "}"), StringComparison.Ordinal);

        // The outline goes on the tracks. A border on a fill inside an overflow:hidden track
        // changes its width, and the width is the number.
        Assert.Contains("border: 1px solid CanvasText;",
            Between(hc, ".wn-odds-bar {", "}"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The emphasis colours say "this is the band this lot is in", "this is the money already
    /// committed", "this is the cell the ceiling was cut by". Forced colours flatten all of them,
    /// so the emphasis moves to a border width — the one thing the OS does not override.
    /// </summary>
    [Fact]
    public void The_one_that_matters_is_marked_by_a_width_rather_than_a_colour()
    {
        var hc = MediaBlock("@media (forced-colors: active) {", ".wn-meter-track");
        var emphasis = Between(hc, ".wn-cond-band-mine,", "}");

        foreach (var mine in new[]
        {
            ".wn-stock-bar-shelf", ".wn-stock-bar-tonight", ".wn-hold-cell-this",
            ".wn-ship-cell-this", ".wn-tax-cell-this", ".wn-budget-cell-this", ".wn-odds-cell-this",
        })
            Assert.Contains(mine, emphasis, StringComparison.Ordinal);

        Assert.Contains("border-width: 2px;", emphasis, StringComparison.Ordinal);
    }

    /// <summary>
    /// The call needs no rescuing and deliberately gets none — BID UP TO $90, DON'T BID, NO DATA
    /// and CAN'T LIST IT are words, on the badge, on every lot row and in the one line above the
    /// card. What the badge gets is an edge, so it still reads as an object rather than as another
    /// line of text.
    /// </summary>
    [Fact]
    public void The_call_is_words_and_the_badge_keeps_an_edge_of_its_own()
    {
        Assert.Contains("CantListLabel = \"CAN'T LIST IT\"",
            ReadSource(Path.Combine("Services", "LiveBidAdvisor.cs")), StringComparison.Ordinal);

        var hc = MediaBlock("@media (forced-colors: active) {", ".wn-meter-track");
        var edges = Between(hc, ".wn-call-badge,", "}");
        foreach (var edged in new[] { ".wn-lot-call", ".wn-status", ".wn-gate", ".wn-say" })
            Assert.Contains(edged, edges, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid CanvasText;", edges, StringComparison.Ordinal);

        // The one line's stripe is a coloured left border — the part that does not survive — so it
        // is widened rather than left as an invisible three pixels.
        Assert.Contains("border-left-width: 4px;", hc, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both animated widths on this screen animate because a number moved, which during a live sale
    /// is a bar sliding every few seconds in the corner of the eye of somebody who asked the system
    /// not to do that.
    /// </summary>
    [Fact]
    public void The_two_bars_that_slide_stop_sliding_when_motion_is_refused()
    {
        var reduced = MediaBlock("@media (prefers-reduced-motion: reduce) {", ".wn-odds-bar-covered");

        Assert.Contains(".wn-budget-bar-spent", reduced, StringComparison.Ordinal);
        Assert.Contains("transition: none;", reduced, StringComparison.Ordinal);

        // And they are still animated for everybody else — a reduced-motion rule that matched an
        // animation nobody had would be a rule about nothing.
        Assert.Equal(2, Count(Css, "transition: width .18s ease-out;"));
    }

    // ── Nothing else moved ────────────────────────────────────────────────────

    /// <summary>Sold comps stay fully working. WhatsNot has been additive in every session and a
    /// polish pass is exactly the kind of change that quietly drops an endpoint.</summary>
    [Fact]
    public void Sold_comps_and_every_whatsnot_endpoint_are_still_there()
    {
        foreach (var route in new[]
        {
            "app.MapGet(\"/api/sold-comps\"",
            "app.MapPost(\"/api/whatsnot/bid\"",
            "app.MapPost(\"/api/whatsnot/rebid\"",
            "app.MapPost(\"/api/whatsnot/won\"",
            "app.MapGet(\"/api/whatsnot/sheet\"",
            "app.MapPost(\"/api/whatsnot/list\"",
            "app.MapPost(\"/api/whatsnot/lots\"",
            "app.MapGet(\"/api/whatsnot/embed-check\"",
            "app.MapPost(\"/api/whatsnot/read\"",
            "app.MapPost(\"/api/whatsnot/photo\"",
        })
            Assert.Contains(route, Program, StringComparison.Ordinal);
    }

    /// <summary>The one line above the card is still the screen's only live region, and the card is
    /// still not one. The restore added here moves the keyboard, which announces on its own — a
    /// second live region underneath it would talk over the sentence that decides the bid.</summary>
    [Fact]
    public void There_is_still_exactly_one_live_region_on_the_screen()
    {
        var section = Between(Html, "<section id=\"whatsnot-section\"", "<section id=\"shipping-section\"");

        Assert.Equal(3, Count(section, "aria-live="));   // the one line, the feed status, the lot run
        Assert.Contains("<p id=\"wn-say\" class=\"wn-say hidden\" role=\"status\" aria-live=\"polite\" aria-atomic=\"true\">",
            section, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-live", Between(section, "<div id=\"wn-card\"", ">"), StringComparison.Ordinal);
    }

    /// <summary>The browser has to fetch both changed assets or the fix ships to nobody — the
    /// wwwroot files are embedded resources served with a cache-busting version.</summary>
    [Fact]
    public void The_changed_assets_are_versioned()
    {
        Assert.Contains("app.js?v=140", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=123", Html, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A whole <c>@media</c> block, taken by matching braces. The stylesheet holds several blocks
    /// under each of these headers — one per screen — so the one wanted is named by a selector only
    /// it contains rather than by being the first.
    /// </summary>
    private static string MediaBlock(string header, string mustContain)
    {
        for (var at = 0; ;)
        {
            var start = Css.IndexOf(header, at, StringComparison.Ordinal);
            Assert.True(start >= 0, $"no \"{header}\" block mentioning {mustContain}");
            var body = BalancedBlock(Css, start + header.Length - 1);
            if (body.Contains(mustContain, StringComparison.Ordinal)) return body;
            at = start + header.Length;
        }
    }

    private static string BalancedBlock(string css, int openBraceAt)
    {
        var depth = 0;
        for (var i = openBraceAt; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return css[openBraceAt..(i + 1)];
        }

        Assert.Fail("the block never closed");
        return "";
    }

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
