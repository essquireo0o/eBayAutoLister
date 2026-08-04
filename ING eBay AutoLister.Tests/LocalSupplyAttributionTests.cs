using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A multi-source goldmine board reported per-site counts from the SEARCH and then said nothing
// else, while four filters between the search and the table dropped rows unevenly across sites.
// These cases pin what each chip is now allowed to claim — because the seller's next decision is
// which sites to tick, and every wrong reading of a chip unticks the one that was paying.
public class LocalSupplyAttributionTests
{
    private static LocalSupplyListing Listing(string source, string id = "1") =>
        new() { Source = source, SourceLabel = source, ItemId = id, Title = $"{source}-{id}" };

    private static LocalArbitrageOpportunity Row(string source, decimal? netProfit) =>
        new() { Source = source, SourceLabel = source, NetProfit = netProfit, Title = $"{source} row" };

    private static LocalSupplySourceOutcome Outcome(string id, string status = "ok", int count = 0) =>
        new() { Id = id, Label = id, Status = status, Count = count };

    // ── What each site was worth ───────────────────────────────────────────────

    [Fact]
    public void Apply_SplitsTheBoardsMoneyBetweenTheSitesItCameFrom()
    {
        var craigslist = Outcome("craigslist", count: 40);
        var facebook = Outcome("facebook", count: 4);

        LocalSupplyAttribution.Apply(
            [craigslist, facebook],
            usable: [Listing("craigslist"), Listing("craigslist", "2"), Listing("facebook")],
            analyzed: [Listing("craigslist"), Listing("craigslist", "2"), Listing("facebook")],
            ranked: [Row("craigslist", 300m), Row("craigslist", -20m), Row("facebook", 45m)]);

        Assert.Equal(2, craigslist.Ranked);
        Assert.Equal(1, craigslist.ProfitableCount);
        Assert.Equal(300m, craigslist.PotentialProfit);

        Assert.Equal(1, facebook.Ranked);
        Assert.Equal(45m, facebook.PotentialProfit);
    }

    // A row that lost money is not this site's contribution to the profit — a chip that summed
    // every row would report a site as earning while every deal on it is a deal to walk away from.
    [Fact]
    public void Apply_ALosingRowIsNotMoneyTheSiteMade()
    {
        var craigslist = Outcome("craigslist");

        LocalSupplyAttribution.Apply([craigslist], [Listing("craigslist")], [Listing("craigslist")],
            [Row("craigslist", -80m), Row("craigslist", null)]);

        Assert.Equal(2, craigslist.Ranked);
        Assert.Equal(0, craigslist.ProfitableCount);
        Assert.Equal(0m, craigslist.PotentialProfit);
    }

    // ── The cap, said out loud ────────────────────────────────────────────────

    // "48 found, 12 priced" is the difference between a site with nothing left on it and a site the
    // scan stopped reading. Only the second one has a fix.
    [Fact]
    public void Apply_ASiteTheCapCutShort_SaysSo()
    {
        var craigslist = Outcome("craigslist", count: 40);

        LocalSupplyAttribution.Apply(
            [craigslist],
            usable: [.. Enumerable.Range(1, 40).Select(i => Listing("craigslist", i.ToString()))],
            analyzed: [.. Enumerable.Range(1, 12).Select(i => Listing("craigslist", i.ToString()))],
            ranked: []);

        Assert.Equal(12, craigslist.Analyzed);
        Assert.True(craigslist.Capped);
    }

    [Fact]
    public void Apply_EveryUsableListingPriced_IsNotCapped()
    {
        var facebook = Outcome("facebook", count: 3);

        LocalSupplyAttribution.Apply(
            [facebook],
            usable: [Listing("facebook", "1"), Listing("facebook", "2")],
            analyzed: [Listing("facebook", "1"), Listing("facebook", "2")],
            ranked: [Row("facebook", 10m)]);

        Assert.False(facebook.Capped);
    }

    // Listings screened out before the cap ever ran — a $1 repair service, a post with no price —
    // are not the cap's doing, and a "we stopped looking" note over a site whose junk was filtered
    // sends the seller to raise a limit that was never reached.
    [Fact]
    public void Apply_ScreenedOutListingsAreNotTheCapsFault()
    {
        var craigslist = Outcome("craigslist", count: 40);

        // 40 came back from the search; only 5 were usable, and all 5 were priced.
        LocalSupplyAttribution.Apply(
            [craigslist],
            usable: [.. Enumerable.Range(1, 5).Select(i => Listing("craigslist", i.ToString()))],
            analyzed: [.. Enumerable.Range(1, 5).Select(i => Listing("craigslist", i.ToString()))],
            ranked: []);

        Assert.False(craigslist.Capped);
    }

    // A site that never answered has nothing to attribute. Zeroes are fine; a "capped" badge on a
    // disconnected Facebook would be the app blaming its own limit for a login.
    [Fact]
    public void Apply_ASiteThatNeverAnswered_IsLeftAtZeroAndNotCapped()
    {
        var facebook = Outcome("facebook", "not_connected");

        LocalSupplyAttribution.Apply([facebook], [Listing("craigslist")], [Listing("craigslist")],
            [Row("craigslist", 500m)]);

        Assert.Equal(0, facebook.Analyzed);
        Assert.Equal(0, facebook.Ranked);
        Assert.Equal(0m, facebook.PotentialProfit);
        Assert.False(facebook.Capped);
    }

    [Fact]
    public void Apply_MatchesSourceIdsWithoutCaringAboutCase()
    {
        var craigslist = Outcome("craigslist");

        LocalSupplyAttribution.Apply([craigslist], [Listing("Craigslist")], [Listing("Craigslist")],
            [Row("Craigslist", 120m)]);

        Assert.Equal(1, craigslist.Analyzed);
        Assert.Equal(120m, craigslist.PotentialProfit);
    }

    // ── Which site to open first ──────────────────────────────────────────────

    // Money, not rows. A site with one $340 flip is a better Saturday than a site with nine $6 ones,
    // and ranking on row count would send the seller to the second.
    [Fact]
    public void BestEarner_RanksOnMoneyRatherThanOnHowManyRowsASiteFilled()
    {
        var craigslist = Outcome("craigslist");
        var facebook = Outcome("facebook");

        LocalSupplyAttribution.Apply([craigslist, facebook],
            usable: [], analyzed: [],
            ranked: [.. Enumerable.Repeat(Row("craigslist", 6m), 9), Row("facebook", 340m)]);

        Assert.Equal("facebook", LocalSupplyAttribution.BestEarner([craigslist, facebook])?.Id);
    }

    // Nothing on the board makes money, so there is no site to send anybody to. Naming the least
    // bad one would read as a recommendation to go and buy from it.
    [Fact]
    public void BestEarner_NothingProfitable_NamesNobody()
    {
        var craigslist = Outcome("craigslist");
        var facebook = Outcome("facebook");

        LocalSupplyAttribution.Apply([craigslist, facebook], [], [],
            [Row("craigslist", -10m), Row("facebook", null)]);

        Assert.Null(LocalSupplyAttribution.BestEarner([craigslist, facebook]));
    }

    [Fact]
    public void BestEarner_NoSourcesAtAll_NamesNobody() =>
        Assert.Null(LocalSupplyAttribution.BestEarner([]));
}
