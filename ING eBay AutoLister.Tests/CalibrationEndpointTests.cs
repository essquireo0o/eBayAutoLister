using System.Net;
using System.Net.Http.Json;
using System.Text;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The calibration endpoints the arb-bot writes to and the owner reads. The one thing that must
/// hold: they are gated by the admin key exactly as /api/owner/stats is, so a stranger cannot post a
/// calibration that reprices every listing on the deployment.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class CalibrationEndpointTests : IAsyncLifetime
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "ing-calibration-api-" + Guid.NewGuid().ToString("N"));

    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _adminKey = "";
    private string _calibrationFile = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // The admin key lives on CredentialsStore, exactly as it does in the real app.
        var credentials = new CredentialsStore(Path.Combine(_root, "credentials.json"));
        _adminKey = credentials.EnsureAdminKey();

        var calibration = new CalibrationStore(Path.Combine(_root, "App_Data", "calibration.json"));
        _calibrationFile = Path.Combine(_root, "App_Data", "calibration.json");

        builder.Services.AddSingleton(credentials);
        builder.Services.AddSingleton(calibration);
        builder.Services.AddSingleton<ActionLog>();

        _app = builder.Build();
        // The very same handlers Program.cs maps — the endpoint under test is real, not a shape.
        CalibrationEndpoints.Map(_app);

        await _app.StartAsync();
        _client = new HttpClient { BaseAddress = new Uri(_app.Urls.First()) };
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static StringContent Body(string json) => new(json, Encoding.UTF8, "application/json");

    private const string SampleJson = """
        {
          "generatedUtc": "2026-08-14T00:00:00Z",
          "sampleSize": 123,
          "overallBiasPct": 8.4,
          "buckets": {
            "thin(<8 comps)": { "biasPct": 12.1, "n": 40 },
            "mid(8-20)":      { "biasPct": 6.2,  "n": 55 },
            "deep(>20)":      { "biasPct": 3.0,  "n": 28 }
          },
          "predictor": "median-of-comps-v1"
        }
        """;

    // ── The gate ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithoutTheAdminKey_Is401AndStoresNothing()
    {
        var response = await _client.PostAsync("/api/calibration/update", Body(SampleJson));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(File.Exists(_calibrationFile));   // nothing was written
    }

    [Fact]
    public async Task Update_WithTheWrongAdminKey_Is401()
    {
        var response = await _client.PostAsync(
            "/api/calibration/update?k=deadbeefdeadbeefdeadbeefdeadbeef", Body(SampleJson));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Read_WithoutTheAdminKey_Is401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _client.GetAsync("/api/calibration")).StatusCode);
    }

    // ── The happy path ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Update_WithTheAdminKey_StoresIt_AndReadBackReturnsIt()
    {
        var post = await _client.PostAsync(
            $"/api/calibration/update?k={_adminKey}", Body(SampleJson));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var ack = await post.Content.ReadFromJsonAsync<UpdateAck>();
        Assert.True(ack!.Ok);
        Assert.Equal(123, ack.SampleSize);
        Assert.Equal(3, ack.Buckets);

        // And it survives to disk and reads back through the GET, with labels normalized.
        var read = await _client.GetFromJsonAsync<CalibrationData>($"/api/calibration?k={_adminKey}");
        Assert.NotNull(read);
        Assert.Equal(123, read!.SampleSize);
        Assert.Equal("median-of-comps-v1", read.Predictor);
        Assert.True(read.Buckets.ContainsKey("thin"));
        Assert.Equal(40, read.Buckets["thin"].N);
    }

    [Fact]
    public async Task Update_WithAGarbageBody_Is400_AndNeverThrowsToTheClient()
    {
        var response = await _client.PostAsync(
            $"/api/calibration/update?k={_adminKey}", Body("this is not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed record UpdateAck(bool Ok, int SampleSize, int Buckets);
}
