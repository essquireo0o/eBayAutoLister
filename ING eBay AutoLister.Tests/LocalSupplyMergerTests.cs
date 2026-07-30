using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// One ranked table, several sites. These cases pin the two rules that decide whether a
// multi-source search is honest: a site that couldn't answer must never blank out one that did,
// and the analysis budget has to be shared rather than eaten by whichever site is chattiest.
public class LocalSupplyMergerTests
{
    private static LocalSupplyListing Item(string source, string id, decimal? price, string title = "Antminer S19") =>
        new() { Source = source, SourceLabel = source, ItemId = id, Title = title, Price = price, IsFree = price is null };

    private static LocalSupplySearchResult Result(string source, string status, params LocalSupplyListing[] items) =>
        new() { SourceId = source, SourceLabel = source, Status = status, Items = [.. items], SearchUrl = $"https://{source}/search" };

    // ── Status roll-up ─────────────────────────────────────────────────────────

    [Fact]
    public void RollUpStatus_OneSiteAnswering_MakesTheWholeSearchOk()
    {
        var status = LocalSupplyMerger.RollUpStatus([
            Result("craigslist", "ok", Item("craigslist", "1", 300m)),
            Result("facebook", "not_connected"),
        ]);

        // The whole point of pluggable sources: an unconnected Facebook is not an empty search.
        Assert.Equal("ok", status);
    }

    [Fact]
    public void RollUpStatus_NothingAnswered_PrefersTheReasonTheSellerCanActOn()
    {
        Assert.Equal("session_expired", LocalSupplyMerger.RollUpStatus([
            Result("craigslist", "error"), Result("facebook", "session_expired")]));

        Assert.Equal("not_connected", LocalSupplyMerger.RollUpStatus([
            Result("craigslist", "error"), Result("facebook", "not_connected")]));

        Assert.Equal("error", LocalSupplyMerger.RollUpStatus([Result("craigslist", "error")]));
    }

    [Fact]
    public void RollUpStatus_NoSourcesSelectedAtAll_SaysSo() =>
        Assert.Equal("no_sources", LocalSupplyMerger.RollUpStatus([]));

    // A site the scan ran out of time to reach must not blank out one that answered — the same rule
    // as a disconnected Facebook, and the reason a partial board is worth showing at all.
    [Fact]
    public void RollUpStatus_ASiteWasSkippedForTime_TheSearchIsStillOk()
    {
        var status = LocalSupplyMerger.RollUpStatus([
            Result("craigslist", "ok", Item("craigslist", "1", 300m)),
            Result("facebook", LocalSupplyGuard.SkippedStatus),
        ]);

        Assert.Equal("ok", status);
    }

    // "Wasn't searched" says nothing the seller can act on when another site actually broke. The
    // error is the headline; the skipped site is still on its own chip.
    [Fact]
    public void RollUpStatus_NothingAnswered_AnErrorOutranksASiteThatWasNeverAsked() =>
        Assert.Equal("error", LocalSupplyMerger.RollUpStatus([
            Result("craigslist", "error"), Result("facebook", LocalSupplyGuard.SkippedStatus)]));

    // Every site skipped is its own answer and not an error: nothing was tried, so nothing failed.
    [Fact]
    public void RollUpStatus_EverySiteSkipped_SaysSoRatherThanBlamingTheSites() =>
        Assert.Equal(LocalSupplyGuard.SkippedStatus, LocalSupplyMerger.RollUpStatus([
            Result("craigslist", LocalSupplyGuard.SkippedStatus),
            Result("facebook", LocalSupplyGuard.SkippedStatus)]));

    // ── Merge ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Merge_CombinesEverySiteIntoOneCheapestFirstList()
    {
        var merged = LocalSupplyMerger.Merge([
            Result("craigslist", "ok", Item("craigslist", "1", 900m), Item("craigslist", "2", 300m)),
            Result("facebook", "ok", Item("facebook", "1", 500m)),
        ], "antminer", "89101", 25);

        Assert.Equal([300m, 500m, 900m], merged.Items.Select(i => i.Price));
        Assert.Equal(300m, merged.Min);
        Assert.Equal(500m, merged.Median);
        Assert.Equal(900m, merged.Max);
        Assert.Equal(2, merged.Sources.Count);
    }

    // Post ids are unique per site, not across sites — keying on the id alone would silently drop
    // a real Facebook listing because Craigslist happened to use the same number.
    [Fact]
    public void Merge_SameIdOnTwoDifferentSites_IsTwoListings()
    {
        var merged = LocalSupplyMerger.Merge([
            Result("craigslist", "ok", Item("craigslist", "7712345678", 300m)),
            Result("facebook", "ok", Item("facebook", "7712345678", 400m)),
        ], "antminer", "89101", 25);

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void Merge_KeepsPerSourceErrorsWithoutFailingTheWholeSearch()
    {
        var failed = Result("facebook", "error");
        failed.Error = "Facebook timed out.";

        var merged = LocalSupplyMerger.Merge([
            Result("craigslist", "ok", Item("craigslist", "1", 300m)), failed,
        ], "antminer", "89101", 25);

        Assert.Equal("ok", merged.Status);
        Assert.Null(merged.Error);   // a whole-search error over real results reads as a failure
        Assert.Equal("Facebook timed out.", merged.Sources.Single(s => s.Id == "facebook").Error);
    }

    [Fact]
    public void Merge_NothingWorked_SurfacesTheFirstRealError()
    {
        var failed = Result("craigslist", "error");
        failed.Error = "Craigslist is rate-limiting this connection right now.";

        var merged = LocalSupplyMerger.Merge([failed, Result("facebook", "not_connected")], "antminer", "89101", 25);

        Assert.Equal("not_connected", merged.Status);
        Assert.Equal("Craigslist is rate-limiting this connection right now.", merged.Error);
    }

    // ── Sharing the analysis budget ────────────────────────────────────────────

    [Fact]
    public void TakeBalanced_SharesTheCapAcrossSitesInsteadOfLettingOneEatIt()
    {
        // Craigslist returns 50 cheap rows from one HTTP call; Facebook a handful from an
        // expensive page load. Cheapest-first over the merged list would price only Craigslist
        // and then report Facebook as having no local supply.
        var craigslist = Enumerable.Range(1, 50).Select(i => Item("craigslist", i.ToString(), i));
        var facebook = Enumerable.Range(1, 5).Select(i => Item("facebook", i.ToString(), 800m + i));

        var taken = LocalSupplyMerger.TakeBalanced(craigslist.Concat(facebook), 10);

        Assert.Equal(10, taken.Count);
        Assert.Equal(5, taken.Count(i => i.Source == "facebook"));
        Assert.Equal(5, taken.Count(i => i.Source == "craigslist"));
    }

    [Fact]
    public void TakeBalanced_OneSiteRunsOut_TheOtherFillsTheRest()
    {
        var taken = LocalSupplyMerger.TakeBalanced(
            [Item("craigslist", "1", 10m), Item("craigslist", "2", 20m), Item("facebook", "1", 30m)], 10);

        Assert.Equal(3, taken.Count);
    }

    [Fact]
    public void TakeBalanced_TakesTheCheapestFromEachSiteFirst()
    {
        var taken = LocalSupplyMerger.TakeBalanced(
            [Item("craigslist", "1", 900m), Item("craigslist", "2", 100m), Item("facebook", "1", 500m)], 2);

        Assert.Equal(["100", "500"], taken.Select(i => i.Price?.ToString("0")));
    }

    // A free item is the cheapest cost basis there is, not a missing price — it must not sort to
    // the back of its own queue and get cut by the cap.
    [Fact]
    public void TakeBalanced_FreeItemsGoFirstWithinTheirSite()
    {
        var taken = LocalSupplyMerger.TakeBalanced(
            [Item("craigslist", "1", 900m), Item("craigslist", "2", null)], 1);

        Assert.True(taken.Single().IsFree);
    }

    [Fact]
    public void TakeBalanced_ZeroCap_TakesNothing() =>
        Assert.Empty(LocalSupplyMerger.TakeBalanced([Item("craigslist", "1", 10m)], 0));
}
