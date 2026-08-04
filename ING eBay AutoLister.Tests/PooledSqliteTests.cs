namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The collection every test class that clears SQLite's connection pool belongs to.
/// </summary>
/// <remarks>
/// <para>
/// Each of those classes writes to its own temp database file and, to release the file handle
/// before deleting it, calls <c>SqliteConnection.ClearAllPools()</c>. That call is exactly as
/// global as it sounds: it disposes every pooled connection in the process, including ones another
/// test class is in the middle of using. The victim then fails with
/// <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c> somewhere entirely unrelated to whatever
/// broke it, and does so only when the scheduler happens to overlap the two.
/// </para>
/// <para>
/// Putting them in one collection is the fix that matches the cause: a pool clear is a
/// process-wide event, so the classes that fire one run one at a time, and
/// <c>DisableParallelization</c> keeps them from running beside a class in another collection that
/// merely holds a pooled connection without ever clearing anything.
/// </para>
/// <para>
/// The alternative — clearing only the pool for the test's own connection string — was not taken
/// because it depends on every store building its connection string identically to the test that
/// cleans up after it, which is a silent, per-class assumption that would rot.
/// </para>
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PooledSqliteTests
{
    public const string Name = "pooled-sqlite";
}
