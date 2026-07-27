using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Retail arbitrage: buy it new on clearance, sell it on eBay.
///
/// The other sourcing screens all answer "what is for sale near me?". This one answers "what is
/// discounted right now, anywhere?" — Slickdeals, DealNews and TechBargains publish exactly that as
/// public RSS, and every entry gets priced against the same sold-comps database, through the same
/// <see cref="LocalArbitrageAnalyzer"/>, and lands in the same ranked table beside the Craigslist
/// and Facebook rows. It is the only sourcing board in this app that works for a seller whose local
/// market is empty, which for most sellers most weeks it is.
///
/// It is an <see cref="ILocalSupplySource"/> and nothing more. Grouping, comp lookups, the profit
/// maths and the ranking were already written against <c>LocalSupplyListing</c>, so nothing
/// downstream had to learn that retail supply exists — the same reason Craigslist was a hundred
/// lines when Facebook had been four hundred.
///
/// What is different from the local sources, and is modelled rather than hidden:
///   • <b>Nowhere near you.</b> Zip and radius mean nothing to a nationwide feed, so
///     <see cref="IsLocationBased"/> is false and the UI stops promising a radius it didn't search.
///   • <b>Sales tax.</b> Retail charges it and a stranger with a drill doesn't — see
///     <see cref="RetailBuyCosts"/>, without which every row here would overstate its profit.
///   • <b>No haggling.</b> Walmart does not take an offer, so these rows carry no negotiation plan
///     instead of a drafted message nobody can send.
///
/// Posture, matching CraigslistService exactly:
///   • User-driven only. Nothing scheduled, nothing crawled, nothing stored, no account, no key.
///   • One click is one GET per feed for the seller's own query — the feeds these sites publish
///     for the purpose, not their pages.
///   • A feed that fails fails alone: the scan reports which one and returns everything the others
///     found, and never retries past a refusal.
/// </summary>
public sealed class DealFeedService(IHttpClientFactory httpFactory, ActionLog log) : ILocalSupplySource
{
    // One feed. These are static XML files behind a CDN; a request near this is one that isn't
    // going to complete.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    // The whole scan, all feeds together. Bounded well inside LocalSupplyGuard.PublicSourceTimeout
    // so a slow feed costs the seller the feeds after it, never the results already in hand.
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(35);

    // TechBargains publishes hundreds of deals in one document. Generous, but bounded: an endpoint
    // that starts streaming without end must not be able to exhaust this process's memory.
    private const int MaxFeedBytes = 8 * 1024 * 1024;

    public string Id => DealFeedCatalog.SourceId;
    public string Label => DealFeedCatalog.SourceLabel;
    public bool RequiresConnection => false;
    public bool IsAvailable => true;
    public bool IsLocationBased => false;

    public string AvailabilityNote =>
        "Public RSS from Slickdeals, DealNews and TechBargains — no login, nationwide (zip and radius don't apply).";

    /// <summary>
    /// One user-initiated scan. <paramref name="zip"/> and <paramref name="radiusMiles"/> are
    /// accepted and echoed back so the merged result stays coherent beside the local sources, but
    /// they cannot filter a nationwide feed and nothing here pretends otherwise.
    /// </summary>
    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Fail(query, zip, radiusMiles, "Enter something to look for — the deal feeds carry everything, so a scan needs a keyword.");

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;
        PublicFeedHttp.ApplyBrowserHeaders(http);

        // The scan's own deadline, so six feeds can't add up to six times what one is allowed. The
        // caller's token is linked in and the two are told apart below: a seller who navigated away
        // and a feed that stopped answering deserve different handling.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ScanBudget);

        var perFeed = new List<(DealFeed Feed, List<LocalSupplyListing> Listings)>();
        var failures = new List<string>();
        var retryable = false;
        var skipped = 0;

        foreach (var feed in DealFeedCatalog.Feeds)
        {
            // Out of time. The feeds already read are real results and are returned; saying how
            // many were skipped is more useful than either dropping them silently or failing the
            // whole scan over the slowest site in the list.
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                skipped = DealFeedCatalog.Feeds.Count - perFeed.Count - failures.Count;
                break;
            }

            var url = DealFeedCatalog.BuildUrl(feed, query);
            var (xml, error, feedRetryable) = await PublicFeedHttp.GetAsync(
                http, url, feed.Site, nameof(DealFeedCatalog), MaxFeedBytes, budget.Token, ct);

            if (xml is null)
            {
                failures.Add($"{feed.Label}: {error}");
                retryable |= feedRetryable;
                continue;
            }

            perFeed.Add((feed, DealFeedParser.ParseFeed(xml, feed)));
        }

        // Every feed refused. That is a failure worth reporting as one — there is nothing to show
        // and no ranking to put a warning next to.
        if (perFeed.Count == 0)
        {
            return Fail(query, zip, radiusMiles,
                failures.Count > 0
                    ? $"None of the deal feeds could be read. {string.Join(" · ", failures)}"
                    : "None of the deal feeds could be read.",
                retryable);
        }

        var result = DealFeedParser.BuildResult(perFeed, query);
        result.ZipCode = zip ?? "";
        result.RadiusMiles = radiusMiles;
        result.Retryable = retryable;

        // Partial success, said plainly beside real results rather than instead of them.
        var notes = new List<string>();
        if (failures.Count > 0) notes.Add($"{failures.Count} of {DealFeedCatalog.Feeds.Count} feeds couldn't be read ({string.Join(" · ", failures)})");
        if (skipped > 0) notes.Add($"{skipped} feed(s) skipped — the scan ran out of time");
        if (notes.Count > 0) result.Error = string.Join(". ", notes) + ".";

        log.Add("Info", "Deal feed scan",
            $"\"{query}\" across {perFeed.Count}/{DealFeedCatalog.Feeds.Count} feed(s) — " +
            $"{perFeed.Sum(f => f.Listings.Count)} deal(s) parsed, {result.Count} matched the query" +
            $"{(failures.Count > 0 ? $"; failed: {string.Join(" · ", failures)}" : "")}.");

        return result;
    }

    private static LocalSupplySearchResult Fail(
        string query, string? zip, int radius, string error, bool retryable = false) => new()
    {
        SourceId = DealFeedCatalog.SourceId,
        SourceLabel = DealFeedCatalog.SourceLabel,
        // Public feeds: no session to lose, so this only ever fails as "error" — never
        // not_connected or session_expired.
        Status = "error",
        Query = query,
        ZipCode = zip ?? "",
        RadiusMiles = radius,
        SearchUrl = DealFeedCatalog.SearchPageUrl(query),
        ScopeLabel = DealFeedCatalog.ScopeLabel,
        Error = error,
        Retryable = retryable,
    };
}
