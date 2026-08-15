using System.Text.Json;
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
/// <c>/api/amazon/status</c> over real HTTP, against the handler Program.cs maps.
/// </summary>
/// <remarks>
/// <para>
/// A real server on a real socket rather than a direct call to the handler, because the thing being
/// checked is the answer an operator gets — the JSON shape, the field names, and above all what is
/// <i>not</i> in it. <see cref="The_status_response_carries_no_credential"/> is the one that earns
/// this file: every other assertion here would still pass if the endpoint helpfully echoed the
/// client secret back.
/// </para>
/// <para>
/// No test in this file reaches Amazon. The configurations exercised are ones where the answer is
/// decided before a request would be made — which is not a limitation, it is the case this phase is
/// actually in.
/// </para>
/// </remarks>
public class AmazonStatusEndpointTests
{
    private const string ClientId     = "amzn1.application-oa2-client.notarealclientid";
    private const string ClientSecret = "amzn1.oa2-cs.v1.notarealsecret";

    [Fact]
    public async Task A_deployment_with_nothing_configured_says_so_and_names_the_first_thing_to_set()
    {
        using var server = await AmazonStatusServer.StartAsync();
        var body = await server.GetStatusAsync();

        Assert.False(body.GetProperty("configured").GetBoolean());
        Assert.False(body.GetProperty("applicationConfigured").GetBoolean());
        // Null, not false: nothing was asked of Amazon, so nothing was refused by Amazon.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("tokenObtainable").ValueKind);
        Assert.Equal("no_client_id", body.GetProperty("code").GetString());
        Assert.Contains("Credentials__AmazonClientId", body.GetProperty("nextAction").GetString());
    }

    [Fact]
    public async Task The_owners_sandbox_application_reports_an_application_without_a_seller()
    {
        // The exact state this app is in at the end of Amazon phase 1.
        using var server = await AmazonStatusServer.StartAsync(
            ("Credentials__AmazonClientId",     ClientId),
            ("Credentials__AmazonClientSecret", ClientSecret));

        var body = await server.GetStatusAsync();

        Assert.False(body.GetProperty("configured").GetBoolean());
        Assert.True(body.GetProperty("applicationConfigured").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("tokenObtainable").ValueKind);
        Assert.Equal("no_refresh_token", body.GetProperty("code").GetString());

        var has = body.GetProperty("has");
        Assert.True(has.GetProperty("clientId").GetBoolean());
        Assert.True(has.GetProperty("clientSecret").GetBoolean());
        Assert.False(has.GetProperty("refreshToken").GetBoolean());
        Assert.False(has.GetProperty("marketplaceId").GetBoolean());
        Assert.False(has.GetProperty("sellerId").GetBoolean());
    }

    [Fact]
    public async Task The_environment_reported_is_the_sandbox_and_it_names_the_host()
    {
        using var server = await AmazonStatusServer.StartAsync();
        var body = await server.GetStatusAsync();

        Assert.True(body.GetProperty("sandbox").GetBoolean());
        Assert.Equal("Sandbox", body.GetProperty("environment").GetString());
        Assert.Equal("NorthAmerica", body.GetProperty("region").GetString());
        Assert.Equal("sandbox.sellingpartnerapi-na.amazon.com", body.GetProperty("apiHost").GetString());
    }

    [Fact]
    public async Task Leaving_the_sandbox_is_visible_in_the_answer()
    {
        // The one setting worth being able to read off a live deployment without guessing.
        using var server = await AmazonStatusServer.StartAsync(("Credentials__AmazonSandbox", "false"));
        var body = await server.GetStatusAsync();

        Assert.False(body.GetProperty("sandbox").GetBoolean());
        Assert.Equal("Production", body.GetProperty("environment").GetString());
        Assert.Equal("sellingpartnerapi-na.amazon.com", body.GetProperty("apiHost").GetString());
    }

    [Fact]
    public async Task The_status_response_carries_no_credential()
    {
        // Fully configured, so every one of the five is present to be leaked.
        using var server = await AmazonStatusServer.StartAsync(
            ("Credentials__AmazonClientId",      ClientId),
            ("Credentials__AmazonClientSecret",  ClientSecret),
            ("Credentials__AmazonRefreshToken",  "Atzr|IwEBIExampleRefreshTokenThatIsNotReal0000000000"),
            ("Credentials__AmazonMarketplaceId", "ATVPDKIKX0DER"),
            ("Credentials__AmazonSellerId",      "A0000000000000"));

        var raw = await server.GetStatusTextAsync();

        Assert.DoesNotContain(ClientId, raw);
        Assert.DoesNotContain(ClientSecret, raw);
        Assert.DoesNotContain("Atzr|", raw);
        // The marketplace and seller are not secrets, but they are the seller's, and a diagnostic
        // that reports "which" rather than "whether" has started answering a different question.
        Assert.DoesNotContain("A0000000000000", raw);
        Assert.DoesNotContain("ATVPDKIKX0DER", raw);
    }

    // ── A server, on a port the OS picks ─────────────────────────────────────────────────────

    /// <summary>
    /// One app with the real endpoint mapped on it. Port 0 deliberately: the desktop app binds a
    /// fixed 9332 (<see cref="AppPaths.Port"/>) and a test that took it would fight the running app
    /// for the port that eBay's relay redirects to.
    /// </summary>
    private sealed class AmazonStatusServer : IDisposable
    {
        private readonly WebApplication _app;
        private readonly HttpClient _client;

        private AmazonStatusServer(WebApplication app, HttpClient client)
        {
            _app = app;
            _client = client;
        }

        public static async Task<AmazonStatusServer> StartAsync(params (string Key, string Value)[] variables)
        {
            var root = Path.Combine(Path.GetTempPath(), "amazon-status-" + Guid.NewGuid().ToString("N"));
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

            var app = builder.Build();
            AmazonStatusEndpoint.Map(app);
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!.Addresses.First();

            return new AmazonStatusServer(app, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async Task<JsonElement> GetStatusAsync() =>
            JsonDocument.Parse(await GetStatusTextAsync()).RootElement.Clone();

        public async Task<string> GetStatusTextAsync()
        {
            var response = await _client.GetAsync(AmazonStatusEndpoint.Path);
            Assert.True(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode} from {AmazonStatusEndpoint.Path}");
            return await response.Content.ReadAsStringAsync();
        }

        public void Dispose()
        {
            _client.Dispose();
            _app.StopAsync().GetAwaiter().GetResult();
            ((IDisposable)_app).Dispose();
        }
    }
}
