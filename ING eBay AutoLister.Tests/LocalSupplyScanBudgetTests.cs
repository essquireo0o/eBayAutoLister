using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The failure this class exists to stop: a scan whose sites, searched one at a time, between them
// outlast the browser waiting on the answer. When that happened the seller lost everything — the
// fetch aborted at eight minutes and the forty Craigslist rows that had arrived in four seconds
// went with it, because the two slow sites after them were still being waited on.
//
// LocalSupplyGuard bounds one source. These cases pin the rule that bounds the sum of them.
public class LocalSupplyScanBudgetTests
{
    private sealed class StubSource(string id, bool requiresConnection = false) : ILocalSupplySource
    {
        public string Id => id;
        public string Label => id;
        public bool RequiresConnection => requiresConnection;
        public bool IsAvailable => true;
        public string AvailabilityNote => "ready";

        public Task<LocalSupplySearchResult> SearchAsync(string query, string zip, int radiusMiles, CancellationToken ct = default) =>
            Task.FromResult(new LocalSupplySearchResult { SourceId = id, SourceLabel = id, Status = "ok" });
    }

    private static readonly StubSource Craigslist = new("craigslist");
    private static readonly StubSource Facebook = new("facebook", requiresConnection: true);

    // ── Handing out the time ───────────────────────────────────────────────────

    // Nothing spent yet, so each site gets exactly what LocalSupplyGuard would have given it on its
    // own. A one-source scan must never be cut short by a budget meant for six.
    [Fact]
    public void Allow_WithTheWholeBudgetLeft_GivesEachSourceItsOwnTimeout()
    {
        var budget = new LocalSupplyScanBudget();

        Assert.Equal(LocalSupplyGuard.TimeoutFor(Craigslist), budget.Allow(Craigslist));
        Assert.Equal(LocalSupplyGuard.TimeoutFor(Facebook), budget.Allow(Facebook));
        Assert.False(budget.Exhausted);
    }

    // A source's own budget is the ceiling, never the floor. Facebook ordinarily gets three
    // minutes; with ninety seconds left in the scan it gets ninety seconds.
    [Fact]
    public void Allow_MoreTimeWantedThanRemains_HandsOverWhatIsLeft()
    {
        var budget = new LocalSupplyScanBudget(TimeSpan.FromMinutes(5));
        budget.Spend(TimeSpan.FromMinutes(3.5));

        var slice = budget.Allow(Facebook);

        Assert.Equal(TimeSpan.FromMinutes(1.5), slice);
        Assert.True(slice < LocalSupplyGuard.TimeoutFor(Facebook));
    }

    // The fast sites must not spend the budget they didn't use. Craigslist answering in four
    // seconds has to leave the rest of the scan the other forty-one.
    [Fact]
    public void Spend_ChargesWhatTheSearchActuallyTook_NotWhatItWasAllowed()
    {
        var budget = new LocalSupplyScanBudget(TimeSpan.FromMinutes(5));

        budget.Spend(TimeSpan.FromSeconds(4));

        Assert.Equal(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(4), budget.Remaining);
        Assert.Equal(LocalSupplyGuard.TimeoutFor(Facebook), budget.Allow(Facebook));
    }

    // ── Running out ────────────────────────────────────────────────────────────

    // Below the minimum useful slice, searching is a formality: the site gets a few seconds, times
    // out, and the seller waits out the shortfall to be told it failed. Refusing immediately is the
    // same information, sooner, and true rather than blaming the site.
    [Fact]
    public void Allow_TooLittleTimeLeftToBeWorthIt_RefusesRatherThanSettingUpAFailure()
    {
        var budget = new LocalSupplyScanBudget(TimeSpan.FromMinutes(5));
        budget.Spend(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));

        Assert.Null(budget.Allow(Craigslist));
        Assert.True(budget.Exhausted);
    }

    [Fact]
    public void Allow_BudgetOverspent_RemainingIsZeroRatherThanNegative()
    {
        var budget = new LocalSupplyScanBudget(TimeSpan.FromMinutes(1));

        // A source that ignores its token can overrun the slice it was given.
        budget.Spend(TimeSpan.FromMinutes(4));

        Assert.Equal(TimeSpan.Zero, budget.Remaining);
        Assert.Null(budget.Allow(Craigslist));
    }

    // Exhausted is the scan admitting it is incomplete. Until a source is actually refused, it
    // isn't — a budget merely running low has cost nobody anything.
    [Fact]
    public void Exhausted_OnlyOnceASourceHasActuallyBeenRefused()
    {
        var budget = new LocalSupplyScanBudget(TimeSpan.FromMinutes(5));
        budget.Spend(TimeSpan.FromMinutes(4.9));

        Assert.False(budget.Exhausted);
        Assert.Null(budget.Allow(Facebook));
        Assert.True(budget.Exhausted);
    }

    // ── What the seller is told ────────────────────────────────────────────────

    // An "error" would be a lie about a site that was never asked, and would put a blocked-looking
    // chip on a source that is working perfectly — sending the seller off to check a connection
    // that was never the problem.
    [Fact]
    public void Skipped_IsItsOwnStatus_AndDoesNotBlameTheSite()
    {
        var result = LocalSupplyScanBudget.Skipped(Facebook, "drill", "89101", 25);

        Assert.Equal(LocalSupplyGuard.SkippedStatus, result.Status);
        Assert.NotEqual("error", result.Status);
        Assert.Equal("facebook", result.SourceId);
        Assert.Equal("drill", result.Query);
        Assert.Equal("89101", result.ZipCode);
        Assert.Equal(25, result.RadiusMiles);

        // The sentence has to say whose fault it wasn't, and what to do instead.
        Assert.Contains("wasn't searched", result.Error);
        Assert.Contains("fewer sites", result.Error);
        Assert.True(result.Retryable);
    }

    // The guard normalises unrecognised statuses to "error". A skipped result passing through it —
    // which is what happens the moment anything re-reads one — must survive intact.
    [Fact]
    public void Skipped_SurvivesTheGuardsNormalisation()
    {
        var skipped = LocalSupplyScanBudget.Skipped(Facebook, "drill", "89101", 25);

        var normalized = LocalSupplyGuard.Normalize(skipped, Facebook, "drill", "89101", 25);

        Assert.Equal(LocalSupplyGuard.SkippedStatus, normalized.Status);
    }
}
