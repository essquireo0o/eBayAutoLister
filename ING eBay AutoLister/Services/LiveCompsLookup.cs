using System.Collections.Concurrent;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Fetches one model's recent sold prices from the OpenWebNinja API while the seller waits, and
/// files what it finds into the comps database.
/// </summary>
/// <remarks>
/// <para>
/// This replaced the browser scraper on 2026-08-14 (owner's decision: "forget about the other ebay
/// stuff scraper just use this"). The rewrite is not a tidy-up. The scraper needed Chrome, a
/// signed-in eBay profile and a person available to clear a bot check, so it could only ever run on
/// the owner's own PC — which is why <c>app.inglisting.com</c> had no live lookups at all — and by
/// the end it was returning a verification challenge for every single lookup. The one thing that
/// changes for every caller is where the rows come from: the run/stage/percent/rowsFound/outcome
/// protocol behind <c>/api/comps/live/start</c> and <c>/status</c> is deliberately identical, so
/// the progress bar, the "blocked versus empty" wording and every existing caller keep working
/// untouched.
/// </para>
/// <para>
/// <b>The outcomes are honest about the new source.</b> An API error is <c>error</c>. An empty
/// result is <c>empty</c> — the model genuinely has no recent sales, which is real information for
/// a pricing decision. <c>challenge</c> and <c>blocked</c> are gone, because there is no bot check
/// any more and a state that can never happen is a lie the UI would keep rendering.
/// </para>
/// <para>
/// <b>It still never fails the lookup.</b> Every refusal — no key configured, live lookups switched
/// off, today's allowance spent, this model already fetched within the day — comes back as an
/// already-finished run carrying an outcome, because the caller's next move is the same in all of
/// them: read the stored comps.
/// </para>
/// <para>
/// <b>What it does NOT do is decide whether a call may be made.</b> That is
/// <see cref="LiveCompsBudget"/>, and the order here matters: the cache is consulted before the
/// allowance, so a model that is already known costs neither a call nor a seller's daily quota.
/// </para>
/// </remarks>
public sealed class LiveCompsLookup(
    OpenWebNinjaClient api, LiveCompsStore store, LiveCompsBudget budget, ActionLog log)
{
    /// <summary>
    /// How long one lookup may run before it is abandoned. The API answers in two or three seconds;
    /// the old three-minute budget was the cost of driving a browser and is gone with it.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, LiveCompsRun> _runs = new();
    private readonly object _gate = new();
    private LiveCompsRun? _live;

    /// <summary>Whether a live lookup can be attempted at all on this deployment.</summary>
    /// <remarks>
    /// True on a server as well as on a desktop — which was never true of the scraper — provided a
    /// key is configured and the kill switch is on.
    /// </remarks>
    public bool IsAvailable => budget.Enabled && api.IsConfigured;

    /// <summary>The run for <paramref name="id"/>, or null once it has been forgotten.</summary>
    public LiveCompsRun? Get(string id) => _runs.TryGetValue(id, out var run) ? run : null;

    /// <summary>
    /// Starts a lookup for <paramref name="query"/>, or returns an already-finished run when there
    /// is a reason not to spend a call.
    /// </summary>
    /// <remarks>Never throws and never blocks.</remarks>
    public LiveCompsRun Start(string query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Finished("error", "Nothing to search for.");
        if (q.Length > 120) q = q[..120];

        if (!budget.Enabled)
            return Finished("unavailable",
                "Live sold-price lookups are switched off here, so this is priced from stored comps.");

        if (!api.IsConfigured)
            return Finished("unavailable",
                "Live sold-price lookups aren't set up on this deployment, so this is priced from stored comps.");

        // The saving that matters most, and it is checked before anything is spent — neither a call
        // nor the seller's own daily allowance. Sold history does not move in a day.
        if (budget.LastAnswer(q) is { } last && budget.Now - last.At < LiveCompsBudget.CacheFor)
        {
            var run = Finished("fresh",
                "Already fetched from eBay in the last 24 hours — pricing from those sold comps.");
            run.Query = q;
            // What the last fetch actually found, not how many rows still carry this exact search
            // term: a sale that was already in the database keeps the keyword it was first stored
            // under, so counting by keyword reports four comps for a model that has twenty-five.
            run.RowsFound = last.RowsFound > 0 ? last.RowsFound : store.StoredRowCount(q);
            return run;
        }

        LiveCompsRun started;
        long userId;

        lock (_gate)
        {
            // One at a time. Two lookups for the same model in flight is two calls off a finite
            // budget for one answer; joining the run already running costs nothing.
            if (_live is { Finished: false })
            {
                if (_live.Query.Equals(q, StringComparison.OrdinalIgnoreCase)) return _live;
                return Finished("busy", "Another live lookup is running — this is priced from stored comps.");
            }

            // Read while the request is still alive: on the hosted build this comes out of the
            // HttpContext, and the work below runs on a task that outlives the response.
            userId = budget.CurrentUserId;

            var allowance = budget.TryReserve();
            if (!allowance.Allowed)
                return Finished("rate_limited", RefusalMessage(allowance));

            started = new LiveCompsRun { Query = q, StartedAt = budget.Now };
            _live   = started;
        }

        _runs[started.Id] = started;
        Forget();
        _ = Task.Run(() => RunAsync(started, userId));
        return started;
    }

    private async Task RunAsync(LiveCompsRun run, long userId)
    {
        using var kill = new CancellationTokenSource(Timeout);
        var status = 0;

        try
        {
            run.Stage   = "Asking eBay for recent sold prices";
            run.Percent = 15;

            var fetch = await api.SearchSoldAsync(run.Query, page: 1, kill.Token);
            status = fetch.Status;

            if (!fetch.Ok)
            {
                run.Outcome = "error";
                run.Message = "The sold-price lookup couldn't be completed, so this is priced from "
                            + "stored comps instead.";
            }
            else if (fetch.Rows.Count == 0)
            {
                run.Outcome = "empty";
                run.Message = "eBay has no recent sold listings for this, which is worth knowing before "
                            + "you price it.";
            }
            else
            {
                run.Stage   = "Saving what eBay sent back";
                run.Percent = 70;

                var (stored, added) = store.Save(run.Query, fetch.Rows, budget.Now);

                run.RowsFound = fetch.Rows.Count;
                run.RowsNew   = added;
                run.Outcome   = "ok";
                run.Message   = $"{fetch.Rows.Count} recent sold listings fetched, {added} new to your database.";

                if (stored == 0)
                    run.Message += " (They could not be saved for next time — see the activity log.)";
            }
        }
        catch (OperationCanceledException)
        {
            run.Outcome = "timeout";
            run.Message = "The sold-price lookup took too long to answer, so this is priced from stored comps.";
        }
        catch (Exception ex)
        {
            run.Outcome = "error";
            run.Message = "The live sold-price lookup couldn't run, so this is priced from stored comps.";
            log.Add("Warning", "Live comps lookup failed", $"{run.Query}: {ex.Message}");
        }
        finally
        {
            run.Stage      = StageFor(run.Outcome);
            run.Percent    = 100;
            run.Finished   = true;
            run.FinishedAt = budget.Now;

            lock (_gate) { if (ReferenceEquals(_live, run)) _live = null; }

            // Every call, including the ones that failed: the API bills the attempt, so an audit
            // that only recorded successes would understate the spend it exists to account for.
            budget.Record(userId, run.Query, run.Outcome, run.RowsFound, run.RowsNew, status);

            log.Add(run.Outcome == "ok" ? "Info" : "Warning",
                $"Live comps lookup {run.Outcome}",
                $"\"{run.Query}\" — {run.RowsFound} row(s) found, {run.RowsNew} new to the database. "
                + "One API call spent.");
        }
    }

    /// <summary>
    /// What the seller reads when the bar finishes. Deliberately different sentences for "the
    /// lookup failed" and "this model has no recent sales" — the row count is identical in both and
    /// the conclusions are opposite.
    /// </summary>
    private static string StageFor(string outcome) => outcome switch
    {
        "ok"           => "Got fresh sold prices from eBay",
        "empty"        => "eBay has no recent sales for this",
        "fresh"        => "Using sold prices fetched earlier today",
        "timeout"      => "The sold-price lookup took too long",
        "error"        => "The sold-price lookup didn't answer",
        "rate_limited" => "Today's live lookups are used up",
        "busy"         => "Another live lookup is running",
        _              => "Using stored comps",
    };

    /// <summary>The sentence a seller who has run out reads. Says what, why, and when it comes back.</summary>
    private static string RefusalMessage(LiveCompsAllowance allowance)
    {
        if (!allowance.Enforced)
            return "Live sold-price lookups need you signed in — this is priced from stored comps.";

        var resets = allowance.ResetsAt.UtcDateTime.ToString("HH:mm 'UTC'",
            System.Globalization.CultureInfo.InvariantCulture);

        return $"That's today's {allowance.Limit} live sold-price lookups. The rest are priced from "
             + $"stored comps, and your next {allowance.Limit} arrive at {resets}.";
    }

    private LiveCompsRun Finished(string outcome, string message)
    {
        var run = new LiveCompsRun
        {
            StartedAt = budget.Now, FinishedAt = budget.Now,
            Finished = true, Percent = 100, Outcome = outcome, Message = message,
            Stage = StageFor(outcome),
        };
        _runs[run.Id] = run;
        return run;
    }

    /// <summary>Drops finished runs the UI has had ample time to read, so the map cannot grow forever.</summary>
    private void Forget()
    {
        if (_runs.Count < 64) return;
        var cutoff = budget.Now - TimeSpan.FromMinutes(15);
        foreach (var (id, run) in _runs)
            if (run.Finished && run.FinishedAt < cutoff) _runs.TryRemove(id, out _);
    }
}

/// <summary>One live comps lookup, readable while it runs.</summary>
/// <remarks>
/// Field-for-field what <c>CompsScrapeRun</c> was, because the UI polls this shape and the point of
/// the change is that only the source of the rows moved. It is a separate type rather than a reuse
/// so that the retired scraper and everything it drags with it can be deleted in one piece.
/// </remarks>
public sealed class LiveCompsRun
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string Query { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>What is happening, in words the seller can read. Drives the bar's caption.</summary>
    public string Stage { get; set; } = "Starting";

    /// <summary>0-100. Held below 100 until the run is actually over.</summary>
    public int Percent { get; set; }

    public int RowsFound { get; set; }
    public int RowsNew { get; set; }
    public bool Finished { get; set; }

    /// <summary>ok | empty | error | timeout | fresh | busy | rate_limited | unavailable.</summary>
    public string Outcome { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>
    /// Whether an empty result is the lookup's fault rather than the item's.
    /// </summary>
    /// <remarks>
    /// The panel needs this to choose between "no recent sales for this model" and "we could not
    /// find out", which are the same zero rows and opposite advice. It keeps the name the UI already
    /// reads; what changed is that eBay's bot check is no longer one of the reasons, because there
    /// is no browser left for eBay to challenge.
    /// </remarks>
    public bool SourceFailed => Outcome is "error" or "timeout";

    /// <summary>Seconds elapsed — lets the bar show real time rather than an invented estimate.</summary>
    public int ElapsedSeconds =>
        (int)((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds;
}
