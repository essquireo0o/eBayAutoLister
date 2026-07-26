using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The rule the local-sourcing feature can't afford to break: a search always comes back with
/// something the UI can render.
///
/// Every source is a different kind of fragile. Craigslist is a public site that answers a
/// request it doesn't like with a block page; Facebook is a headless browser driving a login-gated
/// page that can hang, crash on a missing Node, or find its saved session gone. Left to itself any
/// of those turns into an unhandled exception at the HTTP edge, which reaches the browser as a 500
/// with an HTML body — and a <c>fetch(...).then(r =&gt; r.json())</c> against that rejects, so the
/// panel dead-ends on "Failed to fetch" with no results, no explanation and no way forward. The
/// seller sees a broken app; what actually happened is one site being one site.
///
/// So every source is searched through here, and this returns a <see cref="LocalSupplySearchResult"/>
/// in every case, never an exception:
///   • <b>Not connected</b> is answered here rather than by the source, before any work starts.
///     A disconnected Facebook otherwise spends its full browser-launch budget to discover what
///     <see cref="ILocalSupplySource.IsAvailable"/> already knew.
///   • <b>Slow</b> is bounded by a wall-clock timeout enforced with <see cref="Task.WhenAny"/>
///     rather than by the cancellation token alone, so a source that never observes its token
///     still can't hold the response open.
///   • <b>Broken</b> — any exception at all — becomes an <c>error</c> result naming the site.
///   • <b>Malformed</b> — a null result, a blank status, missing labels — is normalised, because a
///     result the merger can't read is the same dead end as no result.
///
/// The one exception that does propagate is the caller's own cancellation: if the browser hung up
/// there is nothing left to render for, and continuing to price the results would be work for
/// nobody.
///
/// Nothing here retries. A source that just refused us is not a source to ask again immediately —
/// the result carries <see cref="LocalSupplySearchResult.Retryable"/> and the seller decides.
/// </summary>
public static class LocalSupplyGuard
{
    /// <summary>
    /// Public sources are one HTTP request or two, so a slow one is a broken one. Craigslist
    /// bounds itself well inside this (see CraigslistService); this is the backstop.
    /// </summary>
    public static readonly TimeSpan PublicSourceTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Session-based sources drive a real browser: a page load, a location dialog and a scrolled
    /// grid. Generous on purpose — the failure mode being prevented is a hang, not slowness.
    /// </summary>
    public static readonly TimeSpan SessionSourceTimeout = TimeSpan.FromMinutes(3);

    private static readonly string[] KnownStatuses = ["ok", "not_connected", "session_expired", "error"];

    public static TimeSpan TimeoutFor(ILocalSupplySource source) =>
        source.RequiresConnection ? SessionSourceTimeout : PublicSourceTimeout;

    /// <summary>
    /// Runs one source's search and guarantees a usable result. <paramref name="search"/> is a
    /// callback rather than a straight <c>source.SearchAsync</c> call so the HTTP edge can keep
    /// passing its one site-specific parameter (Craigslist's metro override) without this knowing
    /// the parameter exists.
    /// </summary>
    public static async Task<LocalSupplySearchResult> RunAsync(
        ILocalSupplySource source,
        Func<CancellationToken, Task<LocalSupplySearchResult>> search,
        string query, string zip, int radiusMiles,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        // Answered without touching the source: this is a state the seller fixes in one click, and
        // saying so instantly beats saying so after a two-minute browser launch that was never
        // going to work. Craigslist and every other public source skip this entirely.
        if (source.RequiresConnection && !source.IsAvailable)
        {
            return Failure(source, "not_connected", query, zip, radiusMiles,
                $"Connect {source.Label} first. {source.AvailabilityNote}".TrimEnd());
        }

        var budget = timeout ?? TimeoutFor(source);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(budget);

        Task<LocalSupplySearchResult> searchTask;
        try
        {
            searchTask = search(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A source that throws before its first await — a bad URL, a null dependency.
            return Failure(source, "error", query, zip, radiusMiles,
                $"{source.Label} couldn't be searched: {Describe(ex)}", retryable: true);
        }

        if (searchTask is null)
        {
            return Failure(source, "error", query, zip, radiusMiles,
                $"{source.Label} didn't start a search.", retryable: true);
        }

        // WhenAny rather than awaiting the token: a source is free to ignore cancellation (the
        // Facebook one is a child process behind a Node runtime), and the whole point of this
        // method is that no source can hold the endpoint open regardless of how it behaves.
        var finished = await Task.WhenAny(searchTask, Task.Delay(budget, ct)).ConfigureAwait(false);
        if (finished != searchTask)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            Abandon(searchTask);

            return Failure(source, "error", query, zip, radiusMiles,
                $"{source.Label} didn't answer within {(int)budget.TotalSeconds} seconds — try it again, " +
                "or untick it and search the other sites.", retryable: true);
        }

        try
        {
            return Normalize(await searchTask.ConfigureAwait(false), source, query, zip, radiusMiles);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Failure(source, "error", query, zip, radiusMiles,
                $"{source.Label} took too long and was stopped — try it again in a moment.", retryable: true);
        }
        catch (Exception ex)
        {
            return Failure(source, "error", query, zip, radiusMiles,
                $"{source.Label} couldn't be searched: {Describe(ex)}", retryable: true);
        }
    }

    /// <summary>
    /// Fills in whatever a source left blank and forces an unrecognised status to <c>error</c>.
    /// The merger keys every decision it makes off <see cref="LocalSupplySearchResult.Status"/>,
    /// and the UI names sites from the labels — a result missing either is as unrenderable as no
    /// result at all, so it's repaired here rather than defended against in four places.
    /// </summary>
    public static LocalSupplySearchResult Normalize(
        LocalSupplySearchResult? result, ILocalSupplySource source, string query, string zip, int radiusMiles)
    {
        if (result is null)
            return Failure(source, "error", query, zip, radiusMiles, $"{source.Label} returned no result.", retryable: true);

        if (string.IsNullOrWhiteSpace(result.SourceId)) result.SourceId = source.Id;
        if (string.IsNullOrWhiteSpace(result.SourceLabel)) result.SourceLabel = source.Label;
        if (string.IsNullOrWhiteSpace(result.Query)) result.Query = query ?? "";
        if (string.IsNullOrWhiteSpace(result.ZipCode)) result.ZipCode = zip ?? "";
        if (result.RadiusMiles <= 0) result.RadiusMiles = radiusMiles;
        result.Items ??= [];

        if (!KnownStatuses.Contains(result.Status, StringComparer.Ordinal))
        {
            result.Status = "error";
            result.Error ??= $"{source.Label} answered in a shape this app doesn't recognise.";
        }

        // An "ok" with nothing in it is a real answer — an empty local market — and must not be
        // dressed up as a failure. It stays exactly as the source reported it.
        return result;
    }

    public static LocalSupplySearchResult Failure(
        ILocalSupplySource source, string status, string query, string zip, int radiusMiles,
        string error, bool retryable = false) => new()
    {
        SourceId = source.Id,
        SourceLabel = source.Label,
        Status = status,
        Query = query ?? "",
        ZipCode = zip ?? "",
        RadiusMiles = radiusMiles,
        Error = error,
        Retryable = retryable,
    };

    // The innermost message is the one that says something. "One or more errors occurred." is what
    // reaches the seller otherwise.
    private static string Describe(Exception ex)
    {
        var inner = ex is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.FirstOrDefault() ?? ex
            : ex;
        return string.IsNullOrWhiteSpace(inner.Message) ? inner.GetType().Name : inner.Message;
    }

    // The abandoned task still runs to completion somewhere. Observing its exception keeps a
    // faulted one from surfacing later as an unrelated unhandled task exception.
    private static void Abandon(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
}
