namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The live half of the WhatsNot card: the bid moves, and the answer has to move with it inside the
/// couple of seconds a live lot allows. That is HTML, CSS and JavaScript, and nothing in C# notices
/// when a binding is dropped or a stale answer is allowed to paint over a newer one.
///
/// Three of these are decisions rather than plumbing, and each is easy to "tidy" into its opposite:
/// a re-price never reads eBay <b>and never hides that</b>, a held quote is thrown away the moment
/// the item changes, and an answer that arrives late is discarded rather than displayed.
/// </summary>
public class WhatsNotLiveBidAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    // ── The bid, with hands on it ─────────────────────────────────────────────

    /// <summary>
    /// The bid box is the one field that moves during a lot, so it has buttons and arrow keys. A
    /// seller watching a stream is not looking at the keyboard.
    /// </summary>
    [Fact]
    public void The_bid_can_be_stepped_by_button_and_by_arrow_key()
    {
        foreach (var id in new[] { "wn-bid-up", "wn-bid-down" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains("on('wn-bid-up', 'click', () => wnStepBid(1));", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-bid-down', 'click', () => wnStepBid(-1));", Js, StringComparison.Ordinal);
        Assert.Contains("e.key === 'ArrowUp'", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The step is worth what the bidding is worth at that level. A fixed $1 is useless at $600 and
    /// a fixed $25 is wrong at $12 — but this is a convenience on an input box, and it must stay
    /// that: nothing in the stepper may decide what anything is worth.
    /// </summary>
    [Fact]
    public void The_step_scales_with_the_bid_and_never_goes_below_zero()
    {
        Assert.Contains("function wnBidStep(bid)", Js, StringComparison.Ordinal);
        Assert.Contains("Math.max(0, Math.ceil((current - step) / step) * step)", Js, StringComparison.Ordinal);
    }

    // ── The re-price ──────────────────────────────────────────────────────────

    /// <summary>
    /// Moving the bid, the shipping, the premium or the target re-answers off comps already in hand
    /// — no eBay call. The four boxes that change the ceiling without changing the sold history are
    /// exactly the four wired to it; the item box is deliberately not one of them.
    /// </summary>
    [Fact]
    public void Moving_the_bid_reprices_without_reading_ebay_again()
    {
        Assert.Contains("safePost('/api/whatsnot/rebid', {", Js, StringComparison.Ordinal);

        // The boxes are named individually rather than pinned as a list: the list grew when the
        // quantity box arrived, and an equality here is a test that fails on every addition and
        // gets "fixed" by deletion. What matters is that each of them re-answers off held comps.
        var start = Js.IndexOf("['wn-bid',", StringComparison.Ordinal);
        Assert.True(start >= 0, "the boxes that re-price off held comps are no longer a list");
        var boxes = Js[start..Js.IndexOf("].forEach", start, StringComparison.Ordinal)];

        foreach (var id in new[] { "wn-bid", "wn-qty", "wn-ship", "wn-fee", "wn-target" })
            Assert.Contains($"'{id}'", boxes, StringComparison.Ordinal);

        Assert.Contains("$(id)?.addEventListener('input', wnScheduleRebid);", Js, StringComparison.Ordinal);
        Assert.Contains("token: wnToken,", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// A held quote is an answer about one item. The moment the typed item stops matching the one it
    /// was issued for, the token goes — otherwise a re-price would put one lot's comps under another
    /// lot's name, which is the most expensive mistake this screen could make.
    /// </summary>
    [Fact]
    public void Changing_the_item_throws_the_held_comps_away()
    {
        Assert.Contains("function wnDropToken()", Js, StringComparison.Ordinal);
        // The handler grew a second statement — a stale quantity is dropped with the stale token,
        // for the same reason — so this pins the condition and the drop rather than one line.
        Assert.Contains("!== wnTokenItem) {", Js, StringComparison.Ordinal);
        var onTyping = Section(Js, "$('wn-item')?.addEventListener('input', () => {", "});");
        Assert.Contains("wnDropToken();", onTyping, StringComparison.Ordinal);
        // And a fresh price supersedes anything held, including an answer still in flight.
        Assert.Contains("wnSaveSettings();\n    // A fresh read supersedes anything held", Js.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The bid moves faster than a round trip completes, so answers can come back out of order. An
    /// older one painted over a newer one would put a number on screen that is not the number for
    /// the bid in the box — the single failure this feature cannot afford.
    /// </summary>
    [Fact]
    public void A_late_answer_about_an_earlier_bid_is_dropped()
    {
        Assert.Contains("const seq = ++wnRebidSeq;", Js, StringComparison.Ordinal);
        Assert.Contains("if (seq !== wnRebidSeq) return;", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// When the server has let the comps go, the screen says so and stops re-pricing against a token
    /// that no longer resolves. It does not silently keep the old numbers looking live.
    /// </summary>
    [Fact]
    public void Losing_the_held_comps_is_said_rather_than_papered_over()
    {
        Assert.Contains("wnHeldNote(", Js, StringComparison.Ordinal);
        Assert.Contains("wn-held-stale", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-held-stale {", Css, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every card says where its resale price came from and how old it is. A re-priced card is the
    /// same computation as a fresh one — what it is not is a fresh READ, and the difference belongs
    /// on screen rather than in a comment.
    /// </summary>
    [Fact]
    public void Every_card_says_how_old_the_comps_behind_it_are()
    {
        Assert.Contains("Bid moved without re-reading eBay", Js, StringComparison.Ordinal);
        Assert.Contains("c.compsAgeSeconds", Js, StringComparison.Ordinal);
        Assert.Contains("Comps read just now and held", Js, StringComparison.Ordinal);
        // And the ask row promises exactly that, so the behaviour is not a surprise.
        Assert.Contains("Changing the item reads eBay again.", Html, StringComparison.Ordinal);
    }

    // ── The meter ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The glanceable half: one track from nothing to the walk-away line, the ceiling marked on it,
    /// and the bid somewhere along it. Every boundary it draws is a number the server computed —
    /// the browser divides them to get a percentage of a bar and does no other arithmetic, because a
    /// second opinion about the ceiling is the thing this whole feature is built to avoid.
    /// </summary>
    [Fact]
    public void The_meter_draws_the_servers_numbers_and_computes_none_of_its_own()
    {
        Assert.Contains("class=\"wn-meter\"", Js, StringComparison.Ordinal);
        Assert.Contains("const pct = v => Math.max(0, Math.min(100, (v / c.breakEvenBid) * 100));", Js, StringComparison.Ordinal);
        Assert.Contains("const ceiling = pct(c.maxBid);", Js, StringComparison.Ordinal);

        foreach (var rule in new[] { ".wn-meter-track {", ".wn-meter-good {", ".wn-meter-edge {", ".wn-meter-bid {", ".wn-meter-bid-past {" })
            Assert.Contains(rule, Css, StringComparison.Ordinal);

        // No target return, no fee percentage, no break-even arithmetic in the browser.
        Assert.DoesNotContain("targetRoiPercent / 100", Js, StringComparison.Ordinal);
        Assert.DoesNotContain("buyerFeePercent / 100", Js, StringComparison.Ordinal);
    }

    /// <summary>The bid marker changes colour past the ceiling, and the meter carries a text label
    /// for anyone who cannot read a colour.</summary>
    [Fact]
    public void The_meter_says_in_words_what_it_shows_in_colour()
    {
        Assert.Contains("role=\"img\" aria-label=", Js, StringComparison.Ordinal);
        Assert.Contains("wn-meter-bid-past", Js, StringComparison.Ordinal);
        Assert.Contains("loses money past", Js, StringComparison.Ordinal);
    }

    // ── The endpoint ──────────────────────────────────────────────────────────

    /// <summary>
    /// The re-price runs the same advisor over the held analysis. If it ever grew a price
    /// calculation of its own, the fast answer and the fresh answer would be two different opinions
    /// about one item — and the fast one is the one the seller acts on.
    /// </summary>
    [Fact]
    public void The_reprice_endpoint_recomputes_rather_than_approximating()
    {
        var route = Between(Program, "app.MapPost(\"/api/whatsnot/rebid\"", "});");

        Assert.Contains("advisor.Build(quote.Item, quote.Analysis", route, StringComparison.Ordinal);
        Assert.Contains("RepricedFromHeldComps = true", route, StringComparison.Ordinal);
        Assert.Contains("quote.AgeSeconds(now)", route, StringComparison.Ordinal);

        // No comp lookup, and no second market read of any kind — that is the whole point.
        Assert.DoesNotContain("AnalyzeProductAsync", route, StringComparison.Ordinal);
        Assert.DoesNotContain("marketplace", route, StringComparison.Ordinal);
    }

    /// <summary>
    /// No held comps means no answer. A re-price that quietly priced against nothing would be the
    /// one number on this screen with no sold history under it.
    /// </summary>
    [Fact]
    public void An_unheld_token_is_refused_rather_than_priced()
    {
        var route = Between(Program, "app.MapPost(\"/api/whatsnot/rebid\"", "});");

        Assert.Contains("if (quote is null)", route, StringComparison.Ordinal);
        Assert.Contains("Press Price it to read eBay again.", route, StringComparison.Ordinal);
        // And a token cannot be redirected onto a different item.
        Assert.Contains("That's a different item", route, StringComparison.Ordinal);
    }

    /// <summary>The fresh price is what holds the comps, and it hands back the token that re-prices
    /// them. Without this the live half never starts.</summary>
    [Fact]
    public void The_fresh_price_holds_its_comps_and_returns_the_token()
    {
        var route = Between(Program, "app.MapPost(\"/api/whatsnot/bid\"", "});");

        // The argument list is pinned as a prefix rather than whole: what this test exists to catch
        // is the hold disappearing or being pointed at something other than the item just priced,
        // and later sessions legitimately hand it more to keep (the seller's own record, most
        // recently). An equality here fails on every addition and gets "fixed" by deletion.
        Assert.Contains("board.Hold(title, analysis, category", route, StringComparison.Ordinal);
        Assert.Contains("card.Token = quote.Token;", route, StringComparison.Ordinal);
        Assert.Contains("builder.Services.AddSingleton<LiveBidBoard>();", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sold-comps path this is built on top of stays exactly where it was. Every one of these
    /// routes is somebody's screen, and the live card is additive to all of them.
    /// </summary>
    [Fact]
    public void The_sold_comps_paths_are_left_alone()
    {
        foreach (var route in new[]
                 {
                     "app.MapPost(\"/api/snap\"",
                     "app.MapPost(\"/api/whatsnot/bid\"",
                     "app.MapGet(\"/api/whatsnot/embed-check\"",
                 })
        {
            Assert.Contains(route, Program, StringComparison.Ordinal);
        }

        // The re-price reads a held object and writes nothing anywhere.
        var reprice = Between(Program, "app.MapPost(\"/api/whatsnot/rebid\"", "});");
        foreach (var mutation in new[] { "Save", "Insert", "Update", "Delete", "ExecuteNonQuery" })
            Assert.DoesNotContain(mutation, reprice, StringComparison.Ordinal);
    }

    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>A slice of an asset between two literals, both on the same terms — unlike
    /// <see cref="Between"/>, which anchors its end to the start of a line.</summary>
    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find \"{to}\" after \"{from}\"");
        return text[start..end];
    }

    /// <summary>The text of one route, from its map call to the first line that closes a lambda at
    /// column zero. Cheap, and enough to tell "inside this endpoint" from "somewhere in a 3,000-line
    /// file".</summary>
    private static string Between(string source, string from, string to)
    {
        var start = source.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\" in Program.cs");
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
