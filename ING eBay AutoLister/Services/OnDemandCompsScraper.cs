using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Scrapes one model's sold comps from eBay while the seller waits, and files what it finds
/// into the comps database.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a daily background crawl that was retired on 2026-08-07. The crawl kept 2,306
/// keywords fresh on a clock whether or not anyone ever listed them, and that sustained volume is
/// what drew eBay's soft blocks — two captchas in two days, both immediately after high-volume
/// passes. Scraping only what the seller actually types is a far quieter traffic pattern, and it
/// deepens the database along the models that get listed instead of the ones somebody guessed at
/// in advance.
/// </para>
/// <para>
/// Every search is written to <c>SoldListings</c>. That is the point: the corpus grows on use, and
/// the second lookup of the same model is served from disk in milliseconds instead of costing
/// another minute and another hit on eBay.
/// </para>
/// <para>
/// <b>It runs in exactly two places, by the seller's instruction (2026-08-11): an opportunity
/// search, and pricing an item they are about to list.</b> Both are moments where money is being
/// decided on the number, which is what justifies making someone wait for it. Nothing else in the
/// app may trigger a scrape — not typing, not navigating, not a background refresh — because
/// volume is what drew eBay's blocks in the first place, and a scrape nobody asked for is pure
/// volume.
/// </para>
/// <para>
/// A scrape takes roughly a minute, so it cannot happen inside the request that asked for it — the
/// browser gives up long before eBay answers. The work is started once and polled, exactly like
/// <see cref="CopilotSeoJob"/>, so the panel can draw a progress bar instead of a spinner that
/// looks like a hang.
/// </para>
/// <para>
/// <b>This never fails the lookup.</b> A block, a bot check or a timeout ends the job with an
/// outcome and no rows, and the caller falls back to the stored comps. The one thing it must never
/// do is let "eBay isn't answering us" reach the seller as "this item doesn't sell" — those are
/// opposite facts and they used to render as the same empty panel.
/// </para>
/// </remarks>
public sealed class OnDemandCompsScraper(CredentialsStore credentials, ActionLog log)
{
    /// <summary>
    /// How long a scrape may run before it is killed. Two pages of 50 sold rows takes ~40-60s
    /// against a healthy Seller Hub, but a slow-but-working Seller Hub can take considerably
    /// longer, and killing it at two minutes threw away an answer that was about to arrive.
    /// </summary>
    /// <remarks>
    /// Three minutes is a deliberate choice by the seller (2026-08-11): a real sold-price answer
    /// is worth waiting for, because the two places this now runs — an opportunity search and
    /// pricing something you are about to list — are both decisions about money, and neither is
    /// somewhere you want a guess. The wait is only acceptable because the UI says out loud what
    /// it is doing and how long it may take; if that explanation is ever removed, this number has
    /// to come back down with it.
    /// </remarks>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Scrapes allowed per rolling hour. The whole reason the crawl was retired is that volume
    /// draws blocks, so on-demand must not quietly reintroduce it — a seller working through a
    /// pile of inventory could otherwise fire eighty scrapes in an afternoon and land in exactly
    /// the tarpit this design avoids. Over the cap the lookup still answers, from stored comps.
    /// </summary>
    public const int MaxPerHour = 12;

    /// <summary>
    /// A model looked up again within this window is not re-scraped. Sold history does not change
    /// meaningfully in an hour, and a seller comparing two similar units re-types near-identical
    /// queries constantly.
    /// </summary>
    public static readonly TimeSpan RecentlyScraped = TimeSpan.FromHours(1);

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, CompsScrapeRun> _runs = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastScraped = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DateTimeOffset> _starts = [];
    private CompsScrapeRun? _live;

    /// <summary>The BitData checkout holding the scraper, and the interpreter that can run it.</summary>
    /// <remarks>
    /// Configurable rather than hardcoded because the scraper lives outside this repo — it is the
    /// collector's own toolchain, with its own venv and its own Playwright install. When it is not
    /// present the app is not broken: <see cref="IsAvailable"/> goes false and every lookup serves
    /// stored comps, which is precisely the pre-existing behaviour.
    /// </remarks>
    public string ScraperDir => Trimmed(credentials.Get().CompsScraperDir, @"C:\Users\nsquires\source\repos\BitData");

    private string ScriptPath => Path.Combine(ScraperDir, "scrape_one_keyword.py");

    /// <summary>The venv interpreter if the checkout has one, else whatever <c>python</c> resolves to.</summary>
    private string PythonPath
    {
        get
        {
            var configured = Trimmed(credentials.Get().CompsScraperPython, "");
            if (configured.Length > 0) return configured;
            var venv = Path.Combine(ScraperDir, ".venv", "Scripts", "python.exe");
            return File.Exists(venv) ? venv : "python";
        }
    }

    /// <summary>Whether a live scrape can be attempted at all.</summary>
    public bool IsAvailable => File.Exists(ScriptPath);

    /// <summary>The run for <paramref name="id"/>, or null once it has been forgotten.</summary>
    public CompsScrapeRun? Get(string id) => _runs.TryGetValue(id, out var r) ? r : null;

    /// <summary>
    /// Starts a scrape for <paramref name="query"/>, or returns a run that is already finished when
    /// there is a reason not to scrape at all.
    /// </summary>
    /// <remarks>
    /// Never throws and never blocks. Every "no" — no scraper installed, over the hourly cap,
    /// scraped recently, another scrape already running — comes back as a completed run carrying a
    /// <see cref="CompsScrapeRun.Outcome"/> the caller can act on, because the caller's next move
    /// is the same in all of them: read the stored comps.
    /// </remarks>
    public CompsScrapeRun Start(string query)
    {
        var q = (query ?? "").Trim();
        if (q.Length == 0) return Finished("error", "Nothing to search for.");
        if (q.Length > 120) q = q[..120];

        if (!IsAvailable)
            return Finished("unavailable",
                "Live eBay lookups aren't set up on this machine, so this is priced from stored comps.");

        lock (_gate)
        {
            // One at a time, always. Two Playwright browsers on Seller Hub at once is the exact
            // pattern that got this machine's IP tarpitted while the crawl was running six workers.
            if (_live is { Finished: false })
            {
                return _live.Query.Equals(q, StringComparison.OrdinalIgnoreCase)
                    ? _live      // same model, already being fetched — join it rather than queue a twin
                    : Finished("busy", "Another live lookup is running — this is priced from stored comps.");
            }

            if (_lastScraped.TryGetValue(q, out var last) && DateTimeOffset.UtcNow - last < RecentlyScraped)
                return Finished("fresh", "Already fetched from eBay within the hour — using those comps.");

            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromHours(1);
            _starts.RemoveAll(t => t < cutoff);
            if (_starts.Count >= MaxPerHour)
                return Finished("rate_limited",
                    $"That's {MaxPerHour} live eBay lookups this hour — the rest are priced from stored "
                    + "comps so eBay doesn't start blocking us.");

            _starts.Add(DateTimeOffset.UtcNow);
            _live = new CompsScrapeRun { Query = q, StartedAt = DateTimeOffset.UtcNow };
        }

        var run = _live!;
        _runs[run.Id] = run;
        Forget();
        _ = Task.Run(() => RunAsync(run));
        return run;
    }

    private async Task RunAsync(CompsScrapeRun run)
    {
        using var kill = new CancellationTokenSource(Timeout);
        Process? proc = null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = PythonPath,
                WorkingDirectory       = ScraperDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            psi.ArgumentList.Add(ScriptPath);
            psi.ArgumentList.Add(run.Query);
            psi.ArgumentList.Add("--pages");
            psi.ArgumentList.Add("2");

            proc = Process.Start(psi)
                   ?? throw new InvalidOperationException("Python did not start.");

            // Drained rather than buffered: stdout is the progress protocol, and reading it to the
            // end first would mean the bar only moves once the scrape is already over.
            var stderr = proc.StandardError.ReadToEndAsync(kill.Token);

            while (await proc.StandardOutput.ReadLineAsync(kill.Token) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;   // borrowed helpers still log prose
                try { Apply(run, JsonDocument.Parse(line).RootElement); }
                catch (JsonException) { /* a half-written line is not worth failing a scrape over */ }
            }

            await proc.WaitForExitAsync(kill.Token);

            if (run.Outcome.Length == 0)
            {
                var err = (await stderr).Trim();
                run.Outcome = "error";
                run.Message = err.Length > 0
                    ? $"The lookup didn't finish: {err[..Math.Min(200, err.Length)]}"
                    : "The lookup didn't finish.";
            }
        }
        catch (OperationCanceledException)
        {
            run.Outcome = "timeout";
            run.Message = "eBay took too long to answer, so this is priced from stored comps instead.";
            TryKill(proc);
        }
        catch (Exception ex)
        {
            run.Outcome = "error";
            run.Message = "The live eBay lookup couldn't run, so this is priced from stored comps.";
            log.Add("Warning", "On-demand comps scrape failed", $"{run.Query}: {ex.Message}");
            TryKill(proc);
        }
        finally
        {
            run.Stage    = StageFor(run.Outcome);
            run.Percent  = 100;
            run.Finished = true;
            run.FinishedAt = DateTimeOffset.UtcNow;

            // Only a real answer counts as "recently scraped". Suppressing a retry after a block
            // would leave the seller unable to try again for an hour over a failure that is often
            // gone in minutes.
            if (run.Outcome is "ok" or "empty")
                _lastScraped[run.Query] = DateTimeOffset.UtcNow;

            lock (_gate) { if (ReferenceEquals(_live, run)) _live = null; }

            log.Add(run.Outcome == "ok" ? "Info" : "Warning",
                $"Live comps lookup {run.Outcome}",
                $"\"{run.Query}\" — {run.RowsFound} row(s) found, {run.RowsNew} new to the database.");
        }
    }

    /// <summary>Folds one progress line from the scraper into the run.</summary>
    private static void Apply(CompsScrapeRun run, JsonElement e)
    {
        if (e.TryGetProperty("stage", out var stage) && stage.ValueKind == JsonValueKind.String)
            run.Stage = stage.GetString() ?? run.Stage;

        if (e.TryGetProperty("pct", out var pct) && pct.TryGetInt32(out var p))
            // Never let the bar go backwards — a retried page re-reports a lower percentage, and a
            // bar that slides back reads as the job failing and restarting.
            run.Percent = Math.Clamp(Math.Max(run.Percent, p), 0, 99);

        if (e.TryGetProperty("rows", out var rows) && rows.TryGetInt32(out var r))
            run.RowsFound = r;

        if (!e.TryGetProperty("done", out var done) || !done.GetBoolean()) return;

        run.Outcome = e.TryGetProperty("outcome", out var o) ? o.GetString() ?? "error" : "error";
        if (e.TryGetProperty("rows", out var fr) && fr.TryGetInt32(out var frv)) run.RowsFound = frv;
        if (e.TryGetProperty("new", out var nw) && nw.TryGetInt32(out var nwv)) run.RowsNew = nwv;
        if (e.TryGetProperty("message", out var m)) run.Message = m.GetString() ?? "";
    }

    /// <summary>
    /// What the seller reads when the bar finishes. Deliberately different sentences for "eBay
    /// blocked us" and "this model has no recent sales" — the row count is identical and the
    /// conclusions are opposite.
    /// </summary>
    private static string StageFor(string outcome) => outcome switch
    {
        "ok"        => "Got fresh sold prices from eBay",
        "empty"     => "eBay has no recent sales for this",
        "blocked"   => "eBay isn't answering research requests",
        "challenge" => "eBay wants you to verify yourself",
        "timeout"   => "eBay took too long",
        _           => "Using stored comps",
    };

    private static void TryKill(Process? p)
    {
        try { if (p is { HasExited: false }) p.Kill(entireProcessTree: true); }
        catch { /* it was already gone, which is the outcome we wanted */ }
    }

    private CompsScrapeRun Finished(string outcome, string message)
    {
        var run = new CompsScrapeRun
        {
            StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow,
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
        var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(15);
        foreach (var (id, r) in _runs)
            if (r.Finished && r.FinishedAt < cutoff) _runs.TryRemove(id, out _);
    }

    private static string Trimmed(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

/// <summary>One live comps lookup, readable while it runs.</summary>
public sealed class CompsScrapeRun
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

    /// <summary>ok | empty | blocked | challenge | timeout | busy | fresh | rate_limited | unavailable | error.</summary>
    public string Outcome { get; set; } = "";

    public string Message { get; set; } = "";

    /// <summary>
    /// Whether the empty result is eBay's fault rather than the item's. The panel needs this to
    /// choose between "no recent sales for this model" and "eBay is blocking us right now", which
    /// are the same zero rows and opposite advice.
    /// </summary>
    public bool BlockedByEbay => Outcome is "blocked" or "challenge" or "timeout";

    /// <summary>Seconds elapsed — lets the bar show real time rather than an invented estimate.</summary>
    public int ElapsedSeconds =>
        (int)((FinishedAt ?? DateTimeOffset.UtcNow) - StartedAt).TotalSeconds;
}
