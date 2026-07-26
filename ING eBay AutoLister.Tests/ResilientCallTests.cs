using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The retry layer exists because a single Anthropic "overloaded" used to destroy a listing analysis
// the seller had waited minutes and real API spend for. These tests pin both halves of that: a
// transient failure is retried, and a permanent one fails immediately instead of making the seller
// wait out three attempts before being told to go and fix their API key.
public class ResilientCallTests
{
    // ── Backoff schedule ────────────────────────────────────────────────────

    [Fact]
    public void The_wait_grows_between_attempts()
    {
        var first = ResilientCall.Backoff(1, null);
        var second = ResilientCall.Backoff(2, null);
        var third = ResilientCall.Backoff(3, null);

        Assert.True(second > first);
        Assert.True(third > second);
    }

    // A seller is sitting in front of this. Past a certain wait, telling them to try again themselves
    // beats a page that appears to have hung.
    [Fact]
    public void The_wait_is_capped_however_many_attempts_have_failed()
    {
        Assert.True(ResilientCall.Backoff(20, null) <= TimeSpan.FromSeconds(30));
        Assert.True(ResilientCall.Backoff(200, null) <= TimeSpan.FromSeconds(30));
    }

    // Hammering a service that just said "wait 25 seconds" is how a soft rate limit becomes a hard
    // one, so the server's own instruction wins whenever it asks for longer than the backoff.
    [Fact]
    public void A_server_requested_wait_wins_when_it_is_longer()
    {
        var delay = ResilientCall.Backoff(1, retryAfterSeconds: 25);

        Assert.True(delay >= TimeSpan.FromSeconds(24), $"Expected at least ~25s, got {delay}");
    }

    [Fact]
    public void A_server_requested_wait_shorter_than_the_backoff_does_not_shorten_it()
    {
        var withoutHint = ResilientCall.Backoff(3, null);
        var withHint = ResilientCall.Backoff(3, retryAfterSeconds: 1);

        Assert.Equal(withoutHint, withHint);
    }

    // A service asking us to wait an hour is not something to sit on with a spinner up.
    [Fact]
    public void An_absurd_server_requested_wait_is_still_capped()
    {
        Assert.True(ResilientCall.Backoff(1, retryAfterSeconds: 3600) <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Jitter_moves_the_wait_without_changing_its_scale()
    {
        var low = ResilientCall.Backoff(2, null, jitter: 0.8);
        var high = ResilientCall.Backoff(2, null, jitter: 1.2);
        var mid = ResilientCall.Backoff(2, null, jitter: 1.0);

        Assert.True(low < mid);
        Assert.True(high > mid);
        Assert.True(high < mid * 1.5);
    }

    [Fact]
    public void Jitter_is_clamped_so_a_bad_value_cannot_produce_a_silly_wait()
    {
        Assert.True(ResilientCall.Backoff(1, null, jitter: 99) <= TimeSpan.FromSeconds(30));
        Assert.True(ResilientCall.Backoff(1, null, jitter: -5) > TimeSpan.Zero);
    }

    // ── The retry decision ──────────────────────────────────────────────────

    [Fact]
    public void ShouldRetry_says_yes_to_a_transient_failure_with_attempts_left()
    {
        var overloaded = new FailureInfo { Kind = FailureKind.Overloaded };

        Assert.True(ResilientCall.ShouldRetry(overloaded, completedAttempts: 1, maxAttempts: 3));
        Assert.True(ResilientCall.ShouldRetry(overloaded, completedAttempts: 2, maxAttempts: 3));
        Assert.False(ResilientCall.ShouldRetry(overloaded, completedAttempts: 3, maxAttempts: 3));
    }

    [Fact]
    public void ShouldRetry_says_no_to_a_permanent_failure_however_many_attempts_remain()
    {
        var rejectedKey = new FailureInfo { Kind = FailureKind.AiKeyRejected };

        Assert.False(ResilientCall.ShouldRetry(rejectedKey, completedAttempts: 1, maxAttempts: 5));
    }

    // ── Running work ────────────────────────────────────────────────────────

    [Fact]
    public async Task Work_that_succeeds_first_time_runs_exactly_once()
    {
        var calls = 0;
        var result = await ResilientCall.RunAsync(() => { calls++; return Task.FromResult(42); },
            FailureDomain.Ai, "test");

        Assert.Equal(42, result);
        Assert.Equal(1, calls);
    }

    // The whole point: a transient blip must not cost the seller their analysis.
    [Fact]
    public async Task A_transient_failure_is_retried_and_the_later_success_is_returned()
    {
        var calls = 0;
        var result = await ResilientCall.RunAsync(() =>
        {
            calls++;
            if (calls < 3) throw new Exception("overloaded_error");
            return Task.FromResult("written");
        }, FailureDomain.Ai, "test", maxAttempts: 3);

        Assert.Equal("written", result);
        Assert.Equal(3, calls);
    }

    // A rejected key retried three times is three delays before the seller is told to fix the key.
    [Fact]
    public async Task A_permanent_failure_is_not_retried()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<AppFailureException>(() =>
            ResilientCall.RunAsync<string>(() =>
            {
                calls++;
                throw new Exception("authentication_error: invalid x-api-key");
            }, FailureDomain.Ai, "test", maxAttempts: 3));

        Assert.Equal(1, calls);
        Assert.Equal(FailureKind.AiKeyRejected, ex.Failure.Kind);
    }

    [Fact]
    public async Task Exhausting_the_attempts_reports_the_last_failure_and_how_many_were_tried()
    {
        var calls = 0;
        var ex = await Assert.ThrowsAsync<AppFailureException>(() =>
            ResilientCall.RunAsync<string>(() =>
            {
                calls++;
                throw new Exception("overloaded_error");
            }, FailureDomain.Ai, "test", maxAttempts: 2));

        Assert.Equal(2, calls);
        Assert.Equal(FailureKind.Overloaded, ex.Failure.Kind);
        Assert.Equal(2, ex.Failure.Attempts);
        // Still retryable: the seller pressing Try again is a genuinely different attempt.
        Assert.True(ex.Failure.Retryable);
    }

    // The browser has gone. Retrying — and paying for another model call — is work for nobody.
    [Fact]
    public async Task A_cancelled_caller_stops_the_retries_immediately()
    {
        using var cts = new CancellationTokenSource();
        var calls = 0;

        await Assert.ThrowsAnyAsync<Exception>(() =>
            ResilientCall.RunAsync<string>(() =>
            {
                calls++;
                cts.Cancel();
                throw new Exception("overloaded_error");
            }, FailureDomain.Ai, "test", maxAttempts: 5, cancellationToken: cts.Token));

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_cancellation_thrown_by_the_work_itself_is_not_swallowed_as_a_retryable_failure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ResilientCall.RunAsync<string>(
                () => throw new OperationCanceledException(),
                FailureDomain.Ai, "test", cancellationToken: cts.Token));
    }

    [Fact]
    public async Task The_void_overload_retries_the_same_way()
    {
        var calls = 0;
        await ResilientCall.RunAsync(() =>
        {
            calls++;
            if (calls < 2) throw new Exception("overloaded_error");
            return Task.CompletedTask;
        }, FailureDomain.Ai, "test", maxAttempts: 3);

        Assert.Equal(2, calls);
    }
}
