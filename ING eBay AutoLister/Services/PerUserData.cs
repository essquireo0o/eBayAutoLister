using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Whose rows the work in flight is allowed to read and write.
/// </summary>
/// <remarks>
/// <para>
/// Every table in this app was written for one seller on their own machine, so none of them has a
/// column saying who a row belongs to — there was only ever one answer. Put that same database on a
/// server behind <see cref="HostedAuth"/> and the second person to sign up opens Money Made and
/// finds the first person's sales in it, with their prices, their costs and their profit. So under
/// HOSTED every row carries the id of the user who created it and every query filters on the user
/// making the request. The desktop build keeps one owner, <see cref="DesktopOwner"/>, which is also
/// what every row written before this change already belongs to — see <see cref="UserOwnedTable"/>.
/// </para>
/// <para>
/// <b>Why a resolver rather than a scoped service.</b> The stores are singletons, injected into
/// other singletons and into three background workers, and ASP.NET Core will not hand a scoped
/// service to a singleton. What has to be per-request is the answer, not the object that gives it —
/// the same reasoning, and the same shape, as <see cref="PerUserCredentialsSource"/>.
/// </para>
/// <para>
/// <b>When nobody is signed in.</b> <see cref="OwnerId"/> is null, and every store reads nothing and
/// writes nothing. That is the only safe answer and it is deliberately not an exception: background
/// work — the earnings import, the token refresh, the SEO job — runs with no HttpContext, and the
/// alternative to dropping its writes is picking a user to act as, which is picking whose earnings
/// to write a stranger's order into. No API request can reach here without a user, because under
/// HOSTED every endpoint requires one before it runs.
/// </para>
/// </remarks>
public sealed class UserScope
{
    /// <summary>
    /// The one seller a desktop database belongs to, and the owner every row created before this
    /// change already has: the migration adds the column with this as its default, so nothing
    /// already on disk becomes invisible.
    /// </summary>
    public const long DesktopOwner = 0;

    private readonly Func<long?>? _signedInUserId;

    private UserScope(Func<long?>? signedInUserId) => _signedInUserId = signedInUserId;

    /// <summary>One machine, one seller, every row theirs. The desktop build, and every test that
    /// is not about isolation.</summary>
    public static UserScope Desktop { get; } = new(null);

    /// <summary>A row owner resolved per request from whoever is signed in. The hosted build.</summary>
    public static UserScope PerUser(Func<long?> signedInUserId) => new(signedInUserId);

    /// <summary>True when rows are separated by user — the hosted build.</summary>
    public bool IsPerUser => _signedInUserId is not null;

    /// <summary>
    /// The user whose rows may be touched right now, or null when a hosted deployment has nobody
    /// signed in on this call. Read once per operation, never cached: a singleton store holding on
    /// to the first answer is exactly how one seller's board ends up in front of another.
    /// </summary>
    public long? OwnerId => _signedInUserId is null ? DesktopOwner : _signedInUserId();
}

/// <summary>
/// The one migration every user-owned table takes, in one place so all four take the same one.
/// </summary>
/// <remarks>
/// Added in place with <c>ALTER TABLE ... ADD COLUMN</c> rather than by rebuilding the table — the
/// house pattern from <see cref="EarningsStore"/>'s cost-confirmation column. A default of
/// <see cref="UserScope.DesktopOwner"/> is what keeps the existing rows valid and, on the machine
/// this matters on, correct: every sale, listing, cost and deal already in a desktop database was
/// entered by the one person sitting at it, and they keep all of it.
/// </remarks>
public static class UserOwnedTable
{
    /// <summary>The column, named the same in every table so a query is obvious at a glance.</summary>
    public const string OwnerColumn = "user_id";

    /// <summary>
    /// Adds the owner column to an existing table if it is not already there, and indexes it.
    /// Call from a store's Initialize after the CREATE TABLE and before any index that mentions
    /// the column — on an old database that index would otherwise be built on a column that does
    /// not exist yet.
    /// </summary>
    public static void Migrate(SqliteConnection connection, string table)
    {
        AddColumnIfMissing(connection, table, OwnerColumn, $"INTEGER NOT NULL DEFAULT {UserScope.DesktopOwner}");

        using var index = connection.CreateCommand();
        index.CommandText = $"CREATE INDEX IF NOT EXISTS ix_{table}_{OwnerColumn} ON {table}({OwnerColumn});";
        index.ExecuteNonQuery();
    }

    /// <summary>
    /// The house pattern, generalised over the table name so four stores share one copy of it.
    /// </summary>
    public static void AddColumnIfMissing(SqliteConnection connection, string table, string column, string declaration)
    {
        using var probe = connection.CreateCommand();
        probe.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = @name;";
        probe.Parameters.AddWithValue("@name", column);
        if (Convert.ToInt32(probe.ExecuteScalar()) > 0) return;

        using var alter = connection.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alter.ExecuteNonQuery();
    }
}

/// <summary>
/// Which of the two answers to "whose rows are these" this build runs on. One line in Program.cs,
/// and the difference between one seller's Money Made screen and everybody's.
/// </summary>
public static class PerUserData
{
    /// <summary>
    /// Registers the <see cref="UserScope"/> every store injects: one owner in the desktop build,
    /// the signed-in user in the hosted one.
    /// </summary>
    /// <param name="hosted">Overrides <see cref="HostedAuth.IsHostedBuild"/>. Tests pass both.</param>
    public static void AddUserScope(WebApplicationBuilder builder, bool? hosted = null)
    {
        if (!(hosted ?? HostedAuth.IsHostedBuild))
        {
            builder.Services.AddSingleton(UserScope.Desktop);
            return;
        }

        // How a singleton store learns who is asking — the same accessor, for the same reason, as
        // the per-user credential store next door.
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton(sp =>
        {
            // Through HostedAuth, so which claim carries the id is decided in the one place that
            // puts it there when somebody signs in.
            var accessor = sp.GetRequiredService<IHttpContextAccessor>();
            return UserScope.PerUser(() => HostedAuth.CurrentUserId(accessor));
        });
    }
}
