using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// When the radar is allowed to look, and when it is allowed to interrupt. Pure arithmetic over a
/// clock the caller supplies, so the whole cadence is testable without waiting for it.
/// </summary>
/// <remarks>
/// <para>
/// This is where the scraping posture lives. Every source the radar reads is somebody else's site,
/// read the same way a person reads it — and the difference between "a seller who checks craigslist
/// four times a day" and "a bot" is entirely a matter of rate. So:
/// </para>
/// <list type="bullet">
///   <item><b>One watch per tick.</b> Six saved searches do not become six simultaneous scans; they
///     become six scans spread across the hour, in the order they went overdue.</item>
///   <item><b>A floor under the interval</b> (<see cref="MinIntervalMinutes"/>), so no setting in the
///     UI can turn the feature into a polling loop.</item>
///   <item><b>A global gap</b> (<see cref="MinGapMinutes"/>) between any two scans, whichever watches
///     they belong to — the site sees one request pattern, not one per watch.</item>
///   <item><b>Jitter.</b> A fixed three-hour interval means requests on the hour, every hour, forever,
///     which is the single most machine-looking thing a scheduler can do. The offset is derived from
///     the watch id, so it is stable across restarts rather than random per run.</item>
/// </list>
/// <para>
/// Quiet hours are the opposite question — not when to look, but when to stay silent. Scanning never
/// stops for them: the entire promise of this feature is that it works overnight. What stops is the
/// popping, and the finds are waiting in the feed at breakfast.
/// </para>
/// </remarks>
public static class DealRadarClock
{
    /// <summary>The floor under any watch's interval. Half an hour is already faster than a person.</summary>
    public const int MinIntervalMinutes = 30;
    public const int DefaultIntervalMinutes = 180;
    /// <summary>A day. Longer than this and the watch is a bookmark, not a radar.</summary>
    public const int MaxIntervalMinutes = 1440;

    /// <summary>The shortest gap between any two scans, across every watch.</summary>
    public const int MinGapMinutes = 5;

    /// <summary>How many saved searches one seller can run. Twelve every three hours is 96 scans a day.</summary>
    public const int MaxWatches = 12;

    /// <summary>Up to this many minutes of stable, per-watch offset, so scans don't land on the hour.</summary>
    public const int JitterMinutes = 7;

    public static int SanitizeInterval(int? minutes) =>
        Math.Clamp(minutes ?? DefaultIntervalMinutes, MinIntervalMinutes, MaxIntervalMinutes);

    /// <summary>
    /// The per-watch offset. Deterministic in the id: watch 3 always runs three-ish minutes off the
    /// hour, restart or no restart, which is what keeps two watches on the same interval from
    /// marching in lockstep without making the schedule unpredictable to the seller.
    /// </summary>
    public static int JitterFor(long watchId) =>
        JitterMinutes <= 0 ? 0 : (int)(Math.Abs(watchId) % (JitterMinutes + 1));

    /// <summary>When a watch that just ran at <paramref name="ranAt"/> should run again.</summary>
    public static DateTimeOffset NextRun(DealWatch watch, DateTimeOffset ranAt) =>
        ranAt.AddMinutes(SanitizeInterval(watch.IntervalMinutes) + JitterFor(watch.Id));

    /// <summary>
    /// The one watch to scan right now, or null. Most overdue first, so a watch that fell behind
    /// while the app was closed is caught up before one that is barely due.
    /// </summary>
    /// <param name="lastScanUtc">
    /// When any watch last scanned. The global gap is measured from here, which is what stops a
    /// restart — where every watch is instantly overdue — from firing twelve scans in twelve seconds.
    /// </param>
    public static DealWatch? NextDueWatch(
        IEnumerable<DealWatch> watches, DateTimeOffset now, DateTimeOffset? lastScanUtc)
    {
        if (lastScanUtc is { } last && now < last.AddMinutes(MinGapMinutes)) return null;

        return watches
            .Where(w => w.Enabled)
            .Where(w => DueAt(w) <= now)
            .OrderBy(w => DueAt(w))
            .ThenBy(w => w.Id)
            .FirstOrDefault();
    }

    /// <summary>
    /// When the next background scan will happen, for the "next sweep at 4:20pm" line. Null when
    /// nothing is enabled — which the UI says out loud rather than showing a blank.
    /// </summary>
    public static DateTimeOffset? NextScanDue(IEnumerable<DealWatch> watches, DateTimeOffset now)
    {
        var due = watches.Where(w => w.Enabled).Select(DueAt).ToList();
        if (due.Count == 0) return null;

        var earliest = due.Min();
        // An overdue watch is "due now", not due in the past: a card reading "next scan 4 hours ago"
        // describes the schedule rather than what is about to happen.
        return earliest < now ? now : earliest;
    }

    /// <summary>
    /// A watch with no <c>NextRunUtc</c> — one just created, or one from before this field existed —
    /// is due immediately. That is the behaviour a seller expects from pressing Save on a watch.
    /// </summary>
    private static DateTimeOffset DueAt(DealWatch watch) =>
        watch.NextRunUtc ?? (watch.LastRunUtc is { } ran ? NextRun(watch, ran) : DateTimeOffset.MinValue);

    /// <summary>
    /// Whether a find should be collected silently. Handles the overnight wrap (23 → 7) rather than
    /// assuming from &lt; to, because the window a seller actually wants quiet always wraps midnight.
    /// </summary>
    /// <param name="localNow">Local time, not UTC — 11pm means the seller's 11pm.</param>
    public static bool IsQuiet(DealRadarSettings settings, DateTimeOffset localNow)
    {
        if (settings is null || !settings.QuietHoursEnabled) return false;

        var from = Math.Clamp(settings.QuietFromHour, 0, 23);
        var to = Math.Clamp(settings.QuietToHour, 0, 23);
        if (from == to) return false;   // a zero-width window is "never quiet", not "always quiet"

        var hour = localNow.Hour;
        return from < to
            ? hour >= from && hour < to        // 1am → 6am
            : hour >= from || hour < to;       // 11pm → 7am, across midnight
    }

    /// <summary>"11pm — 7am" for the settings line. Local, 12-hour, because that is how the window is set.</summary>
    public static string DescribeQuietHours(DealRadarSettings settings)
    {
        if (settings is null || !settings.QuietHoursEnabled) return "";
        var from = Math.Clamp(settings.QuietFromHour, 0, 23);
        var to = Math.Clamp(settings.QuietToHour, 0, 23);
        return from == to ? "" : $"{Hour(from)} — {Hour(to)}";

        static string Hour(int h) => h switch
        {
            0 => "12am",
            12 => "12pm",
            < 12 => $"{h}am",
            _ => $"{h - 12}pm",
        };
    }
}
