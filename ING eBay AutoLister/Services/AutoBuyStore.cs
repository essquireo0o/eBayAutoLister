using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Where Auto-Buy keeps its rules, its money, and its ledger — the app's own SQLite database,
/// beside <see cref="DealRadarStore"/>.
/// </summary>
/// <remarks>
/// <para>Four tables. <c>autobuy_rules</c> is the saved rules. <c>autobuy_executions</c> is the
/// ledger — every decision, simulated or real, kept for good, because a feature that spends money
/// on its own owes the seller a complete record of what it did and why. <c>autobuy_seen</c> is the
/// memory that stops a rule ever acting on the same eBay item twice. <c>autobuy_settings</c> is the
/// single row of master switches and global caps.</para>
/// <para>The spend counters live here and only move on a real, placed buy — <see cref="RecordBuy"/>
/// is the one method that touches money, and it moves the rule's spend, the global spend, and the
/// per-day count in a single transaction so a crash can't bank a purchase against one counter and
/// not the others.</para>
/// </remarks>
public sealed class AutoBuyStore
{
    /// <summary>Rows kept in the ledger view. The full history stays in the table; this bounds the read.</summary>
    public const int MaxRecentReturned = 100;

    /// <summary>The most rules that can run at once — every one is another search on a timer.</summary>
    public const int MaxRules = 20;

    /// <summary>A hard ceiling on any single item price a rule may name, whatever the seller types.</summary>
    public const decimal MaxItemPriceCeiling = 10_000m;

    private readonly string _databasePath;
    private readonly object _writeLock = new();

    public AutoBuyStore(ListingDatabase database) : this(database.DatabasePath) { }

    public AutoBuyStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    // ── Rules ───────────────────────────────────────────────────────────────

    public List<AutoBuyRule> ListRules()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {RuleColumns} FROM autobuy_rules ORDER BY id;";

        var rows = new List<AutoBuyRule>();
        using var reader = command.ExecuteReader();
        while (reader.Read()) rows.Add(ReadRule(reader));
        return rows;
    }

    public AutoBuyRule? GetRule(long id) => ListRules().FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Creates a rule, or applies a partial edit. Absent fields are left alone so the pause toggle
    /// can post <c>{ id, enabled }</c> without erasing the ceilings the seller set. Throws with a
    /// sentence the seller can act on when the rule would be unsafe to save.
    /// </summary>
    public AutoBuyRule SaveRule(AutoBuyRuleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        lock (_writeLock)
        {
            var existing = request.Id is > 0 ? GetRule(request.Id.Value) : null;
            if (request.Id is > 0 && existing is null)
                throw new InvalidOperationException("That rule no longer exists — it may have been deleted in another tab.");

            if (existing is null && ListRules().Count >= MaxRules)
                throw new InvalidOperationException(
                    $"You can run {MaxRules} auto-buy rules at once. Delete or pause one to add another.");

            var rule = existing ?? new AutoBuyRule { CreatedUtc = DateTimeOffset.UtcNow };

            if (request.Query is not null) rule.Query = request.Query.Trim();
            if (request.Name is not null) rule.Name = request.Name.Trim();
            if (request.Mode is not null) rule.Mode = ParseMode(request.Mode);
            if (request.Condition is not null) rule.Condition = request.Condition.Trim().ToUpperInvariant();
            if (request.MaxItemPrice is { } max) rule.MaxItemPrice = Math.Clamp(max, 0m, MaxItemPriceCeiling);
            if (request.OfferOrBidPrice is { } offer) rule.OfferOrBidPrice = Math.Max(0m, offer);
            if (request.ExcludeKeywords is not null) rule.ExcludeKeywords = request.ExcludeKeywords.Trim();
            if (request.RequiredKeywords is not null) rule.RequiredKeywords = request.RequiredKeywords.Trim();
            if (request.MinItemPrice is { } floor) rule.MinItemPrice = Math.Max(0m, floor);
            if (request.MinSellerFeedback is { } fb) rule.MinSellerFeedback = Math.Max(0, fb);
            if (request.BudgetCap is { } budget) rule.BudgetCap = Math.Max(0m, budget);
            if (request.MaxBuys is { } buys) rule.MaxBuys = Math.Max(0, buys);
            if (request.IntervalMinutes is { } interval) rule.IntervalMinutes = Math.Clamp(interval, 5, 24 * 60);
            if (request.Enabled is { } enabled) rule.Enabled = enabled;
            // A NEW rule that says nothing about enabled is on. The console's form never sends the
            // flag, so every rule used to save paused and the scheduler ignored it - "I saved it and
            // nothing happens" (2026-09-18 diagnostic run). An edit still leaves the flag alone.
            else if (existing is null) rule.Enabled = true;

            if (rule.Name.Length == 0) rule.Name = rule.Query.Length > 0 ? rule.Query : "Untitled rule";

            Validate(rule);

            // A new rule, and one just un-paused, run on the next tick rather than a quarter-hour
            // from now: pressing Save and seeing nothing happen is how a seller thinks it is broken.
            if (existing is null || request.Enabled == true) rule.NextRunUtc = null;

            return Upsert(rule);
        }
    }

    /// <summary>The safety floor for a rule. These are refusals, not clamps: a seller who set a
    /// ceiling of zero wants to know it saves nothing, not to have a number invented for them.</summary>
    public static void Validate(AutoBuyRule rule)
    {
        if (rule.Query.Trim().Length == 0)
            throw new InvalidOperationException("An auto-buy rule needs something to search for.");
        if (rule.MaxItemPrice <= 0m)
            throw new InvalidOperationException("Set the most you will pay for one item. A rule with no price ceiling cannot be saved.");
        if (rule.BudgetCap <= 0m)
            throw new InvalidOperationException("Set a total budget for this rule. A rule with no budget cannot be saved.");
        if (rule.BudgetCap < rule.MaxItemPrice)
            throw new InvalidOperationException("The rule's budget is smaller than one item's ceiling, so it could never buy anything. Raise the budget or lower the ceiling.");
        if (rule.MinItemPrice > 0m && rule.MinItemPrice >= rule.MaxItemPrice)
            throw new InvalidOperationException("The price floor is at or above the ceiling, so nothing could ever match. Lower the floor or raise the ceiling.");
        if (rule.Mode is AutoBuyMode.BestOffer or AutoBuyMode.AuctionBid)
        {
            if (rule.OfferOrBidPrice <= 0m)
                throw new InvalidOperationException(rule.Mode == AutoBuyMode.BestOffer
                    ? "Set the amount to offer."
                    : "Set the maximum bid.");
            if (rule.OfferOrBidPrice > rule.MaxItemPrice)
                throw new InvalidOperationException("The offer or bid is above the per-item ceiling. Lower it, or raise the ceiling.");
        }
    }

    public AutoBuyRule Upsert(AutoBuyRule rule)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            if (rule.Id > 0)
            {
                command.CommandText = """
                    UPDATE autobuy_rules SET
                        name=@name, query=@query, mode=@mode, condition=@condition,
                        max_item_price=@max, offer_bid_price=@offer, exclude_keywords=@exclude,
                        required_keywords=@required, min_item_price=@floor,
                        min_seller_feedback=@fb, budget_cap=@budget, max_buys=@maxbuys,
                        spent=@spent, buys=@buys, enabled=@enabled, interval_minutes=@interval,
                        last_run_at=@lastrun, next_run_at=@nextrun, last_status=@laststatus, last_note=@lastnote
                    WHERE id=@id;
                    """;
                command.Parameters.AddWithValue("@id", rule.Id);
            }
            else
            {
                command.CommandText = """
                    INSERT INTO autobuy_rules
                        (name, query, mode, condition, max_item_price, offer_bid_price, exclude_keywords,
                         required_keywords, min_item_price,
                         min_seller_feedback, budget_cap, max_buys, spent, buys, enabled, interval_minutes,
                         created_at, last_run_at, next_run_at, last_status, last_note)
                    VALUES
                        (@name, @query, @mode, @condition, @max, @offer, @exclude,
                         @required, @floor,
                         @fb, @budget, @maxbuys, @spent, @buys, @enabled, @interval,
                         @created, @lastrun, @nextrun, @laststatus, @lastnote);
                    SELECT last_insert_rowid();
                    """;
                command.Parameters.AddWithValue("@created", Iso(rule.CreatedUtc));
            }

            command.Parameters.AddWithValue("@required", rule.RequiredKeywords);
            command.Parameters.AddWithValue("@floor", rule.MinItemPrice);
            command.Parameters.AddWithValue("@lastnote", rule.LastNote);
            command.Parameters.AddWithValue("@name", rule.Name);
            command.Parameters.AddWithValue("@query", rule.Query);
            command.Parameters.AddWithValue("@mode", (int)rule.Mode);
            command.Parameters.AddWithValue("@condition", rule.Condition);
            command.Parameters.AddWithValue("@max", rule.MaxItemPrice);
            command.Parameters.AddWithValue("@offer", rule.OfferOrBidPrice);
            command.Parameters.AddWithValue("@exclude", rule.ExcludeKeywords);
            command.Parameters.AddWithValue("@fb", rule.MinSellerFeedback);
            command.Parameters.AddWithValue("@budget", rule.BudgetCap);
            command.Parameters.AddWithValue("@maxbuys", rule.MaxBuys);
            command.Parameters.AddWithValue("@spent", rule.Spent);
            command.Parameters.AddWithValue("@buys", rule.Buys);
            command.Parameters.AddWithValue("@enabled", rule.Enabled ? 1 : 0);
            command.Parameters.AddWithValue("@interval", rule.IntervalMinutes);
            command.Parameters.AddWithValue("@lastrun", NullableIso(rule.LastRunUtc));
            command.Parameters.AddWithValue("@nextrun", NullableIso(rule.NextRunUtc));
            command.Parameters.AddWithValue("@laststatus", rule.LastStatus);

            if (rule.Id > 0) command.ExecuteNonQuery();
            else rule.Id = (long)(command.ExecuteScalar() ?? 0L);

            return rule;
        }
    }

    public bool DeleteRule(long id)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                DELETE FROM autobuy_seen WHERE rule_id=@id;
                DELETE FROM autobuy_rules WHERE id=@id;
                """;
            command.Parameters.AddWithValue("@id", id);
            return command.ExecuteNonQuery() > 0;
        }
    }

    /// <summary>Moves a rule's schedule forward and records what the run did. Never touches money.</summary>
    public void RecordRun(long ruleId, string status, DateTimeOffset ranAt, int intervalMinutes, string note = "")
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE autobuy_rules
                SET last_run_at=@ran, next_run_at=@next, last_status=@status, last_note=@note
                WHERE id=@id;
                """;
            command.Parameters.AddWithValue("@id", ruleId);
            command.Parameters.AddWithValue("@ran", Iso(ranAt));
            command.Parameters.AddWithValue("@next", Iso(ranAt.AddMinutes(Math.Clamp(intervalMinutes, 5, 24 * 60))));
            command.Parameters.AddWithValue("@status", status ?? "");
            command.Parameters.AddWithValue("@note", note ?? "");
            command.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Takes a rule off the schedule with a reason on it. Used when eBay says the application is not
    /// allowed to place offers at all: every further run would fail the same way and burn the rule's
    /// matches as FAILED rows, so the rule rests until the seller reads the note.
    /// </summary>
    public void PauseRule(long ruleId, string note)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE autobuy_rules SET enabled=0, last_note=@note WHERE id=@id;";
            command.Parameters.AddWithValue("@id", ruleId);
            command.Parameters.AddWithValue("@note", note ?? "");
            command.ExecuteNonQuery();
        }
    }

    // ── Seen memory ─────────────────────────────────────────────────────────

    /// <summary>Remembers this rule acted on this item. Returns false if it already had — a rule
    /// must never buy the same listing twice, and this is the gate that guarantees it.</summary>
    public bool MarkSeen(long ruleId, string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return false;
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO autobuy_seen (rule_id, item_id, seen_at)
                VALUES (@rule, @item, @at);
                """;
            command.Parameters.AddWithValue("@rule", ruleId);
            command.Parameters.AddWithValue("@item", itemId);
            command.Parameters.AddWithValue("@at", Iso(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() > 0;
        }
    }

    public bool HasSeen(long ruleId, string itemId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM autobuy_seen WHERE rule_id=@rule AND item_id=@item LIMIT 1;";
        command.Parameters.AddWithValue("@rule", ruleId);
        command.Parameters.AddWithValue("@item", itemId);
        return command.ExecuteScalar() is not null;
    }

    // ── Ledger ──────────────────────────────────────────────────────────────

    /// <summary>Writes one ledger row. The record of every decision, live or simulated.</summary>
    public AutoBuyExecution RecordExecution(AutoBuyExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO autobuy_executions
                    (rule_id, rule_name, item_id, title, url, mode, price, outcome, detail, created_at)
                VALUES (@rule, @name, @item, @title, @url, @mode, @price, @outcome, @detail, @at);
                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("@rule", execution.RuleId);
            command.Parameters.AddWithValue("@name", execution.RuleName);
            command.Parameters.AddWithValue("@item", execution.ItemId);
            command.Parameters.AddWithValue("@title", execution.Title);
            command.Parameters.AddWithValue("@url", execution.Url);
            command.Parameters.AddWithValue("@mode", (int)execution.Mode);
            command.Parameters.AddWithValue("@price", execution.Price);
            command.Parameters.AddWithValue("@outcome", (int)execution.Outcome);
            command.Parameters.AddWithValue("@detail", execution.Detail);
            command.Parameters.AddWithValue("@at", Iso(execution.CreatedUtc));
            execution.Id = (long)(command.ExecuteScalar() ?? 0L);
            return execution;
        }
    }

    public List<AutoBuyExecution> ListRecent(int limit = 60)
    {
        limit = Math.Clamp(limit, 1, MaxRecentReturned);
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, rule_id, rule_name, item_id, title, url, mode, price, outcome, detail, created_at
            FROM autobuy_executions ORDER BY id DESC LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", limit);

        var rows = new List<AutoBuyExecution>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AutoBuyExecution
            {
                Id = reader.GetInt64(0),
                RuleId = reader.GetInt64(1),
                RuleName = reader.GetString(2),
                ItemId = reader.GetString(3),
                Title = reader.GetString(4),
                Url = reader.GetString(5),
                Mode = (AutoBuyMode)reader.GetInt32(6),
                Price = reader.GetDecimal(7),
                Outcome = (AutoBuyOutcome)reader.GetInt32(8),
                Detail = reader.GetString(9),
                CreatedUtc = ParseIso(reader.GetString(10)),
            });
        }
        return rows;
    }

    // ── Settings and money ──────────────────────────────────────────────────

    public AutoBuySettings GetSettings()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT armed, live_buying, global_budget_cap, global_spent, max_buys_per_day,
                   buys_today, buys_today_date
            FROM autobuy_settings WHERE id=1;
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new AutoBuySettings();

        var settings = new AutoBuySettings
        {
            Armed = reader.GetInt32(0) == 1,
            LiveBuying = reader.GetInt32(1) == 1,
            GlobalBudgetCap = reader.GetDecimal(2),
            GlobalSpent = reader.GetDecimal(3),
            MaxBuysPerDay = reader.GetInt32(4),
            BuysToday = reader.GetInt32(5),
            BuysTodayDateUtc = reader.GetString(6),
        };
        RollDayIfNeeded(settings);
        return settings;
    }

    /// <summary>
    /// Applies the master switches and global caps. Partial: a body that names only
    /// <c>armed</c> flips the arm and leaves the budget as it was. Turning the master arm off is
    /// always allowed; it is the brake and must never be blocked.
    /// </summary>
    /// <remarks>
    /// The two switches are ordered, and this is where the order is enforced rather than in the
    /// page. Live buying is only ever on while the master arm is on: disarming clears it, and a
    /// request to turn live on while disarmed (or in the same body as arming) leaves it off. Before
    /// this, the owner's database sat at <c>armed=0, live_buying=1</c>, which made the next click on
    /// "Master arm" a jump straight to real money with no confirmation (2026-09-18).
    /// </remarks>
    public AutoBuySettings SaveSettings(bool? armed, bool? liveBuying, decimal? globalBudgetCap, int? maxBuysPerDay)
    {
        lock (_writeLock)
        {
            var current = GetSettings();
            var wasArmed = current.Armed;
            if (armed is { } a) current.Armed = a;
            if (liveBuying is { } l) current.LiveBuying = l && current.Armed && wasArmed;
            if (!current.Armed) current.LiveBuying = false;
            if (globalBudgetCap is { } g) current.GlobalBudgetCap = Math.Max(0m, g);
            if (maxBuysPerDay is { } m) current.MaxBuysPerDay = Math.Max(0, m);

            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE autobuy_settings SET
                    armed=@armed, live_buying=@live, global_budget_cap=@budget, max_buys_per_day=@perday
                WHERE id=1;
                """;
            command.Parameters.AddWithValue("@armed", current.Armed ? 1 : 0);
            command.Parameters.AddWithValue("@live", current.LiveBuying ? 1 : 0);
            command.Parameters.AddWithValue("@budget", current.GlobalBudgetCap);
            command.Parameters.AddWithValue("@perday", current.MaxBuysPerDay);
            command.ExecuteNonQuery();
            return GetSettings();
        }
    }

    /// <summary>
    /// The one method that banks a real purchase. In a single transaction it adds the price to the
    /// rule's spend and count, to the global spend, and to today's count — so a crash can never
    /// leave one counter ahead of the others and let the caps be walked past.
    /// </summary>
    public void RecordBuy(long ruleId, decimal price)
    {
        lock (_writeLock)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var rule = connection.CreateCommand())
            {
                rule.Transaction = transaction;
                rule.CommandText = "UPDATE autobuy_rules SET spent=spent+@p, buys=buys+1 WHERE id=@id;";
                rule.Parameters.AddWithValue("@p", price);
                rule.Parameters.AddWithValue("@id", ruleId);
                rule.ExecuteNonQuery();
            }

            var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
            using (var settings = connection.CreateCommand())
            {
                settings.Transaction = transaction;
                // Reset today's count if the stored day is not today, then add this buy — both in
                // the same statement so the roll-over can't race the increment.
                settings.CommandText = """
                    UPDATE autobuy_settings SET
                        global_spent = global_spent + @p,
                        buys_today = CASE WHEN buys_today_date = @today THEN buys_today ELSE 0 END + 1,
                        buys_today_date = @today
                    WHERE id=1;
                    """;
                settings.Parameters.AddWithValue("@p", price);
                settings.Parameters.AddWithValue("@today", today);
                settings.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private void RollDayIfNeeded(AutoBuySettings settings)
    {
        var today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        if (settings.BuysTodayDateUtc == today) return;
        settings.BuysToday = 0;
        settings.BuysTodayDateUtc = today;
    }

    // ── Plumbing ────────────────────────────────────────────────────────────

    private const string RuleColumns =
        "id, name, query, mode, condition, max_item_price, offer_bid_price, exclude_keywords, " +
        "min_seller_feedback, budget_cap, max_buys, spent, buys, enabled, interval_minutes, " +
        "created_at, last_run_at, next_run_at, last_status, required_keywords, min_item_price, last_note";

    private static AutoBuyRule ReadRule(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(0),
        Name = r.GetString(1),
        Query = r.GetString(2),
        Mode = (AutoBuyMode)r.GetInt32(3),
        Condition = r.GetString(4),
        MaxItemPrice = r.GetDecimal(5),
        OfferOrBidPrice = r.GetDecimal(6),
        ExcludeKeywords = r.GetString(7),
        MinSellerFeedback = r.GetInt32(8),
        BudgetCap = r.GetDecimal(9),
        MaxBuys = r.GetInt32(10),
        Spent = r.GetDecimal(11),
        Buys = r.GetInt32(12),
        Enabled = r.GetInt32(13) == 1,
        IntervalMinutes = r.GetInt32(14),
        CreatedUtc = ParseIso(r.GetString(15)),
        LastRunUtc = ParseNullableIso(r.GetString(16)),
        NextRunUtc = ParseNullableIso(r.GetString(17)),
        LastStatus = r.GetString(18),
        RequiredKeywords = r.GetString(19),
        MinItemPrice = r.GetDecimal(20),
        LastNote = r.GetString(21),
    };

    public static AutoBuyMode ParseMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "bestoffer" or "best_offer" or "offer" => AutoBuyMode.BestOffer,
        "auctionbid" or "auction_bid" or "bid" or "auction" => AutoBuyMode.AuctionBid,
        _ => AutoBuyMode.BuyItNow,
    };

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().ToString("O");
    private static string NullableIso(DateTimeOffset? value) => value is { } v ? Iso(v) : "";
    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var v)
            ? v : DateTimeOffset.UtcNow;
    private static DateTimeOffset? ParseNullableIso(string value) =>
        string.IsNullOrEmpty(value) ? null : ParseIso(value);

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS autobuy_rules (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                name TEXT NOT NULL DEFAULT '',
                query TEXT NOT NULL DEFAULT '',
                mode INTEGER NOT NULL DEFAULT 0,
                condition TEXT NOT NULL DEFAULT '',
                max_item_price NUMERIC NOT NULL DEFAULT 0,
                offer_bid_price NUMERIC NOT NULL DEFAULT 0,
                exclude_keywords TEXT NOT NULL DEFAULT '',
                min_seller_feedback INTEGER NOT NULL DEFAULT 0,
                budget_cap NUMERIC NOT NULL DEFAULT 0,
                max_buys INTEGER NOT NULL DEFAULT 0,
                spent NUMERIC NOT NULL DEFAULT 0,
                buys INTEGER NOT NULL DEFAULT 0,
                enabled INTEGER NOT NULL DEFAULT 0,
                interval_minutes INTEGER NOT NULL DEFAULT 15,
                created_at TEXT NOT NULL DEFAULT '',
                last_run_at TEXT NOT NULL DEFAULT '',
                next_run_at TEXT NOT NULL DEFAULT '',
                last_status TEXT NOT NULL DEFAULT 'never_run'
            );

            CREATE TABLE IF NOT EXISTS autobuy_executions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                rule_id INTEGER NOT NULL,
                rule_name TEXT NOT NULL DEFAULT '',
                item_id TEXT NOT NULL DEFAULT '',
                title TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL DEFAULT '',
                mode INTEGER NOT NULL DEFAULT 0,
                price NUMERIC NOT NULL DEFAULT 0,
                outcome INTEGER NOT NULL DEFAULT 0,
                detail TEXT NOT NULL DEFAULT '',
                created_at TEXT NOT NULL DEFAULT ''
            );
            CREATE INDEX IF NOT EXISTS ix_autobuy_exec_rule ON autobuy_executions(rule_id);

            -- The memory that makes a double-buy impossible: one row per (rule, item) ever acted on.
            CREATE TABLE IF NOT EXISTS autobuy_seen (
                rule_id INTEGER NOT NULL,
                item_id TEXT NOT NULL,
                seen_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (rule_id, item_id)
            );

            CREATE TABLE IF NOT EXISTS autobuy_settings (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                armed INTEGER NOT NULL DEFAULT 0,
                live_buying INTEGER NOT NULL DEFAULT 0,
                global_budget_cap NUMERIC NOT NULL DEFAULT 500,
                global_spent NUMERIC NOT NULL DEFAULT 0,
                max_buys_per_day INTEGER NOT NULL DEFAULT 5,
                buys_today INTEGER NOT NULL DEFAULT 0,
                buys_today_date TEXT NOT NULL DEFAULT ''
            );

            -- Disarmed, simulate-only, the first time and every time until the seller says otherwise.
            -- A feature that could spend money on its own the first time the app launches is not one
            -- this app is going to ship.
            INSERT OR IGNORE INTO autobuy_settings (id, armed, live_buying) VALUES (1, 0, 0);
            """;
        command.ExecuteNonQuery();

        // Columns added after the first release. SQLite has no ADD COLUMN IF NOT EXISTS; read the
        // table's columns once and add what is missing, so a database from 2.6.9 opens cleanly.
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cols = connection.CreateCommand())
        {
            cols.CommandText = "PRAGMA table_info(autobuy_rules);";
            using var reader = cols.ExecuteReader();
            while (reader.Read()) have.Add(reader.GetString(1));
        }
        foreach (var (name, ddl) in new[]
        {
            ("required_keywords", "TEXT NOT NULL DEFAULT ''"),
            ("min_item_price",    "NUMERIC NOT NULL DEFAULT 0"),
            ("last_note",         "TEXT NOT NULL DEFAULT ''"),
        })
        {
            if (have.Contains(name)) continue;
            using var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE autobuy_rules ADD COLUMN {name} {ddl};";
            alter.ExecuteNonQuery();
        }

        // The brake the settings row must satisfy from now on (see SaveSettings): live buying
        // cannot be on while disarmed. A database left in that state is corrected on open.
        using var brake = connection.CreateCommand();
        brake.CommandText = "UPDATE autobuy_settings SET live_buying=0 WHERE id=1 AND armed=0 AND live_buying=1;";
        brake.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}
