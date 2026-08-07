namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The read reaches the live card through five links, and four of them are outside C#: the service
/// is registered, the endpoint is mapped, the browser asks for it, the browser puts the answer in
/// the box, and the stylesheet tells the three outcomes apart. Break any one and the button quietly
/// stops filling anything, which looks exactly like a show with no lot on it.
/// </summary>
/// <remarks>
/// Three of these are decisions rather than plumbing, and all three are the sort of thing a later
/// tidy-up undoes without reading why: the read prices nothing of its own, the automatic loop gets
/// out of the seller's way rather than fighting them for the box, and the sold-comps path this whole
/// screen stands on is left completely alone.
/// </remarks>
public class WhatsNotReadShowAssetTests
{
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Reader = ReadSource("Services/WhatnotShowReader.cs");
    private static readonly string Parser = ReadSource("Services/WhatnotShowParser.cs");

    // ── The endpoint ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_reader_is_registered_and_the_endpoint_is_mapped()
    {
        Assert.Contains("builder.Services.AddSingleton<WhatnotShowReader>();", Program, StringComparison.Ordinal);
        Assert.Contains("app.MapPost(\"/api/whatsnot/read\"", Program, StringComparison.Ordinal);
        Assert.Contains("await reader.ReadAsync(req.Url, ct)", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole safety property of this feature. The read produces a title and a starting number;
    /// what the lot is WORTH is the eBay sold-comp path behind /api/whatsnot/bid, unchanged. An
    /// endpoint that priced what it had just scraped would be a second opinion about money on the
    /// one screen where there is no time to notice two.
    /// </summary>
    [Fact]
    public void Reading_the_show_prices_nothing()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/read\"", "});");

        foreach (var pricing in new[] { "AnalyzeProductAsync", "advisor.Build", "board.Hold", "MaxBid" })
            Assert.DoesNotContain(pricing, endpoint, StringComparison.Ordinal);
    }

    [Fact]
    public void The_read_is_logged_so_a_bad_title_can_be_traced_to_the_page_it_came_off()
    {
        var endpoint = Between(Program, "app.MapPost(\"/api/whatsnot/read\"", "});");

        Assert.Contains("log.Add(", endpoint, StringComparison.Ordinal);
        Assert.Contains("Read the show", endpoint, StringComparison.Ordinal);
    }

    // ── What the reader will and won't fetch ─────────────────────────────────────────────────

    /// <summary>
    /// The app is about to make a request at an address somebody typed, so the same guard the embed
    /// check runs has to run here — loopback and private-network addresses are not live shows, and a
    /// "read the show" box that fetched them would be a way to make this machine fetch anything.
    /// </summary>
    [Fact]
    public void The_address_goes_through_the_same_guard_the_embed_check_uses()
    {
        Assert.Contains("FrameEmbedPolicy.Normalize(rawUrl)", Reader, StringComparison.Ordinal);
        Assert.Contains("FrameEmbedPolicy.Validate(url)", Reader, StringComparison.Ordinal);
        // And then, separately, that it is one Whatnot show rather than any web page at all.
        Assert.Contains("WhatnotShowParser.ValidateShowUrl(url)", Reader, StringComparison.Ordinal);
    }

    /// <summary>
    /// The tradeoff this feature turns on, written where the code that makes it lives. It is the
    /// first thing a later session will be tempted to "improve" — the private API is faster and
    /// carries the live bid — so the reason not to is next to the request, not in a commit message.
    /// </summary>
    [Fact]
    public void Why_the_public_page_and_not_the_private_api_is_written_down_in_the_code()
    {
        foreach (var source in new[] { Reader, Parser })
        {
            // Named, so nobody has to guess which faster path was rejected.
            Assert.Contains("GraphQL", source, StringComparison.Ordinal);
            Assert.Contains("public", source, StringComparison.OrdinalIgnoreCase);
        }

        // And the cost of that choice, in the same breath: a page is a snapshot, so the bid is not live.
        Assert.Contains("snapshot", Parser, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_read_gives_up_inside_the_time_a_lot_lasts()
    {
        // A read that outlasts the lot it was about has missed its moment, and a seller staring at a
        // spinner is worse off than one who typed the name.
        Assert.Contains("public const int ReadTimeoutSeconds = 8;", Reader, StringComparison.Ordinal);
        Assert.Contains("deadline.CancelAfter(TimeSpan.FromSeconds(ReadTimeoutSeconds))", Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void The_page_is_read_through_the_apps_own_bounded_fetch_rather_than_a_second_byte_loop()
    {
        Assert.Contains("PublicFeedHttp.ApplyBrowserHeaders(http)", Reader, StringComparison.Ordinal);
        Assert.Contains("PublicFeedHttp.ReadBoundedAsync(response, WhatnotShowParser.MaxPageBytes", Reader, StringComparison.Ordinal);
        // A 200 is not an answer: a challenge page arrives with a success status and parses to no lot.
        Assert.Contains("DealFeedParser.DetectBlock(body, \"Whatnot\")", Reader, StringComparison.Ordinal);
    }

    [Fact]
    public void A_read_that_fails_costs_the_shortcut_and_never_the_screen()
    {
        // Every refusal path carries a next move, and the next move is always the typed box that has
        // worked since the first version of this screen.
        Assert.Contains("catch (Exception ex)", Reader, StringComparison.Ordinal);
        Assert.Contains("Type the item in and press Price it.", Reader, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_read_row_sits_above_the_box_it_fills()
    {
        Assert.Contains("id=\"wn-read-url\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-read-btn\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-read-out\"", Html, StringComparison.Ordinal);

        Assert.True(Html.IndexOf("id=\"wn-read-url\"", StringComparison.Ordinal)
                  < Html.IndexOf("id=\"wn-item\"", StringComparison.Ordinal),
            "the read belongs above the box it fills");
    }

    /// <summary>
    /// The card is replaced whole every time the bid moves and the one line above it is the screen's
    /// only live region. The read's own panel must not become a second one — a screen reader given
    /// two competing announcements during a live sale is a screen reader saying nothing usable.
    /// </summary>
    [Fact]
    public void The_read_panel_is_not_a_second_live_region()
    {
        var panel = Between(Html, "<div id=\"wn-read-out\"", ">");

        Assert.DoesNotContain("aria-live", panel, StringComparison.Ordinal);
        // What it found is announced through the line that already exists.
        Assert.Contains("wnSayLine([read.headline, read.hint]", Js, StringComparison.Ordinal);
    }

    [Fact]
    public void What_was_read_goes_into_the_same_box_and_through_the_same_price_it()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        Assert.Contains("setVal('wn-item', read.title);", readShow, StringComparison.Ordinal);
        Assert.Contains("wnPriceItem();", readShow, StringComparison.Ordinal);
        // A different item is a different question, so the comps held for the old one are let go.
        Assert.Contains("wnDropToken();", readShow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_browser_computes_nothing_about_the_read()
    {
        // Every sentence on the panel is the server's. A browser that assembled its own would be a
        // second account of what somebody else's page said, and it has no access to that page.
        var render = Between(Js, "function wnRenderRead(read)", "async function wnReadShow(options)");

        Assert.Contains("esc(read.headline", render, StringComparison.Ordinal);
        Assert.Contains("esc(read.detail)", render, StringComparison.Ordinal);
        Assert.Contains("esc(w)", render, StringComparison.Ordinal);
        foreach (var arithmetic in new[] { "* 1.3", "/ 1.3", ".toFixed(", "reduce(" })
            Assert.DoesNotContain(arithmetic, render, StringComparison.Ordinal);
    }

    [Fact]
    public void Where_every_field_came_from_is_on_the_screen()
    {
        // This is a claim about a document the seller cannot see, so a read that went wrong has to be
        // inspectable rather than mysterious.
        Assert.Contains("Where each field came from", Js, StringComparison.Ordinal);
        Assert.Contains(".wn-read-ev {", Css, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_of_the_three_outcomes_has_an_edge_of_its_own()
    {
        Assert.Contains("wn-read-${kind}", Js, StringComparison.Ordinal);
        foreach (var rule in new[] { ".wn-read-ok {", ".wn-read-none {", ".wn-read-bad {" })
            Assert.Contains(rule, Css, StringComparison.Ordinal);
    }

    // ── The automatic loop ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The only thing on this screen that reaches the network on its own, so all three of its brakes
    /// are pinned: it is slower than the bidding, it stops itself, and it stops when the tab closes.
    /// </summary>
    [Fact]
    public void Keep_reading_is_slower_than_the_bidding_and_stops_itself()
    {
        Assert.Contains("const WN_WATCH_MS = 20000;", Js, StringComparison.Ordinal);
        Assert.Contains("const WN_WATCH_MAX_READS = 90;", Js, StringComparison.Ordinal);
        Assert.Contains("if (++wnWatchReads > WN_WATCH_MAX_READS)", Js, StringComparison.Ordinal);

        // Off unless the seller turns it on.
        Assert.Contains("id=\"wn-watch\" type=\"checkbox\" />", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Closing_the_tab_stops_the_reader()
    {
        var close = Between(Js, "function closeWhatsNotSection()", "// ── WhatsNot: the live-auction arbitrage card");

        Assert.Contains("wnClearWatchTimer();", close, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property that makes an automatic reader usable at all: what the seller typed outranks what
    /// the page says. A loop that overwrote the item box every twenty seconds would be worst at
    /// exactly the moment it matters.
    /// </summary>
    [Fact]
    public void The_loop_gets_out_of_the_way_the_moment_the_seller_types()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        Assert.Contains("typed !== wnReadFilled", readShow, StringComparison.Ordinal);
        Assert.Contains("the box is yours", readShow, StringComparison.Ordinal);
        // And it does not re-price a lot that is still the same lot.
        Assert.Contains("if (!movedOn) return;", readShow, StringComparison.Ordinal);
    }

    [Fact]
    public void A_show_between_lots_is_not_treated_as_a_failure()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        // It keeps watching through "no lot on the page" and stops on anything that has actually
        // broken — a loop that kept requesting through a refusal would be hammering somebody's site.
        Assert.Contains("read.status !== 'no-listing'", readShow, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_answer_never_paints_over_a_newer_one()
    {
        var readShow = Between(Js, "async function wnReadShow(options)", "function wnClearWatchTimer()");

        Assert.Contains("if (seq !== wnReadSeq) return;", readShow, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stylesheet_and_the_script_were_both_re_stamped()
    {
        // wwwroot is embedded, and a cached app.js against a new server renders a row whose button
        // does nothing at all.
        Assert.Contains("app.js?v=125", Html, StringComparison.Ordinal);
        Assert.Contains("style.css?v=108", Html, StringComparison.Ordinal);
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
                     "/api/whatsnot/embed-check",
                 })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        // And the live price still runs on the app's own sold-comp pipeline rather than a new lookup.
        Assert.Contains("analysis = await AnalyzeProductAsync(", Program, StringComparison.Ordinal);
    }

    [Fact]
    public void The_typed_path_still_works_on_its_own()
    {
        // The read is a shortcut into the box, not a replacement for it. Somebody watching a show on
        // a platform this read has never heard of still types the lot and gets the same ceiling.
        Assert.Contains("on('wn-price', 'click', wnPriceItem);", Js, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-item\"", Html, StringComparison.Ordinal);
    }

    [Fact]
    public void The_feeds_that_share_the_bounded_fetch_still_use_it_their_own_way()
    {
        // This session made PublicFeedHttp.ReadBoundedAsync public rather than writing a second byte
        // loop. GetAsync — the path every deal feed runs on — still calls it and is untouched.
        var feed = ReadSource("Services/PublicFeedHttp.cs");

        Assert.Contains("var body = await ReadBoundedAsync(response, maxBytes, budget);", feed, StringComparison.Ordinal);
        Assert.Contains("public static async Task<string?> ReadBoundedAsync(", feed, StringComparison.Ordinal);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" is no longer in the source");
        var to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"\"{end}\" no longer follows \"{start}\"");
        return source[from..to];
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
