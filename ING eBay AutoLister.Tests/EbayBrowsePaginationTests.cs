using System.Net;
using System.Text;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The Opportunity Finder is a complete sweep, not a sample of eBay's first page. Browse exposes
/// continuation links in 200-row pages; these tests drive the real service through more than one.
/// </summary>
public sealed class EbayBrowsePaginationTests : IDisposable
{
    private readonly string _credentials = Path.Combine(
        Path.GetTempPath(), $"ebay_browse_paging_{Guid.NewGuid():N}.json");

    [Fact]
    public async Task Search_follows_every_next_page_until_ebay_says_the_result_set_is_finished()
    {
        var handler = new RecordingHandler()
            .Then(HttpStatusCode.OK, """{"access_token":"browse-token","expires_in":7200}""")
            .Then(HttpStatusCode.OK, Page(
                [Item("1", "First miner"), Item("2", "Second miner")],
                "https://api.ebay.com/buy/browse/v1/item_summary/search?q=miner&limit=200&offset=2"))
            .Then(HttpStatusCode.OK, Page([Item("3", "Third miner")], next: null));

        var found = await Service(handler).SearchEndingSoonAsync("miner", limit: 10_000, listingType: "BOTH");

        Assert.Equal(["1", "2", "3"], found.Select(i => i.ItemId));
        Assert.Equal(3, handler.Calls);
        Assert.Contains("limit=200", handler.Urls[1]);
        Assert.Contains("offset=0", handler.Urls[1]);
        Assert.Contains("offset=2", handler.Urls[2]);
    }

    [Fact]
    public async Task A_small_caller_still_stops_at_its_requested_total_even_when_next_exists()
    {
        var handler = new RecordingHandler()
            .Then(HttpStatusCode.OK, """{"access_token":"browse-token","expires_in":7200}""")
            .Then(HttpStatusCode.OK, Page(
                [Item("1", "One"), Item("2", "Two")],
                "https://api.ebay.com/buy/browse/v1/item_summary/search?q=miner&limit=1&offset=1"));

        var found = await Service(handler).SearchEndingSoonAsync("miner", limit: 1);

        Assert.Single(found);
        Assert.Equal("1", found[0].ItemId);
        Assert.Equal(2, handler.Calls); // token + exactly one Browse page
    }

    private EbayService Service(HttpMessageHandler handler)
    {
        var store = new CredentialsStore(_credentials);
        store.Save(new CredentialsPatch
        {
            EbayClientId = "ING-PRD-pagination",
            EbayClientSecret = "PRD-pagination-secret",
        });
        return new EbayService(store, new StubHttpClientFactory(handler), new ActionLog());
    }

    private static string Page(string[] items, string? next) =>
        "{\"itemSummaries\":[" + string.Join(',', items) + "]" +
        (next is null ? "" : ",\"next\":\"" + next + "\"") + "}";

    private static string Item(string id, string title) =>
        $$"""{"legacyItemId":"{{id}}","title":"{{title}}","price":{"value":"100.00"},"buyingOptions":["FIXED_PRICE"]}""";

    public void Dispose()
    {
        foreach (var path in new[] { _credentials, _credentials + ".bak" })
            if (File.Exists(path)) File.Delete(path);
        GC.SuppressFinalize(this);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new();
        public int Calls { get; private set; }
        public List<string> Urls { get; } = [];

        public RecordingHandler Then(HttpStatusCode status, string body)
        {
            _responses.Enqueue(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            Urls.Add(request.RequestUri?.ToString() ?? "");
            if (_responses.Count == 0) throw new InvalidOperationException("Unexpected extra HTTP call.");
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
