using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The half of "nothing you wrote has been lost" that was missing.
/// </summary>
/// <remarks>
/// <para>
/// Autosave held every field of an unfinished listing from the moment it was typed, and getting it
/// back took noticing a Recover button, opening it, and choosing a row. A safety net nobody knows to
/// reach for catches nobody: the work was saved and lost anyway, which from the seller's side is
/// indistinguishable from never having saved it at all.
/// </para>
/// <para>
/// So the AI Listing screen restores by itself. <see cref="WorkRecoveryStore.LatestResumable"/> is
/// what decides which draft that is, and everything it deliberately refuses is here — a wrong answer
/// is not a missing feature, it is a listing opened over somebody's work or a publish invited for a
/// second time on an item that is already live.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class WorkAutoRestoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"work_resume_{Guid.NewGuid():N}.db");

    private WorkRecoveryStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private const string RealListing = """{"title":"Antminer S19 Pro 110TH","price":1899.0}""";

    private static WorkSnapshot Draft(string key, string label = "Antminer S19 Pro 110TH", string payload = RealListing)
        => new() { Key = key, Label = label, Payload = payload };

    // ── What comes back ─────────────────────────────────────────────────────

    [Fact]
    public void The_screen_opens_on_the_listing_that_was_being_written()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));

        var resumed = store.LatestResumable();

        Assert.NotNull(resumed);
        Assert.Equal("wip-1", resumed!.Key);
        Assert.Equal(RealListing, resumed.Payload);
    }

    /// <summary>
    /// The screen holds one listing, and "carry on where I left off" has exactly one answer: the
    /// last one touched. Everything older stays reachable under Recover.
    /// </summary>
    [Fact]
    public void The_newest_draft_wins_when_there_are_several()
    {
        var store = NewStore();
        store.Save(Draft("wip-old", "An older listing"));
        Thread.Sleep(15);
        store.Save(Draft("wip-new", "The one still open"));

        Assert.Equal("wip-new", store.LatestResumable()!.Key);
    }

    [Fact]
    public void Nothing_saved_means_nothing_is_put_on_screen()
        => Assert.Null(NewStore().LatestResumable());

    // ── What it refuses, and why each refusal is the point ──────────────────

    /// <summary>
    /// A row still marked <c>publishing</c> is a listing that may be live on eBay this second — the
    /// app went down between sending it and hearing back. It has its own urgent dashboard banner
    /// saying the outcome is unknown, and a <em>Check eBay</em> button that resolves it. Quietly
    /// opening it in the editor instead invites the seller to press Publish on an item that is
    /// already up, which is two insertion fees and an oversell as soon as one sells.
    /// </summary>
    [Fact]
    public void A_publish_the_app_cannot_vouch_for_is_never_restored_silently()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));
        store.MarkPublishing("wip-1", "fingerprint-1", "Antminer S19 Pro 110TH");

        Assert.Null(store.LatestResumable());

        // And it is still offered where it belongs — this is a filter on the automatic path only,
        // not a row being hidden from the seller.
        Assert.Contains(store.Recoverable(), r => r.Key == "wip-1");
    }

    [Fact]
    public void A_failed_publish_keeps_its_banner_rather_than_reopening_by_itself()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));
        store.MarkFailed("wip-1", "eBay rejected the category.", "Antminer S19 Pro 110TH");

        Assert.Null(store.LatestResumable());
        Assert.Contains(store.Recoverable(), r => r.Key == "wip-1");
    }

    [Fact]
    public void A_published_listing_is_not_offered_back_as_unfinished_work()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));
        store.RecordPublished("wip-1", "fingerprint-1", "110012345678");

        Assert.Null(store.LatestResumable());
    }

    /// <summary>
    /// A publish is journalled even when its draft never reached the store, so some rows carry an
    /// outcome to resolve and no listing to put back. Restoring one means clearing the screen and
    /// filling it with nothing — strictly worse than the blank form it replaced.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    public void A_row_with_no_listing_in_it_is_not_worth_clearing_the_screen_for(string payload)
    {
        var store = NewStore();
        // Past Save's own checks: these are rows the publish journal and older builds leave behind.
        WriteRowDirectly("wip-1", WorkStage.Editing, "Antminer S19 Pro 110TH", payload);

        Assert.Null(store.LatestResumable());
    }

    /// <summary>
    /// Opening the modal and closing it again leaves a full-sized payload behind — every control
    /// already holds its default. Restoring one of those would announce "picked up where you left
    /// off" over a form identical to the blank one, on every single open.
    /// </summary>
    [Fact]
    public void An_untouched_form_is_not_work_to_pick_up()
    {
        var store = NewStore();
        WriteRowDirectly("wip-blank", WorkStage.Editing, "Untitled listing",
            """{"title":"","description":"","price":0,"condition":"USED_EXCELLENT","quantity":1}""");

        Assert.Null(store.LatestResumable());
    }

    // ── Opening the screen twice ────────────────────────────────────────────

    /// <summary>
    /// The rule the whole feature turns on. The client adopts the restored row's key, so carrying on
    /// typing updates that row rather than starting a second one beside it. Without this, every open
    /// of the screen would fork the draft and the next launch would offer the same listing back
    /// twice — once as what was restored, once as what was typed after restoring it.
    /// </summary>
    [Fact]
    public void Restoring_and_carrying_on_leaves_one_draft_rather_than_two()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));

        // Open. Restore. Type another word. Adopting the key is what makes this the same row.
        var first = store.LatestResumable()!;
        store.Save(Draft(first.Key, payload: """{"title":"Antminer S19 Pro 110TH — tested","price":1899.0}"""));

        // Open again: the same draft, carrying the later edit, and still only one of it.
        var second = store.LatestResumable()!;
        Assert.Equal(first.Key, second.Key);
        Assert.Contains("tested", second.Payload);
        Assert.Single(store.Recoverable(40));
    }

    // ── Whose draft it is ───────────────────────────────────────────────────

    /// <summary>
    /// This table was the last one in the app without an owner column, and it was the one that
    /// mattered most once the restore stopped needing a click. The recovery list at least waited to
    /// be opened; auto-restore does not — so on the hosted deployment an unscoped table would have
    /// opened the AI Listing screen on a stranger's half-written listing, with their title, their
    /// price and their photos, without anybody asking for it.
    /// </summary>
    [Fact]
    public void One_sellers_unfinished_listing_never_opens_on_another_sellers_screen()
    {
        long? signedIn = 7;
        var scoped = new WorkRecoveryStore(_dbPath, UserScope.PerUser(() => signedIn));

        scoped.Save(Draft("wip-seven", "Seven's Antminer"));
        Assert.Equal("wip-seven", scoped.LatestResumable()!.Key);

        signedIn = 8;
        Assert.Null(scoped.LatestResumable());
        Assert.Empty(scoped.Recoverable());

        // Eight's own work is their own, and does not become Seven's either.
        scoped.Save(Draft("wip-eight", "Eight's GPU"));
        Assert.Equal("wip-eight", scoped.LatestResumable()!.Key);

        signedIn = 7;
        Assert.Equal("wip-seven", scoped.LatestResumable()!.Key);
    }

    /// <summary>
    /// Discarding is a write, and a write that is not scoped is a write onto somebody else's row.
    /// </summary>
    [Fact]
    public void One_seller_cannot_discard_another_sellers_draft()
    {
        long? signedIn = 7;
        var scoped = new WorkRecoveryStore(_dbPath, UserScope.PerUser(() => signedIn));
        scoped.Save(Draft("wip-seven", "Seven's Antminer"));

        signedIn = 8;
        Assert.False(scoped.Discard("wip-seven"));
        Assert.Equal(0, scoped.DiscardAll());

        signedIn = 7;
        Assert.Equal("wip-seven", scoped.LatestResumable()!.Key);
    }

    /// <summary>
    /// Two sellers listing the same item produce the same publish fingerprint. One seller's live
    /// listing must not brake the other's publish, and must never be the listing ID the app points
    /// them at — that item is not theirs and they cannot manage it.
    /// </summary>
    [Fact]
    public void One_sellers_published_listing_does_not_block_anothers_publish()
    {
        long? signedIn = 7;
        var scoped = new WorkRecoveryStore(_dbPath, UserScope.PerUser(() => signedIn));
        scoped.RecordPublished(null, "same-fingerprint", "110000000007");

        signedIn = 8;
        Assert.Null(scoped.FindPublished("same-fingerprint", TimeSpan.FromMinutes(30)));

        // And eight's own publish still journals, under its own row rather than colliding with seven's.
        scoped.RecordPublished(null, "same-fingerprint", "110000000008");
        Assert.Equal("110000000008", scoped.FindPublished("same-fingerprint", TimeSpan.FromMinutes(30))!.ListingId);

        signedIn = 7;
        Assert.Equal("110000000007", scoped.FindPublished("same-fingerprint", TimeSpan.FromMinutes(30))!.ListingId);
    }

    /// <summary>
    /// Nobody signed in on a hosted deployment is the one case where the only safe answer is
    /// nothing. Restoring under those conditions would mean picking a seller to act as, which is
    /// picking whose listing to put in front of a stranger.
    /// </summary>
    [Fact]
    public void Nobody_signed_in_restores_nothing()
    {
        var store = NewStore();
        store.Save(Draft("wip-1"));

        var anonymous = new WorkRecoveryStore(_dbPath, UserScope.PerUser(() => null));
        Assert.Null(anonymous.LatestResumable());
        Assert.Empty(anonymous.Recoverable());
    }

    // ── An old database ─────────────────────────────────────────────────────

    /// <summary>
    /// Every draft already on a desktop machine was written by the one person sitting at it, and the
    /// migration's default keeps all of it theirs. A restore that could not see the work saved
    /// before the upgrade would be the feature losing the listing it exists to save.
    /// </summary>
    [Fact]
    public void Drafts_written_before_the_owner_column_existed_still_come_back()
    {
        CreateTableWithoutOwnerColumn();
        InsertIntoTableWithoutOwnerColumn("wip-legacy", RealListing);

        var store = NewStore();   // opening it runs the migration

        var resumed = store.LatestResumable();
        Assert.NotNull(resumed);
        Assert.Equal("wip-legacy", resumed!.Key);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        return connection;
    }

    // Writes a row straight into the table, past Save's own checks — how a row left by an earlier
    // build, or by the publish journal, gets into a test.
    private void WriteRowDirectly(string key, string stage, string label, string payload)
    {
        NewStore();   // opening a store is what creates the table; do that before writing into it

        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_in_progress
                (key, user_id, label, stage, payload, fingerprint, listing_id, last_error,
                 attempt_count, created_at, updated_at)
            VALUES (@key, 0, @label, @stage, @payload, '', '', '', 0, @now, @now);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@label", label);
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private void CreateTableWithoutOwnerColumn()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE work_in_progress (
                key TEXT PRIMARY KEY,
                label TEXT NOT NULL DEFAULT '',
                stage TEXT NOT NULL DEFAULT 'editing',
                payload TEXT NOT NULL DEFAULT '',
                fingerprint TEXT NOT NULL DEFAULT '',
                listing_id TEXT NOT NULL DEFAULT '',
                last_error TEXT NOT NULL DEFAULT '',
                attempt_count INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL DEFAULT '',
                updated_at TEXT NOT NULL DEFAULT ''
            );
            """;
        command.ExecuteNonQuery();
    }

    private void InsertIntoTableWithoutOwnerColumn(string key, string payload)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_in_progress (key, label, stage, payload, created_at, updated_at)
            VALUES (@key, 'Legacy draft', 'editing', @payload, @now, @now);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }
}
