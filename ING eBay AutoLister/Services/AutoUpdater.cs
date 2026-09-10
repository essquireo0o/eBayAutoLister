using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Installs a newer release on its own, so a seller never has to go and fetch one.
/// </summary>
/// <remarks>
/// <para>
/// Before this, an update was a banner with a link: download the installer, run it, click through.
/// The owner asked for it to happen by itself. The pieces that make that safe:
/// </para>
/// <list type="number">
///   <item><b>It only installs what it can verify.</b> The installer is fetched from the GitHub
///     release and its SHA-256 has to equal the digest the release publishes for that asset.
///     A byte off and the file is thrown away. A release with no digest is never installed
///     automatically — the banner still offers the download page.</item>
///   <item><b>It only installs when the seller is not in the middle of something.</b> Anything
///     that changes state (a POST, a PUT) or opens the app counts as activity; polling from an open
///     tab does not. After twenty quiet minutes with nothing in flight, it goes ahead. "Install now"
///     from the banner skips the wait.</item>
///   <item><b>It never nags.</b> The install is per-machine, so Windows asks for permission once
///     (msiexec's own prompt — the app itself never elevates). Refuse it three times and the
///     automatic attempts stop for that version; the banner keeps the button.</item>
///   <item><b>Only the installed copy does this.</b> A build running from a source tree would
///     otherwise install over Program Files on a developer's machine every time a release went out.</item>
/// </list>
/// <para>
/// The installer stops this process itself (it terminates AutoListerB1.exe before touching its
/// files — see installer.wxs) and, because a passive install has no finish screen, it is also the
/// installer that starts the new version: the AUTOUPDATE=1 property passed here turns that on.
/// So a successful install is one this code never sees the end of. What it sees is the next start:
/// the pending note it wrote is read back, and if the version now running is the one it named, the
/// update is reported as done and the installer file is removed.
/// </para>
/// </remarks>
public sealed class AutoUpdater(UpdateChecker updates, IHttpClientFactory httpFactory, ActionLog log) : BackgroundService
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan Every = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(20);
    public const int MaxAutomaticAttempts = 3;

    public static string UpdatesDir => Path.Combine(AppPaths.DataHome, "updates");
    public static string PendingPath => Path.Combine(UpdatesDir, "pending.json");
    public static string InstallLogPath => Path.Combine(UpdatesDir, "install.log");
    public static string InstallerPathFor(string version) => Path.Combine(UpdatesDir, $"ING-AutoLister-Setup-{version}.msi");

    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly object _lock = new();
    private long _inFlight;
    private DateTimeOffset _lastActivity = DateTimeOffset.UtcNow;
    private Task? _userInstall;

    public UpdatePhase Phase { get; private set; } = UpdatePhase.Idle;
    /// <summary>The version the phase is about — the newest release, once one is known.</summary>
    public string? Version { get; private set; }
    public string? Detail { get; private set; }

    /// <summary>True when this process is the copy the installer put in Program Files.</summary>
    public bool AutomaticUpdatesOn { get; } = IsInstalledCopy(Environment.ProcessPath, ProgramFilesRoots());

    // ── What counts as "the seller is using it" ───────────────────────────────────────────────

    /// <summary>Wraps one HTTP request: counts it as in flight, and as activity when it is one.</summary>
    public IDisposable TrackRequest(string method, string path)
    {
        Interlocked.Increment(ref _inFlight);
        if (CountsAsActivity(method, path)) _lastActivity = DateTimeOffset.UtcNow;
        return new RequestScope(this);
    }

    /// <summary>
    /// Writes are activity; so is opening the app. Reads are not: an open tab polls the backend
    /// every few seconds on its own, and treating that as the seller being busy would mean an
    /// update that never installs on a PC where the tab is simply left open.
    /// </summary>
    public static bool CountsAsActivity(string method, string path)
    {
        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
            return true;
        return path == "/" || string.Equals(path, "/index.html", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsIdle(DateTimeOffset now) =>
        Interlocked.Read(ref _inFlight) == 0 && now - _lastActivity >= IdleAfter;

    public static bool IsInstalledCopy(string? processPath, IEnumerable<string> programFilesRoots)
    {
        if (string.IsNullOrEmpty(processPath)) return false;
        foreach (var root in programFilesRoots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (processPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static IEnumerable<string> ProgramFilesRoots() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
    ];

    public UpdateSnapshot Snapshot() =>
        new(Phase.ToString(), Version, Detail, AutomaticUpdatesOn, UpdateChecker.CurrentVersion);

    // ── The loop ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The banner's "Install now": no waiting for idle, no attempt cap.</summary>
    public Task RequestInstallAsync()
    {
        lock (_lock)
        {
            if (_userInstall is { IsCompleted: false }) return _userInstall;
            _userInstall = Task.Run(() => TickAsync(userAsked: true, CancellationToken.None));
            return _userInstall;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try { ReportPreviousAttempt(); } catch { /* a bad pending note must not stop the app */ }

        try { await Task.Delay(FirstCheckDelay, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(userAsked: false, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Set(UpdatePhase.Failed, Version, ex.Message);
                log.Add("Info", "Automatic update skipped", ex.Message);
            }
            try { await Task.Delay(Every, ct); } catch (OperationCanceledException) { return; }
        }
    }

    internal async Task TickAsync(bool userAsked, CancellationToken ct)
    {
        await _tick.WaitAsync(ct);
        try
        {
            if (Phase == UpdatePhase.Installing) return;

            var status = await updates.CheckAsync(force: userAsked, ct);
            if (!status.UpdateAvailable || status.Latest is null)
            {
                Set(UpdatePhase.Idle, status.Latest, null);
                DiscardInstallersExcept(null);
                return;
            }

            if (status.InstallerUrl is null || status.InstallerSha256 is null)
            {
                Set(UpdatePhase.Failed, status.Latest,
                    $"The release has no installer with a checksum to verify against, so it will not install itself. Get it from {status.DownloadUrl}");
                return;
            }

            var msi = await EnsureDownloadedAsync(status, ct);
            if (msi is null) return;

            var pending = ReadPending();
            var attempts = pending?.Version == status.Latest ? pending.Attempts : 0;

            if (!userAsked)
            {
                if (!AutomaticUpdatesOn)
                {
                    Set(UpdatePhase.Ready, status.Latest, "Downloaded. This copy is not the installed one, so it does not install itself.");
                    return;
                }
                if (attempts >= MaxAutomaticAttempts)
                {
                    Set(UpdatePhase.Ready, status.Latest, "Downloaded. Windows was asked for permission three times without getting it, so it is waiting for you.");
                    return;
                }
                if (!IsIdle(DateTimeOffset.UtcNow))
                {
                    Set(UpdatePhase.Ready, status.Latest, "Downloaded. It installs itself once the app has been left alone for 20 minutes.");
                    return;
                }
            }

            await InstallAsync(msi, status.Latest, attempts + 1, ct);
        }
        finally
        {
            _tick.Release();
        }
    }

    private async Task<string?> EnsureDownloadedAsync(UpdateStatus s, CancellationToken ct)
    {
        Directory.CreateDirectory(UpdatesDir);
        var final = InstallerPathFor(s.Latest!);
        if (File.Exists(final) && await Sha256Async(final, ct) == s.InstallerSha256) return final;

        DiscardInstallersExcept(s.Latest);
        Set(UpdatePhase.Downloading, s.Latest, null);
        var part = final + ".part";
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.Add("User-Agent", $"ING-Listing-Engine/{UpdateChecker.CurrentVersion}");

            using var res = await client.GetAsync(s.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            res.EnsureSuccessStatusCode();
            await using (var src = await res.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(part))
                await src.CopyToAsync(dst, ct);

            var sha = await Sha256Async(part, ct);
            if (!string.Equals(sha, s.InstallerSha256, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(part);
                Set(UpdatePhase.Failed, s.Latest, "The downloaded installer did not match the release's checksum, so it was thrown away.");
                log.Add("Warning", "Update download rejected", $"sha256 {sha} is not the release's {s.InstallerSha256}.");
                return null;
            }

            File.Move(part, final, overwrite: true);
            log.Add("Info", "Update downloaded", $"{s.Latest} verified, {new FileInfo(final).Length / 1048576} MB.");
            return final;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            try { File.Delete(part); } catch { }
            Set(UpdatePhase.Failed, s.Latest, "Download failed: " + ex.Message);
            log.Add("Info", "Update download failed", ex.Message);
            return null;
        }
    }

    private async Task InstallAsync(string msi, string version, int attempt, CancellationToken ct)
    {
        WritePending(new PendingInstall(version, attempt, DateTimeOffset.UtcNow));
        Set(UpdatePhase.Installing, version, "Windows is asking for permission to install it. The app restarts by itself when it is done.");
        log.Add("Info", "Installing update", $"{version}, attempt {attempt}. The app restarts when the installer finishes.");

        // Plain msiexec, not elevated by us: a per-machine package makes msiexec show Windows' own
        // permission prompt, and the client side stays the seller's normal, unelevated session —
        // which is what the installer's relaunch of the app then inherits.
        var psi = new ProcessStartInfo("msiexec.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/i");
        psi.ArgumentList.Add(msi);
        psi.ArgumentList.Add("/passive");
        psi.ArgumentList.Add("/norestart");
        psi.ArgumentList.Add("/l*v");
        psi.ArgumentList.Add(InstallLogPath);
        psi.ArgumentList.Add("AUTOUPDATE=1");

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("msiexec did not start.");

        // When the install goes ahead, the installer stops this process and this line is never
        // reached. Reaching it means msiexec finished without replacing us.
        await proc.WaitForExitAsync(ct);
        var (phase, why) = proc.ExitCode switch
        {
            1602 => (UpdatePhase.NeedsPermission, "Windows asked for permission and was told no."),
            1625 => (UpdatePhase.Failed, "A policy on this PC blocks installing it. An administrator can run the installer from the download page."),
            0 or 3010 => (UpdatePhase.Failed, "The installer finished but this copy is still running; restart the app to pick up the new version."),
            var code => (UpdatePhase.Failed, $"The installer stopped with code {code}. Details are in {InstallLogPath}."),
        };
        Set(phase, version, why);
        log.Add("Info", "Update did not install", why);
    }

    // ── The note left for the next start ──────────────────────────────────────────────────────

    private void ReportPreviousAttempt()
    {
        var pending = ReadPending();
        if (pending is null) return;

        var current = UpdateChecker.CurrentVersion;
        if (!UpdateChecker.IsNewer(pending.Version, current))
        {
            log.Add("Info", "Updated", $"Now running {current}.");
            DeletePending();
            DiscardInstallersExcept(null);
        }
        else if (pending.Attempts >= MaxAutomaticAttempts)
        {
            log.Add("Info", "Update waiting", $"{pending.Version} is downloaded; Windows was asked for permission {pending.Attempts} times. Use Install now on the banner.");
        }
    }

    private static PendingInstall? ReadPending()
    {
        try
        {
            return File.Exists(PendingPath)
                ? JsonSerializer.Deserialize<PendingInstall>(File.ReadAllText(PendingPath))
                : null;
        }
        catch { return null; }
    }

    private static void WritePending(PendingInstall p)
    {
        Directory.CreateDirectory(UpdatesDir);
        File.WriteAllText(PendingPath, JsonSerializer.Serialize(p));
    }

    private static void DeletePending()
    {
        try { File.Delete(PendingPath); } catch { }
    }

    /// <summary>Removes downloaded installers other than the one for <paramref name="keepVersion"/>.</summary>
    private static void DiscardInstallersExcept(string? keepVersion)
    {
        if (!Directory.Exists(UpdatesDir)) return;
        var keep = keepVersion is null ? null : InstallerPathFor(keepVersion);
        foreach (var file in Directory.EnumerateFiles(UpdatesDir, "ING-AutoLister-Setup-*.msi*"))
        {
            if (keep is not null && string.Equals(file, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(file); } catch { }
        }
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void Set(UpdatePhase phase, string? version, string? detail)
    {
        Phase = phase;
        Version = version;
        Detail = detail;
    }

    private sealed class RequestScope(AutoUpdater owner) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) Interlocked.Decrement(ref owner._inFlight);
        }
    }

    private sealed record PendingInstall(string Version, int Attempts, DateTimeOffset StartedAt);
}

public enum UpdatePhase
{
    /// <summary>Nothing newer is known.</summary>
    Idle,
    Downloading,
    /// <summary>Verified and on disk, waiting for a quiet moment or for the button.</summary>
    Ready,
    Installing,
    /// <summary>Windows' permission prompt was refused.</summary>
    NeedsPermission,
    Failed,
}

/// <param name="Phase">One of <see cref="UpdatePhase"/>, as text.</param>
/// <param name="Version">The release the phase is about.</param>
/// <param name="Detail">A sentence for the banner when there is something to say.</param>
/// <param name="Automatic">False for a copy that is not the installed one.</param>
/// <param name="Current">The version answering.</param>
public sealed record UpdateSnapshot(string Phase, string? Version, string? Detail, bool Automatic, string Current);
