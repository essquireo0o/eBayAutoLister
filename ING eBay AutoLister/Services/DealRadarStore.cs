using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Where the radar's saved searches, its finds and its settings live — the app's own SQLite
/// database, beside <see cref="DealStore"/> and <see cref="EarningsStore"/>.
/// </summary>
/// <remarks>
/// <para>
/// Two tables rather than one, and the reason is the feature's whole credibility. <c>radar_alerts</c>
/// is the <i>feed</i> — it is read, dismissed and eventually pruned, because a wall of six-week-old
/// finds is not something anyone scrolls. <c>radar_seen</c> is the <i>memory</i>: one row per listing
/// this watch has ever fired on, kept long past the alert it produced.
/// </para>
/// <para>
/// Folding them together looks tempting and is a bug with a long fuse: prune the feed and every
/// classified still up on craigslist is "new" again, so the seller gets last month's deals pushed at
/// them at 2am and turns the feature off. The memory outlives the message.
/// </para>
/// </remarks>
public sealed class DealRadarStore
{
    /// <summary>Alerts kept in the feed. Past this the oldest read ones go.</summary>
    public const int MaxAlertsKept = 300;

    /// <summary>
    /// How long a listing stays "already seen". Comfortably longer than a classified lives — a
    /// craigslist post expires in 30 days, and a repost is genuinely a new listing worth a look.
    /// </summary>
    public const int SeenMemoryDays = 45;

    private readonly string _databasePath;
    private readonly object _writeLock = new();

    public DealRadarStore(ListingDatabase database) : this(database.DatabasePath) { }

    public DealRadarStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    // ── Watches ───────────────────────────────────────────────────────────────

    public List<DealWatch> ListWatches()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {WatchColumns} FROM radar_watches ORDER BY id;";

        var rows = new List<DealWatch>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadWatch(reader));
        return rows;
    }

    public DealWatch? GetWatch(long id) => ListWatches().FirstOrDefault(w => w.Id == id);

    /// <summary>
    /// Creates a watch, or applies a partial edit to one. Absent fields are left alone — the pause
    /// toggle posts <c>{ id, enabled }</c> and must not blank the profit bar it didn't show.
    /// </summary>
    public DealWatch SaveWatch(DealWatchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_writeLock)
        {
            var existing = request.Id is > 0 ? GetWatch(request.Id.Value) : null;
            if (request.Id is > 0 && existing is null)
                throw new InvalidOperationException("That watch no longer exists — it may have been deleted in another tab.");

            if (existing is null && ListWatches().Count >= DealRadarClock.MaxWatches)
            {
                throw new InvalidOperationException(
                    $"You can run {DealRadarClock.MaxWatches} watches at once. Delete or pause one to add another — " +
                    "every extra watch is another site being read on your behalf.");
            }

            var watch = existing ?? new DealWatch { CreatedUtc = DateTimeOffset.UtcNow };

            if (request.Query is not null) watch.Query = request.Query.Trim();
            if (request.CategoryId is not null) watch.CategoryId = ResaleCategoryCatalog.Resolve(request.CategoryId).Id;
            if (request.ZipCode is not null) watch.ZipCode = request.ZipCode.Trim();
            if (request.RadiusMiles is { } radius) watch.RadiusMiles = Math.Clamp(radius, 1, 500);
            if (request.Sources is not null) watch.Sources = request.Sources.Trim();
            if (request.CraigslistSite is not null) watch.CraigslistSite = request.CraigslistSite.Trim();

            if (request.MinNetProfit is { } profit) watch.MinNetProfit = Math.Max(0m, profit);
            if (request.MinRoiPercent is { } roi) watch.MinRoiPercent = Math.Max(0m, roi);
            if (request.MaxAsk is { } ask) watch.MaxAsk = Math.Max(0m, ask);
            if (request.MaxDistanceMiles is { } miles) watch.MaxDistanceMiles = Math.Max(0d, miles);
            if (request.RequireConfidentEvidence is { } strict) watch.RequireConfidentEvidence = strict;

            if (request.IntervalMinutes is { } interval) watch.IntervalMinutes = DealRadarClock.SanitizeInterval(interval);
            if (request.Enabled is { } enabled) watch.Enabled = enabled;
            if (request.NotifyDesktop is { } notify) watch.NotifyDesktop = notify;

            // Named after what it searches for, so a card is readable without opening it.
            if (request.Name is not null) watch.Name = request.Name.Trim();
            if (watch.Name.Length == 0) watch.Name = DefaultName(watch);

            Validate(watch);

            // A new watch, and one that was just un-paused, both run on the next tick rather than
            // three hours from now: pressing Save and seeing nothing happen for an afternoon is how
            // a seller concludes the feature is broken.
            if (existing is null || request.Enabled == true) watch.NextRunUtc = null;

            return Upsert(watch);
        }
    }

    /// <summary>Records what a run did. The only thing that moves a watch's schedule forward.</summary>
    public DealWatch? RecordRun(
        long watchId, string status, string note, int scanned, int matches, decimal profitFound,
        DateTimeOffset ranAt)
    {
        lock (_writeLock)
        {
            var watch = GetWatch(watchId);
            if (watch is null) return null;

            watch.LastRunUtc = ranAt;
            watch.NextRunUtc = DealRadarClock.NextRun(watch, ranAt);
            watch.LastStatus = status;
            watch.LastNote = note ?? "";
            watch.LastScannedCount = scanned;
            watch.LastMatchCount = matches;
            watch.TotalAlertCount += matches;
            watch.TotalProfitFound = Math.Round(watch.TotalProfitFound + profitFound, 2);
            return Upsert(watch);
        }
    }

    public bool DeleteWatch(long id)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // The finds go with it: an alert whose watch is gone has no card to sit under, and the
            // memory of a deleted search is not worth keeping either.
            command.CommandText = """
                DELETE FROM radar_alerts WHERE watch_id = @id;
                DELETE FROM radar_seen WHERE watch_id = @id;
                DELETE FROM radar_watches WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>"S19 · Cars &amp; trucks · 40 mi of 89101" — a name for a watch the seller didn't name.</summary>
    public static string DefaultName(DealWatch watch)
    {
        var category = ResaleCategoryCatalog.Resolve(watch.CategoryId);
        var parts = new List<string>();
        if (watch.Query.Length > 0) parts.Add(watch.Query);
        else if (!category.IsDefault) parts.Add(category.Label);
        if (watch.ZipCode.Length > 0) parts.Add($"{watch.RadiusMiles} mi of {watch.ZipCode}");
        return parts.Count > 0 ? string.Join(" · ", parts) : "New watch";
    }

    // Refuses only what would produce a scan nobody can act on, or one this app shouldn't run.
    private static void Validate(DealWatch watch)
    {
        var category = ResaleCategoryCatalog.Resolve(watch.CategoryId);

        // A blank keyword is a real search on a category board ("everything on the cars board") and
        // the entire classifieds section without one — the same rule the search box already applies.
        if (watch.Query.Length == 0 && category.IsDefault)
            throw new InvalidOperationException("Give the watch something to look for — a keyword, or a category to sweep.");

        // Craigslist resolves a metro from the zip. With neither a zip nor a named board there is
        // nothing to search, and "near you" would mean whatever board happened to be first.
        var hasZip = watch.ZipCode.Length >= 5 && watch.ZipCode.Take(5).All(char.IsDigit);
        if (!hasZip && watch.CraigslistSite.Length == 0)
            throw new InvalidOperationException("Enter the ZIP code to search around, so the scan knows where you are.");

        if (watch.RadiusMiles is < 1 or > 500)
            throw new InvalidOperationException("Search radius has to be between 1 and 500 miles.");
    }

    private DealWatch Upsert(DealWatch watch)
    {
        using var connection = OpenConnection();
        if (watch.Id > 0)
        {
            using var update = connection.CreateCommand();
            update.CommandText = """
                UPDATE radar_watches SET
                    name = @name, query = @query, category_id = @category_id, zip = @zip,
                    radius_miles = @radius_miles, sources = @sources, craigslist_site = @craigslist_site,
                    min_net_profit = @min_net_profit, min_roi_percent = @min_roi_percent,
                    max_ask = @max_ask, max_distance_miles = @max_distance_miles,
                    require_confident = @require_confident, interval_minutes = @interval_minutes,
                    enabled = @enabled, notify_desktop = @notify_desktop,
                    created_at = @created_at, last_run_at = @last_run_at, next_run_at = @next_run_at,
                    last_status = @last_status, last_note = @last_note,
                    last_scanned_count = @last_scanned_count, last_match_count = @last_match_count,
                    total_alert_count = @total_alert_count, total_profit_found = @total_profit_found
                WHERE id = @id;
                """;
            BindWatch(update, watch);
            update.Parameters.AddWithValue("@id", watch.Id);
            update.ExecuteNonQuery();
            return watch;
        }

        using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO radar_watches
                (name, query, category_id, zip, radius_miles, sources, craigslist_site,
                 min_net_profit, min_roi_percent, max_ask, max_distance_miles, require_confident,
                 interval_minutes, enabled, notify_desktop, created_at, last_run_at, next_run_at,
                 last_status, last_note, last_scanned_count, last_match_count,
                 total_alert_count, total_profit_found)
            VALUES
                (@name, @query, @category_id, @zip, @radius_miles, @sources, @craigslist_site,
                 @min_net_profit, @min_roi_percent, @max_ask, @max_distance_miles, @require_confident,
                 @interval_minutes, @enabled, @notify_desktop, @created_at, @last_run_at, @next_run_at,
                 @last_status, @last_note, @last_scanned_count, @last_match_count,
                 @total_alert_count, @total_profit_found);
            SELECT last_insert_rowid();
            """;
        BindWatch(insert, watch);
        watch.Id = Convert.ToInt64(insert.ExecuteScalar());
        return watch;
    }

    // ── Alerts ────────────────────────────────────────────────────────────────

    /// <summary>Every listing this watch has already fired on, so it never fires on it twice.</summary>
    public HashSet<string> SeenKeys(long watchId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT item_key FROM radar_seen WHERE watch_id = @id;";
        command.Parameters.AddWithValue("@id", watchId);

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read()) keys.Add(reader.GetString(0));
        return keys;
    }

    /// <summary>
    /// Files a run's finds. Returns the ones actually stored, with ids — a key that was already
    /// remembered is dropped here rather than at the caller, so two racing scans of the same watch
    /// can't both notify.
    /// </summary>
    public List<DealAlert> AddAlerts(IEnumerable<DealAlert> alerts)
    {
        lock (_writeLock)
        {
            var stored = new List<DealAlert>();
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            foreach (var alert in alerts ?? [])
            {
                if (string.IsNullOrWhiteSpace(alert.ItemKey)) continue;

                using var remember = connection.CreateCommand();
                remember.Transaction = transaction;
                remember.CommandText = """
                    INSERT OR IGNORE INTO radar_seen (watch_id, item_key, seen_at)
                    VALUES (@watch_id, @item_key, @seen_at);
                    """;
                remember.Parameters.AddWithValue("@watch_id", alert.WatchId);
                remember.Parameters.AddWithValue("@item_key", alert.ItemKey);
                remember.Parameters.AddWithValue("@seen_at", alert.FoundUtc.ToString("O"));
                if (remember.ExecuteNonQuery() == 0) continue;   // already known — not a new find

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO radar_alerts
                        (watch_id, watch_name, item_key, title, url, image_url, source, source_label,
                         location, distance_miles, category_label, local_ask, resale_price, net_profit,
                         roi_percent, margin_percent, max_buy_price, days_to_cash, comp_count,
                         evidence_tier, evidence_note, verdict, headline, found_at,
                         notified, read, dismissed)
                    VALUES
                        (@watch_id, @watch_name, @item_key, @title, @url, @image_url, @source, @source_label,
                         @location, @distance_miles, @category_label, @local_ask, @resale_price, @net_profit,
                         @roi_percent, @margin_percent, @max_buy_price, @days_to_cash, @comp_count,
                         @evidence_tier, @evidence_note, @verdict, @headline, @found_at,
                         @notified, @read, @dismissed);
                    SELECT last_insert_rowid();
                    """;
                BindAlert(insert, alert);
                alert.Id = Convert.ToInt64(insert.ExecuteScalar());
                stored.Add(alert);
            }

            transaction.Commit();
            return stored;
        }
    }

    /// <summary>The feed, newest first. Dismissed ones are out of the way but not forgotten.</summary>
    public List<DealAlert> ListAlerts(int limit = 100, bool includeDismissed = false)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {AlertColumns} FROM radar_alerts
            {(includeDismissed ? "" : "WHERE dismissed = 0")}
            ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, MaxAlertsKept));

        var rows = new List<DealAlert>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadAlert(reader));
        return rows;
    }

    public bool SetAlertFlag(long id, string column, bool value)
    {
        if (column is not ("read" or "dismissed" or "notified")) return false;

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            // Interpolated, not parameterised, because a column name cannot be a parameter — and the
            // only values that reach here are the three literals checked above.
            command.CommandText = $"UPDATE radar_alerts SET {column} = @value WHERE id = @id;";
            command.Parameters.AddWithValue("@value", value ? 1 : 0);
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    public int MarkAllRead()
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE radar_alerts SET read = 1 WHERE read = 0;";
            return command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Empties the feed without forgetting anything: <c>radar_seen</c> is untouched, so clearing the
    /// list does not re-alert every listing still up.
    /// </summary>
    public int ClearAlerts()
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM radar_alerts;";
            return command.ExecuteNonQuery();
        }
    }

    public (int Unread, int Total, decimal UnreadProfit) AlertCounts()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                SUM(CASE WHEN read = 0 AND dismissed = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN dismissed = 0 THEN 1 ELSE 0 END),
                SUM(CASE WHEN read = 0 AND dismissed = 0 THEN COALESCE(net_profit, 0) ELSE 0 END)
            FROM radar_alerts;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return (0, 0, 0m);
        return (
            reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
            reader.IsDBNull(2) ? 0m : Math.Round(reader.GetDecimal(2), 2));
    }

    /// <summary>
    /// Keeps the feed and the memory from growing forever. Read alerts go first and the newest
    /// <see cref="MaxAlertsKept"/> always stay, so a seller who hasn't looked in a week still finds
    /// everything waiting.
    /// </summary>
    public void Prune(DateTimeOffset now)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM radar_alerts WHERE id NOT IN (
                    SELECT id FROM radar_alerts ORDER BY read ASC, id DESC LIMIT @keep);
                DELETE FROM radar_seen WHERE seen_at <> '' AND seen_at < @cutoff;
                """;
            command.Parameters.AddWithValue("@keep", MaxAlertsKept);
            command.Parameters.AddWithValue("@cutoff", now.AddDays(-SeenMemoryDays).ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    // ── Settings ──────────────────────────────────────────────────────────────

    public DealRadarSettings GetSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enabled, quiet_enabled, quiet_from_hour, quiet_to_hour, desktop_notifications
            FROM radar_settings WHERE id = 1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new DealRadarSettings();

        return new DealRadarSettings
        {
            Enabled = reader.GetInt32(0) == 1,
            QuietHoursEnabled = reader.GetInt32(1) == 1,
            QuietFromHour = Math.Clamp(reader.GetInt32(2), 0, 23),
            QuietToHour = Math.Clamp(reader.GetInt32(3), 0, 23),
            DesktopNotifications = reader.GetInt32(4) == 1,
        };
    }

    public DealRadarSettings SaveSettings(DealRadarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.QuietFromHour = Math.Clamp(settings.QuietFromHour, 0, 23);
        settings.QuietToHour = Math.Clamp(settings.QuietToHour, 0, 23);

        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE radar_settings SET
                    enabled = @enabled, quiet_enabled = @quiet_enabled,
                    quiet_from_hour = @quiet_from_hour, quiet_to_hour = @quiet_to_hour,
                    desktop_notifications = @desktop_notifications
                WHERE id = 1;
                """;
            command.Parameters.AddWithValue("@enabled", settings.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@quiet_enabled", settings.QuietHoursEnabled ? 1 : 0);
            command.Parameters.AddWithValue("@quiet_from_hour", settings.QuietFromHour);
            command.Parameters.AddWithValue("@quiet_to_hour", settings.QuietToHour);
            command.Parameters.AddWithValue("@desktop_notifications", settings.DesktopNotifications ? 1 : 0);
            command.ExecuteNonQuery();
            return settings;
        }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private const string WatchColumns =
        "id, name, query, category_id, zip, radius_miles, sources, craigslist_site, " +
        "min_net_profit, min_roi_percent, max_ask, max_distance_miles, require_confident, " +
        "interval_minutes, enabled, notify_desktop, created_at, last_run_at, next_run_at, " +
        "last_status, last_note, last_scanned_count, last_match_count, total_alert_count, total_profit_found";

    private const string AlertColumns =
        "id, watch_id, watch_name, item_key, title, url, image_url, source, source_label, location, " +
        "distance_miles, category_label, local_ask, resale_price, net_profit, roi_percent, " +
        "margin_percent, max_buy_price, days_to_cash, comp_count, evidence_tier, evidence_note, " +
        "verdict, headline, found_at, notified, read, dismissed";

    private static void BindWatch(SqliteCommand command, DealWatch watch)
    {
        command.Parameters.AddWithValue("@name", watch.Name ?? "");
        command.Parameters.AddWithValue("@query", watch.Query ?? "");
        command.Parameters.AddWithValue("@category_id", watch.CategoryId ?? ResaleCategoryCatalog.AnythingId);
        command.Parameters.AddWithValue("@zip", watch.ZipCode ?? "");
        command.Parameters.AddWithValue("@radius_miles", watch.RadiusMiles);
        command.Parameters.AddWithValue("@sources", watch.Sources ?? "");
        command.Parameters.AddWithValue("@craigslist_site", watch.CraigslistSite ?? "");
        command.Parameters.AddWithValue("@min_net_profit", watch.MinNetProfit);
        command.Parameters.AddWithValue("@min_roi_percent", watch.MinRoiPercent);
        command.Parameters.AddWithValue("@max_ask", watch.MaxAsk);
        command.Parameters.AddWithValue("@max_distance_miles", watch.MaxDistanceMiles);
        command.Parameters.AddWithValue("@require_confident", watch.RequireConfidentEvidence ? 1 : 0);
        command.Parameters.AddWithValue("@interval_minutes", watch.IntervalMinutes);
        command.Parameters.AddWithValue("@enabled", watch.Enabled ? 1 : 0);
        command.Parameters.AddWithValue("@notify_desktop", watch.NotifyDesktop ? 1 : 0);
        command.Parameters.AddWithValue("@created_at", watch.CreatedUtc.ToString("O"));
        command.Parameters.AddWithValue("@last_run_at", watch.LastRunUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@next_run_at", watch.NextRunUtc?.ToString("O") ?? "");
        command.Parameters.AddWithValue("@last_status", watch.LastStatus ?? RadarRunStatuses.NeverRun);
        command.Parameters.AddWithValue("@last_note", watch.LastNote ?? "");
        command.Parameters.AddWithValue("@last_scanned_count", watch.LastScannedCount);
        command.Parameters.AddWithValue("@last_match_count", watch.LastMatchCount);
        command.Parameters.AddWithValue("@total_alert_count", watch.TotalAlertCount);
        command.Parameters.AddWithValue("@total_profit_found", watch.TotalProfitFound);
    }

    private static DealWatch ReadWatch(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        Query = reader.GetString(2),
        CategoryId = reader.GetString(3),
        ZipCode = reader.GetString(4),
        RadiusMiles = reader.GetInt32(5),
        Sources = reader.GetString(6),
        CraigslistSite = reader.GetString(7),
        MinNetProfit = reader.GetDecimal(8),
        MinRoiPercent = reader.GetDecimal(9),
        MaxAsk = reader.GetDecimal(10),
        MaxDistanceMiles = reader.GetDouble(11),
        RequireConfidentEvidence = reader.GetInt32(12) == 1,
        IntervalMinutes = reader.GetInt32(13),
        Enabled = reader.GetInt32(14) == 1,
        NotifyDesktop = reader.GetInt32(15) == 1,
        CreatedUtc = ReadDate(reader, 16) ?? DateTimeOffset.MinValue,
        LastRunUtc = ReadDate(reader, 17),
        NextRunUtc = ReadDate(reader, 18),
        LastStatus = reader.GetString(19),
        LastNote = reader.GetString(20),
        LastScannedCount = reader.GetInt32(21),
        LastMatchCount = reader.GetInt32(22),
        TotalAlertCount = reader.GetInt32(23),
        TotalProfitFound = reader.GetDecimal(24),
    };

    private static void BindAlert(SqliteCommand command, DealAlert alert)
    {
        command.Parameters.AddWithValue("@watch_id", alert.WatchId);
        command.Parameters.AddWithValue("@watch_name", alert.WatchName ?? "");
        command.Parameters.AddWithValue("@item_key", alert.ItemKey ?? "");
        command.Parameters.AddWithValue("@title", alert.Title ?? "");
        command.Parameters.AddWithValue("@url", alert.Url ?? "");
        command.Parameters.AddWithValue("@image_url", alert.ImageUrl ?? "");
        command.Parameters.AddWithValue("@source", alert.Source ?? "");
        command.Parameters.AddWithValue("@source_label", alert.SourceLabel ?? "");
        command.Parameters.AddWithValue("@location", alert.Location ?? "");
        command.Parameters.AddWithValue("@distance_miles", (object?)alert.DistanceMiles ?? DBNull.Value);
        command.Parameters.AddWithValue("@category_label", alert.CategoryLabel ?? "");
        command.Parameters.AddWithValue("@local_ask", alert.LocalAsk);
        command.Parameters.AddWithValue("@resale_price", (object?)alert.ResalePrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@net_profit", (object?)alert.NetProfit ?? DBNull.Value);
        command.Parameters.AddWithValue("@roi_percent", (object?)alert.RoiPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("@margin_percent", (object?)alert.MarginPercent ?? DBNull.Value);
        command.Parameters.AddWithValue("@max_buy_price", (object?)alert.MaxBuyPrice ?? DBNull.Value);
        command.Parameters.AddWithValue("@days_to_cash", (object?)alert.DaysToCash ?? DBNull.Value);
        command.Parameters.AddWithValue("@comp_count", alert.CompCount);
        command.Parameters.AddWithValue("@evidence_tier", alert.EvidenceTier ?? "");
        command.Parameters.AddWithValue("@evidence_note", alert.EvidenceNote ?? "");
        command.Parameters.AddWithValue("@verdict", alert.Verdict ?? "");
        command.Parameters.AddWithValue("@headline", alert.Headline ?? "");
        command.Parameters.AddWithValue("@found_at", alert.FoundUtc.ToString("O"));
        command.Parameters.AddWithValue("@notified", alert.Notified ? 1 : 0);
        command.Parameters.AddWithValue("@read", alert.Read ? 1 : 0);
        command.Parameters.AddWithValue("@dismissed", alert.Dismissed ? 1 : 0);
    }

    private static DealAlert ReadAlert(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        WatchId = reader.GetInt64(1),
        WatchName = reader.GetString(2),
        ItemKey = reader.GetString(3),
        Title = reader.GetString(4),
        Url = reader.GetString(5),
        ImageUrl = reader.GetString(6),
        Source = reader.GetString(7),
        SourceLabel = reader.GetString(8),
        Location = reader.GetString(9),
        DistanceMiles = reader.IsDBNull(10) ? null : reader.GetDouble(10),
        CategoryLabel = reader.GetString(11),
        LocalAsk = reader.GetDecimal(12),
        ResalePrice = reader.IsDBNull(13) ? null : reader.GetDecimal(13),
        NetProfit = reader.IsDBNull(14) ? null : reader.GetDecimal(14),
        RoiPercent = reader.IsDBNull(15) ? null : reader.GetDecimal(15),
        MarginPercent = reader.IsDBNull(16) ? null : reader.GetDecimal(16),
        MaxBuyPrice = reader.IsDBNull(17) ? null : reader.GetDecimal(17),
        DaysToCash = reader.IsDBNull(18) ? null : reader.GetInt32(18),
        CompCount = reader.GetInt32(19),
        EvidenceTier = reader.GetString(20),
        EvidenceNote = reader.GetString(21),
        Verdict = reader.GetString(22),
        Headline = reader.GetString(23),
        FoundUtc = ReadDate(reader, 24) ?? DateTimeOffset.MinValue,
        Notified = reader.GetInt32(25) == 1,
        Read = reader.GetInt32(26) == 1,
        Dismissed = reader.GetInt32(27) == 1,
    };

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
    {
        var raw = reader.GetString(ordinal);
        return DateTimeOffset.TryParse(raw, out var value) ? value : null;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS radar_watches (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL DEFAULT '',
                query TEXT NOT NULL DEFAULT '',
                category_id TEXT NOT NULL DEFAULT 'anything',
                zip TEXT NOT NULL DEFAULT '',
                radius_miles INTEGER NOT NULL DEFAULT 40,
                sources TEXT NOT NULL DEFAULT '',
                craigslist_site TEXT NOT NULL DEFAULT '',
                min_net_profit NUMERIC NOT NULL DEFAULT 75,
                min_roi_percent NUMERIC NOT NULL DEFAULT 40,
                max_ask NUMERIC NOT NULL DEFAULT 0,
                max_distance_miles REAL NOT NULL DEFAULT 0,
                require_confident INTEGER NOT NULL DEFAULT 1,
                interval_minutes INTEGER NOT NULL DEFAULT 180,
                enabled INTEGER NOT NULL DEFAULT 1,
                notify_desktop INTEGER NOT NULL DEFAULT 1,
                created_at TEXT NOT NULL DEFAULT '',
                last_run_at TEXT NOT NULL DEFAULT '',
                next_run_at TEXT NOT NULL DEFAULT '',
                last_status TEXT NOT NULL DEFAULT 'never_run',
                last_note TEXT NOT NULL DEFAULT '',
                last_scanned_count INTEGER NOT NULL DEFAULT 0,
                last_match_count INTEGER NOT NULL DEFAULT 0,
                total_alert_count INTEGER NOT NULL DEFAULT 0,
                total_profit_found NUMERIC NOT NULL DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS radar_alerts (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                watch_id INTEGER NOT NULL,
                watch_name TEXT NOT NULL DEFAULT '',
                item_key TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL DEFAULT '',
                image_url TEXT NOT NULL DEFAULT '',
                source TEXT NOT NULL DEFAULT '',
                source_label TEXT NOT NULL DEFAULT '',
                location TEXT NOT NULL DEFAULT '',
                distance_miles REAL NULL,
                category_label TEXT NOT NULL DEFAULT '',
                local_ask NUMERIC NOT NULL DEFAULT 0,
                resale_price NUMERIC NULL,
                net_profit NUMERIC NULL,
                roi_percent NUMERIC NULL,
                margin_percent NUMERIC NULL,
                max_buy_price NUMERIC NULL,
                days_to_cash INTEGER NULL,
                comp_count INTEGER NOT NULL DEFAULT 0,
                evidence_tier TEXT NOT NULL DEFAULT '',
                evidence_note TEXT NOT NULL DEFAULT '',
                verdict TEXT NOT NULL DEFAULT '',
                headline TEXT NOT NULL DEFAULT '',
                found_at TEXT NOT NULL DEFAULT '',
                notified INTEGER NOT NULL DEFAULT 0,
                read INTEGER NOT NULL DEFAULT 0,
                dismissed INTEGER NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS ix_radar_alerts_watch ON radar_alerts(watch_id);
            CREATE INDEX IF NOT EXISTS ix_radar_alerts_unread ON radar_alerts(read, dismissed);

            -- The memory, kept apart from the feed on purpose: pruning the list of finds must never
            -- make a listing that is still up look new again. See the class remarks.
            CREATE TABLE IF NOT EXISTS radar_seen (
                watch_id INTEGER NOT NULL,
                item_key TEXT NOT NULL,
                seen_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (watch_id, item_key)
            );

            CREATE TABLE IF NOT EXISTS radar_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                enabled INTEGER NOT NULL DEFAULT 0,
                quiet_enabled INTEGER NOT NULL DEFAULT 1,
                quiet_from_hour INTEGER NOT NULL DEFAULT 23,
                quiet_to_hour INTEGER NOT NULL DEFAULT 7,
                desktop_notifications INTEGER NOT NULL DEFAULT 1
            );

            -- Off until the seller turns it on. A feature that starts reading other people's sites
            -- on its own the first time the app launches is not one this app is going to ship.
            INSERT OR IGNORE INTO radar_settings (id, enabled) VALUES (1, 0);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
