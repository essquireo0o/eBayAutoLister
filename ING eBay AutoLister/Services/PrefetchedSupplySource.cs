using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// An <see cref="ILocalSupplySource"/> over listings that have already been fetched.
/// </summary>
/// <remarks>
/// <para>
/// Today's Picks is Facebook's own feed for this seller — it is browsed, not searched, so it never
/// went through the arbitrage pipeline and arrived on screen as a wall of photos with an asking
/// price and nothing else. "$600 Segway gokart pro2" is not information: the seller still has to
/// leave the app, look up what one sells for, and work out whether $600 is a buy.
/// </para>
/// <para>
/// Everything needed to answer that already exists — the comps matcher, the price estimator, the
/// fee model, the profit calculator, the sell-through calculator — wired together inside the scan
/// pipeline, which takes sources rather than listings. Wrapping the already-fetched feed as a
/// source lets the picks run down that identical path instead of growing a second pricing
/// implementation that would drift from the first.
/// </para>
/// </remarks>
public sealed class PrefetchedSupplySource(LocalSupplySearchResult prefetched) : ILocalSupplySource
{
    public string Id => prefetched.SourceId;
    public string Label => prefetched.SourceLabel;

    // The fetch already happened, so there is nothing left to connect to or be refused by.
    public bool RequiresConnection => false;
    public bool IsAvailable => true;
    public string AvailabilityNote => "";

    /// <summary>
    /// Returns the listings as fetched, whatever is asked for.
    /// </summary>
    /// <remarks>
    /// The query, zip and radius are ignored on purpose: this feed was assembled by Facebook for
    /// this seller and cannot be re-filtered after the fact. Pretending otherwise — quietly
    /// returning the same rows while the UI claims they were narrowed to a keyword — would be the
    /// dishonest version of this class.
    /// </remarks>
    public Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default) =>
        Task.FromResult(prefetched);
}
