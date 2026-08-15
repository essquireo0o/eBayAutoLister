using System.Net.Http.Json;
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

// The mapper is proven next door against a real draft and a real schema. What is left is the wiring,
// and the wiring has exactly one failure this can catch that unit tests cannot: a route that is not
// mapped, or is mapped to a handler that is not the one the app serves. So these start a real server
// and map the real Map(), the same way AmazonProductTypeEndpointTests does.
//
// Every case here runs WITHOUT Amazon credentials, because that is this deployment's actual state —
// the stored LWA client secret is a placeholder note, so no token can be obtained and no schema can
// be fetched. What that must produce is a 200 carrying not_configured, not a 500: "this deployment
// cannot ask Amazon" is a different instruction from "Amazon refused", and an HTTP error flattens
// both into the same red box.
public class AmazonListingFillEndpointTests
{
    [Fact]
    public async Task Posting_a_draft_answers_with_a_status_rather_than_an_error()
    {
        using var server = await Server.StartAsync();

        var answer = await server.FillAsync(new AmazonFillRequest
        {
            Title = "Bitaxe NerdQaxe++ 4.8TH/s BM1370 Bitcoin Solo Miner",
            Brand = "NerdQaxe",
            Price = 549.99m,
        });

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, answer.GetProperty("status").GetString());
        Assert.False(answer.GetProperty("canSubmit").GetBoolean());

        // The draft is named back. Without it, an operator holding several answers cannot tell which
        // draft any of them is about.
        Assert.Contains("Bitaxe", answer.GetProperty("sourceTitle").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_filled_in_when_Amazons_requirements_could_not_be_read()
    {
        using var server = await Server.StartAsync();

        var answer = await server.FillAsync(new AmazonFillRequest { Title = "bluetooth speaker", Brand = "JBL" });

        // A brand and a title are sitting right there, and neither goes into a payload — because
        // without the schema there is nothing to say they are what Amazon asked for. An answer
        // showing two filled attributes would be inventing the requirements, not just the values.
        Assert.Equal(0, answer.GetProperty("counts").GetProperty("required").GetInt32());
        Assert.Empty(answer.GetProperty("payload").EnumerateObject());
        Assert.False(string.IsNullOrWhiteSpace(answer.GetProperty("headline").GetString()));
    }

    [Fact]
    public async Task A_named_product_type_is_taken_as_given_rather_than_searched_for()
    {
        using var server = await Server.StartAsync();

        // The seller picked it from the candidates. Second-guessing that would be the app overruling
        // them — so this must not come back as "no product type was chosen".
        var answer = await server.FillAsync(new AmazonFillRequest
        {
            Title = "anything at all",
            ProductType = "BLUETOOTH_SPEAKER",
        });

        Assert.Equal(AmazonDefinitionStatus.NotConfigured, answer.GetProperty("status").GetString());
        Assert.DoesNotContain("no Amazon product type was chosen",
            answer.GetProperty("headline").GetString()!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_report_route_answers_as_text_and_never_carries_a_credential()
    {
        using var server = await Server.StartAsync(
            ("Credentials:AmazonClientId", "amzn1.application-oa2-client.notarealclientid"),
            ("Credentials:AmazonClientSecret", "amzn1.oa2-cs.v1.notarealsecret"),
            ("Credentials:AmazonRefreshToken", "Atzr|NotARealRefreshToken"));

        var (contentType, text) = await server.ReportAsync(new AmazonFillRequest { Title = "bluetooth speaker" });

        Assert.Equal("text/plain", contentType);
        Assert.Contains("bluetooth speaker", text, StringComparison.OrdinalIgnoreCase);

        // The five Amazon credentials never leave the process. A report is a description, and a
        // description that carries a secret is a leak with a friendly layout.
        Assert.DoesNotContain("notarealsecret", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NotARealRefreshToken", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("notarealclientid", text, StringComparison.OrdinalIgnoreCase);
    }

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
            var root = Path.Combine(Path.GetTempPath(), "amazon-fill-" + Guid.NewGuid().ToString("N"));
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
            AmazonListingFillEndpoints.Map(app);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            return new Server(app, new HttpClient { BaseAddress = new Uri(address) }, root);
        }

        public async Task<JsonElement> FillAsync(AmazonFillRequest request)
        {
            var response = await _client.PostAsJsonAsync(AmazonListingFillEndpoints.FillPath, request);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        public async Task<(string ContentType, string Text)> ReportAsync(AmazonFillRequest request)
        {
            var response = await _client.PostAsJsonAsync(AmazonListingFillEndpoints.ReportPath, request);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
            return (response.Content.Headers.ContentType?.MediaType ?? "",
                    await response.Content.ReadAsStringAsync());
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
