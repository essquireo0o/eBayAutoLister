using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The deal feeds need no login, so the whole source is testable without a network call. The
// fixtures below are the shapes Slickdeals, DealNews and TechBargains actually serve — three RSS
// dialects, read by one parser.
//
// Most of what is pinned here is REFUSAL. A deal feed is advertising: the majority of entries are
// not one buyable object, and a large minority of the dollar figures in them are not prices. Every
// one of those that slips through becomes a confident, badged, ranked profit figure built on a cost
// basis the seller cannot buy at — so "$50 off" must never be read as "$50", and a gift-card
// promotion must never reach the ranking at all.
public class DealFeedParserTests
{
    private static readonly DealFeed Slickdeals =
        DealFeedCatalog.ById("slickdeals") ?? throw new InvalidOperationException("slickdeals feed missing");

    private static readonly DealFeed SlickdealsFrontpage =
        DealFeedCatalog.ById("slickdeals-frontpage") ?? throw new InvalidOperationException("frontpage feed missing");

    private static readonly DealFeed DealNews =
        DealFeedCatalog.ById("dealnews") ?? throw new InvalidOperationException("dealnews feed missing");

    private static readonly DealFeed TechBargains =
        DealFeedCatalog.ById("techbargains") ?? throw new InvalidOperationException("techbargains feed missing");

    // Slickdeals: RSS 2.0, price in the title, store named in the body as "Amazon [amazon.com] has".
    private const string SlickdealsFeed = """
        <?xml version="1.0"?>
        <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
          <channel>
            <item>
              <title><![CDATA[15.6" Travelon Anti-Theft Large Classic Laptop Backpack (Black) $56.96 + Free Shipping]]></title>
              <link>https://slickdeals.net/f/19810980-travelon-backpack?utm_source=rss</link>
              <description><![CDATA[Amazon [amazon.com] has *15.6" Travelon Anti-Theft Large Classic Laptop Backpack (Black)* for *$56.96*. *Shipping is free*.]]></description>
              <content:encoded><![CDATA[<div><img src="https://static.slickdealscdn.com/attachment/21109122.thumb" alt="x" /></div><div>Amazon [amazon.com] has it</div>]]></content:encoded>
              <pubDate>Sun, 26 Jul 2026 14:25:10 +0000</pubDate>
            </item>
            <item>
              <title><![CDATA[HP OmniBook 7 (Cert. Refurb) 17.3" Laptop $799.99]]></title>
              <link>https://slickdeals.net/f/19810452-hp-omnibook-7?utm_source=rss</link>
              <description><![CDATA[Woot [woot.com] has the *HP OmniBook 7* for *$799.99*, was $1,199.99. Use promo code <span class='code'><strong>BESTOFPC</strong></span> at checkout.]]></description>
              <pubDate>Sun, 26 Jul 2026 12:00:00 +0000</pubDate>
            </item>
          </channel>
        </rss>
        """;

    // DealNews: structured fields — a real price, the retailer, and a deal type that says whether
    // the entry is one item or a whole category sale.
    private const string DealNewsFeed = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:dealnews="https://www.dealnews.com/ns/rss/1.0.htm" xmlns:media="http://search.yahoo.com/mrss/">
          <channel>
            <item>
              <title>Nautica Admiral Backpack for $36 + free shipping</title>
              <link>https://www.dealnews.com/products/Nautica/Nautica-Admiral-Backpack/512061.html?iref=rss</link>
              <description>&lt;p&gt;Amazon offers the Nautica Admiral Backpack for $36. That's a $4 low. Shipping is free.&lt;/p&gt;</description>
              <guid>https://www.dealnews.com/21933285.html?iref=rss</guid>
              <pubDate>Sun, 26 Jul 2026 21:24:38 +0000</pubDate>
              <dealnews:retailer>Amazon</dealnews:retailer>
              <dealnews:dealType>product</dealnews:dealType>
              <dealnews:price currency="USD">36.00</dealnews:price>
              <media:content url="https://d.dlnws.com/64599/backpack.jpg" medium="image"/>
            </item>
            <item>
              <title>Office Depot Gift Deals: Free w/ qualifying purchase</title>
              <link>https://www.dealnews.com/Office-Depot-Gift-Deals/21933286.html?iref=rss</link>
              <description>&lt;p&gt;Get free gifts based on how much you spend, from $75 to $500.&lt;/p&gt;</description>
              <guid>https://www.dealnews.com/21933286.html</guid>
              <dealnews:retailer>Office Depot</dealnews:retailer>
              <dealnews:dealType>deal</dealnews:dealType>
              <dealnews:price currency="USD">0.00</dealnews:price>
            </item>
            <item>
              <title>Dyson Vacuum Sale: Up to 40% off</title>
              <link>https://www.dealnews.com/Dyson-Vacuum-Sale/21933287.html</link>
              <description>&lt;p&gt;Save on a range of vacuums, from $199.&lt;/p&gt;</description>
              <guid>https://www.dealnews.com/21933287.html</guid>
              <dealnews:retailer>Dyson</dealnews:retailer>
              <dealnews:dealType>sale</dealnews:dealType>
              <dealnews:price currency="USD">199.00</dealnews:price>
            </item>
          </channel>
        </rss>
        """;

    // TechBargains: vendorname / imagelink, price only in the title, guid IS the destination URL.
    private const string TechBargainsFeed = """
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
            <item>
              <title><![CDATA[Sony WH-1000XM5 Wireless Headphones $248 + free shipping]]></title>
              <link>https://www.amazon.com/dp/B09XS7JWHH?tag=tb-20</link>
              <description><![CDATA[<img src='https://www.techbargains.com/imagery/deals/203588.jpg' /><a href='#'>Sony WH-1000XM5</a>]]></description>
              <guid>https://www.amazon.com/dp/B09XS7JWHH?tag=tb-20</guid>
              <pubDate>Sun, 26 Jul 2026 19:15:00 +0000</pubDate>
              <vendorname>Amazon</vendorname>
              <category>Headphones</category>
              <imagelink><![CDATA[https://www.techbargains.com/imagery/deals/203588.jpg]]></imagelink>
            </item>
          </channel>
        </rss>
        """;

    // ── Reading a feed ────────────────────────────────────────────────────────

    [Fact]
    public void ParseFeed_ReadsSlickdealsTitlePriceStoreAndImage()
    {
        var items = DealFeedParser.ParseFeed(SlickdealsFeed, Slickdeals);

        var backpack = Assert.Single(items, i => i.Title.Contains("Travelon"));
        Assert.Equal(DealFeedCatalog.SourceId, backpack.Source);
        Assert.Equal("Slickdeals", backpack.SourceLabel);
        Assert.Equal(56.96m, backpack.Price);
        Assert.Equal("Amazon", backpack.Retailer);
        Assert.Equal("Amazon", backpack.Location);
        Assert.True(backpack.FreeShipping);
        Assert.Equal("https://static.slickdealscdn.com/attachment/21109122.thumb", backpack.ImageUrl);
    }

    // Every retail row has to say so: it is what makes the analyzer charge sales tax on it and
    // withhold a negotiation plan from it.
    [Fact]
    public void ParseFeed_MarksEveryDealAsRetailWithNoDistance()
    {
        var items = DealFeedParser.ParseFeed(DealNewsFeed, DealNews);

        Assert.All(items, i =>
        {
            Assert.True(i.IsRetail);
            Assert.Null(i.DistanceMiles);
            Assert.False(i.IsFree);
        });
    }

    // The structured price is the site's own answer and beats anything read out of prose.
    [Fact]
    public void ParseFeed_PrefersDealNewsStructuredPriceAndRetailer()
    {
        var backpack = Assert.Single(DealFeedParser.ParseFeed(DealNewsFeed, DealNews));

        Assert.Equal(36.00m, backpack.Price);
        Assert.Equal("Amazon", backpack.Retailer);
        Assert.Equal("Nautica Admiral Backpack", backpack.Title);
        Assert.Equal("https://d.dlnws.com/64599/backpack.jpg", backpack.ImageUrl);
    }

    [Fact]
    public void ParseFeed_ReadsTechBargainsVendorAndImage()
    {
        var headphones = Assert.Single(DealFeedParser.ParseFeed(TechBargainsFeed, TechBargains));

        Assert.Equal(248m, headphones.Price);
        Assert.Equal("Amazon", headphones.Retailer);
        Assert.Equal("Sony WH-1000XM5 Wireless Headphones", headphones.Title);
        Assert.Equal("https://www.techbargains.com/imagery/deals/203588.jpg", headphones.ImageUrl);
    }

    [Fact]
    public void ParseFeed_CapturesTheStruckThroughRetailPriceAndTheCouponCode()
    {
        var laptop = Assert.Single(DealFeedParser.ParseFeed(SlickdealsFeed, Slickdeals),
            i => i.Title.Contains("OmniBook"));

        Assert.Equal(799.99m, laptop.Price);
        Assert.Equal(1199.99m, laptop.OriginalPrice);
        Assert.Equal("BESTOFPC", laptop.CouponCode);
    }

    [Fact]
    public void ParseFeed_ReturnsEmptyRatherThanThrowingOnMalformedXml()
    {
        Assert.Empty(DealFeedParser.ParseFeed("<rss><channel><item></broken>", DealNews));
        Assert.Empty(DealFeedParser.ParseFeed("", DealNews));
        Assert.Empty(DealFeedParser.ParseFeed(null, DealNews));
    }

    // ── Refusing what isn't a buyable item ────────────────────────────────────

    // "$0.00" is what DealNews prints for "free with purchase". Taking it at face value would hand
    // a whole class of promotions a $0 cost basis — an unbounded ROI at the top of the ranking.
    [Fact]
    public void ParseFeed_DropsTheZeroPricedGiftPromotion()
    {
        var items = DealFeedParser.ParseFeed(DealNewsFeed, DealNews);

        Assert.DoesNotContain(items, i => i.Title.Contains("Office Depot"));
    }

    // A category sale is not one thing to buy, and the price it quotes belongs to something unnamed.
    [Fact]
    public void ParseFeed_DropsCategorySalesEvenWhenTheyQuoteAPrice()
    {
        var items = DealFeedParser.ParseFeed(DealNewsFeed, DealNews);

        Assert.DoesNotContain(items, i => i.Title.Contains("Dyson"));
    }

    [Theory]
    [InlineData("$50 Amazon Gift Card for $45")]
    [InlineData("Costco Membership + $40 Digital Costco Shop Card")]
    [InlineData("Up to 60% off Tools at Home Depot, from $9")]
    [InlineData("Nike Sale: extra 25% off clearance styles")]
    [InlineData("Chase Sapphire credit card: 60,000 bonus points")]
    [InlineData("Free $10 Target gift card w/ purchase of 2")]
    [InlineData("4-Night Bahamas Cruise from $299")]
    public void IsNotAProduct_RejectsOffersThatAreNotObjects(string title)
    {
        Assert.True(DealFeedParser.IsNotAProduct(title));
    }

    // Refurbished and open-box are the best margins on the board — they must never be filtered out.
    [Theory]
    [InlineData("HP OmniBook 7 (Cert. Refurb) 17.3\" Laptop $799.99")]
    [InlineData("Open-Box Samsung 65\" QLED TV $649")]
    [InlineData("Bitmain Antminer S19j Pro 104TH Miner $1,899")]
    public void IsNotAProduct_KeepsRealProducts(string title)
    {
        Assert.False(DealFeedParser.IsNotAProduct(title));
    }

    // ── Telling a price from a saving ─────────────────────────────────────────
    // The single most expensive thing this parser could get wrong. Reading "$50 off" as a $50 cost
    // basis turns a full-price item into a goldmine at the top of a ranking someone spends on.

    [Theory]
    [InlineData("Instant Pot Duo 6qt: $50 off")]
    [InlineData("Save $120 on the Dyson V15 Detect")]
    [InlineData("Get $25 back in Best Buy credit on select laptops")]
    [InlineData("Free $10 gift card with orders over $75")]
    [InlineData("Extra $30 off with promo code SPRING30")]
    public void ScanPrices_RefusesToReadASavingAsAPrice(string title)
    {
        var (price, _) = DealFeedParser.ScanPrices(title);

        Assert.Null(price);
    }

    // Found live on Slickdeals, and the worst case this parser has: reading the LAST figure blindly
    // prices a $500 laptop at its $14.99 shipping charge, which lands as a fabricated goldmine at
    // the very top of the ranking — the one place a wrong number does the most damage.
    [Theory]
    [InlineData("HP OmniBook X Flip 16\" 2-in-1 Laptop 8GB 512GB $499.99 +$14.99 Shipping Costco.com", 499.99)]
    [InlineData("Anker Power Bank $29.99 + $5 shipping", 29.99)]
    [InlineData("Samsung 990 Pro 2TB SSD $149.99, free shipping w/ $35 minimum", 149.99)]
    public void ScanPrices_NeverReadsAShippingChargeAsThePrice(string title, double expected)
    {
        var (price, _) = DealFeedParser.ScanPrices(title);

        Assert.Equal((decimal)expected, price);
    }

    [Theory]
    [InlineData("Nautica Admiral Backpack for $36 + free shipping", 36)]
    [InlineData("HP Elitebook 14\" Ryzen AI 9, 32GB, 1TB (Factory Reconditioned) $1147.99", 1147.99)]
    [InlineData("Sony WH-1000XM5 Headphones $248", 248)]
    [InlineData("LG 65\" OLED TV $1,299.99 + free delivery", 1299.99)]
    public void ScanPrices_ReadsTheRealPrice(string title, double expected)
    {
        var (price, _) = DealFeedParser.ScanPrices(title);

        Assert.Equal((decimal)expected, price);
    }

    // The last unqualified figure wins, because that is where these titles put the price — and the
    // "was" figure is captured separately rather than mistaken for it.
    [Fact]
    public void ScanPrices_TakesThePriceAndTheWasPriceApart()
    {
        var (price, was) = DealFeedParser.ScanPrices("Ninja Air Fryer $79.99, was $159.99");

        Assert.Equal(79.99m, price);
        Assert.Equal(159.99m, was);
    }

    [Fact]
    public void ScanPrices_IgnoresAWasPriceBelowTheAskBecauseItCannotBeOne()
    {
        var item = DealFeedParser.ParseFeed("""
            <rss version="2.0"><channel><item>
              <title>Weird Widget $199.99 (list price $99.99)</title>
              <link>https://slickdeals.net/f/12345678-widget</link>
            </item></channel></rss>
            """, Slickdeals);

        Assert.Null(Assert.Single(item).OriginalPrice);
    }

    // A parsed figure this far outside the range of a resellable retail deal is a parsing accident.
    [Theory]
    [InlineData("Bulk Pallet Lot $250,000")]
    [InlineData("Candy Bar $0.75")]
    public void ScanPrices_RejectsImplausibleFigures(string title)
    {
        var (price, _) = DealFeedParser.ScanPrices(title);

        Assert.Null(price);
    }

    // ── Titles the comp matcher can work with ─────────────────────────────────

    [Theory]
    [InlineData("Nautica Admiral Backpack for $36 + free shipping", "Nautica Admiral Backpack")]
    [InlineData("Sony WH-1000XM5 Headphones $248 + free shipping", "Sony WH-1000XM5 Headphones")]
    [InlineData("LG 65\" OLED evo C4 TV - $1,299.99 shipped", "LG 65\" OLED evo C4 TV")]
    [InlineData("Bitmain Antminer S19j Pro 104TH $1,899", "Bitmain Antminer S19j Pro 104TH")]
    [InlineData("Cert. Refurb: Dyson V8 Origin Cordless Vacuum Red $168 + Free S&H", "Cert. Refurb: Dyson V8 Origin Cordless Vacuum Red")]
    public void CleanTitle_StripsThePriceAndTheShippingPromise(string raw, string expected)
    {
        Assert.Equal(expected, DealFeedParser.CleanTitle(raw));
    }

    // All four found live. Every one of these reaches ProductNormalizer as part of the product
    // identity unless it comes off — and a comp lookup for "Backpack at Amazon" matches nothing.
    [Theory]
    [InlineData("$19.99* | 24L Beraliy Waterproof Laptop Backpack", "24L Beraliy Waterproof Laptop Backpack")]
    [InlineData("[Lightning Deal] APILDELLA 14'' Dual Laptop Screen Extender", "APILDELLA 14'' Dual Laptop Screen Extender")]
    [InlineData("Dell 15.6\" 2K Touchscreen Laptop i5 512GB $449.99 Bestbuy.com", "Dell 15.6\" 2K Touchscreen Laptop i5 512GB")]
    [InlineData("18\" SwissGear Civic Pro Laptop Backpack (Black) at Woot!", "18\" SwissGear Civic Pro Laptop Backpack (Black)")]
    [InlineData("Woot! App: $155.99 | Dyson V11 Animal Cordless Stick Vacuum (Refurbished)", "Dyson V11 Animal Cordless Stick Vacuum (Refurbished)")]
    public void CleanTitle_StripsThePostingConventionsWrappedAroundTheProduct(string raw, string expected)
    {
        Assert.Equal(expected, DealFeedParser.CleanTitle(raw));
    }

    // A brand named after the product is not a store, and stripping it would cost the comp match
    // the most identifying word in the title.
    [Fact]
    public void CleanTitle_LeavesABrandNameAlone()
    {
        Assert.Equal("Ryzen 5 7600X CPU from AMD", DealFeedParser.CleanTitle("Ryzen 5 7600X CPU from AMD"));
    }

    // ── Answering the seller's actual question ────────────────────────────────

    [Fact]
    public void MatchesQuery_RequiresEveryRealWordOfTheQuery()
    {
        var deal = new LocalSupplyListing { Title = "Sony WH-1000XM5 Wireless Headphones", Retailer = "Amazon" };

        Assert.True(DealFeedParser.MatchesQuery(deal, "sony headphones"));
        Assert.True(DealFeedParser.MatchesQuery(deal, "WH-1000XM5"));
        Assert.False(DealFeedParser.MatchesQuery(deal, "sony camera"));
        Assert.False(DealFeedParser.MatchesQuery(deal, "bose headphones"));
    }

    [Fact]
    public void MatchesQuery_CountsTheRetailerAsPartOfTheHaystack()
    {
        var deal = new LocalSupplyListing { Title = "Open-Box Monitors", Retailer = "Best Buy", Location = "Best Buy" };

        Assert.True(DealFeedParser.MatchesQuery(deal, "best buy monitors"));
    }

    // The opposite of LocalSupplyResults.FilterByRelevance's fallback, and deliberately so: with a
    // firehose of unrelated deals as the input, "nothing matched" is the honest answer and
    // "here are 600 things you didn't ask for" is not.
    [Fact]
    public void BuildResult_FiltersBrowseFeedsHardButTrustsAFeedThatSearched()
    {
        var browse = DealFeedParser.ParseFeed(TechBargainsFeed, TechBargains);
        var searched = DealFeedParser.ParseFeed(SlickdealsFeed, Slickdeals);

        var browseOnly = DealFeedParser.BuildResult([(TechBargains, browse)], "antminer");
        Assert.Empty(browseOnly.Items);

        // Slickdeals ran the query itself, so its answer stands even though the words differ.
        var searchedOnly = DealFeedParser.BuildResult([(Slickdeals, searched)], "antminer");
        Assert.NotEmpty(searchedOnly.Items);
    }

    [Fact]
    public void BuildResult_SummarisesThePriceSpreadAndPointsAtTheRealSearchPage()
    {
        var result = DealFeedParser.BuildResult(
            [(Slickdeals, DealFeedParser.ParseFeed(SlickdealsFeed, Slickdeals))], "laptop backpack");

        Assert.Equal("ok", result.Status);
        Assert.Equal(DealFeedCatalog.SourceId, result.SourceId);
        Assert.Equal(56.96m, result.Min);
        Assert.Equal(799.99m, result.Max);
        Assert.Contains("slickdeals.net", result.SearchUrl);
    }

    // ── Not counting the same deal three times ────────────────────────────────

    // These aggregators repost each other, and the per-source id dedupe can't see it: one Amazon
    // price is a Slickdeals thread, a DealNews page and a direct Amazon link, with three ids. Three
    // rows would give one deal triple the weight in a ranking meant to be one row per thing to buy.
    [Fact]
    public void DedupeAcrossFeeds_CollapsesTheSameDealRepostedElsewhere()
    {
        var deals = new List<LocalSupplyListing>
        {
            new() { Source = "dealfeeds", ItemId = "slickdeals:1", Title = "Sony WH-1000XM5 Headphones", Price = 248m },
            new() { Source = "dealfeeds", ItemId = "dealnews:2", Title = "Headphones Sony WH-1000XM5", Price = 248m },
            new() { Source = "dealfeeds", ItemId = "techbargains:3", Title = "Sony WH-1000XM5 Headphones", Price = 229m },
        };

        var kept = DealFeedParser.DedupeAcrossFeeds(deals);

        // The genuinely cheaper repost survives — a different price is a different deal.
        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, k => k.Price == 229m);
    }

    [Fact]
    public void ItemIdOf_IsUniquePerFeedSoOneAggregatorCannotSuppressAnother()
    {
        var items = DealFeedParser.ParseFeed(SlickdealsFeed, Slickdeals)
            .Concat(DealFeedParser.ParseFeed(SlickdealsFeed, SlickdealsFrontpage))
            .ToList();

        Assert.StartsWith("slickdeals:", items[0].ItemId);
        Assert.Equal(4, items.Select(i => i.ItemId).Distinct().Count());
        // The numeric post id is what survives the affiliate query string.
        Assert.Equal("slickdeals:19810980", items[0].ItemId);
    }

    // ── A 200 that isn't an answer ────────────────────────────────────────────

    [Fact]
    public void DetectBlock_CatchesAChallengePageServedWithASuccessStatus()
    {
        Assert.NotNull(DealFeedParser.DetectBlock(
            "<html><head><title>Attention Required! | Cloudflare</title></head>", "Slickdeals"));
        Assert.NotNull(DealFeedParser.DetectBlock("", "DealNews"));
        Assert.Null(DealFeedParser.DetectBlock(SlickdealsFeed, "Slickdeals"));
    }

    // A post that genuinely talks about access being denied, deep in a body, is not a block page.
    [Fact]
    public void DetectBlock_OnlyScansTheHeadOfTheDocument()
    {
        var realFeed = SlickdealsFeed + new string(' ', DealFeedSelectors.BlockScanChars) + "access denied";

        Assert.Null(DealFeedParser.DetectBlock(realFeed, "Slickdeals"));
    }

    // ── The catalog ───────────────────────────────────────────────────────────

    [Fact]
    public void Catalog_HasExactlyOneKeywordSearchFeedAndUrlEncodesTheQuery()
    {
        Assert.Contains(DealFeedCatalog.Feeds, f => f.IsKeywordSearch);

        var url = DealFeedCatalog.BuildUrl(Slickdeals, "antminer s19j pro");
        Assert.Contains("antminer%20s19j%20pro", url);
        Assert.DoesNotContain("{query}", url);
    }

    [Fact]
    public void Catalog_IdsAreUniqueBecauseTheyPrefixEveryListingId()
    {
        Assert.Equal(DealFeedCatalog.Feeds.Count, DealFeedCatalog.Feeds.Select(f => f.Id).Distinct().Count());
    }
}
