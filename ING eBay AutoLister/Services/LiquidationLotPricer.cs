using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The money on a liquidation-auction row — and the place the sourcing scan and the Liquidation Lot
/// Analyzer become one feature instead of two.
///
/// <para>Everything a pallet needs was already written, for the analyzer that prices a pasted
/// manifest: <see cref="LotAnalyzer.Grades"/> knows what share of a returns lot is dead stock,
/// <see cref="LotAnalyzer.CostOf"/> knows that a buyer's premium is taxed along with the hammer,
/// <see cref="LotAnalyzer.MaxAsk"/> solves the highest bid that still clears a target ROI, and
/// <see cref="LotAnalyzer.RetailSanityCheck"/> knows that a comp several times an item's own stated
/// retail means the matcher found something else. This class does not re-derive any of it. It calls
/// them — so a lot found by a scan and the same lot pasted into the analyzer cannot disagree about
/// what it is worth.</para>
///
/// <para><b>What it adds is the three ways an auction differs from a shelf</b>, each of which
/// silently overstates profit if it is left out:</para>
/// <list type="number">
///   <item><b>The price is a bid.</b> It is the floor, not the cost. So the headline output here is
///   not "the profit at this price" but <see cref="LiquidationLotEconomics.MaxBidForTargetRoi"/> —
///   the most you can bid and still make the money you came for.</item>
///   <item><b>The premium and the tax.</b> A 15% premium plus 8% tax turns a $100 bid into $124.
///   Charged through <see cref="LotAnalyzer.CostOf"/>, which already bills tax on hammer + premium
///   the way an auction house does.</item>
///   <item><b>It may be several things.</b> A lot of 8 priced against one comp is wrong by 8x in
///   the direction that invents a goldmine, so units are multiplied — but only through a grade's
///   recovery rate, and only when the listing stated a count.</item>
/// </list>
///
/// <para>Pure except for the shared <see cref="ProfitCalculator"/>, so a unit out of an auction lot
/// is costed by exactly the same rules as a dropship, a Craigslist flip or a clearance buy.</para>
/// </summary>
public sealed class LiquidationLotPricer(ProfitCalculator profitCalc)
{
    /// <summary>
    /// The ROI the max-bid figure is solved for — the same default the Liquidation Lot Analyzer
    /// offers on a pasted manifest (<see cref="LotAnalysisRequest.TargetRoiPercent"/>). One idea of
    /// what a lot has to return, not a friendlier second one for the scan that found it.
    /// </summary>
    public const decimal TargetRoiPercent = 40m;

    /// <summary>
    /// Charged when an auction publishes no premium rate and prints no percentage in its terms.
    ///
    /// A published zero and an unpublished premium are indistinguishable in this data, and of the
    /// two possible mistakes only one costs money: assuming a premium that isn't charged makes the
    /// app pass on a deal, while assuming none where 15% is charged makes it buy a loser. 15% is
    /// the modal published rate across the auctions seen live. Rows carrying it say so.
    /// </summary>
    public const decimal AssumedBuyerPremiumPercent = 15m;

    /// <summary>
    /// Past this many units the evidence bar stops rising. A hundred sold comps is not available
    /// for most products, and demanding it would refuse every large lot rather than judge it.
    /// </summary>
    public const int MaxCompsRequiredForLot = 15;

    /// <summary>
    /// How much sold history a lot of <paramref name="units"/> has to have behind it before it can
    /// be badged a goldmine.
    ///
    /// <para>A single item's profit is one comp's worth of guess. A lot's profit is that same guess
    /// multiplied by the unit count — a 20% error on one $60 item is $12, and on forty of them it is
    /// $480 — so the bar rises with the thing that scales the risk. The rule is simply: <b>don't
    /// claim to know the market for N units from fewer than N observed sales</b>, floored at the
    /// board's ordinary goldmine bar and capped where the demand would stop being meetable.</para>
    ///
    /// <para>Deliberately not just <see cref="LotAnalyzer.GoodEvidenceComps"/>: that is 5, which is
    /// exactly <see cref="LocalArbitrageAnalyzer.GoldmineMinComps"/>, so a bar set there would be no
    /// bar at all.</para>
    /// </summary>
    public static int RequiredCompsForLot(int units) =>
        Math.Clamp(units, LocalArbitrageAnalyzer.GoldmineMinComps, MaxCompsRequiredForLot);

    /// <summary>An auction closing inside this is one to decide on today rather than bookmark.</summary>
    public const int ClosingSoonHours = 48;

    /// <summary>
    /// What one liquidation row is actually worth, before the verdict is written.
    /// </summary>
    /// <param name="Economics">The row's auction and lot money, for rendering.</param>
    /// <param name="TotalCost">Bid + premium + tax — what leaves the wallet if the bid wins.</param>
    /// <param name="NetRecovery">Everything the units return after eBay's fees and shipping, before the bid.</param>
    /// <param name="NetProfit">Net recovery minus the all-in cost.</param>
    /// <param name="Fees">eBay's cut across every unit expected to sell.</param>
    /// <param name="ShipCost">Shipping and handling across every unit expected to sell.</param>
    /// <param name="RoiPercent">Measured against the all-in cost, never against the bare bid.</param>
    /// <param name="MaxBid">The highest bid that merely breaks even.</param>
    public readonly record struct LiquidationQuote(
        LiquidationLotEconomics Economics, decimal TotalCost, decimal NetRecovery, decimal NetProfit,
        decimal Fees, decimal ShipCost, decimal? RoiPercent, decimal? MarginPercent, decimal? MaxBid);

    /// <summary>
    /// Prices one liquidation row. Returns a quote with <see cref="LiquidationLotEconomics.UnpriceableReason"/>
    /// set and no money on it when the row cannot honestly be priced — an assorted lot, bulk stock
    /// with no stated count, a for-parts unit, or a comp that fails the retail cross-check.
    /// </summary>
    public LiquidationQuote Price(
        LiquidationLotDetails details, decimal bid, ResalePricing? resale, FeeProfile fees, decimal salesTaxPercent)
    {
        var taxPercent = RetailBuyCosts.Sanitize(salesTaxPercent);
        var cost = LotAnalyzer.CostOf(bid, details.BuyerPremiumPercent, taxPercent, freight: 0m);

        // A lot's grade decides how many of its units survive to be listed and what they fetch. A
        // single item gets no grade at all: it is priced exactly as a Craigslist find of the same
        // thing is, because applying a haircut to one and not the other would make two rows in the
        // same ranking incomparable.
        var grade = details.IsLot
            ? LotAnalyzer.Assumptions(details.GradeId, null, null)
            : SingleItem;

        var economics = new LiquidationLotEconomics
        {
            AuctionHouse = details.AuctionHouse,
            EventName = details.EventName,
            IsLiquidationEvent = details.IsLiquidationEvent,
            EventUrl = details.EventUrl,
            BidCount = details.BidCount,
            IsStartingBid = details.IsStartingBid,
            ClosesUtc = details.ClosesUtc,
            TimeLeft = details.TimeLeft,

            BuyerPremiumPercent = details.BuyerPremiumPercent,
            BuyerPremium = cost.BuyerPremium,
            BuyerPremiumAssumed = details.BuyerPremiumAssumed,
            SalesTaxPercent = taxPercent,
            SalesTax = cost.SalesTax,

            IsLot = details.IsLot,
            Units = Math.Max(1, details.Units),
            GradeId = details.IsLot ? grade.Id : "",
            GradeLabel = details.IsLot ? grade.Label : "",
            GradeNote = details.IsLot ? grade.Note : "",
            ClaimedRetailTotal = details.ClaimedRetailTotal,

            TargetRoiPercent = TargetRoiPercent,
            UnpriceableReason = details.UnpriceableReason,
        };

        // Refused at the parser: assorted contents, bulk with no count, for-parts. The cost side is
        // still filled in, because "this bid will cost you $124 all in" is true and useful even when
        // the resale side can't be answered.
        if (details.UnpriceableReason is not null || resale is null || !resale.HasPrice)
            return Unpriced(economics, cost.TotalCost);

        var comp = resale.QuickSale is > 0 ? resale.QuickSale!.Value
            : resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value
            : resale.Median!.Value;

        // The manifest analyzer's own cross-check, on the retail value the listing claimed for
        // itself. A comp several times the stated retail, or a small fraction of it, means the
        // matcher found an accessory or an unrelated product — and on a lot that error is
        // multiplied by the unit count before it reaches the seller.
        var claimedUnitRetail = details.ClaimedRetailTotal is > 0m && economics.Units > 0
            ? details.ClaimedRetailTotal / economics.Units
            : null;
        var mismatch = LotAnalyzer.RetailSanityCheck(
            Math.Round(comp, 2), claimedUnitRetail, resale.SoldCompCount + resale.TerapeakCompCount);
        if (mismatch is not null)
        {
            economics.UnpriceableReason = mismatch;
            return Unpriced(economics, cost.TotalCost);
        }

        var unitResale = Math.Round(comp * grade.PriceFactorPercent / 100m, 2);
        var sellableUnits = Math.Round(economics.Units * grade.SellableRatePercent / 100m, 2);

        if (unitResale <= 0m || sellableUnits <= 0m)
        {
            economics.UnpriceableReason = "Priced at zero once this grade's recovery assumptions are applied.";
            return Unpriced(economics, cost.TotalCost);
        }

        // Shipping is booked on both sides, exactly as everywhere else in this app: buyers paid it
        // (revenue, and eBay charges its final value fee on it) and it costs the seller the same to
        // ship. Cost is zero here on purpose — the bid is not charged per unit, it is charged once
        // to the whole lot below, which is the only way a per-unit figure and a lot figure can agree.
        var perUnit = profitCalc.Calculate(
            supplierUnitCost: 0m, quantity: 1, expectedSalePrice: unitResale, quickSalePrice: unitResale,
            buyerPaidShipping: resale.AvgCompShipping, fees: fees,
            actualShippingCostOverride: resale.AvgCompShipping > 0 ? resale.AvgCompShipping : null);

        var grossResale = Math.Round((unitResale + resale.AvgCompShipping) * sellableUnits, 2);
        var netRecovery = Math.Round(perUnit.NetProfitPerUnit * sellableUnits, 2);
        var netProfit = Math.Round(netRecovery - cost.TotalCost, 2);

        economics.SellableUnits = sellableUnits;
        economics.UnitResale = unitResale;
        economics.UnitNetRecovery = Math.Round(perUnit.NetProfitPerUnit, 2);
        economics.CostPerSellableUnit = Math.Round(cost.TotalCost / sellableUnits, 2);
        economics.ResalePercentOfRetail = details.ClaimedRetailTotal is > 0m
            ? Math.Round(grossResale / details.ClaimedRetailTotal.Value * 100m, 1)
            : null;
        // The number to write on your hand: premium and tax already taken out of it, so it is a
        // bid, not a budget. Exact arithmetic — see LotAnalyzer.MaxAsk.
        economics.MaxBidForTargetRoi = LotAnalyzer.MaxAsk(
            netRecovery, details.BuyerPremiumPercent, taxPercent, freight: 0m, TargetRoiPercent);

        return new LiquidationQuote(
            economics,
            TotalCost: cost.TotalCost,
            NetRecovery: netRecovery,
            NetProfit: netProfit,
            Fees: Math.Round(perUnit.MarketplaceFeeTotal * sellableUnits, 2),
            ShipCost: Math.Round(perUnit.FulfilmentCostTotal * sellableUnits, 2),
            RoiPercent: cost.TotalCost > 0m ? Math.Round(netProfit / cost.TotalCost * 100m, 1) : null,
            MarginPercent: grossResale > 0m ? Math.Round(netProfit / grossResale * 100m, 1) : null,
            // Break-even: the highest bid at which the lot returns exactly what it cost.
            MaxBid: LotAnalyzer.MaxAsk(netRecovery, details.BuyerPremiumPercent, taxPercent, freight: 0m, 0m));
    }

    /// <summary>
    /// The verdict wording an auction needs and a shelf price doesn't: every figure is measured at a
    /// bid that has not finished moving, so the row says what the bid is now and where to stop.
    /// </summary>
    public static string BidNote(LiquidationLotEconomics economics, decimal bid)
    {
        var at = $"At {Money(bid)}{(economics.IsStartingBid ? " opening" : "")}";

        if (economics.MaxBidForTargetRoi is not > 0m)
            return $"{at} — the bid is already past what this returns.";

        var target = economics.MaxBidForTargetRoi.Value;

        // Still profitable, but the bidding has already gone past the price that earns the return
        // the seller came for. "Bid up to $29.60" on a lot standing at $37 reads as an instruction
        // to bid when the honest answer is to stop.
        return target < bid
            ? $"{at} — already past the {Money(target)} that clears {TargetRoiPercent:0}%. Let it go."
            : $"{at} — bid up to {Money(target)} and you still clear {TargetRoiPercent:0}%.";
    }

    /// <summary>
    /// Whether a row's evidence is thin enough that a multi-unit lot must not be badged a goldmine.
    /// A single item keeps the board's ordinary bar; a lot has to clear one that rises with its
    /// unit count — see <see cref="RequiredCompsForLot"/>.
    /// </summary>
    public static bool EvidenceTooThinForLot(LiquidationLotEconomics economics, int compCount) =>
        economics.IsLot && compCount < RequiredCompsForLot(economics.Units);

    public static bool ClosingSoon(LiquidationLotEconomics economics, DateTime nowUtc) =>
        economics.ClosesUtc is { } closes && closes > nowUtc && (closes - nowUtc).TotalHours <= ClosingSoonHours;

    // A single auction item is not graded: 100% of it is sellable and it fetches the comp price,
    // which is exactly how every other single item in this app is treated.
    private static readonly LotGradeAssumption SingleItem = new()
    {
        Id = "", Label = "", SellableRatePercent = 100m, PriceFactorPercent = 100m, Note = "",
    };

    private static LiquidationQuote Unpriced(LiquidationLotEconomics economics, decimal totalCost) =>
        new(economics, totalCost, 0m, 0m, 0m, 0m, null, null, null);

    private static string Money(decimal value) =>
        value < 0m ? $"-${Math.Abs(value):N2}" : $"${value:N2}";
}
