using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// The board used to assume one shape of flip: something you can box and post, priced against eBay
// sold comps and costed with eBay's percentage fee plus a shipping label. These pin the three ways
// that is wrong for the categories where the biggest local money is — the board that gets searched,
// what is allowed to value the row, and what selling it actually costs — and, above all, the rule
// that ties them together: this app never invents a resale price for something it cannot value.
public class CategoryArbitrageTests
{
    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/shipping/labor

    private static LocalSupplyListing Listing(string title, decimal? price, string id = "1") =>
        new()
        {
            Source = "craigslist", SourceLabel = "Craigslist", ItemId = id, Title = title,
            Url = $"https://lasvegas.craigslist.org/d/{id}.html", Price = price, Location = "Henderson",
        };

    private static ResalePricing Pricing(
        decimal expected, int comps = 6, int confidence = 70, bool identityVerified = true,
        string lookupTitle = "2011 Toyota Tundra") =>
        new()
        {
            LookupTitle = lookupTitle,
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.9m,
            SoldCompCount = comps, PricedCompCount = comps, IdentityVerified = identityVerified,
            ConfidenceScore = confidence, ConfidenceLevel = "Good",
        };

    // ── Sourcing: the right board, and the facts buried in a vehicle title ────────────────────

    [Theory]
    [InlineData(ResaleCategoryCatalog.CarsId, "cta")]
    [InlineData(ResaleCategoryCatalog.BoatsId, "boo")]
    [InlineData(ResaleCategoryCatalog.RvsId, "rva")]
    [InlineData(ResaleCategoryCatalog.TrailersId, "tra")]
    [InlineData(ResaleCategoryCatalog.PowersportsId, "mca")]
    [InlineData(ResaleCategoryCatalog.TractorsId, "hva")]
    [InlineData(ResaleCategoryCatalog.AppliancesId, "ppa")]
    [InlineData(ResaleCategoryCatalog.FurnitureId, "fua")]
    public void Category_SearchesItsOwnCraigslistBoard(string id, string board)
    {
        // The whole reason a category is not a filter: a keyword search of the for-sale board finds
        // four posts that happen to say "truck", where the cars board is the local truck market.
        Assert.Equal(board, ResaleCategoryCatalog.Resolve(id).CraigslistBoard);
    }

    [Fact]
    public void Category_UnknownIdSearchesEverything_RatherThanFailing()
    {
        Assert.Equal(ResaleCategoryCatalog.AnythingId, ResaleCategoryCatalog.Resolve("bicycles?").Id);
        Assert.Equal(ResaleCategoryCatalog.AnythingId, ResaleCategoryCatalog.Resolve(null).Id);
    }

    [Fact]
    public void Vehicle_ReadsYearMakeModelAndMileage()
    {
        var vehicle = VehicleTitleParser.Parse("2011 Toyota Tundra SR5 4x4 crew cab 137k miles");

        Assert.NotNull(vehicle);
        Assert.Equal(2011, vehicle!.Year);
        Assert.Equal("Toyota", vehicle.Make);
        Assert.Equal("Tundra SR5", vehicle.Model);
        Assert.Equal(137_000, vehicle.Mileage);
        Assert.Equal("2011 Toyota Tundra SR5", vehicle.Label);
        Assert.Equal("137,000 miles", vehicle.WearText);
        Assert.True(vehicle.IsIdentified);
    }

    [Theory]
    [InlineData("2008 Honda Accord 137,000 miles", 137_000)]
    [InlineData("2008 Honda Accord 137000 mi", 137_000)]
    [InlineData("2008 Honda Accord 89k", 89_000)]
    public void Vehicle_ReadsMileageHoweverItIsWritten(string title, int expected)
    {
        Assert.Equal(expected, VehicleTitleParser.Parse(title)!.Mileage);
    }

    [Fact]
    public void Vehicle_ReadsHoursWhereThatIsHowWearIsStated()
    {
        // Tractors, excavators and boats have no odometer. An hour meter is the same fact.
        var vehicle = VehicleTitleParser.Parse("2015 Kubota L3901 tractor 1,240 hours");

        Assert.Equal(1_240, vehicle!.EngineHours);
        Assert.Equal("1,240 hours", vehicle.WearText);
    }

    [Fact]
    public void Vehicle_UnstatedMileageIsUnknown_NeverZero()
    {
        // A zero-mile truck is a completely different thing from a truck that didn't say.
        Assert.Null(VehicleTitleParser.Parse("2011 Toyota Tundra 4x4")!.Mileage);
    }

    [Fact]
    public void Vehicle_TwoAdsForTheSameTruckShareAGroupKey()
    {
        var one = VehicleTitleParser.Parse("2011 Toyota Tundra SR5 — must see!!");
        var two = VehicleTitleParser.Parse("2011 TOYOTA TUNDRA SR5 crew cab, clean title");

        Assert.Equal(VehicleTitleParser.GroupKey(one), VehicleTitleParser.GroupKey(two));
        Assert.NotEqual("", VehicleTitleParser.GroupKey(one));
    }

    [Fact]
    public void Vehicle_DifferentVehiclesDoNotShareAGroupKey()
    {
        // The failure this stops: the generic product signature keys a classifieds title on the
        // brand, which would put these two in one group and price them off one lookup.
        Assert.NotEqual(
            VehicleTitleParser.GroupKey(VehicleTitleParser.Parse("2011 Toyota Tundra")),
            VehicleTitleParser.GroupKey(VehicleTitleParser.Parse("2003 Toyota Camry")));
    }

    [Fact]
    public void Vehicle_SearchQueryIsTheIdentity_NotTheAdCopy()
    {
        var vehicle = VehicleTitleParser.Parse("MUST SELL!! 2011 Toyota Tundra SR5 runs great");
        Assert.Equal("2011 Toyota Tundra SR5", VehicleTitleParser.SearchQuery(vehicle, "fallback"));
    }

    // ── Classification: the expensive misreads ───────────────────────────────────────────────

    [Fact]
    public void Classify_AVehicleTitleWithAYearAndAMakeIsAVehicle()
    {
        var listing = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", 8_500m);
        Assert.Equal(ResaleCategoryCatalog.CarsId, ResaleCategoryCatalog.Classify(listing).Id);
        Assert.NotNull(listing.Vehicle);
    }

    [Theory]
    [InlineData("2024 Ford F-150 tailgate assembly")]
    [InlineData("Toyota Tundra trailer hitch receiver OEM")]
    [InlineData("2015 Chevy Silverado floor mats")]
    public void Classify_APartThatNamesAVehicleIsNotAVehicle(string title)
    {
        // The single most expensive misread available: every one of these carries a year or a make,
        // and costing a $180 parcel flip a flat vehicle fee and a title transfer turns it into a
        // loss the board would then tell the seller to walk away from.
        var listing = Listing(title, 180m);

        Assert.Equal(ResaleCategoryCatalog.AnythingId, ResaleCategoryCatalog.Classify(listing).Id);
        Assert.Null(listing.Vehicle);
    }

    [Fact]
    public void Classify_ABrandAloneIsNotAVehicle()
    {
        // "Ford" is written on a keyring. Without a year there is nothing here to identify.
        Assert.Equal(ResaleCategoryCatalog.AnythingId,
            ResaleCategoryCatalog.Classify(Listing("Ford racing jacket size L", 40m)).Id);
    }

    [Fact]
    public void Classify_TravelTrailerIsAnRv_NotATrailer()
    {
        Assert.Equal(ResaleCategoryCatalog.RvsId,
            ResaleCategoryCatalog.Classify(Listing("2005 Jayco travel trailer 26ft", 9_000m)).Id);
    }

    [Fact]
    public void Classify_CargoTrailerIsATrailer_NotACar()
    {
        // "cargo" contains "car". Whole-word matching is the only thing standing between this row
        // and the wrong fee model — see VehicleTitleParser.ContainsWord.
        Assert.Equal(ResaleCategoryCatalog.TrailersId,
            ResaleCategoryCatalog.Classify(Listing("2019 cargo trailer 6x12 enclosed", 3_400m)).Id);
    }

    [Fact]
    public void Classify_PressureWasherIsATool_NotAnAppliance()
    {
        Assert.Equal(ResaleCategoryCatalog.ToolsId,
            ResaleCategoryCatalog.Classify(Listing("Simpson 3400 psi pressure washer", 320m)).Id);
    }

    [Fact]
    public void Classify_DishwasherIsNotMistakenForAWasher()
    {
        Assert.Equal(ResaleCategoryCatalog.AppliancesId,
            ResaleCategoryCatalog.Classify(Listing("Bosch 300 series dishwasher", 240m)).Id);
    }

    [Fact]
    public void Classify_TheSourcesOwnAnswerWins()
    {
        // Craigslist searched the cars board, so this is a car — a stronger statement than anything
        // a title parser could make about "CLEAN TITLE RUNS GREAT".
        var listing = Listing("CLEAN TITLE RUNS GREAT MUST SEE", 4_200m);
        listing.CategoryId = ResaleCategoryCatalog.CarsId;

        Assert.Equal(ResaleCategoryCatalog.CarsId, ResaleCategoryCatalog.Classify(listing).Id);
    }

    [Fact]
    public void Classify_ARequestedVehicleCategoryDoesNotApplyToACheapPart()
    {
        // A seller searching Cars & trucks on a keyword-only source gets hubcaps back. Costing one
        // as a car is exactly the failure the parts check exists for, requested category or not.
        var listing = Listing("set of 4 hubcaps", 40m);
        var category = ResaleCategoryCatalog.Classify(listing, ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId));

        Assert.Equal(ResaleCategoryCatalog.AnythingId, category.Id);
    }

    [Theory]
    [InlineData("Bitmain Antminer S19j Pro 104TH")]
    [InlineData("DeWalt 20V impact wrench with 2 batteries")]
    [InlineData("Vintage brass desk lamp")]
    public void Classify_TheOrdinaryParcelItemIsStillCostedAsAParcel(string title)
    {
        // The regression that matters most. A parcel item may pick up a nicer label — an Antminer is
        // Electronics rather than Anything — but the two answers that move money must not change:
        // it still sells on eBay for the percentage fee, and eBay sold comps still price it.
        var category = ResaleCategoryCatalog.Classify(Listing(title, 2_500m));

        Assert.Equal(SaleChannel.EbayParcel, category.Channel);
        Assert.Equal(ResaleValuationProviders.EbayComps, category.ValuationProviderId);
        Assert.False(category.ValuedByHand);
    }

    // ── The money: category-aware fees and costs ─────────────────────────────────────────────

    [Fact]
    public void Costs_TheDefaultCategoryIsTheParcelModel_Untouched()
    {
        var quote = CategoryCosts.For(ResaleCategoryCatalog.Anything, Fees, avgCompShipping: 12m);

        Assert.Same(Fees, quote.Fees);              // the seller's own profile, not a clone
        Assert.Equal(12m, quote.BuyerPaidShipping); // comp shipping booked on both sides
        Assert.Equal(12m, quote.ShippingOverride);
        Assert.Equal(0m, quote.OtherCosts);
        Assert.True(quote.Economics.ShipsToBuyer);
    }

    [Fact]
    public void Costs_AVehicleIsAFlatFee_NoShipping_AndATitle()
    {
        var quote = CategoryCosts.For(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), Fees, avgCompShipping: 12m);

        // The percentage fee is the parcel model, and on an $8,500 truck it is over a thousand
        // dollars of fee that is never charged.
        Assert.Equal(0m, quote.Fees.EbayFinalValueFeePercent);
        Assert.Equal(CategoryCosts.VehicleSaleFee, quote.Fees.EbayFinalValueFeeFixed);
        // Nobody posts a truck, so neither side of the shipping is booked.
        Assert.Equal(0m, quote.BuyerPaidShipping);
        Assert.Equal(0m, quote.ShippingOverride);
        Assert.Equal(CategoryCosts.TitleTransferCost, quote.OtherCosts);
        Assert.False(quote.Economics.ShipsToBuyer);
        // And the seller's own profile is left alone — this is a clone.
        Assert.Equal(13.25m, Fees.EbayFinalValueFeePercent);
    }

    [Fact]
    public void Costs_ABulkyLocalPickupKeepsEbaysCutAndDropsTheShipping()
    {
        var quote = CategoryCosts.For(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.FurnitureId), Fees, avgCompShipping: 40m);

        Assert.Equal(13.25m, quote.Fees.EbayFinalValueFeePercent);  // it still sells on eBay
        Assert.Equal(0m, quote.BuyerPaidShipping);
        Assert.Equal(0m, quote.ShippingOverride);
        Assert.Equal(0m, quote.OtherCosts);                          // no title on a sofa
        Assert.False(quote.Economics.ShipsToBuyer);
    }

    [Fact]
    public void Build_AVehicleIsCostedAsAVehicle()
    {
        var listing = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", 6_000m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(8_500m), Fees);

        // $8,500 sale: a flat $125 vehicle fee, no shipping either way, and $85 for the title.
        Assert.Equal(CategoryCosts.VehicleSaleFee, row.EstimatedFees);
        Assert.Equal(8_500m - 6_000m - CategoryCosts.VehicleSaleFee - CategoryCosts.TitleTransferCost, row.NetProfit);
        // The title is a real cost and it is not a fee eBay charges — it shows on its own line.
        Assert.Equal(CategoryCosts.TitleTransferCost, row.Category!.ExtraCostTotal);
        Assert.False(row.Category.ShipsToBuyer);
        Assert.Equal(ResaleCategoryCatalog.CarsId, row.CategoryId);
        Assert.Equal("2011 Toyota Tundra SR5", row.Vehicle!.Label);
    }

    [Fact]
    public void Build_TheParcelFeeModelWouldHaveInventedALossOnTheSameTruck()
    {
        var vehicle = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", 6_000m);
        ResaleCategoryCatalog.Classify(vehicle);
        var parcel = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", 6_000m, id: "2");
        // Deliberately left unclassified — the old behaviour, for comparison.

        var costedAsVehicle = Analyzer.Build(vehicle, Pricing(8_500m), Fees);
        var costedAsParcel = Analyzer.Build(parcel, Pricing(8_500m), Fees);

        // 13.25% of $8,500 is $1,126 of fee eBay does not charge on a vehicle sale. That is the gap
        // between a $2,290 flip and a $1,373 one, on the same truck at the same price.
        Assert.True(costedAsVehicle.NetProfit > costedAsParcel.NetProfit);
        Assert.True(costedAsVehicle.NetProfit - costedAsParcel.NetProfit > 800m);
    }

    [Fact]
    public void Build_MaxBuyPriceOnAVehicleUsesTheVehicleCosts()
    {
        var listing = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", 6_000m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(8_500m), Fees);

        // Paying exactly that leaves nothing — the number to walk into the driveway with.
        Assert.Equal(6_000m + row.NetProfit, row.MaxBuyPrice);

        var atCeiling = Listing("2011 Toyota Tundra SR5 4x4 crew cab 137k miles", row.MaxBuyPrice!.Value, id: "3");
        ResaleCategoryCatalog.Classify(atCeiling);
        Assert.Equal(0m, Analyzer.Build(atCeiling, Pricing(8_500m), Fees).NetProfit);
    }

    // ── Valuation: refusing, rather than inventing ───────────────────────────────────────────

    private static GuardedCompsValuationProvider Motors() =>
        (GuardedCompsValuationProvider)ResaleValuationRegistry.BuildDefaults()
            .First(p => p.Id == ResaleValuationProviders.EbayMotors);

    [Fact]
    public void Valuation_APartsMatchIsRefused_NotPublished()
    {
        // The exact failure: an $8,500 truck whose "comps" are four tow hitches at $180. The
        // arithmetic on that is flawless and the answer is nonsense.
        var listing = Listing("2011 Toyota Tundra SR5 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        var reason = Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), listing, Pricing(180m));

        Assert.NotNull(reason);
        Assert.Contains("parts", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Valuation_AnAbsurdMultipleOnATitledThingIsRefused()
    {
        var listing = Listing("1998 Bayliner 175 bowrider boat", 600m);
        ResaleCategoryCatalog.Classify(listing);

        Assert.NotNull(Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.BoatsId), listing, Pricing(9_000m)));
    }

    [Fact]
    public void Valuation_TooFewCompsRefusesOutright_RatherThanEstimating()
    {
        // On a $60 gadget two comps is a labelled estimate. On a four-figure buy there is no version
        // of "estimate" worth putting next to the number.
        var listing = Listing("2011 Toyota Tundra SR5 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        Assert.NotNull(Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), listing, Pricing(9_000m, comps: 2)));
    }

    [Fact]
    public void Valuation_AnUnverifiedIdentityIsRefused()
    {
        var listing = Listing("2011 Toyota Tundra SR5 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        Assert.NotNull(Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), listing,
            Pricing(9_000m, identityVerified: false)));
    }

    [Fact]
    public void Valuation_AnUnpricedVehicleAdIsNeverTreatedAsFree()
    {
        // Straight off the live cars board: "*100,000 MILES WARRANTY*WHEELCHAIR VAN*FREE SHIPPING!"
        // with no price. The word "free" in the title makes the parser read it as a giveaway, and a
        // $0 cost basis makes ROI unbounded — so an ad nobody priced would clear the goldmine bar on
        // return alone. Nobody gives away a van; the ad just didn't say.
        var listing = Listing("*100,000 MILES WARRANTY*WHEELCHAIR VAN VANS*FREE SHIPPING!", price: null);
        listing.IsFree = true;
        listing.CategoryId = ResaleCategoryCatalog.CarsId;

        var reason = Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), listing, Pricing(9_000m));

        Assert.NotNull(reason);
        Assert.Contains("doesn't state a price", reason!);
    }

    [Fact]
    public void Valuation_PlausibleCompsArePricedNormally()
    {
        var listing = Listing("2011 Toyota Tundra SR5 137k miles", 6_000m);
        ResaleCategoryCatalog.Classify(listing);

        Assert.Null(Motors().Reject(
            ResaleCategoryCatalog.Resolve(ResaleCategoryCatalog.CarsId), listing, Pricing(8_500m)));
    }

    [Fact]
    public void Build_ARefusedValuationShowsNoPriceAtAll()
    {
        var listing = Listing("2011 Toyota Tundra SR5 4x4 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(180m), Fees);

        // Nothing invented anywhere: no resale price, no profit, no ROI, no verdict but "no data".
        Assert.Null(row.EbayExpectedSale);
        Assert.Null(row.NetProfit);
        Assert.Null(row.RoiPercent);
        Assert.Equal("no_data", row.Verdict);
        // And an answer rather than a shrug: what went wrong, and the search that fixes it.
        Assert.Equal(ValuationStatuses.Manual, row.Valuation!.Status);
        Assert.Contains("2011", row.Valuation.LookupQuery);
        Assert.Contains("LH_Sold=1", row.Valuation.LookupUrl);
        Assert.Equal(row.Valuation.Note, row.VerdictNote);
    }

    [Fact]
    public void Build_ARefusedValuationStillSaysWhatTheThingCosts()
    {
        var listing = Listing("2011 Toyota Tundra SR5 4x4 137k miles", 8_500m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(180m), Fees);

        // Unpriceable is not unusable: the ask, the category and the identity are all still worth
        // reading, which is the difference between a lead and a dropped row.
        Assert.Equal(8_500m, row.LocalAsk);
        Assert.Equal(ResaleCategoryCatalog.CarsId, row.CategoryId);
        Assert.Equal("2011 Toyota Tundra SR5", row.Vehicle!.Label);
    }

    [Fact]
    public void Build_AnOrdinaryParcelRowIsLabelledAndOtherwiseUnchanged()
    {
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(200m, lookupTitle: "Bitmain Antminer S19j Pro"), Fees);

        // The pre-existing numbers, to the cent: $200 sale, 13.25% + $0.40 = $26.90.
        Assert.Equal(26.90m, row.EstimatedFees);
        Assert.Equal(123.10m, row.NetProfit);
        // Plus the new labelling, which every row carries whether or not its money changed.
        Assert.Equal(ValuationStatuses.Comps, row.Valuation!.Status);
        Assert.Equal("eBay sold comps", row.Valuation.SourceLabel);
        Assert.Equal(LocalArbitrageAnalyzer.EvidenceConfident, row.Valuation.Confidence);
        Assert.True(row.Category!.ShipsToBuyer);
    }

    [Fact]
    public void Build_TheValuationConfidenceIsTheSameGradingTheRowDims_NotASecondOne()
    {
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(200m, comps: 1), Fees);

        Assert.Equal(LocalArbitrageAnalyzer.EvidenceLow, row.EvidenceTier);
        Assert.Equal(row.EvidenceTier, row.Valuation!.Confidence);
    }

    [Fact]
    public void Build_NoSoldHistoryStillReadsAsNoSoldHistory()
    {
        // The pre-existing message on the pre-existing path, unchanged by the valuation layer.
        var listing = Listing("Bitmain Antminer S19j Pro 104TH", 50m);
        ResaleCategoryCatalog.Classify(listing);

        var row = Analyzer.Build(listing, Pricing(0m, comps: 0), Fees);

        Assert.Equal("no_data", row.Verdict);
        Assert.Equal("No eBay sold history matched this title.", row.VerdictNote);
    }

    // ── The board ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Tally_CountsEachCategoryAndHowManyOfItCouldBePriced()
    {
        var rows = new List<LocalArbitrageOpportunity>
        {
            new() { CategoryId = ResaleCategoryCatalog.CarsId, EbayExpectedSale = null },
            new() { CategoryId = ResaleCategoryCatalog.CarsId, EbayExpectedSale = 8_500m },
            new() { CategoryId = ResaleCategoryCatalog.ToolsId, EbayExpectedSale = 220m },
        };

        var tally = ResaleCategoryCatalog.Tally(rows);

        var cars = tally.Single(t => t.Id == ResaleCategoryCatalog.CarsId);
        Assert.Equal(2, cars.Count);
        // The useful half: this app put a number on one of the two, and the other needs a look.
        Assert.Equal(1, cars.PricedCount);
        Assert.Equal("Cars & trucks", cars.Label);
    }

    [Fact]
    public void Describe_SaysWhichCategoriesThisAppCannotPrice()
    {
        var described = ResaleCategoryCatalog.Describe();

        Assert.True(described.Single(c => c.Id == ResaleCategoryCatalog.CarsId).ValuedByHand);
        Assert.False(described.Single(c => c.Id == ResaleCategoryCatalog.AnythingId).ValuedByHand);
        // Every category names its group, because the picker renders them under headings.
        Assert.All(described, c => Assert.False(string.IsNullOrWhiteSpace(c.Group)));
    }
}
