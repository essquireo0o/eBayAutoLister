using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One saved search, expressed the way <c>/api/local/arbitrage</c> takes it. The seam between the
/// radar and the scan pipeline.
/// </summary>
public sealed record LocalArbitrageScanRequest(
    string Query, string Zip, int RadiusMiles, string Sources, string? CraigslistSite,
    string CategoryId, int MaxItems, int TerapeakBudget, bool Coupons);

/// <summary>
/// Runs the local-arbitrage pipeline. A delegate rather than an interface because the pipeline
/// itself lives in <c>Program.cs</c> as <c>FindLocalArbitrageAsync</c>, wired to fourteen singleton
/// services — and the alternative to this one line of indirection is either lifting that whole
/// orchestration into a class or teaching the radar to price things a second way. The second one is
/// how a background alert ends up quoting a different profit from the board it links to.
/// </summary>
public delegate Task<LocalArbitrageResult> LocalArbitrageScan(
    LocalArbitrageScanRequest request, CancellationToken ct);

/// <summary>What one run of one watch did, in the shape the card and the log both want.</summary>
public sealed record RadarRunResult(string Status, string Note, IReadOnlyList<DealAlert> Alerts, int Scanned)
{
    public static RadarRunResult Empty(string status, string note) => new(status, note, [], 0);
}

/// <summary>
/// The loop. Wakes once a minute, runs at most one due watch, and puts what clears the bar on the
/// desktop.
/// </summary>
/// <remarks>
/// <para><b>The scraping posture, restated as code.</b> Everything this reads belongs to somebody
/// else, and the only difference between a seller who checks craigslist a few times a day and a
/// crawler is rate and volume. So:</para>
/// <list type="bullet">
///   <item><b>One scan at a time, process-wide</b> — the gate is a semaphore of one, and a manual
///     "Scan now" takes the same gate. Six watches never become six concurrent sessions.</item>
///   <item><b>One watch per tick</b>, most overdue first, with a five-minute floor between any two
///     scans — see <see cref="DealRadarClock"/>.</item>
///   <item><b>No Terapeak scrapes unattended.</b> A Terapeak lookup drives a real logged-in browser
///     session; doing that on a timer while nobody is at the keyboard is a different thing from
///     doing it because someone pressed a button. Background runs are cache-only
///     (<c>terapeakBudget: 0</c>); a manual run gets a small budget, because a person is there.</item>
///   <item><b>Craigslist unless told otherwise.</b> A watch with no source list reads the public
///     site, not the login-gated one. Facebook is opt-in per watch and simply reports
///     <c>not_connected</c> when there is no session — it is never logged into on a schedule.</item>
///   <item><b>Off until switched on.</b> The master setting ships disabled.</item>
/// </list>
/// <para>Nothing here retries. A site that just refused us is not a site to ask again in sixty
/// seconds; the watch reports what happened and waits for its next slot like any other.</para>
/// </remarks>
public sealed class DealRadarService(
    DealRadarStore store,
    DesktopNotifier notifier,
    LocalArbitrageScan scan,
    ActionLog log) : BackgroundService
{
    /// <summary>How long after startup before the first scan. The app has a browser to open first.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);

    /// <summary>A hard ceiling on one run, so a hung source can't jam the loop for the afternoon.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(6);

    /// <summary>Listings priced per run. Lower than the board's 30 — this runs unattended, repeatedly.</summary>
    public const int MaxItemsPerScan = 25;

    /// <summary>Terapeak scrapes a manual "Scan now" may spend. Background runs get none. See remarks.</summary>
    public const int ManualTerapeakBudget = 3;

    /// <summary>Balloons one run may raise before it summarises instead. Five in five seconds is noise.</summary>
    public const int MaxNotificationsPerRun = 2;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>True while a scan is actually in flight — surfaced so the UI can say which watch.</summary>
    public bool Scanning { get; private set; }
    public long? ScanningWatchId { get; private set; }
    public DateTimeOffset? LastScanUtc { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(DateTimeOffset.UtcNow, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // The loop outlives everything it calls. A watch whose scan blew up has already been
                // recorded as an error against that watch; anything reaching here is the scheduler
                // itself, and stopping the radar over it would silently end the feature.
                log.Add("Error", "Deal Radar tick failed", ex.Message);
            }

            try { await Task.Delay(TickInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass of the scheduler. Separate from the loop so a test can drive it by hand.</summary>
    public async Task<RadarRunResult?> TickAsync(DateTimeOffset now, CancellationToken ct)
    {
        var settings = store.GetSettings();
        if (!settings.Enabled) return null;

        var due = DealRadarClock.NextDueWatch(store.ListWatches(), now, LastScanUtc);
        if (due is null) return null;

        return await RunWatchAsync(due.Id, manual: false, ct);
    }

    /// <summary>
    /// Scans one watch and files what it found. Shared by the timer and by the Scan-now button, so
    /// there is exactly one path from a saved search to an alert.
    /// </summary>
    /// <param name="manual">
    /// True when a person is waiting. The only two differences: a small Terapeak budget, and finds
    /// that don't pop a balloon — the seller is looking at the screen the results land on.
    /// </param>
    public async Task<RadarRunResult> RunWatchAsync(long watchId, bool manual, CancellationToken ct)
    {
        var watch = store.GetWatch(watchId);
        if (watch is null) return RadarRunResult.Empty(RadarRunStatuses.Error, "That watch no longer exists.");

        // Not queued: a "Scan now" pressed while the timer is mid-sweep of another watch is answered
        // straight away with what is happening, rather than blocking the request for six minutes.
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
        {
            return RadarRunResult.Empty(watch.LastStatus,
                "Another scan is running right now — this one will go next. Only one site is read at a time.");
        }

        var startedUtc = DateTimeOffset.UtcNow;
        Scanning = true;
        ScanningWatchId = watchId;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RunTimeout);

            var result = await scan(Request(watch, manual), timeout.Token);
            var run = Interpret(watch, result, startedUtc);

            // The store has the last word on what is new: it holds the memory, and a find it has
            // seen before is not one to report or count. Restating the run from what it accepted
            // keeps the card's sentence and the feed from ever disagreeing.
            var stored = store.AddAlerts(run.Alerts);
            if (run.Status == RadarRunStatuses.Ok && stored.Count != run.Alerts.Count)
            {
                run = stored.Count > 0
                    ? run with { Note = $"{stored.Count} new deal{(stored.Count == 1 ? "" : "s")} cleared your bar." }
                    : run with { Status = RadarRunStatuses.NoMatches, Note = "Nothing new — every match was one you'd already been shown." };
            }

            store.RecordRun(
                watch.Id, run.Status, run.Note, run.Scanned, stored.Count,
                stored.Sum(a => a.NetProfit ?? 0m), startedUtc);
            store.Prune(startedUtc);

            if (stored.Count > 0)
            {
                log.Add("Deal Radar", $"{watch.Name}: {stored.Count} new deal{(stored.Count == 1 ? "" : "s")}",
                    DealRadarMatcher.SummaryHeadline(stored));
                Announce(watch, stored, manual, startedUtc);
            }

            return run with { Alerts = stored };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The app is shutting down, or the browser hung up on a manual run. Not a watch failure:
            // recording it as one would leave a red card on a scan that was never allowed to finish.
            throw;
        }
        catch (OperationCanceledException)
        {
            var note = $"The scan was still running after {(int)RunTimeout.TotalMinutes} minutes and was stopped.";
            store.RecordRun(watch.Id, RadarRunStatuses.Error, note, 0, 0, 0m, startedUtc);
            return RadarRunResult.Empty(RadarRunStatuses.Error, note);
        }
        catch (Exception ex)
        {
            var note = $"The scan couldn't be completed: {ex.Message}";
            log.Add("Error", $"Deal Radar scan failed: {watch.Name}", ex.Message);
            store.RecordRun(watch.Id, RadarRunStatuses.Error, note, 0, 0, 0m, startedUtc);
            return RadarRunResult.Empty(RadarRunStatuses.Error, note);
        }
        finally
        {
            Scanning = false;
            ScanningWatchId = null;
            LastScanUtc = DateTimeOffset.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>The watch, as the scan pipeline's parameters. See the class remarks for the two rations.</summary>
    private static LocalArbitrageScanRequest Request(DealWatch watch, bool manual) => new(
        Query: watch.Query,
        Zip: watch.ZipCode,
        RadiusMiles: watch.RadiusMiles,
        Sources: EffectiveSources(watch),
        CraigslistSite: watch.CraigslistSite is { Length: > 0 } site ? site : null,
        CategoryId: watch.CategoryId,
        MaxItems: MaxItemsPerScan,
        TerapeakBudget: manual ? ManualTerapeakBudget : 0,
        // The buy-side codes only apply to retail rows and cost one lookup per store. Worth it on a
        // manual run the seller is watching; not worth extra requests on a timer.
        Coupons: manual);

    /// <summary>
    /// A watch that named no sources reads the public site. Never "everything available", which
    /// would quietly enrol a connected Facebook session into an unattended schedule.
    /// </summary>
    public static string EffectiveSources(DealWatch watch) =>
        string.IsNullOrWhiteSpace(watch.Sources) ? CraigslistParser.SourceId : watch.Sources.Trim();

    /// <summary>
    /// Turns a scan into a run: what cleared the bar, and — when nothing did — which of the several
    /// quite different reasons that was.
    /// </summary>
    private RadarRunResult Interpret(DealWatch watch, LocalArbitrageResult result, DateTimeOffset now)
    {
        if (result is null) return RadarRunResult.Empty(RadarRunStatuses.Error, "The scan returned nothing at all.");

        // A source that couldn't be reached is not an empty market, and the card must not say
        // "0 deals" when what happened is that Facebook's session expired.
        if (result.Status != "ok")
        {
            var reason = result.Error
                ?? result.Sources.FirstOrDefault(s => s.Status != "ok")?.Error
                ?? "The sites this watch reads couldn't be searched.";
            return new RadarRunResult(RadarRunStatuses.Error, reason, [], result.LocalListingsFound);
        }

        // Matched twice, deliberately: once against the bar alone and once against the memory. The
        // difference between the two is the most common outcome of a healthy watch — a classified
        // sits on the site for a fortnight, so every scan after the first re-finds the deals it
        // already reported. Telling the seller "nothing cleared your bar" then would be false, and
        // would send them to lower a threshold that is working perfectly.
        var seen = store.SeenKeys(watch.Id);
        var qualified = DealRadarMatcher.Match(watch, result, now);
        var alerts = qualified.Where(a => !seen.Contains(a.ItemKey)).ToList();

        if (alerts.Count > 0)
        {
            return new RadarRunResult(RadarRunStatuses.Ok,
                $"{alerts.Count} new deal{(alerts.Count == 1 ? "" : "s")} cleared your bar.",
                alerts, result.LocalListingsFound);
        }

        if (qualified.Count > 0)
        {
            return new RadarRunResult(RadarRunStatuses.NoMatches,
                $"{result.LocalListingsFound} listings — {qualified.Count} still clearing your bar, " +
                "all of them ones you've already been shown.", [], result.LocalListingsFound);
        }

        // Nothing qualified at all, which has three quite different meanings. Saying which one is
        // the difference between a seller adjusting a threshold and a seller assuming it is broken.
        var note = result.LocalListingsFound == 0
            ? "Nothing was listed near you this time."
            : result.Items.Count(r => r.NetProfit is > 0) == 0
                ? $"{result.LocalListingsFound} listings, none of them profitable after fees."
                : $"{result.LocalListingsFound} listings — some profitable, none clearing " +
                  $"{watch.MinNetProfit:C0} and {watch.MinRoiPercent:0}% on evidence this app stands behind.";

        return new RadarRunResult(RadarRunStatuses.NoMatches, note, [], result.LocalListingsFound);
    }

    /// <summary>
    /// Puts the find on the desktop, unless it shouldn't be. Four separate reasons it shouldn't, and
    /// every one of them still leaves the alert in the feed.
    /// </summary>
    private void Announce(DealWatch watch, IReadOnlyList<DealAlert> alerts, bool manual, DateTimeOffset now)
    {
        var settings = store.GetSettings();

        // Someone is looking at the screen these just landed on.
        if (manual) return;
        if (!settings.DesktopNotifications || !watch.NotifyDesktop) return;
        // Quiet hours suppress the pop, never the scan: the whole promise is that the app works
        // overnight, and the finds are in the feed at breakfast either way. Local time, deliberately —
        // "11pm" means the seller's 11pm.
        if (DealRadarClock.IsQuiet(settings, now.ToLocalTime())) return;

        // One balloon per find up to the cap, then one that summarises — because a person who gets
        // five notifications in five seconds learns to dismiss them without reading.
        var announced = alerts.Count <= MaxNotificationsPerRun
            ? alerts.Select(a => new DesktopNotification(
                DealRadarMatcher.NotificationTitle(watch, 1), a.Headline, a.Id)).ToList()
            : [new DesktopNotification(
                DealRadarMatcher.NotificationTitle(watch, alerts.Count),
                DealRadarMatcher.SummaryHeadline(alerts),
                alerts.OrderByDescending(a => a.NetProfit ?? 0m).First().Id)];

        foreach (var notification in announced)
        {
            if (!notifier.Send(notification)) continue;
            // Only ticked when a channel actually took it. With no tray attached this stays false and
            // the page's own Notification API is what reaches the seller — see DesktopNotifier.
            store.SetAlertFlag(notification.AlertId, "notified", true);
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}
