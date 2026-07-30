using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Services/ResaleValuation.cs is the one place in the app that is allowed to say "there is no
// resale price for this row", and the rule it exists to enforce is a money rule, not a tidiness
// one: the sold-comps database will answer "what does a 2011 Tundra sell for" with the median of
// four tow hitches, that answer costs nothing to produce, looks exactly like a real one, and would
// put a $7,900 profit on a board a seller spends real money from.
//
// CategoryArbitrageTests pins the refusals a Cars row hits. These pin the rest of the file — the
// contract of a refused OUTCOME (no price anywhere on it, and a search handed back instead), the
// bulky-goods provider and its deliberately looser bounds, the ask floor, the exact edges of the
// stated multiples, and the registry that decides which of the three answers a category gets.
public class ResaleValuationTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static readonly ResaleCategory Cars = ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId);
    private static readonly ResaleCategory Furniture = ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.FurnitureId);
    private static readonly ResaleCategory Anything = ResaleCategoryCatalog.Anything;

    private static GuardedCompsValuationProvider Provider(string id) =>
        (GuardedCompsValuationProvider)ResaleValuationRegistry.BuildDefaults().First(p => p.Id == id);

    private static GuardedCompsValuationProvider Motors() => Provider(ResaleValuationProviders.EbayMotors);
    private static GuardedCompsValuationProvider Bulky() => Provider(ResaleValuationProviders.BulkyLocal);

    private static IResaleValuationProvider Parcel() =>
        ResaleValuationRegistry.BuildDefaults().First(p => p.Id == ResaleValuationProviders.EbayComps);

    private static LocalSupplyListing Listing(string title, decimal? price, string id = "1") =>
        new()
        {
            Source = "craigslist", SourceLabel = "Craigslist", ItemId = id, Title = title,
            Url = $"https://lasvegas.craigslist.org/d/{id}.html", Price = price, Location = "Henderson",
        };

    /// <summary>A truck ad, already classified — the shape every Motors assertion below starts from.</summary>
    private static LocalSupplyListing Truck(decimal? price = 8_500m)
    {
        var listing = Listing("MUST SELL 2011 Toyota Tundra SR5 4x4 crew cab 137k miles", price);
        ResaleCategoryCatalog.Classify(listing);
        return listing;
    }

    private static ResalePricing Pricing(
        decimal? expected, int comps = 6, bool identityVerified = true,
        string lookupTitle = "2011 Toyota Tundra", decimal? median = null) =>
        new()
        {
            LookupTitle = lookupTitle,
            Median = median ?? expected, ExpectedSale = expected, QuickSale = expected * 0.9m,
            SoldCompCount = comps, PricedCompCount = comps, IdentityVerified = identityVerified,
            ConfidenceScore = 70, ConfidenceLevel = "Good",
        };

    // ── The registry: which of the three answers a category gets ─────────────────────────────

    [Fact]
    public void Registry_EveryCategoryInTheCatalogIsOwnedByAProvider()
    {
        // A category whose ValuationProviderId matches nothing prices off the parcel provider by
        // fallback — which is silently the WRONG answer for a titled thing, and the failure mode is
        // a confident four-figure number rather than an exception. So the pairing is checked here
        // rather than discovered on a board.
        Assert.All(ResaleCategoryCatalog.All, category =>
            Assert.Contains(ResaleValuationRegistry.Default.All, p => p.Handles(category)));
    }

    [Fact]
    public void Registry_TheDefaultIsTheSameSetOfRulesTheContainerRegisters()
    {
        // Program.cs registers BuildDefaults() into DI; every analyzer constructed by hand uses
        // Default. Two different sets of rules would mean the app refuses a row the tests price.
        Assert.Equal(
            ResaleValuationRegistry.BuildDefaults().Select(p => p.Id),
            ResaleValuationRegistry.Default.All.Select(p => p.Id));
    }

    [Fact]
    public void Registry_AnUnknownProviderIdFallsBackToTheParcelAnswer_RatherThanFailing()
    {
        // The half-finished-integration case: a category pointed at "kbb_someday" before the book
        // value service exists. The board keeps working on the answer this app does have.
        var category = new ResaleCategory
        {
            Id = "book_value", Label = "Book value", Group = "Vehicles",
            ValuationProviderId = "kbb_someday",
        };

        var outcome = ResaleValuationRegistry.Default.Value(category, Truck(), Pricing(9_000m));

        Assert.Equal(ValuationStatuses.Comps, outcome.Valuation.Status);
        Assert.Equal(ResaleValuationProviders.EbayComps, outcome.Valuation.ProviderId);
    }

    [Fact]
    public void Registry_WithNoProvidersAtAllRefuses_AndStillHandsBackTheSearch()
    {
        // Only a misconfigured container reaches this. Refusing is the safe answer: the row keeps
        // its listing and its ask and says it wasn't valued, which beats pricing it off nothing.
        var outcome = new ResaleValuationRegistry([]).Value(Cars, Truck(), Pricing(9_000m));

        Assert.Equal(ValuationStatuses.Manual, outcome.Valuation.Status);
        Assert.Null(outcome.Pricing);
        Assert.Contains("No valuation source is configured", outcome.Valuation.Note);
        Assert.Contains("LH_Sold=1", outcome.Valuation.LookupUrl);
    }

    [Fact]
    public void Registry_EveryCategoryEitherPricesTheRowOrHandsBackASearch()
    {
        // The invariant a new category has to keep: an unpriceable row is never a shrug. It says
        // what went wrong in a sentence and carries the sold-listings search that answers it.
        foreach (var category in ResaleCategoryCatalog.All)
        {
            var outcome = ResaleValuationRegistry.Default.Value(category, Truck(), comps: null);

            Assert.Equal(ValuationStatuses.Manual, outcome.Valuation.Status);
            Assert.False(outcome.Valuation.HasPrice);
            Assert.NotEqual("", outcome.Valuation.Note);
            Assert.NotEqual("", outcome.Valuation.LookupQuery);
            Assert.True(Uri.IsWellFormedUriString(outcome.Valuation.LookupUrl, UriKind.Absolute),
                $"{category.Id} handed back an unusable search link");
        }
    }

    // ── The parcel provider: the original behaviour, unchanged ───────────────────────────────

    [Fact]
    public void Parcel_PricedCompsAreTakenAtTheirWord()
    {
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);

        var outcome = Parcel().Value(Anything, listing, Pricing(200m, lookupTitle: "Bitmain Antminer S19j Pro"));

        Assert.Equal(ValuationStatuses.Comps, outcome.Valuation.Status);
        Assert.Equal("eBay sold comps", outcome.Valuation.SourceLabel);
        // The lookup title, not the seller's ad copy — the row shows what was actually searched.
        Assert.Equal("Bitmain Antminer S19j Pro", outcome.Valuation.LookupQuery);
        Assert.Contains("Bitmain Antminer S19j Pro", outcome.Valuation.Note);
        Assert.NotNull(outcome.Pricing);
    }

    [Fact]
    public void Parcel_ALookupThatFoundNothingIsStillPassedThrough()
    {
        // "We looked and found nothing" is a different row from "we never looked": the lookup
        // carries the comp counts and the identity flag the row grades its absent evidence from.
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);
        var comps = Pricing(0m, comps: 0);

        var outcome = Parcel().Value(Anything, listing, comps);

        Assert.Equal(ValuationStatuses.Manual, outcome.Valuation.Status);
        Assert.Equal("No eBay sold history matched this title.", outcome.Valuation.Note);
        Assert.Same(comps, outcome.Pricing);
        Assert.Equal(listing.Title, outcome.Valuation.LookupQuery);
    }

    [Fact]
    public void Parcel_NeverHavingLookedCarriesNoLookupAtAll()
    {
        var outcome = Parcel().Value(Anything, Listing("Bitmain Antminer S19j Pro 104TH", 50m), comps: null);

        Assert.Equal(ValuationStatuses.Manual, outcome.Valuation.Status);
        Assert.Null(outcome.Pricing);
    }

    [Fact]
    public void Parcel_DoesNotSecondGuessTheCompsAgainstTheAsk()
    {
        // Deliberate, and the reason the guard is a separate provider rather than a global rule: on
        // the parcel board a $12 comp against a $180 ask is an ordinary underpriced flip — that IS
        // the product. The same shape on a titled thing is a parts match, and Motors refuses it.
        var outcome = Parcel().Value(Anything, Listing("DeWalt 20V impact wrench", 180m), Pricing(12m));

        Assert.Equal(ValuationStatuses.Comps, outcome.Valuation.Status);
    }

    // ── The refusal contract: no number leaves the building ──────────────────────────────────

    [Fact]
    public void Guarded_ARefusedRowCarriesNoPriceAnywhereOnIt()
    {
        // The single assertion this whole file exists for. The comps object came in with $180 on
        // it; nothing downstream may ever see that $180, because $180 is what a tow hitch sells for.
        var outcome = Motors().Value(Cars, Truck(), Pricing(180m));

        Assert.Null(outcome.Pricing);
        Assert.Equal(ValuationStatuses.Manual, outcome.Valuation.Status);
        Assert.False(outcome.Valuation.HasPrice);
    }

    [Fact]
    public void Guarded_ARefusedRowSaysWhatWentWrongAndHandsBackTheSearch()
    {
        var outcome = Motors().Value(Cars, Truck(), Pricing(180m));

        Assert.Equal("estimate unavailable", outcome.Valuation.SourceLabel);
        Assert.Equal(LocalArbitrageEvidence.None, outcome.Valuation.Confidence);
        Assert.Equal(ResaleValuationProviders.EbayMotors, outcome.Valuation.ProviderId);
        Assert.Contains("parts", outcome.Valuation.Note, StringComparison.OrdinalIgnoreCase);
        // The search asks for the truck, not for "MUST SELL" — and it asks eBay Motors, because a
        // site-wide search for a Tundra is a page of floor mats.
        Assert.Equal("2011 Toyota Tundra SR5", outcome.Valuation.LookupQuery);
        Assert.Contains("_sacat=6001", outcome.Valuation.LookupUrl);
        Assert.Contains("LH_Sold=1", outcome.Valuation.LookupUrl);
    }

    [Fact]
    public void Guarded_NoSoldHistoryAtAllIsRefusedInThisCategorysOwnWords()
    {
        // Two providers, two vocabularies, because these sentences reach the seller verbatim and
        // "no sold history this app can price a vehicle from" on a dresser reads as a bug.
        var vehicle = Motors().Value(Cars, Truck(), comps: null);
        var dresser = Bulky().Value(Furniture, Listing("Solid oak dresser, 6 drawers", 600m), comps: null);

        Assert.Contains("a vehicle", vehicle.Valuation.Note);
        Assert.Contains("something this size", dresser.Valuation.Note);
        Assert.Null(vehicle.Pricing);
        Assert.Null(dresser.Pricing);
    }

    [Fact]
    public void Guarded_APricedRowSaysHowManyCompsStoodBehindIt()
    {
        var comps = Pricing(9_000m, comps: 3);

        var outcome = Motors().Value(Cars, Truck(), comps);

        Assert.Equal(ValuationStatuses.Comps, outcome.Valuation.Status);
        Assert.Equal("eBay Motors sold comps", outcome.Valuation.SourceLabel);
        Assert.Contains("3 sold comps", outcome.Valuation.Note);
        Assert.Contains("checked against the local ask", outcome.Valuation.Note);
        Assert.Same(comps, outcome.Pricing);
    }

    [Fact]
    public void Guarded_OneCompIsWrittenAsOneComp()
    {
        // A refusal that reads "Only 1 sold comps matched" is a warning the seller trusts less.
        var reason = Motors().Reject(Cars, Truck(), Pricing(9_000m, comps: 1));

        Assert.NotNull(reason);
        Assert.Contains("Only 1 sold comp matched", reason);
    }

    [Fact]
    public void Guarded_CountsTheCompsThatSetThePrice_NotTheOnesTheSearchReturned()
    {
        // The flattering number is what the lookup RETURNED. A twelve-result search that priced the
        // truck off one of them has one comp behind it, and one comp is how a board publishes a
        // four-figure return on a single loose sale.
        var flattering = new ResalePricing
        {
            LookupTitle = "2011 Toyota Tundra",
            Median = 9_000m, ExpectedSale = 9_000m,
            SoldCompCount = 12, PricedCompCount = 1, IdentityVerified = true,
        };

        var reason = Motors().Reject(Cars, Truck(), flattering);

        Assert.NotNull(reason);
        Assert.Contains("Only 1 sold comp matched", reason);
    }

    [Fact]
    public void Guarded_TerapeakCompsCountTowardsTheEvidence()
    {
        // The hosted database and Terapeak are two halves of one sold history. A truck priced off
        // four Terapeak comps and nothing else has four comps behind it, not zero.
        var terapeakOnly = new ResalePricing
        {
            LookupTitle = "2011 Toyota Tundra",
            Median = 9_000m, ExpectedSale = 9_000m,
            SoldCompCount = 0, PricedCompCount = 0, TerapeakCompCount = 4, IdentityVerified = true,
        };

        Assert.Null(Motors().Reject(Cars, Truck(), terapeakOnly));
    }

    // ── The bounds: what each kind of thing is allowed to be worth ───────────────────────────

    [Fact]
    public void Guarded_BigItemsGetLooserBoundsThanTitledOnes()
    {
        // Same two ratios, two answers. A $150 dresser that resells for $900 is an ordinary flip;
        // the same 6x on a truck is a comp for a different truck. And a resale at 30% of the ask is
        // a parts match on a vehicle and a fair markdown on a used appliance.
        var bulky = Bulky();
        var motors = Motors();
        var thing = Listing("Restoration Hardware Cloud sectional sofa", 1_000m);
        var truck = Truck(1_000m);

        Assert.Null(bulky.Reject(Furniture, thing, Pricing(6_000m)));
        Assert.NotNull(motors.Reject(Cars, truck, Pricing(6_000m)));

        Assert.Null(bulky.Reject(Furniture, thing, Pricing(300m)));
        Assert.NotNull(motors.Reject(Cars, truck, Pricing(300m)));
    }

    [Fact]
    public void Guarded_TheAskFloorKeepsTheRatioTestOffCheapRows()
    {
        // Below the floor the ratio says nothing either way: a $120 row that comps at $12 is as
        // likely to be a genuinely underpriced pickup as a bad match, and refusing it would drop
        // real flips off the board to protect a number nobody was going to spend $120 on.
        var cheap = Listing("Small pine nightstand", 120m);

        Assert.Null(Bulky().Reject(Furniture, cheap, Pricing(12m)));
        // One dollar over the floor and the same ratio is refused.
        Assert.NotNull(Bulky().Reject(Furniture, Listing("Solid oak dresser", 151m), Pricing(15m)));
    }

    [Theory]
    [InlineData(5_000, false)]      // exactly 5x — the stated multiple is allowed
    [InlineData(5_001, true)]
    [InlineData(400, false)]        // exactly 0.4x — likewise
    [InlineData(399, true)]
    public void Guarded_TheStatedMultipleIsTheEdge_NotAnApproximation(int resale, bool refused)
    {
        // The bounds are published on the provider and quoted at the seller ("a 5x return on a
        // vehicle is a comp for a different one"). A row at exactly the stated multiple has to fall
        // on the side the sentence claims it does.
        var reason = Motors().Reject(Cars, Truck(1_000m), Pricing(resale));

        Assert.Equal(refused, reason is not null);
    }

    [Fact]
    public void Guarded_TheRatioIsCheckedAgainstTheMedianWhenThereIsNoExpectedSale()
    {
        // Not every ResalePricing comes from the estimator — the trend projection and the
        // negotiation endpoint build them by hand with a median and no expected sale. A parts match
        // must not slip past the guard just because it arrived by the other door.
        var medianOnly = Pricing(expected: null, median: 180m);

        Assert.True(medianOnly.HasPrice);
        Assert.NotNull(Motors().Reject(Cars, Truck(), medianOnly));
    }

    [Fact]
    public void Guarded_AnUnpricedAdIsRefused_ButAGenuineGiveawayIsNot()
    {
        // A big-ticket ad with no price parses as free when the title happens to shout "FREE
        // SHIPPING!", and a $0 cost basis makes ROI unbounded — so a van nobody priced would clear
        // the goldmine bar on return alone. A real giveaway comes through the freebie board, which
        // states its own cost basis, and is left alone here.
        var unpriced = Truck(price: null);
        unpriced.IsFree = true;

        var reason = Motors().Reject(Cars, unpriced, Pricing(9_000m));
        Assert.NotNull(reason);
        Assert.Contains("doesn't state a price", reason);

        unpriced.Freebie = new FreebieDetails { Kind = FreebieKinds.Free, KindLabel = "Free" };
        Assert.Null(Motors().Reject(Cars, unpriced, Pricing(9_000m)));
    }

    // ── The search a refused row hands back ──────────────────────────────────────────────────

    [Fact]
    public void Links_TheSearchIsEbaysOwnSoldAndCompletedSearch()
    {
        var url = ResaleValuationLinks.SoldSearchUrl(Cars, "2011 Toyota Tundra SR5");

        Assert.StartsWith("https://www.ebay.com/sch/i.html?", url);
        Assert.Contains("_nkw=2011%20Toyota%20Tundra%20SR5", url);
        Assert.Contains("LH_Sold=1", url);
        Assert.Contains("LH_Complete=1", url);
        Assert.Contains("_sacat=6001", url);
    }

    [Fact]
    public void Links_ACategoryWithNoCornerOfTheSiteSearchesItAll()
    {
        Assert.DoesNotContain("_sacat", ResaleValuationLinks.SoldSearchUrl(Furniture, "solid oak dresser"));
        Assert.DoesNotContain("_sacat", ResaleValuationLinks.SoldSearchUrl(Anything, "impact wrench"));
    }

    [Fact]
    public void Links_APunctuatedTitleSurvivesTheUrlIntact()
    {
        // Classifieds titles are full of &, # and +. An unescaped ampersand doesn't break the link
        // visibly — it silently truncates the search to the first two words, which is a page of
        // results for something else and no way for the seller to tell.
        const string raw = "Ford F-150 5.0L & tow pkg #2 100% clean";

        var url = ResaleValuationLinks.SoldSearchUrl(Cars, raw);
        var parameters = new Uri(url).Query.TrimStart('?').Split('&');
        var nkw = parameters.Single(p => p.StartsWith("_nkw=", StringComparison.Ordinal));

        Assert.Equal(raw, Uri.UnescapeDataString(nkw["_nkw=".Length..]));
        // The ampersand in the title did not become a fourth parameter.
        Assert.Equal(4, parameters.Length);
    }

    [Fact]
    public void Links_NoQueryAtAllIsStillAUsableSearch()
    {
        var url = ResaleValuationLinks.SoldSearchUrl(Cars, null);

        Assert.True(Uri.IsWellFormedUriString(url, UriKind.Absolute));
        Assert.Contains("_nkw=&", url);
    }

    // ── End to end: the bulky board, which nothing else exercises ────────────────────────────

    [Fact]
    public void Build_ABulkyRowWhoseCompsAreForSomethingElseShowsNoPriceAtAll()
    {
        // Sold comps for "sectional sofa" are full of sofa COVERS, cushions and slipcovers, which is
        // the same failure as the tow hitch with two fewer zeroes on it.
        var listing = Listing("Restoration Hardware Cloud sectional sofa", 600m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(60m), Fees);

        Assert.Equal(ResaleCategoryCatalog.FurnitureId, row.CategoryId);
        Assert.Null(row.EbayExpectedSale);
        Assert.Null(row.NetProfit);
        Assert.Null(row.RoiPercent);
        Assert.Equal("no_data", row.Verdict);
        // And the row is still worth reading: the ask, the category and the search that fixes it.
        Assert.Equal(600m, row.LocalAsk);
        Assert.Equal(ValuationStatuses.Manual, row.Valuation!.Status);
        Assert.Equal(row.Valuation.Note, row.VerdictNote);
        Assert.Contains("LH_Sold=1", row.Valuation.LookupUrl);
    }

    [Fact]
    public void Build_APlausibleBulkyRowIsPricedWithEbaysCutAndNoShipping()
    {
        var listing = Listing("Restoration Hardware Cloud sectional sofa", 600m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(1_800m), Fees);

        Assert.Equal(ValuationStatuses.Comps, row.Valuation!.Status);
        Assert.Equal("eBay sold comps (checked against the local ask)", row.Valuation.SourceLabel);
        // It still sells on eBay, so the percentage fee still applies: 13.25% of $1,800 + $0.40.
        Assert.Equal(238.90m, row.EstimatedFees);
        // Nobody posts a sectional, so no label and no box come off the profit.
        Assert.False(row.Category!.ShipsToBuyer);
        Assert.Equal(1_800m - 600m - 238.90m, row.NetProfit);
    }
}
