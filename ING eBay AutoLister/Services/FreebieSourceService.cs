using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Free in, cash out: the sourcing board for things that cost nothing.
///
/// Every other board in this app ranks by the gap between what an item costs and what it sells for.
/// This one goes after the case where the first number is zero, which is not a better deal on the
/// same axis — it is a different one. A free item cannot lose money, its ROI has no ceiling, and
/// the only question left is whether it is worth the trip and the packing tape.
///
/// It is an <see cref="ILocalSupplySource"/> and nothing more. Grouping, comp lookups, the profit
/// maths, the ranking, the Deal Pipeline and the Budget Optimizer were all written against
/// <c>LocalSupplyListing</c>, so free supply lands in the same ranked table beside the Craigslist
/// and clearance rows without a single line downstream learning that it exists.
///
/// Two legs, because free supply lives in two very different places:
///   • <b>The local free board.</b> Craigslist's free-stuff category — read through
///     <see cref="CraigslistService"/> rather than a second copy of it, so the headers, the block
///     detection, the RSS fallback and the search budget are all the ones already trusted.
///   • <b>Free after coupon or rebate.</b> Public Slickdeals RSS, read through the same
///     <see cref="DealFeedParser"/> the Deal Scanner uses.
///
/// What is different from every other source, and is modelled rather than hidden:
///   • <b>"Free" is usually a lie.</b> Free shipping, free gift with purchase, free trial, free
///     credit. <see cref="FreebieClassifier"/> exists to refuse all of it — see its remarks.
///   • <b>Free is not always free.</b> A rebate is fronted money that may never come back, and its
///     sales tax never does. See <see cref="FreebiePricer"/>.
///   • <b>It expires.</b> Faster than anything else this app ranks. Every row carries a deadline or
///     says it doesn't have one.
///
/// Posture, matching CraigslistService and DealFeedService exactly: user-driven only, one request
/// per slice per click, nothing scheduled, nothing crawled, nothing stored, no account, no key.
/// </summary>
public sealed class FreebieSourceService(
    IHttpClientFactory httpFactory, CraigslistService craigslist, ActionLog log) : ILocalSupplySource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);

    // The whole scan, both legs together. Bounded well inside LocalSupplyGuard.PublicSourceTimeout
    // so a slow feed costs the seller the feeds after it, never the freebies already in hand.
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(35);

    private const int MaxFeedBytes = 8 * 1024 * 1024;

    public string Id => FreebieCatalog.SourceId;
    public string Label => FreebieCatalog.SourceLabel;
    public bool RequiresConnection => false;
    public bool IsAvailable => true;

    // Half of this source is a board in the seller's own metro, so the zip and radius are real —
    // they simply do nothing for the other half, which the scope label says out loud.
    public bool IsLocationBased => true;

    // A rebate deal is rung up at a till and taxed on the full price; the refund never covers the
    // tax. A curb pickup off a stranger is taxed at nothing. FreebiePricer tells them apart per row.
    public bool ChargesSalesTax => true;

    // The one board where an empty search is the best search there is.
    public bool AllowsBlankQuery => true;

    public IReadOnlyList<LocalSupplyManualSite> ManualSites =>
        FreebieCatalog.ManualSites
            .Select(s => new LocalSupplyManualSite { Id = s.Id, Label = s.Label, UrlTemplate = s.UrlTemplate, Note = s.Note })
            .ToList();

    public string AvailabilityNote =>
        "Craigslist's free-stuff board near you, plus free-after-coupon and free-after-rebate deals " +
        "nationwide. No login. Leave the keyword blank to see everything being given away near you — " +
        "these go fast, so every row shows how long you have.";

    /// <summary>
    /// One user-initiated scan across both legs. A blank <paramref name="query"/> is allowed and
    /// means "everything free near me"; a blank <paramref name="zip"/> simply skips the local leg
    /// and says so, rather than failing a scan the deal feeds could still answer.
    /// </summary>
    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var listings = new List<LocalSupplyListing>();
        var notes = new List<string>();
        var retryable = false;
        var siteLabel = "";

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ScanBudget);

        // ── Leg 1: the local free board ─────────────────────────────────────────────────────────
        // First because it is the best supply and the cheapest request: one GET, no filtering, and
        // everything on it is genuinely $0 to a seller who can drive.
        var site = CraigslistSites.Resolve(zip);
        if (site is null)
        {
            notes.Add("no zip code, so the local free-stuff board wasn't searched — only the nationwide deals were");
        }
        else
        {
            siteLabel = site.Label;
            var local = await craigslist.SearchCategoryAsync(
                FreebieCatalog.CraigslistFreeCategory, query ?? "", zip ?? "", radiusMiles,
                siteId: null, ct: budget.Token, freeBoard: true);

            if (local.Status == "ok")
            {
                listings.AddRange(KeepFreebies(local.Items, fromFreeBoard: true, now));
            }
            else
            {
                notes.Add($"the local free-stuff board couldn't be read ({local.Error})");
                retryable |= local.Retryable;
            }
        }

        // ── Leg 2: free after coupon / rebate, nationwide ───────────────────────────────────────
        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;
        PublicFeedHttp.ApplyBrowserHeaders(http);

        var perFeed = new List<(DealFeed Feed, List<LocalSupplyListing> Listings)>();
        var failures = new List<string>();
        var skipped = 0;

        foreach (var feed in FreebieCatalog.Feeds)
        {
            // Out of time. The feeds already read are real results and are returned; saying how many
            // were skipped beats dropping them silently or failing a scan over the slowest site.
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                skipped = FreebieCatalog.Feeds.Count - perFeed.Count - failures.Count;
                break;
            }

            var url = FreebieCatalog.BuildUrl(feed, query);
            var (xml, error, feedRetryable) = await PublicFeedHttp.GetAsync(
                http, url, feed.Site, nameof(FreebieCatalog), MaxFeedBytes, budget.Token, ct);

            if (xml is null)
            {
                failures.Add($"{feed.Label}: {error}");
                retryable |= feedRetryable;
                continue;
            }

            // requirePrice: false is the whole difference from the Deal Scanner's read of the same
            // feeds. A free item has no price, and dropping every priceless entry would drop exactly
            // the ones this board exists to find.
            var parsed = DealFeedParser.ParseFeed(xml, feed, now, requirePrice: false);
            perFeed.Add((feed, KeepFreebies(parsed, fromFreeBoard: false, now)));
        }

        if (failures.Count > 0)
            notes.Add($"{failures.Count} of {FreebieCatalog.Feeds.Count} deal feeds couldn't be read ({string.Join(" · ", failures)})");
        if (skipped > 0)
            notes.Add($"{skipped} feed(s) skipped — the scan ran out of time");

        // Nothing answered at all. That is a failure worth reporting as one: there is no ranking to
        // put a warning beside.
        if (listings.Count == 0 && perFeed.Count == 0)
        {
            return Fail(query, zip, radiusMiles, siteLabel,
                notes.Count > 0
                    ? $"Nothing could be searched for free items. {string.Join(". ", notes)}."
                    : "Nothing could be searched for free items.",
                retryable);
        }

        var result = BuildResult(listings, perFeed, query, siteLabel);
        result.ZipCode = zip ?? "";
        result.RadiusMiles = radiusMiles;
        result.Retryable = retryable;
        // Partial success, said plainly beside real results rather than instead of them.
        if (notes.Count > 0) result.Error = string.Join(". ", notes) + ".";

        log.Add("Info", "Freebie scan",
            $"\"{query}\"{(siteLabel.Length > 0 ? $" — {siteLabel} free board" : "")} + " +
            $"{perFeed.Count}/{FreebieCatalog.Feeds.Count} feed(s); {result.Count} free item(s) kept" +
            $"{(failures.Count > 0 ? $"; failed: {string.Join(" · ", failures)}" : "")}.");

        return result;
    }

    /// <summary>
    /// Runs every candidate past <see cref="FreebieClassifier"/> and keeps only the ones that are
    /// actually a free, sellable, single object — rewriting the survivors onto this source so they
    /// arrive at the board as freebies rather than as whichever site happened to publish them.
    /// </summary>
    public static List<LocalSupplyListing> KeepFreebies(
        IEnumerable<LocalSupplyListing> candidates, bool fromFreeBoard, DateTime nowUtc)
    {
        var kept = new List<LocalSupplyListing>();

        foreach (var listing in candidates)
        {
            var details = FreebieClassifier.Classify(
                listing.Title, listing.PriceText, listing.Price, fromFreeBoard, nowUtc);
            if (details is null) continue;

            // The comp lookup has to see the product, not the offer wrapped around it.
            var title = FreebieClassifier.CleanTitle(listing.Title);
            if (title.Length == 0) continue;

            listing.Title = title;
            listing.Freebie = details;

            // The board's Source is what the badge, the dedupe key and the round-robin cap all use.
            // A free craigslist post found by this scan is a freebie row, not a second craigslist
            // row — the two boards search different categories and answer different questions.
            listing.Source = FreebieCatalog.SourceId;
            listing.SourceLabel = fromFreeBoard ? "Free — local pickup" : listing.SourceLabel;

            // The price the pipeline sees is the sticker, and it is zero on everything except a
            // near-free item. What the row actually COSTS is FreebiePricer's answer, and the
            // analyzer reads it from Freebie rather than from here — see LocalArbitrageAnalyzer.
            listing.Price = details.Kind == FreebieKinds.NearFree ? details.ListPrice : 0m;
            listing.PriceText = details.KindLabel;
            listing.IsFree = details.Kind != FreebieKinds.NearFree;

            // A rebate or coupon deal is bought from a retailer; a curb pickup is not. IsRetail is
            // left alone on the feed rows (DealFeedParser set it) and cleared on the local ones.
            if (fromFreeBoard)
            {
                listing.IsRetail = false;
                listing.Retailer = "";
            }

            kept.Add(listing);
        }

        return kept;
    }

    /// <summary>
    /// Assembles the search result. The query filter is applied exactly where the Deal Scanner
    /// applies it — leniently to a slice the site itself searched, strictly to a firehose — with one
    /// deliberate exception: the local free board is never filtered strictly, because a seller who
    /// asked for "desk" and is shown a free filing cabinet two miles away has been done a favour,
    /// not shown a false match.
    /// </summary>
    public static LocalSupplySearchResult BuildResult(
        List<LocalSupplyListing> localFree,
        IEnumerable<(DealFeed Feed, List<LocalSupplyListing> Listings)> perFeed,
        string? query, string siteLabel)
    {
        var searched = perFeed.Where(f => f.Feed.IsKeywordSearch).SelectMany(f => f.Listings).ToList();
        var browsed = perFeed.Where(f => !f.Feed.IsKeywordSearch)
            .SelectMany(f => f.Listings.Where(l => DealFeedParser.MatchesQuery(l, query)));

        var items = LocalSupplyResults.FilterByRelevance(localFree, query ?? "")
            .Concat(LocalSupplyResults.FilterByRelevance(searched, query ?? ""))
            .Concat(browsed)
            .ToList();

        items = DealFeedParser.DedupeAcrossFeeds(LocalSupplyResults.Dedupe(items));

        // Cheapest first, and on this board that means the outright freebies lead — which is also
        // the order the analysis cap is spent in when a scan finds more than it can price.
        items = [.. items.OrderBy(i => i.Price ?? 0m)];

        var (min, median, max) = LocalSupplyResults.Summarize(items);

        return new LocalSupplySearchResult
        {
            SourceId = FreebieCatalog.SourceId,
            SourceLabel = FreebieCatalog.SourceLabel,
            Status = "ok",
            Query = query ?? "",
            SearchUrl = FreebieCatalog.SearchPageUrl(query),
            ScopeLabel = FreebieCatalog.ScopeLabel(siteLabel),
            Items = items,
            Min = min, Median = median, Max = max,
        };
    }

    private static LocalSupplySearchResult Fail(
        string? query, string? zip, int radius, string siteLabel, string error, bool retryable) => new()
    {
        SourceId = FreebieCatalog.SourceId,
        SourceLabel = FreebieCatalog.SourceLabel,
        // Public boards: no session to lose, so this only ever fails as "error".
        Status = "error",
        Query = query ?? "",
        ZipCode = zip ?? "",
        RadiusMiles = radius,
        SearchUrl = FreebieCatalog.SearchPageUrl(query),
        ScopeLabel = FreebieCatalog.ScopeLabel(siteLabel),
        Error = error,
        Retryable = retryable,
    };
}
