using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Craigslist needs no login, so the whole source is testable without a browser or a network call:
// these cases pin the feed shapes craigslist actually serves, and the title cleanup that decides
// whether the comp lookup downstream sees a product name or a price tag.
public class CraigslistParserTests
{
    // A real craigslist search feed is RDF/RSS 1.0 with dc:date and enc:enclosure.
    private const string RdfFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rdf:RDF xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#"
                 xmlns="http://purl.org/rss/1.0/"
                 xmlns:enc="http://purl.oclc.org/net/rss_2.0/enc#"
                 xmlns:dc="http://purl.org/dc/elements/1.1/">
          <item rdf:about="https://lasvegas.craigslist.org/ele/d/antminer/7712345678.html">
            <title>Bitmain Antminer S19j Pro 104TH - $2,500 (Henderson)</title>
            <link>https://lasvegas.craigslist.org/ele/d/antminer/7712345678.html</link>
            <description>&lt;p&gt;Barely used, comes with PSU&lt;/p&gt;</description>
            <dc:date>2026-07-25T09:12:33-07:00</dc:date>
            <enc:enclosure resource="https://images.craigslist.org/00k0k_abc_300x300.jpg" type="image/jpeg"/>
          </item>
          <item rdf:about="https://lasvegas.craigslist.org/ele/d/antminer-two/7712345679.html">
            <title>Antminer S19 - $1,900</title>
            <link>https://lasvegas.craigslist.org/ele/d/antminer-two/7712345679.html</link>
            <description>no picture</description>
            <dc:date>2026-07-24T11:00:00-07:00</dc:date>
          </item>
        </rdf:RDF>
        """;

    // ── RSS ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseRss_ReadsTitlePriceImageAndDate()
    {
        var items = CraigslistParser.ParseRss(RdfFeed);

        Assert.Equal(2, items.Count);
        var first = items[0];
        Assert.Equal("craigslist", first.Source);
        Assert.Equal("7712345678", first.ItemId);
        Assert.Equal(2500m, first.Price);
        Assert.Equal("https://images.craigslist.org/00k0k_abc_300x300.jpg", first.ImageUrl);
        Assert.Equal(new DateTime(2026, 7, 25, 16, 12, 33, DateTimeKind.Utc), first.PostedUtc);
    }

    // The comp matcher downstream can only work with the words it's given: leaving the price and
    // the neighbourhood on the title makes "$2,500" part of the product being priced.
    [Fact]
    public void ParseRss_StripsThePriceAndNeighbourhoodFromTheTitle()
    {
        var first = CraigslistParser.ParseRss(RdfFeed)[0];

        Assert.Equal("Bitmain Antminer S19j Pro 104TH", first.Title);
        Assert.Equal("Henderson", first.Location);
    }

    [Fact]
    public void ParseRss_HandlesRss2ItemsToo()
    {
        const string rss2 = """
            <rss version="2.0"><channel>
              <item>
                <title>Dyson V11 vacuum - $180 (Summerlin)</title>
                <link>https://lasvegas.craigslist.org/hsh/d/dyson/7799999999.html</link>
                <pubDate>Fri, 24 Jul 2026 18:00:00 +0000</pubDate>
                <enclosure url="https://images.craigslist.org/dyson_300x300.jpg" type="image/jpeg"/>
              </item>
            </channel></rss>
            """;

        var items = CraigslistParser.ParseRss(rss2);

        Assert.Single(items);
        Assert.Equal("Dyson V11 vacuum", items[0].Title);
        Assert.Equal(180m, items[0].Price);
        Assert.Equal("https://images.craigslist.org/dyson_300x300.jpg", items[0].ImageUrl);
    }

    [Fact]
    public void ParseRss_FallsBackToThePriceInTheBodyWhenTheTitleHasNone()
    {
        const string feed = """
            <rss version="2.0"><channel>
              <item>
                <title>Antminer for sale</title>
                <link>https://lasvegas.craigslist.org/ele/d/x/7712345680.html</link>
                <description>Asking $900 firm, cash only</description>
              </item>
            </channel></rss>
            """;

        Assert.Equal(900m, CraigslistParser.ParseRss(feed)[0].Price);
    }

    // No price and not free means no cost basis, and a sourcing row without a cost basis has
    // nothing to say — same rule the Facebook parser applies.
    [Fact]
    public void ParseRss_DropsPostsWithNoPriceAtAll()
    {
        const string feed = """
            <rss version="2.0"><channel>
              <item>
                <title>Antminer wanted</title>
                <link>https://lasvegas.craigslist.org/ele/d/x/7712345681.html</link>
                <description>Looking to buy one</description>
              </item>
            </channel></rss>
            """;

        Assert.Empty(CraigslistParser.ParseRss(feed));
    }

    [Fact]
    public void ParseRss_FreePostsAreKeptAsTheBestCostBasisThereIs()
    {
        const string feed = """
            <rss version="2.0"><channel>
              <item>
                <title>Free treadmill, you haul</title>
                <link>https://lasvegas.craigslist.org/zip/d/x/7712345682.html</link>
              </item>
            </channel></rss>
            """;

        var item = CraigslistParser.ParseRss(feed)[0];
        Assert.True(item.IsFree);
        Assert.Null(item.Price);
    }

    // A feed that changed shape has to degrade into "try the HTML page", never into a 500.
    [Fact]
    public void ParseRss_MalformedXml_ReturnsNothingRatherThanThrowing() =>
        Assert.Empty(CraigslistParser.ParseRss("<rdf:RDF><item><title>broken"));

    [Fact]
    public void ParseRss_EmptyInput_ReturnsNothing() =>
        Assert.Empty(CraigslistParser.ParseRss(""));

    // ── Static HTML fallback ───────────────────────────────────────────────────

    [Fact]
    public void ParseStaticHtml_ReadsTheNoJavascriptSearchPage()
    {
        const string html = """
            <ol class="cl-static-search-results">
              <li class="cl-static-search-result" title="Antminer S19j Pro">
                <a href="https://lasvegas.craigslist.org/ele/d/antminer/7712345678.html">
                  <div class="title">Antminer S19j Pro 104TH</div>
                  <div class="details">
                    <div class="price">$2,500</div>
                    <div class="location">henderson</div>
                  </div>
                </a>
              </li>
              <li class="cl-static-search-result" title="Antminer S9">
                <a href="https://lasvegas.craigslist.org/ele/d/s9/7712345683.html">
                  <div class="title">Antminer S9 &amp; PSU</div>
                  <div class="details"><div class="price">$120</div><div class="location">las vegas</div></div>
                </a>
              </li>
            </ol>
            """;

        var items = CraigslistParser.ParseStaticHtml(html);

        Assert.Equal(2, items.Count);
        Assert.Equal("Antminer S19j Pro 104TH", items[0].Title);
        Assert.Equal(2500m, items[0].Price);
        Assert.Equal("henderson", items[0].Location);
        // HTML entities in a title would otherwise reach the comp matcher as "&amp;".
        Assert.Equal("Antminer S9 & PSU", items[1].Title);
    }

    [Fact]
    public void ParseStaticHtml_NoResultBlocks_ReturnsNothing() =>
        Assert.Empty(CraigslistParser.ParseStaticHtml("<html><body>nothing here</body></html>"));

    // Copied from a live craigslist search response: the current permalink shape, the whitespace
    // craigslist actually emits, and a post with no location div at all (which is common).
    [Fact]
    public void ParseStaticHtml_HandlesTheMarkupCraigslistActuallyServes()
    {
        const string html = """
            <ol class="cl-static-search-results">
                <li class="cl-static-search-result" title="iPhone 15 pro max 256 GB">
                        <a href="https://www.craigslist.org/view/d/las-vegas-iphone-15-pro-max-256-gb/dgvCZzozi9U4HqEj5VQpif">
                            <div class="title">iPhone 15 pro max 256 GB</div>

                            <div class="details">
                                <div class="price">$600</div>
                            </div>
                        </a>
                    </li>
                <li class="cl-static-search-result" title="Pre owned iPhone XS MAX 512GB">
                        <a href="https://www.craigslist.org/view/d/las-vegas-pre-owned/bjhRdk1HetRa4pB2pqNhGz">
                            <div class="title">Pre owned iPhone XS MAX 512GB</div>

                            <div class="details">
                                <div class="price">$240</div>
                                <div class="location">
                                    Las Vegas
                                </div>
                            </div>
                        </a>
                    </li>
            </ol>
            """;

        var items = CraigslistParser.ParseStaticHtml(html);

        Assert.Equal(2, items.Count);
        Assert.Equal(600m, items[0].Price);
        Assert.Equal("", items[0].Location);              // no location div is normal, not an error
        Assert.Equal("Las Vegas", items[1].Location);     // and the emitted whitespace is trimmed
        // Post ids have to stay unique per source or the dedupe collapses real listings.
        Assert.Equal("dgvCZzozi9U4HqEj5VQpif", items[0].ItemId);
        Assert.Equal("bjhRdk1HetRa4pB2pqNhGz", items[1].ItemId);
    }

    // ── Search URL ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildSearchUrl_SendsPostalAndDistanceSoCraigslistFiltersServerSide()
    {
        var url = CraigslistParser.BuildSearchUrl("lasvegas", "antminer s19 & psu", "89101", 25);

        Assert.StartsWith("https://lasvegas.craigslist.org/search/sss?", url);
        Assert.Contains("query=antminer%20s19%20%26%20psu", url);
        Assert.Contains("postal=89101", url);
        Assert.Contains("search_distance=25", url);
        Assert.Contains("sort=date", url);
        Assert.EndsWith("&format=rss", url);
    }

    // Craigslist ignores a radius with no postal to measure it from, so sending one alone would
    // quietly search the whole metro while the UI claims a 25-mile search.
    [Fact]
    public void BuildSearchUrl_NoUsableZip_OmitsBothPostalAndDistance()
    {
        var url = CraigslistParser.BuildSearchUrl("lasvegas", "antminer", "", 25);

        Assert.DoesNotContain("postal=", url);
        Assert.DoesNotContain("search_distance=", url);
    }

    [Fact]
    public void BuildSearchUrl_PagesWithOffsetAndCanDropTheRssFlag()
    {
        var url = CraigslistParser.BuildSearchUrl("sfbay", "ps5", "94103", 10, offset: 25, rss: false);

        Assert.Contains("&s=25", url);
        Assert.DoesNotContain("format=rss", url);
    }

    [Theory]
    [InlineData(0, 1)]     // clamped up
    [InlineData(9000, 500)] // clamped down
    public void BuildSearchUrl_ClampsTheRadiusToWhatCraigslistAccepts(int requested, int expected) =>
        Assert.Contains($"search_distance={expected}", CraigslistParser.BuildSearchUrl("lasvegas", "x", "89101", requested));

    // ── Result assembly ────────────────────────────────────────────────────────

    [Fact]
    public void BuildResult_DropsDuplicatesAcrossPagesAndSummarisesTheAskSpread()
    {
        var site = CraigslistSites.ById("lasvegas")!;
        var listings = CraigslistParser.ParseRss(RdfFeed);
        // Page 2 of a feed repeats posts that shifted while page 1 was being read.
        listings.AddRange(CraigslistParser.ParseRss(RdfFeed));

        var result = CraigslistParser.BuildResult(listings, site, "antminer", "89101", 25);

        Assert.Equal(2, result.Count);
        Assert.Equal(1900m, result.Min);
        Assert.Equal(2500m, result.Max);
        Assert.Equal(2200m, result.Median);
        Assert.Equal("craigslist", result.SourceId);
        Assert.Equal("ok", result.Status);
        // The seller has to be able to see which board was actually searched.
        Assert.Contains("Las Vegas", result.ScopeLabel);
    }

    [Fact]
    public void BuildResult_FiltersOutTheUnrelatedPaddingCraigslistAddsToThinSearches()
    {
        var site = CraigslistSites.ById("lasvegas")!;
        var listings = CraigslistParser.ParseRss(RdfFeed);
        listings.Add(new LocalSupplyListing { Source = "craigslist", ItemId = "9", Title = "Patio furniture set", Price = 40m });

        var result = CraigslistParser.BuildResult(listings, site, "antminer", "89101", 25);

        Assert.Equal(2, result.Count);
        Assert.Equal(1900m, result.Min); // the $40 padding would otherwise be the local floor
    }

    // ── Title cleanup ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Weber grill - $150 (North Las Vegas)", "Weber grill", "North Las Vegas")]
    [InlineData("Weber grill (North Las Vegas)", "Weber grill", "North Las Vegas")]
    [InlineData("Weber grill - $150", "Weber grill", "")]
    [InlineData("Weber grill", "Weber grill", "")]
    public void CleanTitle_SeparatesTheProductFromThePriceAndPlace(string raw, string title, string place)
    {
        var (cleanTitle, cleanPlace) = CraigslistParser.CleanTitle(raw);
        Assert.Equal(title, cleanTitle);
        Assert.Equal(place, cleanPlace);
    }

    // A model number in parentheses is part of the product, not a neighbourhood — but craigslist
    // only ever appends the place last, so only a trailing group is treated as one.
    [Fact]
    public void CleanTitle_KeepsParenthesesThatArentTheTrailingPlace()
    {
        var (title, place) = CraigslistParser.CleanTitle("Antminer (S19j Pro) 104TH - $2,500 (Henderson)");

        Assert.Equal("Antminer (S19j Pro) 104TH", title);
        Assert.Equal("Henderson", place);
    }

    [Theory]
    [InlineData("$1,250", 1250)]
    [InlineData("$1250.50", 1250.50)]
    [InlineData("Asking $75 obo", 75)]
    public void ExtractPrice_ReadsTheFirstDollarAmount(string text, decimal expected) =>
        Assert.Equal(expected, CraigslistParser.ExtractPrice(text).Price);

    [Fact]
    public void ExtractPrice_NoDollarAmount_IsNullNotZero() =>
        Assert.Null(CraigslistParser.ExtractPrice("make me an offer").Price);

    // Craigslist prints $0 for every post whose seller left the price blank. Read literally, that
    // is a free item with unbounded ROI — a whole class of fake goldmines at the top of the table.
    [Fact]
    public void ExtractPrice_ZeroMeansNoPriceStated() =>
        Assert.Null(CraigslistParser.ExtractPrice("$0").Price);

    [Fact]
    public void ParseStaticHtml_ZeroPricedPostIsDroppedUnlessItSaysItsFree()
    {
        const string html = """
            <li class="cl-static-search-result" title="iPhone 13 cracked">
              <a href="https://www.craigslist.org/view/d/x/aaaaaaaaaaaa1"><div class="title">iPhone 13 cracked screen</div>
              <div class="details"><div class="price">$0</div></div></a></li>
            <li class="cl-static-search-result" title="Free treadmill">
              <a href="https://www.craigslist.org/view/d/y/aaaaaaaaaaaa2"><div class="title">Free treadmill, you haul</div>
              <div class="details"><div class="price">$0</div></div></a></li>
            """;

        var items = CraigslistParser.ParseStaticHtml(html);

        var kept = Assert.Single(items);
        Assert.True(kept.IsFree);
        Assert.Equal("Free treadmill, you haul", kept.Title);
    }

    // Craigslist has both permalink shapes live at once, and an unrecognised third one must cost
    // a tidy id rather than a dropped listing.
    [Fact]
    public void PostIdOf_ReadsBothPermalinkShapesAndFallsBackToTheUrl()
    {
        Assert.Equal("7712345678", CraigslistParser.PostIdOf("https://lasvegas.craigslist.org/ele/d/x/7712345678.html"));
        Assert.Equal("dgvCZzozi9U4HqEj5VQpif",
            CraigslistParser.PostIdOf("https://www.craigslist.org/view/d/las-vegas-iphone/dgvCZzozi9U4HqEj5VQpif"));
        Assert.Equal("https://lasvegas.craigslist.org/ele/d/x", CraigslistParser.PostIdOf("https://lasvegas.craigslist.org/ele/d/x"));
    }

    [Fact]
    public void RelativeAge_ReadsAsSomethingASellerWouldSay()
    {
        var now = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("30 min ago", CraigslistParser.RelativeAge(now.AddMinutes(-30), now));
        Assert.Equal("1 hour ago", CraigslistParser.RelativeAge(now.AddHours(-1), now));
        Assert.Equal("3 days ago", CraigslistParser.RelativeAge(now.AddDays(-3), now));
        // Craigslist timestamps carry a local offset; a clock skew must not produce "-1 min ago".
        Assert.Equal("just posted", CraigslistParser.RelativeAge(now.AddMinutes(5), now));
    }
}
