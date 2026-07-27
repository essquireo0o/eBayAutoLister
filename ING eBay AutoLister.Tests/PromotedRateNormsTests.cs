using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class PromotedRateNormsTests
{
    [Theory]
    [InlineData("Cell Phones & Accessories", "Cell Phones & Accessories")]
    [InlineData("Computers/Tablets & Networking", "Computers & Networking")]
    [InlineData("Video Games & Consoles", "Video Games & Consoles")]
    [InlineData("Sports Mem, Cards & Fan Shop", "Trading Cards & Sports Mem")]
    [InlineData("Clothing, Shoes & Accessories", "Clothing, Shoes & Bags")]
    [InlineData("Business & Industrial", "Business & Industrial")]
    [InlineData("eBay Motors", "eBay Motors")]
    [InlineData("Jewelry & Watches", "Jewelry & Watches")]
    public void Resolve_MatchesTheCategoriesEbayActuallyReports(string categoryName, string expectedLabel)
    {
        var rate = PromotedRateNorms.Resolve(categoryName);

        Assert.Equal(expectedLabel, rate.Label);
        Assert.True(rate.Matched);
        Assert.InRange(rate.TypicalRatePercent, 2m, 20m);
    }

    // "Cell Phones & Accessories" contains "accessories", which also belongs to clothing. The
    // specific entry has to win, or every phone listing gets an 11% norm.
    [Fact]
    public void Resolve_PrefersTheSpecificCategoryOverAGenericKeywordItAlsoContains()
    {
        Assert.Equal("Cell Phones & Accessories", PromotedRateNorms.Resolve("Cell Phones & Accessories").Label);
        Assert.Equal("Computers & Networking", PromotedRateNorms.Resolve("Computers/Tablets & Networking:Tablets").Label);
    }

    [Fact]
    public void Resolve_MatchesOnAFullCategoryPath()
    {
        var rate = PromotedRateNorms.Resolve(
            "Computers/Tablets & Networking:Enterprise Networking, Servers:Servers, Clients & Terminals");
        Assert.Equal("Computers & Networking", rate.Label);
    }

    // An unrecognised or missing category is not a reason to refuse an answer — it is a reason to
    // say the number is the cross-category average.
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Wibble Widgets 3000")]
    public void Resolve_FallsBackToTheCrossCategoryAverage_AndSaysSo(string? categoryName)
    {
        var rate = PromotedRateNorms.Resolve(categoryName);

        Assert.Equal(PromotedRateNorms.DefaultRatePercent, rate.TypicalRatePercent);
        Assert.Equal("default", rate.Basis);
        Assert.False(rate.Matched);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive() =>
        Assert.Equal("Jewelry & Watches", PromotedRateNorms.Resolve("JEWELRY & WATCHES").Label);

    // The seller's own Seller Hub figure always beats the published table.
    [Fact]
    public void Override_TakesTheSellersNumber_ButKeepsTheCategoryLabel()
    {
        var rate = PromotedRateNorms.Override(4.25m, "Consumer Electronics");

        Assert.Equal(4.25m, rate.TypicalRatePercent);
        Assert.Equal("Consumer Electronics", rate.Label);
        Assert.Equal("seller", rate.Basis);
    }

    [Fact]
    public void Override_ClampsARateTheMathCouldNotSurvive()
    {
        Assert.Equal(100m, PromotedRateNorms.Override(9_999m, "").TypicalRatePercent);
        Assert.True(PromotedRateNorms.Override(-5m, "").TypicalRatePercent > 0m);
    }

    [Fact]
    public void Competition_TracksTheRate()
    {
        Assert.Equal("very high", PromotedRateNorms.CompetitionFor(11m));
        Assert.Equal("high", PromotedRateNorms.CompetitionFor(8.5m));
        Assert.Equal("moderate", PromotedRateNorms.CompetitionFor(6m));
        Assert.Equal("lower", PromotedRateNorms.CompetitionFor(4.5m));
    }

    // Every published rate has to be a rate eBay would actually carry, and none of them should sit
    // at a level the app itself calls a clearance decision.
    [Fact]
    public void EveryPublishedRate_IsInsideTheRangeTheAdvisorWillRecommend()
    {
        Assert.All(PromotedRateNorms.All(), rate =>
        {
            Assert.InRange(rate.TypicalRatePercent,
                PromotedRateNorms.EbayMinimumRatePercent, PromotedRateNorms.MaxRecommendedRatePercent);
            Assert.False(string.IsNullOrWhiteSpace(rate.Label));
        });
    }
}
