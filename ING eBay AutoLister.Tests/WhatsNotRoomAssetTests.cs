namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The first read on the live card that is about the other bidders rather than about the item
/// reaches the screen through eight links, and most of them are the sort of thing a later tidy-up
/// removes without reading why: the button that records a hammer price, the endpoint that rebuilds
/// the card at that price, the store that keeps it, the buy sheet handing back its own wins on
/// equal terms, the advisor reading it against the MARKET's ceiling, the strip, the one warning and
/// the spoken line. Break any one and the feature silently does nothing on every card forever,
/// which looks exactly like working.
/// </summary>
/// <remarks>
/// <para>
/// Four of these are decisions rather than plumbing. It measures and it <b>never charges</b> — no
/// ceiling, resale price, median or call moves for anything found here, because a room that outbids
/// a correct ceiling has not made the object worth less. A rate is <b>refused</b> under three rated
/// lots. Nothing is <b>ever pooled across shows</b>, because a room is one host's audience. And the
/// lots that were <b>won are counted too</b>, because a rate built off the losses alone measures the
/// top tail of its own distribution and calls every room hot.
/// </para>
/// <para>
/// And the constraint every WhatsNot session has worked under: the sold-comps path this whole screen
/// stands on is untouched, and this is purely additive to it.
/// </para>
/// </remarks>
public class WhatsNotRoomAssetTests
{
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");
    private static readonly string Advisor = ReadSource("Services/LiveBidAdvisor.cs");
    private static readonly string Room = ReadSource("Services/LiveRoom.cs");
    private static readonly string Book = ReadSource("Services/LiveRoomBook.cs");
    private static readonly string RoomModels = ReadSource("Models/LiveRoomModels.cs");
    private static readonly string BidModels = ReadSource("Models/LiveBidModels.cs");
    private static readonly string Sheet = ReadSource("Services/LiveBuySheet.cs");
    private static readonly string Speech = ReadSource("Services/LiveBidSpeech.cs");

    // ── The evidence it measures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A room is one host's audience. Both stores match a show by the SAME rule the combined-shipping
    /// read matches one by, so a stream named two ways is one room — and an unnamed show matches
    /// nothing at all, in both of them.
    /// </summary>
    [Fact]
    public void Both_books_match_a_show_by_the_one_normalisation_rule()
    {
        Assert.Contains("LiveShipShare.NormalizeShow(show)", Book, StringComparison.Ordinal);
        Assert.Contains("LiveShipShare.NormalizeShow(show)", Sheet, StringComparison.Ordinal);

        // And the empty key is refused rather than matching the unnamed rows.
        Assert.Contains("if (key.Length == 0) return [];", Book, StringComparison.Ordinal);
        Assert.Contains("if (key.Length == 0) return [];", Sheet, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wins count on equal terms, and the sheet is where they come from. A rate built only from
    /// the lots that got away is computed off the top tail of its own distribution — a seller wins
    /// the lots that go cheap — and would report every room as hotter than it is.
    /// </summary>
    [Fact]
    public void The_lots_that_were_won_are_pooled_in_from_the_buy_sheet()
    {
        Assert.Contains("public List<LiveRoomLot> WinsOnShow(", Sheet, StringComparison.Ordinal);
        Assert.Contains("Won: true", Sheet, StringComparison.Ordinal);
        Assert.Contains("public List<LiveRoomLot> PassesOnShow(", Book, StringComparison.Ordinal);
        Assert.Contains("Won: false", Book, StringComparison.Ordinal);

        // Pooled in exactly one place — LiveRoom.Tonight — and the endpoints call it rather than
        // each building a list of their own.
        Assert.Contains("public static LiveRoomTonight Tonight(", Room, StringComparison.Ordinal);
        Assert.Contains("LiveRoom.Tonight(roomBook.PassesOnShow(", Program, StringComparison.Ordinal);
        Assert.Contains("sheet.WinsOnShow(", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both books cut their evidence at the same two weeks, off the same constant. A host's audience
    /// on a Saturday is not their audience three weeks ago, and two different windows would make the
    /// wins and the losses describe different rooms.
    /// </summary>
    [Fact]
    public void Both_books_cut_their_evidence_at_the_one_window()
    {
        Assert.Contains("public const int EvidenceDays = 14;", Room, StringComparison.Ordinal);
        Assert.Contains("AddDays(-LiveRoom.EvidenceDays)", Book, StringComparison.Ordinal);
        Assert.Contains("AddDays(-LiveRoom.EvidenceDays)", Sheet, StringComparison.Ordinal);
    }

    // ── The ceiling it measures against ──────────────────────────────────────────────────────

    /// <summary>
    /// The MARKET's ceiling on both sides. The recorded ceilings are market ceilings — neither the
    /// win path nor the pass path carries the night's budget into the card it writes down — so the
    /// read has to compare against the same kind of number, or a thin wallet reads as a hot room.
    /// </summary>
    [Fact]
    public void The_room_is_measured_against_the_market_ceiling_on_both_sides()
    {
        // Recorded: the market figure, and the budget deliberately left off the rebuilt card.
        Assert.Contains("card.Budget is { MarketCeiling: > 0m } budget ? budget.MarketCeiling : card.MaxBid",
            Book, StringComparison.Ordinal);
        Assert.DoesNotContain("NightBudget =", RoomModels, StringComparison.Ordinal);

        // Read: the same choice, in the advisor.
        Assert.Contains("card.Budget is { MarketCeiling: > 0m } market ? market.MarketCeiling : card.MaxBid",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>The rate is a median of the per-lot ratios rather than a ratio of two medians, so
    /// one $900 lot among nine $20 ones cannot decide a whole room.</summary>
    [Fact]
    public void The_rate_is_a_median_of_ratios_and_not_a_ratio_of_medians()
    {
        Assert.Contains("Select(l => l.Hammer / l.Ceiling)", Room, StringComparison.Ordinal);
        Assert.Contains("read.ClearingRatio = Median(ratios);", Room, StringComparison.Ordinal);
    }

    /// <summary>A rate is refused under three rated lots, and the count is still reported — "both
    /// lots here went over your ceiling" is a true sentence and is not a rate.</summary>
    [Fact]
    public void A_rate_is_refused_under_three_rated_lots()
    {
        Assert.Contains("public const int MinLotsToRate = 3;", Room, StringComparison.Ordinal);
        Assert.Contains("read.Readable = ratios.Count >= MinLotsToRate;", Room, StringComparison.Ordinal);
        // The strip only prints the percentage once the server said it was one.
        Assert.Contains("r.readable ? `<span class=\"wn-room-tag\">${r.clearingPercent}% of ceiling</span>`",
            Js, StringComparison.Ordinal);
    }

    // ── It measures and never charges ────────────────────────────────────────────────────────

    /// <summary>
    /// The property the rest of this card depends on. Nothing in the room file touches a price: no
    /// resale figure, no break-even, no ceiling, no call. A room that outbids a correct ceiling has
    /// not made the object worth less, and shading the price for it would be the app disguising a
    /// fact about people as a fact about an item.
    /// </summary>
    [Fact]
    public void Nothing_in_the_room_read_touches_a_price()
    {
        foreach (var forbidden in new[]
        {
            "ResaleMultiplier", "Discount(", "BreakEvenBid", "MaxBid", "ProfitCalculator", "ResalePricing",
        })
        {
            Assert.DoesNotContain(forbidden, Room, StringComparison.Ordinal);
        }

        // And on the priced path the advisor reads it AFTER the ceiling is settled, never into it.
        // (The other read is on the no-data exit, where there is no ceiling at all.)
        var ceiling = Advisor.IndexOf("card.MaxBid = maxBid;", StringComparison.Ordinal);
        var read = Advisor.LastIndexOf("card.Room = LiveRoom.Read(", StringComparison.Ordinal);
        Assert.True(ceiling > 0 && read > ceiling,
            "the room has to be read after the ceiling, or it is an input to it");
    }

    /// <summary>Only the hot room reaches the warning list. A cheap room is good news and good news
    /// belongs on the strip; a tight room is the ordinary shape of a live auction.</summary>
    [Fact]
    public void Only_the_hot_room_interrupts()
    {
        Assert.Contains("if (read.Verdict != LiveRoomVerdicts.Hot) return;", Room, StringComparison.Ordinal);
        Assert.Contains("if (card.Room is { Warning.Length: > 0 } roomRead) warnings.Add(roomRead.Warning);",
            Advisor, StringComparison.Ordinal);
    }

    /// <summary>Two of the four states speak in the one line the screen reads out loud, and the
    /// clause is the speech file's own — the browser assembles no sentence about a room.</summary>
    [Fact]
    public void The_spoken_line_carries_the_room_in_two_states_only()
    {
        Assert.Contains("private static string WhatThisRoomPays(LiveBidCard card)", Speech, StringComparison.Ordinal);
        Assert.Contains("WhatThisRoomPays(card)", Speech, StringComparison.Ordinal);
        Assert.Contains("LiveRoomVerdicts.Hot", Speech, StringComparison.Ordinal);
        Assert.Contains("LiveRoomVerdicts.Cheap", Speech, StringComparison.Ordinal);
        Assert.DoesNotContain("LiveRoomVerdicts.Tight", Speech, StringComparison.Ordinal);
    }

    // ── The screen ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The button that makes all of it possible, beside the one that records a win — same bid box,
    /// same held comps, and caught on the card rather than bound to a node that is replaced every
    /// two seconds while the bidding runs.
    /// </summary>
    [Fact]
    public void The_went_for_button_sits_beside_won_it_and_is_caught_on_the_card()
    {
        Assert.Contains("data-passed=\"1\"", Js, StringComparison.Ordinal);
        Assert.Contains("🔨 Went for", Js, StringComparison.Ordinal);
        Assert.Contains("if (e.target.closest('[data-passed]')) { wnRecordPass(); return; }",
            Js, StringComparison.Ordinal);
        Assert.Contains("'/api/whatsnot/passed'", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The strip is painted from the server's own words and the browser measures nothing. The one
    /// number it computes is a bar width, and it is capped — a bar drawn to 140% would need a scale
    /// nobody can read at a glance.
    /// </summary>
    [Fact]
    public void The_strip_paints_the_servers_words_and_computes_only_a_width()
    {
        Assert.Contains("function wnRoomStrip(rm)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(r.headline)", Js, StringComparison.Ordinal);
        Assert.Contains("esc(r.note)", Js, StringComparison.Ordinal);
        Assert.Contains("Math.max(0, Math.min(100, r.clearingPercent || 0))", Js, StringComparison.Ordinal);

        // And the card renders it, under the odds.
        Assert.Contains("${roomStrip}", Js, StringComparison.Ordinal);
        var odds = Js.IndexOf(
            "${oddsStrip}\n      ${roomStrip}".Replace("\n", "\r\n"), StringComparison.Ordinal);
        Assert.True(odds > 0, "the room strip belongs directly under the odds strip");
    }

    /// <summary>
    /// Recording a hammer price replaces the strip in place rather than re-rendering the card. The
    /// seller is still looking at the same lot and a redraw would throw away the tables they had
    /// open — and it would cost an eBay read they did not ask for.
    /// </summary>
    [Fact]
    public void Recording_a_hammer_price_replaces_the_strip_and_never_re_prices()
    {
        Assert.Contains("if (slot && body.room) slot.outerHTML = wnRoomStrip(body.room);",
            Js, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-room-slot\"", Js, StringComparison.Ordinal);
        // The endpoint answers with the read already made, so the browser recomputes no rate.
        Assert.Contains("Results.Ok(new { room, book, say", Program, StringComparison.Ordinal);
    }

    /// <summary>The panel exists, is read when the tab opens, and can take one bad row out — a
    /// mistyped hammer price silently biases every read off that room.</summary>
    [Fact]
    public void The_panel_is_loaded_on_open_and_a_bad_row_can_be_taken_out()
    {
        Assert.Contains("id=\"wn-room-book\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-room-head\"", Html, StringComparison.Ordinal);
        Assert.Contains("id=\"wn-room-shows\"", Html, StringComparison.Ordinal);
        Assert.Contains("wnLoadRoomBook();", Js, StringComparison.Ordinal);
        Assert.Contains("'/api/whatsnot/room/remove'", Js, StringComparison.Ordinal);
        Assert.Contains("'/api/whatsnot/room/clear'", Js, StringComparison.Ordinal);
        // Clearing throws away something no press can bring back, so it asks first.
        Assert.Contains("This cannot be undone.", Js, StringComparison.Ordinal);
    }

    /// <summary>Four verdicts, four edges, and the hot one is the WARN colour and not the danger
    /// one: nothing is wrong with the lot or its price, and a seller who reads this as "bad item"
    /// has learned exactly the wrong thing.</summary>
    [Fact]
    public void The_hot_room_gets_the_warn_edge_and_never_the_danger_one()
    {
        foreach (var cls in new[] { ".wn-room-cheap", ".wn-room-tight", ".wn-room-hot", ".wn-room-unread" })
            Assert.Contains(cls, Css, StringComparison.Ordinal);

        var hot = Css.IndexOf(".wn-room-hot {", StringComparison.Ordinal);
        Assert.True(hot > 0);
        var rule = Css[hot..(hot + 120)];
        Assert.Contains("var(--warn", rule, StringComparison.Ordinal);
        Assert.DoesNotContain("var(--danger", rule, StringComparison.Ordinal);

        // And it folds at the same width every other strip on this card folds at.
        Assert.Contains(".wn-room-line {", Css, StringComparison.Ordinal);
    }

    /// <summary>The block is on every card, including the ones with nothing measured — a strip that
    /// only appeared once a room had been measured would have a silence meaning both "this room is
    /// fine" and "nobody has ever written a hammer price down".</summary>
    [Fact]
    public void The_block_is_on_every_card()
    {
        Assert.Contains("public LiveRoomRead Room { get; set; } = new();", BidModels, StringComparison.Ordinal);
        Assert.Contains("card.Room = LiveRoom.Read(", Advisor, StringComparison.Ordinal);
        // Both exits of Build set it, so no card can reach the screen without one.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            Advisor, @"card\.Room = LiveRoom\.Read\(").Count);
    }

    // ── The endpoints, and what was left alone ───────────────────────────────────────────────

    /// <summary>A hammer price with no show is refused rather than written into a bucket nothing can
    /// ever look up, and a hammer price with no held comps is refused rather than recorded beside a
    /// ceiling nobody was shown.</summary>
    [Fact]
    public void A_recorded_hammer_price_needs_a_show_a_price_and_the_comps_it_was_priced_on()
    {
        Assert.Contains("\"/api/whatsnot/passed\"", Program, StringComparison.Ordinal);
        Assert.Contains("Which show was that?", Program, StringComparison.Ordinal);
        Assert.Contains("What did it go for?", Program, StringComparison.Ordinal);
        Assert.Contains("Those comps have been let go", Program, StringComparison.Ordinal);
        Assert.Contains("That's a different item", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// The constraint every session on this screen has worked under: the sold-comps path is
    /// untouched and this is additive. Every route the tab already had is still registered.
    /// </summary>
    [Fact]
    public void The_sold_comps_path_is_untouched_and_every_existing_route_still_answers()
    {
        foreach (var route in new[]
        {
            "/api/sold-comps", "/api/whatsnot/bid", "/api/whatsnot/rebid", "/api/whatsnot/won",
            "/api/whatsnot/sheet", "/api/whatsnot/lots", "/api/whatsnot/list",
            "/api/whatsnot/embed-check", "/api/whatsnot/read", "/api/whatsnot/photo",
            "/api/whatsnot/passed", "/api/whatsnot/room",
        })
        {
            Assert.Contains($"\"{route}\"", Program, StringComparison.Ordinal);
        }

        // And the live price still runs on the shared market pipeline, not on anything of its own.
        Assert.Contains("AnalyzeProductAsync", Program, StringComparison.Ordinal);
    }

    /// <summary>
    /// Nothing recorded here ever reaches the pricing pipeline. A hammer price at a live show is
    /// what one bidder paid one seller on one night, and an app that priced items off auctions it
    /// had watched would be quoting itself.
    /// </summary>
    [Fact]
    public void A_hammer_price_is_never_evidence_of_what_anything_is_worth()
    {
        foreach (var forbidden in new[]
        {
            "MarketplaceComparableResult", "MarketAnalysisResult", "AnalyzeProductAsync", "SoldComp",
        })
        {
            Assert.DoesNotContain(forbidden, Book, StringComparison.Ordinal);
            Assert.DoesNotContain(forbidden, Room, StringComparison.Ordinal);
        }
    }

    /// <summary>The panel and the card's strip are painted from the same read, so they cannot
    /// disagree about what a room clears at.</summary>
    [Fact]
    public void The_panel_and_the_strip_are_the_same_read()
    {
        Assert.Contains("var read = LiveRoom.Read(", Book, StringComparison.Ordinal);
        Assert.Contains("Say = read.Headline,", Book, StringComparison.Ordinal);
    }

    /// <summary>The store is registered, or the endpoints cannot be constructed and the tab returns
    /// a 500 on its first press.</summary>
    [Fact]
    public void The_room_book_is_registered()
    {
        Assert.Contains("builder.Services.AddSingleton<LiveRoomBook>();", Program, StringComparison.Ordinal);
        Assert.Contains("whatsnot-room-book.json", Book, StringComparison.Ordinal);
    }

    /// <summary>The screen was re-cut, so the browser has to be told to fetch it again.</summary>
    [Fact]
    public void The_asset_versions_were_bumped()
    {
        AssetStamp.AtLeast(Html, "app.js?v=", 145);
        AssetStamp.AtLeast(Html, "style.css?v=", 128);
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
