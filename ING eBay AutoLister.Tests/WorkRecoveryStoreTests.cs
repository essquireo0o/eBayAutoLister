using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// This store is what makes "nothing you entered has been lost" true rather than a claim. It holds a
// listing whose AI-written title, description and specifics cost real API spend and a minute or two
// of waiting — so the cases that matter are: does it come back, does it come back only once, and does
// it stay bounded when autosave fires every few seconds while someone types.
public class WorkRecoveryStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"work_recovery_{Guid.NewGuid():N}.db");

    private WorkRecoveryStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    private static WorkSnapshot Draft(string key = "wip-1", string label = "Antminer S19", string payload = """{"title":"Antminer S19"}""")
        => new() { Key = key, Label = label, Payload = payload };

    // ── Round trip ──────────────────────────────────────────────────────────

    [Fact]
    public void A_saved_draft_comes_back_with_its_content_intact()
    {
        var store = NewStore();
        store.Save(Draft());

        var recovered = store.Get("wip-1");

        Assert.NotNull(recovered);
        Assert.Equal("Antminer S19", recovered!.Label);
        Assert.Equal("""{"title":"Antminer S19"}""", recovered.Payload);
        Assert.Equal(WorkStage.Editing, recovered.Stage);
    }

    // Autosave fires repeatedly while the seller types. If each save added a row, one listing would
    // be offered back as dozens of separate recoveries.
    [Fact]
    public void Repeated_autosaves_of_one_draft_overwrite_rather_than_accumulate()
    {
        var store = NewStore();
        for (var i = 0; i < 25; i++)
            store.Save(Draft(payload: $"{{\"title\":\"draft {i}\"}}"));

        Assert.Single(store.Recoverable());
        Assert.Contains("draft 24", store.Get("wip-1")!.Payload);
    }

    [Fact]
    public void A_save_with_no_key_is_refused_rather_than_written_under_a_blank_one()
    {
        var store = NewStore();

        Assert.False(store.Save(new WorkSnapshot { Key = "", Payload = "x" }));
        Assert.Empty(store.Recoverable());
    }

    // A runaway client must not be able to fill the disk one autosave at a time. Reported, not
    // thrown: this runs while the seller is typing and interrupting them would be worse.
    [Fact]
    public void An_oversized_payload_is_refused_without_throwing()
    {
        var store = NewStore();
        var huge = new string('x', WorkRecoveryStore.MaxPayloadBytes + 1);

        Assert.False(store.Save(Draft(payload: huge)));
        Assert.Null(store.Get("wip-1"));
    }

    // ── What counts as recoverable ──────────────────────────────────────────

    [Fact]
    public void A_published_listing_is_no_longer_offered_back_as_unfinished_work()
    {
        var store = NewStore();
        store.Save(Draft());
        store.RecordPublished("wip-1", "fp-1", "1234567890");

        Assert.Empty(store.Recoverable());
    }

    [Fact]
    public void A_failed_publish_stays_recoverable_and_keeps_eBays_reason_attached()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkPublishing("wip-1", "fp-1");
        store.MarkFailed("wip-1", "The item specific Model is missing.");

        var recovered = Assert.Single(store.Recoverable());
        Assert.Equal(WorkStage.Failed, recovered.Stage);
        Assert.Contains("Model is missing", recovered.LastError);
    }

    // The row that matters most: still marked publishing means the app went down between sending the
    // listing and hearing back. Hiding it on the assumption it worked is how a seller ends up with a
    // live listing they never see, or publishes a duplicate of one they do.
    [Fact]
    public void A_publish_the_app_never_saw_finish_is_reported_as_unresolved_and_still_recoverable()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkPublishing("wip-1", "fp-1");

        Assert.Equal(WorkStage.Publishing, Assert.Single(store.Recoverable()).Stage);
        Assert.Equal("wip-1", Assert.Single(store.Unresolved()).Key);
    }

    [Fact]
    public void Newest_work_is_offered_first()
    {
        var store = NewStore();
        store.Save(Draft("wip-old", "Older"));
        Thread.Sleep(15);
        store.Save(Draft("wip-new", "Newer"));

        Assert.Equal("Newer", store.Recoverable().First().Label);
    }

    [Fact]
    public void Each_publish_attempt_is_counted()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkPublishing("wip-1", "fp-1");
        store.MarkFailed("wip-1", "first refusal");
        store.MarkPublishing("wip-1", "fp-1");

        Assert.Equal(2, store.Get("wip-1")!.AttemptCount);
    }

    // A retry after a rejection must not still be showing the old error, or a seller who fixed the
    // problem sees the message that said it was broken.
    [Fact]
    public void Retrying_clears_the_previous_error()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkFailed("wip-1", "old refusal");
        store.MarkPublishing("wip-1", "fp-1");

        Assert.Equal("", store.Get("wip-1")!.LastError);
    }

    [Fact]
    public void Discarding_a_draft_removes_it()
    {
        var store = NewStore();
        store.Save(Draft());

        Assert.True(store.Discard("wip-1"));
        Assert.Null(store.Get("wip-1"));
        Assert.False(store.Discard("wip-1"));
    }

    // ── The publish journal ─────────────────────────────────────────────────

    [Fact]
    public void A_published_fingerprint_is_findable_inside_the_window()
    {
        var store = NewStore();
        store.RecordPublished("wip-1", "fp-abc", "555");

        Assert.Equal("555", store.FindPublished("fp-abc", TimeSpan.FromMinutes(30))?.ListingId);
    }

    [Fact]
    public void A_published_fingerprint_stops_counting_once_the_window_has_passed()
    {
        var store = NewStore();
        store.RecordPublished("wip-1", "fp-abc", "555");

        Assert.Null(store.FindPublished("fp-abc", TimeSpan.Zero));
    }

    [Fact]
    public void An_unknown_or_blank_fingerprint_finds_nothing()
    {
        var store = NewStore();
        store.RecordPublished("wip-1", "fp-abc", "555");

        Assert.Null(store.FindPublished("fp-other", TimeSpan.FromMinutes(30)));
        Assert.Null(store.FindPublished("", TimeSpan.FromMinutes(30)));
    }

    // Publishing the same content twice must not throw on the second write: the guard's answer to a
    // duplicate depends on this record existing, so it has to be safe to write repeatedly.
    [Fact]
    public void Recording_the_same_publish_twice_is_harmless()
    {
        var store = NewStore();
        store.RecordPublished(null, "fp-abc", "555");
        store.RecordPublished(null, "fp-abc", "555");

        Assert.Equal("555", store.FindPublished("fp-abc", TimeSpan.FromMinutes(30))?.ListingId);
    }

    [Fact]
    public void A_blank_fingerprint_is_not_recorded_as_a_publish()
    {
        var store = NewStore();
        store.RecordPublished("wip-1", "", "555");

        Assert.Empty(store.Unresolved());
        Assert.Null(store.FindPublished("", TimeSpan.FromMinutes(30)));
    }

    // ── Bounds ──────────────────────────────────────────────────────────────

    [Fact]
    public void The_recoverable_list_stays_bounded_and_keeps_the_newest()
    {
        var store = NewStore();
        for (var i = 0; i < WorkRecoveryStore.MaxRecoverableRows + 15; i++)
        {
            store.Save(Draft($"wip-{i:D3}", $"Draft {i}"));
            Thread.Sleep(2);   // distinct updated_at values, so "newest" is well defined
        }

        var rows = store.Recoverable(WorkRecoveryStore.MaxRecoverableRows);

        Assert.True(rows.Count <= WorkRecoveryStore.MaxRecoverableRows);
        Assert.Contains(rows, r => r.Label == $"Draft {WorkRecoveryStore.MaxRecoverableRows + 14}");
    }

    [Fact]
    public void Two_stores_over_one_database_see_the_same_work()
    {
        // The real shape after a restart: a new process, a new store instance, the same file.
        NewStore().Save(Draft());

        Assert.Equal("Antminer S19", NewStore().Get("wip-1")!.Label);
    }

    [Fact]
    public void Opening_an_existing_database_a_second_time_does_not_wipe_it()
    {
        var first = NewStore();
        first.Save(Draft());

        _ = NewStore();   // re-runs Initialize()

        Assert.NotNull(first.Get("wip-1"));
    }

    [Fact]
    public void A_long_label_or_error_is_clipped_rather_than_rejected()
    {
        var store = NewStore();
        store.Save(new WorkSnapshot { Key = "wip-1", Label = new string('L', 5000), Payload = "{}" });
        store.MarkFailed("wip-1", new string('E', 5000));

        var row = store.Get("wip-1")!;
        Assert.True(row.Label.Length <= 200);
        Assert.True(row.LastError.Length <= 600);
    }
}
