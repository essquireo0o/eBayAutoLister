using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Local sourcing from craigslist — the easy source, and for most sellers the first one worth
/// running.
///
/// Craigslist publishes its search results publicly: no account, no login, no saved browser
/// session, no Playwright, no cookies. A search is one plain HTTPS GET, which is why this whole
/// service is a hundred lines against FacebookMarketplaceService's four hundred.
///
/// What it reads, and why in that order — both were checked against the live site:
///   • The search page's <b>static results list</b> is the primary source. Craigslist renders its
///     results twice: a JavaScript grid, and an <c>ol.cl-static-search-results</c> list emitted
///     server-side and hidden by CSS for clients that do run JavaScript. That list is complete
///     (~95 posts for a normal query), needs no browser, and honours <c>postal</c> +
///     <c>search_distance</c>.
///   • The <b>RSS feed</b> (<c>&amp;format=rss</c>) is tried only if that returns nothing.
///     Craigslist now answers 403 to the feed on its current search stack regardless of user
///     agent, so it can't be the primary path any more — but it's one cheap request on an
///     otherwise-empty search, and it still works on some boards.
///
/// Posture, matching the rest of the local-sourcing code:
///   • User-driven only. Nothing here is scheduled or runs as a side effect of another feature.
///   • One search is one request (two if the first found nothing) for the seller's own query. No
///     crawling, no paging, no following into post pages, nothing stored.
///   • Craigslist's own <c>postal</c> + <c>search_distance</c> parameters do the radius filtering
///     server-side, so a search asks for exactly what it needs rather than filtering a wider pull.
///   • A 403 or a rate-limit is reported as what it is. Retrying past it is how an IP gets blocked.
/// </summary>
public sealed class CraigslistService(IHttpClientFactory httpFactory, ActionLog log) : ILocalSupplySource
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    public string Id => CraigslistParser.SourceId;
    public string Label => CraigslistParser.SourceLabel;
    // The whole point of this source: nothing to connect, nothing to log into.
    public bool RequiresConnection => false;
    public bool IsAvailable => true;
    public string AvailabilityNote => "Public search — no login needed.";

    public Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default) =>
        SearchAsync(query, zip, radiusMiles, siteId: null, ct);

    /// <summary>
    /// Searches one craigslist regional site. <paramref name="siteId"/> overrides the site the ZIP
    /// resolves to — craigslist is organised by metro, and a seller on a boundary knows their own
    /// board better than a prefix table does (see CraigslistSites).
    /// </summary>
    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, string? siteId, CancellationToken ct = default)
    {
        var radius = Math.Clamp(radiusMiles, 1, 500);

        if (string.IsNullOrWhiteSpace(query))
            return Fail(query, zip, radius, "Enter something to search for.");

        var site = CraigslistSites.Resolve(zip, siteId);
        if (site is null)
        {
            return Fail(query, zip, radius,
                "Craigslist searches one metro at a time, so it needs a US zip code (or a site picked by hand) to know which one.");
        }

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;
        // Craigslist serves the search page to anything that asks politely — the honest identifier
        // below was checked against the live site and gets the same 200 a browser string does, so
        // there's no reason to pretend to be Chrome.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ING-AutoLister/1.0 (local sourcing; contact via inglisting.com)");
        http.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/xhtml+xml, application/rss+xml, application/xml;q=0.9");

        var listings = new List<LocalSupplyListing>();

        var searchUrl = CraigslistParser.BuildSearchUrl(site.Id, query, zip, radius, rss: false);
        var (html, transportError) = await GetAsync(http, searchUrl, ct);
        if (html is not null) listings.AddRange(CraigslistParser.ParseStaticHtml(html));

        // A failed request is not an empty market, and the feed is served by the same host that
        // just refused us — so the fallback is only worth trying when the page itself came back
        // fine and simply had no results in it (or markup that moved).
        if (transportError is not null)
            return Fail(query, zip, radius, transportError, site);

        if (listings.Count == 0)
        {
            var rssUrl = CraigslistParser.BuildSearchUrl(site.Id, query, zip, radius);
            // A 403 here is craigslist's normal answer on its current search stack, not a fault
            // worth reporting on top of an empty result — hence the discarded error.
            var (feed, _) = await GetAsync(http, rssUrl, ct);
            if (feed is not null) listings.AddRange(CraigslistParser.ParseRss(feed));
        }

        var result = CraigslistParser.BuildResult(listings, site, query, zip, radius);

        if (result.Count > 0 && !CraigslistSites.IsExactZipMatch(zip) && string.IsNullOrWhiteSpace(siteId))
        {
            result.Error = $"No craigslist site covers {zip} directly, so the nearest one ({site.Label}) was searched — " +
                           "pick a different site above if that's the wrong metro.";
        }

        log.Add("Info", "Craigslist search",
            $"\"{query}\" within {radius} mi of {zip} on {site.Id}.craigslist.org — {result.Count} local listing(s).");

        return result;
    }

    private static async Task<(string? Body, string? Error)> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 means craigslist is rate-limiting this IP. Backing off is the correct
                // and only response; retrying in a loop is how an address ends up blocked.
                return (null, (int)response.StatusCode switch
                {
                    403 or 429 => "Craigslist is rate-limiting this connection right now — wait a minute and search again.",
                    404 => "That craigslist site or search URL doesn't exist — try picking the site by hand.",
                    _ => $"Craigslist returned HTTP {(int)response.StatusCode}.",
                });
            }

            return (await response.Content.ReadAsStringAsync(ct), null);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, "Craigslist didn't respond in time.");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Couldn't reach craigslist: {ex.Message}");
        }
    }

    private static LocalSupplySearchResult Fail(
        string query, string? zip, int radius, string error, CraigslistSite? site = null) => new()
    {
        SourceId = CraigslistParser.SourceId,
        SourceLabel = CraigslistParser.SourceLabel,
        // Craigslist has no session to lose, so it only ever fails as "error" — never
        // not_connected or session_expired.
        Status = "error",
        Query = query,
        ZipCode = zip ?? "",
        RadiusMiles = radius,
        SearchUrl = site is null ? "" : CraigslistParser.BuildSearchUrl(site.Id, query, zip ?? "", radius, rss: false),
        ScopeLabel = site is null ? "" : $"{site.Label} craigslist ({site.State})",
        Error = error,
    };
}
