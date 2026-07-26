using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Craigslist is organised by metro, so a search has to pick a board before it can ask anything.
// Getting that wrong doesn't skew a price — craigslist filters by postal+distance itself — it
// searches the wrong city entirely, which is why the choice is reported and overridable.
public class CraigslistSitesTests
{
    [Theory]
    [InlineData("89101", "lasvegas")]
    [InlineData("89014", "lasvegas")]   // Henderson
    [InlineData("94103", "sfbay")]
    [InlineData("10001", "newyork")]
    [InlineData("60614", "chicago")]
    [InlineData("77002", "houston")]
    [InlineData("98101", "seattle")]
    [InlineData("30303", "atlanta")]
    [InlineData("02108", "boston")]     // leading zero survives the parse
    public void Resolve_MapsAZipToItsOwnMetro(string zip, string expected) =>
        Assert.Equal(expected, CraigslistSites.Resolve(zip)?.Id);

    // The seller knows their own board better than a prefix table does.
    [Fact]
    public void Resolve_ExplicitSiteAlwaysWins() =>
        Assert.Equal("yuma", CraigslistSites.Resolve("89101", "yuma")?.Id);

    [Fact]
    public void Resolve_UnknownSiteName_FallsBackToTheZip() =>
        Assert.Equal("lasvegas", CraigslistSites.Resolve("89101", "not-a-real-site")?.Id);

    // A prefix nobody covers still has to land somewhere sensible — USPS assigned prefixes
    // geographically, so the numerically nearest one is the neighbouring metro, and the caller
    // reports that it wasn't an exact match.
    [Fact]
    public void Resolve_UncoveredZip_UsesTheNearestPrefixAndSaysItWasntExact()
    {
        var site = CraigslistSites.Resolve("96001x");    // nonsense suffix, 960 is covered
        Assert.Equal("redding", site?.Id);

        Assert.False(CraigslistSites.IsExactZipMatch("00501"));
        Assert.NotNull(CraigslistSites.Resolve("00501"));
        Assert.True(CraigslistSites.IsExactZipMatch("89101"));
    }

    // A Canadian postal code or a typo has no craigslist metro at all, and guessing at one would
    // search a random city rather than admit it.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("M5V 3L9")]
    [InlineData("891")]
    public void Resolve_WithoutAUsableZip_ReturnsNothing(string? zip) =>
        Assert.Null(CraigslistSites.Resolve(zip));

    [Fact]
    public void Zip3Of_TakesTheFirstThreeDigitsOfAFiveDigitZip()
    {
        Assert.Equal(891, CraigslistSites.Zip3Of("89101-1234"));
        Assert.Equal(70, CraigslistSites.Zip3Of("07030"));
        Assert.Null(CraigslistSites.Zip3Of("1234"));
    }

    [Fact]
    public void SiteIdsAreUniqueAndLowercase()
    {
        // The id is a subdomain — an uppercase or duplicated one is a URL that doesn't resolve.
        Assert.Equal(CraigslistSites.All.Length, CraigslistSites.All.Select(s => s.Id).Distinct().Count());
        Assert.All(CraigslistSites.All, s => Assert.Equal(s.Id.ToLowerInvariant(), s.Id));
    }

    // First registration wins where two metros share a prefix, so the table's ordering is load
    // bearing: 853 is Glendale AZ (Phoenix) before it's Yuma.
    [Fact]
    public void SharedPrefixesResolveToTheLargerMarket() =>
        Assert.Equal("phoenix", CraigslistSites.Resolve("85301")?.Id);
}
