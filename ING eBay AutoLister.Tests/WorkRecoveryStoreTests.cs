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

    // Writes a row straight into the table, past Save's own checks. This is how a row left by an
    // earlier build gets into the tests: the store's rules have to hold for rows it did not write.
    private void WriteRowDirectly(string key, string stage, string label, string payload)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO work_in_progress
                (key, label, stage, payload, fingerprint, listing_id, last_error,
                 attempt_count, created_at, updated_at)
            VALUES (@key, @label, @stage, @payload, '', '', '', 0, @now, @now);
            """;
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@label", label);
        command.Parameters.AddWithValue("@stage", stage);
        command.Parameters.AddWithValue("@payload", payload);
        command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

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

    [Fact]
    public void Discarding_everything_clears_the_banner_in_one_action()
    {
        var store = NewStore();
        for (var i = 0; i < 6; i++) store.Save(Draft($"wip-{i}", $"Draft {i}"));

        Assert.Equal(6, store.DiscardAll());
        Assert.Empty(store.Recoverable());
        Assert.Equal(0, store.DiscardAll());   // nothing left, and asking again is harmless
    }

    // Clearing the banner must not cost the seller their duplicate protection: the published rows in
    // this table are the journal PublishGuard reads to answer "did this already go live?".
    [Fact]
    public void Discarding_everything_leaves_the_publish_journal_alone()
    {
        var store = NewStore();
        store.Save(Draft("wip-live", "Already listed"));
        store.RecordPublished("wip-live", "fp-live", "555");
        store.Save(Draft("wip-draft", "Still being written"));

        Assert.Equal(1, store.DiscardAll());
        Assert.Equal("555", store.FindPublished("fp-live", TimeSpan.FromMinutes(30))?.ListingId);
    }

    // ── Empty drafts ────────────────────────────────────────────────────────
    //
    // The banner's credibility is the whole feature. Opening the AI listing modal and closing it again
    // leaves behind a full-sized payload — every control already holds its default — so a size test
    // reads a blank tab as work and the next launch announces an "unfinished listing" with nothing in
    // it. A seller shown that twice stops reading the banner, and then the launch where it is holding a
    // real Claude-written listing goes unread too.

    // A blank tab's payload: form defaults only, no title, and well over any plausible size threshold.
    private const string BlankTabPayload = """
        {"title":"","subtitle":"","category":"","categoryId":"","secondaryCategoryId":"",
         "condition":"USED_EXCELLENT","conditionDescription":"","brand":"","mpn":"","upc":"","ean":"",
         "isbn":"","description":"","price":0,"quantity":1,"packageType":"PACKAGE_THICK_ENVELOPE",
         "weightLbs":0,"weightOz":0,"handlingTimeBusinessDays":1,"itemLocationCountry":"US",
         "listingFormat":"FIXED_PRICE","durationDays":7,"itemSpecifics":{},"imageUrls":[]}
        """;

    [Fact]
    public void An_untouched_blank_form_is_not_saved_as_recoverable_work()
    {
        var store = NewStore();

        Assert.False(store.Save(new WorkSnapshot
        {
            Key = "wip-blank", Label = "Untitled listing", Payload = BlankTabPayload,
        }));
        Assert.Null(store.Get("wip-blank"));
        Assert.Empty(store.Recoverable());
    }

    // The rule cannot be about size: this payload is far bigger than the 400 characters the old check
    // used as its threshold, and holds nothing at all.
    [Fact]
    public void A_blank_form_is_rejected_despite_being_large()
    {
        Assert.True(BlankTabPayload.Length > 400);
        Assert.False(WorkRecoveryStore.IsWorthRecovering(WorkStage.Editing, "Untitled listing", BlankTabPayload));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Untitled listing")]
    [InlineData("untitled")]
    public void A_placeholder_label_is_not_mistaken_for_content(string label)
    {
        Assert.False(WorkRecoveryStore.IsWorthRecovering(WorkStage.Editing, label, BlankTabPayload));
    }

    // The other half of the rule, and the half that matters more: anything the seller actually put in
    // has to survive. A single filled field is enough.
    [Theory]
    [InlineData("""{"title":"Antminer S19","description":"","price":0}""")]
    [InlineData("""{"title":"","description":"Hashes at 95 TH/s","price":0}""")]
    [InlineData("""{"title":"","description":"","price":1499.99}""")]
    [InlineData("""{"title":"","brand":"Bitmain"}""")]
    [InlineData("""{"title":"","itemSpecifics":{"Model":"S19 Pro"}}""")]
    [InlineData("""{"title":"","imageUrls":["/generated-photos/a.jpg"]}""")]
    public void A_draft_with_anything_in_it_is_kept(string payload)
    {
        var store = NewStore();

        Assert.True(store.Save(new WorkSnapshot { Key = "wip-1", Label = "Untitled listing", Payload = payload }));
        Assert.Single(store.Recoverable());
    }

    [Fact]
    public void A_named_draft_is_kept_even_before_anything_else_is_filled_in()
    {
        var store = NewStore();

        Assert.True(store.Save(new WorkSnapshot
        {
            Key = "wip-1", Label = "Antminer S19", Payload = BlankTabPayload,
        }));
        Assert.Equal("Antminer S19", Assert.Single(store.Recoverable()).Label);
    }

    // Blank rows written before this rule existed are still in the table, so the filter has to apply on
    // read too — otherwise the banner stays cluttered until each one happens to be saved over.
    [Fact]
    public void An_empty_draft_already_in_the_database_is_not_offered_back()
    {
        var store = NewStore();
        store.Save(Draft("wip-real"));
        WriteRowDirectly("wip-blank", WorkStage.Editing, "Untitled listing", BlankTabPayload);

        Assert.Equal("wip-real", Assert.Single(store.Recoverable()).Key);
    }

    [Fact]
    public void An_empty_draft_already_in_the_database_is_pruned_away()
    {
        var store = NewStore();
        WriteRowDirectly("wip-blank", WorkStage.Editing, "Untitled listing", BlankTabPayload);

        Assert.True(store.Prune() > 0);
        Assert.Null(store.Get("wip-blank"));
    }

    // The exception that keeps the rule safe. A row past `editing` means something really was sent to
    // eBay, and an unresolved publish is the most important row in the table — it must surface whatever
    // its payload looks like.
    [Theory]
    [InlineData(WorkStage.Publishing)]
    [InlineData(WorkStage.Failed)]
    public void A_publish_attempt_is_never_treated_as_an_empty_draft(string stage)
    {
        var store = NewStore();
        WriteRowDirectly("wip-sent", stage, "", "");

        Assert.True(WorkRecoveryStore.IsWorthRecovering(stage, "", ""));
        Assert.Equal("wip-sent", Assert.Single(store.Recoverable()).Key);
        Assert.NotNull(store.Get("wip-sent"));   // and Prune, which Recoverable does not run, left it
    }

    // A payload shape this rule does not recognise cannot be judged field by field, so it is judged by
    // size — throwing away another caller's work because the field list has not heard of it would be
    // the same bug in the other direction.
    [Fact]
    public void An_unrecognised_payload_shape_is_kept_when_it_holds_anything_substantial()
    {
        Assert.True(WorkRecoveryStore.IsWorthRecovering(
            WorkStage.Editing, "", """{"someOtherDraftKind":"a good deal of text the app does not parse"}"""));
        Assert.False(WorkRecoveryStore.IsWorthRecovering(WorkStage.Editing, "", "{}"));
        Assert.False(WorkRecoveryStore.IsWorthRecovering(WorkStage.Editing, "", ""));
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

    // ── An autosave saves work; it never moves a publish stage ──────────────
    //
    // The two requests really do race. The seller presses Publish, the browser flushes the text one
    // last time, and both are in the air at once — so the ordering below is not contrived, it is the
    // ordinary shape of the busiest moment in the app. Every test in this section is a way the later
    // autosave used to overwrite what the publish path had just recorded.

    [Fact]
    public void An_autosave_landing_after_the_publish_started_does_not_erase_the_unresolved_publish()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkPublishing("wip-1", "fp-1");

        // Exactly what the browser sends: its own idea of the stage, which is always `editing`.
        store.Save(Draft(payload: """{"title":"Antminer S19 Pro"}"""));

        Assert.Equal(WorkStage.Publishing, store.Get("wip-1")!.Stage);
        Assert.Equal("wip-1", Assert.Single(store.Unresolved()).Key);
        // And the seller's last keystrokes are still kept — the row is not frozen, only its stage is.
        Assert.Contains("S19 Pro", store.Get("wip-1")!.Payload);
    }

    // The expensive one. A published row that gets written back to `editing` is no longer found by
    // FindPublished, so PublishGuard's duplicate brake is gone and the next attempt puts a second
    // live listing up for the same physical item.
    [Fact]
    public void An_autosave_landing_after_the_publish_succeeded_cannot_reopen_the_published_row()
    {
        var store = NewStore();
        store.Save(Draft());
        store.RecordPublished("wip-1", "fp-1", "555");

        Assert.Equal(WorkSaveOutcome.PublishJournal, store.SaveWithOutcome(Draft()));
        Assert.Equal(WorkStage.Published, store.Get("wip-1")!.Stage);
        Assert.Equal("555", store.FindPublished("fp-1", TimeSpan.FromMinutes(30))?.ListingId);
        Assert.Empty(store.Recoverable());
    }

    // The duplicate window is measured from the published row's updated_at. An autosave bumping it
    // would quietly extend the window every few seconds for as long as the tab stayed open, so the
    // brake would outlive the moment it is meant to cover.
    [Fact]
    public void An_autosave_into_a_published_row_does_not_extend_the_duplicate_window()
    {
        var store = NewStore();
        store.Save(Draft());
        store.RecordPublished("wip-1", "fp-1", "555");
        var publishedAt = store.Get("wip-1")!.UpdatedUtc;

        Thread.Sleep(15);
        store.Save(Draft(payload: """{"title":"still typing"}"""));

        Assert.Equal(publishedAt, store.Get("wip-1")!.UpdatedUtc);
    }

    [Fact]
    public void An_autosave_after_a_refusal_keeps_the_refusal_on_screen()
    {
        var store = NewStore();
        store.Save(Draft());
        store.MarkFailed("wip-1", "The item specific Model is missing.");

        store.Save(Draft(payload: """{"title":"Antminer S19","itemSpecifics":{"Model":"S19 Pro"}}"""));

        var row = store.Get("wip-1")!;
        Assert.Equal(WorkStage.Failed, row.Stage);
        Assert.Contains("Model is missing", row.LastError);
        Assert.Contains("S19 Pro", row.Payload);   // and the fix the seller just typed survived
    }

    // ── What an autosave did, when it did not simply save ───────────────────
    //
    // The client acts on each of these differently — retry, give up, or start a fresh key — so a
    // single false is not enough of an answer to build a recovery path on.

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_save_with_no_key_reports_that_there_was_nothing_to_save(string key)
        => Assert.Equal(WorkSaveOutcome.Empty, NewStore().SaveWithOutcome(new WorkSnapshot { Key = key, Payload = "x" }));

    [Fact]
    public void An_oversized_payload_says_so_rather_than_looking_like_an_empty_one()
    {
        var store = NewStore();

        Assert.Equal(WorkSaveOutcome.TooLarge,
            store.SaveWithOutcome(Draft(payload: new string('x', WorkRecoveryStore.MaxPayloadBytes + 1))));
        Assert.Null(store.Get("wip-1"));
    }

    [Fact]
    public void A_blank_form_reports_empty_rather_than_a_failure_worth_retrying()
        => Assert.Equal(WorkSaveOutcome.Empty,
            NewStore().SaveWithOutcome(new WorkSnapshot
            {
                Key = "wip-blank", Label = "Untitled listing", Payload = BlankTabPayload,
            }));

    [Fact]
    public void An_ordinary_save_reports_that_it_saved()
        => Assert.Equal(WorkSaveOutcome.Saved, NewStore().SaveWithOutcome(Draft()));

    // ── A publish whose draft never reached the app ─────────────────────────
    //
    // The seller whose autosave has been failing is the seller who most needs the safety net, and
    // until now they were the one seller who had none: the journal was an UPDATE, so a publish with
    // no row behind it affected nothing and was recorded nowhere. An app that died mid-send came
    // back with no sign that anything had been sent.

    [Fact]
    public void A_publish_whose_draft_never_reached_the_app_is_still_journalled()
    {
        var store = NewStore();
        store.MarkPublishing("wip-never-saved", "fp-1", "Antminer S19 Pro 110TH");

        var row = Assert.Single(store.Unresolved());
        Assert.Equal("wip-never-saved", row.Key);
        Assert.Equal(WorkStage.Publishing, row.Stage);
        // The title is what makes the row actionable: Check eBay resolves it by searching on it.
        Assert.Equal("Antminer S19 Pro 110TH", row.Label);
        Assert.Equal(1, row.AttemptCount);
    }

    [Fact]
    public void A_refusal_whose_draft_never_reached_the_app_is_still_shown_to_the_seller()
    {
        var store = NewStore();
        store.MarkFailed("wip-never-saved", "The item specific Model is missing.", "Antminer S19");

        var row = Assert.Single(store.Recoverable());
        Assert.Equal(WorkStage.Failed, row.Stage);
        Assert.Equal("Antminer S19", row.Label);
        Assert.Contains("Model is missing", row.LastError);
    }

    // The draft's title is the seller's own wording, and it outranks whatever the publish path
    // happened to be carrying — otherwise a retry would rename the row under them.
    [Fact]
    public void A_saved_drafts_own_title_is_not_overwritten_by_the_publish()
    {
        var store = NewStore();
        store.Save(Draft(label: "Antminer S19 — my listing"));
        store.MarkPublishing("wip-1", "fp-1", "something the publish path made up");

        Assert.Equal("Antminer S19 — my listing", store.Get("wip-1")!.Label);
    }

    [Fact]
    public void A_journalled_publish_survives_a_restart_with_its_title_intact()
    {
        NewStore().MarkPublishing("wip-never-saved", "fp-1", "Antminer S19 Pro");

        // A new process, a new store instance, the same file: what the seller comes back to.
        Assert.Equal("Antminer S19 Pro", Assert.Single(NewStore().Unresolved()).Label);
    }

    [Fact]
    public void A_publish_with_no_key_at_all_does_not_leave_a_nameless_row_behind()
    {
        var store = NewStore();
        store.MarkPublishing("", "fp-1", "Antminer S19");
        store.MarkFailed("   ", "some refusal", "Antminer S19");

        Assert.Empty(store.Recoverable());
        Assert.Empty(store.Unresolved());
    }

    [Fact]
    public void A_retry_of_a_journalled_publish_counts_the_attempt_rather_than_starting_over()
    {
        var store = NewStore();
        store.MarkPublishing("wip-never-saved", "fp-1", "Antminer S19");
        store.MarkFailed("wip-never-saved", "first refusal");
        store.MarkPublishing("wip-never-saved", "fp-1", "Antminer S19");

        var row = store.Get("wip-never-saved")!;
        Assert.Equal(2, row.AttemptCount);
        Assert.Equal("", row.LastError);
    }
}
