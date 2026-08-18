using ING_eBay_AutoLister.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How much of today's AI allowance one account has spent, and when the next one starts.
/// </summary>
/// <remarks>
/// Serialised straight to the page by <c>/api/ai-quota</c>, so the field names are the ones the UI
/// reads. <see cref="Enforced"/> is false on the desktop build, where the seller is paying for
/// their own key and there is nothing to ration — the page shows no allowance at all rather than
/// an allowance of infinity.
/// </remarks>
public sealed record AiQuotaStatus
{
    /// <summary>False when this build does not ration AI at all. Then every other field is noise.</summary>
    public bool Enforced { get; init; }

    /// <summary>Generations allowed per account per day.</summary>
    public int Limit { get; init; }

    /// <summary>Generations this account has started today.</summary>
    public int Used { get; init; }

    /// <summary>The instant the count goes back to zero — the next UTC midnight.</summary>
    public DateTimeOffset ResetsAt { get; init; }

    /// <summary>Null rather than a huge number when nothing is being rationed.</summary>
    public int? Remaining => Enforced ? Math.Max(0, Limit - Used) : null;

    /// <summary>True when the next generation would be refused.</summary>
    public bool Exhausted => Enforced && Used >= Limit;
}

/// <summary>
/// Thrown instead of calling Anthropic, when the account asking has no allowance left today.
/// </summary>
/// <remarks>
/// It carries a ready-made <see cref="FailureInfo"/> as well as the numbers, because the thing a
/// person needs on the screen is a sentence and not a status code. <see cref="FailureTranslator"/>
/// passes that failure through untouched, so every endpoint that already reports failures — the
/// <c>Guarded</c> wrapper and the hand-written try/catch paths alike — reports this one in the
/// seller's own language without being told about quotas.
/// </remarks>
public sealed class AiQuotaExceededException(AiQuotaStatus status, FailureInfo failure)
    : Exception(failure.Headline + " — " + failure.WhatHappened)
{
    /// <summary>Where the account stands: the limit, what it used, and when that resets.</summary>
    public AiQuotaStatus Status { get; } = status;

    /// <summary>The failure to report, already written the way the UI renders one.</summary>
    public FailureInfo Failure { get; } = failure;
}

/// <summary>
/// How many AI generations each account has started, per day, in the app's own database.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not a <see cref="UserOwnedTable"/>.</b> Every other per-user table in this app is a seller's
/// own work, filtered through <see cref="UserScope"/> so nobody can read anybody else's. This one
/// is the opposite kind of row: it is the owner's meter on their own API key, the user id is its
/// key rather than a privacy fence, and the owner dashboard has to be able to read all of it — a
/// scoped store would hide from the person paying the bill exactly the numbers they are paying.
/// So the user id is always an explicit argument here, and the endpoints decide whose it is.
/// </para>
/// <para>
/// <b>Why the day is a stored string.</b> The row key is the UTC date, so a rollover is a different
/// key rather than a sweep that has to run — nothing has to be scheduled, nothing resets late
/// because the process was asleep at midnight, and yesterday's rows stay readable as a record of
/// what was spent.
/// </para>
/// </remarks>
public sealed class AiUsageStore
{
    private readonly string _databasePath;

    /// <summary>The table lives in the app's one SQLite file, like every other store.</summary>
    public AiUsageStore(ListingDatabase database) : this(database.DatabasePath) { }

    public AiUsageStore(string databasePath)
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

    /// <summary>What <paramref name="userId"/> has already started on <paramref name="day"/>.</summary>
    public int Used(long userId, string day)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT used FROM ai_usage WHERE user_id = @user_id AND day = @day;";
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@day", day);

        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    /// <summary>
    /// Takes one generation from <paramref name="userId"/>'s allowance for <paramref name="day"/>,
    /// and returns the new count — or null when they were already at <paramref name="limit"/> and
    /// nothing was taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One statement, so the read and the increment cannot be separated. Two requests arriving
    /// together on the last generation of the day is not a hypothetical — the listing editor fires
    /// several AI calls from one screen — and a check-then-increment in C# lets both of them see
    /// nine and both of them spend the tenth. The limit is the <c>WHERE</c> on the upsert, so the
    /// row is left alone when it is already full and the count is never inflated past the limit by
    /// requests that were refused.
    /// </para>
    /// <para>
    /// <b>Why the transaction is explicit, and immediate.</b> One statement is atomic but not
    /// unattended: the upsert reads before it writes, so on its own it takes a shared lock first
    /// and asks to upgrade — and SQLite refuses to wait on an upgrade, because two connections
    /// both waiting to upgrade is a deadlock. It answers <c>SQLITE_BUSY</c> at once instead, past
    /// the busy timeout, and the seller gets "database is locked" where a listing should be.
    /// <c>BEGIN IMMEDIATE</c> takes the write lock up front, which is the one thing SQLite will
    /// wait its busy timeout for. Measured, not assumed: twelve concurrent reservations against a
    /// limit of five fail this way without it.
    /// </para>
    /// </remarks>
    public int? TryConsume(long userId, string day, int limit, DateTimeOffset now)
    {
        if (limit <= 0) return null;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ai_usage (user_id, day, used, first_at, last_at)
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

    /// <summary>
    /// Every account that has spent anything on <paramref name="day"/>, busiest first. What the
    /// owner dashboard reads: on a deployment where one key pays for everybody, "who is spending
    /// it" is the question the bill raises.
    /// </summary>
    public List<AiUsageRow> UsageOn(string day)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT user_id, used, first_at, last_at
            FROM ai_usage
            WHERE day = @day
            ORDER BY used DESC, user_id;
            """;
        command.Parameters.AddWithValue("@day", day);

        var rows = new List<AiUsageRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AiUsageRow(
                reader.GetInt64(0),
                reader.GetInt32(1),
                reader.GetString(2),
                reader.GetString(3)));
        }

        return rows;
    }

    private void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS ai_usage (
                -- Whose allowance. 0 on a desktop database, where nothing is rationed and this
                -- table stays empty.
                user_id INTEGER NOT NULL,
                -- The UTC calendar date, so a new day is a new row rather than a scheduled reset.
                day TEXT NOT NULL,
                used INTEGER NOT NULL DEFAULT 0,
                first_at TEXT NOT NULL DEFAULT '',
                last_at TEXT NOT NULL DEFAULT '',
                PRIMARY KEY (user_id, day)
            );

            CREATE INDEX IF NOT EXISTS ix_ai_usage_day ON ai_usage(day);
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

/// <summary>One account's spend on one day, for the owner's own reading.</summary>
public sealed record AiUsageRow(long UserId, int Used, string FirstAt, string LastAt);

/// <summary>
/// The thing that stands between a request and the owner's Anthropic key.
/// </summary>
/// <remarks>
/// <para>
/// On the hosted trial nobody is asked for an API key: the owner's own key pays for every user's
/// analysis, which is deliberate — see <see cref="ServerCredentials"/> — and which means an
/// unmetered endpoint is an open tab at the owner's expense. One person with a script and a
/// signed-up account can spend more in an afternoon than the trial was ever meant to cost.
/// </para>
/// <para>
/// <b>Where this is enforced, and why there.</b> Not at the endpoints. Eleven of them reach
/// <see cref="ClaudeService"/> today and the twelfth is written next month by somebody who has
/// never read this file; a list of endpoints to guard is a list to forget to add to. This sits
/// inside <c>ClaudeService.CallModelAsync</c>, the one method every AI call in the app already
/// funnels through, so a new endpoint is metered by virtue of having been written — the same
/// closed-by-default reasoning as <see cref="HostedAuth"/>'s fallback policy.
/// </para>
/// <para>
/// <b>A generation counts when it starts, not when it succeeds.</b> Anthropic bills the attempt,
/// so a call that comes back unparseable has still cost the owner money and must still cost the
/// user an allowance. The retries inside <c>ResilientCall</c> are the exception: they are the app's
/// own decision to spend, not the user's, and they sit inside the one reservation.
/// </para>
/// <para>
/// <b>When nobody is signed in.</b> Refused. Background work — the whole-account SEO rewrite runs
/// on a detached <c>Task.Run</c> — has no HttpContext and so no user, and there is no allowance to
/// draw from. That is the hole this would otherwise leave wide open: an unmetered path to the
/// owner's key that any user can start with one button. It breaks nothing that works today, because
/// hosted background jobs already have no eBay account to read — see
/// <see cref="PerUserCredentialsSource"/>.
/// </para>
/// </remarks>
public sealed class AiQuotaGate
{
    private readonly AiUsageStore? _usage;
    private readonly UserScope _scope;
    private readonly int _dailyLimit;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long, bool>? _unlimited;

    /// <summary>
    /// The desktop build, and any hosted deployment whose owner has turned the limit off. Nothing
    /// is counted and nothing is refused: the seller is spending their own key.
    /// </summary>
    public static AiQuotaGate Unmetered { get; } = new(null, UserScope.Desktop, 0);

    /// <param name="usage">Where the counts live, or null for a gate that meters nothing.</param>
    /// <param name="scope">Who is asking. Its <see cref="UserScope.IsPerUser"/> is what decides
    /// whether this build rations at all, so one object answers both questions consistently.</param>
    /// <param name="dailyLimit">Generations per account per UTC day. Zero or less means unmetered.</param>
    /// <param name="clock">The current time, injectable so a test can cross midnight without waiting.</param>
    /// <param name="unlimited">Accounts the daily limit does not apply to — the owner using their
    /// own product on their own key. Answered per call, because it is resolved from an email list
    /// in configuration and the account may not even exist yet when the gate is built.</param>
    public AiQuotaGate(AiUsageStore? usage, UserScope scope, int dailyLimit,
                       Func<DateTimeOffset>? clock = null, Func<long, bool>? unlimited = null)
    {
        _usage      = usage;
        _scope      = scope;
        _dailyLimit = dailyLimit;
        _now        = clock ?? (() => DateTimeOffset.UtcNow);
        _unlimited  = unlimited;
    }

    /// <summary>True when this build counts generations at all.</summary>
    public bool Enforced => _usage is not null && _scope.IsPerUser && _dailyLimit > 0;

    /// <summary>
    /// Where the account making this request stands. Reads, never spends — this is what the page
    /// asks so it can say "3 of 10 left today" before the seller starts something they cannot finish.
    /// </summary>
    public AiQuotaStatus Status()
    {
        var now = _now();
        if (!Enforced)
            return new AiQuotaStatus { Enforced = false, ResetsAt = AiUsageStore.ResetAfter(now) };

        // An unlimited account reads the same answer the desktop build gets: nothing is rationed
        // FOR YOU, so the page draws no allowance at all rather than a meter that never moves.
        if (_scope.OwnerId is { } owner && _unlimited?.Invoke(owner) == true)
            return new AiQuotaStatus { Enforced = false, ResetsAt = AiUsageStore.ResetAfter(now) };

        // No signed-in user: nothing has been spent because nothing may be. Reported as a full
        // allowance rather than an error, since the only caller is a page drawing a number.
        var used = _scope.OwnerId is { } userId ? _usage!.Used(userId, AiUsageStore.DayOf(now)) : 0;

        return new AiQuotaStatus
        {
            Enforced = true,
            Limit    = _dailyLimit,
            Used     = used,
            ResetsAt = AiUsageStore.ResetAfter(now),
        };
    }

    /// <summary>
    /// Takes one generation from the allowance of whoever is making this request, or throws
    /// <see cref="AiQuotaExceededException"/> with the sentence to put on the screen.
    /// </summary>
    /// <param name="operation">
    /// What was being attempted, in the words the rest of the app already uses — "AI listing from
    /// photo". Kept for the technical detail, so a support conversation starts with which feature.
    /// </param>
    public void Reserve(string operation)
    {
        if (!Enforced) return;

        var now = _now();

        if (_scope.OwnerId is not { } userId)
            throw NoUser(operation, now);

        var day = AiUsageStore.DayOf(now);

        // An unlimited account is never refused, but it is still COUNTED — the owner dashboard's
        // whole job is "who is spending the key", and exempt-and-invisible would hide exactly the
        // account most able to spend. Unlimited means the cap is gone, not the meter.
        if (_unlimited?.Invoke(userId) == true)
        {
            _usage!.TryConsume(userId, day, int.MaxValue, now);
            return;
        }

        var used = _usage!.TryConsume(userId, day, _dailyLimit, now);
        if (used is not null) return;

        throw Exhausted(_dailyLimit, _dailyLimit, now);
    }

    /// <summary>
    /// Refuses an account that has nothing left, without taking anything from one that has.
    /// </summary>
    /// <remarks>
    /// For the key check, which is a real Anthropic call — Haiku, one token — and so cannot be left
    /// entirely unmetered on somebody else's key. It is also five or six orders of magnitude cheaper
    /// than a listing generation, so charging it a whole generation would take a tenth of a person's
    /// day for pressing a diagnostic button. The line drawn here is that a diagnostic is free while
    /// you have an allowance and unavailable once you do not. The residual is real and small: an
    /// account with allowance left can repeat the check, at a cost per call that rounds to zero.
    /// </remarks>
    public void EnsureNotExhausted(string operation)
    {
        if (!Enforced) return;

        var now = _now();

        if (_scope.OwnerId is not { } userId)
            throw NoUser(operation, now);

        if (_unlimited?.Invoke(userId) == true) return;

        var used = _usage!.Used(userId, AiUsageStore.DayOf(now));
        if (used < _dailyLimit) return;

        throw Exhausted(used, _dailyLimit, now);
    }

    // ── What the person reads ────────────────────────────────────────────────────────────────

    private static AiQuotaExceededException Exhausted(int used, int limit, DateTimeOffset now)
    {
        var resetsAt = AiUsageStore.ResetAfter(now);
        var wait     = resetsAt - now;

        var status = new AiQuotaStatus
        {
            Enforced = true,
            Limit    = limit,
            Used     = used,
            ResetsAt = resetsAt,
        };

        // Never a bare status code, and never silence. The sentence says three things, because a
        // person who has just been refused needs all three: that there is a daily limit, that they
        // have reached it rather than broken something, and when they can carry on.
        return new AiQuotaExceededException(status, new FailureInfo
        {
            Kind         = FailureKind.AiQuotaExhausted,
            Domain       = FailureDomain.Ai,
            Headline     = $"You have used today's {limit} AI generations",
            WhatHappened = $"This account gets {limit} AI generations a day while the trial runs on the "
                         + $"owner's Anthropic key, and all {limit} have been used.",
            WhatToDo     = $"Your next {limit} become available at {Clock(resetsAt)} — {Describe(wait)} from now. "
                         + "Everything you have already written is saved and will still be here.",
            // No Retry button: the same request in ten seconds gets the same answer, and a button
            // that says otherwise is the app lying about what it will do.
            Retryable         = false,
            RetryAfterSeconds = (int)Math.Ceiling(wait.TotalSeconds),
            WorkPreserved     = true,
            Technical         = $"Daily AI quota reached: {used}/{limit} used, resets {resetsAt:O}.",
        });
    }

    private static AiQuotaExceededException NoUser(string operation, DateTimeOffset now) =>
        new(new AiQuotaStatus { Enforced = true, ResetsAt = AiUsageStore.ResetAfter(now) }, new FailureInfo
        {
            Kind         = FailureKind.AiQuotaExhausted,
            Domain       = FailureDomain.Ai,
            Headline     = "That needs you signed in",
            WhatHappened = "AI on this deployment runs on the owner's key and is counted against the "
                         + "account asking for it, and this request arrived without one.",
            WhatToDo     = "Sign in and start it again. Work that runs on its own in the background "
                         + "cannot use AI here, because there is no account to charge it to.",
            Retryable     = false,
            WorkPreserved = true,
            Technical     = $"No signed-in user on an AI call ({operation}); refused rather than billed to nobody.",
        });

    /// <summary>The reset instant as a clock time, in the one timezone a server can be sure of.</summary>
    private static string Clock(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("HH:mm 'UTC'", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// "3 hours 12 minutes". The clock time above is exact but needs the reader to know their own
    /// offset from UTC; this one needs nothing and is the part most people actually use.
    /// </summary>
    private static string Describe(TimeSpan wait)
    {
        if (wait <= TimeSpan.Zero) return "less than a minute";

        var hours   = (int)wait.TotalHours;
        var minutes = wait.Minutes;

        if (hours == 0 && minutes == 0) return "less than a minute";
        if (hours == 0) return $"{minutes} minute{(minutes == 1 ? "" : "s")}";
        if (minutes == 0) return $"{hours} hour{(hours == 1 ? "" : "s")}";

        return $"{hours} hour{(hours == 1 ? "" : "s")} {minutes} minute{(minutes == 1 ? "" : "s")}";
    }
}

/// <summary>
/// Which of the two answers to "may this request call Anthropic" this build runs on. One line in
/// Program.cs, and the difference between a trial that costs what it was budgeted for and one bill.
/// </summary>
public static class AiQuota
{
    /// <summary>
    /// Generations per account per UTC day. Set it in the host's configuration or environment
    /// (<c>Ai__DailyGenerationLimit</c>) to suit what the owner is willing to spend.
    /// </summary>
    public const string DailyLimitSetting = "Ai:DailyGenerationLimit";

    /// <summary>
    /// Enough for a person to write a handful of real listings and see whether the app is for them;
    /// far short of what a script could spend. The number the owner will actually tune, so it is
    /// configuration rather than a constant to recompile.
    /// </summary>
    public const int DefaultDailyLimit = 10;

    /// <summary>
    /// Accounts the daily limit does not apply to, by email address — comma or semicolon
    /// separated (<c>Ai__UnlimitedAccounts=ns@ingmining.com</c>). For the owner using their own
    /// product on their own key: never refused, still counted on the usage dashboard.
    /// </summary>
    public const string UnlimitedAccountsSetting = "Ai:UnlimitedAccounts";

    /// <summary>
    /// The exempt addresses, normalised the way <see cref="UserStore"/> matches them — so
    /// <c>NS@ingmining.com</c> in the environment exempts the account that signed up lowercase.
    /// </summary>
    public static IReadOnlySet<string> UnlimitedAccountsFrom(IConfiguration configuration) =>
        (configuration[UnlimitedAccountsSetting] ?? "")
            .Split(',', ';')
            .Select(UserStore.Normalize)
            .Where(email => !string.IsNullOrEmpty(email))
            .Select(email => email!)
            .ToHashSet();

    /// <summary>
    /// The configured limit, or <see cref="DefaultDailyLimit"/> when nothing usable is set.
    /// </summary>
    /// <remarks>
    /// Zero or less means unmetered, which is a deliberate keystroke for an owner running a hosted
    /// instance for themselves alone. A blank or unparseable setting is NOT read that way — a typo
    /// in an environment variable must not be the thing that quietly opens the tab.
    /// </remarks>
    public static int LimitFrom(IConfiguration configuration)
    {
        var raw = configuration[DailyLimitSetting];
        return int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out var limit)
            ? limit
            : DefaultDailyLimit;
    }

    /// <summary>
    /// Registers the <see cref="AiQuotaGate"/> that <see cref="ClaudeService"/> takes: a no-op in
    /// the desktop build, a per-user daily allowance in the hosted one.
    /// </summary>
    /// <param name="hosted">Overrides <see cref="HostedAuth.IsHostedBuild"/>. Tests pass both.</param>
    public static void AddAiQuota(WebApplicationBuilder builder, bool? hosted = null)
    {
        var limit = LimitFrom(builder.Configuration);

        if (!(hosted ?? HostedAuth.IsHostedBuild) || limit <= 0)
        {
            builder.Services.AddSingleton(AiQuotaGate.Unmetered);
            return;
        }

        // TryAdd, so a test can point the table at a scratch database. Same pattern as the users
        // table in HostedAuth.AddAccounts, and for the same reason.
        builder.Services.TryAddSingleton(sp => new AiUsageStore(sp.GetRequiredService<ListingDatabase>()));

        var unlimitedEmails = UnlimitedAccountsFrom(builder.Configuration);

        // Singleton over a per-request answer, like every other hosted service here: the gate is
        // injected into ClaudeService, which is itself a singleton, and what has to be per-request
        // is who is asking rather than the object that asks. UserScope already resolves that.
        builder.Services.AddSingleton(sp =>
        {
            // Exemption is configured by email but the gate meters by user id, so each check reads
            // the account back and compares addresses. Looked up per call, not resolved once at
            // startup: the exempt account may not have signed up yet when the app boots, and a
            // point-read against the id primary key is nothing next to the Anthropic call behind
            // it. GetService, not GetRequiredService — a build with no UserStore has nobody to
            // exempt, which is the right answer rather than a crash.
            var users = sp.GetService<UserStore>();
            Func<long, bool>? unlimited =
                unlimitedEmails.Count > 0 && users is not null
                    ? id => users.Find(id) is { } account
                            && unlimitedEmails.Contains(UserStore.Normalize(account.Email) ?? "")
                    : null;

            return new AiQuotaGate(
                sp.GetRequiredService<AiUsageStore>(),
                sp.GetRequiredService<UserScope>(),
                limit,
                unlimited: unlimited);
        });
    }
}
