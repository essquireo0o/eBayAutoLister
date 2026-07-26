using ING_eBay_AutoLister.Models;
using Microsoft.AspNetCore.WebUtilities;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace ING_eBay_AutoLister.Services;

public class EbayService(CredentialsStore creds, IHttpClientFactory httpClientFactory, ActionLog log)
{

    private static readonly XNamespace EbayNs = "urn:ebay:apis:eBLBaseComponents";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string BaseUrl => creds.Get().EbaySandbox
        ? "https://api.sandbox.ebay.com"
        : "https://api.ebay.com";

    private string TradingEndpoint => creds.Get().EbaySandbox
        ? "https://api.sandbox.ebay.com/ws/api.dll"
        : "https://api.ebay.com/ws/api.dll";

    private string AuthUrl => creds.Get().EbaySandbox
        ? "https://auth.sandbox.ebay.com/oauth2/authorize"
        : "https://auth.ebay.com/oauth2/authorize";

    private string TokenUrl => creds.Get().EbaySandbox
        ? "https://api.sandbox.ebay.com/identity/v1/oauth2/token"
        : "https://api.ebay.com/identity/v1/oauth2/token";

    // ── OAuth authorization URL ───────────────────────────────────────────────

    // Stores the session ID for the in-flight OAuth request so /api/ebay/finish can validate it.
    public string? PendingOAuthSession { get; private set; }

    public string GetAuthorizationUrl()
    {
        var c = creds.Get();
        if (string.IsNullOrWhiteSpace(c.EbayClientId))
            throw new InvalidOperationException("eBay Client ID is not configured. Open Settings to add it.");
        if (c.EbaySandbox && string.IsNullOrWhiteSpace(c.EbayRuName))
            throw new InvalidOperationException("eBay RuName is not configured. Open Settings to add it.");

        // Random 32-hex-char session ID used as state for CSRF protection and server-side session correlation
        PendingOAuthSession = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLower();

        var redirectUri = GetOAuthRedirectUri(forceProduction: false);
        var scopes = Uri.EscapeDataString(string.Join(" ",
            "https://api.ebay.com/oauth/api_scope",
            "https://api.ebay.com/oauth/api_scope/sell.inventory",
            "https://api.ebay.com/oauth/api_scope/sell.account",
            "https://api.ebay.com/oauth/api_scope/sell.fulfillment"));

        return $"{AuthUrl}?client_id={Uri.EscapeDataString(c.EbayClientId)}" +
               $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
               $"&response_type=code&scope={scopes}&state={Uri.EscapeDataString(PendingOAuthSession)}";
    }

    // ── Token exchange ────────────────────────────────────────────────────────

    public async Task<string> ExchangeCodeForTokenAsync(string code)
    {
        var result = await ExchangeCodeInternalAsync(code, forceProduction: false);
        return result.AccessToken;
    }

    public Task<EbayTokenExchangeResult> ExchangeCodeForTokenResultAsync(string code) =>
        ExchangeCodeInternalAsync(code, forceProduction: false);

    public async Task<EbayOAuthRedirectExchangeResult> ExchangeProductionRedirectUrlAsync(string redirectUrl)
    {
        var (code, state) = ParseProductionRedirectUrl(redirectUrl);
        log.Add("Info", "OAuth code extraction", $"Code present: {!string.IsNullOrWhiteSpace(code)}; State: {state ?? "(none)"}");

        EbayTokenExchangeResult tokenResult;
        try
        {
            tokenResult = await ExchangeCodeInternalAsync(code, forceProduction: true);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "OAuth token exchange failed", ex.Message);
            throw;
        }

        var expiresAtUtc = tokenResult.ExpiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(tokenResult.ExpiresIn).ToString("u")
            : "unknown";
        log.Add("Info", "OAuth token exchange succeeded",
            $"Access token saved: {!string.IsNullOrWhiteSpace(tokenResult.AccessToken)}; " +
            $"Refresh token saved: {!string.IsNullOrWhiteSpace(tokenResult.RefreshToken)}; " +
            $"Expires at: {expiresAtUtc}; Token type: {tokenResult.TokenType}");

        var c2 = creds.Get();
        return new EbayOAuthRedirectExchangeResult(
            tokenResult.AccessToken, tokenResult.RefreshToken,
            tokenResult.ExpiresIn, tokenResult.RefreshTokenExpiresIn, tokenResult.TokenType,
            code, state ?? "", c2.EbayRuName ?? "", c2.EbayRuName ?? "");
    }

    private async Task<EbayTokenExchangeResult> ExchangeCodeInternalAsync(string code, bool forceProduction)
    {
        var c = creds.Get();
        if (string.IsNullOrWhiteSpace(c.EbayClientId))
            throw new InvalidOperationException("eBay Client ID is not configured.");
        if (string.IsNullOrWhiteSpace(c.EbayClientSecret))
            throw new InvalidOperationException("eBay Client Secret is not configured.");

        var redirectUri = GetOAuthRedirectUri(forceProduction);
        var client = httpClientFactory.CreateClient();
        var basicCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.EbayClientId}:{c.EbayClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new FormUrlEncodedContent([
            new("grant_type", "authorization_code"),
            new("code", code),
            new("redirect_uri", redirectUri)
        ]);

        var tokenUrl = forceProduction
            ? "https://api.ebay.com/identity/v1/oauth2/token"
            : TokenUrl;

        var response = await client.PostAsync(tokenUrl, body);
        var responseBody = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"Token exchange HTTP {(int)response.StatusCode}",
            response.IsSuccessStatusCode ? "Exchange succeeded" : responseBody);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"eBay token exchange failed (HTTP {(int)response.StatusCode}): {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var accessToken      = doc.RootElement.TryGetProperty("access_token",             out var at)   ? at.GetString()   ?? "" : "";
        var refreshToken     = doc.RootElement.TryGetProperty("refresh_token",            out var rt)   ? rt.GetString()   ?? "" : "";
        var expiresIn        = doc.RootElement.TryGetProperty("expires_in",               out var exp)  ? exp.GetInt32()        : 0;
        var refreshExpiresIn = doc.RootElement.TryGetProperty("refresh_token_expires_in", out var rexp) ? rexp.GetInt32()       : 0;
        var tokenType        = doc.RootElement.TryGetProperty("token_type",               out var tt)   ? tt.GetString()   ?? "" : "";

        return new EbayTokenExchangeResult(accessToken, refreshToken, expiresIn, refreshExpiresIn, tokenType, redirectUri);
    }

    // ── Token refresh ─────────────────────────────────────────────────────────

    private async Task<string> RefreshAccessTokenAsync(string refreshToken)
    {
        var c = creds.Get();
        if (string.IsNullOrWhiteSpace(c.EbayClientId) || string.IsNullOrWhiteSpace(c.EbayClientSecret))
            throw new InvalidOperationException("eBay ClientId/ClientSecret not configured — cannot refresh token.");

        var client = httpClientFactory.CreateClient();
        var basicCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.EbayClientId}:{c.EbayClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new FormUrlEncodedContent([
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken)
        ]);

        var response = await client.PostAsync(TokenUrl, body);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            log.Add("Warning", $"Token refresh HTTP {(int)response.StatusCode}", responseBody);
            throw new Exception($"eBay token refresh failed (HTTP {(int)response.StatusCode}): {responseBody}");
        }

        using var doc = JsonDocument.Parse(responseBody);
        var accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() ?? "" : "";
        var expiresIn   = doc.RootElement.TryGetProperty("expires_in",   out var exp) ? exp.GetInt32()      : 0;
        var tokenType   = doc.RootElement.TryGetProperty("token_type",   out var tt)  ? tt.GetString() ?? "" : "";

        creds.SaveRefreshedAccessToken(accessToken, expiresIn);
        log.Add("Info", "Access token refreshed",
            $"Saved: {!string.IsNullOrWhiteSpace(accessToken)}; Expires at: {(expiresIn > 0 ? DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToString("u") : "unknown")}; Type: {tokenType}");

        return accessToken;
    }

    // ── Application (client_credentials) token — for Buy APIs that don't need a user login ──
    // NOTE: eBay grants client_credentials tokens all-or-nothing per requested scope string — if
    // any single scope isn't approved for the app, the ENTIRE token request fails. Marketplace
    // Insights requires special approval most dev accounts don't have, so it gets its own token
    // request/cache, separate from the base Buy API scope that Browse search always has access to.

    private string? _appToken;
    private DateTimeOffset _appTokenExpiry;

    private async Task<string> GetApplicationTokenAsync() =>
        await RequestApplicationTokenAsync(
            "https://api.ebay.com/oauth/api_scope https://api.ebay.com/oauth/api_scope/buy.marketplace.insights",
            () => _appToken, () => _appTokenExpiry,
            (t, e) => { _appToken = t; _appTokenExpiry = e; });

    private string? _browseAppToken;
    private DateTimeOffset _browseAppTokenExpiry;

    private async Task<string> GetBrowseApplicationTokenAsync() =>
        await RequestApplicationTokenAsync(
            "https://api.ebay.com/oauth/api_scope",
            () => _browseAppToken, () => _browseAppTokenExpiry,
            (t, e) => { _browseAppToken = t; _browseAppTokenExpiry = e; });

    private async Task<string> RequestApplicationTokenAsync(
        string scope, Func<string?> getCached, Func<DateTimeOffset> getExpiry, Action<string, DateTimeOffset> setCached)
    {
        var cached = getCached();
        if (!string.IsNullOrWhiteSpace(cached) && DateTimeOffset.UtcNow < getExpiry())
            return cached;

        var c = creds.Get();
        if (string.IsNullOrWhiteSpace(c.EbayClientId) || string.IsNullOrWhiteSpace(c.EbayClientSecret))
            throw new InvalidOperationException("eBay ClientId/ClientSecret not configured — cannot request an application token.");

        var client = httpClientFactory.CreateClient();
        var basicCreds = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{c.EbayClientId}:{c.EbayClientSecret}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basicCreds);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var body = new FormUrlEncodedContent([
            new("grant_type", "client_credentials"),
            new("scope", scope)
        ]);

        var response = await client.PostAsync(TokenUrl, body);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"eBay application token request failed (HTTP {(int)response.StatusCode}): {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var accessToken = doc.RootElement.GetProperty("access_token").GetString() ?? "";
        var expiresIn   = doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600;

        setCached(accessToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn - 60));
        return accessToken;
    }

    // ── Recently-sold comps (Marketplace Insights API — last 90 days) ────────

    // eBay's q= param matches each word independently, in any order, anywhere in the title —
    // "graphics card" un-quoted also matches "Vintage Business Cards ... Graphic..." or
    // "... Safety Card ... Graphics". Quoting forces it to require the phrase together, which
    // is what eBay's own site does by default under its relevance-ranked "Best Match" sort.
    private static string QuotePhrase(string term) =>
        term.Contains(' ') ? $"\"{term.Replace("\"", "")}\"" : term;

    public async Task<SoldCompsResult> SearchSoldCompsAsync(string query, int daysBack = 60)
    {
        var token = await GetApplicationTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        var url = "https://api.ebay.com/buy/marketplace_insights/v1_beta/item_sales/search" +
                   $"?q={Uri.EscapeDataString(QuotePhrase(query))}&limit=50";

        var response = await client.GetAsync(url);
        var responseBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Marketplace Insights request failed (HTTP {(int)response.StatusCode}): {responseBody}");

        using var doc = JsonDocument.Parse(responseBody);
        var result = new SoldCompsResult { Query = query };
        var cutoff = DateTimeOffset.UtcNow.AddDays(-daysBack);

        if (doc.RootElement.TryGetProperty("itemSales", out var sales))
        {
            foreach (var item in sales.EnumerateArray())
            {
                var soldDateStr = item.TryGetProperty("lastSoldDate", out var lsd) ? lsd.GetString() : null;
                if (!DateTimeOffset.TryParse(soldDateStr, out var soldDate)) continue;
                if (soldDate < cutoff) continue;

                var price = item.TryGetProperty("lastSoldPrice", out var lp) &&
                            lp.TryGetProperty("value", out var pv) &&
                            decimal.TryParse(pv.GetString(), out var pVal) ? pVal : 0;

                result.Items.Add(new SoldComp
                {
                    Title    = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                    Price    = price,
                    SoldDate = soldDate.UtcDateTime,
                    Url      = item.TryGetProperty("itemWebUrl", out var u) ? u.GetString() ?? "" : "",
                    ImageUrl = item.TryGetProperty("image", out var img) && img.TryGetProperty("imageUrl", out var iu) ? iu.GetString() ?? "" : ""
                });
            }
        }

        if (result.Items.Count > 0)
        {
            var prices = result.Items.Select(i => i.Price).Where(p => p > 0).OrderBy(p => p).ToList();
            result.Count   = prices.Count;
            result.Average = prices.Count > 0 ? Math.Round(prices.Average(), 2) : 0;
            result.Median  = prices.Count > 0 ? prices[prices.Count / 2] : 0;
            result.Min     = prices.Count > 0 ? prices[0] : 0;
            result.Max     = prices.Count > 0 ? prices[^1] : 0;
        }

        return result;
    }

    // ── Live listings ending soon (Browse API — public search, no special approval needed) ──

    public async Task<List<EbayOpportunityItem>> SearchEndingSoonAsync(
        string query, int minFeedback = 0, int limit = 50, string? category = null,
        string? condition = null, decimal? minPrice = null, decimal? maxPrice = null, string listingType = "AUCTION")
    {
        var token = await GetBrowseApplicationTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        // Auctions sort by soonest-ending (the whole "flip before it ends" premise); fixed-price
        // listings have no meaningful end time, so mixing them in switches sort to newly listed.
        var buyingOptions = listingType switch
        {
            "FIXED_PRICE" => "FIXED_PRICE",
            "BOTH"        => "AUCTION|FIXED_PRICE",
            _             => "AUCTION"
        };
        var sort = listingType == "AUCTION" ? "endingSoonest" : "newlyListed";

        var filters = new List<string> { $"buyingOptions:{{{buyingOptions}}}" };

        var conditionIds = condition?.ToUpperInvariant() switch
        {
            "NEW"         => "1000|1500",
            "USED"        => "3000|4000|5000|6000",
            "REFURBISHED" => "2000|2500",
            "FOR_PARTS"   => "7000",
            _             => null
        };
        if (conditionIds is not null)
            filters.Add($"conditionIds:{{{conditionIds}}}");

        if (minPrice.HasValue || maxPrice.HasValue)
        {
            var lo = minPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            var hi = maxPrice?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            filters.Add($"price:[{lo}..{hi}]");
            filters.Add("priceCurrency:USD");
        }

        // No real category taxonomy lookup — folding it into the free-text query is good enough
        // to narrow results without a separate eBay Taxonomy API integration.
        var q = string.IsNullOrWhiteSpace(category) ? QuotePhrase(query) : $"{QuotePhrase(query)} {QuotePhrase(category)}";

        var url = "https://api.ebay.com/buy/browse/v1/item_summary/search" +
                   $"?q={Uri.EscapeDataString(q)}&filter={Uri.EscapeDataString(string.Join(",", filters))}&sort={sort}&limit={limit}";

        var response = await client.GetAsync(url);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"eBay listing search failed (HTTP {(int)response.StatusCode}): {body}");

        var items = new List<EbayOpportunityItem>();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("itemSummaries", out var summaries))
            return items;

        foreach (var item in summaries.EnumerateArray())
        {
            var seller = item.TryGetProperty("seller", out var s) ? s : default;
            var feedbackScore = seller.ValueKind == JsonValueKind.Object &&
                                 seller.TryGetProperty("feedbackScore", out var fs) &&
                                 fs.TryGetInt32(out var fsVal) ? fsVal : 0;
            if (feedbackScore < minFeedback) continue;

            // Auction items with no bids yet often have no "price" — the real current price
            // lives in currentBidPrice instead. Fall back to that so $0 doesn't show for them.
            var price = item.TryGetProperty("price", out var p) &&
                        p.TryGetProperty("value", out var pv) &&
                        decimal.TryParse(pv.GetString(), out var priceVal) ? priceVal : 0;
            if (price == 0 && item.TryGetProperty("currentBidPrice", out var cbp) &&
                cbp.TryGetProperty("value", out var cbv) &&
                decimal.TryParse(cbv.GetString(), out var cbVal))
                price = cbVal;

            DateTime? endDate = item.TryGetProperty("itemEndDate", out var ed) &&
                                DateTimeOffset.TryParse(ed.GetString(), out var edVal)
                                ? edVal.UtcDateTime : null;

            // Cheapest shipping option's cost — used to compare buyer's real total cost
            // (price + shipping) against sold comps, not just the listed price alone.
            var shippingCost = 0m;
            if (item.TryGetProperty("shippingOptions", out var shipOpts) && shipOpts.ValueKind == JsonValueKind.Array)
            {
                foreach (var opt in shipOpts.EnumerateArray())
                {
                    if (opt.TryGetProperty("shippingCost", out var sc) &&
                        sc.TryGetProperty("value", out var scv) &&
                        decimal.TryParse(scv.GetString(), out var scVal))
                    {
                        shippingCost = scVal;
                        break;
                    }
                }
            }

            items.Add(new EbayOpportunityItem
            {
                Title               = item.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "",
                Price               = price,
                ShippingCost        = shippingCost,
                Url                 = item.TryGetProperty("itemWebUrl", out var u) ? u.GetString() ?? "" : "",
                ImageUrl            = item.TryGetProperty("image", out var img) && img.TryGetProperty("imageUrl", out var iu) ? iu.GetString() ?? "" : "",
                EndDate             = endDate,
                SellerUsername      = seller.ValueKind == JsonValueKind.Object && seller.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "",
                SellerFeedbackScore = feedbackScore,
                BuyingOption        = item.TryGetProperty("buyingOptions", out var bo) && bo.GetArrayLength() > 0 ? bo[0].GetString() ?? "" : "",
                BidCount            = item.TryGetProperty("bidCount", out var bc) && bc.TryGetInt32(out var bcVal) ? bcVal : 0,
            });
        }

        return items;
    }

    // Cheap "how many are actually listed right now" check for the Low Competition insight —
    // limit=1 means eBay still returns the real total match count without paying for a full
    // page of results, and it's a normal Browse API call (not Terapeak), so it costs nothing
    // against the scrape budget.
    public async Task<int> GetActiveListingCountAsync(string query, string? category = null)
    {
        var token = await GetBrowseApplicationTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        var q = string.IsNullOrWhiteSpace(category) ? QuotePhrase(query) : $"{QuotePhrase(query)} {QuotePhrase(category)}";
        var url = "https://api.ebay.com/buy/browse/v1/item_summary/search" +
                   $"?q={Uri.EscapeDataString(q)}&filter={Uri.EscapeDataString("buyingOptions:{AUCTION|FIXED_PRICE}")}&limit=1";

        var response = await client.GetAsync(url);
        if (!response.IsSuccessStatusCode) return 0;

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("total", out var total) && total.TryGetInt32(out var totalVal) ? totalVal : 0;
    }

    // Browse API's item_summary/search always requires q/category_ids/epid/gtin — there is no way
    // to ask it for "everything a seller has" without also supplying a keyword (confirmed live:
    // filter=sellers alone comes back "errorId 12001 ... must have a valid q ..."). eBay's newer
    // Finding API (svcs.ebay.com) — the traditional way to do a keyword-less Seller lookup — is
    // returning a blanket 503 from eBay's edge for every request now (verified directly, not an
    // auth/params issue on this end), so it's gone too. Trading API's GetSellerList is what's
    // left: it accepts a UserID for ANY seller (not just the caller's own account, confirmed live
    // against a real third-party seller) and needs no extra credential beyond the same IAF token
    // GetListingsAsync already uses.
    public async Task<List<EbayOpportunityItem>> SearchBySellerAsync(
        string sellerUsername, int limit = 50, string? condition = null,
        decimal? minPrice = null, decimal? maxPrice = null, string listingType = "BOTH")
    {
        var c = creds.Get();
        var token = await GetOrRefreshTokenAsync();
        var entriesPerPage = Math.Clamp(limit, 1, 200);
        var sellerFeedbackScore = await GetSellerFeedbackScoreAsync(sellerUsername, token, c);

        // Filter by EndTime, not StartTime: GTC (Good 'Til Cancelled) listings — most of an
        // established seller's active inventory — keep their original StartTime but roll
        // EndTime forward on every auto-renewal, so a StartTime window only ever catches
        // listings created recently. An EndTime window from "now" out to +119 days catches
        // every currently-active listing regardless of how long ago it was first listed,
        // since an ended listing's EndTime is always in the past.
        var xml =
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <GetSellerListRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <UserID>{System.Security.SecurityElement.Escape(sellerUsername)}</UserID>
              <EndTimeFrom>{DateTime.UtcNow:o}</EndTimeFrom>
              <EndTimeTo>{DateTime.UtcNow.AddDays(119):o}</EndTimeTo>
              <Pagination>
                <EntriesPerPage>{entriesPerPage}</EntriesPerPage>
                <PageNumber>1</PageNumber>
              </Pagination>
              <GranularityLevel>Fine</GranularityLevel>
              <DetailLevel>ReturnAll</DetailLevel>
            </GetSellerListRequest>
            """;

        var client = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("X-EBAY-API-SITEID", "0");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        request.Headers.Add("X-EBAY-API-CALL-NAME", "GetSellerList");
        request.Headers.Add("X-EBAY-API-APP-NAME",  c.EbayClientId);
        request.Headers.Add("X-EBAY-API-DEV-NAME",  c.EbayDevId);
        request.Headers.Add("X-EBAY-API-CERT-NAME", c.EbayClientSecret);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN", token);

        var response = await client.SendAsync(request);
        var xmlBody  = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new Exception($"eBay seller search HTTP {(int)response.StatusCode}: {xmlBody[..Math.Min(500, xmlBody.Length)]}");

        var doc  = XDocument.Parse(xmlBody);
        var root = doc.Root ?? throw new Exception("Empty response from eBay seller search.");
        var ack  = root.Element(EbayNs + "Ack")?.Value ?? "";
        if (ack is "Failure")
        {
            var errors = root.Descendants(EbayNs + "Errors")
                .Select(e => $"[{e.Element(EbayNs + "ErrorCode")?.Value}] {e.Element(EbayNs + "ShortMessage")?.Value}")
                .ToList();
            throw new Exception($"eBay seller search failed: {string.Join("; ", errors)}");
        }

        var minPriceFilter = minPrice ?? 0m;
        var maxPriceFilter = maxPrice ?? decimal.MaxValue;
        var items = new List<EbayOpportunityItem>();

        foreach (var item in root.Descendants(EbayNs + "Item"))
        {
            var listingDetails = item.Element(EbayNs + "ListingDetails");
            var sellingStatus  = item.Element(EbayNs + "SellingStatus");
            var shippingCost   = item.Element(EbayNs + "ShippingDetails")?
                .Element(EbayNs + "ShippingServiceOptions")?
                .Element(EbayNs + "ShippingServiceCost")?.Value;

            var priceStr = sellingStatus?.Element(EbayNs + "ConvertedCurrentPrice")?.Value
                         ?? sellingStatus?.Element(EbayNs + "CurrentPrice")?.Value;
            decimal.TryParse(priceStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var price);
            if (price < minPriceFilter || price > maxPriceFilter) continue;

            decimal.TryParse(shippingCost, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var shipCost);

            var rawListingType = item.Element(EbayNs + "ListingType")?.Value ?? "";
            var buyingOption = rawListingType.Contains("Auction", StringComparison.OrdinalIgnoreCase) ? "AUCTION" : "FIXED_PRICE";
            if (listingType == "AUCTION" && buyingOption != "AUCTION") continue;
            if (listingType == "FIXED_PRICE" && buyingOption != "FIXED_PRICE") continue;

            if (!string.IsNullOrWhiteSpace(condition))
            {
                var conditionDisplay = item.Element(EbayNs + "ConditionDisplayName")?.Value ?? "";
                var matchesCondition = condition.ToUpperInvariant() switch
                {
                    "NEW"         => conditionDisplay.Contains("New", StringComparison.OrdinalIgnoreCase),
                    "USED"        => conditionDisplay.Contains("Used", StringComparison.OrdinalIgnoreCase) || conditionDisplay.Contains("Pre-owned", StringComparison.OrdinalIgnoreCase),
                    "REFURBISHED" => conditionDisplay.Contains("Refurbished", StringComparison.OrdinalIgnoreCase) || conditionDisplay.Contains("Certified", StringComparison.OrdinalIgnoreCase),
                    "FOR_PARTS"   => conditionDisplay.Contains("parts", StringComparison.OrdinalIgnoreCase) || conditionDisplay.Contains("not working", StringComparison.OrdinalIgnoreCase),
                    _             => true
                };
                if (!matchesCondition) continue;
            }

            DateTime? endDate = DateTime.TryParse(listingDetails?.Element(EbayNs + "EndTime")?.Value,
                null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var edVal)
                ? edVal : null;

            int.TryParse(sellingStatus?.Element(EbayNs + "BidCount")?.Value, out var bidCount);

            items.Add(new EbayOpportunityItem
            {
                Title               = item.Element(EbayNs + "Title")?.Value ?? "",
                Price               = price,
                ShippingCost        = shipCost,
                Url                 = listingDetails?.Element(EbayNs + "ViewItemURL")?.Value ?? "",
                ImageUrl            = item.Element(EbayNs + "PictureDetails")?.Element(EbayNs + "GalleryURL")?.Value ?? "",
                EndDate             = endDate,
                SellerUsername      = sellerUsername,
                SellerFeedbackScore = sellerFeedbackScore,
                BuyingOption        = buyingOption,
                BidCount            = bidCount,
            });
        }

        return items;
    }

    // GetSellerList's <Seller> block describes the API caller's own account, not the seller
    // being queried, so it can't be used for a third-party seller's feedback score. GetUser
    // takes any UserID and returns that account's real <FeedbackScore>. Best-effort: a failure
    // here shouldn't fail the whole seller search, it just means trust scoring falls back to 0.
    private async Task<int> GetSellerFeedbackScoreAsync(string sellerUsername, string token, Credentials c)
    {
        try
        {
            var xml =
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <GetUserRequest xmlns="urn:ebay:apis:eBLBaseComponents">
                  <UserID>{System.Security.SecurityElement.Escape(sellerUsername)}</UserID>
                  <DetailLevel>ReturnAll</DetailLevel>
                </GetUserRequest>
                """;

            var client = httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
            {
                Content = new StringContent(xml, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("X-EBAY-API-SITEID", "0");
            request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
            request.Headers.Add("X-EBAY-API-CALL-NAME", "GetUser");
            request.Headers.Add("X-EBAY-API-APP-NAME",  c.EbayClientId);
            request.Headers.Add("X-EBAY-API-DEV-NAME",  c.EbayDevId);
            request.Headers.Add("X-EBAY-API-CERT-NAME", c.EbayClientSecret);
            request.Headers.Add("X-EBAY-API-IAF-TOKEN", token);

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return 0;

            var xmlBody = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlBody);
            var feedbackScoreStr = doc.Root?.Element(EbayNs + "User")?.Element(EbayNs + "FeedbackScore")?.Value;
            return int.TryParse(feedbackScoreStr, out var score) ? score : 0;
        }
        catch
        {
            return 0;
        }
    }

    public async Task ProactiveTokenRefreshAsync()
    {
        var refreshToken = creds.GetRefreshToken();
        if (string.IsNullOrWhiteSpace(refreshToken)) return;
        await RefreshAccessTokenAsync(refreshToken);
    }

    private async Task<string> GetOrRefreshTokenAsync()
    {
        var token = creds.GetUserToken();
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("No eBay user token. Connect your eBay account first.");

        if (creds.IsAccessTokenExpired())
        {
            var refreshToken = creds.GetRefreshToken();
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                log.Add("Warning", "Access token expired, no refresh token", "Re-authenticate via OAuth.");
                throw new InvalidOperationException("eBay access token has expired. Re-authenticate via OAuth.");
            }
            log.Add("Info", "Access token expired — refreshing", "Using stored refresh token");
            token = await RefreshAccessTokenAsync(refreshToken);
        }

        return token;
    }

    // ── Import listings (Trading API + Inventory API merged) ─────────────────

    public async Task<List<EbayListingSummary>> GetListingsAsync()
    {
        var c = creds.Get();
        var env = c.EbaySandbox ? "Sandbox" : "Production";
        log.Add("Info", "Import listings started",
            $"Environment: {env}; Base URL: {BaseUrl}; Token expired: {creds.IsAccessTokenExpired()}; Refresh available: {!string.IsNullOrWhiteSpace(creds.GetRefreshToken())}");

        var token = await GetOrRefreshTokenAsync();

        // Trading API — returns ALL listings including those created on the eBay website
        var tradingListings = new List<EbayListingSummary>();
        try
        {
            tradingListings = await GetTradingApiListingsAsync(token);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Trading API failed", ex.Message);
        }

        // Inventory API — returns only API-created listings but with richer structured data
        var inventoryListings = new List<EbayListingSummary>();
        try
        {
            inventoryListings = await GetInventoryApiListingsAsync(token);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Inventory API failed (non-fatal)", ex.Message);
        }

        // Merge: Trading API is the base (has all listings); the Inventory API's richer structured
        // product data wins where both describe the same listing.
        //
        // Field-level, not whole-object. The Inventory API has no concept of a watch count, a view
        // item URL, a category name or a start time, so replacing the Trading entry outright used
        // to blank all four on every API-created listing. Those are exactly the fields the
        // inventory-health scan runs on, so the Trading values are carried across explicitly.
        var merged = new Dictionary<string, EbayListingSummary>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in tradingListings)
            if (!string.IsNullOrEmpty(l.ListingId)) merged[l.ListingId] = l;
        foreach (var l in inventoryListings)
        {
            if (string.IsNullOrEmpty(l.ListingId)) continue;
            if (merged.TryGetValue(l.ListingId, out var fromTrading))
            {
                l.WatchCount   = fromTrading.WatchCount;
                l.ListingUrl   = fromTrading.ListingUrl;
                l.StartTimeUtc = fromTrading.StartTimeUtc;
                l.QuantitySold = fromTrading.QuantitySold;
                l.HitCount     = fromTrading.HitCount;
                if (string.IsNullOrWhiteSpace(l.Category)) l.Category = fromTrading.Category;
            }
            merged[l.ListingId] = l;
        }

        var result = merged.Values.OrderByDescending(l => l.WatchCount).ThenBy(l => l.Title).ToList();
        log.Add("Info", $"Import complete: {result.Count} listing(s)",
            $"Trading API: {tradingListings.Count}, Inventory API: {inventoryListings.Count}");

        return result;
    }

    // ── Trading API (GetMyeBaySelling) ────────────────────────────────────────

    private async Task<List<EbayListingSummary>> GetTradingApiListingsAsync(string token)
    {
        var c = creds.Get();
        log.Add("Info", "Calling eBay Trading API (GetMyeBaySelling)", TradingEndpoint);

        var results = new List<EbayListingSummary>();
        int pageNumber = 1;

        while (true)
        {
            var requestXml =
                $"""
                <?xml version="1.0" encoding="utf-8"?>
                <GetMyeBaySellingRequest xmlns="urn:ebay:apis:eBLBaseComponents">
                  <ActiveList>
                    <Include>true</Include>
                    <Pagination>
                      <EntriesPerPage>200</EntriesPerPage>
                      <PageNumber>{pageNumber}</PageNumber>
                    </Pagination>
                  </ActiveList>
                </GetMyeBaySellingRequest>
                """;

            var client = httpClientFactory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
            {
                Content = new StringContent(requestXml, Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("X-EBAY-API-SITEID", "0");
            request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
            request.Headers.Add("X-EBAY-API-CALL-NAME", "GetMyeBaySelling");
            request.Headers.Add("X-EBAY-API-APP-NAME",  c.EbayClientId);
            request.Headers.Add("X-EBAY-API-DEV-NAME",  c.EbayDevId);
            request.Headers.Add("X-EBAY-API-CERT-NAME", c.EbayClientSecret);
            request.Headers.Add("X-EBAY-API-IAF-TOKEN", token);

            var response  = await client.SendAsync(request);
            var xmlBody   = await response.Content.ReadAsStringAsync();

            log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
                $"Trading API HTTP {(int)response.StatusCode} (page {pageNumber})",
                response.IsSuccessStatusCode
                    ? $"Response length: {xmlBody.Length} chars"
                    : xmlBody[..Math.Min(400, xmlBody.Length)]);

            if (!response.IsSuccessStatusCode)
                throw new Exception($"Trading API HTTP {(int)response.StatusCode}: {xmlBody[..Math.Min(500, xmlBody.Length)]}");

            var doc  = XDocument.Parse(xmlBody);
            var root = doc.Root;
            if (root == null) break;

            var ack = root.Element(EbayNs + "Ack")?.Value ?? "";
            if (ack is "Failure" or "PartialFailure")
            {
                var errors = root.Descendants(EbayNs + "Errors")
                    .Where(e => (e.Element(EbayNs + "SeverityCode")?.Value ?? "") == "Error")
                    .Select(e => $"[{e.Element(EbayNs + "ErrorCode")?.Value}] {e.Element(EbayNs + "ShortMessage")?.Value}: {e.Element(EbayNs + "LongMessage")?.Value}")
                    .ToList();
                var msg = string.Join("; ", errors);
                log.Add("Warning", $"Trading API Ack={ack}", msg);
                if (ack == "Failure") throw new Exception($"Trading API Failure: {msg}");
                // PartialFailure: log but continue
            }

            var itemArray = root.Descendants(EbayNs + "ItemArray").FirstOrDefault();
            var items = itemArray?.Elements(EbayNs + "Item").ToList() ?? [];
            log.Add("Info", $"Trading API page {pageNumber}: {items.Count} item(s)", $"Ack: {ack}");

            foreach (var item in items)
            {
                var itemId  = Xstr(item, "ItemID");
                var title   = Xstr(item, "Title");
                var sku     = Xstr(item, "SKU");

                decimal price = 0;
                var priceEl = item.Descendants(EbayNs + "CurrentPrice").FirstOrDefault();
                if (priceEl != null)
                    decimal.TryParse(priceEl.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out price);

                int qty = 0;
                var qtyAvailEl = item.Descendants(EbayNs + "QuantityAvailable").FirstOrDefault();
                if (qtyAvailEl != null) int.TryParse(qtyAvailEl.Value, out qty);
                else int.TryParse(Xstr(item, "Quantity"), out qty);

                var thumbnail   = item.Descendants(EbayNs + "GalleryURL").FirstOrDefault()?.Value   ?? "";
                var listingUrl  = item.Descendants(EbayNs + "ViewItemURL").FirstOrDefault()?.Value  ?? "";
                var listingStatus = item.Descendants(EbayNs + "ListingStatus").FirstOrDefault()?.Value ?? "Active";
                var condition   = item.Descendants(EbayNs + "ConditionDisplayName").FirstOrDefault()?.Value ?? "";

                var primaryCat = item.Element(EbayNs + "PrimaryCategory");
                var categoryId = primaryCat?.Element(EbayNs + "CategoryID")?.Value   ?? "";
                var category   = primaryCat?.Element(EbayNs + "CategoryName")?.Value ?? "";

                int watchCount = 0;
                int.TryParse(Xstr(item, "WatchCount"), out watchCount);

                // How long it has been sitting, and what it did while it sat. GetMyeBaySelling
                // always returns StartTime; HitCount is only present on some accounts/call
                // versions, so it is read opportunistically and never depended on.
                DateTime? startTimeUtc = null;
                var startTimeText = item.Descendants(EbayNs + "StartTime").FirstOrDefault()?.Value;
                if (DateTime.TryParse(startTimeText, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal,
                        out var parsedStart))
                    startTimeUtc = parsedStart;

                int quantitySold = 0;
                int.TryParse(item.Descendants(EbayNs + "QuantitySold").FirstOrDefault()?.Value, out quantitySold);

                int hitCount = 0;
                int.TryParse(item.Descendants(EbayNs + "HitCount").FirstOrDefault()?.Value, out hitCount);

                var lastModified = item.Descendants(EbayNs + "TimeLeft").FirstOrDefault()?.Value ?? "";

                results.Add(new EbayListingSummary
                {
                    ListingId    = itemId,
                    OfferId      = "",
                    Sku          = sku,
                    Title        = title,
                    Status       = listingStatus.ToUpperInvariant(),
                    Price        = price,
                    Quantity     = qty,
                    CategoryId   = categoryId,
                    Category     = category,
                    Condition    = condition,
                    ThumbnailUrl = thumbnail,
                    WatchCount   = watchCount,
                    ListingUrl   = listingUrl,
                    StartTimeUtc = startTimeUtc,
                    QuantitySold = quantitySold,
                    HitCount     = hitCount,
                    LastUpdated  = "",
                    Data         = new PostListingRequest
                    {
                        Title       = title,
                        CategoryId  = categoryId,
                        Category    = category,
                        Price       = price,
                        Quantity    = qty,
                        Condition   = condition,
                        ImageUrls   = string.IsNullOrEmpty(thumbnail) ? [] : [thumbnail],
                    }
                });
            }

            // Pagination
            var paginationResult = root.Descendants(EbayNs + "PaginationResult").FirstOrDefault();
            var totalPages = 1;
            int.TryParse(paginationResult?.Element(EbayNs + "TotalNumberOfPages")?.Value, out totalPages);

            if (items.Count == 0 || pageNumber >= totalPages || pageNumber >= 50) break;
            pageNumber++;
        }

        log.Add("Info", $"Trading API: {results.Count} active listing(s) across {pageNumber} page(s)", "");
        return results;
    }

    // ── Inventory API listings ────────────────────────────────────────────────

    private async Task<List<EbayListingSummary>> GetInventoryApiListingsAsync(string token)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        List<JsonElement> rawOffers;
        try
        {
            rawOffers = await GetPagedArrayAsync(client, "/sell/inventory/v1/offer", "offers", 100);
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("400") && ex.Message.Contains("25707"))
            {
                log.Add("Info", "Inventory API: no API-created offers (HTTP 400/25707)",
                    "Listings created on the eBay website are not visible to the Inventory API.");
                return [];
            }
            throw;
        }

        var statusGroups = rawOffers.Count == 0
            ? "No offers"
            : string.Join("; ", rawOffers
                .GroupBy(o =>
                {
                    var s  = Str(o, "status");
                    var ls = o.TryGetProperty("listing", out var lst) ? Str(lst, "listingStatus") : "(no listing)";
                    return $"{s}/{ls}";
                })
                .Select(g => $"{g.Key}:{g.Count()}"));
        log.Add("Info", $"Inventory API: {rawOffers.Count} raw offer(s)", statusGroups);

        var offers = rawOffers.Where(IsActivePublishedOffer).ToList();
        log.Add("Info", $"Inventory API: {offers.Count} PUBLISHED+ACTIVE offer(s)", $"Dropped {rawOffers.Count - offers.Count}");

        var itemsBySku = new Dictionary<string, JsonElement>();
        try
        {
            foreach (var item in await GetPagedArrayAsync(client, "/sell/inventory/v1/inventory_item", "inventoryItems", 200))
            {
                if (item.TryGetProperty("sku", out var s))
                    itemsBySku[s.GetString()!] = item;
            }
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Inventory items API failed (non-fatal)", ex.Message);
        }

        var listings = new List<EbayListingSummary>();
        foreach (var offer in offers)
        {
            var sku        = Str(offer, "sku");
            var offerId    = Str(offer, "offerId");
            var categoryId = Str(offer, "categoryId");
            var format     = offer.TryGetProperty("format", out var fmt) ? fmt.GetString() ?? "FIXED_PRICE" : "FIXED_PRICE";
            var listingId  = offer.TryGetProperty("listing", out var lst) && lst.TryGetProperty("listingId", out var lid) ? lid.GetString() ?? "" : "";
            var offerStatus   = Str(offer, "status");
            var listingStatus = offer.TryGetProperty("listing", out var listing) ? Str(listing, "listingStatus") : "";
            var status     = string.IsNullOrWhiteSpace(listingStatus) ? offerStatus : listingStatus;

            decimal price = 0;
            if (offer.TryGetProperty("pricingSummary", out var ps) && ps.TryGetProperty("price", out var pv) && pv.TryGetProperty("value", out var pval))
                decimal.TryParse(pval.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out price);

            int qty = offer.TryGetProperty("availableQuantity", out var qp) ? qp.GetInt32() : 1;

            string title = "", brand = "", description = "", condition = "", conditionDesc = "", thumbnail = "";
            string mpn = "", upc = "", ean = "", isbn = "";
            var imageUrls = new List<string>();
            var specifics = new Dictionary<string, string>();

            if (itemsBySku.TryGetValue(sku, out var inv))
            {
                condition     = Str(inv, "condition");
                conditionDesc = inv.TryGetProperty("conditionDescription", out var cd) ? cd.GetString() ?? "" : "";
                if (inv.TryGetProperty("product", out var prod))
                {
                    title       = Str(prod, "title");
                    brand       = Str(prod, "brand");
                    description = Str(prod, "description");
                    mpn         = Str(prod, "mpn");
                    if (prod.TryGetProperty("upc",  out var u)) upc  = u.EnumerateArray().FirstOrDefault().GetString() ?? "";
                    if (prod.TryGetProperty("ean",  out var e)) ean  = e.EnumerateArray().FirstOrDefault().GetString() ?? "";
                    if (prod.TryGetProperty("isbn", out var i)) isbn = i.EnumerateArray().FirstOrDefault().GetString() ?? "";
                    if (prod.TryGetProperty("imageUrls", out var imgs))
                        foreach (var img in imgs.EnumerateArray()) imageUrls.Add(img.GetString() ?? "");
                    if (prod.TryGetProperty("aspects", out var asp))
                        foreach (var a in asp.EnumerateObject())
                            specifics[a.Name] = a.Value.EnumerateArray().FirstOrDefault().GetString() ?? "";
                }
            }

            thumbnail = imageUrls.FirstOrDefault() ?? "";

            listings.Add(new EbayListingSummary
            {
                OfferId      = offerId,
                ListingId    = listingId,
                Sku          = sku,
                Status       = status,
                Title        = title,
                CategoryId   = categoryId,
                LastUpdated  = Str(offer, "lastModifiedDate"),
                Price        = price,
                Quantity     = qty,
                Condition    = condition,
                ThumbnailUrl = thumbnail,
                Data = new PostListingRequest
                {
                    Title                = title,
                    CategoryId           = categoryId,
                    Condition            = condition,
                    ConditionDescription = conditionDesc,
                    Brand                = brand,
                    Mpn                  = mpn,
                    Upc                  = upc,
                    Ean                  = ean,
                    Isbn                 = isbn,
                    Description          = description,
                    Price                = price,
                    Quantity             = qty,
                    ListingFormat        = format,
                    ImageUrls            = imageUrls,
                    ItemSpecifics        = specifics,
                }
            });
        }

        return listings;
    }

    // ── Business Policies (Account API) ──────────────────────────────────────

    public async Task<BusinessPoliciesResult> GetBusinessPoliciesAsync()
    {
        var token = await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        var fulfillment = await FetchPoliciesAsync(client, "fulfillment_policy",  "fulfillmentPolicies", "fulfillmentPolicyId");
        var payment     = await FetchPoliciesAsync(client, "payment_policy",      "paymentPolicies",     "paymentPolicyId");
        var returnPol   = await FetchPoliciesAsync(client, "return_policy",       "returnPolicies",      "returnPolicyId");

        log.Add("Info", "Business policies loaded",
            $"Fulfillment: {fulfillment.Policies.Count}, Payment: {payment.Policies.Count}, Return: {returnPol.Policies.Count}");

        var errors = new[] { fulfillment.Error, payment.Error, returnPol.Error }
            .Where(e => !string.IsNullOrEmpty(e)).ToList();

        return new BusinessPoliciesResult(
            fulfillment.Policies, payment.Policies, returnPol.Policies,
            errors.Count > 0 ? string.Join("; ", errors) : null);
    }

    private async Task<(List<PolicyInfo> Policies, string? Error)> FetchPoliciesAsync(
        HttpClient client, string endpoint, string arrayName, string idField)
    {
        var c = creds.Get();
        var url = $"{BaseUrl}/sell/account/v1/{endpoint}?marketplace_id=EBAY_US";
        try
        {
            var res  = await client.GetAsync(url);
            var body = await res.Content.ReadAsStringAsync();

            log.Add(res.IsSuccessStatusCode ? "Info" : "Warning",
                $"Policy fetch {endpoint} HTTP {(int)res.StatusCode}",
                res.IsSuccessStatusCode
                    ? $"{arrayName} body length: {body.Length}"
                    : body[..Math.Min(400, body.Length)]);

            if (!res.IsSuccessStatusCode)
                return ([], $"{endpoint} HTTP {(int)res.StatusCode}: {body[..Math.Min(200, body.Length)]}");

            using var doc = JsonDocument.Parse(body);
            var list = new List<PolicyInfo>();
            if (doc.RootElement.TryGetProperty(arrayName, out var arr))
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var id   = Str(item, idField);
                    var name = Str(item, "name");
                    if (!string.IsNullOrEmpty(id))
                        list.Add(new PolicyInfo(id, name));
                }
            }
            return (list, null);
        }
        catch (Exception ex)
        {
            log.Add("Warning", $"Policy fetch {endpoint} exception", ex.Message);
            return ([], ex.Message);
        }
    }

    // ── Create / update listings ──────────────────────────────────────────────

    public async Task<string> CreateListingAsync(PostListingRequest req, string? userToken)
    {
        var token = !string.IsNullOrWhiteSpace(userToken) ? userToken : await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var sku = $"SKU-{Guid.NewGuid():N}"[..20];
        await CreateInventoryItemAsync(client, req, sku);
        return await CreateOfferAsync(client, req, sku);
    }

    public async Task<PublishListingResult> PublishListingAsync(PostListingRequest req)
    {
        var token = await GetOrRefreshTokenAsync();
        var listingId = await AddFixedPriceItemAsync(token, req);
        log.Add("Info", "eBay listing published live (Trading API)", $"Listing ID: {listingId}");
        return new PublishListingResult("", listingId, "");
    }

    // ── Trading API: AddFixedPriceItem ────────────────────────────────────────

    private static string TradingPackageType(string inventoryApiType) => inventoryApiType switch
    {
        "LETTER"                       => "Letter",
        "LARGE_ENVELOPE_OR_FLAT_PACK"  => "LargeEnvelope",
        "PACKAGE_THICK_ENVELOPE"       => "PackageThickEnvelope",
        "MAILING_BOX"                  => "Mailing",
        "BULKY_GOODS"                  => "BulkyGoods",
        "VERY_LARGE_PACKAGE"           => "BulkyGoods",
        _                              => "PackageThickEnvelope"
    };

    private static int ConditionId(string condition) => condition switch
    {
        "NEW"                       => 1000,
        "LIKE_NEW"                  => 2000,
        "USED_EXCELLENT"            => 3000,
        "USED_VERY_GOOD"            => 4000,
        "USED_GOOD"                 => 5000,
        "USED_ACCEPTABLE"           => 6000,
        "FOR_PARTS_OR_NOT_WORKING"  => 7000,
        _                           => 3000
    };

    private static string Xe(string? s) =>
        string.IsNullOrEmpty(s) ? "" : System.Security.SecurityElement.Escape(s)!;

    // Strip words and patterns that trigger eBay's "improper words" policy filter
    private static string CountryName(string code) => code.ToUpper() switch
    {
        "CN" => "China",
        "HK" => "Hong Kong",
        "GB" => "United Kingdom",
        "DE" => "Germany",
        "JP" => "Japan",
        "CA" => "Canada",
        "AU" => "Australia",
        _    => "United States",
    };

    private static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrEmpty(title)) return "";
        return title
            .Replace("—", "-").Replace("–", "-")
            .Replace("'", "'").Replace("'", "'")
            .Replace(""", "\"").Replace(""", "\"")
            .Replace("…", "...").Replace("®", "").Replace("™", "").Replace("©", "")
            .Replace("�", "");
    }

    private static string SanitizeDescription(string? desc)
    {
        if (string.IsNullOrEmpty(desc)) return "";
        // Replace fancy Unicode punctuation with ASCII equivalents
        desc = desc
            .Replace("—", "-").Replace("–", "-")   // em/en dash
            .Replace("‘", "'").Replace("’", "'")   // smart single quotes
            .Replace("“", "\"").Replace("”", "\"") // smart double quotes
            .Replace("…", "...").Replace("®", "")  // ellipsis, registered TM
            .Replace("™", "").Replace("©", "")     // TM symbol, copyright
            .Replace("�", "");                          // replacement character
        // Remove external URLs — eBay flags href/src pointing off-eBay
        desc = System.Text.RegularExpressions.Regex.Replace(
            desc, @"(https?://|www\.)\S+", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Remove href and src attributes entirely
        desc = System.Text.RegularExpressions.Regex.Replace(
            desc, @"\s*(href|src)\s*=\s*[""'][^""']*[""']", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        // Replace flagged phrases
        var replacements = new (string Pattern, string With)[]
        {
            (@"\bguaranteed?\b",                        "assured"),
            (@"\bwarranty\b",                           "coverage"),
            (@"\bbest price\b",                         "great value"),
            (@"\blowest price\b",                       "competitive price"),
            (@"\bcheapest\b",                           "best value"),
            (@"\bclick here\b",                         "see details"),
            (@"\bcontact us\b",                         "contact seller"),
            (@"\bmessage (?:us|me|seller)\b",           "contact seller via eBay"),
            (@"[\-–]\s*verify before publishing[^<\n]*",""), // strip Claude's internal notes
            (@"\bverify before publishing\b[^<\n]*",    ""),
            (@"\[email[^\]]*\]",                        ""),
            (@"\b[\w.+-]+@[\w-]+\.\w+\b",               ""),  // email addresses
            (@"\b\d{3}[-.\s]\d{3}[-.\s]\d{4}\b",        ""),  // phone numbers
        };
        foreach (var (pattern, with) in replacements)
            desc = System.Text.RegularExpressions.Regex.Replace(
                desc, pattern, with, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return desc;
    }

    private async Task<string> AddFixedPriceItemAsync(string token, PostListingRequest req)
    {
        var c = creds.Get();

        var fulfillmentId = !string.IsNullOrWhiteSpace(req.FulfillmentPolicyId) ? req.FulfillmentPolicyId : c.EbayFulfillmentPolicyId;
        var paymentId     = !string.IsNullOrWhiteSpace(req.PaymentPolicyId)     ? req.PaymentPolicyId     : c.EbayPaymentPolicyId;
        var returnId      = !string.IsNullOrWhiteSpace(req.ReturnPolicyId)      ? req.ReturnPolicyId      : c.EbayReturnPolicyId;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(fulfillmentId)) missing.Add("Fulfillment Policy ID");
        if (string.IsNullOrWhiteSpace(paymentId))     missing.Add("Payment Policy ID");
        if (string.IsNullOrWhiteSpace(returnId))      missing.Add("Return Policy ID");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"eBay Seller Policies not configured: {string.Join(", ", missing)}. Open Settings → eBay Seller Policies.");

        var country  = string.IsNullOrWhiteSpace(req.ItemLocationCountry) ? "US" : req.ItemLocationCountry;
        var duration = req.ListingFormat == "AUCTION" ? $"Days_{req.DurationDays}" : "GTC";
        var condId   = ConditionId(req.Condition);

        // Validate category — if the AI gave us a malformed or unknown ID, fall back via suggestions
        var categoryId = (req.CategoryId ?? "").Replace(",", "").Trim();
        if (string.IsNullOrWhiteSpace(categoryId) || !categoryId.All(char.IsDigit))
        {
            var suggestions = await GetCategorySuggestionsAsync(req.Title ?? req.Category ?? "item");
            categoryId = suggestions.FirstOrDefault()?.Id ?? "99";
            log.Add("Info", "Category ID corrected via suggestion", $"'{req.CategoryId}' → '{categoryId}'");
        }

        // ── Category lookup table — keyword → verified eBay leaf category ID ────
        var titleLower = (req.Title ?? "").ToLowerInvariant();
        var catLower   = (req.Category ?? "").ToLowerInvariant();
        var combined   = titleLower + " " + catLower;

        // Mining hardware — ALL types belong in 179171 (Miners)
        var miningKeywords = new[]
        {
            // Complete miners
            "antminer", "whatsminer", "goldshell", "iceriver", "jasminer", "avalon", "canaan",
            "innosilicon", "bitmain", "microbt", "strongu", "ebang", "aladdin",
            // Model numbers
            "s19", "s21", "s17", "s15", "t19", "t21", "t17", "l7", "l9", "ka3", "ks0", "ks1",
            "ks2", "ks3", "ks5", "kd6", "kd9", "al3", "x16", "x4", "m20", "m30", "m50", "m60",
            // Components
            "hash board", "hashboard", "control board", "controller board", "hash rate board",
            "cb6", "cb7", "cb8", "a113", "a112", "a111", "bm1387", "bm1397", "bm1366", "bm1368",
            // PSUs
            "apw3", "apw7", "apw9", "apw12", "apw17", "p21", "p17", "c13", "server psu", "mining psu",
            "miner power supply", "1800w psu", "2200w psu", "2400w psu", "3200w psu",
            // Tools & accessories
            "test fixture", "miner repair", "hash board tester", "chip tester", "miner tool",
            "mining repair", "miner fan", "mining cable", "pcie cable", "mining frame",
            // General
            "asic", "bitcoin miner", "btc miner", "sha-256 miner", "scrypt miner",
            "crypto miner", "mining rig", "cryptocurrency miner", "miner",
        };

        // Vitamins & supplements — 180959
        var supplementKeywords = new[]
        {
            "vitamin", "supplement", "coq10", "omega-3", "omega 3", "fish oil", "probiotics",
            "magnesium", "zinc", "capsule", "softgel", "gummy vitamin", "multivitamin",
            "collagen", "protein powder", "pre-workout", "creatine", "whey protein"
        };

        // Electronics components — 64800 (Electronic Components & Semiconductors)
        var electronicsKeywords = new[]
        {
            "capacitor", "resistor", "transistor", "mosfet", "ic chip", "microcontroller",
            "arduino", "raspberry pi", "pcb board", "soldering", "oscilloscope"
        };

        var keywordOverrideApplied = false;

        if (miningKeywords.Any(k => combined.Contains(k)))
        {
            if (categoryId != "179171")
                log.Add("Info", "Category → 179171 (Miners)", $"Was: {categoryId}");
            categoryId = "179171";
            keywordOverrideApplied = true;
        }
        else if (supplementKeywords.Any(k => combined.Contains(k)))
        {
            if (categoryId != "180959")
                log.Add("Info", "Category → 180959 (Vitamins & Minerals)", $"Was: {categoryId}");
            categoryId = "180959";
            keywordOverrideApplied = true;
        }
        else if (electronicsKeywords.Any(k => combined.Contains(k)))
        {
            if (categoryId != "64800")
                log.Add("Info", "Category → 64800 (Electronic Components)", $"Was: {categoryId}");
            categoryId = "64800";
            keywordOverrideApplied = true;
        }

        // Taxonomy leaf-check — skip if we already applied a keyword override (those are known-good leaf categories)
        if (!keywordOverrideApplied)
        {
            try
            {
                var leafToken   = await GetOrRefreshTokenAsync();
                var leafClient  = httpClientFactory.CreateClient();
                var treeId      = _categoryTreeId ?? "0";
                var leafRequest = new HttpRequestMessage(HttpMethod.Get,
                    $"https://api.ebay.com/commerce/taxonomy/v1/category_tree/{treeId}/get_category_subtree?category_id={categoryId}");
                leafRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", leafToken);
                var leafResp = await leafClient.SendAsync(leafRequest);
                if (leafResp.IsSuccessStatusCode)
                {
                    using var leafDoc = System.Text.Json.JsonDocument.Parse(await leafResp.Content.ReadAsStringAsync());
                    if (leafDoc.RootElement.TryGetProperty("categorySubtreeNode", out var subtreeNode))
                    {
                        var isLeaf = subtreeNode.TryGetProperty("leafCategoryTreeNode", out var lv) && lv.GetBoolean();
                        if (!isLeaf)
                        {
                            var suggestions = await GetCategorySuggestionsAsync(req.Title ?? "item");
                            var corrected   = suggestions.FirstOrDefault()?.Id ?? "179171";
                            log.Add("Warning", "Non-leaf category auto-corrected", $"{categoryId} → {corrected}");
                            categoryId = corrected;
                        }
                        else
                        {
                            log.Add("Info", "Category leaf-check passed", categoryId);
                        }
                    }
                }
            }
            catch { /* non-fatal — proceed with current categoryId */ }
        }
        else
        {
            log.Add("Info", "Category leaf-check skipped (keyword override)", categoryId);
        }

        var aspectsXml = req.ItemSpecifics.Count > 0
            ? "<ItemSpecifics>" + string.Join("", req.ItemSpecifics.Select(kv =>
                $"<NameValueList><Name>{Xe(kv.Key)}</Name><Value>{Xe(kv.Value)}</Value></NameValueList>")) + "</ItemSpecifics>"
            : "";

        var publicImageUrls = req.ImageUrls
            .Where(u => !string.IsNullOrEmpty(u) && u.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .Take(12).ToList();
        var pictureXml = publicImageUrls.Count > 0
            ? "<PictureDetails>" + string.Join("", publicImageUrls.Select(u =>
                $"<PictureURL>{Xe(u)}</PictureURL>")) + "</PictureDetails>"
            : "";

        var descCdata = $"<![CDATA[{SanitizeDescription(req.Description)}]]>";

        // ShippingPackageDetails omitted — covered by the seller's fulfillment policy

        var bestOfferXml = req.BestOfferEnabled
            ? $"""
              <BestOfferDetails>
                <BestOfferEnabled>true</BestOfferEnabled>
              </BestOfferDetails>
              """
            : "";

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <AddFixedPriceItemRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <Item>
                <Title>{Xe(SanitizeTitle(req.Title))}</Title>
                {(string.IsNullOrWhiteSpace(req.Subtitle) ? "" : $"<SubTitle>{Xe(req.Subtitle)}</SubTitle>")}
                <Description>{descCdata}</Description>
                <PrimaryCategory><CategoryID>{Xe(categoryId)}</CategoryID></PrimaryCategory>
                {(string.IsNullOrWhiteSpace(req.SecondaryCategoryId) ? "" : $"<SecondaryCategory><CategoryID>{Xe(req.SecondaryCategoryId)}</CategoryID></SecondaryCategory>")}
                <ListingType>FixedPriceItem</ListingType>
                <ListingDuration>{duration}</ListingDuration>
                <StartPrice>{req.Price:F2}</StartPrice>
                <Currency>USD</Currency>
                <Country>{country}</Country>
                {(string.IsNullOrWhiteSpace(req.ItemLocationPostalCode) ? "" : $"<PostalCode>{Xe(req.ItemLocationPostalCode)}</PostalCode>")}
                <Location>{(string.IsNullOrWhiteSpace(req.ItemLocationPostalCode) ? CountryName(country) : Xe(req.ItemLocationPostalCode))}</Location>
                <DispatchTimeMax>{req.HandlingTimeBusinessDays}</DispatchTimeMax>
                <Quantity>{req.Quantity}</Quantity>
                <ConditionID>{condId}</ConditionID>
                {(string.IsNullOrWhiteSpace(req.ConditionDescription) ? "" : $"<ConditionDescription>{Xe(req.ConditionDescription)}</ConditionDescription>")}
                {aspectsXml}
                {pictureXml}
                {bestOfferXml}
                <SellerProfiles>
                  <SellerShippingProfile><ShippingProfileID>{fulfillmentId}</ShippingProfileID></SellerShippingProfile>
                  <SellerReturnProfile><ReturnProfileID>{returnId}</ReturnProfileID></SellerReturnProfile>
                  <SellerPaymentProfile><PaymentProfileID>{paymentId}</PaymentProfileID></SellerPaymentProfile>
                </SellerProfiles>
                {(req.QuantityLimitPerBuyer.HasValue ? $"<MaximumBuyerCount>{req.QuantityLimitPerBuyer}</MaximumBuyerCount>" : "")}
                {(req.PrivateListing ? "<HideFromSearch>true</HideFromSearch>" : "")}
              </Item>
            </AddFixedPriceItemRequest>
            """;

        var sanitizedDesc = SanitizeDescription(req.Description ?? "");
        log.Add("Info", "AFPI:Title", req.Title ?? "");
        // Log full description in chunks so nothing is missed
        var descChunks = Enumerable.Range(0, (sanitizedDesc.Length + 999) / 1000)
            .Select(i => sanitizedDesc.Substring(i * 1000, Math.Min(1000, sanitizedDesc.Length - i * 1000)));
        int chunk = 1;
        foreach (var c2 in descChunks)
            log.Add("Info", $"AFPI:Desc:{chunk++}", c2);

        var client  = httpClientFactory.CreateClient();

        // Longer than the 100-second default, deliberately, and this is the one place in the app
        // where a *longer* timeout is the safer choice. Giving up on AddFixedPriceItem does not
        // cancel it at eBay's end: a listing created just after we stopped waiting is live, invisible
        // to the app, and about to be duplicated by a seller who reasonably assumes it failed. Waiting
        // three minutes for a slow-but-successful publish costs patience; timing out on one costs a
        // second set of eBay fees and an oversell.
        client.Timeout = TimeSpan.FromMinutes(3);

        var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("X-EBAY-API-CALL-NAME",           "AddFixedPriceItem");
        request.Headers.Add("X-EBAY-API-SITEID",              "0");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        request.Headers.Add("X-EBAY-API-APP-NAME",            c.EbayClientId);
        request.Headers.Add("X-EBAY-API-DEV-NAME",            c.EbayDevId);
        request.Headers.Add("X-EBAY-API-CERT-NAME",           c.EbayClientSecret);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN",           token);

        var response     = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"AddFixedPriceItem HTTP {(int)response.StatusCode}",
            responseBody[..Math.Min(500, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"AddFixedPriceItem failed (HTTP {(int)response.StatusCode}): {responseBody}");

        var xdoc = XDocument.Parse(responseBody);
        var ack  = xdoc.Descendants(EbayNs + "Ack").FirstOrDefault()?.Value ?? "";
        if (ack == "Failure")
        {
            var errMsg = xdoc.Descendants(EbayNs + "LongMessage").FirstOrDefault()?.Value
                      ?? xdoc.Descendants(EbayNs + "ShortMessage").FirstOrDefault()?.Value
                      ?? responseBody;
            throw new Exception($"AddFixedPriceItem failed: {errMsg}");
        }

        var itemId = xdoc.Descendants(EbayNs + "ItemID").FirstOrDefault()?.Value ?? "";
        if (string.IsNullOrEmpty(itemId))
            throw new Exception($"AddFixedPriceItem: no ItemID in response. Ack={ack}");

        return itemId;
    }

    // ── Trading API: ReviseInventoryStatus (price/quantity-only revision) ──────
    // Listings pulled in via GetMyeBaySelling (see GetPagedArrayAsync/GetListingsAsync) were
    // created directly on eBay or through the Trading API, not through this app's Inventory API
    // flow — they have a real ItemID (ListingId) but no Inventory API offerId, so
    // UpdateListingAsync's PUT /sell/inventory/v1/offer/{offerId} call has nothing to target and
    // can't touch them at all. ReviseInventoryStatus is the Trading API's purpose-built call for
    // exactly this: bump price/quantity on an existing ItemID without re-submitting the whole
    // item (title, category, policies, etc.) — same HTTP/XML/header plumbing already proven
    // working in AddFixedPriceItemAsync above, just a much smaller payload.
    public async Task ReviseInventoryStatusAsync(string itemId, decimal price, int quantity)
    {
        var token = await GetOrRefreshTokenAsync();
        var c = creds.Get();

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ReviseInventoryStatusRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <InventoryStatus>
                <ItemID>{Xe(itemId)}</ItemID>
                <StartPrice>{price:F2}</StartPrice>
                <Quantity>{quantity}</Quantity>
              </InventoryStatus>
            </ReviseInventoryStatusRequest>
            """;

        var client  = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("X-EBAY-API-CALL-NAME",           "ReviseInventoryStatus");
        request.Headers.Add("X-EBAY-API-SITEID",              "0");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        request.Headers.Add("X-EBAY-API-APP-NAME",            c.EbayClientId);
        request.Headers.Add("X-EBAY-API-DEV-NAME",            c.EbayDevId);
        request.Headers.Add("X-EBAY-API-CERT-NAME",           c.EbayClientSecret);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN",           token);

        var response     = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"ReviseInventoryStatus HTTP {(int)response.StatusCode}",
            responseBody[..Math.Min(500, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"ReviseInventoryStatus failed (HTTP {(int)response.StatusCode}): {responseBody}");

        var xdoc = XDocument.Parse(responseBody);
        var ack  = xdoc.Descendants(EbayNs + "Ack").FirstOrDefault()?.Value ?? "";
        if (ack is "Failure")
        {
            var errMsg = xdoc.Descendants(EbayNs + "LongMessage").FirstOrDefault()?.Value
                      ?? xdoc.Descendants(EbayNs + "ShortMessage").FirstOrDefault()?.Value
                      ?? responseBody;
            throw new Exception($"ReviseInventoryStatus failed: {errMsg}");
        }
    }

    private async Task CreateInventoryItemAsync(HttpClient client, PostListingRequest req, string sku, string? locationKey = null)
    {
        var totalOz = req.WeightLbs * 16 + req.WeightOz;

        object? packageWeightAndSize = (totalOz > 0 || req.PackageLengthIn > 0) ? new
        {
            dimensions = req.PackageLengthIn > 0 ? new
            {
                height = (double)req.PackageHeightIn,
                length = (double)req.PackageLengthIn,
                width  = (double)req.PackageWidthIn,
                unit   = "INCH"
            } : null,
            weight      = totalOz > 0 ? new { value = (double)totalOz, unit = "OUNCE" } : null,
            packageType = string.IsNullOrEmpty(req.PackageType) ? null : req.PackageType
        } : null;

        var country = string.IsNullOrWhiteSpace(req.ItemLocationCountry) ? "US" : req.ItemLocationCountry;
        // Use registered merchant location key when available (required for production publish)
        object itemLocation = !string.IsNullOrEmpty(locationKey)
            ? new { locationId = locationKey }
            : string.IsNullOrWhiteSpace(req.ItemLocationPostalCode)
                ? (object)new { country }
                : new { postalCode = req.ItemLocationPostalCode, country };

        var payload = new
        {
            availability = new { shipToLocationAvailability = new { quantity = req.Quantity } },
            condition    = req.Condition,
            conditionDescription = string.IsNullOrWhiteSpace(req.ConditionDescription) ? null : req.ConditionDescription,
            itemLocation,
            packageWeightAndSize,
            product = new
            {
                title       = req.Title,
                subtitle    = string.IsNullOrWhiteSpace(req.Subtitle) ? null : req.Subtitle,
                description = TruncateDescription(req.Description),
                brand       = string.IsNullOrWhiteSpace(req.Brand) ? null : req.Brand,
                mpn         = string.IsNullOrWhiteSpace(req.Mpn) ? null : req.Mpn,
                upc         = string.IsNullOrEmpty(req.Upc)  ? null : new[] { req.Upc },
                ean         = string.IsNullOrEmpty(req.Ean)  ? null : new[] { req.Ean },
                isbn        = string.IsNullOrEmpty(req.Isbn) ? null : new[] { req.Isbn },
                aspects     = req.ItemSpecifics.Count > 0
                    ? req.ItemSpecifics.ToDictionary(k => k.Key, k => new[] { k.Value })
                    : null,
                imageUrls = req.ImageUrls.Count > 0 ? req.ImageUrls : null
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Put,
            $"{BaseUrl}/sell/inventory/v1/inventory_item/{Uri.EscapeDataString(sku)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json")
        };
        request.Content.Headers.Add("Content-Language", "en-US");

        var response = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"CreateInventoryItem HTTP {(int)response.StatusCode}",
            response.IsSuccessStatusCode
                ? $"SKU: {sku}"
                : responseBody[..Math.Min(600, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Inventory item failed (HTTP {(int)response.StatusCode}): {responseBody}");
    }

    private async Task<string> CreateOfferAsync(HttpClient client, PostListingRequest req, string sku)
    {
        var c = creds.Get();

        // Per-listing IDs take priority; fall back to saved credentials
        var fulfillmentId = !string.IsNullOrWhiteSpace(req.FulfillmentPolicyId) ? req.FulfillmentPolicyId : c.EbayFulfillmentPolicyId;
        var paymentId     = !string.IsNullOrWhiteSpace(req.PaymentPolicyId)     ? req.PaymentPolicyId     : c.EbayPaymentPolicyId;
        var returnId      = !string.IsNullOrWhiteSpace(req.ReturnPolicyId)      ? req.ReturnPolicyId      : c.EbayReturnPolicyId;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(fulfillmentId)) missing.Add("Fulfillment Policy ID");
        if (string.IsNullOrWhiteSpace(paymentId))     missing.Add("Payment Policy ID");
        if (string.IsNullOrWhiteSpace(returnId))      missing.Add("Return Policy ID");
        if (missing.Count > 0)
            throw new InvalidOperationException(
                $"eBay Seller Policies not configured: {string.Join(", ", missing)}. " +
                "Open Settings → eBay Seller Policies and select policies, or pick them in the New Listing form.");

        var duration = req.ListingFormat == "FIXED_PRICE" ? "GTC" : $"DAYS_{req.DurationDays}";

        var payload = new
        {
            sku,
            marketplaceId    = "EBAY_US",
            format           = req.ListingFormat,
            availableQuantity = req.Quantity,
            categoryId       = req.CategoryId,
            secondaryCategoryId = string.IsNullOrEmpty(req.SecondaryCategoryId) ? null : req.SecondaryCategoryId,
            listingDescription = req.Description,
            listingPolicies  = new
            {
                fulfillmentPolicyId = fulfillmentId,
                paymentPolicyId     = paymentId,
                returnPolicyId      = returnId
            },
            pricingSummary   = new { price = new { value = req.Price.ToString("F2"), currency = "USD" } },
            listingDuration  = duration,
            quantityLimitPerBuyer = req.QuantityLimitPerBuyer,
            hideBuyerDetails = req.PrivateListing ? true : (bool?)null,
            bestOfferTerms   = req.BestOfferEnabled ? new
            {
                bestOfferEnabled  = true,
                autoAcceptPrice   = req.AutoAcceptPrice.HasValue  ? new { value = req.AutoAcceptPrice.Value.ToString("F2"),  currency = "USD" } : null,
                autoDeclinePrice  = req.AutoDeclinePrice.HasValue ? new { value = req.AutoDeclinePrice.Value.ToString("F2"), currency = "USD" } : null
            } : null,
            charity = req.CharityDonationPercentage > 0 && !string.IsNullOrEmpty(req.CharityId)
                ? new { charityId = req.CharityId, donationPercentage = req.CharityDonationPercentage.ToString() }
                : null
        };

        var offerContent = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        offerContent.Headers.Add("Content-Language", "en-US");
        var response = await client.PostAsync($"{BaseUrl}/sell/inventory/v1/offer", offerContent);

        var offerBody = await response.Content.ReadAsStringAsync();
        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"CreateOffer HTTP {(int)response.StatusCode}",
            response.IsSuccessStatusCode
                ? offerBody[..Math.Min(200, offerBody.Length)]
                : offerBody[..Math.Min(600, offerBody.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Create offer failed (HTTP {(int)response.StatusCode}): {offerBody}");

        using var offerDoc = JsonDocument.Parse(offerBody);
        return offerDoc.RootElement.GetProperty("offerId").GetString() ?? "";
    }

    public async Task<SellerHubDraftResult> CreateSellerHubDraftAsync(PostListingRequest req)
    {
        var token = await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.DefaultRequestHeaders.Add("X-EBAY-C-MARKETPLACE-ID", "EBAY_US");

        var payload = new
        {
            product = new
            {
                title       = req.Title,
                subtitle    = string.IsNullOrWhiteSpace(req.Subtitle)     ? null : req.Subtitle,
                description = string.IsNullOrWhiteSpace(req.Description)  ? null : req.Description,
                imageUrls   = req.ImageUrls.Count > 0 ? req.ImageUrls : null,
                brand       = string.IsNullOrWhiteSpace(req.Brand) ? null : req.Brand,
                mpn         = string.IsNullOrWhiteSpace(req.Mpn)   ? null : req.Mpn,
                upc         = string.IsNullOrEmpty(req.Upc)  ? null : new[] { req.Upc },
                ean         = string.IsNullOrEmpty(req.Ean)  ? null : new[] { req.Ean },
                isbn        = string.IsNullOrEmpty(req.Isbn) ? null : new[] { req.Isbn },
                aspects     = req.ItemSpecifics.Count > 0
                    ? req.ItemSpecifics.ToDictionary(k => k.Key, k => new[] { k.Value })
                    : null,
            },
            categoryId           = string.IsNullOrEmpty(req.CategoryId)          ? null : req.CategoryId,
            condition            = string.IsNullOrEmpty(req.Condition)            ? null : req.Condition,
            conditionDescription = string.IsNullOrEmpty(req.ConditionDescription) ? null : req.ConditionDescription,
            format               = string.IsNullOrEmpty(req.ListingFormat)        ? "FIXED_PRICE" : req.ListingFormat,
            listingDescription   = string.IsNullOrWhiteSpace(req.Description)    ? null : TruncateDescription(req.Description),
            price                = req.Price > 0 ? new { value = req.Price.ToString("F2"), currency = "USD" } : null,
        };

        var content = new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json");
        content.Headers.Add("Content-Language", "en-US");

        var response = await client.PostAsync($"{BaseUrl}/sell/listing/v1_beta/item_draft", content);
        var body = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"CreateSellerHubDraft HTTP {(int)response.StatusCode}",
            response.IsSuccessStatusCode
                ? body[..Math.Min(200, body.Length)]
                : body[..Math.Min(600, body.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Seller Hub draft failed (HTTP {(int)response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var draftId      = doc.RootElement.TryGetProperty("item_draft_id",  out var id)  ? id.GetString()  ?? "" : "";
        var sellerHubUrl = doc.RootElement.TryGetProperty("seller_hub_url", out var url) ? url.GetString() ?? "" : "";

        return new SellerHubDraftResult(draftId, sellerHubUrl);
    }

    public async Task UpdateListingAsync(UpdateListingRequest req)
    {
        // No Inventory API offerId means this listing wasn't created through that API (imported
        // via GetMyeBaySelling instead — see the ReviseInventoryStatusAsync comment above for
        // why) — the offer/{offerId} PUT below has nothing to target, so fall back to the
        // Trading API price/quantity revision, which only needs the ItemID.
        if (string.IsNullOrWhiteSpace(req.OfferId) && !string.IsNullOrWhiteSpace(req.ListingId))
        {
            await ReviseInventoryStatusAsync(req.ListingId, req.Price, req.Quantity);
            return;
        }

        var token = !string.IsNullOrWhiteSpace(req.EbayToken) ? req.EbayToken : await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await CreateInventoryItemAsync(client, req, req.Sku);

        var c = creds.Get();
        var duration = req.ListingFormat == "FIXED_PRICE" ? "GTC" : $"DAYS_{req.DurationDays}";
        var payload = new
        {
            availableQuantity  = req.Quantity,
            categoryId         = req.CategoryId,
            secondaryCategoryId = string.IsNullOrEmpty(req.SecondaryCategoryId) ? null : req.SecondaryCategoryId,
            listingDescription = req.Description,
            listingPolicies    = new
            {
                fulfillmentPolicyId = c.EbayFulfillmentPolicyId,
                paymentPolicyId     = c.EbayPaymentPolicyId,
                returnPolicyId      = c.EbayReturnPolicyId
            },
            pricingSummary     = new { price = new { value = req.Price.ToString("F2"), currency = "USD" } },
            listingDuration    = duration,
            marketplaceId      = "EBAY_US",
            format             = req.ListingFormat,
            quantityLimitPerBuyer = req.QuantityLimitPerBuyer,
            hideBuyerDetails   = req.PrivateListing ? true : (bool?)null,
            bestOfferTerms     = req.BestOfferEnabled ? new
            {
                bestOfferEnabled  = true,
                autoAcceptPrice   = req.AutoAcceptPrice.HasValue  ? new { value = req.AutoAcceptPrice.Value.ToString("F2"),  currency = "USD" } : null,
                autoDeclinePrice  = req.AutoDeclinePrice.HasValue ? new { value = req.AutoDeclinePrice.Value.ToString("F2"), currency = "USD" } : null
            } : null
        };

        var response = await client.PutAsync(
            $"{BaseUrl}/sell/inventory/v1/offer/{Uri.EscapeDataString(req.OfferId)}",
            new StringContent(JsonSerializer.Serialize(payload, _json), Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Update offer failed: {await response.Content.ReadAsStringAsync()}");
    }

    // ── Paging helper ─────────────────────────────────────────────────────────

    private async Task<List<JsonElement>> GetPagedArrayAsync(HttpClient client, string path, string arrayName, int limit)
    {
        var results = new List<JsonElement>();
        var separator = path.Contains('?') ? '&' : '?';
        var offset = 0;

        while (true)
        {
            var url = $"{BaseUrl}{path}{separator}limit={limit}&offset={offset}";
            var response = await client.GetAsync(url);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception($"eBay {arrayName} API returned HTTP {(int)response.StatusCode}: {body}");
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var pageCount = 0;
            if (doc.RootElement.TryGetProperty(arrayName, out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    results.Add(item.Clone());
                    pageCount++;
                }
            }

            if (pageCount == 0 || !doc.RootElement.TryGetProperty("next", out _))
                break;

            offset += pageCount;
        }

        return results;
    }

    private static bool IsActivePublishedOffer(JsonElement offer)
    {
        if (!Str(offer, "status").Equals("PUBLISHED", StringComparison.OrdinalIgnoreCase))
            return false;

        return offer.TryGetProperty("listing", out var listing) &&
            Str(listing, "listingStatus").Equals("ACTIVE", StringComparison.OrdinalIgnoreCase);
    }

    // ── OAuth URL helpers ─────────────────────────────────────────────────────

    private string GetOAuthRedirectUri(bool forceProduction)
    {
        var c = creds.Get();
        if (string.IsNullOrWhiteSpace(c.EbayRuName))
            throw new InvalidOperationException("eBay RuName is not configured. Open Settings to add it.");
        return c.EbayRuName;
    }

    private static (string Code, string State) ParseProductionRedirectUrl(string redirectUrl)
    {
        if (!Uri.TryCreate(redirectUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Paste the full eBay OAuth redirect URL you were sent to after login.");

        if (!uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Redirect URL must be an https:// URL.");

        var query = QueryHelpers.ParseQuery(uri.Query);
        var code  = query.TryGetValue("code",  out var cv) ? cv.ToString() : "";
        var state = query.TryGetValue("state", out var sv) ? sv.ToString() : "";

        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("The pasted URL does not contain a code parameter. Copy the full redirect URL.");

        return (code, state);
    }

    // Truncates description to eBay's 4000-char limit, ending at a tag boundary when possible
    private static string TruncateDescription(string? description, int max = 4000)
    {
        if (string.IsNullOrEmpty(description) || description.Length <= max) return description ?? "";
        // Try to cut before an opening tag within the last 200 chars of the limit
        var tagPos = description.LastIndexOf('<', max - 1);
        if (tagPos > max - 200) return description[..tagPos].TrimEnd();
        return description[..max];
    }

    // Extracts a string from a JSON element property
    private static string Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) ? v.GetString() ?? "" : "";

    // ── Fetch existing eBay listing by item ID ────────────────────────────────

    public async Task<ListingData> GetItemAsync(string itemId)
    {
        var token = await GetOrRefreshTokenAsync();
        var c     = creds.Get();

        var xml = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <GetItemRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <ItemID>{itemId}</ItemID>
              <DetailLevel>ReturnAll</DetailLevel>
              <IncludeItemSpecifics>true</IncludeItemSpecifics>
            </GetItemRequest>
            """;

        var client  = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint)
        {
            Content = new StringContent(xml, Encoding.UTF8, "text/xml")
        };
        request.Headers.Add("X-EBAY-API-CALL-NAME",           "GetItem");
        request.Headers.Add("X-EBAY-API-SITEID",              "0");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        request.Headers.Add("X-EBAY-API-APP-NAME",            c.EbayClientId);
        request.Headers.Add("X-EBAY-API-DEV-NAME",            c.EbayDevId);
        request.Headers.Add("X-EBAY-API-CERT-NAME",           c.EbayClientSecret);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN",           token);

        var response = await client.SendAsync(request);
        var body     = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"GetItem HTTP {(int)response.StatusCode}", body[..Math.Min(300, body.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"GetItem failed (HTTP {(int)response.StatusCode})");

        var xdoc = XDocument.Parse(body);
        var item = xdoc.Descendants(EbayNs + "Item").FirstOrDefault()
            ?? throw new Exception("No Item element in GetItem response");

        string XS(string name) => item.Element(EbayNs + name)?.Value ?? "";

        var categoryId   = item.Element(EbayNs + "PrimaryCategory")?.Element(EbayNs + "CategoryID")?.Value ?? "";
        var categoryName = item.Element(EbayNs + "PrimaryCategory")?.Element(EbayNs + "CategoryName")?.Value ?? "";
        var price        = decimal.TryParse(
                               item.Descendants(EbayNs + "StartPrice").FirstOrDefault()?.Value ?? "",
                               System.Globalization.NumberStyles.Any,
                               System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0;
        // Always default to 1 when importing — eBay's Quantity field is a running GTC total,
        // not the seller's intended stock for a new listing
        var qty = 1;

        // Map Trading API ConditionID → our enum
        var conditionId = XS("ConditionID");
        var condition = conditionId switch
        {
            "1000" => "NEW",
            "2000" or "2500" => "LIKE_NEW",
            "3000" => "USED_EXCELLENT",
            "4000" => "USED_VERY_GOOD",
            "5000" => "USED_GOOD",
            "6000" => "USED_ACCEPTABLE",
            "7000" => "FOR_PARTS_OR_NOT_WORKING",
            _ => "USED_EXCELLENT"
        };

        // Picture URLs
        var imageUrls = item.Descendants(EbayNs + "PictureURL")
            .Select(e => e.Value)
            .Where(u => !string.IsNullOrEmpty(u))
            .Take(6)
            .ToList();

        // Item Specifics
        var specifics = new Dictionary<string, string>();
        foreach (var nvl in item.Descendants(EbayNs + "NameValueList"))
        {
            var name  = nvl.Element(EbayNs + "Name")?.Value ?? "";
            var value = nvl.Element(EbayNs + "Value")?.Value ?? "";
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(value))
                specifics[name] = value;
        }

        var brand = specifics.TryGetValue("Brand", out var b) ? b : "";
        var mpn   = specifics.TryGetValue("MPN",   out var m) ? m : "";

        return new ListingData
        {
            Title           = XS("Title"),
            Category        = categoryName,
            CategoryId      = categoryId,
            Condition       = condition,
            ConditionDescription = XS("ConditionDescription"),
            Description     = XS("Description"),
            Price           = price,
            Quantity        = qty > 0 ? qty : 1,
            Brand           = brand,
            Mpn             = mpn,
            ItemSpecifics   = specifics,
            ImageUrls       = imageUrls,
            BestOfferEnabled = item.Element(EbayNs + "BestOfferDetails")
                                   ?.Element(EbayNs + "BestOfferEnabled")?.Value
                                   ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false,
            ImageType = "webpage_screenshot"
        };
    }

    // ── Category suggestions ──────────────────────────────────────────────────

    private string? _categoryTreeId;

    public async Task<List<CategorySuggestion>> GetCategorySuggestionsAsync(string query)
    {
        var token = await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Cache tree ID per app lifetime
        if (_categoryTreeId == null)
        {
            var treeRes  = await client.GetStringAsync($"{BaseUrl}/commerce/taxonomy/v1/get_default_category_tree_id?marketplace_id=EBAY_US");
            using var td = JsonDocument.Parse(treeRes);
            _categoryTreeId = td.RootElement.TryGetProperty("categoryTreeId", out var v) ? v.GetString() ?? "0" : "0";
        }

        var url = $"{BaseUrl}/commerce/taxonomy/v1/category_tree/{_categoryTreeId}/get_category_suggestions" +
                  $"?q={Uri.EscapeDataString(query)}";

        var res  = await client.GetStringAsync(url);
        using var doc = JsonDocument.Parse(res);

        var results = new List<CategorySuggestion>();
        if (!doc.RootElement.TryGetProperty("categorySuggestions", out var arr)) return results;

        foreach (var item in arr.EnumerateArray().Take(12))
        {
            if (!item.TryGetProperty("category", out var cat)) continue;
            var id   = Str(cat, "categoryId");
            var name = Str(cat, "categoryName");
            if (string.IsNullOrEmpty(id)) continue;

            var breadcrumb = name;
            if (item.TryGetProperty("categoryTreeNodeAncestors", out var ancs))
            {
                var parts = ancs.EnumerateArray()
                    .Select(a => Str(a, "categoryName"))
                    .Where(s => !string.IsNullOrEmpty(s))
                    .ToList();
                if (parts.Count > 0) breadcrumb = string.Join(" › ", parts) + " › " + name;
            }

            results.Add(new CategorySuggestion(id, name, breadcrumb));
        }
        return results;
    }

    public async Task<List<CategorySuggestion>> GetCategoryChildrenAsync(string categoryId)
    {
        var token = await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (_categoryTreeId == null)
        {
            var treeRes  = await client.GetStringAsync($"{BaseUrl}/commerce/taxonomy/v1/get_default_category_tree_id?marketplace_id=EBAY_US");
            using var td = JsonDocument.Parse(treeRes);
            _categoryTreeId = td.RootElement.TryGetProperty("categoryTreeId", out var v) ? v.GetString() ?? "0" : "0";
        }

        var url = string.IsNullOrWhiteSpace(categoryId) || categoryId == "0"
            ? $"{BaseUrl}/commerce/taxonomy/v1/category_tree/{_categoryTreeId}"
            : $"{BaseUrl}/commerce/taxonomy/v1/category_tree/{_categoryTreeId}/get_category_subtree?category_id={Uri.EscapeDataString(categoryId)}";

        var res = await client.GetStringAsync(url);
        using var doc = JsonDocument.Parse(res);

        var results = new List<CategorySuggestion>();

        // Navigate to the right node
        JsonElement root;
        if (string.IsNullOrWhiteSpace(categoryId) || categoryId == "0")
            root = doc.RootElement.TryGetProperty("rootCategoryNode", out var r) ? r : doc.RootElement;
        else
            root = doc.RootElement.TryGetProperty("categorySubtreeNode", out var s) ? s : doc.RootElement;

        if (!root.TryGetProperty("childCategoryTreeNodes", out var children)) return results;

        foreach (var child in children.EnumerateArray())
        {
            if (!child.TryGetProperty("category", out var cat)) continue;
            var id   = Str(cat, "categoryId");
            var name = Str(cat, "categoryName");
            if (string.IsNullOrEmpty(id)) continue;
            var isLeaf = child.TryGetProperty("leafCategoryTreeNode", out var lf) && lf.GetBoolean();
            results.Add(new CategorySuggestion(id, name, isLeaf ? "leaf" : "parent"));
        }
        return results;
    }

    // ── Item Aspects (what the category actually requires) ────────────────────
    //
    // The one eBay call that turns "publish and find out" into "know before you press it".
    // get_item_aspects_for_category returns, per category, which Item Specifics are required,
    // which are the ones buyers filter on, and — the part that makes autofill possible — the
    // exact list of values eBay will accept for each.
    //
    // Cached in memory: a category's aspect definitions change on the order of months, and a
    // seller listing a dozen items in one category would otherwise repeat the same call a dozen
    // times. Entries expire after 12 hours so a mid-session eBay change still lands the same day.

    private readonly Dictionary<string, (DateTime FetchedUtc, List<CategoryAspect> Aspects)> _aspectCache = new();
    private static readonly TimeSpan AspectCacheTtl = TimeSpan.FromHours(12);
    private readonly SemaphoreSlim _aspectLock = new(1, 1);

    public async Task<List<CategoryAspect>> GetCategoryAspectsAsync(string categoryId)
    {
        categoryId = (categoryId ?? "").Trim();
        if (string.IsNullOrEmpty(categoryId)) return [];

        await _aspectLock.WaitAsync();
        try
        {
            if (_aspectCache.TryGetValue(categoryId, out var hit) &&
                DateTime.UtcNow - hit.FetchedUtc < AspectCacheTtl)
                return hit.Aspects;
        }
        finally { _aspectLock.Release(); }

        var token  = await GetOrRefreshTokenAsync();
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        if (_categoryTreeId == null)
        {
            var treeRes  = await client.GetStringAsync($"{BaseUrl}/commerce/taxonomy/v1/get_default_category_tree_id?marketplace_id=EBAY_US");
            using var td = JsonDocument.Parse(treeRes);
            _categoryTreeId = td.RootElement.TryGetProperty("categoryTreeId", out var v) ? v.GetString() ?? "0" : "0";
        }

        var url = $"{BaseUrl}/commerce/taxonomy/v1/category_tree/{_categoryTreeId}" +
                  $"/get_item_aspects_for_category?category_id={Uri.EscapeDataString(categoryId)}";

        var res  = await client.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
        {
            log.Add("Warning", $"Category aspects HTTP {(int)res.StatusCode}",
                $"Category {categoryId}: {body[..Math.Min(300, body.Length)]}");
            throw new InvalidOperationException(DescribeAspectFailure(body, (int)res.StatusCode, categoryId));
        }

        var aspects = ParseAspects(body);

        await _aspectLock.WaitAsync();
        try { _aspectCache[categoryId] = (DateTime.UtcNow, aspects); }
        finally { _aspectLock.Release(); }

        log.Add("Info", "Category aspects loaded",
            $"Category {categoryId}: {aspects.Count} aspects, {aspects.Count(a => a.Required)} required");
        return aspects;
    }

    // Turn eBay's error body into something the seller can act on.
    //
    // The one that actually happens: picking a parent category out of the Browse tree returns
    // errorId 62009, "must be a leaf category" — and eBay refuses the *publish* for the same
    // reason, so this is a real listing blocker showing up early rather than a lookup nicety.
    // Reporting it as "HTTP 400" would waste that.
    public static string DescribeAspectFailure(string body, int statusCode, string categoryId)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) &&
                errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
            {
                var first = errs[0];
                var msg = Str(first, "message");
                var id  = first.TryGetProperty("errorId", out var e) && e.ValueKind == JsonValueKind.Number
                    ? e.GetInt32() : 0;

                if (id == 62009 || msg.Contains("leaf", StringComparison.OrdinalIgnoreCase))
                    return $"Category {categoryId} is a parent category. eBay only lists items in the " +
                           "most specific category, so pick one further down the tree — that also " +
                           "decides which Item Specifics are required.";

                if (!string.IsNullOrWhiteSpace(msg)) return "eBay: " + msg;
            }
        }
        catch (JsonException) { /* fall through to the status-code message */ }

        return statusCode == 401 || statusCode == 403
            ? "eBay rejected the request for this category's Item Specifics — the connection may need renewing."
            : $"eBay returned HTTP {statusCode} for category {categoryId}'s Item Specifics.";
    }

    // Separated from the HTTP call so the response shape is testable without a token.
    public static List<CategoryAspect> ParseAspects(string json)
    {
        var results = new List<CategoryAspect>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("aspects", out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return results;

        foreach (var a in arr.EnumerateArray())
        {
            var name = Str(a, "localizedAspectName");
            if (string.IsNullOrWhiteSpace(name)) continue;

            var aspect = new CategoryAspect { Name = name };

            if (a.TryGetProperty("aspectConstraint", out var c))
            {
                aspect.Required      = c.TryGetProperty("aspectRequired", out var r) &&
                                       r.ValueKind == JsonValueKind.True;
                aspect.Recommended   = string.Equals(Str(c, "aspectUsage"), "RECOMMENDED",
                                                     StringComparison.OrdinalIgnoreCase);
                aspect.SelectionOnly = string.Equals(Str(c, "aspectMode"), "SELECTION_ONLY",
                                                     StringComparison.OrdinalIgnoreCase);
                aspect.MultiSelect   = Str(c, "itemToAspectCardinality")
                                          .Contains("MULTI", StringComparison.OrdinalIgnoreCase);

                if (c.TryGetProperty("aspectMaxLength", out var ml) &&
                    ml.ValueKind == JsonValueKind.Number)
                    aspect.MaxLength = ml.GetInt32();

                // Only a SELECTION_ONLY list is authoritative. For a FREE_TEXT aspect eBay ships
                // the popular values as *suggestions*, and treating that sample as the whole set
                // would have the app rejecting a seller's perfectly valid value — inventing a
                // problem eBay doesn't have.
                aspect.ValuesAreComplete = aspect.SelectionOnly;
            }

            if (a.TryGetProperty("aspectValues", out var vals) && vals.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vals.EnumerateArray())
                {
                    var lv = Str(v, "localizedValue");
                    if (!string.IsNullOrWhiteSpace(lv)) aspect.Values.Add(lv);
                }
            }

            // SELECTION_ONLY with no published values is not something the app can validate
            // against, so treat it as free text rather than rejecting everything.
            if (aspect.Values.Count == 0) aspect.SelectionOnly = false;

            results.Add(aspect);
        }

        // Required first, then the ones buyers filter on, then the rest — the order the seller
        // should work through them, decided once here rather than in the UI.
        return results
            .OrderByDescending(a => a.Required)
            .ThenByDescending(a => a.Recommended)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ── Merchant Inventory Location ──────────────────────────────────────────

    private const string DefaultLocationKey = "INGMainLocation";

    private async Task<string?> GetOrCreateInventoryLocationAsync(HttpClient client, string postalCode, string country)
    {
        country    = string.IsNullOrWhiteSpace(country) ? "US" : country;
        postalCode = postalCode?.Trim() ?? "";

        // Try to fetch existing locations
        var getRes  = await client.GetAsync($"{BaseUrl}/sell/inventory/v1/location");
        var getBody = await getRes.Content.ReadAsStringAsync();
        log.Add(getRes.IsSuccessStatusCode ? "Info" : "Warning",
            $"Inventory location GET HTTP {(int)getRes.StatusCode}",
            getBody[..Math.Min(300, getBody.Length)]);

        if (getRes.IsSuccessStatusCode)
        {
            try
            {
                using var getDoc = JsonDocument.Parse(getBody);
                if (getDoc.RootElement.TryGetProperty("locations", out var locs) && locs.GetArrayLength() > 0)
                {
                    var key = locs[0].TryGetProperty("merchantLocationKey", out var k) ? k.GetString() : null;
                    if (!string.IsNullOrEmpty(key))
                    {
                        log.Add("Info", "Using existing merchant location", key!);
                        return key;
                    }
                }
            }
            catch (Exception ex) { log.Add("Warning", "Location parse error", ex.Message); }
        }

        // Create a new default location
        var createPayload = new
        {
            location = new
            {
                address = new
                {
                    country,
                    postalCode = string.IsNullOrEmpty(postalCode) ? null : postalCode
                }
            },
            locationType           = "WAREHOUSE",
            merchantLocationStatus = "ENABLED",
            name                   = "ING AutoLister Location"
        };

        var content   = new StringContent(JsonSerializer.Serialize(createPayload, _json), Encoding.UTF8, "application/json");
        var createRes  = await client.PostAsync(
            $"{BaseUrl}/sell/inventory/v1/location/{Uri.EscapeDataString(DefaultLocationKey)}", content);
        var createBody = await createRes.Content.ReadAsStringAsync();
        log.Add(createRes.IsSuccessStatusCode ? "Info" : "Warning",
            $"Create merchant location HTTP {(int)createRes.StatusCode}",
            createBody[..Math.Min(300, createBody.Length)]);

        // 200/201 = created, 409 = already exists — both are usable
        if (createRes.IsSuccessStatusCode ||
            createRes.StatusCode == System.Net.HttpStatusCode.Conflict ||
            (int)createRes.StatusCode == 409)
            return DefaultLocationKey;

        return null;
    }

    // ── eBay Picture Services (EPS) upload ───────────────────────────────────

    public async Task<string> UploadPictureToEpsAsync(string imageBase64, string mimeType)
    {
        var token = await GetOrRefreshTokenAsync();
        var c     = creds.Get();

        var imageBytes = Convert.FromBase64String(imageBase64);
        var ext        = (mimeType ?? "").Contains("png") ? "png" : "jpg";

        // OAuth tokens go in X-EBAY-API-IAF-TOKEN header, NOT in <eBayAuthToken> XML element
        var xmlPayload = """
            <?xml version="1.0" encoding="utf-8"?>
            <UploadSiteHostedPicturesRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <PictureSet>Supersize</PictureSet>
            </UploadSiteHostedPicturesRequest>
            """;

        // Build multipart body manually — .NET MultipartFormDataContent adds headers that break eBay's XML parser
        var boundary   = "INGBoundary" + Guid.NewGuid().ToString("N")[..16];
        var xmlBytes   = Encoding.UTF8.GetBytes(xmlPayload.Trim());
        var imgMime    = mimeType ?? "image/jpeg";

        using var ms = new System.IO.MemoryStream();
        // XML part
        var xmlPart = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"XML Payload\"\r\n" +
            $"Content-Type: text/xml; charset=utf-8\r\n\r\n");
        ms.Write(xmlPart);
        ms.Write(xmlBytes);
        ms.Write(Encoding.ASCII.GetBytes("\r\n"));
        // Image part
        var imgHeader = Encoding.ASCII.GetBytes(
            $"--{boundary}\r\n" +
            $"Content-Disposition: form-data; name=\"image\"; filename=\"item.{ext}\"\r\n" +
            $"Content-Type: {imgMime}\r\n\r\n");
        ms.Write(imgHeader);
        ms.Write(imageBytes);
        // Close
        ms.Write(Encoding.ASCII.GetBytes($"\r\n--{boundary}--\r\n"));

        var rawBody   = ms.ToArray();
        var bodyContent = new ByteArrayContent(rawBody);
        bodyContent.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");

        var client  = httpClientFactory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, TradingEndpoint) { Content = bodyContent };
        request.Headers.Add("X-EBAY-API-CALL-NAME",            "UploadSiteHostedPictures");
        request.Headers.Add("X-EBAY-API-SITEID",              "0");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        request.Headers.Add("X-EBAY-API-APP-NAME",            c.EbayClientId);
        request.Headers.Add("X-EBAY-API-DEV-NAME",            c.EbayDevId);
        request.Headers.Add("X-EBAY-API-CERT-NAME",           c.EbayClientSecret);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN",           token);

        var response     = await client.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();

        log.Add(response.IsSuccessStatusCode ? "Info" : "Warning",
            $"UploadSiteHostedPictures HTTP {(int)response.StatusCode}",
            responseBody[..Math.Min(400, responseBody.Length)]);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"eBay picture upload failed (HTTP {(int)response.StatusCode}): {responseBody}");

        var xdoc = XDocument.Parse(responseBody);
        var ack  = xdoc.Descendants(EbayNs + "Ack").FirstOrDefault()?.Value ?? "";
        if (ack != "Success" && ack != "Warning")
        {
            var errMsg = xdoc.Descendants(EbayNs + "LongMessage").FirstOrDefault()?.Value
                      ?? xdoc.Descendants(EbayNs + "ShortMessage").FirstOrDefault()?.Value
                      ?? responseBody;
            throw new Exception($"eBay picture upload failed: {errMsg}");
        }

        var pictureUrl = xdoc.Descendants(EbayNs + "FullURL").FirstOrDefault()?.Value ?? "";
        if (string.IsNullOrEmpty(pictureUrl))
            throw new Exception("eBay did not return a picture URL in the response.");

        return pictureUrl;
    }

    // Extracts a string from an XML element by local name within a parent
    private static string Xstr(XElement parent, string localName) =>
        parent.Element(EbayNs + localName)?.Value ?? "";

    // ── eBay Sniper — place a max bid via Trading API PlaceOffer ─────────────
    public async Task PlaceMaxBidAsync(string itemId, decimal maxBid)
    {
        var token  = await GetOrRefreshTokenAsync();
        var c      = creds.Get();
        var client = httpClientFactory.CreateClient();

        var body = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <PlaceOfferRequest xmlns="urn:ebay:apis:eBLBaseComponents">
              <RequesterCredentials><eBayAuthToken>{token}</eBayAuthToken></RequesterCredentials>
              <ItemID>{itemId}</ItemID>
              <Offer>
                <Action>Bid</Action>
                <MaxBid currencyID="USD">{maxBid:F2}</MaxBid>
                <Quantity>1</Quantity>
              </Offer>
            </PlaceOfferRequest>
            """;

        var req = new HttpRequestMessage(HttpMethod.Post, "https://api.ebay.com/ws/api.dll")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("X-EBAY-API-SITEID", "0");
        req.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "967");
        req.Headers.Add("X-EBAY-API-CALL-NAME", "PlaceOffer");
        req.Headers.Add("X-EBAY-API-APP-NAME", c.EbayClientId ?? "");
        req.Headers.Add("X-EBAY-API-DEV-NAME", c.EbayDevId ?? "");
        req.Headers.Add("X-EBAY-API-CERT-NAME", c.EbayClientSecret ?? "");

        var resp = await client.SendAsync(req);
        var xml  = await resp.Content.ReadAsStringAsync();
        var root = XElement.Parse(xml);
        var ack  = root.Element(EbayNs + "Ack")?.Value ?? "";
        if (ack != "Success" && ack != "Warning")
        {
            var msg = root.Descendants(EbayNs + "LongMessage").FirstOrDefault()?.Value ?? "Bid failed";
            throw new Exception(msg);
        }
    }
}

public sealed record PublishListingResult(string OfferId, string ListingId, string Sku);
public sealed record SellerHubDraftResult(string DraftId, string SellerHubUrl);

public sealed record PolicyInfo(string Id, string Name);
public sealed record CategorySuggestion(string Id, string Name, string Breadcrumb);

public sealed record BusinessPoliciesResult(
    List<PolicyInfo> FulfillmentPolicies,
    List<PolicyInfo> PaymentPolicies,
    List<PolicyInfo> ReturnPolicies,
    string? Error);

public sealed record EbayOAuthRedirectExchangeResult(
    string Token,
    string RefreshToken,
    int ExpiresIn,
    int RefreshTokenExpiresIn,
    string TokenType,
    string Code,
    string State,
    string AcceptedUrl,
    string RedirectUri);

public sealed record EbayTokenExchangeResult(
    string AccessToken,
    string RefreshToken,
    int ExpiresIn,
    int RefreshTokenExpiresIn,
    string TokenType,
    string RedirectUri);
