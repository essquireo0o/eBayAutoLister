using ING_eBay_AutoLister.Models;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The lots that got away, and what they went for.
/// </summary>
/// <remarks>
/// <para>
/// A seller prices thirty lots in a live show and wins four. The buy sheet records those four. The
/// other twenty-six — each one priced by this app, each one with a ceiling written for it, each one
/// sold in front of the seller at a price they watched land — have been discarded, every night, by
/// every version of this screen.
/// </para>
/// <para>
/// They are the only direct measurement of the ROOM that exists. Whether a ceiling can be bought at
/// is not a fact about the item and no quantity of eBay sold history will ever answer it; it is a
/// fact about who else is watching the same stream, and the hammer price is that fact, observed.
/// This is the file those observations go in, so that <see cref="LiveRoom"/> can count them.
/// </para>
/// <para>
/// <b>Nothing in here is evidence of what anything is worth.</b> A hammer price at a live show is
/// what one bidder paid one seller in one room on one night, frequently with no comparison shopping
/// and a countdown running. It is not a sold comp, it never enters the pricing pipeline, and the one
/// thing it is ever compared against is the app's own ceiling for the same lot. The same rule
/// <see cref="LiveBuySheet"/> states about its own rows, for the same reason: an app that priced
/// items off auction prices it had watched would be quoting itself.
/// </para>
/// <para>
/// <b>Persisted, because a room is longer than a session.</b> A show runs for hours and a host runs
/// one every week. Written through <see cref="AtomicFile"/>, like every other thing here whose loss
/// would cost the seller something they cannot type back in.
/// </para>
/// </remarks>
public sealed class LiveRoomBook
{
    /// <summary>The file under <see cref="AppPaths.DataHome"/>.</summary>
    public const string FileName = "whatsnot-room-book.json";

    /// <summary>
    /// How many watched lots are kept. Deliberately larger than the buy sheet's cap: a seller wins
    /// a handful of lots a night and watches dozens, and the whole value of this file is in the
    /// dozens. Past this the oldest go.
    /// </summary>
    public const int MaxLots = 500;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<PassedLot>? _cache;

    public LiveRoomBook() : this(System.IO.Path.Combine(AppPaths.DataHome, FileName)) { }

    public LiveRoomBook(string path) => _path = path;

    /// <summary>Where the book is kept. For the log line and the tests, not the screen.</summary>
    public string FilePath => _path;

    /// <summary>The book as it stands.</summary>
    public RoomBook Read(DateTime? nowUtc = null)
    {
        lock (_gate) return Compose(Load(), nowUtc ?? DateTime.UtcNow);
    }

    /// <summary>
    /// Records what a lot went for, off the card that was built at that price, and returns the whole
    /// book — every show's line moves when a row lands, so handing back the row alone would leave
    /// the screen to recompute a clearing rate in JavaScript.
    /// </summary>
    public RoomBook Record(LiveBidCard card, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var now = nowUtc ?? DateTime.UtcNow;
        var row = RowFrom(card, now);

        lock (_gate)
        {
            var lots = Load();
            lots.Add(row);
            while (lots.Count > MaxLots) lots.RemoveAt(0);
            Save(lots);
            return Compose(lots, now);
        }
    }

    /// <summary>
    /// Every lot watched to the hammer on one show, recently enough to still describe its room.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An unnamed show matches nothing, including rows that are themselves unnamed — the same rule
    /// <see cref="LiveBuySheet.ShippingOnShow"/> enforces, and for a stronger reason here: a
    /// clearing rate pooled across three different hosts is a claim about a room that does not
    /// exist. Matched by <see cref="LiveShipShare.NormalizeShow"/>, so a show named one way on the
    /// card and another way on a won lot is still one room.
    /// </para>
    /// <para>
    /// Rows older than <see cref="LiveRoom.EvidenceDays"/> are left on disk and left out of the
    /// count. A host's audience on a Saturday is not their audience three weeks ago, and a rate
    /// built out of a month of history would be confidently describing a room that has turned over.
    /// </para>
    /// <para>
    /// Read under the same lock as everything else here, off the cached list: no file I/O in the
    /// common case, which is what lets it sit inside a re-price that has to answer between two bids.
    /// </para>
    /// </remarks>
    public List<LiveRoomLot> PassesOnShow(string? show, DateTime? nowUtc = null)
    {
        var key = LiveShipShare.NormalizeShow(show);
        if (key.Length == 0) return [];

        var cutoff = (nowUtc ?? DateTime.UtcNow).AddDays(-LiveRoom.EvidenceDays);

        lock (_gate)
        {
            return Load()
                .Where(l => string.Equals(LiveShipShare.NormalizeShow(l.ShowName), key, StringComparison.Ordinal))
                .Where(l => l.SeenAtUtc >= cutoff)
                .Select(l => new LiveRoomLot(l.HammerPrice, l.CeilingAtPass, Won: false))
                .ToList();
        }
    }

    /// <summary>One row, by its own id. Null when it is not in the book.</summary>
    public PassedLot? Find(string? id)
    {
        var key = (id ?? "").Trim();
        if (key.Length == 0) return null;
        lock (_gate) return Load().FirstOrDefault(l => string.Equals(l.Id, key, StringComparison.Ordinal));
    }

    /// <summary>Removes one row — a mistyped hammer price, or a lot recorded twice. An unknown id is
    /// not an error; the row is already gone, which is what was being asked for.</summary>
    public RoomBook Remove(string? id, DateTime? nowUtc = null)
    {
        var key = (id ?? "").Trim();
        lock (_gate)
        {
            var lots = Load();
            if (key.Length > 0 && lots.RemoveAll(l => string.Equals(l.Id, key, StringComparison.Ordinal)) > 0)
                Save(lots);
            return Compose(lots, nowUtc ?? DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Forgets a room. Named, it clears one show and leaves every other one standing — which is the
    /// case that matters, since the seller's reason for clearing is nearly always "that host changed
    /// something". Unnamed, it clears the lot.
    /// </summary>
    public RoomBook Clear(string? show = null, DateTime? nowUtc = null)
    {
        var key = LiveShipShare.NormalizeShow(show);
        lock (_gate)
        {
            var lots = key.Length == 0
                ? []
                : Load()
                    .Where(l => !string.Equals(LiveShipShare.NormalizeShow(l.ShowName), key, StringComparison.Ordinal))
                    .ToList();
            Save(lots);
            return Compose(lots, nowUtc ?? DateTime.UtcNow);
        }
    }

    // ── The row ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One watched lot, off the card built at the price it hammered at. Pure: every figure is
    /// copied, none is recomputed.
    /// </summary>
    public static PassedLot RowFrom(LiveBidCard card, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(card);

        var row = new PassedLot
        {
            Id = Guid.NewGuid().ToString("N"),
            Item = card.Item,
            CategoryLabel = card.CategoryLabel,
            SeenAtUtc = nowUtc,
            // The card's own wording for the show, so a watched lot and a won lot from the same
            // stream land in the same room.
            ShowName = card.Ship?.ShowName ?? "",
            Units = Math.Max(1, card.Units?.Count ?? 1),
            HammerPrice = card.CurrentBid,
            // The MARKET's ceiling, never the wallet's. LiveBudget always reports the figure the
            // comps produced, and on a card with no budget set the two are the same number.
            CeilingAtPass = card.Budget is { MarketCeiling: > 0m } budget ? budget.MarketCeiling : card.MaxBid,
            Call = card.Call,
            CompCount = card.CompCount,
        };

        row.Say = SayRow(row);
        return row;
    }

    /// <summary>
    /// One watched lot in a sentence — the row's accessible label.
    /// </summary>
    /// <remarks>
    /// The figures round the same way <see cref="LiveBuySheet.SayRow"/> rounds them: what it cost
    /// somebody rounds up, what the app would have paid rounds down. A row skimmed during a show
    /// must never make the seller's own discipline look better than it was.
    /// </remarks>
    public static string SayRow(PassedLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var line = $"{lot.Item} went for {Math.Ceiling(lot.HammerPrice).ToString("C0")}";

        if (lot.CeilingAtPass <= 0m)
            return line + ". Nothing priced it, so there was no ceiling to measure that against.";

        var ceiling = Math.Floor(lot.CeilingAtPass).ToString("C0");

        if (lot.HammerPrice > lot.CeilingAtPass)
        {
            return line + $" — {Math.Ceiling(lot.HammerPrice - lot.CeilingAtPass).ToString("C0")} above " +
                          $"the {ceiling} ceiling. The room outbid the arithmetic.";
        }

        var share = (int)Math.Round(lot.HammerPrice / lot.CeilingAtPass * 100m, MidpointRounding.AwayFromZero);
        return line + $", {share}% of the {ceiling} ceiling.";
    }

    // ── The book ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The book, grouped by room. Pure, and separate from the file so the arithmetic can be tested
    /// without one.
    /// </summary>
    public static RoomBook Compose(IEnumerable<PassedLot>? lots, DateTime nowUtc)
    {
        var rows = (lots ?? []).OrderByDescending(l => l.SeenAtUtc).ToList();

        var book = new RoomBook { Lots = rows, LotCount = rows.Count };

        book.Shows = rows
            .Where(l => LiveShipShare.NormalizeShow(l.ShowName).Length > 0)
            .GroupBy(l => LiveShipShare.NormalizeShow(l.ShowName), StringComparer.Ordinal)
            .Select(g => ShowLine(g.ToList(), nowUtc))
            .OrderByDescending(s => s.LastSeenUtc)
            .ToList();

        book.Say = Say(book);
        return book;
    }

    /// <summary>
    /// One show's line, read through the same <see cref="LiveRoom.Read"/> the card uses — so the
    /// panel and the strip can never disagree about what a room clears at.
    /// </summary>
    private static RoomShow ShowLine(List<PassedLot> rows, DateTime nowUtc)
    {
        var name = rows[0].ShowName;
        var cutoff = nowUtc.AddDays(-LiveRoom.EvidenceDays);
        var recent = rows.Where(l => l.SeenAtUtc >= cutoff).ToList();

        // No ceiling handed in: this line is about the room and not about a lot on screen, so there
        // is nothing for an expected hammer price to be a share of.
        var read = LiveRoom.Read(
            name,
            LiveRoom.Tonight(recent.Select(l => new LiveRoomLot(l.HammerPrice, l.CeilingAtPass, false)).ToList(), null),
            0m);

        return new RoomShow
        {
            ShowName = name,
            Watched = read.Watched,
            Rated = read.Rated,
            OverCeiling = read.OverCeiling,
            ClearingPercent = read.ClearingPercent,
            Verdict = read.Verdict,
            Say = read.Headline,
            LastSeenUtc = rows.Max(l => l.SeenAtUtc),
        };
    }

    /// <summary>The book in one sentence. Counts only — the rate belongs to a room, and this line
    /// is about a file that may hold three of them.</summary>
    public static string Say(RoomBook book)
    {
        ArgumentNullException.ThrowIfNull(book);
        if (book.LotCount == 0) return "";

        var line = $"{LiveRoom.Lots(book.LotCount)} watched to the hammer";
        if (book.Shows.Count > 0)
            line += $" across {(book.Shows.Count == 1 ? "1 show" : $"{book.Shows.Count} shows")}";

        var hot = book.Shows.Count(s => s.Verdict == LiveRoomVerdicts.Hot);
        var cheap = book.Shows.Count(s => s.Verdict == LiveRoomVerdicts.Cheap);

        line += ".";
        if (cheap > 0) line += $" {cheap} clearing under your ceilings.";
        if (hot > 0) line += $" {hot} clearing above them.";

        return line;
    }

    // ── The file ──────────────────────────────────────────────────────────────

    private List<PassedLot> Load()
    {
        if (_cache is not null) return _cache;

        var text = AtomicFile.ReadWithRecovery(_path);
        if (string.IsNullOrWhiteSpace(text)) return _cache = [];

        try
        {
            _cache = JsonSerializer.Deserialize<List<PassedLot>>(text, Json) ?? [];
        }
        catch
        {
            // Unreadable after both the file and its backup. An empty book is the honest state to
            // start from — and nothing is deleted here, so the file is still on disk to look at.
            _cache = [];
        }

        // A row written by an older build, or hand-edited. The sentence is regenerated rather than
        // trusted, so the words on screen always match the numbers beside them.
        foreach (var row in _cache)
        {
            if (row.Id.Length == 0) row.Id = Guid.NewGuid().ToString("N");
            if (row.Units < 1) row.Units = 1;
            row.Say = SayRow(row);
        }

        return _cache;
    }

    private void Save(List<PassedLot> lots)
    {
        _cache = lots;
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(lots, Json));
        }
        catch
        {
            // The book on screen is still the truth and the next write will try again. A show is not
            // interrupted because a disk was busy.
        }
    }
}
