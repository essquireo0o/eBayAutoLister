namespace ING_eBay_AutoLister.Models;

// ── Deal Radar: the board that reads itself ───────────────────────────────────
// Every sourcing screen in this app so far answers a question the seller has to remember to ask.
// The local-arbitrage board is the best of them and it is still a button: no click, no scan, and
// the $400 miner three miles away that resells for $700 was claimed by somebody else on Tuesday
// while the app sat on the dashboard.
//
// A watch is that same scan, saved — keyword, category, zip, radius, sources — plus the only thing
// the board never had: a bar the result has to clear before anyone is interrupted. The watcher runs
// them on a human cadence, prices every hit through exactly the pipeline the board uses
// (LocalArbitrageAnalyzer → the hosted sold comps → ProfitCalculator), and fires a desktop
// notification for the rows that clear the bar.
//
// Two rules run through all of it:
//   • NOTHING here invents a number. An alert carries the row the board would have shown, with the
//     same evidence tier and the same "estimate unavailable" honesty — see DealRadarMatcher.
//   • It scrapes like a person, not like a crawler. One scan at a time, one watch per tick, a floor
//     under the interval, no Terapeak browser scrapes unattended — see DealRadarClock and
//     DealRadarService.
//
// See Services/DealRadarStore.cs (persistence), Services/DealRadarMatcher.cs (the bar),
// Services/DealRadarClock.cs (the cadence), Services/DealRadarService.cs (the loop).

/// <summary>One saved search, its profit bar, and where the watcher got to with it.</summary>
public class DealWatch
{
    public long Id { get; set; }

    /// <summary>What the seller calls it: "S19s under $500". Defaults to the query when left blank.</summary>
    public string Name { get; set; } = "";

    // ── The search: exactly the parameters /api/local/arbitrage takes ─────────
    public string Query { get; set; } = "";
    public string CategoryId { get; set; } = Services.ResaleCategoryCatalog.AnythingId;
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; } = 40;
    /// <summary>Source ids, comma-separated — craigslist,facebook. Empty means everything available.</summary>
    public string Sources { get; set; } = "";
    /// <summary>Craigslist's metro override, for a seller on a board boundary. Empty otherwise.</summary>
    public string CraigslistSite { get; set; } = "";

    // ── The bar: what a row has to clear to be worth waking someone for ───────
    // All four are ANDed, and all four are the seller's own numbers. The defaults are deliberately
    // high: a notification for a $9 flip is how a seller turns notifications off.
    public decimal MinNetProfit { get; set; } = 75m;
    public decimal MinRoiPercent { get; set; } = 40m;
    /// <summary>Ignore anything asking more than this — the cash the seller actually has. 0 = no ceiling.</summary>
    public decimal MaxAsk { get; set; }
    /// <summary>Only alert on distances at or inside this. 0 = anywhere the search reached.</summary>
    public double MaxDistanceMiles { get; set; }
    /// <summary>
    /// When true (the default), a row priced off thin or unverified comps never fires. The board
    /// dims those figures and explains them; a notification has no room to explain, and a 700% ROI
    /// off one loose comp is exactly the alert that gets the feature disbelieved.
    /// </summary>
    public bool RequireConfidentEvidence { get; set; } = true;

    // ── The cadence ──────────────────────────────────────────────────────────
    /// <summary>Minutes between scans. Floored at <see cref="Services.DealRadarClock.MinIntervalMinutes"/>.</summary>
    public int IntervalMinutes { get; set; } = Services.DealRadarClock.DefaultIntervalMinutes;
    public bool Enabled { get; set; } = true;
    /// <summary>Off turns this watch into a saved search that only ever runs when Scan now is pressed.</summary>
    public bool NotifyDesktop { get; set; } = true;

    // ── Where the watcher got to ─────────────────────────────────────────────
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? LastRunUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    /// <summary>never_run | ok | no_matches | error | paused — see <see cref="RadarRunStatuses"/>.</summary>
    public string LastStatus { get; set; } = RadarRunStatuses.NeverRun;
    /// <summary>The sentence behind a failed or empty run. Shown on the card rather than swallowed.</summary>
    public string LastNote { get; set; } = "";
    /// <summary>Listings the last scan actually looked at — the denominator behind "0 matches".</summary>
    public int LastScannedCount { get; set; }
    /// <summary>How many rows cleared the bar last run, and how many ever have.</summary>
    public int LastMatchCount { get; set; }
    public int TotalAlertCount { get; set; }
    /// <summary>Everything the alerts from this watch project, so a card can say what it's worth.</summary>
    public decimal TotalProfitFound { get; set; }
}

/// <summary>The browser's create/update payload. Every field optional — absent means "leave it".</summary>
/// <remarks>
/// Same rule as <see cref="Services.CredentialsPatch"/>, for the same reason: the card's pause
/// toggle posts one field, and a whole-object bind would blank the seller's profit bar with it.
/// </remarks>
public class DealWatchRequest
{
    public long? Id { get; set; }
    public string? Name { get; set; }
    public string? Query { get; set; }
    public string? CategoryId { get; set; }
    public string? ZipCode { get; set; }
    public int? RadiusMiles { get; set; }
    public string? Sources { get; set; }
    public string? CraigslistSite { get; set; }
    public decimal? MinNetProfit { get; set; }
    public decimal? MinRoiPercent { get; set; }
    public decimal? MaxAsk { get; set; }
    public double? MaxDistanceMiles { get; set; }
    public bool? RequireConfidentEvidence { get; set; }
    public int? IntervalMinutes { get; set; }
    public bool? Enabled { get; set; }
    public bool? NotifyDesktop { get; set; }
}

/// <summary>
/// One find worth interrupting somebody for: the board's own row, frozen at the moment it cleared
/// the bar, plus the one sentence a notification has room for.
/// </summary>
/// <remarks>
/// Frozen, not a pointer: a classified is deleted the hour it sells, and an alert that re-reads its
/// listing to render would blank itself exactly when the seller wants to know what they missed. Every
/// figure here is the figure the board published at <see cref="FoundUtc"/> — see DealRadarMatcher.
/// </remarks>
public class DealAlert
{
    public long Id { get; set; }
    public long WatchId { get; set; }
    public string WatchName { get; set; } = "";

    /// <summary>source + item id — what makes the same post found on six scans one alert.</summary>
    public string ItemKey { get; set; } = "";

    // ── The listing ──────────────────────────────────────────────────────────
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    public string CategoryLabel { get; set; } = "";

    // ── The money, exactly as the board computed it ──────────────────────────
    public decimal LocalAsk { get; set; }
    public decimal? ResalePrice { get; set; }
    public decimal? NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    /// <summary>The highest ask that still breaks even — the number to walk in with.</summary>
    public decimal? MaxBuyPrice { get; set; }
    public int? DaysToCash { get; set; }
    public int CompCount { get; set; }
    /// <summary>confident | low — carried so the card can be read the same way the board is.</summary>
    public string EvidenceTier { get; set; } = "";
    public string EvidenceNote { get; set; } = "";
    /// <summary>goldmine | solid — nothing weaker is ever alerted on.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>
    /// "$400 Antminer S19 · 3 mi away → resells ~$700 · $210 profit, 52% margin". The whole point of
    /// the feature in one line, because a Windows toast shows two lines and no table.
    /// </summary>
    public string Headline { get; set; } = "";

    public DateTimeOffset FoundUtc { get; set; }
    /// <summary>Set when the notification actually left — false means it was collected during quiet hours.</summary>
    public bool Notified { get; set; }
    public bool Read { get; set; }
    public bool Dismissed { get; set; }
}

public static class RadarRunStatuses
{
    public const string NeverRun = "never_run";
    public const string Ok = "ok";
    public const string NoMatches = "no_matches";
    public const string Error = "error";
    public const string Paused = "paused";
}

/// <summary>What the radar does as a whole, independently of any one watch.</summary>
public class DealRadarSettings
{
    /// <summary>The master switch. Off means nothing scans in the background; Scan now still works.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Hours when a find is collected silently instead of popped. Scanning does not stop — the
    /// whole promise is that the app works while the seller is asleep — but nothing pings at 3am,
    /// and the badge is waiting in the morning. Local time, 0-23. Equal values mean never quiet.
    /// </summary>
    public int QuietFromHour { get; set; } = 23;
    public int QuietToHour { get; set; } = 7;
    public bool QuietHoursEnabled { get; set; } = true;

    /// <summary>Off keeps the in-app feed and stops the OS-level notification.</summary>
    public bool DesktopNotifications { get; set; } = true;
}

/// <summary>Everything the Deal Radar screen reads in one call.</summary>
public class DealRadarStatus
{
    public DealRadarSettings Settings { get; set; } = new();
    public List<DealWatch> Watches { get; set; } = [];

    /// <summary>True while a scan is actually running — one at a time, ever.</summary>
    public bool Scanning { get; set; }
    /// <summary>Which watch, when one is running.</summary>
    public long? ScanningWatchId { get; set; }
    public DateTimeOffset? LastScanUtc { get; set; }
    /// <summary>When the next background scan is due, across every enabled watch.</summary>
    public DateTimeOffset? NextScanUtc { get; set; }

    public int UnreadAlertCount { get; set; }
    public int TotalAlertCount { get; set; }
    /// <summary>Projected profit across the unread alerts — what is sitting in the feed right now.</summary>
    public decimal UnreadProfit { get; set; }

    /// <summary>
    /// How an alert can actually reach the desktop from this process: <c>tray</c> when the tray icon
    /// is live and Windows will show its balloon, <c>browser</c> when it can't (a headless Windows
    /// service can't draw on the desktop) and the page's own Notification API is the only channel.
    /// Stated rather than assumed — promising a notification that physically cannot be shown is
    /// worse than saying the tab has to stay open.
    /// </summary>
    public string DesktopChannel { get; set; } = RadarChannels.Browser;

    /// <summary>Currently quiet — the UI says "collecting silently until 7am" instead of going quiet itself.</summary>
    public bool InQuietHours { get; set; }

    /// <summary>Watches allowed, so the UI can stop offering an Add button that will be refused.</summary>
    public int MaxWatches { get; set; }
    public int MinIntervalMinutes { get; set; }
}

public static class RadarChannels
{
    public const string Tray = "tray";
    public const string Browser = "browser";
}

/// <summary>One thing to put on the desktop. Small on purpose — a Windows balloon is a title and a line.</summary>
/// <param name="Title">"Deal Radar · S19s under $500"</param>
/// <param name="Message">The alert headline.</param>
/// <param name="AlertId">Which alert this was, so a click can open straight to it.</param>
public sealed record DesktopNotification(string Title, string Message, long AlertId)
{
    public DateTimeOffset SentUtc { get; init; } = DateTimeOffset.UtcNow;
}
