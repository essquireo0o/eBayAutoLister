using System.Globalization;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One sold listing as it arrives from the OpenWebNinja real-time eBay API, normalised into the
/// columns the app's <c>SoldListings</c> table already uses.
/// </summary>
/// <remarks>
/// The column names are not a coincidence and must not drift: every price in this app is read back
/// out of that table by <see cref="MarketplaceRepository"/>, so a row that does not fit it is a
/// call that was paid for and then thrown away.
/// </remarks>
public sealed record LiveCompRow
{
    public string ItemId { get; init; } = "";
    public string Title { get; init; } = "";

    /// <summary>Null means the API gave no usable price — stored as NULL rather than as zero.</summary>
    public decimal? Price { get; init; }

    /// <summary>0 for free shipping, null for unknown. See <see cref="OpenWebNinjaClient.ShippingOf"/>.</summary>
    public decimal? Shipping { get; init; }

    public string Condition { get; init; } = "";
    public string Seller { get; init; } = "";

    /// <summary>ISO <c>yyyy-MM-dd</c>, or empty when the caption could not be read.</summary>
    public string SoldDate { get; init; } = "";

    public string ItemUrl { get; init; } = "";
    public string ImageUrl { get; init; } = "";

    /// <summary>The untouched API item, so a later parser can take fields this one ignored
    /// without spending the call again.</summary>
    public string RawJson { get; init; } = "";
}

/// <summary>What one call to the API came back with. Never throws; the failure is in the record.</summary>
/// <param name="Ok">True when the call answered and its rows were read.</param>
/// <param name="Status">The HTTP status, or 0 when the request never got that far.</param>
/// <param name="Error">Empty when <paramref name="Ok"/>. Technical detail, for the log.</param>
/// <param name="Rows">The sold listings. Empty and <c>Ok</c> means the model genuinely has none.</param>
/// <param name="TotalResults">What the API says exists for this query across all pages.</param>
public sealed record LiveCompsFetch(
    bool Ok, int Status, string Error, IReadOnlyList<LiveCompRow> Rows, int TotalResults)
{
    public static LiveCompsFetch Failed(int status, string error) => new(false, status, error, [], 0);
}

/// <summary>
/// Recent eBay sold prices for one search term, over HTTP, from OpenWebNinja's real-time eBay API.
/// </summary>
/// <remarks>
/// <para>
/// This is what replaced the browser scraper on 2026-08-14. The scraper could only ever run on the
/// owner's own PC — it needed Chrome, a signed-in eBay profile and a person to clear eBay's bot
/// check — which is why <c>app.inglisting.com</c> had no live lookups at all and priced only from
/// stored comps. An HTTP call with a key needs none of those things, so the hosted build gets live
/// sold data for the first time.
/// </para>
/// <para>
/// <b>Every call is paid for out of a fixed 50,000 that does not refill</b>, shared with the bulk
/// collector (<c>BitData/owninja_collect.py</c>). Nothing in this class decides whether a call is
/// allowed — that is <see cref="LiveCompsBudget"/>, deliberately in front of it — but it is why one
/// lookup is one page of 25 rows and not four. Breadth beats depth for comps: page two of the same
/// model costs the same as a whole other model and prices nothing new.
/// </para>
/// <para>
/// The normalising below is a port of the collector's own functions, which have been run against
/// this API for thousands of rows. <see cref="ShippingOf"/> and <see cref="SoldDateOf"/> in
/// particular carry knowledge that is not guessable from the field names — see each one.
/// </para>
/// </remarks>
public sealed class OpenWebNinjaClient(IHttpClientFactory httpFactory, CredentialsStore credentials, ActionLog log)
{
    /// <summary>The sold-items search. <c>show_only=sold_items</c> is what makes it sold rather than active.</summary>
    public const string BaseUrl = "https://api.openwebninja.com/real-time-ebay-data/search";

    /// <summary>Rows the API returns per page. One lookup is one page — see the class remarks.</summary>
    public const int RowsPerPage = 25;

    /// <summary>
    /// Generous, because the alternative is worse. The old scraper was allowed three minutes and
    /// this answers in two or three seconds; a timeout here means the API is genuinely not
    /// answering, and reporting that as "no recent sales" is the one mistake this whole path exists
    /// to avoid.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>The owner's key, from configuration. Never from the repository — see HOSTING.md.</summary>
    private string ApiKey => credentials.Get().OpenWebNinjaApiKey?.Trim() ?? "";

    /// <summary>False when no key is configured, which is a deployment that prices from stored comps.</summary>
    public bool IsConfigured => ApiKey.Length > 0;

    /// <summary>
    /// One page of sold listings for <paramref name="query"/>. Costs exactly one call from the
    /// account's budget whether it answers, errors or comes back empty.
    /// </summary>
    public async Task<LiveCompsFetch> SearchSoldAsync(string query, int page = 1, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return LiveCompsFetch.Failed(0, "No search term.");

        var key = ApiKey;
        if (key.Length == 0) return LiveCompsFetch.Failed(0, "No OpenWebNinja API key is configured.");

        var url = $"{BaseUrl}?query={Uri.EscapeDataString(query.Trim())}" +
                  $"&show_only=sold_items&page={Math.Max(1, page)}";

        try
        {
            var http = httpFactory.CreateClient();
            http.Timeout = Timeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // A header, never a query parameter: the URL of a failed request ends up in logs and in
            // exception messages, and this key is the whole budget.
            request.Headers.Add("X-API-Key", key);

            using var response = await http.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                var detail = body.Length > 180 ? body[..180] : body;
                log.Add("Warning", "Live sold-comps API error",
                    $"HTTP {(int)response.StatusCode} for \"{query}\": {detail}");
                return LiveCompsFetch.Failed((int)response.StatusCode, $"HTTP {(int)response.StatusCode}: {detail}");
            }

            return Parse(body, (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A timeout arrives here as a TaskCanceledException with the caller's token unset.
            log.Add("Warning", "Live sold-comps API unreachable", $"\"{query}\": {ex.Message}");
            return LiveCompsFetch.Failed(0, ex.Message);
        }
    }

    /// <summary>Reads the API's envelope. Public so the parsing can be tested without a network.</summary>
    public static LiveCompsFetch Parse(string body, int status)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) ||
                data.ValueKind != JsonValueKind.Object)
                return LiveCompsFetch.Failed(status, "The API answered without a data object.");

            var total = data.TryGetProperty("total_results", out var t) && t.TryGetInt32(out var totalValue)
                ? totalValue
                : 0;

            var rows = new List<LiveCompRow>();
            if (data.TryGetProperty("products", out var products) && products.ValueKind == JsonValueKind.Array)
                foreach (var item in products.EnumerateArray())
                {
                    var row = ToRow(item);
                    // A row with no id cannot be deduplicated against the 919k already stored, and a
                    // comp counted twice is a market that does not exist.
                    if (row.ItemId.Length > 0) rows.Add(row);
                }

            return new LiveCompsFetch(true, status, "", rows, total);
        }
        catch (JsonException ex)
        {
            return LiveCompsFetch.Failed(status, "The API answered with something that is not JSON: " + ex.Message);
        }
    }

    /// <summary>One API item as a storable row.</summary>
    public static LiveCompRow ToRow(JsonElement item) => new()
    {
        ItemId    = Text(item, "item_id"),
        Title     = Text(item, "title"),
        Price     = Money(Raw(item, "price")),
        Shipping  = ShippingOf(item),
        Condition = Text(item, "condition"),
        Seller    = Text(item, "seller_name"),
        SoldDate  = SoldDateOf(item),
        ItemUrl   = Text(item, "url"),
        ImageUrl  = Text(item, "image_high_res") is { Length: > 0 } hi ? hi : Text(item, "image"),
        RawJson   = item.GetRawText(),
    };

    /// <summary>
    /// Prices arrive as a number, or as text like <c>"$1,234.56"</c>, or absent.
    /// </summary>
    public static decimal? Money(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            value, @"([0-9][0-9,]*(?:\.[0-9]{1,2})?)");
        if (!match.Success) return null;

        return decimal.TryParse(match.Groups[1].Value.Replace(",", ""),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>
    /// <c>"Free shipping"</c> is 0.00 and must not be confused with unknown, which stays null.
    /// </summary>
    /// <remarks>
    /// The difference is a real one for a seller: free delivery is part of what the buyer paid, and
    /// treating an unknown shipping cost as zero quietly understates every comp it touches. The
    /// field also arrives as <c>"+$49.99 delivery"</c>, which is why this is a search and not a parse.
    /// </remarks>
    public static decimal? ShippingOf(JsonElement item)
    {
        var raw = Raw(item, "shipping") ?? "";
        if (raw.Contains("free", StringComparison.OrdinalIgnoreCase)) return 0m;
        return Money(raw);
    }

    /// <summary>
    /// The API puts the sale date in a caption: <c>"Sold Aug 13, 2026"</c>. Returned as ISO
    /// <c>yyyy-MM-dd</c>, or empty when it is not a date this understands.
    /// </summary>
    /// <remarks>
    /// A missing sold date is not a small loss. Every freshness weight in the pricing path is
    /// computed off it, so a row without one is a sale of unknown age and is treated as neutral
    /// rather than recent.
    /// </remarks>
    public static string SoldDateOf(JsonElement item)
    {
        var caption = Text(item, "caption");
        var match = System.Text.RegularExpressions.Regex.Match(caption, @"^\s*Sold\s+(.+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return "";

        string[] formats = ["MMM d, yyyy", "MMMM d, yyyy", "d MMM yyyy", "MMM dd, yyyy"];
        return DateTime.TryParseExact(match.Groups[1].Value.Trim(), formats,
                   CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : "";
    }

    /// <summary>A string property, or "" — including when the API sent an explicit null.</summary>
    private static string Text(JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object &&
        item.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    /// <summary>A property as text whatever its JSON type, so numbers and money strings share a path.</summary>
    private static string? Raw(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }
}
