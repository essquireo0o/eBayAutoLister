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

    // ── Is it actually answering? ────────────────────────────────────────────────────────────
    //
    // IsAvailable only ever meant "a key is configured and the kill switch is on", and that stayed
    // true for the three days from 2026-08-20 while every single call came back
    //
    //     HTTP 503 {"error":{"message":"eBay requires an authenticated session ..."}}
    //
    // Nothing above it knew. Each product spent an API call into the outage, got nothing, and the
    // board said "no sold data" — which reads as "this product has never sold" rather than "the
    // price source is down", and those send a seller to do completely different things. Worse, a
    // 120-product scan spent 120 calls out of a monthly allowance to learn the same thing 120
    // times.
    //
    // So the outcome of every call is remembered. After a few consecutive failures the source is
    // called down: the scan says so in words, and stops spending the allowance on it until a
    // cooldown has passed and one probe is worth trying again. Any success clears it instantly —
    // this is a circuit breaker, not a kill switch, and it must never be the reason a working
    // source stays unused.
    private int _consecutiveFailures;
    private string _lastFailure = "";
    private DateTimeOffset? _lastFailureAt;
    private DateTimeOffset? _lastSuccessAt;

    /// <summary>Consecutive failures before the source is reported as down.</summary>
    /// <remarks>
    /// Three, not one: a single timeout is an ordinary bad minute on any network, and calling the
    /// source down for that would be its own kind of lie.
    /// </remarks>
    public const int FailuresBeforeDown = 3;

    /// <summary>How long a source stays written off before one call is spent checking again.</summary>
    public static readonly TimeSpan RetryAfterDown = TimeSpan.FromMinutes(10);

    /// <summary>What the live sold-price source is doing, in the words a board can print.</summary>
    public LiveCompsHealth Health
    {
        get
        {
            var down = _consecutiveFailures >= FailuresBeforeDown;
            var retryDue = down && (_lastFailureAt is not { } at || budget.Now - at >= RetryAfterDown);
            return new LiveCompsHealth(
                Configured: IsAvailable,
                Answering: !down,
                ConsecutiveFailures: _consecutiveFailures,
                LastError: _lastFailure,
                LastSuccessAt: _lastSuccessAt,
                RetryDue: retryDue);
        }
    }

    /// <summary>
    /// Whether spending a call is worth it right now. False only while a source that has failed
    /// repeatedly is inside its cooldown.
    /// </summary>
    public bool ShouldAttempt
    {
        get { var h = Health; return h.Configured && (h.Answering || h.RetryDue); }
    }

    private void RecordOutcome(string outcome, string detail)
    {
        // "empty" is an answer, not a failure: eBay saying it has no recent sales for a thing is
        // exactly the fact the lookup was spent to learn.
        if (outcome is "ok" or "empty")
        {
            var wasDown = _consecutiveFailures >= FailuresBeforeDown;
            _consecutiveFailures = 0;
            _lastFailure = "";
            _lastSuccessAt = budget.Now;
            if (wasDown) log.Add("Info", "Live sold-comps source is answering again", detail);
            return;
        }

        _consecutiveFailures++;
        _lastFailure = detail;
        _lastFailureAt = budget.Now;
        if (_consecutiveFailures == FailuresBeforeDown)
            log.Add("Warning", "Live sold-comps source called down",
                $"{FailuresBeforeDown} calls in a row failed. Pricing falls back to the stored comps "
                + $"database until one probe is retried in {RetryAfterDown.TotalMinutes:0} minutes. Last: {detail}");
    }

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

    /// <summary>
    /// Starts a lookup for <paramref name="query"/> and waits for it to finish, the way the
    /// browser's poll loop does — for callers that price on the server and cannot poll.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a scan prices its rows against the SAME stored-plus-live path the browser
    /// gives a single row: <see cref="Start"/> files the fresh sold rows into the comps database,
    /// and the caller re-reads that database once this returns. Facebook Marketplace rows — a
    /// whole feed of them, with no single search term the browser could look up first — were the
    /// rows that never got the live half, because nothing server-side could wait for it.
    /// </para>
    /// <para>
    /// Never throws for a lookup reason: every refusal comes back as the finished run it already
    /// is. A refusal returns immediately; a real call returns when the API has answered or the
    /// run's own <see cref="Timeout"/> has ended it. Only the caller's own cancellation propagates.
    /// </para>
    /// </remarks>
    public async Task<LiveCompsRun> FetchAsync(string query, CancellationToken ct = default)
    {
        var run = Start(query);
        // A little past the run's own timeout, which is what actually ends a slow call — this wait
        // only exists so a run that somehow never flips Finished cannot hold a scan open forever.
        var deadline = DateTimeOffset.UtcNow + Timeout + TimeSpan.FromSeconds(10);
        while (!run.Finished && DateTimeOffset.UtcNow < deadline)
            await Task.Delay(200, ct).ConfigureAwait(false);
        return run;
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
            RecordOutcome(run.Outcome, status > 0 ? $"HTTP {status} for \"{run.Query}\"" : run.Message);
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
/// <summary>
/// What the live sold-price source is doing — for a board that has to explain its own numbers.
/// </summary>
/// <param name="Configured">A key is present and the kill switch is on.</param>
/// <param name="Answering">Recent calls have worked. False after several consecutive failures.</param>
/// <param name="LastError">The most recent failure, as the API described it.</param>
/// <param name="LastSuccessAt">When it last answered, or null if it has not answered this run.</param>
/// <param name="RetryDue">The cooldown has passed and one call is worth spending to check.</param>
public sealed record LiveCompsHealth(
    bool Configured, bool Answering, int ConsecutiveFailures,
    string LastError, DateTimeOffset? LastSuccessAt, bool RetryDue)
{
    /// <summary>The sentence a board puts on screen, or empty when there is nothing to say.</summary>
    public string Note =>
        !Configured
            ? "Live sold-price lookups aren't set up here, so everything is priced from stored comps."
            : Answering
                ? ""
                : "Live sold prices are unavailable right now — eBay's sold-listing source is refusing "
                  + "calls — so everything below is priced from the stored comps database and AI estimates. "
                  + "This is a fault at the source, not a product with no sales history.";
}

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
