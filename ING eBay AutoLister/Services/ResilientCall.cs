using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Runs a critical-path operation with bounded retries, and converts whatever it finally fails with
/// into a classified <see cref="AppFailureException"/>.
/// </summary>
/// <remarks>
/// <para>
/// The failure this exists for: a single Anthropic 529 used to destroy a whole analysis. The seller
/// waits a minute or two for a listing to be written, Anthropic answers "overloaded", and the app
/// reports failure — throwing away a request that would have succeeded eight seconds later. That is
/// the most common real failure on the AI path and the most completely recoverable one.
/// </para>
/// <para>
/// Retries are only ever attempted for kinds <see cref="FailureTranslator.IsTransient"/> agrees are
/// transient. A rejected API key retried three times is three times the delay before the seller is
/// told to go and fix the key, so a permanent failure fails immediately and says what to fix.
/// </para>
/// <para>
/// Nothing here retries a write to eBay. A publish is the one operation whose failure may be a lie —
/// a timeout can mean "created, then the answer got lost" — so retrying it risks two live listings
/// for one item. That path uses <see cref="PublishGuard"/> instead.
/// </para>
/// </remarks>
public static class ResilientCall
{
    /// <summary>Three attempts: enough to ride out a transient blip, few enough to stay responsive.</summary>
    public const int DefaultAttempts = 3;

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to wait before attempt number <paramref name="completedAttempts"/> + 1.
    /// </summary>
    /// <param name="completedAttempts">Attempts already made — 1 after the first failure.</param>
    /// <param name="retryAfterSeconds">A server-specified wait, honoured when longer than the backoff.</param>
    /// <param name="jitter">
    /// Multiplier in the 0.8–1.2 range, passed in rather than generated internally so the schedule is
    /// deterministic under test.
    /// </param>
    /// <remarks>
    /// Exponential with jitter, and a server's own Retry-After always wins when it asks for longer —
    /// hammering a service that just said "wait 30 seconds" is how a soft rate limit becomes a hard
    /// one. The 30-second ceiling exists because a seller is sitting in front of this: past that,
    /// telling them to try again themselves beats a page that appears to have hung.
    /// </remarks>
    public static TimeSpan Backoff(int completedAttempts, int? retryAfterSeconds, double jitter = 1.0)
    {
        var exponent = Math.Max(0, completedAttempts - 1);
        var scaled = FirstDelay.TotalMilliseconds * Math.Pow(2, Math.Min(exponent, 6));
        var delay = TimeSpan.FromMilliseconds(Math.Min(scaled, MaxDelay.TotalMilliseconds));

        if (retryAfterSeconds is > 0)
        {
            var requested = TimeSpan.FromSeconds(Math.Min(retryAfterSeconds.Value, 30));
            if (requested > delay) delay = requested;
        }

        var clampedJitter = Math.Clamp(jitter, 0.5, 1.5);
        return TimeSpan.FromMilliseconds(Math.Round(delay.TotalMilliseconds * clampedJitter));
    }

    /// <summary>Whether another attempt is worth making, given what went wrong and where we are.</summary>
    public static bool ShouldRetry(FailureInfo failure, int completedAttempts, int maxAttempts) =>
        completedAttempts < maxAttempts && FailureTranslator.IsTransient(failure.Kind);

    public static async Task<T> RunAsync<T>(
        Func<Task<T>> work,
        FailureDomain domain,
        string operation,
        ActionLog? log = null,
        int maxAttempts = DefaultAttempts,
        CancellationToken cancellationToken = default)
    {
        var attempts = 0;
        FailureInfo? lastFailure = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempts++;
            try
            {
                return await work();
            }
            catch (Exception ex)
            {
                // The seller navigated away or closed the page. There is nobody left to retry for.
                if (cancellationToken.IsCancellationRequested) throw;

                var failure = FailureTranslator.Translate(ex, domain, attempts);
                lastFailure = failure;

                if (!ShouldRetry(failure, attempts, maxAttempts))
                {
                    log?.Add("Warning", $"{operation} failed",
                        $"{failure.Kind} after {attempts} attempt(s): {failure.Technical}");
                    throw new AppFailureException(failure);
                }

                var delay = Backoff(attempts, failure.RetryAfterSeconds, Jitter());
                log?.Add("Info", $"{operation} retrying",
                    $"{failure.Kind} on attempt {attempts} of {maxAttempts}; waiting {delay.TotalSeconds:F1}s");

                try
                {
                    await Task.Delay(delay, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw new AppFailureException(lastFailure);
                }
            }
        }
    }

    public static Task RunAsync(
        Func<Task> work,
        FailureDomain domain,
        string operation,
        ActionLog? log = null,
        int maxAttempts = DefaultAttempts,
        CancellationToken cancellationToken = default) =>
        RunAsync<bool>(async () => { await work(); return true; },
            domain, operation, log, maxAttempts, cancellationToken);

    // Spread concurrent retries so several requests failing together don't all come back in the
    // same millisecond and reproduce the overload that caused the failure.
    private static double Jitter() => 0.8 + Random.Shared.NextDouble() * 0.4;
}
