using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The clock over a whole multi-source scan, as opposed to <see cref="LocalSupplyGuard"/>'s clock
/// over one source.
/// </summary>
/// <remarks>
/// <para>
/// The guard bounds each site individually and nothing bounded their sum. Sources are searched one
/// at a time on purpose — one of them drives a real browser, and running that alongside anything
/// else turns a slow scan into a stuck one — so the budgets add up rather than overlap. Six
/// registered sources is 45s + 45s + 45s + 45s + 180s + 45s of searching alone, before a single
/// sold-comp lookup, against a browser that gives up on the whole request at eight minutes.
/// </para>
/// <para>
/// When that happens the seller loses everything: the fetch aborts, and the forty Craigslist rows
/// that came back in four seconds go with it. That is the worst possible way for this feature to
/// fail — the cheap, reliable source paid for the expensive, unreliable one.
/// </para>
/// <para>
/// So the search half of a scan gets a budget of its own. Each source is still given its own
/// timeout, capped by whatever is left; when what's left is too small to be worth spending, the
/// remaining sources are not searched at all and say so. A board built from three of five sites and
/// labelled as such beats a spinner that ends in "no answer after 8 minutes".
/// </para>
/// <para>
/// Pure and hand-wound: <see cref="Spend"/> is called with what the last search actually took, so
/// the rules here are unit-testable without a clock. The caller owns the stopwatch.
/// </para>
/// </remarks>
public sealed class LocalSupplyScanBudget
{
    /// <summary>
    /// How long all the searching in one scan may take between them.
    /// </summary>
    /// <remarks>
    /// Five minutes of the browser's eight, leaving three for the pricing half — a sold-comp lookup
    /// per distinct product plus whatever Terapeak scrapes the budget allows. Deliberately larger
    /// than any single source's own budget, so a one-source scan is never cut short by this.
    /// </remarks>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The least remaining time worth handing a source.
    /// </summary>
    /// <remarks>
    /// Below this, searching is a formality: the source gets a few seconds, times out, and the
    /// seller waits out the shortfall to be told it failed. "Not searched — the scan was out of
    /// time" is the same information, arrives immediately, and is true rather than blaming the site.
    /// </remarks>
    public static readonly TimeSpan MinimumUsefulSlice = TimeSpan.FromSeconds(15);

    private readonly TimeSpan _budget;
    private TimeSpan _spent;

    public LocalSupplyScanBudget(TimeSpan? budget = null) => _budget = budget ?? DefaultBudget;

    public TimeSpan Remaining => _spent >= _budget ? TimeSpan.Zero : _budget - _spent;

    /// <summary>True once a source has been refused time — the scan is knowingly incomplete.</summary>
    public bool Exhausted { get; private set; }

    /// <summary>Records what a search actually cost. Called with the measured elapsed time.</summary>
    public void Spend(TimeSpan elapsed) => _spent += elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;

    /// <summary>
    /// The timeout to give <paramref name="source"/>, or null when there is no longer enough of the
    /// scan left to spend on it.
    /// </summary>
    /// <remarks>
    /// The source's own budget is the ceiling, never the floor: a Facebook search that would
    /// ordinarily get three minutes gets ninety seconds when that is all that remains. A source cut
    /// short that way reports the guard's ordinary timeout error, which is the honest description —
    /// it was asked, and it did not answer in the time there was.
    /// </remarks>
    public TimeSpan? Allow(ILocalSupplySource source)
    {
        var remaining = Remaining;
        if (remaining < MinimumUsefulSlice)
        {
            Exhausted = true;
            return null;
        }

        var wanted = LocalSupplyGuard.TimeoutFor(source);
        return wanted < remaining ? wanted : remaining;
    }

    /// <summary>
    /// The result stood in for a source the scan had no time left to search.
    /// </summary>
    /// <remarks>
    /// An <c>error</c> would be a lie about the site — it was never asked — and would put a red chip
    /// and a "blocked" reading on a source that is working perfectly. It is marked retryable because
    /// it genuinely is: a scan with fewer sites ticked, or simply run again, reaches it.
    /// </remarks>
    public static LocalSupplySearchResult Skipped(
        ILocalSupplySource source, string query, string zip, int radiusMiles) =>
        LocalSupplyGuard.Failure(
            source, LocalSupplyGuard.SkippedStatus, query, zip, radiusMiles,
            $"{source.Label} wasn't searched — the other sites used up the time for this scan. " +
            "Run it again with fewer sites ticked to reach it.",
            retryable: true);
}
