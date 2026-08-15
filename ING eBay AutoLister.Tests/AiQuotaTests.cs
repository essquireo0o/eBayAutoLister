using System.Net;
using System.Net.Http.Json;
using ING_eBay_AutoLister.Models;
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
/// Proves that the hosted trial cannot be turned into an open tab at the owner's expense.
/// </summary>
/// <remarks>
/// <para>
/// Nobody signing up for the hosted build is asked for an Anthropic key — the owner's own key pays
/// for every user's analysis, deliberately. Which means every AI endpoint spends the owner's money
/// on a stranger's say-so, and one signed-up account with a loop can spend more in an afternoon
/// than the whole trial was budgeted for.
/// </para>
/// <para>
/// The tests below drive ONE gate while changing who is signed in and what time it is, because
/// that is what the app really has: the gate is a singleton injected into a singleton
/// <see cref="ClaudeService"/>, and the user is resolved per request. A test that built a second
/// gate for the second user would pass just as happily against a meter that counted everybody
/// together.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class AiQuotaTests : IDisposable
{
    private const long UserA = 1;
    private const long UserB = 2;

    private const string Password = "a-long-enough-password";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ai_quota_{Guid.NewGuid():N}.db");

    /// <summary>Who is signed in on the call in flight. Moved between assertions, never rebuilt.</summary>
    private long? _signedIn = UserA;

    /// <summary>What time the server thinks it is. Moved to cross midnight without waiting for it.</summary>
    private DateTimeOffset _now = new(2026, 8, 13, 15, 48, 0, TimeSpan.Zero);

    private AiUsageStore? _usage;

    private AiUsageStore Usage => _usage ??= new AiUsageStore(_dbPath);

    /// <summary>The hosted build's gate: one object, an answer that changes per request.</summary>
    private AiQuotaGate Gate(int dailyLimit) =>
        new(Usage, UserScope.PerUser(() => _signedIn), dailyLimit, () => _now);

    // ── The acceptance ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_sixth_generation_of_the_day_is_refused_when_five_are_allowed()
    {
        var gate = Gate(dailyLimit: 5);

        for (var i = 1; i <= 5; i++)
            gate.Reserve("AI listing from photo");

        var refused = Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        Assert.Equal(5, refused.Status.Limit);
        Assert.Equal(5, refused.Status.Used);
        Assert.Equal(0, refused.Status.Remaining);
        Assert.True(refused.Status.Exhausted);

        // And it stays refused — a refusal is not a one-off that the next press gets past.
        Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        // The refusals did not inflate the meter past the limit. It matters: the count is what the
        // page shows the seller and what the owner reads off the dashboard, and "8 of 5 used" is
        // the app reporting a number it did not spend.
        Assert.Equal(5, Usage.Used(UserA, AiUsageStore.DayOf(_now)));
    }

    [Fact]
    public void One_seller_using_up_their_day_leaves_another_seller_untouched()
    {
        var gate = Gate(dailyLimit: 5);

        _signedIn = UserA;
        for (var i = 1; i <= 5; i++) gate.Reserve("AI listing from photo");
        Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        // The second seller signs in behind them and has their whole day.
        _signedIn = UserB;
        Assert.Equal(5, gate.Status().Remaining);
        for (var i = 1; i <= 5; i++) gate.Reserve("AI listing from photo");
        Assert.Equal(0, gate.Status().Remaining);

        // And spending theirs gave the first one nothing back.
        _signedIn = UserA;
        Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));
        Assert.Equal(5, gate.Status().Used);
    }

    [Fact]
    public void The_counter_rolls_over_into_the_next_day()
    {
        var gate = Gate(dailyLimit: 5);

        for (var i = 1; i <= 5; i++) gate.Reserve("AI listing from photo");
        var refused = Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        // One minute short of the reset it is still refused — the boundary is the boundary, not a
        // rounding of it.
        _now = refused.Status.ResetsAt.AddMinutes(-1);
        Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        // And on the far side of it the day starts again, without anything having had to run at
        // midnight to make it happen.
        _now = refused.Status.ResetsAt;
        Assert.Equal(0, gate.Status().Used);
        Assert.Equal(5, gate.Status().Remaining);
        gate.Reserve("AI listing from photo");
        Assert.Equal(1, gate.Status().Used);

        // Yesterday's spend is still on record. It is what the owner reads when the bill arrives.
        Assert.Equal(5, Usage.Used(UserA, AiUsageStore.DayOf(refused.Status.ResetsAt.AddDays(-1))));
    }

    // ── What the person who ran out actually reads ───────────────────────────────────────────

    [Fact]
    public void The_refusal_says_there_is_a_limit_that_it_was_reached_and_when_it_lifts()
    {
        var gate = Gate(dailyLimit: 5);
        for (var i = 1; i <= 5; i++) gate.Reserve("AI listing from photo");

        var failure = Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo")).Failure;

        Assert.Equal(FailureKind.AiQuotaExhausted, failure.Kind);
        Assert.Equal(FailureDomain.Ai, failure.Domain);

        // The three things a refused person needs, and none of them a status code.
        Assert.Contains("5", failure.Headline);
        Assert.Contains("a day", failure.WhatHappened);
        Assert.Contains("00:00 UTC", failure.WhatToDo);
        Assert.Contains("8 hours 12 minutes", failure.WhatToDo);

        // No Retry button on something ten seconds cannot fix, and nothing they typed was lost.
        Assert.False(failure.Retryable);
        Assert.True(failure.WorkPreserved);

        // The same wait as a number, for anything that wants to count down rather than read.
        var untilReset = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero) - _now;
        Assert.Equal((int)untilReset.TotalSeconds, failure.RetryAfterSeconds);

        // And it is a sentence, not an exception type leaking through a stack trace.
        Assert.DoesNotContain("Exception", failure.Headline);
    }

    [Fact]
    public void A_failure_the_gate_already_explained_is_not_reclassified_into_something_vaguer()
    {
        var gate = Gate(dailyLimit: 1);
        gate.Reserve("AI listing from photo");
        var thrown = Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));

        // Every endpoint reports failures through this, and a quota refusal that arrived as
        // "Unknown — something went wrong" would be the clear message thrown away one layer below
        // where it was written.
        var translated = FailureTranslator.Translate(thrown, FailureDomain.Ai);

        Assert.Equal(FailureKind.AiQuotaExhausted, translated.Kind);
        Assert.Equal(thrown.Failure.WhatToDo, translated.WhatToDo);
        Assert.False(FailureTranslator.IsTransient(translated.Kind));
    }

    // ── The default the task specifies ───────────────────────────────────────────────────────

    [Fact]
    public void Ten_generations_a_day_is_what_an_unconfigured_deployment_gets()
    {
        Assert.Equal(10, AiQuota.DefaultDailyLimit);
        Assert.Equal(10, AiQuota.LimitFrom(Config(null)));

        // A blank or fat-fingered setting is NOT read as "no limit". A typo in an environment
        // variable must not be the thing that quietly opens the tab.
        Assert.Equal(10, AiQuota.LimitFrom(Config("")));
        Assert.Equal(10, AiQuota.LimitFrom(Config("ten")));

        // What the owner sets is what the owner gets, including a deliberate zero for a hosted
        // instance they are running for themselves alone.
        Assert.Equal(25, AiQuota.LimitFrom(Config("25")));
        Assert.Equal(0, AiQuota.LimitFrom(Config("0")));

        var gate = Gate(AiQuota.DefaultDailyLimit);
        for (var i = 1; i <= 10; i++) gate.Reserve("AI listing from photo");
        Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI listing from photo"));
    }

    // ── The ways round it that must not exist ────────────────────────────────────────────────

    [Fact]
    public void Two_generations_starting_at_once_cannot_both_spend_the_last_one()
    {
        var gate = Gate(dailyLimit: 5);

        // The listing editor fires several AI calls off one screen, so this is not hypothetical.
        // A read-then-increment in C# lets two requests both see four and both spend the fifth.
        var results = new bool[12];
        Parallel.For(0, results.Length, i =>
        {
            try { gate.Reserve("AI listing from photo"); results[i] = true; }
            catch (AiQuotaExceededException) { results[i] = false; }
        });

        Assert.Equal(5, results.Count(granted => granted));
        Assert.Equal(5, Usage.Used(UserA, AiUsageStore.DayOf(_now)));
    }

    [Fact]
    public void Work_running_in_the_background_with_nobody_signed_in_gets_no_ai_at_all()
    {
        var gate = Gate(dailyLimit: 5);

        // The whole-account SEO rewrite runs on a detached Task.Run, so it has no HttpContext and
        // no user. Unmetered, it is an AI call any signed-in person can start with one button and
        // that no allowance is ever charged for.
        _signedIn = null;

        var refused = Assert.Throws<AiQuotaExceededException>(() => gate.Reserve("AI SEO rewrite"));
        Assert.Equal(FailureKind.AiQuotaExhausted, refused.Failure.Kind);

        // Still a sentence, not a silence — this is what a seller reads when the whole-account
        // rewrite stops on the first listing.
        Assert.False(string.IsNullOrWhiteSpace(refused.Failure.WhatToDo));
        Assert.Contains("signed in", refused.Failure.Headline, StringComparison.OrdinalIgnoreCase);

        // Nothing was written against a made-up account on the way past.
        Assert.Empty(Usage.UsageOn(AiUsageStore.DayOf(_now)));
    }

    // ── The desktop build, which is not to change ────────────────────────────────────────────

    [Fact]
    public void The_desktop_build_counts_nothing_and_refuses_nothing()
    {
        // One seller on a loopback port, spending their own Anthropic key. Rationing it would be
        // the app limiting somebody's use of something they are paying for themselves.
        var gate = new AiQuotaGate(Usage, UserScope.Desktop, AiQuota.DefaultDailyLimit, () => _now);

        Assert.False(gate.Enforced);
        for (var i = 0; i < 50; i++) gate.Reserve("AI listing from photo");

        var status = gate.Status();
        Assert.False(status.Enforced);
        Assert.Null(status.Remaining);
        Assert.False(status.Exhausted);

        // Not merely allowed — not counted. The desktop database does not grow a row per analysis.
        Assert.Empty(Usage.UsageOn(AiUsageStore.DayOf(_now)));
        Assert.False(AiQuotaGate.Unmetered.Enforced);
        AiQuotaGate.Unmetered.Reserve("AI listing from photo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ClaudeService_can_still_be_built_by_the_container_in_both_builds(bool hosted)
    {
        // ClaudeService took two constructor arguments before this change and takes three now, and
        // it is resolved from the container on the first AI call rather than at startup — so a
        // registration this forgot would not fail a build or a boot, it would fail the first time
        // somebody pressed Analyse. Which is the worst place available to find out.
        var root = Path.Combine(Path.GetTempPath(), "ing-ai-quota", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.Logging.ClearProviders();

            PerUserData.AddUserScope(builder, hosted);
            AiQuota.AddAiQuota(builder, hosted);
            builder.Services.AddSingleton<ListingDatabase>();
            builder.Services.AddSingleton<ActionLog>();
            builder.Services.AddSingleton(new CredentialsStore(Path.Combine(root, "credentials.json")));
            builder.Services.AddSingleton<ClaudeService>();

            using var app = builder.Build();

            Assert.NotNull(app.Services.GetRequiredService<ClaudeService>());
            Assert.Equal(hosted, app.Services.GetRequiredService<AiQuotaGate>().Enforced);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }

    [Fact]
    public void An_owner_who_turned_the_limit_off_is_not_metered()
    {
        var gate = Gate(dailyLimit: 0);

        Assert.False(gate.Enforced);
        for (var i = 0; i < 20; i++) gate.Reserve("AI listing from photo");
        Assert.Empty(Usage.UsageOn(AiUsageStore.DayOf(_now)));
    }

    // ── Reading the usage back ───────────────────────────────────────────────────────────────

    [Fact]
    public void What_each_account_has_spent_today_is_readable_by_the_owner_who_is_paying()
    {
        var gate = Gate(dailyLimit: 10);

        _signedIn = UserA;
        for (var i = 0; i < 7; i++) gate.Reserve("AI listing from photo");

        _signedIn = UserB;
        for (var i = 0; i < 2; i++) gate.Reserve("AI listing from photo");

        // Per user, busiest first: on a deployment where one key pays for everybody, "who is
        // spending it" is the question the bill raises.
        var today = Usage.UsageOn(AiUsageStore.DayOf(_now));
        Assert.Equal([UserA, UserB], today.Select(row => row.UserId));
        Assert.Equal([7, 2], today.Select(row => row.Used));
        Assert.All(today, row => Assert.False(string.IsNullOrWhiteSpace(row.LastAt)));

        // And each account can read its own standing without spending anything to ask.
        _signedIn = UserB;
        var status = gate.Status();
        Assert.True(status.Enforced);
        Assert.Equal(2, status.Used);
        Assert.Equal(8, status.Remaining);
        Assert.Equal(new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero), status.ResetsAt);
        Assert.Equal(2, gate.Status().Used);
    }

    // ── That it is really in front of Anthropic, not beside it ───────────────────────────────

    [Fact]
    public async Task ClaudeService_refuses_an_exhausted_account_before_it_reaches_anthropic()
    {
        // The proof of ordering, without a network: this ClaudeService has NO API key, so the
        // moment a call gets past the gate it fails with "the key is not configured". Which of the
        // two failures comes back says whether the quota is checked before the spending starts or
        // after — and only one of those is a quota.
        var claude = ClaudeWithNoKey(Gate(dailyLimit: 5));

        // With allowance left, the call gets through the gate and dies on the missing key.
        var gotPast = await Assert.ThrowsAnyAsync<Exception>(
            () => claude.AnalyzeImageAsync("not-a-real-image", "image/jpeg"));
        Assert.IsNotType<AiQuotaExceededException>(gotPast);
        Assert.Contains("key", gotPast.Message, StringComparison.OrdinalIgnoreCase);

        // That attempt counted, because Anthropic bills an attempt and not a success.
        Assert.Equal(1, Usage.Used(UserA, AiUsageStore.DayOf(_now)));

        // Spend the rest, then the next one never gets as far as the key.
        for (var i = 0; i < 4; i++)
            await Assert.ThrowsAnyAsync<Exception>(() => claude.AnalyzeImageAsync("x", "image/jpeg"));

        var refused = await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.AnalyzeImageAsync("x", "image/jpeg"));
        Assert.Contains("00:00 UTC", refused.Failure.WhatToDo);

        // And it is every AI path, not the photo one — the gate sits in the single method they all
        // funnel through, so a path nobody thought about is metered by virtue of existing.
        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.ImproveSeoAsync(new ImproveSeoRequest { Title = "Antminer S19" }));
        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.ModifyListingAsync(new ModifyListingRequest { Instruction = "shorter title" }));
        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.AnalyzeUrlAsync("a product page", null, null));
        // Including the key check, which is a real request to Anthropic however small. A candidate
        // key is passed because an empty one is answered from disk without anybody being called,
        // and there is nothing to meter about that.
        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.CheckKeyAsync("sk-ant-not-a-real-key"));

        // Still five. Being refused six times does not cost six generations.
        Assert.Equal(5, Usage.Used(UserA, AiUsageStore.DayOf(_now)));
    }

    [Fact]
    public async Task The_spelling_check_the_app_starts_by_itself_does_not_cost_a_generation()
    {
        var claude = ClaudeWithNoKey(Gate(dailyLimit: 5));

        // Nobody asked for this — it fires when a search comes back empty — so charging a tenth of
        // somebody's day for a typo would be the app fining people for typing.
        await Assert.ThrowsAnyAsync<Exception>(() => claude.SuggestSearchSpellingAsync("antmnier s19"));
        Assert.Equal(0, Usage.Used(UserA, AiUsageStore.DayOf(_now)));

        // It is still not a way round the limit: an account with nothing left gets no calls at all.
        var gate = Gate(dailyLimit: 5);
        for (var i = 0; i < 5; i++) gate.Reserve("AI listing from photo");

        await Assert.ThrowsAsync<AiQuotaExceededException>(
            () => claude.SuggestSearchSpellingAsync("antmnier s19"));
    }

    // ── End to end, through two signed-in browsers ───────────────────────────────────────────

    [Fact]
    public async Task Two_signed_in_sellers_get_an_allowance_each_and_a_sentence_when_it_runs_out()
    {
        await using var server = await StartAsync(dailyLimit: 5);
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        Assert.Equal(5, (await first.QuotaAsync()).Remaining);

        for (var i = 1; i <= 5; i++)
            Assert.Equal(HttpStatusCode.OK, await first.GenerateAsync());

        var refusal = await first.GenerateRefusedAsync();

        // A rate limit is what this is, so 429 is what it says — but never on its own. The body
        // carries the whole explanation, because a status code is a thing a browser understands
        // and a seller does not.
        Assert.Equal(HttpStatusCode.TooManyRequests, refusal.Status);
        Assert.Contains("5 AI generations", refusal.Headline);
        Assert.Contains("00:00 UTC", refusal.WhatToDo);
        Assert.False(refusal.Retryable);
        Assert.Equal("AiQuotaExhausted", refusal.Kind);

        // Their own standing, read back without spending anything to ask.
        var spent = await first.QuotaAsync();
        Assert.Equal(5, spent.Used);
        Assert.Equal(0, spent.Remaining);
        Assert.True(spent.Exhausted);

        // The second seller, behind them on the same server and the same owner's key, is untouched.
        Assert.Equal(5, (await second.QuotaAsync()).Remaining);
        Assert.Equal(HttpStatusCode.OK, await second.GenerateAsync());
        Assert.Equal(4, (await second.QuotaAsync()).Remaining);

        // And the owner can see whose spending it was.
        var usage = new AiUsageStore(Path.Combine(server.Root, "App_Data", "ing_listing_engine.db"))
            .UsageOn(AiUsageStore.DayOf(DateTimeOffset.UtcNow));
        Assert.Equal([5, 1], usage.Select(row => row.Used));
    }

    [Fact]
    public async Task Signing_out_and_back_in_is_not_a_fresh_allowance()
    {
        await using var server = await StartAsync(dailyLimit: 5);
        var seller = await server.SignUpAsync("seller@example.com");

        for (var i = 1; i <= 5; i++) await seller.GenerateAsync();

        // The count is against the account, not the session. A cookie is not currency.
        var returning = await server.SignInAsync("seller@example.com");
        Assert.Equal(0, (await returning.QuotaAsync()).Remaining);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await returning.GenerateRefusedAsync()).Status);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private static IConfiguration Config(string? limit) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [AiQuota.DailyLimitSetting] = limit })
            .Build();

    /// <summary>
    /// A real <see cref="ClaudeService"/> over a credentials file that does not exist, so its
    /// Anthropic key is empty. Nothing here can reach the network, which is the point: the failure
    /// that comes back tells you how far the call got.
    /// </summary>
    private ClaudeService ClaudeWithNoKey(AiQuotaGate gate) => new(
        new CredentialsStore(Path.Combine(Path.GetTempPath(), $"ai_quota_creds_{Guid.NewGuid():N}.json")),
        new ActionLog(),
        gate);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own quota, auth and AI wiring around an endpoint shaped like the real
    /// <c>/api/analyze</c> — the closed-by-default policy, the per-request user, and the real
    /// <see cref="ClaudeService"/> behind it.
    /// </summary>
    private static async Task<QuotaServer> StartAsync(int dailyLimit)
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-ai-quota", Guid.NewGuid().ToString("N"));
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

        PerUserData.AddUserScope(builder, hosted: true);
        HostedAuth.AddAccounts(builder, hosted: true, secureCookie: false);
        AiQuota.AddAiQuota(builder, hosted: true);

        builder.Services.AddSingleton<ListingDatabase>();
        builder.Services.AddSingleton<ActionLog>();
        // No Anthropic key, so anything that gets past the gate fails on the key instead of on the
        // network. Which failure comes back is what these tests read.
        builder.Services.AddSingleton(new CredentialsStore(Path.Combine(root, "credentials.json")));
        builder.Services.AddSingleton<ClaudeService>();

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: true);
        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);

        app.MapGet("/api/ai-quota", (AiQuotaGate quota) => Results.Ok(quota.Status()));

        // Shaped like the real one: the endpoint knows nothing about quotas, because the gate is
        // inside ClaudeService. Getting as far as the missing key IS the success case here.
        app.MapPost("/api/analyze", async (ClaudeService claude) =>
        {
            try
            {
                await claude.AnalyzeImageAsync("not-a-real-image", "image/jpeg");
                return Results.Ok(new { generated = true });
            }
            catch (AiQuotaExceededException ex)
            {
                var failure = FailureTranslator.Translate(ex, FailureDomain.Ai);
                return Results.Json(new
                {
                    error = failure.Headline,
                    failure = new
                    {
                        kind         = failure.Kind.ToString(),
                        headline     = failure.Headline,
                        whatHappened = failure.WhatHappened,
                        whatToDo     = failure.WhatToDo,
                        retryable    = failure.Retryable,
                    },
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }
            catch (Exception)
            {
                // Past the gate and out at the missing key: a generation was spent.
                return Results.Ok(new { generated = true });
            }
        });

        await app.StartAsync();
        return new QuotaServer(app, root);
    }

    private sealed record QuotaResponse(bool Enforced, int Limit, int Used, int? Remaining, bool Exhausted);

    private sealed record RefusalBody(string Error, RefusalFailure Failure);

    private sealed record RefusalFailure(string Kind, string Headline, string WhatHappened, string WhatToDo, bool Retryable);

    private sealed record Refusal(HttpStatusCode Status, string Kind, string Headline, string WhatToDo, bool Retryable);

    /// <summary>One signed-in seller, with their own cookie jar.</summary>
    private sealed class Seller(HttpClient client) : IDisposable
    {
        public async Task<HttpStatusCode> GenerateAsync() =>
            (await client.PostAsync("/api/analyze", null)).StatusCode;

        public async Task<Refusal> GenerateRefusedAsync()
        {
            var response = await client.PostAsync("/api/analyze", null);
            var body = await response.Content.ReadFromJsonAsync<RefusalBody>();
            return new Refusal(response.StatusCode, body!.Failure.Kind, body.Failure.Headline,
                               body.Failure.WhatToDo, body.Failure.Retryable);
        }

        public async Task<QuotaResponse> QuotaAsync() =>
            (await client.GetFromJsonAsync<QuotaResponse>("/api/ai-quota"))!;

        public void Dispose() => client.Dispose();
    }

    private sealed class QuotaServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        public string Root => root;

        /// <remarks>
        /// Two calls, because signing up no longer signs you in — the endpoint answers a new
        /// address and an already-registered one identically so that it cannot be used to find
        /// out which addresses exist, and a session cookie on one branch would give that away.
        /// These tests want a signed-in seller, so they sign in.
        /// </remarks>
        public async Task<Seller> SignUpAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();

            var signIn = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password });
            signIn.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        public async Task<Seller> SignInAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        /// <summary>A separate cookie jar per client — which is what makes them different people.</summary>
        private HttpClient NewClient()
        {
            // The jar is held onto so CsrfClientHandler can read the antiforgery cookie out of it
            // and echo it back, which is the one browser behaviour a bare HttpClient lacks and the
            // server now insists on. See CsrfClientHandler.
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
}
