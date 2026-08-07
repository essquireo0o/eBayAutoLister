using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The three earned milestones have to survive a restart or the getting-started panel lies. The
/// only other record that a comp lookup or a publish ever happened is the in-memory ActionLog,
/// which holds a hundred entries and dies with the process — a seller who priced something on
/// Monday would be told on Tuesday to go and do it again.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class OnboardingStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"onboarding_{Guid.NewGuid():N}.db");

    private OnboardingStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void NothingIsReachedOnAFreshInstall()
    {
        var facts = NewStore().Facts(hasAiKey: false, ebayConnected: false);

        Assert.Null(facts.PricedAt);
        Assert.Null(facts.WrittenAt);
        Assert.Null(facts.PublishedAt);
        Assert.False(facts.Dismissed);
        Assert.False(facts.WelcomeSeen);
    }

    [Fact]
    public void AReachedMilestoneSurvivesARestart()
    {
        NewStore().Reach(OnboardingProgress.Milestones.Published);

        // A fresh store over the same file, as if the app had been closed and reopened.
        Assert.NotNull(NewStore().Facts(false, false).PublishedAt);
    }

    [Fact]
    public void TheFirstTimeWins()
    {
        var first = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        var later = first.AddDays(30);

        var store = NewStore();
        store.Reach(OnboardingProgress.Milestones.Priced, first);
        store.Reach(OnboardingProgress.Milestones.Priced, later);

        // "You priced your first item on Aug 1" has to stay true after the hundredth one.
        Assert.Equal(first, store.ReachedAt(OnboardingProgress.Milestones.Priced));
    }

    [Fact]
    public void OnlyTheFirstCallReportsThatItRecordedAnything()
    {
        var store = NewStore();

        Assert.True(store.Reach(OnboardingProgress.Milestones.Written));
        Assert.False(store.Reach(OnboardingProgress.Milestones.Written));
    }

    [Fact]
    public void AnUnknownMilestoneIsRefusedRatherThanStored()
    {
        var store = NewStore();

        // These calls sit inside endpoints that have already succeeded. A typo must not quietly
        // create a row nothing will ever read, and must not throw into a publish that worked.
        Assert.False(store.Reach("sold"));
        Assert.False(store.Reach(""));
        Assert.Null(store.ReachedAt("sold"));
    }

    [Fact]
    public void MilestonesAreRecordedIndependently()
    {
        var store = NewStore();
        store.Reach(OnboardingProgress.Milestones.Priced);

        var facts = store.Facts(false, false);
        Assert.NotNull(facts.PricedAt);
        Assert.Null(facts.WrittenAt);
        Assert.Null(facts.PublishedAt);
    }

    [Fact]
    public void DismissalIsRememberedAndCanBeTakenBack()
    {
        var store = NewStore();

        store.SetDismissed(true);
        Assert.True(NewStore().Facts(false, false).Dismissed);

        // "Show the getting-started steps again" in Settings.
        store.SetDismissed(false);
        Assert.False(NewStore().Facts(false, false).Dismissed);
    }

    [Fact]
    public void DismissingDoesNotForgetWhatWasAlreadyDone()
    {
        var store = NewStore();
        store.Reach(OnboardingProgress.Milestones.Priced);
        store.SetDismissed(true);

        var facts = store.Facts(false, false);
        Assert.True(facts.Dismissed);
        Assert.NotNull(facts.PricedAt);
    }

    [Fact]
    public void TheWelcomeScreenIsOnlyEverShownOnce()
    {
        NewStore().MarkWelcomeSeen();

        var facts = NewStore().Facts(false, false);
        Assert.True(facts.WelcomeSeen);
        Assert.False(OnboardingProgress.Build(facts).ShowWelcome);
    }

    [Fact]
    public void ResetPutsThePanelBackToItsFirstRunState()
    {
        var store = NewStore();
        store.Reach(OnboardingProgress.Milestones.Priced);
        store.Reach(OnboardingProgress.Milestones.Published);
        store.MarkWelcomeSeen();
        store.SetDismissed(true);

        store.Reset();

        var facts = NewStore().Facts(false, false);
        Assert.Null(facts.PricedAt);
        Assert.Null(facts.PublishedAt);
        Assert.False(facts.WelcomeSeen);
        Assert.False(facts.Dismissed);
        Assert.True(OnboardingProgress.Build(facts).ShowWelcome);
    }

    [Fact]
    public void FactsCarryTheCredentialStateStraightThrough()
    {
        // The two logins are credentials.json's answer, not this store's — it must not invent them.
        var facts = NewStore().Facts(hasAiKey: true, ebayConnected: false);

        Assert.True(facts.HasAiKey);
        Assert.False(facts.EbayConnected);
    }

    // ── What Anthropic said about the key ────────────────────────────────────

    [Fact]
    public void AnUncheckedKeyIsUntestedRatherThanBroken()
    {
        var (state, at) = NewStore().KeyCheck();

        Assert.Equal(AiKeyCheck.Untested, state);
        Assert.Null(at);
        // And that has to be the state the plan sees, or every install ever made would suddenly
        // be told its key does not work.
        Assert.Equal(AiKeyCheck.Untested, NewStore().Facts(true, false).AiKeyState);
    }

    [Fact]
    public void AVerdictSurvivesARestart()
    {
        var when = new DateTimeOffset(2026, 8, 7, 9, 30, 0, TimeSpan.Zero);
        NewStore().RecordKeyCheck(AiKeyCheck.Rejected, when);

        // Closed and reopened: a rejected key is still rejected in the morning.
        var facts = NewStore().Facts(hasAiKey: true, ebayConnected: false);
        Assert.Equal(AiKeyCheck.Rejected, facts.AiKeyState);
        Assert.Equal(when, facts.AiKeyCheckedAt);
        Assert.False(OnboardingProgress.Build(facts).Steps.First().Done);
    }

    [Fact]
    public void TheLastVerdictWinsUnlikeAMilestone()
    {
        var store = NewStore();
        store.RecordKeyCheck(AiKeyCheck.Rejected);
        store.RecordKeyCheck(AiKeyCheck.Works);

        // The opposite rule to Reach. A milestone is something that happened once; this is the
        // current state of one key, and a seller who fixes it has to be able to clear the red.
        Assert.Equal(AiKeyCheck.Works, store.KeyCheck().State);
    }

    [Fact]
    public void SavingANewKeyForgetsTheOldVerdict()
    {
        var store = NewStore();
        store.RecordKeyCheck(AiKeyCheck.NoCredit);

        // What /api/setup/save does the moment a new key lands: the previous answer was about a
        // key that is no longer here, and saying anything about the new one would be a guess.
        store.RecordKeyCheck(AiKeyCheck.Untested);

        Assert.Equal(AiKeyCheck.Untested, store.KeyCheck().State);
        Assert.Null(store.KeyCheck().At);
        Assert.True(OnboardingProgress.Build(store.Facts(true, false)).Steps.First().Done);
    }

    [Fact]
    public void AnUnknownVerdictIsNotStoredAsItself()
    {
        var store = NewStore();
        store.RecordKeyCheck("something-else");

        Assert.Equal(AiKeyCheck.Untested, store.KeyCheck().State);
    }

    [Fact]
    public void TheVerdictIsNotAMilestoneAndDoesNotShowUpAsOne()
    {
        var store = NewStore();
        store.RecordKeyCheck(AiKeyCheck.Works);

        var facts = store.Facts(true, false);
        Assert.Null(facts.PricedAt);
        Assert.Null(facts.WrittenAt);
        Assert.Null(facts.PublishedAt);
        Assert.False(facts.Dismissed);
        Assert.False(facts.WelcomeSeen);
    }

    [Fact]
    public void ResetForgetsTheVerdictWithEverythingElse()
    {
        var store = NewStore();
        store.RecordKeyCheck(AiKeyCheck.Rejected);

        store.Reset();

        Assert.Equal(AiKeyCheck.Untested, NewStore().KeyCheck().State);
    }

    [Fact]
    public void AStoreOpenedOverADatabaseWrittenBeforeThisFeatureStillWorks()
    {
        // The migration. An install from last week has an onboarding table with two columns; the
        // note column is added on the way past, and nothing that already worked may break.
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE onboarding (key TEXT PRIMARY KEY, reached_at TEXT NOT NULL DEFAULT '');
                INSERT INTO onboarding (key, reached_at) VALUES ('priced', '2026-08-01T12:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        var store = NewStore();

        Assert.NotNull(store.ReachedAt(OnboardingProgress.Milestones.Priced));
        Assert.Equal(AiKeyCheck.Untested, store.KeyCheck().State);

        store.RecordKeyCheck(AiKeyCheck.Works);
        Assert.Equal(AiKeyCheck.Works, NewStore().KeyCheck().State);
    }
}
