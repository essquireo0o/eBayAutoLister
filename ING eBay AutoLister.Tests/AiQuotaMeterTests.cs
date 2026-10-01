using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// "3 of 5 AI listings left today" — the number on the page, and the answer it is read from.
/// </summary>
/// <remarks>
/// <para>
/// The hosted build has rationed AI per account since the day it went live, and
/// <c>/api/ai-quota</c> has answered "how many are left" for exactly as long. The page never asked.
/// A seller learned the limit by hitting it, halfway through a listing. <see cref="AiQuotaTests"/>
/// proves the counting; this proves the two halves the meter on the page depends on.
/// </para>
/// <para>
/// <b>The shape.</b> The page reads six fields by name out of JSON, and a C# rename
/// (<c>Remaining</c> to <c>Left</c>) compiles, passes every test that deserialises into a record
/// with the same rename, and blanks the meter for every seller. So the names are read here as the
/// raw text a browser gets, on both builds: the desktop answer is the one that keeps the meter
/// hidden where there is nothing to ration.
/// </para>
/// <para>
/// <b>The page.</b> Where the number is drawn, that it starts hidden, and that it is re-read after
/// the requests that can spend one — each is a line somebody could tidy away without a compiler
/// error, and none of them changes anything a desktop tester would ever see.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class AiQuotaMeterTests
{
    private const string Password = "a-long-enough-password";

    private static readonly string Js = ReadAsset("app.js");
    private static readonly string Html = ReadAsset("index.html");
    private static readonly string Css = ReadAsset("style.css");
    private static readonly string Program = ReadSource("Program.cs");

    // ── The endpoint, as the browser receives it ─────────────────────────────────────────────

    [Fact]
    public async Task A_signed_in_seller_gets_exactly_the_six_fields_the_page_reads()
    {
        await using var server = await StartAsync(hosted: true, dailyLimit: 5);
        var seller = await server.SignUpAsync("seller@example.com");

        using var answer = await seller.QuotaJsonAsync();
        var quota = answer.RootElement;

        Assert.Equal(
            ["enforced", "exhausted", "limit", "remaining", "resetsAt", "used"],
            quota.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));

        Assert.Equal(JsonValueKind.True, quota.GetProperty("enforced").ValueKind);
        Assert.Equal(5, quota.GetProperty("limit").GetInt32());
        Assert.Equal(0, quota.GetProperty("used").GetInt32());
        Assert.Equal(5, quota.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.False, quota.GetProperty("exhausted").ValueKind);
    }

    /// <summary>
    /// The page turns this into "you get 5 more at 8:00 PM", so it has to be something
    /// <c>new Date()</c> reads — an ISO instant with its offset — and it has to be the next UTC
    /// midnight, which is when the count really does go back to zero.
    /// </summary>
    [Fact]
    public async Task The_reset_is_an_instant_a_browser_can_parse_and_it_is_the_next_utc_midnight()
    {
        await using var server = await StartAsync(hosted: true, dailyLimit: 5);
        var seller = await server.SignUpAsync("seller@example.com");

        var before = DateTimeOffset.UtcNow;
        using var answer = await seller.QuotaJsonAsync();
        var after = DateTimeOffset.UtcNow;

        var field = answer.RootElement.GetProperty("resetsAt");
        Assert.Equal(JsonValueKind.String, field.ValueKind);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T00:00:00(\.0+)?(Z|\+00:00)$", field.GetString());

        var resetsAt = field.GetDateTimeOffset();
        Assert.Contains(resetsAt, new[] { AiUsageStore.ResetAfter(before), AiUsageStore.ResetAfter(after) });
    }

    [Fact]
    public async Task The_number_goes_down_by_one_per_generation_and_reading_it_costs_nothing()
    {
        await using var server = await StartAsync(hosted: true, dailyLimit: 5);
        var seller = await server.SignUpAsync("seller@example.com");

        await seller.SpendAsync();
        await seller.SpendAsync();

        // "3 of 5 AI listings left today" — and still three after asking three times.
        for (var asked = 0; asked < 3; asked++)
        {
            using var answer = await seller.QuotaJsonAsync();
            Assert.Equal(2, answer.RootElement.GetProperty("used").GetInt32());
            Assert.Equal(3, answer.RootElement.GetProperty("remaining").GetInt32());
            Assert.Equal(JsonValueKind.False, answer.RootElement.GetProperty("exhausted").ValueKind);
        }

        for (var i = 0; i < 3; i++) await seller.SpendAsync();

        using var spent = await seller.QuotaJsonAsync();
        Assert.Equal(5, spent.RootElement.GetProperty("used").GetInt32());
        Assert.Equal(0, spent.RootElement.GetProperty("remaining").GetInt32());
        Assert.Equal(JsonValueKind.True, spent.RootElement.GetProperty("exhausted").ValueKind);
        Assert.Equal(JsonValueKind.True, spent.RootElement.GetProperty("enforced").ValueKind);
    }

    /// <summary>
    /// What hides the meter on the desktop app: the same endpoint, the same field names, and
    /// <c>enforced: false</c> with no number at all for "remaining" — not zero, which the page
    /// would have to draw as "none left", and not a large one, which it would draw as a promise.
    /// </summary>
    [Fact]
    public async Task The_desktop_build_answers_the_same_fields_and_says_nothing_is_rationed()
    {
        await using var server = await StartAsync(hosted: false, dailyLimit: 5);

        using var answer = await server.AnonymousQuotaJsonAsync();
        var quota = answer.RootElement;

        Assert.Equal(
            ["enforced", "exhausted", "limit", "remaining", "resetsAt", "used"],
            quota.EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal));

        Assert.Equal(JsonValueKind.False, quota.GetProperty("enforced").ValueKind);
        Assert.Equal(JsonValueKind.Null, quota.GetProperty("remaining").ValueKind);
        Assert.Equal(JsonValueKind.False, quota.GetProperty("exhausted").ValueKind);
        Assert.Equal(0, quota.GetProperty("limit").GetInt32());
        Assert.Equal(0, quota.GetProperty("used").GetInt32());
    }

    /// <summary>Nobody signed in, nobody's allowance to report: the hosted build's closed door.</summary>
    [Fact]
    public async Task On_the_hosted_build_the_meter_is_not_readable_without_signing_in()
    {
        await using var server = await StartAsync(hosted: true, dailyLimit: 5);

        var response = await server.AnonymousQuotaAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The server above maps the endpoint with the line the app maps it with. If Program.cs stops
    /// being that line, the tests above are testing a copy.
    /// </summary>
    [Fact]
    public void The_app_maps_the_endpoint_these_tests_exercise()
    {
        Assert.Contains("app.MapGet(\"/api/ai-quota\", (AiQuotaGate quota) => Results.Ok(quota.Status()));",
            Program, StringComparison.Ordinal);
    }

    // ── The page ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_number_sits_on_the_new_ai_listing_card_and_at_the_top_of_the_ai_listing_screen()
    {
        var card = Between(Html, "<button id=\"btn-new-ai-listing\"", "</button>");
        Assert.Contains("id=\"dash-ai-quota\"", card, StringComparison.Ordinal);

        // Above the buttons that spend it, inside the block they live in.
        var intake = Html.IndexOf("<div class=\"nl-url-section\">", StringComparison.Ordinal);
        var meter = Html.IndexOf("id=\"nl-ai-quota\"", StringComparison.Ordinal);
        var firstSpender = Html.IndexOf("id=\"nl-quickfill-go\"", StringComparison.Ordinal);
        Assert.True(intake >= 0 && intake < meter && meter < firstSpender,
            "the AI Listing screen's allowance line must come before Auto-Fill, inside .nl-url-section");
    }

    /// <summary>
    /// Hidden in the markup, so the desktop app — and a hosted page whose first read has not come
    /// back yet — shows nothing rather than an empty pill. Only <c>renderAiQuota</c> un-hides it.
    /// </summary>
    [Theory]
    [InlineData("dash-ai-quota")]
    [InlineData("nl-ai-quota")]
    public void Each_meter_starts_hidden_and_is_one_the_script_fills_in(string id)
    {
        var tag = Between(Html, $"id=\"{id}\"", ">");

        Assert.Contains("data-ai-quota", tag, StringComparison.Ordinal);
        Assert.Matches(@"class=""[^""]*\bhidden\b[^""]*""", tag);
        Assert.Matches(@"class=""[^""]*\bai-quota\b[^""]*""", tag);
    }

    [Fact]
    public void The_script_reads_the_endpoint_and_draws_nothing_unless_the_server_says_enforced()
    {
        var read = Between(Js, "async function refreshAiQuota()", "\n  }");
        Assert.Contains("passThroughFetch('/api/ai-quota'", read, StringComparison.Ordinal);
        // A meter that could not be read is left as it was, never redrawn as "none left".
        Assert.Contains("if (!res.ok) return;", read, StringComparison.Ordinal);
        Assert.Contains("renderAiQuota(await res.json())", read, StringComparison.Ordinal);

        var words = Between(Js, "function aiQuotaWords(q)", "\n  }");
        Assert.Contains("if (!q || !q.enforced || !(q.limit > 0)) return null;", words, StringComparison.Ordinal);
        Assert.Contains("`${left} of ${limit} AI listings left today`", words, StringComparison.Ordinal);

        var draw = Between(Js, "function renderAiQuota(q)", "\n  }");
        Assert.Contains("document.querySelectorAll('[data-ai-quota]')", draw, StringComparison.Ordinal);
        Assert.Contains("el.classList.toggle('hidden', !words);", draw, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Update after each generation" without a list of the buttons that generate: the one fetch
    /// wrapper every request already goes through reports each answer, and the meter is re-read
    /// after any that could have spent. A list of AI endpoints is a list to forget to add to.
    /// </summary>
    [Fact]
    public void The_number_is_re_read_after_any_request_that_could_have_spent_one()
    {
        var wrapper = Between(Js, "window.fetch = async (...args) => {", "\n  };");
        Assert.Contains("aiQuotaAfterRequest(args[0], args[1], response.status, Date.now() - asked);",
            wrapper, StringComparison.Ordinal);

        var after = Between(Js, "function aiQuotaAfterRequest(", "\n  }");
        // Nothing to keep current on the desktop build: the wrapper stays a no-op there.
        Assert.Contains("if (!aiQuotaLive) return;", after, StringComparison.Ordinal);
        // The meter's own read must not schedule another read of the meter.
        Assert.Contains("path.startsWith('/api/ai-quota')", after, StringComparison.Ordinal);
        // A quick GET is a status poll. A write, a slow answer or a 429 may be a generation.
        Assert.Contains("if (method === 'GET' && status !== 429 && tookMs < AI_QUOTA_SLOW_MS) return;",
            after, StringComparison.Ordinal);
        Assert.Contains("setTimeout(refreshAiQuota, AI_QUOTA_SETTLE_MS)", after, StringComparison.Ordinal);

        // And once on load, which is what puts the number there before anything is spent.
        Assert.Contains("document.addEventListener('DOMContentLoaded', refreshAiQuota);", Js, StringComparison.Ordinal);
    }

    /// <summary>
    /// The sentence itself, run rather than read: the real function out of app.js, given the
    /// answers the endpoint gives. Skipped on a machine with no Node — the pins above still hold.
    /// </summary>
    [Fact]
    public void The_sentence_says_how_many_are_left_of_how_many_and_nothing_at_all_on_desktop()
    {
        var function = "function aiQuotaWords(q)" + Between(Js, "function aiQuotaWords(q)", "\n  }") + "\n  }";
        var script = function + """

            const reset = '2026-10-02T00:00:00+00:00';
            console.log(JSON.stringify([
              aiQuotaWords({ enforced: true, limit: 5, used: 2, remaining: 3, exhausted: false, resetsAt: reset }),
              aiQuotaWords({ enforced: true, limit: 5, used: 4, remaining: 1, exhausted: false, resetsAt: reset }),
              aiQuotaWords({ enforced: true, limit: 5, used: 5, remaining: 0, exhausted: true, resetsAt: reset }),
              aiQuotaWords({ enforced: true, limit: 5, used: 1, resetsAt: reset }),
              aiQuotaWords({ enforced: false, limit: 0, used: 0, remaining: null, exhausted: false, resetsAt: reset }),
              aiQuotaWords(null),
            ]));
            """;

        if (RunNode(script) is not { } output) return;   // no node here

        using var results = JsonDocument.Parse(output);
        var said = results.RootElement.EnumerateArray().ToArray();

        Assert.Equal("3 of 5 AI listings left today", said[0].GetProperty("text").GetString());
        Assert.Equal("1 of 5 AI listings left today", said[1].GetProperty("text").GetString());
        Assert.Equal(1, said[1].GetProperty("left").GetInt32());

        Assert.Equal("No AI listings left today", said[2].GetProperty("text").GetString());
        Assert.Equal(0, said[2].GetProperty("left").GetInt32());
        // Out is not a dead end: when more arrive, and that the work already done is safe.
        Assert.Contains("You get 5 more at ", said[2].GetProperty("note").GetString());
        Assert.Contains("saved", said[2].GetProperty("note").GetString());

        // An older server that sends no `remaining` still gets the right number.
        Assert.Equal("4 of 5 AI listings left today", said[3].GetProperty("text").GetString());

        // Desktop, an unlimited account, and no answer: no sentence, so nothing is drawn.
        Assert.Equal(JsonValueKind.Null, said[4].ValueKind);
        Assert.Equal(JsonValueKind.Null, said[5].ValueKind);
    }

    [Fact]
    public void The_meter_is_styled_and_the_stamps_moved_so_no_browser_keeps_the_page_without_it()
    {
        Assert.Contains(".ai-quota {", Css, StringComparison.Ordinal);
        Assert.Contains(".ai-quota--low", Css, StringComparison.Ordinal);
        Assert.Contains(".ai-quota--out", Css, StringComparison.Ordinal);

        AssetStamp.AtLeast(Html, "app.js?v=", 175);
        AssetStamp.AtLeast(Html, "style.css?v=", 143);
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own quota and sign-in wiring around the endpoint as Program.cs maps it, plus one
    /// route that spends a generation the way <c>ClaudeService</c> does — by reserving it.
    /// </summary>
    private static async Task<MeterServer> StartAsync(bool hosted, int dailyLimit)
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-ai-quota-meter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AiQuota.DailyLimitSetting] = dailyLimit.ToString(),
        });

        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "App_Data", "users.db")));

        PerUserData.AddUserScope(builder, hosted: hosted);
        HostedAuth.AddAccounts(builder, hosted: hosted, secureCookie: false);
        AiQuota.AddAiQuota(builder, hosted: hosted);

        builder.Services.AddSingleton<ListingDatabase>();

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: hosted);
        HostedAuth.RequireSignIn(app, hosted: hosted);
        HostedAuth.MapAccountEndpoints(app, hosted: hosted);

        app.MapGet("/api/ai-quota", (AiQuotaGate quota) => Results.Ok(quota.Status()));

        app.MapPost("/api/spend", (AiQuotaGate quota) =>
        {
            quota.Reserve("AI listing from photo");
            return Results.Ok(new { generated = true });
        });

        await app.StartAsync();
        return new MeterServer(app, root);
    }

    /// <summary>One signed-in seller, with their own cookie jar.</summary>
    private sealed class Seller(HttpClient client)
    {
        public async Task<JsonDocument> QuotaJsonAsync() =>
            JsonDocument.Parse(await client.GetStringAsync("/api/ai-quota"));

        public async Task SpendAsync() =>
            (await client.PostAsync("/api/spend", null)).EnsureSuccessStatusCode();
    }

    private sealed class MeterServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        /// <remarks>Two calls: signing up does not sign you in. See <see cref="AiQuotaTests"/>.</remarks>
        public async Task<Seller> SignUpAsync(string email)
        {
            var client = NewClient();
            var signUp = await client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password = Password, name = "Dana Ellis" });
            signUp.EnsureSuccessStatusCode();

            var signIn = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password });
            signIn.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        public Task<HttpResponseMessage> AnonymousQuotaAsync() => NewClient().GetAsync("/api/ai-quota");

        public async Task<JsonDocument> AnonymousQuotaJsonAsync() =>
            JsonDocument.Parse(await NewClient().GetStringAsync("/api/ai-quota"));

        private HttpClient NewClient()
        {
            var jar = new CookieContainer();
            var client = new HttpClient(new CsrfClientHandler(jar, new HttpClientHandler
            {
                UseCookies        = true,
                CookieContainer   = jar,
                AllowAutoRedirect = false,
            }))
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            _clients.Add(client);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }

    // ── Reading the page ─────────────────────────────────────────────────────────────────────

    /// <summary>Runs a script under Node and returns what it printed, or null when there is no Node.</summary>
    private static string? RunNode(string script)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ai_quota_words_{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(NodeRuntime.NodeExe, $"\"{file}\"")
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (proc is null) return null;

            var stdout = proc.StandardOutput.ReadToEnd();
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit(30000);
            Assert.True(proc.ExitCode == 0, $"aiQuotaWords did not run:\n{stderr}");
            return stdout;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;   // node isn't installed here
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    private static string Between(string source, string start, string end)
    {
        var from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" is no longer in the file.");

        var to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, $"\"{end}\" does not follow \"{start}\".");

        return source[(from + start.Length)..to];
    }

    private static string ReadAsset(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "wwwroot", name));

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name.Replace('/', Path.DirectorySeparatorChar)));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
