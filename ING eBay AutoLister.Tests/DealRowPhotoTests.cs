using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// One rule, held across every source that can appear on the deals board: <b>if the source page has
/// a photograph of the thing, the row carries it.</b>
///
/// A seller cannot judge a free pool table from the words "Pool Table". On this board the photo is
/// not decoration, it is the decision — so it is pinned here for the table as a whole rather than
/// left to each parser's own file, which is how one source (the Craigslist free board) came to be
/// rendering 30 empty boxes out of 30 while the other four were fine.
///
/// Every fixture is real, captured from the live source.
/// </summary>
public class DealRowPhotoTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    // ── Why the free board was blank ───────────────────────────────────────────

    /// <summary>
    /// The evidence, kept as a test so nobody re-derives the old fix.
    ///
    /// The thumbnail path that came before read the schema.org block out of the results page. This
    /// is that page, as the free-stuff board really serves it: the block is present, so the reader
    /// found it and parsed it happily — and it is EMPTY. Measured live, the whole document was 79
    /// bytes with <c>itemListElement: []</c>, against 279 populated entries on the for-sale board.
    /// There was never a thumbnail on this page to key by title or by anything else, which is why
    /// re-keying alone would have fixed nothing.
    /// </summary>
    [Fact]
    public void TheFreeBoardsResultsPageCarriesNoPhotographsAtAll()
    {
        const string freeBoardPage = """
            <html><body>
            <script type="application/ld+json" id="ld_searchpage_results">
            {"@context":"https://schema.org","itemListElement":[],"@type":"ItemList"}
            </script>
            <ol class="cl-static-search-results">
              <li class="cl-static-search-result" title="FREE couch">
                <a href="https://www.craigslist.org/view/d/cambridge-free-couch/5m1KAEjDtEWgSuW542a2vr">
                  <div class="title">FREE couch</div>
                  <div class="details"><div class="price">$0</div><div class="location">Cambridge</div></div>
                </a>
              </li>
            </ol>
            </body></html>
            """;

        var items = CraigslistParser.ParseStaticHtml(freeBoardPage, freeBoard: true);

        // The row parses — it always did. It simply has no picture available to it anywhere in the
        // response, which is the entire bug the seller reported.
        var couch = Assert.Single(items);
        Assert.Equal("FREE couch", couch.Title);
        Assert.Equal("", couch.ImageUrl);
        Assert.DoesNotContain("images.craigslist.org", freeBoardPage);
    }

    // ── Every source that can reach the table ──────────────────────────────────

    // Craigslist, through the path that does carry photographs. The free board is the case that
    // was broken, so it is the one asserted here.
    [Fact]
    public void CraigslistAFreeBoardPostCarriesItsPhoto()
    {
        const string response = """
            {"data":{"decode":{"locationDescriptions":[0,"Boston","East Boston","Malden","Natick",
              "East Side","Winchester","Cambridge"]},
              "items":[[33699165,43323,101,-1,"1:7~42.3611~-71.1041","0CI0t2",
                [13,"5m1KAEjDtEWgSuW542a2vr"],[4,"3:00808_c9VdtsKTgCq_0CI0t2"],
                [6,"cambridge-free-couch"],[10,"free"],"FREE couch"]]}}
            """;

        var couch = Assert.Single(CraigslistSearchApi.Parse(response, freeBoard: true));

        Assert.Equal("https://images.craigslist.org/00808_c9VdtsKTgCq_0CI0t2_600x450.jpg", couch.ImageUrl);
    }

    /// <summary>
    /// Slickdeals, as its RSS really arrives: the picture is an <c>&lt;img&gt;</c> inside the
    /// CDATA body, never a media element, so the body is where it has to be read from.
    /// </summary>
    [Fact]
    public void ADealFeedRowCarriesItsPhoto()
    {
        const string feedXml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/"><channel>
              <item>
                <title><![CDATA[ASUS 13.4" ROG Flow Z13 2-in-1 Gaming Laptop $3599]]></title>
                <link>https://slickdeals.net/f/19822707-asus-rog-flow-z13</link>
                <description><![CDATA[3.0 GHz AMD Ryzen AI MAX+ 395 16-Core, 128GB LPDDR5x]]></description>
                <content:encoded><![CDATA[<div><img src="https://static.slickdealscdn.com/attachment/1/1/7/9/2/9/8/0/300x300/21140163.thumb" /></div>]]></content:encoded>
                <pubDate>Tue, 28 Jul 2026 14:02:00 +0000</pubDate>
              </item>
            </channel></rss>
            """;

        var deal = Assert.Single(DealFeedParser.ParseFeed(feedXml, DealFeedCatalog.Feeds[0]));

        Assert.Equal("https://static.slickdealscdn.com/attachment/1/1/7/9/2/9/8/0/300x300/21140163.thumb", deal.ImageUrl);
    }

    /// <summary>
    /// HiBid, whose lots carry the picture on the lot record itself. Measured on a live search
    /// page: 162 of 163 lots had a <c>thumbnailLocation</c>, so this source was already correct —
    /// the assertion is here to keep it that way, since the rule is for the table.
    /// </summary>
    [Fact]
    public void ALiquidationLotCarriesItsPhoto()
    {
        const string thumb = "https://cdn.hibid.com/img.axd?id=8206798228&w=0&h=0&sz=MAX";

        var json =
            "{\"apollo.state\":{" +
            "\"Lot:1\":{\"__typename\":\"Lot\",\"id\":1,\"lead\":\"Dyson V8 vacuum\",\"quantity\":1," +
            "\"description\":\"\",\"bidAmount\":123.45," +
            "\"featuredPicture\":{\"__typename\":\"Picture\",\"thumbnailLocation\":\"" + thumb + "\"," +
            "\"hdThumbnailLocation\":\"\",\"fullSizeLocation\":\"\"}," +
            "\"auction\":{\"__ref\":\"Auction:750529\"}," +
            "\"lotState\":{\"__typename\":\"LotState\",\"highBid\":40,\"minBid\":5,\"bidCount\":2," +
            "\"isClosed\":false,\"status\":\"OPEN\",\"timeLeftSeconds\":7200,\"timeLeft\":\"2h  0m  \"}}," +
            "\"Auction:750529\":{\"__typename\":\"Auction\",\"id\":750529,\"eventName\":\"Overstock Liquidation\"," +
            "\"eventCity\":\"Lindon\",\"eventState\":\"UT\",\"buyerPremiumRate\":1.15}," +
            "\"Auctioneer:9\":{\"__typename\":\"Auctioneer\",\"name\":\"Redwood Auctions\"}" +
            "}}";

        // The framework's own five-character escaping, which is what the parser has to undo — the
        // ampersands in the CDN URL are exactly what makes it worth pinning here.
        var escaped = json
            .Replace("&", "&a;").Replace("\"", "&q;").Replace("'", "&s;")
            .Replace("<", "&l;").Replace(">", "&g;");
        var page = $"<html><body><script id=\"hibid-state\" type=\"application/json\">{escaped}</script></body></html>";

        var lot = Assert.Single(LiquidationParser.ParsePage(page, LiquidationCatalog.Feeds[0], new DateTime(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(thumb, lot.ImageUrl);
    }

    // Facebook, whose cards come off the page with the image already on them.
    [Fact]
    public void AFacebookMarketplaceRowCarriesItsPhoto()
    {
        var card = new FacebookRawCard
        {
            Href = "https://www.facebook.com/marketplace/item/1234567890123456/",
            ImageUrl = "https://scontent-bos5-1.xx.fbcdn.net/v/t39.84726-6/photo.jpg",
            Lines = ["$120", "Samsung laptop", "Boston, MA"],
        };

        var listing = FacebookMarketplaceParser.ParseCard(card);

        Assert.NotNull(listing);
        Assert.Equal("https://scontent-bos5-1.xx.fbcdn.net/v/t39.84726-6/photo.jpg", listing!.ImageUrl);
    }

    // ── The whole path, parser to rendered row ─────────────────────────────────

    /// <summary>
    /// The photo has to survive every hop, not just the parser. This is the failure that would be
    /// invisible in a parser test and total on screen: the analyzer builds a new object, and a
    /// field it forgets to copy is a field the board never sees.
    /// </summary>
    [Fact]
    public void ThePhotoSurvivesFromCraigslistAllTheWayOntoTheRowTheBoardRenders()
    {
        const string response = """
            {"data":{"decode":{"locationDescriptions":[0,"Boston","East Boston","Malden","Natick",
              "East Side","Winchester","Cambridge"]},
              "items":[[33699165,43323,101,-1,"1:7~42.3611~-71.1041","0CI0t2",
                [13,"5m1KAEjDtEWgSuW542a2vr"],[4,"3:00808_c9VdtsKTgCq_0CI0t2"],
                [6,"cambridge-free-couch"],[10,"free"],"FREE couch"]]}}
            """;

        // parser → LocalSupplyListing
        var listing = Assert.Single(CraigslistSearchApi.Parse(response, freeBoard: true));
        Assert.NotEqual("", listing.ImageUrl);

        // LocalSupplyListing → the row the UI renders
        var row = Analyzer.Build(listing, Pricing(), new FeeProfile());

        Assert.Equal("https://images.craigslist.org/00808_c9VdtsKTgCq_0CI0t2_600x450.jpg", row.ImageUrl);
    }

    // A row that genuinely has no photo must arrive with none rather than with a guess, because
    // the board renders exactly that difference.
    [Fact]
    public void ARowWithNoPhotoReachesTheBoardWithNoPhoto()
    {
        var listing = new LocalSupplyListing
        {
            Source = "craigslist", SourceLabel = "Craigslist", ItemId = "uAZ8PZUM1M3xHhoEPU853t",
            Title = "Box Fan", Url = "https://www.craigslist.org/view/d/north-attleboro-box-fan/uAZ8PZUM1M3xHhoEPU853t",
            IsFree = true, ImageUrl = "",
        };

        Assert.Equal("", Analyzer.Build(listing, Pricing(), new FeeProfile()).ImageUrl);
    }

    private static ResalePricing Pricing() => new()
    {
        LookupTitle = "couch", Median = 120m, ExpectedSale = 120m, QuickSale = 100m,
        SoldCompCount = 8, PricedCompCount = 8, IdentityVerified = true,
        ConfidenceScore = 70, ConfidenceLevel = "Good",
    };

    // ── What the board actually draws ──────────────────────────────────────────

    private static readonly string Js = ReadAsset("app.js");

    /// <summary>
    /// A row with a photo renders an <c>&lt;img&gt;</c>. Nothing in C# draws this table, so nothing
    /// in C# would fail if the thumbnail were dropped from the markup — which is how it would come
    /// back.
    /// </summary>
    [Fact]
    public void TheBoardRendersAnImageWhenTheRowHasOne()
    {
        var thumb = ThumbnailFunction();

        Assert.Contains("row.imageUrl", thumb);
        Assert.Contains("<img class=\"fb-arb-thumb\"", thumb);
        Assert.Contains("src=\"${esc(row.imageUrl)}\"", thumb);
    }

    /// <summary>
    /// A URL that 404s or is refused by the host it points at has to land on the same "no photo"
    /// box as a post that never had one. Without this the browser draws its own broken-image glyph,
    /// which reads as the app being broken rather than the post being bare.
    /// </summary>
    [Fact]
    public void ABrokenPhotoFallsBackInsteadOfShowingABrokenImage()
    {
        Assert.Contains("onerror=\"__arbThumbFailed(this)\"", ThumbnailFunction());

        // And the handler has to actually put the box there, not just hide the image.
        Assert.Contains("window.__arbThumbFailed", Js);
        Assert.Contains("fb-arb-thumb-empty", Js);
        Assert.Contains("img.replaceWith(box)", Js);
    }

    /// <summary>
    /// The 📦 box has exactly one meaning left, and it has to say so: this post has no photograph.
    /// It is no longer allowed to be what a reader sees when something failed silently.
    /// </summary>
    [Fact]
    public void TheEmptyBoxSaysThePostHasNoPhoto()
    {
        Assert.Contains("This listing has no photo", Js);
        Assert.Contains("photo couldn't be loaded", Js);
    }

    // Craigslist and several of the deal CDNs refuse a hotlink that names where it came from.
    [Fact]
    public void TheThumbnailDoesNotLeakTheReferrer()
    {
        Assert.Contains("referrerpolicy=\"no-referrer\"", ThumbnailFunction());
    }

    private static string ThumbnailFunction()
    {
        var start = Js.IndexOf("function arbThumbHtml(", StringComparison.Ordinal);
        Assert.True(start >= 0, "app.js no longer has an arbThumbHtml function — the deal row's thumbnail moved");

        var end = Js.IndexOf("\n  }", start, StringComparison.Ordinal);
        Assert.True(end > start, "could not find the end of arbThumbHtml");
        return Js[start..end];
    }

    private static string ReadAsset(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", "wwwroot", name);
        Assert.True(File.Exists(path), "missing web asset: " + path);
        return File.ReadAllText(path);
    }
}
