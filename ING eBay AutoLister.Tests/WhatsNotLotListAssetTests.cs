namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The show's lot list: paste what is coming, price it before the hammer, and be there for the one
/// worth being there for. Most of that is HTML and JavaScript, and nothing in C# notices when a
/// binding is dropped.
///
/// Four of these are decisions rather than plumbing, and each is one "tidy-up" away from its
/// opposite: the list has <b>no pricing path of its own</b>, opening a priced row <b>reads no
/// eBay</b>, the rows <b>do not re-order while they are being read</b>, and the browser <b>holds no
/// opinion</b> about which lot is worth waiting for.
/// </summary>
public class WhatsNotLotListAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    // ── The screen exists and is wired ────────────────────────────────────────

    [Fact]
    public void The_lot_list_is_on_the_whatsnot_screen()
    {
        foreach (var id in new[] { "wn-queue", "wn-lots", "wn-lots-price", "wn-lots-clear", "wn-lots-note", "wn-lots-rows" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains("on('wn-lots-price', 'click', wnPriceLotList);", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-lots-clear', 'click', wnClearLotList);", Js, StringComparison.Ordinal);
    }

    /// <summary>It ships closed. During a live sale the card is the screen; the list is a thing you
    /// open once, before the show starts, and it must not push the card off the top.</summary>
    [Fact]
    public void The_list_starts_folded_away_above_the_feed_and_below_the_card()
    {
        var section = Between(Html, "<section id=\"whatsnot-section\"", "      </section>");
        Assert.Contains("<details id=\"wn-queue\"", section, StringComparison.Ordinal);
        Assert.DoesNotContain("<details id=\"wn-queue\" open", section, StringComparison.Ordinal);

        Assert.True(section.IndexOf("id=\"wn-card\"", StringComparison.Ordinal)
                  < section.IndexOf("id=\"wn-queue\"", StringComparison.Ordinal),
            "the card the seller is bidding on belongs above the list of what is coming");
    }

    [Fact]
    public void The_rows_have_styles_of_their_own()
    {
        foreach (var rule in new[] { ".wn-queue", ".wn-lots", ".wn-lot-rows", ".wn-lot-row", ".wn-lot-call", ".wn-lot-max" })
            Assert.Contains(rule + " {", Css, StringComparison.Ordinal);

        // The call colours the row, and the four calls the card can return are the four spelled here.
        foreach (var call in new[] { "bid", "risky", "stop", "no_data" })
            Assert.Contains($".wn-lot-call-{call} >", Css, StringComparison.Ordinal);
    }

    // ── One pricing path ──────────────────────────────────────────────────────

    /// <summary>
    /// Every lot on the list goes through the same <c>/api/whatsnot/bid</c> the typed item does.
    /// A bulk endpoint would be faster and would mean the app has two opinions about one item —
    /// the one on the row, and the one on the card that opens when you click it.
    /// </summary>
    [Fact]
    public void A_lot_on_the_list_is_priced_by_the_same_endpoint_as_a_typed_item()
    {
        var run = Between(Js, "async function wnPriceLotList()", "  function wnLotStateLabel");

        Assert.Contains("safePost('/api/whatsnot/bid', {", run, StringComparison.Ordinal);
        Assert.Contains("title: row.lot.title,", run, StringComparison.Ordinal);
        // Read, not priced: the list endpoint returns lines, never a ceiling.
        Assert.Contains("safePost('/api/whatsnot/lots', { text })", run, StringComparison.Ordinal);
    }

    /// <summary>
    /// The parse endpoint is a parse. If it ever grew a comp lookup there would be a second way to
    /// reach a ceiling in this app, and only one of the two would be the one the tests cover.
    /// </summary>
    [Fact]
    public void The_lot_list_endpoint_prices_nothing()
    {
        var route = Between(Program, "app.MapPost(\"/api/whatsnot/lots\"", "});");

        Assert.Contains("LiveLotList.Parse(req.Text)", route, StringComparison.Ordinal);
        foreach (var forbidden in new[] { "AnalyzeProductAsync", "LiveBidAdvisor", "advisor.Build", "board.Hold", "marketplace" })
            Assert.DoesNotContain(forbidden, route, StringComparison.Ordinal);
    }

    /// <summary>Sold comps, the typed card and the re-price are all still registered. The lot list
    /// is additive to that screen and takes nothing off it.</summary>
    [Fact]
    public void Nothing_the_screen_already_did_was_taken_away()
    {
        foreach (var route in new[]
                 {
                     "app.MapPost(\"/api/whatsnot/bid\"",
                     "app.MapPost(\"/api/whatsnot/rebid\"",
                     "app.MapGet(\"/api/whatsnot/embed-check\"",
                     "app.MapPost(\"/api/whatsnot/lots\"",
                     "app.MapPost(\"/api/snap\"",
                 })
            Assert.Contains(route, Program, StringComparison.Ordinal);

        Assert.Contains("on('wn-price', 'click', wnPriceItem);", Js, StringComparison.Ordinal);
        Assert.Contains("safePost('/api/whatsnot/rebid', {", Js, StringComparison.Ordinal);
    }

    // ── Opening a row ─────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the list: when the lot reaches the block its card is already in hand and
    /// its comps are still held, so opening it reads no eBay. A fresh price here would spend the
    /// second the seller does not have, to arrive at the answer already on the row.
    /// </summary>
    [Fact]
    public void Opening_a_priced_row_reads_no_ebay()
    {
        var open = Between(Js, "  function wnOpenLot(index)", "  function wnClearLotList");

        Assert.Contains("wnRenderCard(row.card);", open, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/whatsnot/bid", open, StringComparison.Ordinal);
        Assert.DoesNotContain("wnPriceItem", open, StringComparison.Ordinal);
        // The token comes with the row, so the steppers keep working off the held comps.
        Assert.Contains("wnToken = row.card.token || '';", open, StringComparison.Ordinal);
        Assert.Contains("wnScheduleRebid();", open, StringComparison.Ordinal);
    }

    /// <summary>
    /// Shipping, the premium and the target may have moved since the list was priced. Opening a row
    /// re-prices that one lot off its held comps rather than showing a card computed under settings
    /// that are no longer on screen — and if the comps have been let go, the card says so, which is
    /// the sentence any other stale token already gets.
    /// </summary>
    [Fact]
    public void An_opened_row_is_brought_up_to_the_settings_on_screen()
    {
        var open = Between(Js, "  function wnOpenLot(index)", "  function wnClearLotList");
        Assert.Contains("wnTokenItem = row.card.item;", open, StringComparison.Ordinal);
        Assert.Contains("wnRebidSeq++;", open, StringComparison.Ordinal);
        Assert.Contains("function wnHeldNote(text)", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// Changing shipping, the fee or the target must never re-price the whole list. Twelve fresh
    /// eBay reads on a keystroke is exactly the cost this screen exists to avoid.
    /// </summary>
    [Fact]
    public void Moving_a_setting_never_reprices_the_whole_list()
    {
        var bind = Between(Js, "  function bindWhatsNot()", "  function bindShipping");
        Assert.Contains("$(id)?.addEventListener('input', wnScheduleRebid);", bind, StringComparison.Ordinal);
        Assert.DoesNotContain("addEventListener('input', wnPriceLotList", bind, StringComparison.Ordinal);
    }

    // ── The order ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Which lot is worth waiting for is decided next to the ceiling it is made of, in C#. A sort
    /// key computed in the browser would be a second opinion about money that nothing tests.
    /// </summary>
    [Fact]
    public void The_browser_holds_no_opinion_about_which_lot_is_worth_waiting_for()
    {
        var run = Between(Js, "async function wnPriceLotList()", "  function wnLotStateLabel");

        Assert.Contains("b.card?.lotRank", run, StringComparison.Ordinal);
        // Nothing here recombines the parts of a rank, which is the only way to disagree with it.
        foreach (var forbidden in new[] { "profitAtMaxBid *", "maxBid *", "headroom *", "sellThroughRate *" })
            Assert.DoesNotContain(forbidden, run, StringComparison.Ordinal);
    }

    /// <summary>
    /// The list re-orders once, when every lot has an answer. Sorting as replies arrive would move
    /// rows out from under the pointer of somebody reading them — and the order means nothing until
    /// the last one is in anyway.
    /// </summary>
    [Fact]
    public void The_rows_do_not_move_while_they_are_being_read()
    {
        var run = Between(Js, "async function wnPriceLotList()", "  function wnLotStateLabel");

        var loop = run.IndexOf("for (let i = 0; i < wnLots.length; i++)", StringComparison.Ordinal);
        var sort = run.IndexOf("wnLots.sort(", StringComparison.Ordinal);
        Assert.True(loop > 0 && sort > loop, "the list must be sorted after the pricing loop, not inside it");
        Assert.Equal(sort, run.LastIndexOf("wnLots.sort(", StringComparison.Ordinal));
    }

    // ── Stopping ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A run in flight can be stopped, and closing the screen stops it. A dozen eBay reads for a
    /// screen nobody is on is the kind of cost that only shows up in somebody else's rate limit.
    /// </summary>
    [Fact]
    public void A_run_can_be_stopped_and_closing_the_screen_stops_it()
    {
        Assert.Contains("function wnStopLotRun()", Js, StringComparison.Ordinal);
        Assert.Contains("if (wnLotRunning) {", Js, StringComparison.Ordinal);

        var close = Between(Js, "  function closeWhatsNotSection()", "  // ── WhatsNot: the live-auction");
        Assert.Contains("wnStopLotRun();", close, StringComparison.Ordinal);
    }

    /// <summary>
    /// An answer from a run nobody is on must never paint a row. The loop checks the run number
    /// after every await, because each one is a place the seller can have pressed Stop.
    /// </summary>
    [Fact]
    public void An_answer_from_an_abandoned_run_paints_nothing()
    {
        var run = Between(Js, "async function wnPriceLotList()", "  function wnLotStateLabel");
        var checks = run.Split("if (run !== wnLotRun) return;").Length - 1;
        Assert.True(checks >= 5, $"expected a run check after every await, found {checks}");
    }

    // ── What the screen promises ──────────────────────────────────────────────

    /// <summary>
    /// The hint says what actually happens: the price on the line is where the bidding starts, not
    /// what the lot is worth, and opening a row is instant because the comps are already read. Both
    /// are claims the code above is held to.
    /// </summary>
    [Fact]
    public void The_hint_promises_only_what_the_code_does()
    {
        var section = Between(Html, "<details id=\"wn-queue\"", "          </details>");
        Assert.Contains("where the\n                bidding starts, not what the lot is worth", section.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("comps already in hand", section, StringComparison.Ordinal);
        Assert.Contains("one at a time", section, StringComparison.Ordinal);
    }

    /// <summary>The browser was given a new copy of both assets. A cached app.js is a feature that
    /// silently is not there.</summary>
    [Fact]
    public void The_browser_is_made_to_fetch_the_new_assets()
    {
        Assert.Contains("app.js?v=119", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=102", Html, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = source.IndexOf("\n" + to, start, StringComparison.Ordinal);
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
