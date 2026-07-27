using System.Collections.Concurrent;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Finds the public promo codes and cashback offers for one store, so the buy price on the
/// arbitrage board can be cut before the profit is computed against it.
///
/// A dollar taken off the buy is worth more than a dollar added to the sale: eBay takes none of it,
/// nothing has to ship for it, and it lands today. This is the cheapest such dollar there is —
/// the codes are already published, for free, by the same aggregators this app already reads for
/// clearance (<see cref="DealFeedService"/>).
///
/// Posture, matching every other public source in this app:
///   • User-driven only. Nothing scheduled, nothing crawled, nothing stored, no account, no key.
///   • One click is at most one GET per list per store, and never more than
///     <see cref="MaxStoresPerScan"/> stores — a board of thirty retail rows is six lookups, not
///     thirty.
///   • A list that fails fails alone: the lookup reports which one and returns what the others
///     found, and never retries past a refusal.
///   • Answers are cached for <see cref="CacheTtl"/>, because a seller re-running a scan with a
///     different keyword must not re-read Slickdeals' coupon feed for the same store a minute later.
/// </summary>
public sealed class CouponService(IHttpClientFactory httpFactory, ActionLog log)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>All lists for one store together. Bounded so a slow list costs the lists after it.</summary>
    private static readonly TimeSpan LookupBudget = TimeSpan.FromSeconds(20);

    private const int MaxFeedBytes = 4 * 1024 * 1024;

    /// <summary>
    /// How long an answer stays good. Codes are published for weeks and change on nobody's clock,
    /// so re-reading the same store's lists inside half an hour would spend requests to learn
    /// nothing — and the sourcing screens are used in bursts of scans.
    /// </summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// The most stores one scan will look up. A board mixing Amazon, Walmart, Newegg and four
    /// others is the normal case; beyond this the lookups cost more time than the codes save, so
    /// the stores carrying the most money on the board are checked and the rest are named as
    /// unchecked rather than silently skipped.
    /// </summary>
    public const int MaxStoresPerScan = 6;

    /// <summary>The most offers kept per store. Past this the list is noise beside a ranked table.</summary>
    private const int MaxOffersPerStore = 12;

    // Small, bounded and per-process: keyed by merchant id, holding the same result object the
    // caller gets. Nothing here is written to disk — a promo code is somebody else's publication,
    // not this app's data.
    private readonly ConcurrentDictionary<string, (DateTime FetchedUtc, CouponLookupResult Result)> _cache = new();

    /// <summary>
    /// Every public offer this app can find for one store. Never throws for a site's benefit: a
    /// blocked list, a moved feed and a timeout all arrive as a status the UI can render beside
    /// whatever else was found. Only the caller's own cancellation propagates.
    /// </summary>
    public async Task<CouponLookupResult> LookupAsync(string? store, CancellationToken ct = default)
    {
        var merchant = CouponCatalog.Resolve(store);
        if (merchant is null)
        {
            return new CouponLookupResult
            {
                Status = "no_codes", Query = store ?? "", CheckedUtc = DateTime.UtcNow,
                Error = "Name a store to look for codes at.",
            };
        }

        if (_cache.TryGetValue(merchant.Id, out var cached) && DateTime.UtcNow - cached.FetchedUtc < CacheTtl)
            return cached.Result;

        var result = await FetchAsync(merchant, store ?? "", ct);
        // Only successful reads are cached. Caching a failure would hold a red status over a store
        // for half an hour after the site came back.
        if (result.Status != "error") _cache[merchant.Id] = (DateTime.UtcNow, result);

        return result;
    }

    private async Task<CouponLookupResult> FetchAsync(CouponMerchant merchant, string query, CancellationToken ct)
    {
        var result = new CouponLookupResult
        {
            Query = query,
            MerchantId = merchant.Id,
            MerchantLabel = merchant.Label,
            MerchantKnown = merchant.Known,
            MerchantNote = merchant.CodesRare ? merchant.Note : null,
            ManualSites = CouponCatalog.ManualSitesFor(merchant),
            CheckedUtc = DateTime.UtcNow,
        };

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;
        PublicFeedHttp.ApplyBrowserHeaders(http);

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(LookupBudget);

        var offers = new List<CouponOffer>();
        var read = 0;

        foreach (var feed in CouponCatalog.Feeds)
        {
            // Out of time. The lists already read are real answers and are returned; the ones after
            // them are reported as unread rather than dropped silently.
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                result.Sources.Add(new CouponSourceOutcome
                {
                    Id = feed.Id, Label = feed.Label, Status = "error",
                    Error = "skipped — the lookup ran out of time", Retryable = true,
                });
                continue;
            }

            // Searched under the store's own name, which for an uncatalogued store is whatever the
            // deal said — a store nobody catalogued still has codes published for it.
            var url = CouponCatalog.BuildUrl(feed, merchant.Label);
            var (xml, error, retryable) = await PublicFeedHttp.GetAsync(
                http, url, feed.Site, nameof(CouponCatalog), MaxFeedBytes, budget.Token, ct);

            if (xml is null)
            {
                result.Sources.Add(new CouponSourceOutcome
                {
                    Id = feed.Id, Label = feed.Label, Status = "error", Error = error, Retryable = retryable,
                });
                result.Retryable |= retryable;
                continue;
            }

            read++;
            var parsed = CouponParser.ParseFeed(xml, feed, merchant);
            offers.AddRange(parsed);
            result.Sources.Add(new CouponSourceOutcome
            {
                Id = feed.Id, Label = feed.Label, Status = "ok", Count = parsed.Count,
            });
        }

        result.Offers = Rank(Dedupe(offers));

        // Every list refused. Worth reporting as a failure rather than as "this store has no codes",
        // which is a different and much more actionable answer.
        if (read == 0)
        {
            result.Status = "error";
            result.Error = "None of the coupon lists could be read right now. " +
                           string.Join(" · ", result.Sources.Select(s => $"{s.Label}: {s.Error}"));
            return result;
        }

        result.Status = result.Offers.Count > 0 ? "ok" : "no_codes";

        log.Add("Info", "Coupon lookup",
            $"{merchant.Label}: {result.Offers.Count} offer(s) from {read}/{CouponCatalog.Feeds.Count} list(s)" +
            $"{(merchant.Known ? "" : " (store not in the catalogue — searched under its own name)")}.");

        return result;
    }

    /// <summary>
    /// The same code published on three lists is one code. Keyed on what the seller would actually
    /// type plus what it does, so "SAVE20" for 20% off and "SAVE20" for $20 off stay apart — they
    /// are different offers and one of them is a misparse.
    /// </summary>
    public static List<CouponOffer> Dedupe(IEnumerable<CouponOffer> offers)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<CouponOffer>();

        foreach (var offer in offers)
        {
            var key = $"{offer.Kind}|{offer.Code}|{offer.Value:0.##}|{offer.MinSpend:0.##}";
            if (seen.Add(key)) kept.Add(offer);
        }

        return kept;
    }

    /// <summary>
    /// Best first: what is worth believing, then what is worth most. Confidence leads because a
    /// bigger discount nobody can use is worth less than a smaller one that works — and because the
    /// top of this list is what <see cref="CouponStacker"/> ends up costing the buy against.
    /// </summary>
    public static List<CouponOffer> Rank(IEnumerable<CouponOffer> offers) =>
        offers
            .OrderByDescending(o => CouponConfidence.Rank(o.Confidence))
            .ThenByDescending(o => o.Code.Length > 0)
            .ThenByDescending(o => o.Kind == CouponKinds.PercentOff ? o.Value : 0m)
            .ThenByDescending(o => o.Value)
            .ThenBy(o => o.MinSpend)
            .Take(MaxOffersPerStore)
            .ToList();
}
