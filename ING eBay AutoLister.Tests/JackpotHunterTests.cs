using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Roll the Dice hands someone a product they have never heard of and tells them to spend money on
// it, so what's pinned here is the honesty of it: which clusters are too vague or too thin to price
// at all, that the buy prices are the same arithmetic Local Deals uses, and that a "jackpot" badge
// is the same bar LocalArbitrageAnalyzer already calls a goldmine — never a friendlier one.
public class JackpotHunterTests
{
    private static readonly FeeProfile Fees = new();                       // 13.25% + $0.40, no shipping/labor
    private static readonly JackpotHunter Hunter = new(new ProfitCalculator());
    private static readonly LocalArbitrageAnalyzer Arbitrage = new(new ProfitCalculator());
    private static readonly ProductNormalizer Normalizer = new(new ProductIdentityExtractor());
    private static readonly DateTime Now = new(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);

    private static MarketplaceComparableResult Comp(
        string title, decimal price, int daysAgo = 10, int quantity = 1, string? imageUrl = null) =>
        new()
        {
            ItemId = Guid.NewGuid().ToString(), Title = title, SoldPrice = price, TotalPrice = price,
            SoldDate = Now.AddDays(-daysAgo), Quantity = quantity, ImageUrl = imageUrl,
        };

    private static ResalePricing Pricing(
        decimal? expected = 200m, int soldComps = 8, int terapeakComps = 0,
        decimal avgShipping = 0m, int confidence = 70, string level = "Good Confidence") =>
        new()
        {
            LookupTitle = "Bitmain Antminer S19j Pro 104TH",
            Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
            SoldCompCount = soldComps, TerapeakCompCount = terapeakComps, AvgCompShipping = avgShipping,
            ConfidenceScore = confidence, ConfidenceLevel = level, EstimatedDaysToSell = 14,
        };

    private static LocalSupplyListing LocalListing(decimal price, string id = "1") => new()
    {
        Source = "craigslist", SourceLabel = "Craigslist", ItemId = id,
        Title = "Antminer S19j Pro", Url = $"https://lasvegas.craigslist.org/{id}.html",
        Price = price, Location = "Las Vegas, NV",
    };

    private static JackpotSourceOption OptionAt(decimal buyPrice, ResalePricing? resale = null) =>
        JackpotSourceOption.From(Arbitrage.Build(LocalListing(buyPrice), resale ?? Pricing(), Fees));

    private static JackpotCandidate Candidate(
        int comps = 6, decimal median = 200m, decimal low = 150m, decimal high = 260m,
        int recent = 4, int? newestAgeDays = 10, bool loose = false,
        string title = "Bitmain Antminer S19j Pro 104TH") =>
        new()
        {
            NicheId = "mining", NicheLabel = "Crypto mining hardware", Probe = "antminer s19",
            Key = "s19j", LookupTitle = title, CompCount = comps, MedianSold = median,
            LowSold = low, HighSold = high, RecentCompCount = recent, NewestCompAgeDays = newestAgeDays,
            LooseIdentity = loose,
        };

    // ── Clustering: sold listings in, products out ────────────────────────────

    [Fact]
    public void ProductSignature_SameProductWordedDifferentlyIsOneCluster()
    {
        var (a, _) = JackpotHunter.ProductSignature("Bitmain Antminer S19j Pro 104TH Bitcoin Miner");
        var (b, _) = JackpotHunter.ProductSignature("Antminer S19j Pro 96TH ASIC Miner Tested Working");

        Assert.Equal("s19j", a);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ProductSignature_SpecTokensAreNotMistakenForModelNumbers()
    {
        // "104TH" and "3068W" describe the miner; "S19j" identifies it.
        var (_, model) = JackpotHunter.ProductSignature("Antminer 104TH 3068W S19j Pro");
        Assert.Equal("s19j", model);
    }

    [Fact]
    public void ProductSignature_ShortModelTokensCarryABrandAnchor()
    {
        // "m18" alone would collide with every other brand's M18; "v11" with every other V11.
        var (milwaukee, _) = JackpotHunter.ProductSignature("Milwaukee M18 FUEL Hammer Drill Kit");
        var (dyson, _) = JackpotHunter.ProductSignature("Dyson V11 Torque Drive Cordless Vacuum");

        Assert.Equal("milwaukee|m18", milwaukee);
        Assert.Equal("dyson|v11", dyson);
    }

    [Fact]
    public void ProductSignature_AWordFollowedByANumberIsAModel()
    {
        var (gpu, model) = JackpotHunter.ProductSignature("NVIDIA GeForce RTX 3080 Founders Edition");
        Assert.Equal("rtx3080", gpu);
        Assert.Equal("rtx3080", model);
    }

    [Fact]
    public void ProductSignature_NoModelDesignatorIsFlaggedLoose()
    {
        var (key, model) = JackpotHunter.ProductSignature("KitchenAid Stand Mixer White");
        Assert.Null(model);
        Assert.Equal("kitchenaid|stand", key);
    }

    [Fact]
    public void Cluster_GroupsOneProductAndSummarisesItsSoldHistory()
    {
        var candidates = JackpotHunter.Cluster(
        [
            Comp("Bitmain Antminer S19j Pro 104TH Bitcoin Miner", 300m),
            Comp("Antminer S19j Pro 96TH Miner Tested Working", 200m),
            Comp("Bitmain Antminer S19j Pro Miner 104TH PSU", 100m, daysAgo: 300),
        ], "mining", "Crypto mining hardware", "antminer s19", Now);

        var candidate = Assert.Single(candidates);
        Assert.Equal(3, candidate.CompCount);
        Assert.Equal(2, candidate.RecentCompCount);
        Assert.Equal(10, candidate.NewestCompAgeDays);
        Assert.Equal(200m, candidate.MedianSold);
        Assert.Equal(100m, candidate.LowSold);
        Assert.Equal(300m, candidate.HighSold);
        Assert.False(candidate.LooseIdentity);
        Assert.Equal("mining", candidate.NicheId);
        Assert.Equal("antminer s19", candidate.Probe);
    }

    [Fact]
    public void Cluster_PricesLotsPerUnitNotPerSticker()
    {
        var candidates = JackpotHunter.Cluster(
        [
            Comp("Lot of 4 Antminer S19j Pro Miners", 400m, quantity: 4),
            Comp("Antminer S19j Pro 104TH Miner", 110m),
            Comp("Bitmain Antminer S19j Pro Miner", 90m),
        ], "mining", "Crypto mining hardware", "antminer s19", Now);

        // The lot counts as a $100 comparable, so the median stays a single miner's price.
        var candidate = Assert.Single(candidates);
        Assert.Equal(100m, candidate.MedianSold);
        Assert.Equal(90m, candidate.LowSold);
        Assert.Equal(110m, candidate.HighSold);
    }

    [Fact]
    public void Cluster_PicksTheLeanestTitleToPriceAndSearchOn()
    {
        var candidates = JackpotHunter.Cluster(
        [
            Comp("**RARE** BITMAIN ANTMINER S19j Pro 104TH BITCOIN MINER FREE FAST SHIPPING LOOK", 300m),
            Comp("Antminer S19j Pro 104TH Miner", 290m),
        ], "mining", "Crypto mining hardware", "antminer s19", Now);

        Assert.Equal("Antminer S19j Pro 104TH Miner", Assert.Single(candidates).LookupTitle);
    }

    [Fact]
    public void Cluster_IgnoresRowsWithNoPriceOrNoTitle()
    {
        var candidates = JackpotHunter.Cluster(
        [
            Comp("Antminer S19j Pro 104TH Miner", 0m),
            Comp("   ", 250m),
        ], "mining", "Crypto mining hardware", "antminer s19", Now);

        Assert.Empty(candidates);
    }

    // ── Screening: what is not worth spending a lookup on ─────────────────────

    [Fact]
    public void Screen_KeepsAProductWithRealRecentHistory()
    {
        var (keep, reason) = JackpotHunter.Screen(Candidate(), Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH"));

        Assert.True(keep);
        Assert.Null(reason);
    }

    [Fact]
    public void Screen_DropsAccessoryComps()
    {
        var (keep, reason) = JackpotHunter.Screen(Candidate(), Normalizer.Normalize("Power Cable for Antminer S19j Pro"));

        Assert.False(keep);
        Assert.Contains("accessor", reason);
    }

    [Fact]
    public void Screen_DropsMultiUnitLots()
    {
        var (keep, reason) = JackpotHunter.Screen(Candidate(), Normalizer.Normalize("Lot of 4 Antminer S19j Pro Miners"));

        Assert.False(keep);
        Assert.Contains("lot", reason);
    }

    [Fact]
    public void Screen_DropsBrokenAndForPartsComps()
    {
        var (keep, reason) = JackpotHunter.Screen(
            Candidate(), Normalizer.Normalize("Antminer S19j Pro for parts not working"));

        Assert.False(keep);
        Assert.Contains("parts", reason);
    }

    [Fact]
    public void Screen_DropsThinHistory()
    {
        var identity = Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH");
        var (keep, reason) = JackpotHunter.Screen(Candidate(comps: JackpotHunter.MinCompsToPrice - 1), identity);

        Assert.False(keep);
        Assert.Contains("sold comp", reason);
    }

    [Fact]
    public void Screen_HoldsVagueClustersToAHigherEvidenceBar()
    {
        var identity = Normalizer.Normalize("Vitamix Professional Blender");

        // Five comps is plenty for a cluster keyed on a model number, and not enough for one keyed
        // on two ordinary words — "vitamix blender" could be six different machines.
        Assert.False(JackpotHunter.Screen(Candidate(comps: 5, loose: true), identity).Keep);
        Assert.True(JackpotHunter.Screen(Candidate(comps: JackpotHunter.MinCompsForLooseIdentity, loose: true), identity).Keep);
    }

    [Fact]
    public void Screen_DropsItemsTooCheapToCarryFeesAndShipping()
    {
        var identity = Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH");
        var (keep, reason) = JackpotHunter.Screen(Candidate(median: 25m, low: 20m, high: 30m), identity);

        Assert.False(keep);
        Assert.Contains("too little", reason);
    }

    [Fact]
    public void Screen_DropsDemandThatHasAlreadyBeenAndGone()
    {
        var identity = Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH");
        var (keep, reason) = JackpotHunter.Screen(Candidate(recent: 0, newestAgeDays: 400), identity);

        Assert.False(keep);
        Assert.Contains("sold in", reason);
    }

    // Undated comps are a hole in the data, not proof of staleness — the confidence score already
    // penalises unknown recency, so this must not be treated as a dead product.
    [Fact]
    public void Screen_KeepsProductsWhoseCompsCarryNoDates()
    {
        var identity = Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH");
        Assert.True(JackpotHunter.Screen(Candidate(recent: 0, newestAgeDays: null), identity).Keep);
    }

    [Fact]
    public void Screen_DropsClustersTooWideToBeOneProduct()
    {
        var identity = Normalizer.Normalize("Bitmain Antminer S19j Pro 104TH");
        var (keep, reason) = JackpotHunter.Screen(Candidate(low: 50m, high: 400m), identity);

        Assert.False(keep);
        Assert.Contains("one product", reason);
    }

    // ── The keyword the seller goes hunting with ──────────────────────────────

    [Fact]
    public void ShoppingQuery_StripsListingCopyAndStaysShort()
    {
        // "Pro" stays — it's part of the product's name, and dropping it would send someone hunting
        // for the wrong miner. What goes is the seller's shouting.
        Assert.Equal("bitmain antminer s19j pro 104th", JackpotHunter.ShoppingQuery(
            "**BITMAIN Antminer S19j Pro 104TH Bitcoin Miner** NEW SEALED FREE FAST SHIPPING"));
    }

    [Fact]
    public void ShoppingQuery_FallsBackToTheTitleRatherThanEmptyText()
    {
        Assert.Equal("A", JackpotHunter.ShoppingQuery("A"));
        Assert.Equal("", JackpotHunter.ShoppingQuery(null));
    }

    // ── Buying on eBay is costed exactly like buying locally ──────────────────

    [Fact]
    public void AsSupplyListing_FoldsShippingIntoWhatAcquisitionCosts()
    {
        var listing = JackpotHunter.AsSupplyListing(new EbayOpportunityItem
        {
            Title = "Antminer S19j Pro", Price = 120m, ShippingCost = 30m,
            Url = "https://www.ebay.com/itm/123", SellerUsername = "someseller",
        });

        Assert.Equal("ebay", listing.Source);
        Assert.Equal(150m, listing.Price);

        // And it prices through the same analyzer a Craigslist row does: $173.10 break-even at a
        // $200 resale, minus the $150 all-in cost.
        var row = Arbitrage.Build(listing, Pricing(expected: 200m), Fees);
        Assert.Equal(23.10m, row.NetProfit);
    }

    // ── The buy-side identity guard ───────────────────────────────────────────
    // Every one of these titles came back from a real search for the product itself, priced like a
    // bargain. Booking any of them as profit is how a "jackpot" becomes a lie.

    private static (bool Plausible, string? Reason) Supply(string title, decimal price, string product, decimal floor = 30m) =>
        JackpotHunter.IsPlausibleSupply(title, price, Normalizer.Normalize(title), product, floor);

    [Fact]
    public void IdentityTokens_AreTheBrandWordAndEveryModelNumber()
    {
        Assert.Equal(["irobot", "675"], JackpotHunter.IdentityTokens("iRobot Roomba 675 Robotic Vacuum Cleaner"));
        // "104TH" is a spec that varies between listings of the same miner, not an identifier.
        Assert.Equal(["bitmain", "s19j"], JackpotHunter.IdentityTokens("Bitmain Antminer S19j Pro 104TH"));
        Assert.Empty(JackpotHunter.IdentityTokens(""));
    }

    [Fact]
    public void IsPlausibleSupply_AcceptsTheProductItself()
    {
        var (plausible, reason) = Supply("iRobot Roomba 675 Robotic Vacuum Cleaner Black", 90m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner");

        Assert.True(plausible);
        Assert.Null(reason);
    }

    [Fact]
    public void IsPlausibleSupply_RejectsPartsAndConsumablesForTheProduct()
    {
        var filters = Supply("6-Pack HEPA Filters + Cleaning Brush for iRobot Roomba 675", 21.51m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner");
        Assert.False(filters.Plausible);

        var mopPad = Supply("Replacement Mop Pad Tray for iRobot Roomba Combo 10 Max", 29.90m,
            "iRobot Roomba Combo 10 Max Robot Vacuum");
        Assert.False(mopPad.Plausible);
    }

    [Fact]
    public void IsPlausibleSupply_RejectsAListingForADifferentModel()
    {
        var (plausible, reason) = Supply("iRobot Roomba 692 Robotic Vacuum Cleaner", 95m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner");

        Assert.False(plausible);
        Assert.Contains("675", reason);
    }

    [Fact]
    public void IsPlausibleSupply_RejectsBrokenAndForPartsUnits()
    {
        Assert.False(Supply("iRobot Roomba 675 Robotic Vacuum - For Parts Not Working", 40m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner").Plausible);
    }

    // Found by pointing the auction sniper at a real market: every one of these titles came back
    // from a live eBay search for the miner itself, each naming the brand AND the model number,
    // each around $15, and each priced as a spectacular flip against a $148 machine.
    [Fact]
    public void IsPlausibleSupply_RejectsFitForAccessoriesWrittenAsABareVerb()
    {
        var fan = Supply("Cooling Fan 4 pin fit Bitmain Antminer S19 XP S19j Pro", 15m,
            "Bitmain Antminer S19 Pro 110TH", floor: 12m);

        Assert.False(fan.Plausible);
        Assert.Contains("fit-for accessory", fan.Reason);
    }

    [Fact]
    public void IsPlausibleSupply_RejectsThePartsAKeywordSearchForAMinerReturns()
    {
        const string miner = "Bitmain Antminer S19 Pro 110TH";

        Assert.False(Supply("Antminer S19 T19 Fan Speed Controller E9 pro KS3", 15m, miner, floor: 12m).Plausible);
        Assert.False(Supply("4 pcs Fan Simulator Emulator Antminer S19 L7", 16.99m, miner, floor: 12m).Plausible);
        Assert.False(Supply("Antminer S19 TPU rubber vibration absorbing standoffs", 14.99m, miner, floor: 12m).Plausible);
        Assert.False(Supply("Used Bitmain Antminer S19 Pro Hashboard Tested", 59.99m, miner, floor: 12m).Plausible);
    }

    [Fact]
    public void IsPlausibleSupply_StillAcceptsAProductThatNamesItsOwnParts()
    {
        // The component check compares against the product's own title, so a machine being sold AS
        // a hashboard — or one whose title mentions its fans — is not rejected for saying so.
        Assert.True(Supply("Bitmain Antminer S19 Pro Hashboard 110TH Tested Working", 200m,
            "Bitmain Antminer S19 Pro Hashboard 110TH", floor: 30m).Plausible);
    }

    // Also found live, and the most dangerous rows a keyword search returns: they name the brand and
    // the model exactly, cost a dollar, and are not things in boxes.
    [Fact]
    public void IsPlausibleSupply_RejectsServicesSoldAlongsideTheProduct()
    {
        const string miner = "Antminer S21 200TH Bitcoin Miner";

        var hosting = Supply("ASIC Miner Hosting Europe - Antminer S21 S23 S19", 1m, miner, floor: 10m);
        Assert.False(hosting.Plausible);
        Assert.Contains("hosting", hosting.Reason);

        Assert.False(Supply("Overclock Antminer S21 S19 S17 - Adds 10-20% hashrate", 19.99m, miner, floor: 10m).Plausible);
        Assert.False(Supply("Bitmain Antminer S21 Repair Service Send In", 75m, miner, floor: 10m).Plausible);
    }

    [Fact]
    public void IsPlausibleSupply_DoesNotMistakeBenefitForFit()
    {
        // "benefit" ends in "fit", and a substring test here would reject half of eBay.
        Assert.True(Supply("iRobot Roomba 675 Robotic Vacuum Cleaner Benefit Sale", 90m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner").Plausible);
    }

    // The catch-all: whatever the title says, a price far under what the product itself ever sells
    // for means it isn't the product.
    [Fact]
    public void IsPlausibleSupply_RejectsPricesNoRealOneSellsAt()
    {
        var (plausible, reason) = Supply("iRobot Roomba 675 Robotic Vacuum Cleaner", 24.92m,
            "iRobot Roomba 675 Robotic Vacuum Cleaner", floor: 30m);

        Assert.False(plausible);
        Assert.Contains("floor", reason);
    }

    [Fact]
    public void SupplyPriceFloor_IsAFractionOfTheQuickSalePrice()
    {
        // Quick sale (P25) is $170 on a $200 expected sale, so a quarter of that is the floor.
        Assert.Equal(42.50m, JackpotHunter.SupplyPriceFloor(Pricing(expected: 200m)));
        Assert.Equal(0m, JackpotHunter.SupplyPriceFloor(new ResalePricing()));
    }

    [Fact]
    public void IsPlausibleSupply_RejectsAComponentTheProductItselfDoesntName()
    {
        // Same brand, same model number, $40, and it's a plastic water tank.
        var tank = Supply("Dirty Water Tank to iRobot Roomba Combo 10 Max ADL-N1", 42m,
            "iRobot Roomba Combo 10 Max Robot Vacuum and Mop");
        Assert.False(tank.Plausible);
        Assert.Contains("tank", tank.Reason);

        // But a product that legitimately ships with a dock isn't rejected for saying "dock".
        Assert.True(Supply("iRobot Roomba Combo 10 Max Robot Vacuum and Mop with AutoWash Dock", 900m,
            "iRobot Roomba Combo 10 Max Robot Vacuum and Mop + AutoWash Dock", floor: 200m).Plausible);
    }

    // ── Nothing goes on the board priced off two comps ────────────────────────

    [Fact]
    public void HasEnoughHistoryToShow_RequiresRealSoldHistory()
    {
        Assert.False(JackpotHunter.HasEnoughHistoryToShow(Pricing(soldComps: 2)));
        Assert.False(JackpotHunter.HasEnoughHistoryToShow(Pricing(expected: null, soldComps: 20)));

        Assert.True(JackpotHunter.HasEnoughHistoryToShow(Pricing(soldComps: JackpotHunter.MinCompsToBelieve)));
        // Terapeak comps count towards it — two sources agreeing is the strongest evidence there is.
        Assert.True(JackpotHunter.HasEnoughHistoryToShow(Pricing(soldComps: 2, terapeakComps: 3)));
    }

    // ── The two reads of the same product have to agree ───────────────────────

    [Fact]
    public void EstimateAgreesWithSweep_RejectsAnEstimateThatPricedADifferentProduct()
    {
        // The sweep clustered a $150 robot vacuum; the per-product lookup came back with $504.
        Assert.False(JackpotHunter.EstimateAgreesWithSweep(150m, Pricing(expected: 504m)));
        Assert.False(JackpotHunter.EstimateAgreesWithSweep(504m, Pricing(expected: 150m)));

        Assert.True(JackpotHunter.EstimateAgreesWithSweep(150m, Pricing(expected: 190m)));
        Assert.True(JackpotHunter.EstimateAgreesWithSweep(150m, Pricing(expected: 300m)));
    }

    [Fact]
    public void EstimateAgreesWithSweep_HasNothingToCheckWithoutBothNumbers()
    {
        Assert.True(JackpotHunter.EstimateAgreesWithSweep(0m, Pricing(expected: 504m)));
        Assert.True(JackpotHunter.EstimateAgreesWithSweep(150m, Pricing(expected: null)));
    }

    // ── The money ─────────────────────────────────────────────────────────────

    [Fact]
    public void BreakEvenBuyPrice_IsTheSameNumberLocalDealsCallsMaxToPay()
    {
        var breakEven = Hunter.BreakEvenBuyPrice(Pricing(expected: 200m), Fees);
        var localRow = Arbitrage.Build(LocalListing(50m), Pricing(expected: 200m), Fees);

        Assert.Equal(173.10m, breakEven);
        Assert.Equal(localRow.MaxBuyPrice, breakEven);
    }

    [Fact]
    public void BreakEvenBuyPrice_ChargesShippingTheCompsShowBuyersPaying()
    {
        // Buyers paid $20 and it costs $20 to send, so only eBay's fee on that $20 is lost.
        Assert.Equal(170.45m, Hunter.BreakEvenBuyPrice(Pricing(expected: 200m, avgShipping: 20m), Fees));
    }

    [Fact]
    public void BreakEvenBuyPrice_IsNegativeWhenFeesAndShippingExceedTheSalePrice()
    {
        var heavyShipping = new FeeProfile { DefaultShippingCost = 25m };
        Assert.True(Hunter.BreakEvenBuyPrice(Pricing(expected: 20m), heavyShipping) < 0);
    }

    [Fact]
    public void TargetBuyPrice_IsExactlyTheAskThatEarnsTheGoldmineBadge()
    {
        var resale = Pricing(expected: 200m);
        var target = JackpotHunter.TargetBuyPrice(Hunter.BreakEvenBuyPrice(resale, Fees));

        Assert.Equal(98.10m, target);

        // Paying it clears both halves of the bar the rest of the app uses...
        var atTarget = Arbitrage.Build(LocalListing(target), resale, Fees);
        Assert.Equal("goldmine", atTarget.Verdict);
        Assert.True(atTarget.NetProfit >= LocalArbitrageAnalyzer.GoldmineProfit);
        Assert.True(atTarget.RoiPercent >= LocalArbitrageAnalyzer.GoldmineRoiPercent);

        // ...and a dollar more does not, which is what makes it a number worth quoting.
        Assert.NotEqual("goldmine", Arbitrage.Build(LocalListing(target + 1m), resale, Fees).Verdict);
    }

    [Fact]
    public void TargetBuyPrice_IsZeroWhenNoPriceCanClearTheBar()
    {
        // A $60 item can never yield $75 of profit, however cheaply it's bought.
        Assert.Equal(0m, JackpotHunter.TargetBuyPrice(Hunter.BreakEvenBuyPrice(Pricing(expected: 60m), Fees)));
        Assert.Equal(0m, JackpotHunter.TargetBuyPrice(-5m));
    }

    // ── Plays ─────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildPlay_LiveSupplyThatClearsTheBarIsAJackpot()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(50m)], Fees);

        Assert.Equal("jackpot", play.Tier);
        Assert.Equal(123.10m, play.NetProfit);
        Assert.Equal(50m, play.BestBuyPrice);
        Assert.Equal(173.10m, play.MaxBuyPrice);
        Assert.Equal(98.10m, play.TargetBuyPrice);
        Assert.Equal(75.00m, play.ProfitAtTarget);
        // The wait is the whole wait: 14 days to sell, then packing, transit and eBay's payout.
        Assert.Equal(14, play.DaysToSell);
        Assert.Equal(14 + DaysToCashEstimator.PipelineDays, play.DaysToCash);
        Assert.Equal("steady", play.SpeedTier);
        // $123.10 net over 22 days of tied-up cash.
        Assert.Equal(Math.Round(play.NetProfit!.Value / play.DaysToCash!.Value, 2), play.ProfitPerDay);
        Assert.Contains("Craigslist", play.WhereToLook);
        Assert.True(play.HasLiveSupply);
    }

    [Fact]
    public void BuildPlay_PutsTheCheapestRealBuyFirst()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(150m, Pricing()), OptionAt(50m)], Fees);

        Assert.Equal(50m, play.Sources[0].BuyPrice);
        Assert.Equal(123.10m, play.NetProfit);
        Assert.Equal(2, play.Sources.Count);
    }

    [Fact]
    public void BuildPlay_NoSupplyButBelievableEvidenceIsATargetToHuntFor()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m, soldComps: 8, confidence: 70), [], Fees);

        Assert.Equal("target", play.Tier);
        Assert.Null(play.NetProfit);
        Assert.Equal(98.10m, play.TargetBuyPrice);
        Assert.Contains("98.1", play.TierNote);
        Assert.Contains("Nothing under", play.WhereToLook);
    }

    // Thin evidence must never be dressed up as an opportunity, however big the arithmetic.
    [Fact]
    public void BuildPlay_ThinEvidenceIsOnlyEverSomethingToWatch()
    {
        var play = Hunter.BuildPlay(
            Candidate(), Pricing(expected: 4000m, soldComps: 2, confidence: 20, level: "Insufficient Evidence"), [], Fees);

        Assert.Equal("watch", play.Tier);
        Assert.Contains("Only 2 sold comps", play.TierNote);

        // With plenty of comps but low confidence, the blocker is the match, not the count — saying
        // "not enough sold comps" in front of twenty of them reads as a bug.
        var manyButWeak = Hunter.BuildPlay(
            Candidate(), Pricing(expected: 400m, soldComps: 20, confidence: 41, level: "Limited Confidence"), [], Fees);
        Assert.Equal("watch", manyButWeak.Tier);
        Assert.Contains("20 sold comps", manyButWeak.TierNote);
        Assert.Contains("limited confidence", manyButWeak.TierNote);
    }

    // A real listing you can drive out and inspect earns "worth it" on three comps. A product a
    // sweep pushed at you has to bring more than that before the board calls it a strong play.
    [Fact]
    public void BuildPlay_ProfitOnThinEvidenceIsCappedAtThin()
    {
        var thin = Pricing(expected: 200m, soldComps: 3, confidence: 34, level: "Limited Confidence");

        Assert.Equal("solid", Arbitrage.Build(LocalListing(50m), thin, Fees).Verdict);

        var play = Hunter.BuildPlay(Candidate(), thin, [OptionAt(50m, thin)], Fees);
        Assert.Equal("thin", play.Tier);
        Assert.Contains("Evidence is thin", play.TierNote);
        Assert.Equal(123.10m, play.NetProfit);   // the money is still reported honestly
    }

    [Fact]
    public void BuildPlay_SupplyPricedAboveTheCeilingIsAPassNotATarget()
    {
        // Supply exists, it just costs more than the flip is worth — saying "nothing for sale"
        // would be the comfortable answer and the wrong one.
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(190m)], Fees);

        Assert.Equal("pass", play.Tier);
        Assert.True(play.NetProfit < 0);
        Assert.Contains("For sale now", play.WhereToLook);
    }

    [Fact]
    public void BuildPlay_ItemsThatCannotCarryTheirOwnFeesArePassedOn()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 20m), [], new FeeProfile { DefaultShippingCost = 25m });

        Assert.Equal("pass", play.Tier);
        Assert.Contains("no buy price", play.TierNote);
    }

    [Fact]
    public void BuildPlay_NoSoldHistoryIsNoData()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: null), [], Fees);

        Assert.Equal("no_data", play.Tier);
        Assert.Null(play.NetProfit);
        Assert.Equal(0m, play.MaxBuyPrice);
    }

    [Fact]
    public void BuildPlay_CarriesTheEvidenceOntoThePlay()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(soldComps: 8, terapeakComps: 4), [], Fees);

        Assert.Equal("hosted_comps+terapeak", play.ResaleSource);
        Assert.Equal(8, play.SoldCompCount);
        Assert.Equal(4, play.TerapeakCompCount);
        Assert.Equal("Bitmain Antminer S19j Pro 104TH", play.PricedAs);
        Assert.Equal("bitmain antminer s19j pro 104th", play.SearchQuery);
    }

    // ── Ranking ───────────────────────────────────────────────────────────────

    [Fact]
    public void Rank_PutsBelievableMoneyAboveBigNumbers()
    {
        var jackpot = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(50m)], Fees);
        var target = Hunter.BuildPlay(Candidate(), Pricing(expected: 300m), [], Fees);
        var watch = Hunter.BuildPlay(Candidate(), Pricing(expected: 9000m, soldComps: 2, confidence: 15), [], Fees);
        var pass = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(190m)], Fees);

        var ranked = JackpotHunter.Rank([watch, pass, target, jackpot]);

        Assert.Equal(["jackpot", "target", "watch", "pass"], ranked.Select(p => p.Tier));
    }

    [Fact]
    public void Rank_BreaksTiesOnMoneyThenOnEvidence()
    {
        var smaller = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [OptionAt(50m)], Fees);
        var bigger = Hunter.BuildPlay(Candidate(), Pricing(expected: 400m), [OptionAt(50m)], Fees);

        var ranked = JackpotHunter.Rank([smaller, bigger]);
        Assert.Equal(bigger.NetProfit, ranked[0].NetProfit);
    }

    [Fact]
    public void Rank_FastestCash_ReordersTheBoardByHowSoonTheMoneyComesBack()
    {
        var slow = Pricing(expected: 400m);
        slow.EstimatedDaysToSell = 150;
        var quick = Pricing(expected: 200m);
        quick.EstimatedDaysToSell = 5;

        var fat = Hunter.BuildPlay(Candidate(), slow, [OptionAt(50m, slow)], Fees);
        var nimble = Hunter.BuildPlay(Candidate(), quick, [OptionAt(50m, quick)], Fees);

        // Money-first still puts the bigger margin on top...
        Assert.Equal(fat.NetProfit, JackpotHunter.Rank([nimble, fat])[0].NetProfit);

        // ...and both velocity sorts put the money you get back this month on top instead.
        Assert.Equal(nimble.NetProfit,
            JackpotHunter.Rank([fat, nimble], LocalArbitrageAnalyzer.SortByFastestCash)[0].NetProfit);
        Assert.Equal(nimble.NetProfit,
            JackpotHunter.Rank([fat, nimble], LocalArbitrageAnalyzer.SortByProfitPerDay)[0].NetProfit);
    }

    [Fact]
    public void Rank_VelocitySorts_KeepUnbuyablePlaysAtTheBottom()
    {
        var fast = Pricing(expected: 200m);
        fast.EstimatedDaysToSell = 2;

        // Priced above the ceiling: a pass, however quickly the product itself moves.
        var pass = Hunter.BuildPlay(Candidate(), fast, [OptionAt(190m, fast)], Fees);
        var target = Hunter.BuildPlay(Candidate(), Pricing(expected: 300m), [], Fees);

        var ranked = JackpotHunter.Rank([pass, target], LocalArbitrageAnalyzer.SortByFastestCash);
        Assert.Equal(["target", "pass"], ranked.Select(p => p.Tier));
    }

    [Fact]
    public void BuildPlay_NoLiveSupply_StillRatesTheSpeedOfTheTargetBuy()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 200m), [], Fees);

        // Nothing is for sale, so the money being judged is what buying at the target would net.
        Assert.Null(play.NetProfit);
        Assert.Equal(14 + DaysToCashEstimator.PipelineDays, play.DaysToCash);
        Assert.Equal(Math.Round(play.ProfitAtTarget / play.DaysToCash!.Value, 2), play.ProfitPerDay);
        Assert.True(play.AnnualizedRoiPercent > 0);
    }

    [Fact]
    public void TierRank_OrdersEveryTierAndAnythingUnknownLast()
    {
        Assert.True(JackpotHunter.TierRank("jackpot") < JackpotHunter.TierRank("strong"));
        Assert.True(JackpotHunter.TierRank("strong") < JackpotHunter.TierRank("target"));
        Assert.True(JackpotHunter.TierRank("target") < JackpotHunter.TierRank("thin"));
        Assert.True(JackpotHunter.TierRank("thin") < JackpotHunter.TierRank("watch"));
        Assert.True(JackpotHunter.TierRank("watch") < JackpotHunter.TierRank("pass"));
        Assert.True(JackpotHunter.TierRank("pass") < JackpotHunter.TierRank("no_data"));
    }
}
