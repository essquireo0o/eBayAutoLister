using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// "How many presses are left" reaches the live screen through six links, and three of them are
/// outside C#: the advisor reads the block off the ceiling it just computed, the warning reaches the
/// warning list, the spoken line picks up the two states worth hearing, the browser posts the typed
/// step, renders the strip and computes none of it — and the − / + buttons step by the same ladder
/// the count is made of.
/// </summary>
/// <remarks>
/// Two of these are decisions rather than plumbing, and each is the sort of thing a later tidy-up
/// undoes without reading why: a step the seller typed is held flat rather than talked upwards by
/// the assumed ladder, and the profit at the next bid is NOT clamped at zero the way the profit at
/// the ceiling is. The rest is the constraint every WhatsNot session has worked under — the
/// sold-comps path this screen stands on is untouched.
/// </remarks>
public class WhatsNotNextBidAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Increment = ReadSource("Services/LiveBidIncrement.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");
    private static readonly string Models = ReadSource("Models/LiveBidModels.cs");

    // ── The block reaches the card ───────────────────────────────────────────────────────────

    /// <summary>
    /// Read inside <c>Build</c>, off the ceiling and the break-even that method just computed, so a
    /// card re-priced against held comps re-runs the same reading. A block computed at the endpoint
    /// and carried on the quote would be a number the fresh card and the re-priced one could
    /// disagree about, on the screen where the bid moves every two seconds.
    /// </summary>
    [Fact]
    public void The_advisor_reads_the_press_off_its_own_break_even()
    {
        Assert.Contains("card.NextBid = LiveBidIncrement.Read(card, breakEvenAllIn, request.BidIncrement);",
            Advisor, StringComparison.Ordinal);

        // The endpoints hand it nothing — it is derived, not carried.
        Assert.DoesNotContain("LiveBidIncrement", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// On every card, never null. A block that only appears when it has bad news is a block whose
    /// silence means both "press away" and "nothing looked".
    /// </summary>
    [Fact]
    public void Every_card_carries_the_block()
    {
        Assert.Contains("public LiveNextBid NextBid { get; set; } = new();", Models, StringComparison.Ordinal);
        Assert.Contains("public decimal? BidIncrement { get; set; }", Models, StringComparison.Ordinal);
    }

    /// <summary>
    /// It costs the live path nothing. No clock, no state, no network — which is why it can be read
    /// on every re-price during a lot without the seconds the decision is made in going anywhere.
    /// </summary>
    [Fact]
    public void The_press_costs_no_lookup_and_no_clock()
    {
        foreach (var forbidden in new[] { "DateTime.UtcNow", "DateTime.Now", "await ", "HttpClient" })
            Assert.DoesNotContain(forbidden, Increment, StringComparison.Ordinal);

        // And the endpoint still reads eBay at most twice: the search, and the one widening.
        var bid = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "app.MapPost(\"/api/whatsnot/rebid\"");
        Assert.Equal(2, Occurrences(bid, "await AnalyzeProductAsync("));
    }

    // ── The two decisions ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A step the seller typed is held flat all the way up. They are watching the show and this app
    /// is not, and the count is not what anybody acts on — the next press is costed exactly either
    /// way, and the card is re-answered every time the bid moves.
    /// </summary>
    [Fact]
    public void A_stated_step_is_not_talked_upwards_by_the_ladder()
    {
        Assert.Contains("step = stated ? increment : Assumed(at);", Increment, StringComparison.Ordinal);
        Assert.Contains("stated: source == SourceSeller", Increment, StringComparison.Ordinal);
    }

    /// <summary>
    /// The profit at the next bid is deliberately NOT clamped at zero, unlike
    /// <c>ProfitAtMaxBid</c> directly above it in the advisor. A negative here is the figure's whole
    /// job: it says the press loses money, and $0.00 would say it makes none.
    /// </summary>
    [Fact]
    public void The_profit_at_the_next_bid_is_allowed_to_be_negative()
    {
        Assert.Contains("read.Profit = Math.Round(breakEvenAllIn - read.Landed, 2);",
            Increment, StringComparison.Ordinal);

        // The clamp exists one file over, on the figure that is meant to have it.
        Assert.Contains("Math.Max(0m, breakEvenAllIn - LandedCost(maxBid", Advisor, StringComparison.Ordinal);
    }

    /// <summary>
    /// Landed by the card's own arithmetic rather than by a second assembly of bid-plus-premium-
    /// plus-shipping. Two of those in one app is how the strip and the badge end up a dollar apart.
    /// </summary>
    [Fact]
    public void The_next_bid_is_landed_by_the_cards_own_function()
    {
        Assert.Contains(
            "LiveBidAdvisor.LandedCost(\n            read.Amount, card.BuyerFeePercent, card.ShippingCost, card.Tax.RatePercent)",
            Increment.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    // ── The warning, and the sentence ────────────────────────────────────────────────────────

    /// <summary>
    /// The one line on this card that contradicts the room figure printed next to it, so it is read
    /// before the warnings about the money and after the ones about what is being counted.
    /// </summary>
    [Fact]
    public void The_no_press_warning_sits_between_the_lot_warnings_and_the_money_ones()
    {
        var lots = Advisor.IndexOf("warnings.AddRange(LotWarnings(card));", StringComparison.Ordinal);
        var press = Advisor.IndexOf("card.NextBid is { Warning.Length: > 0 } press", StringComparison.Ordinal);
        // The shipping warning is now LiveShipShare's own sentence, so the strip and the warning
        // list cannot describe the same freight differently. Its place in the order is unchanged.
        var shipping = Advisor.IndexOf("card.Ship is { Warning.Length: > 0 } freight", StringComparison.Ordinal);

        Assert.True(lots > 0 && press > lots, "the press warning has to come after the lot warnings");
        Assert.True(shipping > press, "the press warning has to come before the ones about the money");
    }

    /// <summary>
    /// Exactly two states are spoken, and they are the two where hearing the sentence changes what
    /// the hand does. A count on every card would be a clause on every lot in exchange for
    /// information the room figure already carried.
    /// </summary>
    [Fact]
    public void The_spoken_line_speaks_only_on_the_last_press_and_on_no_press()
    {
        var clause = Section(Speech, "private static string HowManyPressesLeft", "private static string WhatItResellsFor");

        Assert.Contains("LiveNextBidVerdicts.Last =>", clause, StringComparison.Ordinal);
        Assert.Contains("LiveNextBidVerdicts.Stop =>", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveNextBidVerdicts.Press", clause, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveNextBidVerdicts.Over", clause, StringComparison.Ordinal);

        // And it is said straight after where the bidding stands, because it is the clause that can
        // contradict it.
        Assert.Contains("WhereTheBiddingIs(card), HowManyPressesLeft(card)", Speech, StringComparison.Ordinal);
    }

    // ── The browser ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The seller can read the show's own next-bid amount off the screen and this app cannot, so
    /// there is a box for it — and it reaches both the fresh price and the instant re-price.
    /// </summary>
    [Fact]
    public void The_typed_step_reaches_both_endpoints()
    {
        Assert.Contains("id=\"wn-inc\"", Html, StringComparison.Ordinal);
        Assert.Equal(3, Occurrences(Js, "bidIncrement: wnNumber('wn-inc')"));

        // Instant, off the held comps, like the other boxes that change no comps — now including
        // the show and its extra-item rate, which are the only two that can move a ceiling UP.
        Assert.Contains(
            "['wn-bid', 'wn-inc', 'wn-qty', 'wn-ship', 'wn-ship-add', 'wn-show', 'wn-fee', 'wn-tax', 'wn-target',",
            Js, StringComparison.Ordinal);
        // And remembered between lots, like the shipping, the fee and the target.
        Assert.Contains("inc: $('wn-inc')?.value ?? ''", Js, StringComparison.Ordinal);
        Assert.Contains("if (saved.inc != null) setVal('wn-inc', saved.inc);", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The strip is first under the badge — above the search, the trend, the units and the ladder —
    /// because it is the only block on the card about the thing the hand is about to do.
    /// </summary>
    [Fact]
    public void The_strip_is_rendered_first_under_the_badge()
    {
        // Scoped to the card's own template — ${ladder} is a name three other boards use too.
        var render = Section(Js, "<div class=\"wn-call wn-call-${esc(c.call)}\">", "<div class=\"wn-stats\">");
        var next = render.IndexOf("${nextStrip}", StringComparison.Ordinal);
        var search = render.IndexOf("${searchStrip}", StringComparison.Ordinal);
        var ladder = render.IndexOf("${ladder}", StringComparison.Ordinal);

        Assert.True(next > 0, "the next-bid strip is no longer rendered");
        Assert.True(next < search && search < ladder, "the strip has moved out from under the badge");
    }

    /// <summary>
    /// The browser computes none of it. Every word and every dollar on the strip is the server's,
    /// including the count — a press counted in JavaScript would be a second opinion about money
    /// that nothing tests, and the one it would disagree with is the badge.
    /// </summary>
    [Fact]
    public void The_browser_counts_nothing()
    {
        var render = Section(Js, "const n = c.nextBid || {};", "const ladder = priced ?");

        foreach (var arithmetic in new[] { "n.amount +", "n.amount -", "n.bidsLeft +", "n.bidsLeft -",
                                           "c.maxBid -", "/ n.increment" })
            Assert.DoesNotContain(arithmetic, render, StringComparison.Ordinal);

        // Every sentence on it arrives written.
        foreach (var field in new[] { "n.headline", "n.note", "n.incrementNote", "n.verdict" })
            Assert.Contains(field, render, StringComparison.Ordinal);
    }

    /// <summary>
    /// The − / + buttons step by the server's own increment when there is a card on screen, so
    /// pressing + lands on exactly the number the strip calls the next bid. A stepper with its own
    /// opinion is the app disagreeing with itself about the one figure this whole block is for.
    /// </summary>
    [Fact]
    public void The_stepper_prefers_the_number_the_card_just_printed()
    {
        Assert.Contains("const step = wnStepSize(current);", Js, StringComparison.Ordinal);

        var order = Section(Js, "function wnStepSize(current) {", "function wnStepBid(direction) {");
        var typed = order.IndexOf("wnNumber('wn-inc')", StringComparison.Ordinal);
        var server = order.IndexOf("wnLastIncrement", StringComparison.Ordinal);
        var ladder = order.IndexOf("wnBidStep(current)", StringComparison.Ordinal);

        Assert.True(typed >= 0 && typed < server && server < ladder,
            "typed, then the server's, then the ladder — in that order");

        // And the server's figure is dropped with the comps it came from, so it cannot follow the
        // seller onto a different lot.
        Assert.Contains("wnLastIncrement = 0;", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The two ladders are the same ladder. The count is made of the C# one and the + button moves
    /// by the JavaScript one, and a + that jumped to $50 under a card saying the next bid was $55
    /// would be the worst kind of wrong: quiet, and about the number the seller is acting on.
    /// </summary>
    [Fact]
    public void The_browsers_bid_ladder_is_the_servers_ladder()
    {
        var js = Section(Js, "function wnBidStep(bid) {", "let wnLastIncrement");
        var cs = Section(Increment, "public static decimal Assumed(decimal bid)", "public static (decimal Increment, string Source) Sanitize");

        var jsRungs = Regex.Matches(js, @"if \(at < ([\d.]+)\) return ([\d.]+);")
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value)).ToList();
        var csRungs = Regex.Matches(cs, @"< ([\d_.]+)m => ([\d_.]+)m,")
            .Select(m => (m.Groups[1].Value.Replace("_", ""), m.Groups[2].Value.Replace("_", ""))).ToList();

        Assert.NotEmpty(jsRungs);
        Assert.Equal(csRungs, jsRungs);

        // Including the top rung, which neither writes as a comparison.
        Assert.Equal(
            Regex.Match(cs, @"_ => ([\d_.]+)m,").Groups[1].Value.Replace("_", ""),
            Regex.Match(js, @"return ([\d.]+);\s*\}").Groups[1].Value);
    }

    /// <summary>
    /// Coloured by what to do rather than by how the lot scored, and every verdict the server can
    /// return has a rule — a state with no style is a strip that silently renders as "no opinion".
    /// </summary>
    [Fact]
    public void Every_verdict_has_somewhere_to_land_in_the_stylesheet()
    {
        foreach (var verdict in new[] { "press", "last", "stop", "over" })
            Assert.Contains($".wn-next-{verdict}", Css, StringComparison.Ordinal);

        AssetStamp.AtLeast(Html, "style.css?v=", 128);
        AssetStamp.AtLeast(Html, "app.js?v=", 145);
    }

    /// <summary>
    /// The strip folds at the narrow width the same way the search and trend strips do — this screen
    /// is used as a column down the side of a live stream.
    /// </summary>
    [Fact]
    public void The_strip_folds_when_the_window_is_a_column()
    {
        var narrow = Css[Css.IndexOf("@media (max-width: 620px)", StringComparison.Ordinal)..];
        Assert.Contains(".wn-next-line {", narrow, StringComparison.Ordinal);
        Assert.Contains(".wn-field-inc,", Css, StringComparison.Ordinal);
    }

    // ── The constraint every session works under ─────────────────────────────────────────────

    [Fact]
    public void Sold_comps_are_untouched()
    {
        foreach (var route in new[]
        {
            "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
            "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
            "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
        })
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);

        Assert.Contains("await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static int Occurrences(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string Section(string text, string from, string to)
    {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not find \"{from}\"");
        var end = text.IndexOf(to, start, StringComparison.Ordinal);
        Assert.True(end > start, $"could not find \"{to}\" after \"{from}\"");
        return text[start..end];
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
