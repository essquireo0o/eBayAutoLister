using System.Text.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// <c>/api/amazon/product-types</c> over real HTTP, against the handler Program.cs maps.
/// </summary>
/// <remarks>
/// <para>
/// The same arrangement as <see cref="AmazonStatusEndpointTests"/> and for the same reason: what is
/// being checked is the answer a caller gets, including what is not in it.
/// </para>
/// <para>
/// No test here reaches Amazon. Every configuration exercised is one where the answer is decided
/// before a request would be made — which is not a limitation of the tests, it is the state this
/// deployment is in: there is no Amazon credential that can obtain a token, so the endpoint's
/// honest report of exactly that is the behaviour most worth pinning.
/// </para>
/// </remarks>
public class AmazonProductTypeEndpointTests
{
    private const string ClientId     = "amzn1.application-oa2-client.notarealclientid";
    private const string ClientSecret = "amzn1.oa2-cs.v1.notarealsecret";
    private const string RefreshToken = "Atzr|IwEBIExampleRefreshTokenThatIsNotReal0000000000";
    private const string UsMarketplace = "ATVPDKIKX0DER";
    private const string SellerId      = "A0000000000000";

    [Fact]
    public async Task A_deployment_with_no_amazon_credentials_says_which_one_to_set()
    {
        using var server = await Server.StartAsync();

        var body = await server.SearchAsync("bluetooth speaker");

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, body.GetProperty("status").GetString());
        Assert.Contains("Credentials__AmazonClientId", body.GetProperty("message").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("chosen").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("definition").ValueKind);

        // No sandbox notice, because nothing was asked of the sandbox. A sentence about what came
        // back from a request that was never made describes something that did not happen.
        Assert.Equal("", body.GetProperty("sandboxNotice").GetString());
    }

    [Fact]
    public async Task An_application_without_a_seller_is_not_reported_as_a_lookup_failure()
    {
        // Exactly where this app stands: the owner's sandbox application and no seller grant.
        // "We cannot ask Amazon" and "Amazon says this product type needs nothing" must never
        // arrive looking alike.
        using var server = await Server.StartAsync(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret));

        var body = await server.SearchAsync("bluetooth speaker");

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, body.GetProperty("status").GetString());
        Assert.Contains("Credentials__AmazonRefreshToken", body.GetProperty("message").GetString());
        Assert.Empty(body.GetProperty("candidates").EnumerateArray());
    }

    [Fact]
    public async Task A_grant_without_a_marketplace_names_the_marketplace()
    {
        // A token proves the application and the seller. It does not say which marketplace to scope
        // a definitions call to, and Amazon rejects the call rather than assuming one.
        using var server = await Server.StartAsync(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret),
            ("Credentials__AmazonRefreshToken", RefreshToken));

        var body = await server.SearchAsync("bluetooth speaker");

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, body.GetProperty("status").GetString());
        Assert.Contains("Credentials__AmazonMarketplaceId", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_named_product_type_route_refuses_the_same_way()
    {
        using var server = await Server.StartAsync();

        var body = await server.DefinitionAsync("BLUETOOTH_SPEAKER");

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, body.GetProperty("status").GetString());
        Assert.Equal(0, body.GetProperty("counts").GetProperty("required").GetInt32());
    }

    [Fact]
    public async Task An_empty_query_is_answered_rather_than_sent_to_amazon()
    {
        using var server = await Server.StartAsync(
            ("Credentials__AmazonClientId",      ClientId),
            ("Credentials__AmazonClientSecret",  ClientSecret),
            ("Credentials__AmazonRefreshToken",  RefreshToken),
            ("Credentials__AmazonMarketplaceId", UsMarketplace),
            ("Credentials__AmazonSellerId",      SellerId));

        var body = await server.SearchAsync("");

        Assert.Equal(AmazonDefinitionStatus.NoMatch, body.GetProperty("status").GetString());
        Assert.Contains("nothing to look up", body.GetProperty("message").GetString());
    }

    [Fact]
    public async Task The_text_report_is_readable_rather_than_json()
    {
        using var server = await Server.StartAsync();

        var (contentType, text) = await server.ReportAsync("BLUETOOTH_SPEAKER");

        Assert.Equal("text/plain", contentType);
        Assert.StartsWith(AmazonDefinitionStatus.NotConfigured + ":", text);
    }

    [Fact]
    public async Task The_answer_carries_no_credential_and_no_pre_signed_link()
    {
        // Fully configured, so every credential is present to be leaked. The schema URL matters as
        // much as the secrets do: it is pre-signed, so anyone holding it can fetch the document
        // without a token until it expires — a capability, not a description.
        using var server = await Server.StartAsync(
            ("Credentials__AmazonClientId",      ClientId),
            ("Credentials__AmazonClientSecret",  ClientSecret),
            ("Credentials__AmazonRefreshToken",  RefreshToken),
            ("Credentials__AmazonMarketplaceId", UsMarketplace),
            ("Credentials__AmazonSellerId",      SellerId));

        var raw = await server.SearchTextAsync("bluetooth speaker");

        Assert.DoesNotContain(ClientId, raw);
        Assert.DoesNotContain(ClientSecret, raw);
        Assert.DoesNotContain("Atzr|", raw);
        Assert.DoesNotContain(SellerId, raw);
        Assert.DoesNotContain("X-Amz-", raw);
        Assert.DoesNotContain("schemaUrl", raw);
    }

    [Fact]
    public async Task The_answers_this_deployment_actually_gives_are_written_down()
    {
        // The point of a diagnostic is the sentence an operator reads, and a green test does not
        // show it to anyone. These are the two states this deployment can actually be in today,
        // recorded verbatim from the real handler so they can be quoted rather than paraphrased.
        using var bare = await Server.StartAsync();
        using var application = await Server.StartAsync(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret));

        var text = string.Join(Environment.NewLine + Environment.NewLine,
            "# GET /api/amazon/product-types?query=bluetooth speaker — nothing configured",
            Pretty(await bare.SearchTextAsync("bluetooth speaker")),
            "# GET /api/amazon/product-types?query=bluetooth speaker — the owner's sandbox application, no seller grant",
            Pretty(await application.SearchTextAsync("bluetooth speaker")));

        var path = Path.Combine(Path.GetTempPath(), "amazon-product-type-endpoint.txt");
        await File.WriteAllTextAsync(path, text);

        Assert.Contains(AmazonDefinitionStatus.NotConfigured, await File.ReadAllTextAsync(path));
    }

    private static string Pretty(string json) =>
        JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement,
            new JsonSerializerOptions { WriteIndented = true });

    // ── A server, on a port the OS picks ─────────────────────────────────────────────────────

    private sealed class Server : IDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;
        private readonly string _root;

        private Server(WebApplication app, HttpClient client, string root)
        {
            _app = app;
            _client = client;
            _root = root;
        }

        public static async Task<Server> StartAsync(params (string Key, string Value)[] variables)
        {
            var root = Path.Combine(Path.GetTempPath(), "amazon-pt-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ContentRootPath = root,
                EnvironmentName = "Production",
            });
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Configuration.AddInMemoryCollection(
                variables.ToDictionary(v => v.Key.Replace("__", ":"), v => (string?)v.Value));

            builder.Services.AddHttpClient();
            builder.Services.AddSingleton<ActionLog>();
            builder.Services.AddSingleton(AmazonOptions.FromConfiguration(builder.Configuration));
            builder.Services.AddSingleton<AmazonService>();
            // Rooted in the test's own folder: a test must never write into the seller's schema cache.
            builder.Services.AddSingleton(new AmazonSchemaCache(Path.Combine(root, "schemas")));
            builder.Services.AddSingleton<AmazonProductTypeService>();

            var app = builder.Build();
            AmazonProductTypeEndpoints.Map(app);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            return new Server(app, new HttpClient { BaseAddress = new Uri(address) }, root);
        }

        public async Task<JsonElement> SearchAsync(string query) =>
            JsonDocument.Parse(await SearchTextAsync(query)).RootElement.Clone();

        public async Task<string> SearchTextAsync(string query) =>
            await GetAsync($"{AmazonProductTypeEndpoints.SearchPath}?query={Uri.EscapeDataString(query)}");

        public async Task<JsonElement> DefinitionAsync(string productType) =>
            JsonDocument.Parse(await GetAsync($"/api/amazon/product-types/{productType}")).RootElement.Clone();

        public async Task<(string ContentType, string Text)> ReportAsync(string productType)
        {
            var response = await _client.GetAsync($"/api/amazon/product-types/{productType}/report");
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return (response.Content.Headers.ContentType?.MediaType ?? "",
                    await response.Content.ReadAsStringAsync());
        }

        private async Task<string> GetAsync(string path)
        {
            var response = await _client.GetAsync(path);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode} from {path}");
            return await response.Content.ReadAsStringAsync();
        }

        public void Dispose()
        {
            _client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
            try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
        }
    }
}
