using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Coupon lists are advertising, and advertising is written to be clicked rather than read
// literally. Almost every number in one is attached to a condition: "up to 70% off" is a range,
// "$50 off $500" is not $50 off, and "20% off select styles" is 20% off something the seller's item
// probably isn't.
//
// What these pin is mostly REFUSAL, and the stakes are the opposite way round from the deal feeds.
// A missed code costs the seller a discount they never knew about. A fabricated one lowers the cost
// basis under a profit figure that is already on screen with a badge on it — so the board would be
// promising money that cannot be made, on exactly the rows a seller acts on.
public class CouponParserTests
{
    private static readonly CouponFeed Feed = CouponCatalog.Feeds[0];

    private static readonly DateTime Now = new(2026, 7, 27, 12, 0, 0, DateTimeKind.Utc);

    private static CouponMerchant HomeDepot =>
        CouponCatalog.Resolve("Home Depot") ?? throw new InvalidOperationException("home depot missing");

    // The shape these lists actually serve: RSS 2.0, everything of value in the title and the
    // description, and the code shouted in the middle of a sentence.
    private static string FeedXml(params string[] items) => $"""
        <?xml version="1.0"?>
        <rss version="2.0">
          <channel>
        {string.Join("\n", items)}
          </channel>
        </rss>
        """;

    private static string Item(string title, string description, string? pubDate = null) => $"""
            <item>
              <title><![CDATA[{title}]]></title>
              <link>https://slickdeals.net/f/19811001-code</link>
              <description><![CDATA[{description}]]></description>
              <pubDate>{pubDate ?? "Sun, 26 Jul 2026 14:25:10 +0000"}</pubDate>
            </item>
        """;

    private static List<CouponOffer> Parse(string title, string description, string? pubDate = null) =>
        CouponParser.ParseFeed(FeedXml(Item(title, description, pubDate)), Feed, HomeDepot, Now);

    // ── What a real code looks like ───────────────────────────────────────────

    [Fact]
    public void ReadsAPercentageCodeWithItsDeadline()
    {
        var offers = Parse(
            "Home Depot: 20% off your order",
            "Home Depot is taking 20% off with promo code SPRING20 at checkout. Exp 8/15.");

        var offer = Assert.Single(offers);
        Assert.Equal(CouponKinds.PercentOff, offer.Kind);
        Assert.Equal(20m, offer.Value);
        Assert.Equal("SPRING20", offer.Code);
        Assert.Equal(new DateTime(2026, 8, 15), offer.ExpiresUtc!.Value.Date);
    }

    [Fact]
    public void ReadsADollarCodeAndTheOrderItIsGatedBehind()
    {
        var offers = Parse(
            "Home Depot Coupon: $50 off $250+",
            "Save with coupon code TOOLS50 on orders of $250 or more at Home Depot.");

        var offer = Assert.Single(offers);
        Assert.Equal(CouponKinds.AmountOff, offer.Kind);
        Assert.Equal(50m, offer.Value);
        Assert.Equal(250m, offer.MinSpend);
    }

    // A cap and a range are the same three words in different places. "20% off, up to $50" is a real
    // discount with a ceiling; "up to 20% off" is an advertisement.
    [Fact]
    public void ACeilingAfterThePercentageIsACapAndNotARange()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: 20% off appliances, up to $100 off",
            "Use code APPL20 at Home Depot. Exp 8/9."));

        Assert.Equal(20m, offer.Value);
        Assert.Equal(100m, offer.MaxDiscount);
        Assert.Equal(100m, offer.DiscountOn(1_000m));   // the cap, not the $200 the percentage says
    }

    [Fact]
    public void UpToIsARangeAndCarriesNoDiscountAtAll()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: up to 40% off select appliances",
            "Up to 40% off at Home Depot with code SUMMER. Exp 8/30."));

        // Parsed and shown — it may well be a real sale — but worth nothing to a cost basis: the
        // top of that range applies to one item in one department, not to what the seller is buying.
        Assert.Equal(0m, offer.Value);
        Assert.Equal(CouponConfidence.Low, offer.Confidence);
        Assert.False(CouponStacker.BankableDiscount(offer, 500m));
    }

    // ── The code itself ───────────────────────────────────────────────────────

    [Fact]
    public void WordsThatMerelyShoutAreNotCodes()
    {
        // "SAVE" is not a code, and a wrong code is worse than none: it sends the seller to a
        // checkout that rejects it and leaves them thinking the price was a lie.
        Assert.False(CouponParser.LooksLikeCode("SAVE"));
        Assert.False(CouponParser.LooksLikeCode("FREE"));
        Assert.False(CouponParser.LooksLikeCode("SHIPPING"));
        Assert.False(CouponParser.LooksLikeCode("2026"));
        Assert.False(CouponParser.LooksLikeCode("abc"));

        Assert.True(CouponParser.LooksLikeCode("SPRING20"));
        Assert.True(CouponParser.LooksLikeCode("TAKE15OFF"));
        Assert.True(CouponParser.LooksLikeCode("BESTOFPC"));
    }

    [Fact]
    public void ADiscountWithNoCodeIsNeverBankable()
    {
        var offer = Assert.Single(Parse(
            "Home Depot Coupon: 15% off tools sitewide",
            "Home Depot is taking 15% off tools. No code needed. Exp 8/12."));

        Assert.Equal(15m, offer.Value);
        Assert.Equal("", offer.Code);
        // A sale that needs no code is already in the price on the page. Subtracting it again would
        // discount the item twice.
        Assert.False(CouponStacker.BankableDiscount(offer, 400m));
        Assert.Equal(CouponConfidence.Low, offer.Confidence);
    }

    // ── The order, or one item ────────────────────────────────────────────────
    // The most important distinction here, and one taken from the live feeds rather than guessed:
    // 22 of the 25 entries a "Newegg promo code" search returns carry a code, and nearly all of them
    // are bound to the one item the thread is about.

    [Fact]
    public void ACodePostedAgainstOneDealCannotDiscountADifferentItem()
    {
        var newegg = CouponCatalog.Resolve("Newegg")!;
        var offers = CouponParser.ParseFeed(FeedXml(Item(
            "MSI MAG X870E Gaming Max WiFi AM5 ATX Motherboard $189.99 + Free Shipping",
            "Newegg [newegg.com] has *MSI MAG X870E Gaming Max WiFi AM5 ATX Motherboard* on sale for " +
            "$229.99 - $40 off when you apply promo code LUSF2737 at checkout = *$189.99*.")),
            Feed, newegg, Now);

        var offer = Assert.Single(offers);
        Assert.Equal("LUSF2737", offer.Code);
        // Real, usable, and only on that motherboard. Typing it against a graphics card buys nothing.
        Assert.False(offer.AppliesToOrder);
        Assert.False(CouponStacker.BankableDiscount(offer, 300m));
        Assert.Contains("one specific deal", offer.ConfidenceNote);
    }

    [Fact]
    public void ACodeAgainstTheWholeOrderIsTheOneThatCanCutAPrice()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: 15% off your entire order",
            "Home Depot is taking 15% off your entire order with promo code ORDER15. Exp 8/18."));

        Assert.True(offer.AppliesToOrder);
        Assert.True(CouponStacker.BankableDiscount(offer, 300m));
    }

    // ── Refusal ───────────────────────────────────────────────────────────────

    [Fact]
    public void SaysItIsDeadSoItIsDropped()
    {
        Assert.Empty(Parse(
            "Home Depot: 20% off with code SPRING20",
            "This code is expired — no longer valid at Home Depot."));
    }

    [Fact]
    public void ADeadlineAlreadyPassedIsDropped()
    {
        Assert.Empty(Parse(
            "Home Depot: 20% off with code JUNE20",
            "20% off at Home Depot with promo code JUNE20. Expires 6/30/26."));
    }

    [Fact]
    public void AnEntryAboutAnotherStoreIsDropped()
    {
        // A store search returns whatever mentions the store. An offer attributed to the wrong
        // retailer is a code typed into a checkout that has never heard of it.
        Assert.Empty(Parse(
            "Lowe's: 20% off with code LOWES20",
            "Lowe's is taking 20% off with promo code LOWES20. Exp 8/15."));
    }

    [Fact]
    public void MentioningTheStoreIsNotBeingSoldByIt()
    {
        // Live, and it was every result for one store: a search for Lenovo codes returns Amazon
        // listings for "laptop power bank for Lenovo", each with a working Amazon code on it.
        // Attributing those to Lenovo would put codes on a Lenovo row that Lenovo has never issued.
        var lenovo = CouponCatalog.Resolve("Lenovo")!;
        var offers = CouponParser.ParseFeed(FeedXml(Item(
            "TALIX 140W 20000mAh Laptop Power Bank $45",
            "Amazon [amazon.com] has the *TALIX 140W Power Bank* for Lenovo and Dell laptops for *$45* " +
            "with promo code 4YFJQUGG.")),
            Feed, lenovo, Now);

        Assert.Empty(offers);
    }

    [Fact]
    public void AnEntryWithNoCouponWordingAndNoCodeIsNotACoupon()
    {
        // A clearance item is already priced on the board by the deal feeds; it has nothing to
        // contribute to the buy price of anything else.
        Assert.Empty(Parse(
            "Home Depot has the Ryobi 18V Drill for $79",
            "Home Depot has the Ryobi ONE+ 18V drill for $79. Free store pickup."));
    }

    // ── Confidence ────────────────────────────────────────────────────────────

    [Fact]
    public void ConditionsAttachedHoldTheGradeDown()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: 20% off select power tools",
            "Home Depot is taking 20% off select power tools with code TOOL20. Exp 8/20."));

        Assert.Equal(CouponConfidence.Low, offer.Confidence);
        Assert.Contains("selected items", offer.ExclusionsNote);
    }

    [Fact]
    public void AFreshCodeWithADeadlineAndNoConditionsEarnsTheTopGrade()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: $25 off $200",
            "Home Depot coupon code SAVE25NOW. Exp 8/10.",
            "Mon, 27 Jul 2026 08:00:00 +0000"));

        Assert.Equal(CouponConfidence.High, offer.Confidence);
    }

    [Fact]
    public void AnOldUndatedCodeIsALeadRatherThanAPrice()
    {
        var offer = Assert.Single(Parse(
            "Home Depot: 10% off with code SPRING10",
            "Home Depot coupon code SPRING10 for 10% off.",
            "Wed, 01 Apr 2026 08:00:00 +0000"));

        Assert.Equal(CouponConfidence.Low, offer.Confidence);
        Assert.Contains("days ago", offer.ConfidenceNote);
    }

    // ── Cashback ──────────────────────────────────────────────────────────────

    [Fact]
    public void CashbackIsItsOwnOfferAndNamesWhoPaysIt()
    {
        var offers = Parse(
            "Home Depot: 8% cash back via Rakuten",
            "Rakuten is paying 8% cash back at Home Depot this week.");

        var offer = Assert.Single(offers);
        Assert.Equal(CouponKinds.Cashback, offer.Kind);
        Assert.Equal(8m, offer.Value);
        Assert.Contains("Rakuten", offer.MerchantLabel);
        // Nothing to type at the retailer's checkout, which is exactly why it stacks with a code.
        Assert.Equal("", offer.Code);
    }

    [Fact]
    public void ACodeAndACashbackRateInOneEntryAreTwoOffers()
    {
        var offers = Parse(
            "Home Depot: 15% off with code TOOLS15, plus 6% cash back",
            "Home Depot coupon code TOOLS15 for 15% off. TopCashback is also paying 6% cash back. Exp 8/14.");

        Assert.Equal(2, offers.Count);
        Assert.Contains(offers, o => o.Kind == CouponKinds.PercentOff && o.Code == "TOOLS15");
        Assert.Contains(offers, o => o.Kind == CouponKinds.Cashback && o.Value == 6m);
    }

    // ── The deadline ──────────────────────────────────────────────────────────

    [Fact]
    public void AnUndatedDeadlineBelongsToTheYearThatMakesItStillLive()
    {
        // Written in December for January. Reading that as the January just gone would drop a code
        // that is live for another six weeks.
        var (expires, _) = CouponParser.ReadExpiry(
            "20% off with code NY20. Exp 1/15.",
            publishedUtc: new DateTime(2026, 12, 20, 0, 0, 0, DateTimeKind.Utc),
            nowUtc: new DateTime(2026, 12, 21, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2027, 1, 15), expires!.Value.Date);
    }

    [Fact]
    public void TodayOnlyIsADeadlineEvenWithNoDateOnIt()
    {
        var (expires, text) = CouponParser.ReadExpiry("Today only: 20% off w/ code DAY20", Now, Now);

        Assert.Equal(Now.Date, expires!.Value.Date);
        Assert.Contains("today only", text, StringComparison.OrdinalIgnoreCase);
    }

    // ── Which store an entry belongs to ───────────────────────────────────────

    [Fact]
    public void StoreNamesAreMatchedHoweverTheListWroteThem()
    {
        // The same store arrives as four different strings depending on which aggregator published
        // it, and all four have to reach the same code list.
        Assert.Equal("homedepot", CouponCatalog.Resolve("Home Depot")!.Id);
        Assert.Equal("homedepot", CouponCatalog.Resolve("homedepot.com")!.Id);
        Assert.Equal("homedepot", CouponCatalog.Resolve("The Home Depot")!.Id);
        Assert.Equal("bestbuy", CouponCatalog.Resolve("BestBuy")!.Id);
    }

    [Fact]
    public void AStoreNobodyCataloguedIsStillSearchedUnderItsOwnName()
    {
        var merchant = CouponCatalog.Resolve("monoprice.com");

        Assert.NotNull(merchant);
        Assert.False(merchant!.Known);
        Assert.Equal("Monoprice", merchant.Label);
        // What it loses is only the catalogue's own knowledge, never the lookup itself.
        Assert.NotEmpty(CouponCatalog.ManualSitesFor(merchant));
    }

    [Fact]
    public void TheStoresWhereCodesAreTheWrongThingToLookForSaySo()
    {
        var amazon = CouponCatalog.Resolve("Amazon")!;

        // "No codes found at Amazon" reads as "no discount available", and that is false: Amazon's
        // discounts are the clip-the-coupon box on the item page.
        Assert.True(amazon.CodesRare);
        Assert.Contains("clip", amazon.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NoStoreNameIsNoLookup()
    {
        Assert.Null(CouponCatalog.Resolve(""));
        Assert.Null(CouponCatalog.Resolve(null));
        Assert.Null(CouponCatalog.Resolve("   "));
    }

    // ── Malformed input ───────────────────────────────────────────────────────

    [Fact]
    public void AFeedThatChangedShapeFindsNothingRatherThanThrowing()
    {
        Assert.Empty(CouponParser.ParseFeed("<html>not a feed at all", Feed, HomeDepot, Now));
        Assert.Empty(CouponParser.ParseFeed("", Feed, HomeDepot, Now));
        Assert.Empty(CouponParser.ParseFeed(null, Feed, HomeDepot, Now));
    }
}
