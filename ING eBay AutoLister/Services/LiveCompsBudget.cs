using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ING_eBay_AutoLister.Services;

/// <summary>Where one account stands against today's live-lookup allowance.</summary>
public sealed record LiveCompsAllowance
{
    /// <summary>False when this deployment rations nothing. Then every other field is noise.</summary>
    public bool Enforced { get; init; }

    /// <summary>Live lookups allowed per account per UTC day.</summary>
    public int Limit { get; init; }

    /// <summary>Lookups this account has spent today.</summary>
    public int Used { get; init; }

    /// <summary>The instant the count goes back to zero — the next UTC midnight.</summary>
    public DateTimeOffset ResetsAt { get; init; }

    /// <summary>True when the call this was asked about may go ahead.</summary>
    public bool Allowed { get; init; }

    public int? Remaining => Enforced ? Math.Max(0, Limit - Used) : null;
}

/// <summary>One call that was spent, kept so the owner can account for the budget.</summary>
public sealed record LiveCompsCall(
    long UserId, string Query, string Outcome, int RowsFound, int RowsNew, int HttpStatus, string At);

/// <summary>
/// The meter on a paid, finite, non-refilling 50,000 API calls — and the record of where they went.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the day is a stored string, and why the count is not reconstructed.</b> Both copied from
/// <see cref="AiUsageStore"/>, which solved the same problem for the owner's Anthropic key: the row
/// key is the UTC date, so a rollover is a different row rather than a sweep that has to run, and
/// yesterday's rows stay readable as a record of what was spent.
/// </para>
/// <para>
/// <b>Two tables, not one.</b> <c>live_comps_usage</c> is the allowance and has to be cheap to
/// check and impossible to race. <c>live_comps_calls</c> is the audit trail — every call, whether
/// it answered, errored or came back empty — and it is also what makes the 24-hour cache possible:
/// "have we already asked about this model today" is one indexed query against it, and it is the
/// single biggest saving available on a budget that does not refill.
/// </para>
/// </remarks>
public sealed class LiveCompsUsageStore
{
    private readonly string _databasePath;

    /// <summary>The tables live in the app's one SQLite file, like every other store.</summary>
    public LiveCompsUsageStore(ListingDatabase database) : this(database.DatabasePath) { }

    public LiveCompsUsageStore(string databasePath)
    {
        _databasePath = databasePath;
        Initialize();
    }

    /// <summary>The row key for an instant: its UTC calendar date.</summary>
    public static string DayOf(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The next UTC midnight after <paramref name="moment"/> — when the count starts again.</summary>
    public static DateTimeOffset ResetAfter(DateTimeOffset moment) =>
        new(moment.UtcDateTime.Date.AddDays(1), TimeSpan.Zero);

    /// <summary>The cache key for a search term: trimmed and case-folded, nothing else.</summary>
    /// <remarks>
    /// Deliberately not a normalising parse. Two sellers typing the same model with different
    /// capitalisation is the case worth catching; guessing that "s19 pro" and "antminer s19 pro"
    /// are the same query is how a cache serves the wrong comps.
    /// </remarks>
    public static string KeyFor(string query) => (query ?? "").Trim().ToLowerInvariant();

    /// <summary>What <paramref name="userId"/> has already spent on <paramref name="day"/>.</summary>
    public int Used(long userId, string day)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT used FROM live_comps_usage WHERE user_id = @user_id AND day = @day;";
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@day", day);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Takes one lookup from <paramref name="userId"/>'s allowance, or returns null when they were
    /// already at <paramref name="limit"/> and nothing was taken.
    /// </summary>
    /// <remarks>
    /// One statement, so the read and the increment cannot be separated — the same upsert, and the
    /// same <c>BEGIN IMMEDIATE</c>, as <see cref="AiUsageStore.TryConsume"/>. A check-then-increment
    /// in C# lets two requests arriving together both see nine and both spend the tenth, and on this
    /// budget every one of those is real money that does not come back.
    /// </remarks>
    public int? TryConsume(long userId, string day, int limit, DateTimeOffset now)
    {
        if (limit <= 0) return null;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO live_comps_usage (user_id, day, used, first_at, last_at)
            VALUES (@user_id, @day, 1, @now, @now)
            ON CONFLICT(user_id, day) DO UPDATE SET
                used    = used + 1,
                last_at = @now
            WHERE used < @limit
            RETURNING used;
            """;
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@day", day);
        command.Parameters.AddWithValue("@limit", limit);
        command.Parameters.AddWithValue("@now", now.ToString("O"));

        var value = command.ExecuteScalar();
        transaction.Commit();

        return value is null or DBNull ? null : Convert.ToInt32(value);
    }

    /// <summary>Writes one call to the audit trail. Called for every call, including failed ones.</summary>
    public void RecordCall(long userId, string query, string outcome, int rowsFound, int rowsNew,
                           int httpStatus, DateTimeOffset at)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO live_comps_calls (user_id, query, outcome, rows_found, rows_new, http_status, at)
            VALUES (@user_id, @query, @outcome, @rows_found, @rows_new, @http_status, @at);
            """;
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@query", KeyFor(query));
        command.Parameters.AddWithValue("@outcome", outcome);
        command.Parameters.AddWithValue("@rows_found", rowsFound);
        command.Parameters.AddWithValue("@rows_new", rowsNew);
        command.Parameters.AddWithValue("@http_status", httpStatus);
        command.Parameters.AddWithValue("@at", at.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// When this model was last actually asked about and answered, or null if never.
    /// </summary>
    /// <remarks>
    /// Only an answer counts — <c>ok</c> or <c>empty</c>. Suppressing a retry after an API error
    /// would leave a seller unable to look up a model over a failure that is usually gone in
    /// seconds, and no call was spent gathering rows in that case anyway.
    /// </remarks>
    public (DateTimeOffset At, int RowsFound)? LastAnswer(string query)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        // Ordered by the row id rather than by the timestamp: same answer, and it reads straight
        // off the primary key instead of sorting text dates.
        command.CommandText = """
            SELECT at, rows_found FROM live_comps_calls
            WHERE query = @query AND outcome IN ('ok', 'empty')
            ORDER BY id DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@query", KeyFor(query));

        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;

        return DateTimeOffset.TryParse(reader.GetString(0),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? (parsed, reader.GetInt32(1))
            : null;
    }

    /// <summary>How many calls have been spent since <paramref name="since"/>. What the bill is.</summary>
    public int CallsSince(DateTimeOffset since)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM live_comps_calls WHERE at >= @since;";
        command.Parameters.AddWithValue("@since", since.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar());
    }

    /// <summary>The most recent calls, newest first — the owner's own reading of where it went.</summary>
    public List<LiveCompsCall> RecentCalls(int limit = 50)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, query, outcome, rows_found, rows_new, http_status, at
            FROM live_comps_calls
            ORDER BY id DESC
            LIMIT @limit;
            """;
        command.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var rows = new List<LiveCompsCall>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new LiveCompsCall(
                reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6)));

        return rows;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS live_comps_usage (
                -- Whose allowance. 0 on a desktop database, where there is one seller.
                user_id INTEGER NOT NULL,
                -- The UTC calendar date, so a new day is a new row rather than a scheduled reset.
                day TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0,
                first_at TEXT NOT NULL DEFAULT '',
                last_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (user_id, day)
            );

            CREATE INDEX IF NOT EXISTS ix_live_comps_usage_day ON live_comps_usage(day);

            CREATE TABLE IF NOT EXISTS live_comps_calls (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                user_id INTEGER NOT NULL,
                -- Case-folded, so the cache probe below is a straight equality match.
                query TEXT NOT NULL,
                outcome TEXT NOT NULL,
                rows_found INTEGER NOT NULL DEFAULT 0,
                rows_new INTEGER NOT NULL DEFAULT 0,
                http_status INTEGER NOT NULL DEFAULT 0,
                at TEXT NOT NULL
            );

            -- The 24-hour cache probe: query first, because that is what it selects on.
            CREATE INDEX IF NOT EXISTS ix_live_comps_calls_query ON live_comps_calls(query, at);
            CREATE INDEX IF NOT EXISTS ix_live_comps_calls_at ON live_comps_calls(at);
            """;
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = _databasePath }.ToString());
        connection.Open();
        return connection;
    }
}

/// <summary>
/// The thing that stands between a request and the owner's OpenWebNinja budget.
/// </summary>
/// <remarks>
/// <para>
/// 50,000 calls, paid for, shared with the bulk collector, and they do not refill. That is not a
/// rate limit to be polite about — it is a quantity of a thing the owner bought, and a loop in a
/// browser tab can spend a month of it in an afternoon. So nothing calls
/// <see cref="OpenWebNinjaClient"/> without coming through here first, and this is server-side by
/// construction: the UI cannot opt out of it because the UI never sees it.
/// </para>
/// <para>
/// <b>Three defences, in the order they cost least.</b> The kill switch answers without touching
/// the database. The cache answers with one indexed query and is where most of the saving actually
/// comes from — a seller comparing two similar units retypes near-identical queries all day. Only
/// what survives both reaches the per-account daily cap, which is the backstop for a runaway.
/// </para>
/// <para>
/// <b>A call counts when it is made, not when it succeeds.</b> The API bills the attempt, so an
/// error still costs the owner a call and must still cost the account its allowance — the same rule
/// <see cref="AiQuotaGate"/> applies to Anthropic, for the same reason.
/// </para>
/// </remarks>
public sealed class LiveCompsBudget
{
    private readonly LiveCompsUsageStore? _usage;
    private readonly UserScope _scope;
    private readonly int _dailyLimit;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// How long an answer about a model stands before it is worth paying to ask again.
    /// </summary>
    /// <remarks>
    /// Sold history is a record of things that have already happened; a day of it is a rounding
    /// error against a 90-day window, and the stored rows are the same rows a fresh call would
    /// mostly return. This is the single biggest saving available on the budget and it costs one
    /// query to check.
    /// </remarks>
    public static readonly TimeSpan CacheFor = TimeSpan.FromHours(24);

    /// <summary>A budget that refuses everything: the kill switch, or a deployment with no key.</summary>
    public static LiveCompsBudget Off { get; } = new(null, UserScope.Desktop, 0, enabled: false);

    /// <param name="usage">Where the counts and the audit trail live, or null for no live lookups.</param>
    /// <param name="scope">Who is asking — one seller on the desktop, the signed-in user on a server.</param>
    /// <param name="dailyLimit">Lookups per account per UTC day. Zero or less means uncapped.</param>
    /// <param name="enabled">The kill switch. False turns live lookups off entirely.</param>
    /// <param name="clock">The current time, injectable so a test can cross midnight without waiting.</param>
    public LiveCompsBudget(LiveCompsUsageStore? usage, UserScope scope, int dailyLimit,
                           bool enabled = true, Func<DateTimeOffset>? clock = null)
    {
        _usage      = usage;
        _scope      = scope;
        _dailyLimit = dailyLimit;
        Enabled     = enabled && usage is not null;
        _now        = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>False when live lookups are switched off for everybody. Falls back to stored comps.</summary>
    public bool Enabled { get; }

    /// <summary>True when a per-account daily cap is actually being applied.</summary>
    public bool Enforced => Enabled && _dailyLimit > 0;

    /// <summary>The cap, for the sentence a refused seller reads.</summary>
    public int DailyLimit => _dailyLimit;

    /// <summary>Now, through the injected clock — so callers stamp their rows with the same time.</summary>
    public DateTimeOffset Now => _now();

    /// <summary>Where the account making this request stands. Reads, never spends.</summary>
    public LiveCompsAllowance Status()
    {
        var now = _now();
        if (!Enforced)
            return new LiveCompsAllowance { Enforced = false, Allowed = Enabled, ResetsAt = LiveCompsUsageStore.ResetAfter(now) };

        var used = _scope.OwnerId is { } userId ? _usage!.Used(userId, LiveCompsUsageStore.DayOf(now)) : 0;

        return new LiveCompsAllowance
        {
            Enforced = true,
            Limit    = _dailyLimit,
            Used     = used,
            ResetsAt = LiveCompsUsageStore.ResetAfter(now),
            Allowed  = used < _dailyLimit,
        };
    }

    /// <summary>
    /// When this model was last answered for and how many rows that answer held, or null. The
    /// caller serves the stored rows instead of spending a call when this is inside
    /// <see cref="CacheFor"/>.
    /// </summary>
    public (DateTimeOffset At, int RowsFound)? LastAnswer(string query) =>
        _usage?.LastAnswer(query);

    /// <summary>True when <paramref name="query"/> was answered recently enough to serve from store.</summary>
    public bool IsCached(string query) =>
        LastAnswer(query) is { } last && _now() - last.At < CacheFor;

    /// <summary>
    /// Takes one call from the allowance of whoever is asking, and says whether it may be made.
    /// </summary>
    /// <remarks>
    /// Refuses rather than throws: every caller's next move is the same — price from stored comps —
    /// and the allowance is part of the answer the progress panel reports either way.
    /// </remarks>
    public LiveCompsAllowance TryReserve()
    {
        var now = _now();

        if (!Enabled)
            return new LiveCompsAllowance { Enforced = false, Allowed = false, ResetsAt = LiveCompsUsageStore.ResetAfter(now) };

        if (!Enforced)
            return new LiveCompsAllowance { Enforced = false, Allowed = true, ResetsAt = LiveCompsUsageStore.ResetAfter(now) };

        // Nobody signed in: refused. Background work has no account to charge, and picking one is
        // how a loop with no owner spends a budget nobody notices. Same rule as AiQuotaGate.
        if (_scope.OwnerId is not { } userId)
            return new LiveCompsAllowance
            {
                Enforced = true, Limit = _dailyLimit, Used = _dailyLimit, Allowed = false,
                ResetsAt = LiveCompsUsageStore.ResetAfter(now),
            };

        var day  = LiveCompsUsageStore.DayOf(now);
        var used = _usage!.TryConsume(userId, day, _dailyLimit, now);

        return new LiveCompsAllowance
        {
            Enforced = true,
            Limit    = _dailyLimit,
            Used     = used ?? _usage.Used(userId, day),
            Allowed  = used is not null,
            ResetsAt = LiveCompsUsageStore.ResetAfter(now),
        };
    }

    /// <summary>
    /// Who is asking, resolved now. Callers that finish their work on a background task must read
    /// this while the request is still alive: on the hosted build the answer comes from the
    /// <c>HttpContext</c>, and a task that outlives the response would find nobody there and file
    /// its spend against the desktop owner.
    /// </summary>
    public long CurrentUserId => _scope.OwnerId ?? UserScope.DesktopOwner;

    /// <summary>Records a call that was made, against the account that made it.</summary>
    public void Record(long userId, string query, string outcome, int rowsFound, int rowsNew, int httpStatus) =>
        _usage?.RecordCall(userId, query, outcome, rowsFound, rowsNew, httpStatus, _now());
}

/// <summary>
/// How the live-lookup budget is configured, and the one line in Program.cs that turns it on.
/// </summary>
public static class LiveComps
{
    /// <summary>The kill switch. Set to <c>false</c> and every lookup serves stored comps instead.</summary>
    public const string EnabledSetting = "LiveComps:Enabled";

    /// <summary>Live lookups per account per UTC day. <c>LiveComps__DailyLookupLimit</c> in an environment.</summary>
    public const string DailyLimitSetting = "LiveComps:DailyLookupLimit";

    /// <summary>
    /// Ten a day is enough to price a real session's worth of inventory and nowhere near enough for
    /// a loop to matter: at the worst case of every account spending all ten, the 50,000 lasts
    /// years rather than a weekend.
    /// </summary>
    public const int DefaultDailyLimit = 10;

    /// <summary>The configured cap, or <see cref="DefaultDailyLimit"/> when nothing usable is set.</summary>
    /// <remarks>
    /// A blank or unparseable value is NOT read as "uncapped" — a typo in an environment variable
    /// must not be the thing that opens the tab. Zero or less, deliberately typed, does mean uncapped.
    /// </remarks>
    public static int LimitFrom(IConfiguration configuration) =>
        int.TryParse(configuration[DailyLimitSetting], System.Globalization.NumberStyles.Integer,
                     System.Globalization.CultureInfo.InvariantCulture, out var limit)
            ? limit
            : DefaultDailyLimit;

    /// <summary>Whether live lookups are switched on at all. On unless the owner says otherwise.</summary>
    public static bool EnabledFrom(IConfiguration configuration) =>
        !bool.TryParse(configuration[EnabledSetting], out var enabled) || enabled;

    /// <summary>
    /// Registers the live-lookup budget. Metered on BOTH builds — unlike the AI quota, the resource
    /// here is the owner's either way, because the desktop app calls the same paid API against the
    /// same 50,000.
    /// </summary>
    public static void AddLiveComps(WebApplicationBuilder builder)
    {
        var limit   = LimitFrom(builder.Configuration);
        var enabled = EnabledFrom(builder.Configuration);

        if (!enabled)
        {
            builder.Services.AddSingleton(LiveCompsBudget.Off);
            return;
        }

        // TryAdd, so a test can point the tables at a scratch database. Same pattern as the users
        // table in HostedAuth.AddAccounts, and for the same reason.
        builder.Services.TryAddSingleton(sp => new LiveCompsUsageStore(sp.GetRequiredService<ListingDatabase>()));

        builder.Services.AddSingleton(sp => new LiveCompsBudget(
            sp.GetRequiredService<LiveCompsUsageStore>(),
            sp.GetRequiredService<UserScope>(),
            limit));
    }
}
