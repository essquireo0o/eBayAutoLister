using ING_eBay_AutoLister.Models;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the show cost, and what it is worth — the other end of the arbitrage card.
/// </summary>
/// <remarks>
/// <para>
/// The card stops at the hammer. It says <c>BID UP TO $240</c>, the lot goes for $180, and from
/// there the app has nothing further to say: whether the night made money lives in the seller's
/// head and in a stack of Whatnot receipts. Which means the two questions that actually decide
/// whether live buying is worth doing — <b>how much have I spent</b> and <b>what is it all worth</b>
/// — are the two this app was not answering.
/// </para>
/// <para>
/// <b>A won lot is not a new calculation.</b> Recording a win takes the comps already held for that
/// lot (<see cref="LiveBidBoard"/>) and runs <see cref="LiveBidAdvisor.Build"/> over them again at
/// the price it hammered at. Everything on the row — the landed cost, the resale price, the profit,
/// the return — is that card's own figures. So a row here cannot disagree with the card the bid was
/// made on, and it costs no eBay read: the sheet updates in milliseconds, mid-show, which is the
/// only speed this screen is allowed to work at.
/// </para>
/// <para>
/// <b>Why the ceiling is stored and not recomputed.</b> <see cref="WonLot.CeilingAtWin"/> is the
/// maximum bid the app gave <i>before</i> the hammer. Twenty minutes later the comps are let go, and
/// after that there is no way back to it — so the one number that says whether tonight's discipline
/// held is written down at the moment it is still true. It is what makes
/// <see cref="BuySheet.OverpaidCount"/> a fact about what happened rather than an opinion formed
/// afterwards.
/// </para>
/// <para>
/// <b>Persisted, because a show is longer than a session.</b> A live sale runs for hours; an app
/// restarted in the middle of one must not lose the record of $1,200 already spent. The file is
/// written through <see cref="AtomicFile"/>, like every other thing here whose loss would cost the
/// seller something they cannot type back in.
/// </para>
/// <para>
/// It is a buy record, never a sold-comp source. Nothing in here is evidence of what anything is
/// worth — these are prices <i>this</i> seller paid at auction, and letting them anywhere near the
/// pricing pipeline would be the app quoting itself.
/// </para>
/// </remarks>
public sealed class LiveBuySheet
{
    /// <summary>The file under <see cref="AppPaths.DataHome"/>.</summary>
    public const string FileName = "whatsnot-buy-sheet.json";

    /// <summary>
    /// How many won lots are kept. A long night is dozens; past this the oldest go, because a sheet
    /// nobody has cleared in a month has stopped being a show's takings and become an archive, and
    /// the totals on it answer a question nobody asked.
    /// </summary>
    public const int MaxLots = 200;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    private readonly string _path;
    private readonly object _gate = new();
    private List<WonLot>? _cache;

    public LiveBuySheet() : this(System.IO.Path.Combine(AppPaths.DataHome, FileName)) { }

    public LiveBuySheet(string path) => _path = path;

    /// <summary>Where the sheet is kept. For the log line and the tests, not the screen.</summary>
    public string FilePath => _path;

    /// <summary>The sheet as it stands.</summary>
    public BuySheet Read()
    {
        lock (_gate) return Compose(Load());
    }

    /// <summary>
    /// Records a win from the card built at the winning bid, and returns the whole sheet — the
    /// totals move on every row, so handing back the row alone would leave the screen to add up
    /// money in JavaScript.
    /// </summary>
    public BuySheet Record(LiveBidCard card, DateTime? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(card);
        var row = RowFrom(card, nowUtc ?? DateTime.UtcNow);

        lock (_gate)
        {
            var lots = Load();
            lots.Add(row);
            // Oldest out first, and only ever at the cap. The lot just won is the newest and is the
            // one thing that must survive a trim.
            while (lots.Count > MaxLots) lots.RemoveAt(0);
            Save(lots);
            return Compose(lots);
        }
    }

    /// <summary>Removes one row — recorded by mistake, or outbid after all. Unknown ids are not an
    /// error; the row is already gone, which is what was being asked for.</summary>
    public BuySheet Remove(string? id)
    {
        var key = (id ?? "").Trim();
        lock (_gate)
        {
            var lots = Load();
            if (key.Length > 0 && lots.RemoveAll(l => string.Equals(l.Id, key, StringComparison.Ordinal)) > 0)
                Save(lots);
            return Compose(lots);
        }
    }

    /// <summary>Ends the show. The next win starts a new sheet.</summary>
    public BuySheet Clear()
    {
        lock (_gate)
        {
            var lots = new List<WonLot>();
            Save(lots);
            return Compose(lots);
        }
    }

    // ── The row ───────────────────────────────────────────────────────────────

    /// <summary>
    /// One won lot, off the card built at the winning bid. Pure: every figure is copied, none is
    /// recomputed, and the two that are derived here — the overpay and the sentence — are made of
    /// nothing but what the card already decided.
    /// </summary>
    public static WonLot RowFrom(LiveBidCard card, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(card);

        // Priced means there is a resale side to count. A card that could not price the item still
        // records a real spend — dropping the row would make the sheet's total quietly smaller than
        // the seller's bank statement, which is the one way this screen could mislead about money.
        var priced = card.Call != LiveBidCalls.NoData && card.ResalePrice is > 0m;

        var row = new WonLot
        {
            Id = Guid.NewGuid().ToString("N"),
            Item = card.Item,
            CategoryLabel = card.CategoryLabel,
            WonAtUtc = nowUtc,

            WinningBid = card.CurrentBid,
            BuyerFeePercent = card.BuyerFeePercent,
            BuyerFee = card.BuyerFee,
            ShippingCost = card.ShippingCost,
            LandedCost = card.LandedCostNow,

            Priced = priced,
            ResalePrice = priced ? card.ResalePrice : null,
            ProjectedProfit = priced ? card.ProfitNow : null,
            ProjectedRoiPercent = priced ? card.RoiNow : null,
            DaysToCash = priced ? card.DaysToCash : null,

            Call = card.Call,
            CeilingAtWin = card.MaxBid,
            // Only against a ceiling that exists. A card with no ceiling at all — fees eat the whole
            // resale price — has no line to be over, and "$180 past it" would invite the reading
            // that some smaller bid was fine.
            PaidOverCeiling = card.MaxBid > 0m && card.CurrentBid > card.MaxBid
                ? Math.Round(card.CurrentBid - card.MaxBid, 2)
                : 0m,

            CompCount = card.CompCount,
            SellThroughRate = card.SellThroughUnbounded ? null : card.SellThroughRate,
            EvidenceTier = card.EvidenceTier,
        };

        row.Say = SayRow(row);
        return row;
    }

    /// <summary>
    /// One won lot in a sentence — the row's accessible label, and the only thing a screen reader
    /// needs to hear from a row of eight figures.
    /// </summary>
    /// <remarks>
    /// Every number rounds against the seller, exactly as <see cref="LiveBidSpeech"/> does: what was
    /// paid rounds up, what it is worth rounds down, what was overpaid rounds up. A line skimmed
    /// during a show must never be the optimistic version of the row it is summarising.
    /// </remarks>
    public static string SayRow(WonLot lot)
    {
        ArgumentNullException.ThrowIfNull(lot);

        var line = $"Won {lot.Item} at {Math.Ceiling(lot.WinningBid).ToString("C0")}";
        line += lot.LandedCost > lot.WinningBid
            ? $", {Math.Ceiling(lot.LandedCost).ToString("C0")} all in."
            : ".";

        if (!lot.Priced || lot.ResalePrice is not { } resale)
            return line + " Nothing on eBay priced it, so this is a cost with no resale figure behind it.";

        line += $" Resells around {Math.Floor(resale).ToString("C0")}";

        if (lot.ProjectedProfit is { } profit)
        {
            line += profit > 0m
                ? $" — {Math.Floor(profit).ToString("C0")} profit"
                : $" — {Math.Ceiling(-profit).ToString("C0")} down";
            if (lot.ProjectedRoiPercent is { } roi && profit > 0m) line += $", {Math.Floor(roi):0}% return";
        }

        line += ".";

        if (lot.PaidOverCeiling > 0m)
            line += $" Paid {Math.Ceiling(lot.PaidOverCeiling).ToString("C0")} over the ceiling the app gave.";

        return line;
    }

    // ── The totals ────────────────────────────────────────────────────────────

    /// <summary>
    /// The sheet, added up. Pure, and separate from the file so the arithmetic can be tested
    /// without one.
    /// </summary>
    public static BuySheet Compose(IEnumerable<WonLot>? lots)
    {
        var rows = (lots ?? []).OrderByDescending(l => l.WonAtUtc).ToList();

        var sheet = new BuySheet
        {
            Lots = rows,
            LotCount = rows.Count,
            Spent = Math.Round(rows.Sum(l => l.LandedCost), 2),
            PricedSpend = Math.Round(rows.Where(l => l.Priced).Sum(l => l.LandedCost), 2),
            ProjectedResale = Math.Round(rows.Sum(l => l.ResalePrice ?? 0m), 2),
            ProjectedProfit = Math.Round(rows.Sum(l => l.ProjectedProfit ?? 0m), 2),
            OverpaidCount = rows.Count(l => l.PaidOverCeiling > 0m),
            OverpaidBy = Math.Round(rows.Sum(l => l.PaidOverCeiling), 2),
            // Only the priced rows can be said to be losing. An unpriced lot is unknown, and
            // counting unknown as bad is how a screen teaches a seller to ignore it.
            LosingCount = rows.Count(l => l.Priced && (l.ProjectedProfit ?? 0m) <= 0m),
            UnpricedCount = rows.Count(l => !l.Priced),
            FirstWonUtc = rows.Count == 0 ? null : rows.Min(l => l.WonAtUtc),
            LastWonUtc = rows.Count == 0 ? null : rows.Max(l => l.WonAtUtc),
        };

        // A return on the money that has a resale figure behind it. Dividing by the whole spend
        // would report a lower return than the evidence supports and dress it up as caution.
        sheet.ProjectedRoiPercent = sheet.PricedSpend > 0m
            ? Math.Round(sheet.ProjectedProfit / sheet.PricedSpend * 100m, 1)
            : null;

        sheet.Say = Say(sheet);
        return sheet;
    }

    /// <summary>
    /// The night in one sentence. Same rules as the row: spend up, worth down, profit down,
    /// overpay up.
    /// </summary>
    public static string Say(BuySheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        if (sheet.LotCount == 0) return "";

        var line = $"{sheet.LotCount} lot{(sheet.LotCount == 1 ? "" : "s")} won, " +
                   $"{Math.Ceiling(sheet.Spent).ToString("C0")} spent.";

        if (sheet.PricedSpend > 0m)
        {
            line += $" Resells for about {Math.Floor(sheet.ProjectedResale).ToString("C0")}";
            line += sheet.ProjectedProfit >= 0m
                ? $" — {Math.Floor(sheet.ProjectedProfit).ToString("C0")} profit"
                : $" — {Math.Ceiling(-sheet.ProjectedProfit).ToString("C0")} down";
            if (sheet.ProjectedRoiPercent is { } roi && sheet.ProjectedProfit > 0m)
                line += $", {Math.Floor(roi):0}% return";
            line += ".";
        }

        // The discipline clauses come last and are never dropped for being unflattering — they are
        // the reason a seller reads this rather than the receipts.
        if (sheet.OverpaidCount > 0)
            line += $" {sheet.OverpaidCount} won above the ceiling, {Math.Ceiling(sheet.OverpaidBy).ToString("C0")} over in total.";

        if (sheet.LosingCount > 0)
            line += $" {sheet.LosingCount} priced to lose money.";

        if (sheet.UnpricedCount > 0)
            line += $" {sheet.UnpricedCount} with no resale figure.";

        return line;
    }

    // ── The file ──────────────────────────────────────────────────────────────

    private List<WonLot> Load()
    {
        if (_cache is not null) return _cache;

        var text = AtomicFile.ReadWithRecovery(_path);
        if (string.IsNullOrWhiteSpace(text)) return _cache = [];

        try
        {
            _cache = JsonSerializer.Deserialize<List<WonLot>>(text, Json) ?? [];
        }
        catch
        {
            // Unreadable after both the file and its backup. An empty sheet is the honest state to
            // start from — and nothing is deleted here, so the file is still on disk to look at.
            _cache = [];
        }

        // A row written by an older build, or hand-edited. The sentence is regenerated rather than
        // trusted, so the words on screen always match the numbers beside them.
        foreach (var row in _cache)
        {
            if (row.Id.Length == 0) row.Id = Guid.NewGuid().ToString("N");
            row.Say = SayRow(row);
        }

        return _cache;
    }

    private void Save(List<WonLot> lots)
    {
        _cache = lots;
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(lots, Json));
        }
        catch
        {
            // A locked or unwritable data folder must not cost the seller the answer on screen. The
            // sheet is still correct for this session; it is the next launch that will have lost it.
        }
    }
}
