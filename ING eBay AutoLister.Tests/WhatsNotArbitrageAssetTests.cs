namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The WhatsNot card is HTML, CSS and JavaScript, and nothing in C# renders it — so nothing in C#
/// notices when the ask box stops posting, the card loses its verdict classes, or the screen stops
/// loading because a binding was dropped. These lock the wiring that
/// <see cref="ING_eBay_AutoLister.Services.LiveBidAdvisor"/> is useless without.
///
/// Three of them are decisions rather than plumbing, and all three are easy to "tidy" into their
/// opposite by somebody who has not read why: the card is fed by the app's own sold-comps pipeline
/// rather than a new lookup, the screen tells the truth about what an iframe can and cannot read,
/// and the sold-comps path this is built on top of is left completely alone.
/// </summary>
public class WhatsNotArbitrageAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    [Fact]
    public void The_ask_box_posts_the_bid_the_shipping_the_premium_and_the_target()
    {
        // Every field the ceiling depends on has to reach the server, or the number on screen is a
        // ceiling for a different auction.
        foreach (var id in new[] { "wn-item", "wn-bid", "wn-ship", "wn-fee", "wn-target", "wn-price", "wn-card" })
            Assert.Contains($"id=\"{id}\"", Html, StringComparison.Ordinal);

        Assert.Contains("safePost('/api/whatsnot/bid', {", Js, StringComparison.Ordinal);
        foreach (var field in new[] { "currentBid:", "shippingCost:", "buyerFeePercent:", "targetRoiPercent:" })
            Assert.Contains(field, Js, StringComparison.Ordinal);

        // Bound at start-up, or the button does nothing.
        Assert.Contains("bindWhatsNot();", Js, StringComparison.Ordinal);
        Assert.Contains("on('wn-price', 'click', wnPriceItem);", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bid moves while the card is on screen. Re-pricing has to be one keystroke from whichever
    /// box the seller is already in, or the card is stale by the time it is read.
    /// </summary>
    [Fact]
    public void Enter_from_any_of_the_boxes_reprices()
    {
        Assert.Contains("['wn-item', 'wn-bid', 'wn-ship', 'wn-fee', 'wn-target'].forEach", Js, StringComparison.Ordinal);
        Assert.Contains("if (e.key === 'Enter') wnPriceItem();", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty premium box means "not stated", which is a different answer from zero: the card
    /// warns about an unstated premium and cannot warn about one the browser silently turned into a
    /// 0 the server then believed.
    /// </summary>
    [Fact]
    public void An_empty_number_box_is_sent_as_unstated_rather_than_as_zero()
    {
        Assert.Contains("function wnNumber(id) {", Js, StringComparison.Ordinal);
        Assert.Contains("if (!raw) return null;", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_four_verdicts_the_advisor_can_return_all_have_a_colour()
    {
        // The badge carries the whole answer at a glance. A call with no rule renders as unstyled
        // text next to three that shout, which reads as "nothing to see here".
        foreach (var call in new[] { "wn-call-bid", "wn-call-risky", "wn-call-stop", "wn-call-no_data" })
            Assert.Contains(call, Css, StringComparison.Ordinal);

        Assert.Contains("wn-call wn-call-${esc(c.call)}", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The five numbers that make this arbitrage rather than a price lookup: where the bidding is,
    /// where to stop, where it stops making money, what is left between them, and what it resells
    /// for. Drop any one and the card stops answering the question it exists for.
    /// </summary>
    [Fact]
    public void The_ladder_shows_the_bid_the_ceiling_the_break_even_the_room_and_the_resale()
    {
        foreach (var label in new[] { "Bid now", "Stop at", "Break-even", "Room left", "Resells for" })
            Assert.Contains($">{label}</span>", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void The_decision_statistics_are_all_on_the_card()
    {
        // Sell-through, the spread, the velocity and the evidence — the four things that decide
        // whether the ceiling above them is worth acting on.
        foreach (var stat in new[] { "'Middle half of sales'", "'Sell-through'", "'Sells in'", "'Evidence'", "'eBay resale'" })
            Assert.Contains(stat, Js, StringComparison.Ordinal);

        Assert.Contains("c.freshnessNote", Js, StringComparison.Ordinal);
        Assert.Contains("c.evidenceNote", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// A sell-through rate with no active listings behind it has no denominator. Rendering it as
    /// 100% is how thin data ends up wearing the same badge as a measured market.
    /// </summary>
    [Fact]
    public void A_rate_with_no_denominator_renders_as_a_dash()
    {
        Assert.Contains("c.sellThroughUnbounded ? '—'", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The screen must not claim to read the feed. An iframe cannot read a cross-origin page, and a
    /// panel that implied otherwise would be the app's most expensive lie — the seller would think
    /// the numbers were about the item on screen rather than about what they typed.
    /// </summary>
    [Fact]
    public void The_screen_says_what_the_frame_can_and_cannot_do()
    {
        Assert.Contains("The app cannot read what is inside the", Html, StringComparison.Ordinal);
        // And what to do about it. This used to end "so the item is typed into the box above"; the
        // read gave the sentence a better second half, and the half that matters — the frame is not
        // where the lot's name comes from — is still the one being made.
        Assert.Contains("Read the show", Html, StringComparison.Ordinal);
    }

    /// <summary>
    /// The card is a translation of the app's existing pipeline, never a second lookup. If this
    /// endpoint ever grows its own comp search there will be two resale prices for one item in one
    /// app, and no way to tell which one is on screen.
    /// </summary>
    [Fact]
    public void The_endpoint_reuses_the_apps_own_market_analysis_and_never_scrapes_terapeak()
    {
        var endpoint = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "// One GET for one page the seller pasted.");

        Assert.Contains("await AnalyzeProductAsync(", endpoint, StringComparison.Ordinal);
        Assert.Contains("advisor.Build(", endpoint, StringComparison.Ordinal);

        // A real Terapeak scrape is a browser page load against a logged-in session. A live auction
        // is over before it finishes.
        Assert.Contains("allowRealTerapeakScrape: false", endpoint, StringComparison.Ordinal);

        // And it prices nothing itself — no fee arithmetic, no median, no second opinion.
        Assert.DoesNotContain("profitCalc.Calculate(", endpoint, StringComparison.Ordinal);
    }

    /// <summary>
    /// The hard constraint on this whole feature: sold comps is what everything else in the app
    /// rests on, and WhatsNot is additive to it. The card READS that path and changes nothing about
    /// it — no writes, no disabling, no alternative repository.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_read_and_never_touched()
    {
        var endpoint = Section(Program, "app.MapPost(\"/api/whatsnot/bid\"", "// One GET for one page the seller pasted.");

        // The lookup goes through the same interface every other board resolves.
        Assert.Contains("IMarketplaceRepository marketplace", endpoint, StringComparison.Ordinal);

        foreach (var forbidden in new[] { "SaveComparable", "DeleteComparable", "ClearComparables", "ExternalMarketplaceDb" })
            Assert.DoesNotContain(forbidden, endpoint, StringComparison.Ordinal);

        // The three screens that were already there are still registered and still wired.
        Assert.Contains("app.MapPost(\"/api/snap\"", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/opportunities/analyze-supplier-file\"", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_settings_that_do_not_change_between_items_are_remembered()
    {
        // During a live sale there is time to type the item's name and nothing else.
        Assert.Contains("const WN_SETTINGS_KEY = 'whatsnotBidSettings';", Js, StringComparison.Ordinal);
        Assert.Contains("wnLoadSettings();", Js, StringComparison.Ordinal);
        Assert.Contains("wnSaveSettings();", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bar that set the ceiling is written by the server, because it quotes a dollar figure —
    /// <see cref="ING_eBay_AutoLister.Services.LocalArbitrageAnalyzer.SolidProfit"/> — that must
    /// have exactly one definition in the app.
    /// </summary>
    [Fact]
    public void The_browser_never_restates_the_apps_own_money_bars()
    {
        Assert.Contains("c.ceilingNote", Js, StringComparison.Ordinal);
        Assert.Contains("SolidProfit:C0} cash floor", ReadSource(Path.Combine("Services", "LiveBidAdvisor.cs")),
            StringComparison.Ordinal);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
