using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The buy sheet records the four lots a night that were won. This records the twenty-six that were
// watched and lost, which are the only direct measurement of the room there is.
//
// What is pinned here is that the row is the CARD's own figures — the ceiling written beside the
// hammer price is the one the seller was actually shown, not one recomputed later against different
// comps — that a room is one host's audience and nothing is ever pooled across shows, and that a
// hammer price older than two weeks stops describing tonight's room.
public class LiveRoomBookTests
{
    private static readonly ProfitCalculator Profit = new();
    private static readonly JackpotHunter Hunter = new(Profit);
    private static readonly LiveBidAdvisor Advisor = new(Profit, Hunter);
    private static readonly FeeProfile Fees = new();
    private static readonly DateTime Now = new(2026, 8, 7, 20, 0, 0, DateTimeKind.Utc);

    private const string Product = "Bitmain Antminer S19j Pro 104TH";
    private const string Show = "@bitminer_bill";

    /// <summary>The show as the app canonicalises it — <see cref="LiveShipShare.NormalizeShow"/>'s
    /// answer, which is what the card carries and therefore what a row records.</summary>
    private const string ShowKey = "bitminer_bill";

    private static LiveRoomBook NewBook() =>
        new(Path.Combine(Path.GetTempPath(), "wn-room-" + Guid.NewGuid().ToString("N") + ".json"));

    // ── The row is the card ───────────────────────────────────────────────────

    /// <summary>
    /// The whole legitimacy of this file. The ceiling written beside the hammer price is the card's
    /// own, copied — a ceiling recomputed a week later against comps that have moved would not be
    /// the number the seller's decision was measured against.
    /// </summary>
    [Fact]
    public void A_row_carries_the_cards_own_ceiling_and_computes_none_of_it()
    {
        var card = Advisor.Build(Product, Analysis(), Ask(bid: 130m, show: Show), Fees, nowUtc: Now);

        var row = LiveRoomBook.RowFrom(card, Now);

        Assert.Equal(card.CurrentBid, row.HammerPrice);
        Assert.Equal(card.MaxBid, row.CeilingAtPass);
        Assert.Equal(card.Item, row.Item);
        Assert.Equal(card.Call, row.Call);
        Assert.Equal(card.CompCount, row.CompCount);
        Assert.Equal(ShowKey, row.ShowName);
        Assert.Equal(Now, row.SeenAtUtc);
    }

    /// <summary>
    /// The MARKET's ceiling, never the wallet's. A lot that hammered at twice the app's number on a
    /// night the seller's cash had run out says nothing whatever about the room, and recording it
    /// against the budget-capped figure would turn this whole read into a report on a bank balance.
    /// </summary>
    [Fact]
    public void The_recorded_ceiling_is_the_markets_and_not_the_budgets()
    {
        var rich = Advisor.Build(Product, Analysis(), Ask(bid: 130m, show: Show), Fees, nowUtc: Now);

        // The same lot on a night with $40 left, which caps the ceiling hard.
        var broke = Advisor.Build(
            Product, Analysis(), Ask(bid: 130m, show: Show, budget: 500m), Fees, nowUtc: Now,
            cash: new LiveBudgetTonight(4, 460m));

        Assert.True(broke.Budget.Capped);
        Assert.True(broke.MaxBid < rich.MaxBid);

        // And the row records the untouched market ceiling either way.
        Assert.Equal(rich.MaxBid, LiveRoomBook.RowFrom(broke, Now).CeilingAtPass);
    }

    /// <summary>A card nothing could price has a real hammer price and no ceiling. The row is kept —
    /// it is still a lot this room bought — and it is never rated.</summary>
    [Fact]
    public void A_lot_nothing_priced_is_recorded_with_no_ceiling()
    {
        var card = Advisor.Build(Product, analysis: null, Ask(bid: 130m, show: Show), Fees, nowUtc: Now);

        var row = LiveRoomBook.RowFrom(card, Now);

        Assert.Equal(130m, row.HammerPrice);
        Assert.Equal(0m, row.CeilingAtPass);
        Assert.Contains("no ceiling to measure that against", row.Say, StringComparison.Ordinal);
    }

    /// <summary>Every figure in a row's sentence rounds the way the buy sheet's does: what it cost
    /// somebody rounds up, what the app would have paid rounds down. A row skimmed during a show
    /// must never make the seller's own discipline look better than it was.</summary>
    [Fact]
    public void The_rows_sentence_rounds_against_the_seller()
    {
        var over = LiveRoomBook.SayRow(new PassedLot
        { Item = "Antminer S9", HammerPrice = 130.20m, CeilingAtPass = 100.90m });

        Assert.Contains("went for $131", over, StringComparison.Ordinal);   // up
        Assert.Contains("the $100 ceiling", over, StringComparison.Ordinal); // down
        Assert.Contains("The room outbid the arithmetic", over, StringComparison.Ordinal);

        var under = LiveRoomBook.SayRow(new PassedLot
        { Item = "Antminer S9", HammerPrice = 60m, CeilingAtPass = 100m });
        Assert.Contains("60% of the $100 ceiling", under, StringComparison.Ordinal);
    }

    // ── A room is one host's audience ─────────────────────────────────────────

    /// <summary>
    /// Nothing is ever pooled across shows. A clearing rate built from three different streams is a
    /// confident claim about a room that does not exist.
    /// </summary>
    [Fact]
    public void Rows_never_cross_between_shows()
    {
        var book = NewBook();
        book.Record(Pass(bid: 60m, show: Show), Now);
        book.Record(Pass(bid: 400m, show: "@someone_else"), Now);

        Assert.Single(book.PassesOnShow(Show, Now));
        Assert.Single(book.PassesOnShow("@someone_else", Now));
        // And an unnamed show matches nothing at all, including rows that are themselves unnamed.
        Assert.Empty(book.PassesOnShow("", Now));
        Assert.Empty(book.PassesOnShow(null, Now));
    }

    /// <summary>One stream is one room however it happened to be typed — the same normalisation the
    /// combined-shipping read matches shows by.</summary>
    [Fact]
    public void A_show_typed_two_ways_is_still_one_room()
    {
        var book = NewBook();
        book.Record(Pass(bid: 60m, show: "@BitMiner_Bill"), Now);
        book.Record(Pass(bid: 70m, show: "  bitminer_bill "), Now);

        Assert.Equal(2, book.PassesOnShow("@bitminer_bill", Now).Count);
    }

    /// <summary>
    /// A host's audience on a Saturday is not their audience three weeks ago. Old rows stay on disk
    /// — nothing here deletes what the seller wrote down — and stop being counted.
    /// </summary>
    [Fact]
    public void A_hammer_price_stops_describing_the_room_after_two_weeks()
    {
        var book = NewBook();
        book.Record(Pass(bid: 60m, show: Show), Now.AddDays(-LiveRoom.EvidenceDays - 1));
        book.Record(Pass(bid: 70m, show: Show), Now.AddDays(-1));

        Assert.Single(book.PassesOnShow(Show, Now));
        // Still in the file, and still visible in the panel.
        Assert.Equal(2, book.Read(Now).LotCount);
    }

    // ── The book ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The panel's per-show line is read through the SAME <see cref="LiveRoom.Read"/> the card's
    /// strip is painted from, so the two can never disagree about what a show clears at.
    /// </summary>
    [Fact]
    public void Each_shows_line_is_the_same_read_the_card_uses()
    {
        var book = NewBook();
        foreach (var bid in new[] { 60m, 50m, 70m }) book.Record(Pass(bid, Show), Now);

        var line = Assert.Single(book.Read(Now).Shows);

        Assert.Equal(ShowKey, line.ShowName);
        Assert.Equal(3, line.Watched);
        Assert.Equal(3, line.Rated);

        var direct = LiveRoom.Read(Show, LiveRoom.Tonight(book.PassesOnShow(Show, Now), null), 0m);
        Assert.Equal(direct.ClearingPercent, line.ClearingPercent);
        Assert.Equal(direct.Verdict, line.Verdict);
        Assert.Equal(direct.Headline, line.Say);
    }

    /// <summary>Rows survive a reload, because a show runs for hours and a host runs one a week.</summary>
    [Fact]
    public void Rows_survive_a_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), "wn-room-" + Guid.NewGuid().ToString("N") + ".json");
        new LiveRoomBook(path).Record(Pass(bid: 60m, show: Show), Now);

        var reopened = new LiveRoomBook(path);
        var row = Assert.Single(reopened.Read(Now).Lots);
        Assert.Equal(60m, row.HammerPrice);
        Assert.NotEqual("", row.Say);
    }

    /// <summary>A mistyped hammer price silently biases every read off this room, so one row can
    /// always be taken out — and an id that is already gone is not an error.</summary>
    [Fact]
    public void One_row_can_be_removed_and_an_unknown_id_is_not_an_error()
    {
        var book = NewBook();
        book.Record(Pass(bid: 60m, show: Show), Now);
        var id = book.Read(Now).Lots[0].Id;

        Assert.Empty(book.Remove(id, Now).Lots);
        Assert.Empty(book.Remove("not-a-row", Now).Lots);
    }

    /// <summary>Forgetting a room clears one show and leaves the others standing, which is nearly
    /// always what is meant — the reason to clear is that one host changed something.</summary>
    [Fact]
    public void Clearing_a_named_show_leaves_the_other_rooms_alone()
    {
        var book = NewBook();
        book.Record(Pass(bid: 60m, show: Show), Now);
        book.Record(Pass(bid: 400m, show: "@someone_else"), Now);

        var after = book.Clear(Show, Now);
        var left = Assert.Single(after.Lots);
        Assert.Equal("someone_else", left.ShowName);

        Assert.Empty(book.Clear(null, Now).Lots);
    }

    /// <summary>The oldest go at the cap, and the one just recorded is the newest and survives.</summary>
    [Fact]
    public void The_book_is_capped_and_the_newest_row_survives()
    {
        var book = NewBook();
        for (var i = 0; i < LiveRoomBook.MaxLots + 5; i++)
            book.Record(Pass(bid: 10m + i, show: Show), Now.AddSeconds(i));

        var read = book.Read(Now);
        Assert.Equal(LiveRoomBook.MaxLots, read.LotCount);
        Assert.Equal(10m + LiveRoomBook.MaxLots + 4, read.Lots[0].HammerPrice);
    }

    /// <summary>The book's own sentence counts rooms and refuses to state a rate — a rate belongs to
    /// one room and this line is about a file that may hold three of them.</summary>
    [Fact]
    public void The_books_sentence_counts_rooms_and_states_no_rate()
    {
        var book = NewBook();
        foreach (var bid in new[] { 130m, 140m, 120m }) book.Record(Pass(bid, Show), Now);
        book.Record(Pass(bid: 20m, show: "@someone_else"), Now);

        var say = book.Read(Now).Say;

        Assert.Contains("4 lots watched to the hammer", say, StringComparison.Ordinal);
        Assert.Contains("2 shows", say, StringComparison.Ordinal);
        Assert.Contains("1 clearing above them", say, StringComparison.Ordinal);
    }

    /// <summary>An empty book says nothing rather than saying zero — the panel's own empty state is
    /// the sentence that tells the seller what the button is for.</summary>
    [Fact]
    public void An_empty_book_has_no_sentence()
    {
        Assert.Equal("", NewBook().Read(Now).Say);
    }

    // ── The wins side, on the buy sheet ───────────────────────────────────────

    /// <summary>
    /// The buy sheet's own rows come back as outcomes on equal terms, cut at the same two weeks and
    /// matched by the same show rule — and marked as wins, so the strip can say how the count splits.
    /// </summary>
    [Fact]
    public void The_buy_sheet_reports_its_wins_as_room_outcomes()
    {
        var sheet = new LiveBuySheet(
            Path.Combine(Path.GetTempPath(), "wn-sheet-" + Guid.NewGuid().ToString("N") + ".json"));

        var card = Advisor.Build(Product, Analysis(), Ask(bid: 90m, show: Show), Fees, nowUtc: Now);
        sheet.Record(card, Now);

        var win = Assert.Single(sheet.WinsOnShow(Show, Now));
        Assert.True(win.Won);
        Assert.Equal(90m, win.Hammer);
        Assert.Equal(card.MaxBid, win.Ceiling);

        Assert.Empty(sheet.WinsOnShow("@someone_else", Now));
        Assert.Empty(sheet.WinsOnShow("", Now));
        Assert.Empty(sheet.WinsOnShow(Show, Now.AddDays(LiveRoom.EvidenceDays + 1)));
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────

    private static LiveBidCard Pass(decimal bid, string show) =>
        Advisor.Build(Product, Analysis(), Ask(bid: bid, show: show), Fees, nowUtc: Now);

    private static LiveBidRequest Ask(decimal? bid = null, string? show = null, decimal? budget = null) =>
        new() { Title = Product, CurrentBid = bid, ShowName = show, NightBudget = budget };

    /// <summary>A market analysis in the shape <c>AnalyzeProductAsync</c> produces one.</summary>
    private static MarketAnalysisResult Analysis(decimal? expected = 200m) => new()
    {
        PriceEstimate = new PriceEstimate
        {
            MedianPrice = expected,
            ExpectedSalePrice = expected,
            QuickSalePrice = expected * 0.85m,
            Percentile25 = 170m,
            Percentile75 = 240m,
            MinimumRealisticPrice = 136m,
            MaximumRealisticPrice = 288m,
            LocalMedianPrice = expected,
            LocalExpectedSalePrice = expected,
            LocalWeight = 1m,
            PricedOnCompCount = 8,
            IdentityVerified = true,
            LocalOldestSoldAtUtc = Now.AddDays(-60),
            LocalNewestSoldAtUtc = Now.AddDays(-9),
        },
        SellThrough = new SellThroughAnalysis
        {
            SoldComparableCount = 8,
            ActiveComparableCount = 10,
            SellThroughRate = 80m,
            SellThroughScore = 72,
            Interpretation = "Very Strong",
            EstimatedMonthlySales = 4m,
            EstimatedDaysToSell = 14,
            LiquidityLevel = "Fast Mover",
        },
        Confidence = new ConfidenceBreakdown { Score = 70, Level = "Good" },
        Sources = new SourceBreakdown
        {
            LocalComparableCount = 8,
            TerapeakComparableCount = 0,
            PricedOnCompCount = 8,
            IdentityVerified = true,
        },
    };
}
