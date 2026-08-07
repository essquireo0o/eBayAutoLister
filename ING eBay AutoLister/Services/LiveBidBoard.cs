using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The comps behind a live-auction card, held for as long as the lot is on screen, so that moving
/// the bid re-answers <b>instantly</b> instead of re-reading eBay.
/// </summary>
/// <remarks>
/// <para>
/// A live auction moves faster than the thing it is being priced against. The bid climbs every two
/// or three seconds; the sold history it is measured against was written over the last two months
/// and does not change between one bid and the next. Re-running the comp lookup for every "it's at
/// $45 now" spends the seconds the seller does not have, to arrive at the same resale price it
/// already had.
/// </para>
/// <para>
/// So the expensive half is held and the cheap half is repeated. What is stored is the
/// <see cref="MarketAnalysisResult"/> exactly as the pipeline produced it — not a card, not a
/// ceiling, not a conclusion — and a re-price is
/// <see cref="LiveBidAdvisor.Build"/> run again over that same analysis with the new bid. That
/// matters more than the speed: there is still one function in this app that decides a maximum bid,
/// and a re-priced card is not a cheaper approximation of a fresh one, it is the same computation.
/// </para>
/// <para>
/// Held quotes are a cache of somebody else's data with a shelf life, so they are bounded two ways
/// and honest about both: <see cref="Capacity"/> of them, and <see cref="HoldFor"/> to live. Past
/// either, the token stops resolving and the screen is told to read eBay again rather than being
/// quietly served a stale price.
/// </para>
/// </remarks>
public sealed class LiveBidBoard
{
    /// <summary>
    /// How many lots are kept. A live sale runs a queue, and the seller flicks back to the one two
    /// lots ago as often as they price the one on screen; a dozen covers a stream without turning
    /// this into somewhere market data lives.
    /// </summary>
    public const int Capacity = 12;

    /// <summary>
    /// How long a held quote stays usable. Long enough to outlast a lot and the haggling after it,
    /// short enough that "instant" never quietly means "half an hour old". The card prints the age
    /// on every re-price, so this is the point at which it stops being shown at all rather than the
    /// point at which it starts being worth mentioning.
    /// </summary>
    public static readonly TimeSpan HoldFor = TimeSpan.FromMinutes(20);

    private readonly Dictionary<string, LiveBidQuote> _held = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>How many quotes are currently held. For the tests and the log line, not the screen.</summary>
    public int Count
    {
        get { lock (_gate) return _held.Count; }
    }

    /// <summary>
    /// Keeps the comps behind one priced item and hands back the token that re-prices it.
    /// </summary>
    /// <param name="own">
    /// The seller's own history with this product, held for the same reason the comps are: it is
    /// evidence about the seller rather than about the auction, so it cannot change while a lot is
    /// on screen, and re-reading two SQLite tables for every keystroke of a climbing bid spends the
    /// seconds this whole screen exists to save.
    /// </param>
    public LiveBidQuote Hold(
        string item, MarketAnalysisResult analysis, ResaleCategory? category,
        DateTime? nowUtc = null, OwnSalesEvidence? own = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var quote = new LiveBidQuote
        {
            Token = Guid.NewGuid().ToString("N"),
            Item = item,
            Analysis = analysis,
            Category = category,
            Own = own,
            PricedAtUtc = now,
        };

        lock (_gate)
        {
            Prune(now);
            _held[quote.Token] = quote;

            // Oldest out first. The lot being bid on right now is the one that must survive, and it
            // is always the newest.
            while (_held.Count > Capacity)
            {
                var oldest = _held.OrderBy(p => p.Value.PricedAtUtc).First().Key;
                _held.Remove(oldest);
            }
        }

        return quote;
    }

    /// <summary>
    /// The held quote for a token, or null when there is none — expired, evicted, never issued, or
    /// simply not sent. Null is not an error here; it is the instruction to read eBay again.
    /// </summary>
    public LiveBidQuote? Find(string? token, DateTime? nowUtc = null)
    {
        var key = (token ?? "").Trim();
        if (key.Length == 0) return null;

        var now = nowUtc ?? DateTime.UtcNow;
        lock (_gate)
        {
            Prune(now);
            return _held.TryGetValue(key, out var quote) && !quote.HasExpired(now) ? quote : null;
        }
    }

    /// <summary>Drops a held quote — the item on screen has changed, so its comps are answers to a
    /// question nobody is asking any more.</summary>
    public bool Release(string? token)
    {
        var key = (token ?? "").Trim();
        if (key.Length == 0) return false;
        lock (_gate) return _held.Remove(key);
    }

    /// <summary>Called under the lock, on every touch — a cache nobody sweeps is a cache that serves
    /// a two-hour-old price at three in the morning.</summary>
    private void Prune(DateTime nowUtc)
    {
        if (_held.Count == 0) return;
        foreach (var key in _held.Where(p => p.Value.HasExpired(nowUtc)).Select(p => p.Key).ToList())
            _held.Remove(key);
    }
}

/// <summary>One lot's comps, held between bids. See <see cref="LiveBidBoard"/>.</summary>
public sealed class LiveBidQuote
{
    public string Token { get; init; } = "";

    /// <summary>What was priced. Held so a re-price cannot be pointed at a different item — the
    /// token buys you a faster answer about <i>this</i> lot, not a free answer about another one.</summary>
    public string Item { get; init; } = "";

    /// <summary>The comps, exactly as the pipeline produced them. Never edited here.</summary>
    public MarketAnalysisResult Analysis { get; init; } = null!;

    /// <summary>The classification the fresh price ran under, kept so a re-price cannot land in a
    /// different category and quietly change what the item is.</summary>
    public ResaleCategory? Category { get; init; }

    /// <summary>
    /// The seller's own sales of this product, as they stood when the lot was priced. Held so a
    /// re-price keeps saying what the seller's own record says, without re-reading their book on
    /// every keystroke. Null when nothing read it.
    /// </summary>
    public OwnSalesEvidence? Own { get; init; }

    /// <summary>When the comps were read — the age printed on every re-priced card.</summary>
    public DateTime PricedAtUtc { get; init; }

    public bool HasExpired(DateTime nowUtc) => nowUtc - PricedAtUtc >= LiveBidBoard.HoldFor;

    /// <summary>How long ago the comps were read, in whole seconds, never negative.</summary>
    public int AgeSeconds(DateTime nowUtc) => (int)Math.Max(0d, (nowUtc - PricedAtUtc).TotalSeconds);
}
