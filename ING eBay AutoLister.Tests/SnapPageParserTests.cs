using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The fast path into Snap &amp; Source. Everything here is a pure parse, so the whole surface is
/// testable — which matters more here than in most parsers, because the failure mode is not an
/// error. A title with "- craigslist" on the end silently searches eBay for the wrong thing, and a
/// price scraped off the wrong element produces a confident BUY on a deal that does not exist.
/// </summary>
public class SnapPageParserTests
{
    // ── Is it a link or is it an item name? ──────────────────────────────────

    [Theory]
    [InlineData("https://sfbay.craigslist.org/sby/tls/d/drill/7712345678.html")]
    [InlineData("http://example.com/item/1")]
    [InlineData("https://www.facebook.com/marketplace/item/1234567890/")]
    public void AbsoluteHttpAddressesAreLinks(string text) =>
        Assert.True(SnapPageParser.LooksLikeUrl(text));

    [Theory]
    [InlineData("DeWalt DCD771 20V drill")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("craigslist.org")]              // no scheme — a search term as far as this is concerned
    [InlineData("ftp://example.com/thing")]
    [InlineData("https://example.com/a b")]     // a space means somebody typed a sentence
    public void EverythingElseIsAnItemName(string text) =>
        Assert.False(SnapPageParser.LooksLikeUrl(text));

    // ── Where the price came from ────────────────────────────────────────────

    [Theory]
    [InlineData("https://sfbay.craigslist.org/sby/tls/d/x/771.html", "Craigslist")]
    [InlineData("https://www.facebook.com/marketplace/item/123/", "Facebook Marketplace")]
    [InlineData("https://offerup.com/item/detail/abc", "OfferUp")]
    [InlineData("https://www.ebay.com/itm/123456789", "eBay")]
    [InlineData("https://www.mercari.com/us/item/m123/", "Mercari")]
    public void KnownSitesAreNamed(string url, string expected) =>
        Assert.Equal(expected, SnapPageParser.SiteLabel(url));

    [Fact]
    public void AnUnknownSiteFallsBackToItsHostWithoutTheWww()
    {
        Assert.Equal("someauction.co.uk", SnapPageParser.SiteLabel("https://www.SomeAuction.co.uk/lot/4"));
    }

    [Fact]
    public void SomethingThatIsNotAUrlHasNoSiteLabel()
    {
        Assert.Equal("", SnapPageParser.SiteLabel("DeWalt drill"));
        Assert.Equal("", SnapPageParser.SiteLabel(null));
    }

    // ── Title ────────────────────────────────────────────────────────────────

    [Fact]
    public void OpenGraphTitleWinsOverTheDocumentTitle()
    {
        // <title> carries the site name and the city; og:title is the item. Preferring the wrong
        // one feeds "for sale in San Jose" to the comp lookup.
        const string html = """
            <html><head>
              <title>DeWalt DCD771 drill - tools - by owner - sale - craigslist</title>
              <meta property="og:title" content="DeWalt DCD771 20V Cordless Drill" />
            </head><body></body></html>
            """;

        Assert.Equal("DeWalt DCD771 20V Cordless Drill", SnapPageParser.Parse(html).Title);
    }

    [Theory]
    [InlineData("Sony WH-1000XM5 Headphones - craigslist", "Sony WH-1000XM5 Headphones")]
    [InlineData("Sony WH-1000XM5 Headphones | eBay", "Sony WH-1000XM5 Headphones")]
    [InlineData("Marketplace - Sony WH-1000XM5 Headphones", "Sony WH-1000XM5 Headphones")]
    [InlineData("$120 - Sony WH-1000XM5 Headphones", "Sony WH-1000XM5 Headphones")]
    [InlineData("Sony WH-1000XM5 Headphones (Reno)", "Sony WH-1000XM5 Headphones")]
    public void SiteFurnitureComesOffTheName(string ogTitle, string expected)
    {
        var html = $"<html><head><meta property=\"og:title\" content=\"{ogTitle}\"></head></html>";
        Assert.Equal(expected, SnapPageParser.Parse(html).Title);
    }

    [Fact]
    public void AttributesInEitherOrderAreRead()
    {
        // Sites emit content-first and property-first in roughly equal measure, and the one that
        // was not handled would look exactly like "that page didn't say what it is".
        const string html = """<meta content="Milwaukee M18 Impact Driver" property="og:title">""";
        Assert.Equal("Milwaukee M18 Impact Driver", SnapPageParser.Parse(html).Title);
    }

    [Fact]
    public void HtmlEntitiesAreDecodedAndWhitespaceCollapsed()
    {
        const string html = """<meta property="og:title" content="Weber   Q&amp;A   Grill &#39;22">""";
        Assert.Equal("Weber Q&A Grill '22", SnapPageParser.Parse(html).Title);
    }

    [Fact]
    public void ATitleThatIsJustTheSiteNameIsNoTitleAtAll()
    {
        // A blocked or interstitial page routinely titles itself. Passing "Craigslist" to the comp
        // lookup would price whatever eBay thinks the word is worth.
        const string html = """<meta property="og:title" content="Craigslist">""";
        var facts = SnapPageParser.Parse(html, "https://sfbay.craigslist.org/x.html");
        Assert.Equal("", facts.Title);
        Assert.False(facts.HasTitle);
    }

    // ── Challenge pages ──────────────────────────────────────────────────────
    // Found by pointing the finished feature at a live Walmart product page: the bot check answered
    // HTTP 200 with a full set of Open Graph tags, and the screen priced an item called "Robot or
    // human?" — a confident "BUY UNDER $464" against comps for whatever eBay thinks those words are
    // worth. Nothing failed, so no status check could have caught it.

    [Theory]
    [InlineData("Robot or human?")]
    [InlineData("Are you a human?")]
    [InlineData("Just a moment...")]
    [InlineData("Attention Required! | Cloudflare")]
    [InlineData("Pardon Our Interruption")]
    [InlineData("Access Denied")]
    [InlineData("Blocked")]
    [InlineData("Page Not Found")]
    [InlineData("captcha")]
    public void ABotCheckIsNotAnItem(string title) => Assert.True(SnapPageParser.IsChallengeTitle(title));

    [Theory]
    [InlineData("Blocked Drain Auger 25ft Snake")]
    [InlineData("Ring Security Camera Outdoor 2-Pack")]
    [InlineData("Vintage 'Page Not Found' 404 Poster Print")]
    [InlineData("DeWalt DCD771 20V Cordless Drill")]
    [InlineData("")]
    public void RealItemsThatHappenToUseThoseWordsSurvive(string title) =>
        Assert.False(SnapPageParser.IsChallengeTitle(title));

    [Fact]
    public void AChallengePageParsesToNoTitleSoNothingCanBePricedOffIt()
    {
        // Refusing to name it is what routes this into "that page didn't say what it is", which
        // tells the seller to photograph the thing instead — the one route no CDN can block.
        const string html = """
            <meta property="og:title" content="Robot or human?">
            <meta property="og:image" content="https://i5.walmartimages.com/x.png">
            """;

        var facts = SnapPageParser.Parse(html, "https://www.walmart.com/ip/13977397");
        Assert.False(facts.HasTitle);
    }

    // ── The site's own name, for sites nobody enumerated ─────────────────────

    [Theory]
    [InlineData("https://en.wikipedia.org/wiki/DeWalt", "DeWalt - Wikipedia", "DeWalt")]
    [InlineData("https://www.newegg.com/p/N82E1", "Corsair RM750e PSU | Newegg", "Corsair RM750e PSU")]
    [InlineData("https://www.someshop.co.uk/x", "Bosch GSB 18V – SomeShop", "Bosch GSB 18V")]
    public void TheSitesOwnNameComesOffEvenWhenItIsNotOnTheKnownList(string url, string ogTitle, string expected)
    {
        var html = $"<meta property=\"og:title\" content=\"{ogTitle}\">";
        Assert.Equal(expected, SnapPageParser.Parse(html, url).Title);
    }

    [Fact]
    public void ABrandInTheMiddleOfANameIsNotStripped()
    {
        // Only a trailing, separated occurrence is site furniture. "eBay Motors Toolbox" is an item.
        const string html = """<meta property="og:title" content="eBay Motors Branded Toolbox Steel">""";
        Assert.Equal("eBay Motors Branded Toolbox Steel",
            SnapPageParser.Parse(html, "https://www.ebay.com/itm/1").Title);
    }

    [Fact]
    public void APageWithNothingInItParsesToNothing()
    {
        var facts = SnapPageParser.Parse("<html><body>hello</body></html>");
        Assert.False(facts.HasTitle);
        Assert.Null(facts.Price);
        Assert.Equal("", facts.ImageUrl);
    }

    [Fact]
    public void NullAndEmptyHtmlAreSafe()
    {
        Assert.False(SnapPageParser.Parse(null).HasTitle);
        Assert.False(SnapPageParser.Parse("").HasTitle);
    }

    // ── Price ────────────────────────────────────────────────────────────────

    [Fact]
    public void ADeclaredProductPriceIsRead()
    {
        const string html = """
            <meta property="og:title" content="Antminer S19j Pro">
            <meta property="product:price:amount" content="1250.00">
            """;
        Assert.Equal(1250.00m, SnapPageParser.Parse(html).Price);
    }

    [Fact]
    public void AJsonLdOfferPriceIsRead()
    {
        const string html = """
            <meta property="og:title" content="Nikon D750 Body">
            <script type="application/ld+json">
            {"@type":"Product","name":"Nikon D750 Body","offers":{"@type":"Offer","price":"640.00","priceCurrency":"USD"}}
            </script>
            """;
        Assert.Equal(640.00m, SnapPageParser.Parse(html).Price);
    }

    [Fact]
    public void CraigslistsOwnPriceElementIsTheOneMarkupException()
    {
        // Craigslist publishes no price metadata at all, and it is the single most likely site for
        // this feature to be pointed at. Without this it would ask for a price it could have read.
        const string html = """
            <meta property="og:title" content="Snap-on ratchet set">
            <div class="postingtitle"><span class="price">$185</span></div>
            """;
        Assert.Equal(185m, SnapPageParser.Parse(html).Price);
    }

    [Theory]
    [InlineData("1,250.00", 1250.00)]
    [InlineData("$1,250", 1250)]
    [InlineData("USD 640", 640)]
    [InlineData("640.00 USD", 640)]
    [InlineData("89.99", 89.99)]
    public void PricesParseOutOfWhateverShapeTheFieldArrivedIn(string text, double expected)
    {
        Assert.True(SnapPageParser.TryParsePrice(text, out var price));
        Assert.Equal((decimal)expected, price);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("call for price")]
    [InlineData("0")]
    [InlineData("0.00")]
    [InlineData("9999999")]   // past believable for a listing — a page id, not a price
    public void NonPricesAreRefusedRatherThanGuessed(string? text) =>
        Assert.False(SnapPageParser.TryParsePrice(text, out _));

    [Fact]
    public void FreeIsAPriceOfZeroAndSaysSo()
    {
        // Not the same as "no price found": free is the best possible answer to "what do they want
        // for it", and the verdict downstream reads it as a real cost basis.
        const string html = """
            <meta property="og:title" content="Free dresser">
            <meta property="product:price:amount" content="Free">
            """;
        var facts = SnapPageParser.Parse(html);
        Assert.Equal(0m, facts.Price);
        Assert.True(facts.IsFree);
    }

    [Fact]
    public void ADeclaredNumberBeatsTheWordFreeInTheTitle()
    {
        // "Free shipping" in a title must never zero out a stated price. The declared field is
        // checked first for exactly this reason.
        const string html = """
            <meta property="og:title" content="Dell XPS 13 — free shipping">
            <meta property="product:price:amount" content="410.00">
            """;
        var facts = SnapPageParser.Parse(html);
        Assert.Equal(410m, facts.Price);
        Assert.False(facts.IsFree);
    }

    [Fact]
    public void APageWithNoPriceFieldReportsNoPriceRatherThanZero()
    {
        // Zero would read downstream as "they are giving it away". Null is what makes the UI ask.
        const string html = """<meta property="og:title" content="Kitchen table, solid oak">""";
        var facts = SnapPageParser.Parse(html);
        Assert.Null(facts.Price);
        Assert.False(facts.IsFree);
    }

    // ── Image ────────────────────────────────────────────────────────────────

    [Fact]
    public void TheOpenGraphImageIsTaken()
    {
        const string html = """
            <meta property="og:title" content="Bike">
            <meta property="og:image" content="https://images.example.com/bike.jpg">
            """;
        Assert.Equal("https://images.example.com/bike.jpg", SnapPageParser.Parse(html).ImageUrl);
    }

    [Fact]
    public void ARelativeOrDataImageIsDroppedRatherThanRenderedBroken()
    {
        const string html = """
            <meta property="og:title" content="Bike">
            <meta property="og:image" content="/static/bike.jpg">
            """;
        Assert.Equal("", SnapPageParser.Parse(html).ImageUrl);
    }

    // ── Bounds ───────────────────────────────────────────────────────────────

    [Fact]
    public void AnEnormousPageIsScannedOnlyAtItsHead()
    {
        // Facebook's document is megabytes of script; the metadata is in the first few kilobytes.
        // The bound is what keeps a paste of a heavy page from stalling the one screen that has to
        // answer fast.
        var html = "<meta property=\"og:title\" content=\"Trek Domane\">"
                   + new string('x', SnapPageParser.MaxScanChars * 2);

        Assert.Equal("Trek Domane", SnapPageParser.Parse(html).Title);
    }
}
