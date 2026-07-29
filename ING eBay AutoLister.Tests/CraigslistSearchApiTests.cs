using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The craigslist path that carries photographs.
///
/// Every fixture below is real: captured from craigslist's own search endpoint for the free-stuff
/// board within 40 miles of 02341 — the exact search a seller ran when they reported that every
/// row on the deals board showed the empty box instead of a picture.
/// </summary>
public class CraigslistSearchApiTests
{
    /// <summary>
    /// Three real posts, unedited, wrapped in the envelope craigslist sends them in.
    ///
    /// Chosen because between them they carry the three cases that matter:
    ///   • two DIFFERENT couches whose titles are the same word ("FREE couch" / "FREE COUCH"),
    ///     which is what makes a title an unusable key on this board, and
    ///   • a post with no photograph at all, which has to stay photograph-less rather than
    ///     inherit somebody else's.
    /// </summary>
    private const string FreeBoardResponse = """
        {"apiVersion":8,"data":{
          "decode":{"locationDescriptions":[0,"Boston","East Boston","Malden","Natick","East Side","Winchester","Cambridge",
            "Somerville","Quincy","Brookline","Newton","Waltham","Arlington","Lynn","Everett","Medford","North Attleboro"]},
          "items":[
            [33699165,43323,101,-1,"1:7~42.3611~-71.1041","0CI0t2",[13,"5m1KAEjDtEWgSuW542a2vr"],
              [4,"3:00808_c9VdtsKTgCq_0CI0t2","3:00B0B_51xjOLbg8pK_0CI0t2","3:01717_4nqnfjsSicu_0CI0t2"],
              [6,"cambridge-free-couch"],[10,"free"],"FREE couch"],
            [34681668,22113,101,-1,"1:16~42.4173~-71.1087","0t20CI",[13,"3FMVR3PDpPpV3ex6JkYiqX"],
              [4,"3:00I0I_dolHrB30uEb_0t20CI","3:00r0r_i1xMqmrOoB2_0CI0t2","3:00M0M_7g9M4XezzXn_0t20CI"],
              [6,"medford-free-couch"],[10,"free"],"FREE COUCH"],
            [33646261,185418,101,-1,"4:17~41.9834~-71.3367",0,[13,"uAZ8PZUM1M3xHhoEPU853t"],
              [6,"north-attleboro-box-fan"],[10,"free"],"Box Fan"]
          ]}}
        """;

    // A real for-sale post, which differs in the two ways that matter: it has a price, and
    // craigslist states that price twice — as a display string and as a bare number.
    private const string PricedBoardResponse = """
        {"apiVersion":8,"data":{
          "decode":{"locationDescriptions":[0,"Norwood"]},
          "items":[
            [5867177,1397103,96,20,"2:1~42.1779~-71.197","0hS0CI",[13,"wQZGmnyfErvk2E17Ekvatg"],
              [4,"3:00202_VxA8SwpGX0_0hS0CI","3:00L0L_6Fk3Bh4SCX5_0hS0CI"],
              [6,"norwood-microsoft-modern-usb-headset"],[10,"$20"],"Microsoft modern usb- c headset"]
          ]}}
        """;

    // ── The bug this path exists to fix ────────────────────────────────────────

    /// <summary>
    /// The reported bug, as a test. Two posts, same title, different couches — and before this the
    /// only key available was that title, which cannot tell them apart even in principle.
    /// </summary>
    [Fact]
    public void TwoPostsWithTheSameTitleEachGetTheirOwnPhoto()
    {
        var items = CraigslistSearchApi.Parse(FreeBoardResponse, freeBoard: true);

        var couches = items.Where(i => i.Title.Contains("couch", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, couches.Count);

        Assert.Equal("https://images.craigslist.org/00808_c9VdtsKTgCq_0CI0t2_600x450.jpg", couches[0].ImageUrl);
        Assert.Equal("https://images.craigslist.org/00I0I_dolHrB30uEb_0t20CI_600x450.jpg", couches[1].ImageUrl);
        Assert.NotEqual(couches[0].ImageUrl, couches[1].ImageUrl);
    }

    /// <summary>
    /// The free board is the one that was completely blank, so it gets the direct assertion: a
    /// post that has a photo comes back with it.
    /// </summary>
    [Fact]
    public void AFreeBoardPostCarriesItsRealThumbnail()
    {
        var couch = CraigslistSearchApi.Parse(FreeBoardResponse, freeBoard: true)[0];

        Assert.Equal("https://images.craigslist.org/00808_c9VdtsKTgCq_0CI0t2_600x450.jpg", couch.ImageUrl);
        Assert.True(couch.IsFree);
        Assert.Equal("5m1KAEjDtEWgSuW542a2vr", couch.ItemId);
        Assert.Equal("https://www.craigslist.org/view/d/cambridge-free-couch/5m1KAEjDtEWgSuW542a2vr", couch.Url);
    }

    /// <summary>
    /// The genuinely photograph-less post — 8 of the 30 rows on the reported search are these, and
    /// they are the only rows the empty box is allowed to mean anything about. Inventing a URL
    /// here would be worse than the bug: a broken image reads as the app failing.
    /// </summary>
    [Fact]
    public void APostWithNoPhotoGetsNoPhotoRatherThanSomebodyElses()
    {
        var fan = CraigslistSearchApi.Parse(FreeBoardResponse, freeBoard: true)
            .Single(i => i.Title == "Box Fan");

        Assert.Equal("", fan.ImageUrl);
    }

    // The rest of the row still has to be right — a photo is not worth a wrong price or a
    // wrong link.
    [Fact]
    public void APricedPostKeepsItsPriceTitleAndLink()
    {
        var item = Assert.Single(CraigslistSearchApi.Parse(PricedBoardResponse));

        Assert.Equal("Microsoft modern usb- c headset", item.Title);
        Assert.Equal(20m, item.Price);
        Assert.False(item.IsFree);
        Assert.Equal("Norwood", item.Location);
        Assert.Equal("https://images.craigslist.org/00202_VxA8SwpGX0_0hS0CI_600x450.jpg", item.ImageUrl);
    }

    /// <summary>
    /// The town is the index AFTER the colon. The two indexes are equal often enough that reading
    /// the wrong one looks correct on a spot check — this pins the couch that proves otherwise,
    /// whose geo is "1:7" and which is in Cambridge, not Boston.
    /// </summary>
    [Fact]
    public void TheTownIsReadFromTheSecondIndexNotTheFirst()
    {
        var items = CraigslistSearchApi.Parse(FreeBoardResponse, freeBoard: true);

        Assert.Equal("Cambridge", items[0].Location);
        Assert.Equal("Medford", items[1].Location);
        Assert.Equal("North Attleboro", items[2].Location);
    }

    // ── Degrading, rather than failing ─────────────────────────────────────────

    // Craigslist moving this endpoint has to look like "no results", because that is the signal
    // CraigslistService already acts on by falling back to the HTML page. A throw here would take
    // the whole scan down with it.
    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"data":{}}""")]
    [InlineData("""{"data":{"items":"moved"}}""")]
    public void AResponseThisParserDoesNotUnderstandYieldsNothingRatherThanThrowing(string body)
    {
        Assert.Empty(CraigslistSearchApi.Parse(body));
    }

    // A post with no price and no free-board to make it free has no cost basis, which is the same
    // bar the RSS and HTML paths hold.
    [Fact]
    public void APostWithNoPriceIsDroppedOffThePricedBoard()
    {
        const string body = """
            {"data":{"items":[[123,1,96,-1,"1:1~42.0~-71.0","0a0b",[13,"tok"],[6,"slug"],"Some thing"]]}}
            """;

        Assert.Empty(CraigslistSearchApi.Parse(body));
        Assert.Single(CraigslistSearchApi.Parse(body, freeBoard: true));
    }

    // ── The request ────────────────────────────────────────────────────────────

    [Fact]
    public void TheSearchUrlCarriesTheBoardTheZipAndTheRadius()
    {
        var url = CraigslistSearchApi.BuildUrl("dewalt", "02341", 40, FreebieCatalog.CraigslistFreeCategory);

        Assert.Contains("searchPath=zip", url);
        Assert.Contains("query=dewalt", url);
        Assert.Contains("postal=02341", url);
        Assert.Contains("search_distance=40", url);
    }

    // Radius without a postal code is meaningless to craigslist, and sending one alone invites it
    // to ignore both — the same rule the results-page URL follows.
    [Fact]
    public void NoZipMeansNoRadiusEither()
    {
        var url = CraigslistSearchApi.BuildUrl("dewalt", "", 40);

        Assert.DoesNotContain("postal=", url);
        Assert.DoesNotContain("search_distance=", url);
    }

    // The board code goes into the query string, and craigslist's own vocabulary is the only thing
    // that belongs there — same guard as CraigslistParser.BuildSearchUrl.
    [Theory]
    [InlineData("../../etc")]
    [InlineData("sss&evil=1")]
    [InlineData("")]
    public void AnythingThatIsNotACraigslistBoardCodeFallsBackToForSale(string category)
    {
        Assert.Contains($"searchPath={CraigslistParser.ForSaleCategory}", CraigslistSearchApi.BuildUrl("x", "02341", 40, category));
    }
}
