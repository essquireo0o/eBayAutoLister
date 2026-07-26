using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// A lot is one decision with hundreds or thousands of dollars on it, made once, on numbers nobody
// checks afterwards. These cases pin the three things that decide it: the cost side (which is
// where the buyer's premium and the per-unit shipping hide), the max bid (which has to be exact
// arithmetic or it is worthless in a negotiation), and every case where the analyzer is supposed
// to refuse to make a call rather than make a confident wrong one.
public class LotAnalyzerTests
{
    private static readonly LotAnalyzer Analyzer = new(new ProfitCalculator());
    private static readonly FeeProfile Fees = new(); // 13.25% + $0.40, no promoted/packaging/labor

    private static LotGradeAssumption NoHaircut =>
        LotAnalyzer.Assumptions("new", sellableOverride: 100m, priceFactorOverride: 100m);

    private static ManifestLine Line(
        string description = "DeWalt DCD771C2 20V Drill Kit", int qty = 1, decimal? retail = null) =>
        new() { Description = description, SearchQuery = description, Quantity = qty, UnitRetail = retail };

    private static ResalePricing Pricing(
        decimal quickSale = 100m, int soldComps = 8, decimal avgShipping = 0m,
        int confidence = 70, decimal monthlySales = 0m) =>
        new()
        {
            LookupTitle = "DeWalt DCD771C2 20V Drill Kit",
            Median = quickSale * 1.15m, ExpectedSale = quickSale * 1.1m, QuickSale = quickSale,
            SoldCompCount = soldComps, AvgCompShipping = avgShipping,
            ConfidenceScore = confidence, ConfidenceLevel = "Good",
            EstimatedMonthlySales = monthlySales,
        };

    // ── The cost side ────────────────────────────────────────────────────────

    [Fact]
    public void CostOf_AddsPremiumThenTaxThenFreight()
    {
        var cost = LotAnalyzer.CostOf(askPrice: 1000m, buyerPremiumPercent: 15m, salesTaxPercent: 8.375m, freight: 250m);

        Assert.Equal(1000m, cost.AskPrice);
        Assert.Equal(150m, cost.BuyerPremium);
        // Tax follows the hammer PLUS the premium, which is how auction houses bill it.
        Assert.Equal(96.31m, cost.SalesTax);
        Assert.Equal(250m, cost.FreightCost);
        Assert.Equal(1496.31m, cost.TotalCost);
    }

    // The premium is the cost line pallet buyers forget most often, and it is charged before the
    // lot has earned a cent.
    [Fact]
    public void CostOf_APremiumAloneMovesTheAllInCostByMoreThanItsFaceValue()
    {
        var without = LotAnalyzer.CostOf(1000m, 0m, 8m, 0m);
        var with = LotAnalyzer.CostOf(1000m, 18m, 8m, 0m);

        Assert.True(with.TotalCost - without.TotalCost > 180m);
    }

    [Fact]
    public void CostOf_TreatsNegativeInputsAsZero()
    {
        var cost = LotAnalyzer.CostOf(-500m, -10m, -5m, -100m);

        Assert.Equal(0m, cost.TotalCost);
    }

    // ── One line ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildLine_ChargesEbayFeesOnEveryUnit()
    {
        var row = Analyzer.BuildLine(Line(qty: 10), Pricing(quickSale: 100m), NoHaircut, Fees, perUnitHandlingCost: 0m);

        Assert.Equal("priced", row.Status);
        Assert.Equal(10m, row.SellableUnits);
        Assert.Equal(1000m, row.GrossResale);
        // 13.25% of $100 plus $0.40 is $13.65 a unit, ten times over.
        Assert.Equal(136.50m, row.EstimatedFees);
        Assert.Equal(863.50m, row.NetRecovery);
    }

    // The line that kills pallet math: 200 items at $8 to ship is $1,600 that never appears on
    // the spreadsheet the lot was bought on.
    [Fact]
    public void BuildLine_ChargesShippingOnEveryUnitSold()
    {
        var row = Analyzer.BuildLine(Line(qty: 20), Pricing(quickSale: 50m), NoHaircut, Fees, perUnitHandlingCost: 8m);

        Assert.Equal(160m, row.EstimatedShipCost);
        Assert.Equal(1000m, row.GrossResale);
        Assert.True(row.NetRecovery < 1000m - 160m);
    }

    [Fact]
    public void BuildLine_BooksBuyerPaidShippingAsRevenueAndAsCost()
    {
        var withShipping = Analyzer.BuildLine(
            Line(qty: 1), Pricing(quickSale: 100m, avgShipping: 12m), NoHaircut, Fees, perUnitHandlingCost: 0m);

        // Buyers paid $112 in total and eBay charges its fee on all of it.
        Assert.Equal(112m, withShipping.GrossResale);
        Assert.Equal(12m, withShipping.EstimatedShipCost);
    }

    // Lots are liquidated, not held out for the top price, so the honest basis is the quick-sale
    // figure rather than the optimistic one.
    [Fact]
    public void BuildLine_PricesOnTheQuickSaleFigureNotTheExpectedOne()
    {
        var row = Analyzer.BuildLine(Line(), Pricing(quickSale: 80m), NoHaircut, Fees, 0m);

        Assert.Equal(80m, row.CompUnitPrice);
    }

    [Fact]
    public void BuildLine_AppliesTheGradesRecoveryAssumptions()
    {
        var grade = LotAnalyzer.Assumptions("customer_returns", null, null); // 80% sellable at 88% of comps
        var row = Analyzer.BuildLine(Line(qty: 10), Pricing(quickSale: 100m), grade, Fees, 0m);

        Assert.Equal(8m, row.SellableUnits);
        Assert.Equal(88m, row.UnitResale);
        Assert.Equal(704m, row.GrossResale);
    }

    // Expected value, kept fractional: rounding a 0.65 recovery down to zero per line would
    // quietly delete most of a mixed manifest's value.
    [Fact]
    public void BuildLine_KeepsFractionalSellableUnitsOnSingleUnitLines()
    {
        var grade = LotAnalyzer.Assumptions("salvage", null, null); // 40% sellable
        var row = Analyzer.BuildLine(Line(qty: 1), Pricing(quickSale: 200m), grade, Fees, 0m);

        Assert.Equal(0.4m, row.SellableUnits);
        Assert.True(row.NetRecovery > 0m);
    }

    [Fact]
    public void BuildLine_ReportsNoDataWhenNothingMatched()
    {
        var row = Analyzer.BuildLine(Line(), resale: null, NoHaircut, Fees, 0m);

        Assert.Equal("no_data", row.Status);
        Assert.Equal(0m, row.NetRecovery);
    }

    [Fact]
    public void BuildLine_FlagsThinEvidenceRatherThanPresentingItAsPriced()
    {
        var row = Analyzer.BuildLine(Line(qty: 5), Pricing(quickSale: 100m, soldComps: 2), NoHaircut, Fees, 0m);

        Assert.Equal("thin", row.Status);
        Assert.Contains("2 sold comps", row.StatusNote);
        // Still valued — thin evidence is a caveat on the number, not a reason to hide it.
        Assert.True(row.NetRecovery > 0m);
    }

    // ── The guards: where the analyzer refuses to price ──────────────────────

    // Sold comps are per single item. A "case of 12" line matched against them is the exact
    // mistake that produced a 27% markdown on a working listing in the inventory scan.
    [Fact]
    public void BuildLine_RefusesToPriceAMultiPackLineAgainstSingleUnitComps()
    {
        var row = Analyzer.BuildLine(
            Line("Lot of 12 Anker PowerCore 10000 chargers", qty: 3), Pricing(quickSale: 26m),
            NoHaircut, Fees, 0m, packQuantity: 12);

        Assert.Equal("excluded", row.Status);
        Assert.Contains("multi-pack", row.ExclusionReason);
        Assert.Equal(0m, row.NetRecovery);
    }

    // The manifest's retail column is worthless as a value and excellent as a cross-check.
    [Fact]
    public void RetailSanityCheck_RejectsACompFarAboveTheStatedRetail()
    {
        var reason = LotAnalyzer.RetailSanityCheck(compUnitPrice: 300m, unitRetail: 19.99m);

        Assert.NotNull(reason);
        Assert.Contains("mismatched product", reason);
    }

    // The low side costs money too: an accessory match talks a buyer out of a good lot.
    [Fact]
    public void RetailSanityCheck_RejectsACompThatLooksLikeAnAccessory()
    {
        var reason = LotAnalyzer.RetailSanityCheck(compUnitPrice: 4m, unitRetail: 400m);

        Assert.NotNull(reason);
        Assert.Contains("accessory", reason);
    }

    [Fact]
    public void RetailSanityCheck_AllowsALegitimateGapBetweenRetailAndResale()
    {
        // Used goods routinely sell for a fraction of MSRP — that is the whole business.
        Assert.Null(LotAnalyzer.RetailSanityCheck(compUnitPrice: 42m, unitRetail: 169m));
        Assert.Null(LotAnalyzer.RetailSanityCheck(compUnitPrice: 180m, unitRetail: 169m));
    }

    // The one the first live run against the real comps database produced: a DeWalt DCD771C2 kit
    // priced at $14 on two comps against a $169 stated retail. That is a spare battery, not a
    // drill kit, and left alone it drags a whole lot towards a wrong SKIP.
    [Fact]
    public void RetailSanityCheck_RefusesADeepDiscountThatOnlyTwoCompsStandBehind()
    {
        var reason = LotAnalyzer.RetailSanityCheck(compUnitPrice: 14m, unitRetail: 169m, compCount: 2);

        Assert.NotNull(reason);
        Assert.Contains("not enough history", reason);
    }

    // Same ratio, real history behind it: some categories genuinely resell at a tenth of MSRP,
    // and five or more sold comps is the bar the rest of the app uses to settle exactly this.
    [Fact]
    public void RetailSanityCheck_AcceptsTheSameDiscountWhenTheHistorySupportsIt()
    {
        Assert.Null(LotAnalyzer.RetailSanityCheck(compUnitPrice: 14m, unitRetail: 169m, compCount: 9));
    }

    // A ratio this extreme is an accessory match however many comps agree with it.
    [Fact]
    public void RetailSanityCheck_RejectsAnAccessoryMatchNoMatterHowMuchHistoryItHas()
    {
        Assert.NotNull(LotAnalyzer.RetailSanityCheck(compUnitPrice: 4m, unitRetail: 400m, compCount: 40));
    }

    [Fact]
    public void RetailSanityCheck_SaysNothingWhenTheManifestStatesNoRetail()
    {
        Assert.Null(LotAnalyzer.RetailSanityCheck(compUnitPrice: 5000m, unitRetail: null, compCount: 1));
    }

    [Fact]
    public void BuildLine_ExcludesALineWhoseCompFailsTheRetailCrossCheck()
    {
        var row = Analyzer.BuildLine(Line("Phone case", qty: 40, retail: 19.99m), Pricing(quickSale: 300m), NoHaircut, Fees, 0m);

        Assert.Equal("excluded", row.Status);
        Assert.Equal(0m, row.NetRecovery);
    }

    [Fact]
    public void BuildLine_ExcludesADeepDiscountBackedByTooLittleHistory()
    {
        var row = Analyzer.BuildLine(
            Line("DeWalt DCD771C2 20V Drill Kit", qty: 4, retail: 169m),
            Pricing(quickSale: 14m, soldComps: 2), NoHaircut, Fees, 0m);

        Assert.Equal("excluded", row.Status);
        Assert.Contains("$169", row.ExclusionReason);
        Assert.Equal(0m, row.NetRecovery);
    }

    // ── Cost allocation ──────────────────────────────────────────────────────

    [Fact]
    public void AllocateCost_SplitsTheAskProRataByRecoveredValue()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { Description = "big", NetRecovery = 750m },
            new() { Description = "small", NetRecovery = 250m },
        };

        LotAnalyzer.AllocateCost(lines, totalCost: 400m);

        Assert.Equal(300m, lines[0].AllocatedCost);
        Assert.Equal(100m, lines[1].AllocatedCost);
        Assert.Equal(450m, lines[0].NetProfit);
        Assert.Equal(150m, lines[1].NetProfit);
        Assert.Equal(150m, lines[0].RoiPercent);
    }

    [Fact]
    public void AllocateCost_AllocatesEveryCentSoTheLinesAddUpToTheLot()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { NetRecovery = 100m }, new() { NetRecovery = 100m }, new() { NetRecovery = 100m },
        };

        LotAnalyzer.AllocateCost(lines, totalCost: 100m);

        Assert.Equal(100m, lines.Sum(l => l.AllocatedCost));
    }

    // A line that recovered nothing did not cost anything to have on the pallet — it just failed
    // to pay for itself, and charging it a share would double-count the loss.
    [Fact]
    public void AllocateCost_ChargesNothingToLinesThatRecoveredNothing()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { NetRecovery = 500m },
            new() { NetRecovery = 0m, Status = "no_data" },
        };

        LotAnalyzer.AllocateCost(lines, totalCost: 200m);

        Assert.Equal(200m, lines[0].AllocatedCost);
        Assert.Equal(0m, lines[1].AllocatedCost);
    }

    // ── Totals ───────────────────────────────────────────────────────────────

    [Fact]
    public void Summarize_ReportsNetProfitAfterTheWholeCostOfTheLot()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { GrossResale = 2000m, EstimatedFees = 265m, EstimatedShipCost = 160m, NetRecovery = 1575m, SellableUnits = 20m },
        };
        var cost = LotAnalyzer.CostOf(800m, 15m, 0m, 100m);

        var totals = LotAnalyzer.Summarize(lines, cost, manifestUnits: 20, manifestRetailTotal: 6000m);

        Assert.Equal(1020m, totals.TotalCost);
        Assert.Equal(555m, totals.NetProfit);
        Assert.Equal(54.4m, totals.RoiPercent);
        Assert.Equal(51m, totals.CostPerSellableUnit);
    }

    // The headline every liquidation listing leads with, tested against what the items really do.
    [Fact]
    public void Summarize_ReportsResaleAsAPercentageOfTheManifestsClaimedRetail()
    {
        var lines = new List<LotLineAnalysis> { new() { GrossResale = 2400m, NetRecovery = 1800m } };

        var totals = LotAnalyzer.Summarize(lines, LotAnalyzer.CostOf(0m, 0m, 0m, 0m), 40, manifestRetailTotal: 12000m);

        Assert.Equal(20m, totals.ResalePercentOfRetail);
    }

    // Capital comes back when the LAST item sells, not the median one.
    [Fact]
    public void Summarize_ReportsTheSlowestLineAndTheTypicalOne()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { NetRecovery = 100m, EstimatedDaysToSell = 20 },
            new() { NetRecovery = 100m, EstimatedDaysToSell = 60 },
            new() { NetRecovery = 100m, EstimatedDaysToSell = 400 },
        };

        var totals = LotAnalyzer.Summarize(lines, LotAnalyzer.CostOf(0m, 0m, 0m, 0m), 3, 0m);

        Assert.Equal(400, totals.DaysToSellSlowestLine);
        Assert.Equal(60, totals.MedianDaysToSell);
    }

    [Fact]
    public void BuildLine_TimesTheWholeQuantityNotOneUnit()
    {
        // Two sell a month; twenty units is ten months, not the days one unit takes.
        var row = Analyzer.BuildLine(Line(qty: 20), Pricing(quickSale: 50m, monthlySales: 2m), NoHaircut, Fees, 0m);

        Assert.Equal(300, row.EstimatedDaysToSell);
    }

    // ── Max bid ──────────────────────────────────────────────────────────────

    [Fact]
    public void MaxAsk_SolvesTheBreakEvenAskExactly()
    {
        var ask = LotAnalyzer.MaxAsk(netRecovery: 1000m, buyerPremiumPercent: 0m, salesTaxPercent: 0m,
            freight: 0m, targetRoiPercent: 0m);

        Assert.Equal(1000m, ask);
    }

    [Fact]
    public void MaxAsk_TakesThePremiumTaxAndFreightOutOfTheNumberYouBidTo()
    {
        var ask = LotAnalyzer.MaxAsk(netRecovery: 1200m, buyerPremiumPercent: 20m, salesTaxPercent: 0m,
            freight: 200m, targetRoiPercent: 0m);

        // (1200 - 200) / 1.2
        Assert.Equal(833.33m, ask);
    }

    // The round trip that makes the number trustworthy: bid exactly the max and the ROI comes out
    // at exactly the target.
    [Fact]
    public void MaxAsk_BiddingTheMaxProducesExactlyTheRequestedRoi()
    {
        const decimal recovery = 2000m;
        var ask = LotAnalyzer.MaxAsk(recovery, buyerPremiumPercent: 15m, salesTaxPercent: 8m,
            freight: 150m, targetRoiPercent: 40m)!.Value;

        var cost = LotAnalyzer.CostOf(ask, 15m, 8m, 150m);
        var roi = (recovery - cost.TotalCost) / cost.TotalCost * 100m;

        Assert.InRange(roi, 39.9m, 40.1m);
    }

    [Fact]
    public void MaxAsk_ReturnsNothingWhenFreightAloneEatsTheRecovery()
    {
        Assert.Null(LotAnalyzer.MaxAsk(netRecovery: 300m, buyerPremiumPercent: 0m, salesTaxPercent: 0m,
            freight: 400m, targetRoiPercent: 0m));
    }

    [Fact]
    public void MaxAsk_ReturnsNothingWhenTheLotRecoversNothing()
    {
        Assert.Null(LotAnalyzer.MaxAsk(netRecovery: -50m, 10m, 8m, 0m, 40m));
    }

    // ── Concentration ────────────────────────────────────────────────────────

    [Fact]
    public void Concentrate_FlagsTheLinesThatCarryEightyPercentOfTheValue()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { Description = "Sony headphones", NetRecovery = 600m },
            new() { Description = "Drill kit", NetRecovery = 250m },
            new() { Description = "Phone cases", NetRecovery = 100m },
            new() { Description = "Cables", NetRecovery = 50m },
        };

        var concentration = LotAnalyzer.Concentrate(lines);

        Assert.Equal(2, concentration.LinesForEightyPercent);
        Assert.Equal(60m, concentration.TopLineSharePercent);
        Assert.True(lines[0].CarriesTheValue);
        Assert.False(lines[3].CarriesTheValue);
    }

    [Fact]
    public void Concentrate_WarnsWhenOneLineIsMoreThanHalfTheLot()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { Description = "MacBook Pro 16", NetRecovery = 1200m },
            new() { Description = "Assorted cables", NetRecovery = 100m },
        };

        var concentration = LotAnalyzer.Concentrate(lines);

        Assert.NotNull(concentration.Warning);
        Assert.Contains("MacBook Pro 16", concentration.Warning);
    }

    [Fact]
    public void Concentrate_SaysNothingAboutAnEvenlySpreadLot()
    {
        var lines = Enumerable.Range(0, 10).Select(i => new LotLineAnalysis { Description = $"item {i}", NetRecovery = 100m }).ToList();

        var concentration = LotAnalyzer.Concentrate(lines);

        Assert.Null(concentration.Warning);
        Assert.Equal(8, concentration.LinesForEightyPercent);
    }

    // ── Coverage ─────────────────────────────────────────────────────────────

    [Fact]
    public void Coverage_WeightsByTheManifestsOwnClaimedValueWhenItStatesOne()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { RetailTotal = 900m, Quantity = 1, Status = "priced" },
            new() { RetailTotal = 100m, Quantity = 50, Status = "no_data" },
        };

        // 50 unpriced trinkets do not make a lot 98% unknown when the money is in the one item.
        Assert.Equal(90m, LotAnalyzer.Coverage(lines));
    }

    [Fact]
    public void Coverage_FallsBackToUnitCountWhenNoRetailIsStated()
    {
        var lines = new List<LotLineAnalysis>
        {
            new() { Quantity = 3, Status = "priced" },
            new() { Quantity = 1, Status = "no_data" },
        };

        Assert.Equal(75m, LotAnalyzer.Coverage(lines));
    }

    [Fact]
    public void Coverage_CountsThinlyPricedLinesAsPriced()
    {
        var lines = new List<LotLineAnalysis> { new() { Quantity = 1, Status = "thin" } };

        Assert.Equal(100m, LotAnalyzer.Coverage(lines));
    }

    // ── The verdict ──────────────────────────────────────────────────────────

    private static LotTotals Totals(decimal cost, decimal recovery)
    {
        var totals = LotAnalyzer.CostOf(cost, 0m, 0m, 0m);
        totals.NetRecovery = recovery;
        totals.NetProfit = recovery - totals.TotalCost;
        totals.RoiPercent = totals.TotalCost > 0m ? Math.Round(totals.NetProfit / totals.TotalCost * 100m, 1) : null;
        return totals;
    }

    [Fact]
    public void Judge_SaysBuyWhenTheMoneyAndTheEvidenceAreBothThere()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(1000m, 2000m), coveragePercent: 85m, linesPriced: 12,
            breakEvenAsk: 2000m, maxBid: 1428m, targetRoiPercent: 40m);

        Assert.Equal("buy", verdict);
        Assert.Contains("$1,428", note);
    }

    // The most useful answer a lot tool can give: not "no", but "yes, at this price".
    [Fact]
    public void Judge_TurnsAnOverpricedLotIntoAPriceToBidInstead()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(2500m, 2000m), coveragePercent: 85m, linesPriced: 12,
            breakEvenAsk: 2000m, maxBid: 1428m, targetRoiPercent: 40m);

        Assert.Equal("buy_below", verdict);
        Assert.Contains("$2,000", note);
        Assert.Contains("$1,428", note);
    }

    // The ceiling is built only from lines that could be priced, and an unpriced line can only add
    // value — so at low coverage it is a floor, and saying otherwise talks someone out of a lot
    // whose unpriced half was the good half.
    [Fact]
    public void Judge_CallsAPartlyPricedCeilingAFloorRatherThanACap()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(2500m, 2000m), coveragePercent: 52m, linesPriced: 6,
            breakEvenAsk: 2000m, maxBid: 1428m, targetRoiPercent: 40m);

        Assert.Equal("buy_below", verdict);
        Assert.Contains("treat that as a floor", note);
    }

    [Fact]
    public void Judge_DoesNotAddTheFloorCaveatWhenCoverageIsGood()
    {
        var (_, note) = LotAnalyzer.Judge(Totals(2500m, 2000m), coveragePercent: 88m, linesPriced: 14,
            breakEvenAsk: 2000m, maxBid: 1428m, targetRoiPercent: 40m);

        Assert.DoesNotContain("treat that as a floor", note);
    }

    [Fact]
    public void Judge_CallsALotDeadWhenFeesAndShippingExceedTheResale()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(500m, -80m), coveragePercent: 90m, linesPriced: 10,
            breakEvenAsk: null, maxBid: null, targetRoiPercent: 40m);

        Assert.Equal("dead", verdict);
        Assert.Contains("Even free", note);
    }

    // A verdict on a manifest that could only be half priced is a coin flip with a dollar sign
    // in front of it.
    [Fact]
    public void Judge_RefusesToCallALotItCouldBarelyPrice()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(1000m, 5000m), coveragePercent: 22m, linesPriced: 2,
            breakEvenAsk: 5000m, maxBid: 3500m, targetRoiPercent: 40m);

        Assert.Equal("no_data", verdict);
        Assert.Contains("22", note);
    }

    [Fact]
    public void Judge_DowngradesAProfitableLotWithPartialCoverageToALead()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(1000m, 3000m), coveragePercent: 50m, linesPriced: 6,
            breakEvenAsk: 3000m, maxBid: 2142m, targetRoiPercent: 40m);

        Assert.Equal("thin", verdict);
        Assert.Contains("lead, not a decision", note);
    }

    [Fact]
    public void Judge_CallsAProfitableButUnderTargetLotThinAndNamesThePriceThatWouldWork()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(1000m, 1200m), coveragePercent: 90m, linesPriced: 12,
            breakEvenAsk: 1200m, maxBid: 857m, targetRoiPercent: 40m);

        Assert.Equal("thin", verdict);
        Assert.Contains("$857", note);
    }

    [Fact]
    public void Judge_AsksForTheAskPriceRatherThanPretendingToHaveAVerdict()
    {
        var totals = Totals(0m, 1500m);
        totals.GrossResale = 2000m;

        var (verdict, note) = LotAnalyzer.Judge(totals, coveragePercent: 90m, linesPriced: 8,
            breakEvenAsk: 1500m, maxBid: 1071m, targetRoiPercent: 40m);

        Assert.Equal("no_ask", verdict);
        Assert.Contains("Enter what the lot costs", note);
    }

    [Fact]
    public void Judge_SaysNoAnswerRatherThanSkipWhenNothingCouldBePriced()
    {
        var (verdict, note) = LotAnalyzer.Judge(Totals(1000m, 0m), coveragePercent: 0m, linesPriced: 0,
            breakEvenAsk: null, maxBid: null, targetRoiPercent: 40m);

        Assert.Equal("no_data", verdict);
        Assert.Contains("not a skip", note);
    }

    // ── Grade assumptions ────────────────────────────────────────────────────

    [Fact]
    public void Assumptions_FallsBackToTestedCustomerReturnsForAnUnknownGrade()
    {
        var grade = LotAnalyzer.Assumptions("something_else", null, null);

        Assert.Equal("customer_returns", grade.Id);
    }

    [Fact]
    public void Assumptions_HonoursTheSellersOwnOverridesAndSaysSo()
    {
        var grade = LotAnalyzer.Assumptions("customer_returns", sellableOverride: 92m, priceFactorOverride: 95m);

        Assert.Equal(92m, grade.SellableRatePercent);
        Assert.Equal(95m, grade.PriceFactorPercent);
        Assert.Contains("Adjusted from the default", grade.Note);
    }

    [Fact]
    public void Assumptions_ClampsNonsenseOverrides()
    {
        var grade = LotAnalyzer.Assumptions("new", sellableOverride: 400m, priceFactorOverride: -20m);

        Assert.Equal(100m, grade.SellableRatePercent);
        Assert.Equal(0m, grade.PriceFactorPercent);
    }

    [Fact]
    public void Grades_RunFromBestToWorstRecovery()
    {
        var rates = LotAnalyzer.Grades.Select(g => g.SellableRatePercent).ToList();

        Assert.Equal(rates.OrderByDescending(r => r), rates);
        Assert.All(LotAnalyzer.Grades, g => Assert.False(string.IsNullOrWhiteSpace(g.Note)));
    }

    // ── Ranking ──────────────────────────────────────────────────────────────

    // Silently dropping a line the app refused to value is how a buyer misses the one item that
    // needed their own eyes on it.
    [Fact]
    public void Rank_PutsTheMoneyFirstButKeepsUnpricedLinesOnTheTable()
    {
        var rows = new List<LotLineAnalysis>
        {
            new() { Description = "unpriced", Status = "no_data", RetailTotal = 900m },
            new() { Description = "small", NetRecovery = 50m },
            new() { Description = "big", NetRecovery = 500m },
        };

        var ranked = LotAnalyzer.Rank(rows);

        Assert.Equal("big", ranked[0].Description);
        Assert.Equal("small", ranked[1].Description);
        Assert.Equal("unpriced", ranked[2].Description);
    }
}
