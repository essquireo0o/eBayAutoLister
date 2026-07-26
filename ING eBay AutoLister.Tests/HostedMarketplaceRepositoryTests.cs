using System.Net;
using System.Text;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace ING_eBay_AutoLister.Tests;

// Answers every request with one canned body/status and records the URL it was asked for, so the
// hosted-comps client can be exercised without ever touching the real comps.php endpoint.
internal sealed class StubHttpHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        });
    }
}

internal sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

// Content root points at a directory that does not exist, so CredentialsStore falls back to its
// defaults instead of reading the machine's real credentials.json.
internal sealed class StubWebHostEnvironment : IWebHostEnvironment
{
    public string ApplicationName { get; set; } = "ING eBay AutoLister.Tests";
    public string EnvironmentName { get; set; } = "Development";
    public string ContentRootPath { get; set; } = Path.Combine(Path.GetTempPath(), $"hosted_comps_test_{Guid.NewGuid():N}");
    public string WebRootPath { get; set; } = "";
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
}

// Covers the seam the local SQLite path doesn't have: turning comps.php JSON into
// MarketplaceComparableResult rows. PHP/PDO hands back money and dates as strings and omits
// columns it has no value for, so the mapping has to be lenient without silently inventing data.
public class HostedMarketplaceRepositoryTests
{
    private const string ApiUrl = "https://example.invalid/comps.php";

    private static (HostedMarketplaceClient Client, StubHttpHandler Handler) CreateClient(
        string body, HttpStatusCode status = HttpStatusCode.OK, bool configured = true)
    {
        var handler = new StubHttpHandler(status, body);
        var creds = new CredentialsStore(new StubWebHostEnvironment());
        if (configured)
        {
            creds.Get().MarketCompsApiUrl = ApiUrl;
            creds.Get().MarketCompsApiKey = "stub-key-not-a-real-secret";
        }
        return (new HostedMarketplaceClient(new StubHttpClientFactory(handler), creds, new ActionLog()), handler);
    }

    private static HostedMarketplaceRepository CreateRepository(HostedMarketplaceClient client)
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        return new(client, new ActionLog(), new LiquidityScoringService(), normalizer, new ComparableMatcher(normalizer));
    }

    // Shaped exactly like a comps.php payload: numbers arrive as JSON strings.
    private const string TwoRowPayload = """
        {"count":2,"results":[
          {"ItemId":"1002","Title":"Bitmain Antminer S19j Pro 104TH/s Bitcoin Miner ASIC Tested Working",
           "Price":"950.00","Shipping":"45.00","Condition":"Pre-Owned","SoldDate":"2026-07-12",
           "Seller":"testseller","ItemUrl":"https://www.ebay.com/itm/1002","ImageUrl":"https://i.ebayimg.com/1002.jpg"},
          {"ItemId":"1003","Title":"Bitmain Antminer S19 95TH/s SHA-256 ASIC Bitcoin Miner - Tested Working",
           "Price":"900.00","Shipping":"40.00","Condition":"Pre-Owned","SoldDate":"2026-07-01",
           "Seller":"testseller","ItemUrl":"https://www.ebay.com/itm/1003","ImageUrl":""}
        ]}
        """;

    // ── Client: JSON mapping ─────────────────────────────────────────────────────

    [Fact]
    public async Task FetchCandidatesAsync_StringPricesAndShipping_ParseToDecimalsAndSumIntoTotal()
    {
        var (client, _) = CreateClient(TwoRowPayload);

        var rows = await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None);

        Assert.Equal(2, rows.Count);
        var first = rows[0];
        Assert.Equal("1002", first.ItemId);
        Assert.Equal(950.00m, first.SoldPrice);
        Assert.Equal(45.00m, first.Shipping);
        Assert.Equal(995.00m, first.TotalPrice);
        Assert.Equal("Pre-Owned", first.Condition);
        Assert.Equal("testseller", first.Seller);
        Assert.Equal("https://www.ebay.com/itm/1002", first.ItemUrl);
    }

    [Fact]
    public async Task FetchCandidatesAsync_NumericPrices_ParseJustLikeStringOnes()
    {
        // The API currently stringifies money, but a JSON number must not silently become $0.
        var (client, _) = CreateClient("""
            {"results":[{"ItemId":"1","Title":"Antminer S19 Miner","Price":900.5,"Shipping":40}]}
            """);

        var rows = await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None);

        Assert.Equal(900.5m, rows[0].SoldPrice);
        Assert.Equal(40m, rows[0].Shipping);
        Assert.Equal(940.5m, rows[0].TotalPrice);
    }

    [Fact]
    public async Task FetchCandidatesAsync_SoldDate_ParsesBothIsoAndScrapedDateFormats()
    {
        var (client, _) = CreateClient("""
            {"results":[
              {"ItemId":"1","Title":"Antminer S19 Miner","Price":"900.00","SoldDate":"2026-07-12"},
              {"ItemId":"2","Title":"Antminer S19 Miner","Price":"900.00","SoldDate":"Jul 1, 2026"}
            ]}
            """);

        var rows = await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None);

        Assert.Equal(new DateTime(2026, 7, 12), rows[0].SoldDate);
        Assert.Equal(new DateTime(2026, 7, 1), rows[1].SoldDate);
    }

    [Fact]
    public async Task FetchCandidatesAsync_UnparseableOrNullSoldDate_LeavesTheDateNullRatherThanGuessing()
    {
        var (client, _) = CreateClient("""
            {"results":[
              {"ItemId":"1","Title":"Antminer S19 Miner","Price":"900.00","SoldDate":"sometime last spring"},
              {"ItemId":"2","Title":"Antminer S19 Miner","Price":"900.00","SoldDate":null},
              {"ItemId":"3","Title":"Antminer S19 Miner","Price":"900.00","SoldDate":""}
            ]}
            """);

        var rows = await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None);

        Assert.All(rows, r => Assert.Null(r.SoldDate));
    }

    [Fact]
    public async Task FetchCandidatesAsync_MissingFields_AreToleratedWithSafeDefaults()
    {
        // A row that carries only a title still has to come back as a usable object — the matcher
        // scores primarily on Title, and Epid/buying-format aren't in the hosted dataset at all.
        var (client, _) = CreateClient("""{"results":[{"Title":"Antminer S19 Miner"}]}""");

        var row = Assert.Single(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));

        Assert.Equal("Antminer S19 Miner", row.Title);
        Assert.Equal("", row.ItemId);
        Assert.Equal(0m, row.SoldPrice);
        Assert.Equal(0m, row.Shipping);
        Assert.Equal(0m, row.TotalPrice);
        Assert.Null(row.Condition);
        Assert.Null(row.SoldDate);
        Assert.Null(row.Seller);
        Assert.Null(row.ItemUrl);
        Assert.Null(row.ImageUrl);
        Assert.Null(row.Epid);
        Assert.False(row.IsFixedPrice);
    }

    [Fact]
    public async Task FetchCandidatesAsync_NonNumericPriceString_BecomesZeroInsteadOfThrowing()
    {
        var (client, _) = CreateClient("""
            {"results":[{"ItemId":"1","Title":"Antminer S19 Miner","Price":"N/A","Shipping":"Free"}]}
            """);

        var row = Assert.Single(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));

        Assert.Equal(0m, row.SoldPrice);
        Assert.Equal(0m, row.Shipping);
    }

    [Fact]
    public async Task FetchCandidatesAsync_SendsTheQueryKeyAndCandidateLimitOnTheUrl()
    {
        var (client, handler) = CreateClient(TwoRowPayload);

        await client.FetchCandidatesAsync("Antminer S19 Pro", CancellationToken.None);

        var uri = Assert.Single(handler.Requests);
        Assert.Equal("example.invalid", uri.Host);
        Assert.Contains("Antminer S19 Pro", Uri.UnescapeDataString(uri.Query));
        Assert.Contains("limit=500", uri.Query);
    }

    // ── Client: degradation ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("""{"count":0}""")]                       // no results property at all
    [InlineData("""{"results":null}""")]                  // present but not an array
    [InlineData("""{"results":"none"}""")]
    [InlineData("""{"results":[]}""")]
    public async Task FetchCandidatesAsync_NoUsableResultsArray_ReturnsEmpty(string body)
    {
        var (client, _) = CreateClient(body);

        Assert.Empty(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));
    }

    [Fact]
    public async Task FetchCandidatesAsync_HttpError_ReturnsEmptyInsteadOfThrowing()
    {
        var (client, _) = CreateClient("""{"error":"forbidden"}""", HttpStatusCode.Forbidden);

        Assert.Empty(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));
    }

    [Fact]
    public async Task FetchCandidatesAsync_MalformedJson_ReturnsEmptyInsteadOfThrowing()
    {
        var (client, _) = CreateClient("<html>502 Bad Gateway</html>");

        Assert.Empty(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));
    }

    [Fact]
    public async Task FetchCandidatesAsync_NotConfigured_ReturnsEmptyWithoutCallingTheApi()
    {
        var (client, handler) = CreateClient(TwoRowPayload, configured: false);

        Assert.False(client.IsConfigured);
        Assert.Empty(await client.FetchCandidatesAsync("Antminer S19", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task FetchCandidatesAsync_BlankQuery_ReturnsEmptyWithoutCallingTheApi()
    {
        var (client, handler) = CreateClient(TwoRowPayload);

        Assert.Empty(await client.FetchCandidatesAsync("   ", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    // ── Repository: mapped rows flow through scoring and pricing ─────────────────

    [Fact]
    public async Task SearchByKeywordAsync_MappedHostedRows_AreScoredAndReturned()
    {
        var repo = CreateRepository(CreateClient(TwoRowPayload).Client);

        var results = await repo.SearchByKeywordAsync("Bitmain Antminer S19 Bitcoin Miner");

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.True(r.SoldPrice > 0, "hosted string prices must survive into the scored set"));
        Assert.All(results, r => Assert.True(r.MatchScore > 0));
    }

    [Fact]
    public async Task FindComparablesAsync_HostedRows_ProduceAPricingSummary()
    {
        var repo = CreateRepository(CreateClient(TwoRowPayload).Client);

        var summary = await repo.FindComparablesAsync(new MarketplaceLookupRequest
        {
            Keywords = "Bitmain Antminer S19 Bitcoin Miner",
        });

        Assert.True(summary.MatchCount > 0);
        Assert.NotNull(summary.MedianPrice);
        // The stats must land in the mapped $900–$950 band, not at $0 from a failed string parse.
        Assert.InRange(summary.MedianPrice!.Value, 900m, 950m);
        Assert.InRange(summary.AverageShipping!.Value, 40m, 45m);
        Assert.NotNull(summary.SuggestedResalePrice);
    }

    [Fact]
    public async Task FindComparablesAsync_EmptyHostedResponse_ReturnsZeroMatchesNotAnException()
    {
        var repo = CreateRepository(CreateClient("""{"results":[]}""").Client);

        var summary = await repo.FindComparablesAsync(new MarketplaceLookupRequest { Keywords = "Nonexistent Widget Zzzqx" });

        Assert.Equal(0, summary.MatchCount);
        Assert.Null(summary.SuggestedResalePrice);
    }

    [Fact]
    public async Task IsAvailableAsync_ReflectsWhetherTheHostedApiIsConfigured()
    {
        Assert.True(await CreateRepository(CreateClient(TwoRowPayload).Client).IsAvailableAsync());
        Assert.False(await CreateRepository(CreateClient(TwoRowPayload, configured: false).Client).IsAvailableAsync());
    }
}
