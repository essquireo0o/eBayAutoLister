using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The promise the Local Deals panel is built on: a search always comes back with something
// renderable. Each case here is a way a real source has of failing — Craigslist serving a block
// page, Facebook's headless browser hanging, a missing Node runtime throwing on launch — and the
// assertion is always the same shape: a result, never an exception, naming the site that failed.
public class LocalSupplyGuardTests
{
    private sealed class StubSource(
        string id,
        Func<CancellationToken, Task<LocalSupplySearchResult>>? search = null,
        bool available = true,
        bool requiresConnection = false) : ILocalSupplySource
    {
        public int Calls { get; private set; }

        public string Id => id;
        public string Label => id;
        public bool RequiresConnection => requiresConnection;
        public bool IsAvailable => available;
        public string AvailabilityNote => available ? "ready" : "Needs a one-time login.";

        public Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default)
        {
            Calls++;
            return search is null
                ? Task.FromResult(new LocalSupplySearchResult { SourceId = id, SourceLabel = id, Status = "ok" })
                : search(ct);
        }
    }

    private static Task<LocalSupplySearchResult> Run(
        StubSource source, TimeSpan? timeout = null, CancellationToken ct = default) =>
        LocalSupplyGuard.RunAsync(
            source, token => source.SearchAsync("drill", "89101", 25, token),
            "drill", "89101", 25, timeout, ct);

    // ── Not connected ─────────────────────────────────────────────────────────

    // A disconnected Facebook is answered instantly instead of spending its whole browser-launch
    // budget to discover what IsAvailable already knew.
    [Fact]
    public async Task RunAsync_SourceNeedsALoginItDoesntHave_AnswersWithoutSearching()
    {
        var source = new StubSource("facebook", available: false, requiresConnection: true);

        var result = await Run(source);

        Assert.Equal("not_connected", result.Status);
        Assert.Equal(0, source.Calls);
        // The status alone renders as a bare chip; the seller needs the sentence and the reason.
        Assert.Contains("Connect facebook first", result.Error);
        Assert.Contains("one-time login", result.Error);
    }

    // A public source is never gated on connection — that's the whole reason Craigslist leads.
    [Fact]
    public async Task RunAsync_PublicSource_IsSearchedEvenThoughItHasNoLogin()
    {
        var source = new StubSource("craigslist", requiresConnection: false);

        var result = await Run(source);

        Assert.Equal("ok", result.Status);
        Assert.Equal(1, source.Calls);
    }

    // ── Failures that must not escape ─────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SourceThrows_BecomesAnErrorResultNamingTheSite()
    {
        var source = new StubSource("craigslist", _ => throw new HttpRequestException("connection reset"));

        var result = await Run(source);

        Assert.Equal("error", result.Status);
        Assert.Equal("craigslist", result.SourceId);
        Assert.Contains("connection reset", result.Error);
        Assert.True(result.Retryable);
    }

    [Fact]
    public async Task RunAsync_SourceFaultsAsynchronously_IsCaughtJustTheSame()
    {
        var source = new StubSource("facebook", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("node is not installed");
        });

        var result = await Run(source);

        Assert.Equal("error", result.Status);
        Assert.Contains("node is not installed", result.Error);
    }

    // "One or more errors occurred." is what a seller would otherwise be told.
    [Fact]
    public async Task RunAsync_AggregateException_ReportsTheMessageInsideIt()
    {
        var source = new StubSource("craigslist",
            _ => Task.FromException<LocalSupplySearchResult>(
                new AggregateException(new HttpRequestException("craigslist refused the connection"))));

        var result = await Run(source);

        Assert.Contains("craigslist refused the connection", result.Error);
        Assert.DoesNotContain("One or more errors", result.Error);
    }

    // The failure this whole class exists for: a source that hangs. The timeout is enforced on the
    // clock, not on the token, so a source that never checks for cancellation still can't hold the
    // endpoint open — which is what reaches the browser as a request that never resolves.
    [Fact]
    public async Task RunAsync_SourceHangsAndIgnoresItsToken_StillComesBack()
    {
        var released = new TaskCompletionSource<LocalSupplySearchResult>();
        var source = new StubSource("facebook", _ => released.Task);

        var result = await Run(source, TimeSpan.FromMilliseconds(150));

        Assert.Equal("error", result.Status);
        Assert.Contains("didn't answer within", result.Error);
        Assert.True(result.Retryable);

        released.SetResult(new LocalSupplySearchResult { Status = "ok" });   // let the abandoned task finish
    }

    [Fact]
    public async Task RunAsync_SourceReturnsNull_IsAnErrorRatherThanACrashDownstream()
    {
        var source = new StubSource("craigslist", _ => Task.FromResult<LocalSupplySearchResult>(null!));

        var result = await Run(source);

        Assert.Equal("error", result.Status);
        Assert.Equal("craigslist", result.SourceId);
    }

    // ── Normalising what a source returns ─────────────────────────────────────

    // The merger keys every decision off Status and the UI names sites from the labels, so a
    // result missing either is as unrenderable as no result at all.
    [Fact]
    public async Task RunAsync_ResultMissingItsLabels_IsFilledInFromTheSource()
    {
        var source = new StubSource("craigslist", _ => Task.FromResult(new LocalSupplySearchResult { Status = "ok" }));

        var result = await Run(source);

        Assert.Equal("craigslist", result.SourceId);
        Assert.Equal("craigslist", result.SourceLabel);
        Assert.Equal("drill", result.Query);
        Assert.Equal("89101", result.ZipCode);
        Assert.Equal(25, result.RadiusMiles);
    }

    [Fact]
    public async Task RunAsync_UnrecognisedStatus_BecomesError()
    {
        var source = new StubSource("craigslist", _ => Task.FromResult(new LocalSupplySearchResult { Status = "weird" }));

        var result = await Run(source);

        Assert.Equal("error", result.Status);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // An empty local market is a real answer. Dressing it up as a failure would put a connect
    // prompt in front of a seller whose search simply found nothing.
    [Fact]
    public async Task RunAsync_OkWithNoListings_StaysOk()
    {
        var source = new StubSource("craigslist",
            _ => Task.FromResult(new LocalSupplySearchResult { SourceId = "craigslist", Status = "ok" }));

        var result = await Run(source);

        Assert.Equal("ok", result.Status);
        Assert.Empty(result.Items);
        Assert.Null(result.Error);
    }

    // A source's own not_connected / session_expired reporting is passed through untouched — those
    // are the statuses that put the one-click fix on screen.
    [Fact]
    public async Task RunAsync_SessionExpired_IsPassedThroughUnchanged()
    {
        var source = new StubSource("facebook", requiresConnection: true, search:
            _ => Task.FromResult(new LocalSupplySearchResult
            {
                SourceId = "facebook", Status = "session_expired", Error = "Your saved session expired.",
            }));

        var result = await Run(source);

        Assert.Equal("session_expired", result.Status);
        Assert.Equal("Your saved session expired.", result.Error);
    }

    // ── Cancellation ──────────────────────────────────────────────────────────

    // The one thing that does propagate: the browser hung up, so there is nothing left to render
    // for and no reason to keep pricing results nobody will see.
    [Fact]
    public async Task RunAsync_CallerCancels_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var source = new StubSource("facebook", async token =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new LocalSupplySearchResult();
        });

        var running = Run(source, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
    }

    // ── Per-source budgets ────────────────────────────────────────────────────

    // A session-based source drives a real browser and legitimately takes a minute; a public one
    // is an HTTP call. One timeout for both would either cut Facebook off or let Craigslist stall.
    [Fact]
    public void TimeoutFor_GivesTheBrowserSourceTheLongerBudget()
    {
        var craigslist = new StubSource("craigslist", requiresConnection: false);
        var facebook = new StubSource("facebook", requiresConnection: true);

        Assert.True(LocalSupplyGuard.TimeoutFor(facebook) > LocalSupplyGuard.TimeoutFor(craigslist));
    }
}
