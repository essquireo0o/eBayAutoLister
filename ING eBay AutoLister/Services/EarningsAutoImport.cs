namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Keeps the earnings tracker current on its own: imports sold orders as soon as eBay is
/// connected, then refreshes on a slow schedule, so "Import eBay Sales" becomes something the
/// seller can press rather than something they must remember to.
///
/// Why this one CAN be automatic when the Facebook session import could not: that needed cookies
/// out of the seller's own Chrome, which Chromium seals and refuses to hand over. This is an
/// ordinary server-to-server Sell API call with an OAuth token the app already holds and already
/// refreshes before expiry. There is no browser in the path and nothing to steal focus.
///
/// Designed around one requirement above all: it must not fail noisily and repeatedly.
///   • Not connected is not a failure. It is skipped silently and re-checked, so the first import
///     happens on its own the moment the seller finishes the eBay login.
///   • A failure is logged ONCE per run of bad luck, not once per cycle, and backs off. A tracker
///     quietly a few hours stale is fine; a log full of the same red line is not.
///   • It never wipes seller-entered costs — see EarningsImportRunner for why that is structural
///     rather than a promise.
///   • One import at a time, and never on top of a manual one.
/// </summary>
public class EarningsAutoImport(
    IServiceScopeFactory scopes, CredentialsStore creds, ActionLog log) : BackgroundService
{
    /// <summary>How far back each automatic refresh looks. Wide enough that a few missed days heal themselves.</summary>
    private const int WindowDays = 90;

    /// <summary>Gap between successful refreshes. Sales don't arrive fast enough to justify more.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromHours(6);

    /// <summary>How often to look for a newly-connected eBay account while disconnected.</summary>
    private static readonly TimeSpan ConnectPoll = TimeSpan.FromSeconds(30);

    /// <summary>Backoff after a failure, so a broken token doesn't retry every 30 seconds.</summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromMinutes(30);

    /// <summary>Let the app finish starting before doing anything speculative.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(25);

    // Only one import may run at a time, and the manual endpoint takes the same gate — a scheduled
    // import landing on top of a hand-pressed one would have both writing the same rows.
    public static readonly SemaphoreSlim ImportGate = new(1, 1);

    public DateTimeOffset? LastRunUtc { get; private set; }
    public DateTimeOffset? LastSuccessUtc { get; private set; }
    public string? LastStatus { get; private set; }
    public string? LastMessage { get; private set; }
    public int LastLinesImported { get; private set; }

    private bool _wasConnected;
    private string? _announcedFailure;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await Wait(StartupDelay, stoppingToken)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = RefreshEvery;

            try
            {
                var connected = !string.IsNullOrWhiteSpace(creds.GetRefreshToken());

                if (!connected)
                {
                    // Silent on purpose: "you haven't connected eBay" is the app's normal state
                    // before setup, not something to report every half minute.
                    _wasConnected = false;
                    delay = ConnectPoll;
                }
                else
                {
                    // The literal "once the user logs into eBay" case: the first loop that sees a
                    // token imports immediately rather than waiting out a refresh interval.
                    var justConnected = !_wasConnected;
                    _wasConnected = true;

                    if (justConnected || LastSuccessUtc is null || DateTimeOffset.UtcNow - LastSuccessUtc >= RefreshEvery)
                        delay = await ImportOnceAsync(justConnected, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Automatic and unattended: a fault here must never take the app down.
                AnnounceFailureOnce("error", ex.Message);
                delay = AfterFailure;
            }

            if (!await Wait(delay, stoppingToken)) return;
        }
    }

    private async Task<TimeSpan> ImportOnceAsync(bool justConnected, CancellationToken ct)
    {
        // Don't queue behind a manual import — if the seller is already importing by hand, this
        // cycle has nothing to add. Try again on the next one.
        if (!await ImportGate.WaitAsync(TimeSpan.Zero, ct)) return ConnectPoll;

        try
        {
            using var scope = scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<EarningsImportRunner>();

            var result = await runner.RunAsync(WindowDays, ct);

            LastRunUtc  = DateTimeOffset.UtcNow;
            LastStatus  = result.Status;
            LastMessage = result.Message;

            if (!string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // "not_connected" between the token check and the call is a race, not a fault —
                // the next cycle picks it up without a word.
                if (!string.Equals(result.Status, "not_connected", StringComparison.OrdinalIgnoreCase))
                    AnnounceFailureOnce(result.Status, result.Message);
                return AfterFailure;
            }

            LastSuccessUtc    = DateTimeOffset.UtcNow;
            LastLinesImported = result.LinesImported;
            _announcedFailure = null;

            // Worth a line only when it did something, or when it is the first import after the
            // seller connected — otherwise this writes an identical entry every six hours forever.
            if (result.LinesAdded > 0 || justConnected)
                log.Add("Info", "eBay sales imported automatically",
                    $"{result.LinesImported} sold line(s) from {result.OrdersRead} order(s) — "
                    + $"{result.LinesAdded} new, {result.LinesUpdated} updated. Your typed costs were kept.");

            return RefreshEvery;
        }
        finally
        {
            ImportGate.Release();
        }
    }

    /// <summary>
    /// Logs a failure the first time it appears and stays quiet while it persists. The seller needs
    /// to be told once that their sales aren't updating — not reminded of it every cycle.
    /// </summary>
    private void AnnounceFailureOnce(string? status, string? message)
    {
        var key = $"{status}|{message}";
        if (_announcedFailure == key) return;
        _announcedFailure = key;

        LastStatus  = status;
        LastMessage = message;

        var detail = string.IsNullOrWhiteSpace(message) ? "No detail reported." : message;
        if (string.Equals(status, "reconnect", StringComparison.OrdinalIgnoreCase))
            log.Add("Warning", "Automatic eBay sales import needs you to reconnect",
                $"{detail} Your recorded sales are safe — reconnect eBay in Settings and it resumes on its own.");
        else
            log.Add("Warning", "Automatic eBay sales import didn't run",
                $"{detail} It will keep trying; your recorded sales are unaffected.");
    }

    private static async Task<bool> Wait(TimeSpan delay, CancellationToken ct)
    {
        try { await Task.Delay(delay, ct); return true; }
        catch (OperationCanceledException) { return false; }
    }
}
