using System.Reflection;
using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Tells the seller when a newer version has been released, and where to get it.
/// </summary>
/// <remarks>
/// <para>
/// The app installs from an .msi and never phones home, so a seller who installed in July was still
/// running July's build in October with no way to know. Shipping improvements nobody receives is the
/// same as not shipping them.
/// </para>
/// <para>
/// It reads the latest release tag from the public GitHub repo. Three rules govern it, all of them
/// about not becoming a nuisance:
/// </para>
/// <list type="number">
///   <item>An update check must never break the app. Every failure here — offline, rate-limited,
///     GitHub down, a tag that doesn't parse — ends as "no update known", never as an error the
///     seller sees. Nobody's listing tool should fall over because a version check failed.</item>
///   <item>It must not hammer GitHub. Unauthenticated API allows 60 requests an hour <em>per IP</em>,
///     shared across every install behind one address, so the answer is cached and re-checked at
///     most once every six hours.</item>
///   <item>It points at the download page, not the GitHub tag. A tag exists the moment it is pushed;
///     the installer only exists once publish-update.ps1 has shipped it. Sending sellers to fetch a
///     build that has not been packaged yet would be worse than saying nothing.</item>
/// </list>
/// </remarks>
public sealed class UpdateChecker(IHttpClientFactory httpFactory, ActionLog log)
{
    private const string ReleasesApi = "https://api.github.com/repos/essquireo0o/eBayAutoLister/releases/latest";

    /// <summary>Where an update is actually downloaded. publish-update.ps1 keeps this current.</summary>
    public const string DownloadUrl = "https://inglisting.com/";

    private static readonly TimeSpan CacheFor = TimeSpan.FromHours(6);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateStatus? _cached;
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;

    /// <summary>The version this build reports, from the assembly.</summary>
    public static string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public async Task<UpdateStatus> CheckAsync(bool force = false, CancellationToken ct = default)
    {
        if (!force && _cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
            return _cached;

        await _gate.WaitAsync(ct);
        try
        {
            // A caller that queued behind the one that just refreshed gets that answer, rather than
            // spending a second request on the same question.
            if (!force && _cached is not null && DateTimeOffset.UtcNow - _checkedAt < CacheFor)
                return _cached;

            var status = await FetchAsync(ct);
            _cached = status;
            _checkedAt = DateTimeOffset.UtcNow;
            return status;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<UpdateStatus> FetchAsync(CancellationToken ct)
    {
        var current = CurrentVersion;

        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            // GitHub rejects requests with no User-Agent outright.
            client.DefaultRequestHeaders.Add("User-Agent", $"ING-Listing-Engine/{current}");
            client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");

            var res = await client.GetAsync(ReleasesApi, ct);
            if (!res.IsSuccessStatusCode)
                return new UpdateStatus(current, null, false, DownloadUrl, null);

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var notes = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;

            if (string.IsNullOrWhiteSpace(tag))
                return new UpdateStatus(current, null, false, DownloadUrl, null);

            var latest = Normalize(tag);
            var newer = IsNewer(latest, current);

            if (newer)
                log.Add("Info", "Update available", $"{current} installed, {latest} released.");

            return new UpdateStatus(current, latest, newer, DownloadUrl, notes);
        }
        catch (Exception ex)
        {
            // Deliberately quiet: a failed version check is not the seller's problem and must not
            // reach their screen as an error.
            log.Add("Info", "Update check skipped", ex.Message);
            return new UpdateStatus(current, null, false, DownloadUrl, null);
        }
    }

    /// <summary>Strips a leading "v" and anything after the numbers: "v2.2.0-beta" -> "2.2.0".</summary>
    public static string Normalize(string tag)
    {
        var s = tag.Trim().TrimStart('v', 'V');
        var cut = s.IndexOfAny(['-', '+', ' ']);
        return cut > 0 ? s[..cut] : s;
    }

    /// <summary>
    /// True when <paramref name="latest"/> is a higher version than <paramref name="current"/>.
    /// </summary>
    /// <remarks>
    /// Compared part by part as numbers, never as text: "2.10.0" is newer than "2.9.0" and a string
    /// comparison says the opposite. Anything unparseable returns false — silence beats a wrong
    /// "update available" that sends someone to reinstall what they already have.
    /// </remarks>
    public static bool IsNewer(string latest, string current)
    {
        static int[] Parts(string v) =>
            v.Split('.').Select(p => int.TryParse(p, out var i) ? i : -1).ToArray();

        var a = Parts(latest);
        var b = Parts(current);
        if (a.Length == 0 || a.Any(x => x < 0) || b.Any(x => x < 0)) return false;

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }
}

/// <param name="Current">The version running right now.</param>
/// <param name="Latest">The newest released version, or null when it could not be determined.</param>
/// <param name="UpdateAvailable">Only true when a newer version is genuinely known to exist.</param>
/// <param name="DownloadUrl">Where to get it — the download page, not the git tag.</param>
/// <param name="ReleaseName">The release's title, when it has one.</param>
public sealed record UpdateStatus(
    string Current, string? Latest, bool UpdateAvailable, string DownloadUrl, string? ReleaseName);
