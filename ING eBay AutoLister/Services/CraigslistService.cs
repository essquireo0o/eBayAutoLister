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
///   • A 403, a rate-limit or a block page is reported as what it is, once. Retrying past it is
///     how an IP gets blocked, so the result says "retryable" and the seller chooses.
/// </summary>
public sealed class CraigslistService(IHttpClientFactory httpFactory, ActionLog log) : ILocalSupplySource
{
    // One HTTP call. Craigslist answers a search in about a second; anything near this is a
    // connection that isn't going to complete.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    // The whole search — the results page plus the RSS fallback. Bounded separately so two slow
    // requests can't add up to twice the wait a single one is allowed.
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(35);

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
        ApplyBrowserHeaders(http);

        // The search's own budget, so the results page and the fallback feed together can't hold
        // the caller longer than SearchTimeout however slow either one is. The caller's token is
        // linked in, and the two are told apart below — a seller who navigated away and a
        // craigslist that stopped answering deserve different handling.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(SearchTimeout);

        var listings = new List<LocalSupplyListing>();

        var searchUrl = CraigslistParser.BuildSearchUrl(site.Id, query, zip, radius, rss: false);
        var (html, transportError, retryable) = await GetAsync(http, searchUrl, budget.Token, ct);
        if (html is not null) listings.AddRange(CraigslistParser.ParseStaticHtml(html));

        // A failed request is not an empty market, and the feed is served by the same host that
        // just refused us — so the fallback is only worth trying when the page itself came back
        // fine and simply had no results in it (or markup that moved).
        if (transportError is not null)
            return Fail(query, zip, radius, transportError, site, retryable);

        if (listings.Count == 0)
        {
            var rssUrl = CraigslistParser.BuildSearchUrl(site.Id, query, zip, radius);
            // A 403 here is craigslist's normal answer on its current search stack, not a fault
            // worth reporting on top of an empty result — hence the discarded error.
            var (feed, _, _) = await GetAsync(http, rssUrl, budget.Token, ct);
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

    /// <summary>
    /// Craigslist decides what to serve partly on how the request looks, and a request with no
    /// browser headers is the shape it refuses first — a bare client draws a 403 on searches a
    /// browser gets a 200 for. So the request carries what it actually is: a person's browser
    /// fetching one search page they asked for, at their pace, from their own machine.
    ///
    /// No Accept-Encoding. This client isn't configured to decompress, and advertising gzip would
    /// hand the parser a binary blob that reads as zero results — a silent wrong answer, which is
    /// worse than a loud failure.
    /// </summary>
    private static void ApplyBrowserHeaders(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,application/rss+xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        // TryAddWithoutValidation because these aren't in HttpClient's known-header table; a plain
        // Add would throw on the "?1" values.
        http.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");
    }

    /// <summary>
    /// One request, and every way it can go wrong turned into a sentence. Nothing here throws:
    /// a craigslist that refuses, stalls or answers with a block page has to arrive at the UI as a
    /// message beside whatever the other sources found, never as a failed response.
    ///
    /// <paramref name="budget"/> is the search's own deadline; <paramref name="caller"/> is the
    /// request's. Only the caller's cancellation propagates — the browser is gone, so there is
    /// nothing left to report to.
    /// </summary>
    private static async Task<(string? Body, string? Error, bool Retryable)> GetAsync(
        HttpClient http, string url, CancellationToken budget, CancellationToken caller)
    {
        try
        {
            using var response = await http.GetAsync(url, budget);
            if (!response.IsSuccessStatusCode)
            {
                // 403/429 means craigslist is rate-limiting or blocking this IP. Backing off is the
                // correct and only response; retrying in a loop is how an address ends up blocked.
                return (int)response.StatusCode switch
                {
                    403 or 429 => (null, "Craigslist is rate-limiting this connection right now — wait a minute and search again.", true),
                    404 => (null, "That craigslist site or search URL doesn't exist — try picking the site by hand.", false),
                    >= 500 => (null, $"Craigslist is having trouble at its end (HTTP {(int)response.StatusCode}) — try again shortly.", true),
                    _ => (null, $"Craigslist returned HTTP {(int)response.StatusCode}.", false),
                };
            }

            var body = await response.Content.ReadAsStringAsync(budget);

            // A 200 is not the same as an answer: craigslist serves its block page and the
            // interstitials in front of it with a success status, and both parse to zero listings.
            var blocked = CraigslistParser.DetectBlock(body);
            return blocked is not null ? (null, blocked, true) : (body, null, false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return (null, "Craigslist didn't respond in time — try again in a moment.", true);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"Couldn't reach craigslist: {ex.Message}", true);
        }
        catch (Exception ex)
        {
            // Deliberately total. Whatever this was — a DNS layer, a proxy, a TLS failure — it is
            // one site failing, and the search's other sources still have results to show.
            return (null, $"Craigslist couldn't be searched: {ex.Message}", true);
        }
    }

    private static LocalSupplySearchResult Fail(
        string query, string? zip, int radius, string error, CraigslistSite? site = null, bool retryable = false) => new()
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
        Retryable = retryable,
    };
}
