using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Going-out-of-business sourcing: buy it from a business that is emptying itself, sell it on eBay.
///
/// <para>The other sourcing boards buy one object from one seller — a drill off Craigslist, a
/// clearance vacuum off a deal feed. This one buys from the sales that happen when a shop closes, a
/// warehouse clears its returns, or a company is dispersed: store closings, overstock and customer
/// -return auctions, municipal and school surplus. It is the cheapest stock in this app by a wide
/// margin, because the seller's goal is an empty building rather than a good price.</para>
///
/// <para>It is an <see cref="ILocalSupplySource"/> and nothing more. Grouping, comp lookups, the
/// profit maths and the ranking were already written against <c>LocalSupplyListing</c>, so nothing
/// downstream had to learn that liquidation supply exists — the same payoff Craigslist and the deal
/// feeds collected before it. What it adds is priced rather than hidden, in
/// <see cref="LiquidationLotPricer"/>:</para>
/// <list type="bullet">
///   <item><b>It is an auction.</b> The price is the current bid, which is a floor, not a cost. So
///   the number that matters is the highest bid still worth making.</item>
///   <item><b>There is a buyer's premium</b>, and sales tax on top of it.</item>
///   <item><b>It is often a lot.</b> Several units of one product, priced per unit through the same
///   grade assumptions the Liquidation Lot Analyzer applies to a pasted manifest.</item>
/// </list>
///
/// <para>Posture, matching CraigslistService and DealFeedService exactly: user-driven only, one GET
/// per slice per click for the seller's own query, nothing scheduled, nothing crawled, nothing
/// stored, no account, no key. A slice that fails fails alone, and nothing retries past a refusal.</para>
/// </summary>
public sealed class LiquidationSourceService(IHttpClientFactory httpFactory, ActionLog log) : ILocalSupplySource
{
    // One search page. These are large server-rendered documents rather than a small feed, so this
    // is more generous than the deal feeds get — but a request near it is one that isn't going to
    // complete.
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    // The whole scan, all slices together. Bounded well inside LocalSupplyGuard.PublicSourceTimeout
    // so a slow slice costs the seller the slices after it, never the lots already in hand.
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(38);

    // A search page carrying a hundred lots and their auctions runs to a couple of megabytes.
    // Generous, but bounded: an endpoint that starts streaming without end must not be able to
    // exhaust this process's memory.
    private const int MaxPageBytes = 12 * 1024 * 1024;

    public string Id => LiquidationCatalog.SourceId;
    public string Label => LiquidationCatalog.SourceLabel;
    public bool RequiresConnection => false;
    public bool IsAvailable => true;

    // Auctions really do happen somewhere, and the site filters by zip — so unlike the deal feeds
    // this source honours the form's location. What it does NOT honour is a tight radius; see
    // LiquidationCatalog.MinRadiusMiles and the scope label, which says what was actually searched.
    public bool IsLocationBased => true;

    // An auction house bills sales tax on the hammer plus the premium, exactly as a till does.
    public bool ChargesSalesTax => true;

    public int MinRadiusMiles => LiquidationCatalog.MinRadiusMiles;

    public IReadOnlyList<LocalSupplyManualSite> ManualSites =>
        LiquidationCatalog.ManualSites
            .Select(s => new LocalSupplyManualSite { Id = s.Id, Label = s.Label, UrlTemplate = s.UrlTemplate, Note = s.Note })
            .ToList();

    public string AvailabilityNote =>
        $"Public search on {LiquidationCatalog.Site} — store closings, overstock, customer returns and surplus " +
        $"auctions from thousands of auction houses. No login. Searches at least {LiquidationCatalog.MinRadiusMiles} miles, " +
        "because auctions are far sparser than classifieds.";

    /// <summary>
    /// One user-initiated scan across the catalogue slices.
    /// </summary>
    public async Task<LocalSupplySearchResult> SearchAsync(
        string query, string zip, int radiusMiles, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Fail(query, zip, radiusMiles, "Enter something to look for — these auctions carry everything from office chairs to pallets, so a scan needs a keyword.");

        var http = httpFactory.CreateClient();
        http.Timeout = RequestTimeout;
        ApplyBrowserHeaders(http);

        // The scan's own deadline, so four slices can't add up to four times what one is allowed.
        // The caller's token is linked in and the two are told apart below: a seller who navigated
        // away and a site that stopped answering deserve different handling.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(ScanBudget);

        var perFeed = new List<List<LocalSupplyListing>>();
        var failures = new List<string>();
        var retryable = false;
        var skipped = 0;
        var parsed = 0;

        foreach (var feed in LiquidationCatalog.Feeds)
        {
            // Out of time. The slices already read are real results and are returned; saying how
            // many were skipped is more useful than dropping them silently or failing the whole
            // scan over the slowest request in the list.
            if (budget.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                skipped = LiquidationCatalog.Feeds.Count - perFeed.Count - failures.Count;
                break;
            }

            var url = LiquidationCatalog.BuildUrl(feed, query, zip, radiusMiles);
            var (html, error, feedRetryable) = await GetAsync(http, url, budget.Token, ct);

            if (html is null)
            {
                failures.Add($"{feed.Label}: {error}");
                retryable |= feedRetryable;
                continue;
            }

            var listings = LiquidationParser.ParsePage(html, feed, DateTime.UtcNow);
            parsed += listings.Count;
            perFeed.Add(listings);
        }

        // Every slice refused. That is a failure worth reporting as one — there is nothing to show
        // and no ranking to put a warning next to.
        if (perFeed.Count == 0)
        {
            return Fail(query, zip, radiusMiles,
                failures.Count > 0
                    ? $"{LiquidationCatalog.Site} couldn't be searched. {string.Join(" · ", failures)}"
                    : $"{LiquidationCatalog.Site} couldn't be searched.",
                retryable);
        }

        var result = LiquidationParser.BuildResult(perFeed, query, zip, radiusMiles);
        result.Retryable = retryable;

        // Partial success, said plainly beside real results rather than instead of them.
        var notes = new List<string>();
        if (failures.Count > 0) notes.Add($"{failures.Count} of {LiquidationCatalog.Feeds.Count} searches couldn't be run ({string.Join(" · ", failures)})");
        if (skipped > 0) notes.Add($"{skipped} search(es) skipped — the scan ran out of time");
        if (notes.Count > 0) result.Error = string.Join(". ", notes) + ".";

        log.Add("Info", "Liquidation scan",
            $"\"{query}\" across {perFeed.Count}/{LiquidationCatalog.Feeds.Count} search(es) " +
            $"within {LiquidationCatalog.RadiusFor(radiusMiles)} mi{(string.IsNullOrWhiteSpace(zip) ? " (nationwide)" : $" of {zip}")} — " +
            $"{parsed} lot(s) parsed, {result.Count} after dedupe" +
            $"{(failures.Count > 0 ? $"; failed: {string.Join(" · ", failures)}" : "")}.");

        return result;
    }

    /// <summary>
    /// This is a public search page, so the request is shaped like a reader asking for one page it
    /// was pointed at. A bare client with no user agent is the shape a CDN refuses first, and a
    /// refusal parses to zero lots — which would reach the seller as "no liquidation stock matches"
    /// rather than as a failure.
    ///
    /// No Accept-Encoding: this client isn't configured to decompress, and advertising gzip would
    /// hand the parser a binary blob that reads as an empty page. A silent wrong answer is worse
    /// than a loud failure.
    /// </summary>
    private static void ApplyBrowserHeaders(HttpClient http)
    {
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
    }

    /// <summary>
    /// One search, and every way it can go wrong turned into a sentence. Nothing here throws: a site
    /// that refuses, stalls or answers with a challenge page has to arrive at the UI as a note
    /// beside whatever the other slices found, never as a failed response.
    /// </summary>
    private static async Task<(string? Body, string? Error, bool Retryable)> GetAsync(
        HttpClient http, string url, CancellationToken budget, CancellationToken caller)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, budget);
            if (!response.IsSuccessStatusCode)
            {
                return (int)response.StatusCode switch
                {
                    // Backing off is the correct and only response. Retrying in a loop is how an
                    // address gets blocked from a site that was working fine a minute ago.
                    403 or 429 => (null, "rate-limited right now — wait a minute and scan again", true),
                    404 or 410 => (null, "that search has moved — it needs updating in LiquidationCatalog", false),
                    >= 500 => (null, $"the site is having trouble (HTTP {(int)response.StatusCode})", true),
                    _ => (null, $"HTTP {(int)response.StatusCode}", false),
                };
            }

            if (response.Content.Headers.ContentLength > MaxPageBytes)
                return (null, "that page came back far larger than a search page should be", false);

            var body = await ReadBoundedAsync(response, budget);
            if (body is null) return (null, "that page came back far larger than a search page should be", false);

            // A 200 is not the same as an answer: a challenge page is served with a success status
            // and parses to zero lots, which looks exactly like "nothing matched".
            var blocked = LiquidationParser.DetectBlock(body);
            return blocked is not null ? (null, blocked, true) : (body, null, false);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
            throw;   // the browser hung up; there is nothing left to report to
        }
        catch (OperationCanceledException)
        {
            return (null, "didn't respond in time", true);
        }
        catch (HttpRequestException ex)
        {
            return (null, $"couldn't be reached ({ex.Message})", true);
        }
        catch (Exception ex)
        {
            // Deliberately total. Whatever this was — DNS, a proxy, a TLS failure — it is one slice
            // failing, and the scan's other slices still have lots to show.
            return (null, ex.Message, true);
        }
    }

    // Reads the body without trusting the site to have declared its length. Returns null past the
    // ceiling rather than growing a string until the process dies.
    private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var memory = new MemoryStream();

        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (memory.Length + read > MaxPageBytes) return null;
            memory.Write(buffer, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(memory.ToArray());
    }

    private static LocalSupplySearchResult Fail(
        string query, string? zip, int radius, string error, bool retryable = false) => new()
    {
        SourceId = LiquidationCatalog.SourceId,
        SourceLabel = LiquidationCatalog.SourceLabel,
        // A public search: no session to lose, so this only ever fails as "error" — never
        // not_connected or session_expired.
        Status = "error",
        Query = query,
        ZipCode = zip ?? "",
        RadiusMiles = LiquidationCatalog.RadiusFor(radius),
        SearchUrl = LiquidationCatalog.SearchPageUrl(query, zip, radius),
        ScopeLabel = LiquidationCatalog.ScopeLabel(zip, radius),
        Error = error,
        Retryable = retryable,
    };
}
