using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The WhatsNot tab's arbitrage card: an item is on screen in a live-selling feed, the bidding is
/// running, and this turns real eBay sold history into the one number that decides it — the highest
/// bid worth making — plus the statistics that say whether to believe it.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here prices anything. The resale side arrives already computed by
/// <c>AnalyzeProductAsync</c> — the same eBay sold comps + sell-through pipeline the Opportunity
/// Finder, Local Deals, Roll the Dice and the auction sniper all run — and the money is
/// <see cref="JackpotHunter.BreakEvenBuyPrice"/> and
/// <see cref="AuctionSniperAnalyzer.MaxBidDetail"/>, unchanged. A live card that disagreed with the
/// sniper about the same item at the same price would mean the app has two opinions and the bidder
/// has none.
/// </para>
/// <para>
/// What is genuinely different from every other screen is the shape of the price. It is a
/// <b>bid</b>: it moves while the card is on screen, a buyer's premium sits on top of it, and
/// shipping is part of what winning costs rather than something taken out of the profit afterwards.
/// So the output is a ceiling and a headroom, not a verdict on a number somebody has agreed to.
/// </para>
/// <para>
/// Pure except <see cref="Build"/>, which delegates every dollar to the shared
/// <see cref="ProfitCalculator"/>.
/// </para>
/// </remarks>
public sealed class LiveBidAdvisor(ProfitCalculator profitCalc, JackpotHunter hunter)
{
    /// <summary>The return the ceiling defaults to — the app's own "worth doing" bar, not a
    /// friendlier one for the feature with a countdown on it.</summary>
    public const decimal DefaultTargetRoiPercent = LocalArbitrageAnalyzer.SolidRoiPercent;

    /// <summary>Targets above this are refused. Not a judgement on ambition: a 4,000% target makes
    /// the ceiling a rounding error and the card stops saying anything.</summary>
    public const decimal MaxTargetRoiPercent = 500m;

    /// <summary>A buyer's premium beyond this is a typo — 8% is typical, 40% is not a marketplace.
    /// Clamped rather than rejected so a stray keystroke costs a wrong number, not the answer.</summary>
    public const decimal MaxBuyerFeePercent = 40m;

    /// <summary>Below this many sold comps the ceiling is arithmetic, not evidence. The same bar the
    /// auction sniper refuses to bid under.</summary>
    public const int MinCompsToBid = AuctionSniperAnalyzer.MinCompsToBid;

    /// <summary>A sold comp older than this stops being evidence about today's market. It is not a
    /// hard cut — the comps still price the item — but the card says so out loud.</summary>
    public const int StaleCompDays = 120;

    public static decimal SanitizeTargetRoi(decimal? raw) =>
        raw is not decimal value || value <= 0m ? DefaultTargetRoiPercent : Math.Min(value, MaxTargetRoiPercent);

    public static decimal SanitizeBuyerFee(decimal? raw) =>
        raw is not decimal value || value <= 0m ? 0m : Math.Min(value, MaxBuyerFeePercent);

    /// <summary>What winning at this bid actually costs: the bid, the platform's cut of it, and
    /// getting it delivered.</summary>
    public static decimal LandedCost(decimal bid, decimal buyerFeePercent, decimal shipping) =>
        Math.Round(bid * (1m + Math.Max(0m, buyerFeePercent) / 100m) + Math.Max(0m, shipping), 2);

    /// <summary>The highest bid that breaks even, with the premium and the shipping already taken
    /// out of it — so it is a number to bid to, not a budget to spend.</summary>
    public static decimal BreakEvenBid(decimal breakEvenAllIn, decimal buyerFeePercent, decimal shipping)
    {
        if (breakEvenAllIn <= 0m) return 0m;
        var bid = (breakEvenAllIn - Math.Max(0m, shipping)) / (1m + Math.Max(0m, buyerFeePercent) / 100m);
        // Truncated like the ceiling above it, and for the same reason: a walk-away line rounded up
        // is a walk-away line that quietly permits the bid it exists to refuse.
        return bid <= 0m ? 0m : Math.Floor(bid * 100m) / 100m;
    }

    /// <summary>One item on a live feed, costed and called.</summary>
    /// <param name="analysis">
    /// The market analysis for this title, or null when nothing priced it. Never recomputed here —
    /// this is a translation of figures the pipeline has already produced.
    /// </param>
    /// <param name="own">
    /// The seller's own history with this product, when their book was read. Priced at this
    /// request's terms and attached to the card — never allowed to move the call, which stays the
    /// market's answer. See <see cref="OwnTrackRecord"/>.
    /// </param>
    /// <param name="search">
    /// What the sold search actually asked eBay for. Carried onto the card and never used to price
    /// anything — the seller has to be able to see the question the five statistics answer. Null
    /// falls back to what <see cref="LiveSearchQuery"/> would have asked for, so a card can never
    /// claim a search that nothing would run.
    /// </param>
    /// <param name="tonight">
    /// How many units of this product tonight's buy sheet is already holding
    /// (<see cref="LiveBuySheet.UnitsWonOf"/>). Default is none, so a card built without a sheet is
    /// priced identically and simply counts one fewer thing. It changes no figure here — see
    /// <see cref="LiveStockDepth"/> for why saturation is reported in months rather than in dollars.
    /// </param>
    /// <param name="ship">
    /// What tonight's buy sheet has already committed to THIS show's box
    /// (<see cref="LiveBuySheet.ShippingOnShow"/>). Default is nothing, so a card built without a
    /// sheet is charged full freight — which is exactly what every card was charged before
    /// <see cref="LiveShipShare"/> existed. Unlike everything else attached here it CAN move money,
    /// upward, and only when the show is named, its extra-item rate is stated, and a lot from that
    /// same show is already on the sheet.
    /// </param>
    public LiveBidCard Build(
        string item, MarketAnalysisResult? analysis, LiveBidRequest request, FeeProfile fees,
        ResaleCategory? category = null, DateTime? nowUtc = null, OwnSalesEvidence? own = null,
        LiveSearchTerms? search = null, LiveStockTonight tonight = default, LiveShipTonight ship = default)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var terms = search ?? LiveSearchQuery.Build(item);
        // The lookup title is the QUERY, not the typed name — PricedAs has always meant "what the
        // comp lookup ran against", and on a live show those two stopped being the same thing the
        // moment the auction wording started being taken out of it.
        var resale = analysis is null ? null : ResalePricing.From(analysis, terms.Query);

        // Which way the price has been going. Read off the SAME comps the resale figure above came
        // from — no second lookup, nothing extra to wait for — and recomputed on a re-price rather
        // than carried, so a card priced from held comps says exactly what the fresh one said.
        // The only thing on this card allowed to take money off the ceiling; see LiveTrend for the
        // two guards on that and for why a climb is never allowed to add any.
        var trend = LiveTrend.Read(analysis?.AllSoldComparables, now);

        // What condition this is, against what condition the comps were in. Read off the SAME rows
        // the resale figure came from — the condition eBay recorded on each sale, which nothing on
        // this card has ever looked at. The second thing allowed to take money off the ceiling, and
        // it composes with the trend rather than competing: one is what these fetch lately, the
        // other is what they fetch in this shape. See LiveCondition for why a better condition is
        // never allowed to add any.
        var condition = LiveCondition.Read(item, request.Condition, analysis?.AllSoldComparables);

        // What getting THIS lot delivered actually adds. A live seller posts one box per show, not
        // one per lot, so the fourth win of a night costs the show's extra-item rate and not its
        // first-item rate — and charging the full rate four times has been overstating the landed
        // cost of exactly the lots where the difference is most of the margin. The only read on this
        // card that can raise a ceiling, which is why it is gated on three facts the seller can see;
        // fail any one and this is the typed figure, unchanged. See LiveShipShare.
        var freight = LiveShipShare.Read(
            request.ShowName, Math.Max(0m, request.ShippingCost ?? 0m), request.AdditionalItemShipping, ship);
        var shipping = freight.Marginal;

        var feePercent = SanitizeBuyerFee(request.BuyerFeePercent);
        var target = SanitizeTargetRoi(request.TargetRoiPercent);
        var bid = Math.Max(0m, request.CurrentBid ?? 0m);

        // How many things is this? Sold comps are per unit everywhere in this app, so every figure
        // below is a per-unit figure until this says otherwise — and on a live show it says
        // otherwise often enough to matter. See LiveLotSize for what it refuses to guess.
        var units = LiveLotSize.Read(item, request.Quantity);
        var count = Math.Max(1, units.Count);

        var card = new LiveBidCard
        {
            Units = units,
            Item = item,
            Search = terms,
            Trend = trend,
            Condition = condition,
            Ship = freight,
            PricedAs = resale?.LookupTitle ?? terms.Query,
            CategoryLabel = category?.Label ?? "",
            CurrentBid = bid,
            BidWasKnown = bid > 0m,
            ShippingCost = shipping,
            BuyerFeePercent = feePercent,
            BuyerFee = Math.Round(bid * feePercent / 100m, 2),
            LandedCostNow = LandedCost(bid, feePercent, shipping),
            TargetRoiPercent = target,
            SoldSearchUrl = ResaleValuationLinks.SoldSearchUrl(
                category ?? ResaleCategoryCatalog.Resolve(request.CategoryId),
                resale?.LookupTitle is { Length: > 0 } priced ? priced : item),
        };

        if (analysis is not null) ApplyMarket(card, analysis, now);

        // Nothing priced it. The card says which of the two reasons applies rather than showing a
        // dash — "no sold history matched this title" and "the comps are for something else" send
        // the bidder in completely different directions, and both are actionable in seconds.
        if (resale is null || !resale.HasPrice)
        {
            card.Call = LiveBidCalls.NoData;
            card.CallLabel = "CAN'T PRICE IT";
            // The evidence note is the better sentence whenever there IS evidence to describe —
            // "no comp carries this model number" is a different problem, with a different next
            // move, from "nothing matched at all". Its own wording is about an ask rather than a
            // bid, so the nothing-matched case gets a bid-shaped sentence of its own.
            card.Reason = card.EvidenceTier == LocalArbitrageAnalyzer.EvidenceNone
                // Named, because "this item" and "the words the search actually used" are different
                // things on a live show, and which one found nothing is the whole next move: a
                // seller looking at a query with a word in it they did not mean can fix it in one
                // press, and a seller told "no sold history" cannot do anything at all.
                ? $"No eBay sold history matched “{terms.Query}”, so there is no resale price to bid against."
                : card.EvidenceNote;
            // Attached even here — especially here. A card the market could not price is exactly
            // the card on which "you have sold four of these yourself" is the only evidence there
            // is, and the seller's own ceiling becomes the only one on screen.
            AttachOwnRecord(card, own);
            // And how many of it are already in the house. Read on this path too, with no clearance
            // rate to measure the pile against — "nothing priced this AND you are holding four" is
            // the single most useful thing an unpriceable card can say, and it is exactly the card
            // on which a seller talks themselves into one more cheap one.
            ApplyStock(card, own, tonight, monthlySales: 0m, daysToSellOne: null, activeComps: card.ActiveCompCount);
            card.LotRank = RankLot(card.Call, card.ProfitAtMaxBid);
            card.Say = LiveBidSpeech.Say(card);
            return card;
        }

        // The price the rest of this method spends. Identical to `resale` — the same object, not a
        // copy of it — unless the trend read found a confirmed, material fall that survived both of
        // its guards, in which case the three prices the ceiling is built from are scaled to what
        // these have actually been fetching lately. Everything the comps DESCRIBE stays as it was:
        // the middle-half spread, the comp table, the sell-through and the confidence are records
        // of sales that really happened, and scaling those would be inventing sales nobody made.
        // Two ratios, stacked, in the order they were measured. The trend one says what these have
        // been fetching lately; the condition one says what they fetch in the shape this one is in.
        // Both come off the same rows and both only ever cut, so applying one after the other is
        // the two facts together rather than one opinion overwriting another. When neither fired
        // this is the same OBJECT as `resale`, which is what makes "a card with nothing to report
        // is priced exactly as it was before either of these existed" a property of the code.
        var bidAgainst = LiveCondition.Discount(LiveTrend.Discount(resale, trend), condition);

        // The resale price is the LOT's, because the bid it is measured against is the lot's — one
        // hammer buys all of them. The spread, the median and the quick-sale figure stay per unit:
        // those are descriptions of the sold comps, which are sales of one of the thing, and
        // multiplying a percentile by three would be inventing a lot that nobody sold.
        var perUnitResale = bidAgainst.ExpectedSale ?? bidAgainst.Median;
        units.ResalePerUnit = perUnitResale;
        card.ResalePrice = perUnitResale is decimal each ? Math.Round(each * count, 2) : null;
        card.MedianPrice = bidAgainst.Median;
        card.QuickSalePrice = bidAgainst.QuickSale;

        // The money. Every figure below is one subtraction away from this: net profit falls exactly
        // one dollar for every dollar of landed cost, which is what makes the ceiling arithmetic
        // rather than a rule of thumb.
        //
        // The break-even arrives per unit and is multiplied by the count. The cash floor inside the
        // ceiling below is deliberately NOT multiplied: the per-unit work — the packing, the label,
        // the handling — is already charged inside this break-even by ProfitCalculator, and what
        // the floor is left standing for is the hour of finding it and deciding, which happens once
        // for the whole lot however many things are in it.
        var breakEvenPerUnit = hunter.BreakEvenBuyPrice(bidAgainst, fees);
        var breakEvenAllIn = Math.Round(breakEvenPerUnit * count, 2);
        var (maxBid, boundBy) = AuctionSniperAnalyzer.MaxBidDetail(breakEvenAllIn, shipping, target, feePercent);

        card.BreakEvenBid = BreakEvenBid(breakEvenAllIn, feePercent, shipping);
        card.MaxBid = maxBid;
        card.CeilingBoundBy = boundBy;
        card.CeilingNote = boundBy == AuctionSniperAnalyzer.CeilingByCash
            ? $"Ceiling set by the {LocalArbitrageAnalyzer.SolidProfit:C0} cash floor — a percentage has no size, " +
              "and finding it, listing it and packing it costs the same hour whatever it cost to buy." +
              (units.IsLot ? $" The floor is charged once for the lot, not {count} times: the packing and the " +
                             "label on each of them is already in the break-even above it." : "")
            : $"Ceiling set by your {target:0.#}% target return.";
        card.Headroom = Math.Round(maxBid - bid, 2);
        card.ProfitAtMaxBid = Math.Round(Math.Max(0m, breakEvenAllIn - LandedCost(maxBid, feePercent, shipping)), 2);

        // The same ceiling divided back down, because the number a seller carries between lots is
        // "what is one of these worth to me". Truncated like the ceiling it comes from.
        units.MaxBidPerUnit = count > 1 ? Math.Floor(maxBid / count * 100m) / 100m : maxBid;
        units.ProfitPerUnit = count > 1 ? Math.Round(card.ProfitAtMaxBid / count, 2) : card.ProfitAtMaxBid;

        // The press, as opposed to the price. Everything above compares the bid ON SCREEN against
        // the ceiling, which answers whether the last bid was all right; nobody buys at that price.
        // Pressing bid commits to the next increment, and a lot with a dollar of room above the bid
        // can have no press left that stays inside it. Read here, off the ceiling this method just
        // computed and the same break-even it came from, so the profit at the next bid and the
        // profit at the ceiling are one subtraction apart. See LiveBidIncrement.
        card.NextBid = LiveBidIncrement.Read(card, breakEvenAllIn, request.BidIncrement);

        if (card.BidWasKnown)
        {
            var expected = bidAgainst.ExpectedSale is > 0 ? bidAgainst.ExpectedSale!.Value : bidAgainst.Median!.Value;
            // The landed cost is the lot's — one bid, one premium, one shipment — and it is divided
            // across the units so the calculator prices each unit's own sale. Quantity then puts
            // the lot back together, which is what keeps the ROI on a lot of three identical to the
            // ROI on one of them bought at a third of the price.
            var profit = profitCalc.Calculate(
                supplierUnitCost: Math.Round(card.LandedCostNow / count, 2), quantity: count,
                expectedSalePrice: expected,
                quickSalePrice: bidAgainst.QuickSale ?? expected,
                buyerPaidShipping: bidAgainst.AvgCompShipping, fees: fees,
                actualShippingCostOverride: bidAgainst.AvgCompShipping > 0 ? bidAgainst.AvgCompShipping : null);

            card.ProfitNow = count > 1 ? profit.TotalPotentialProfit : profit.NetProfitPerUnit;
            card.RoiNow = profit.RoiPercent;
            card.MarginNow = profit.MarginPercent;
            card.EstimatedFees = Math.Round(profit.MarketplaceFeeTotal * count, 2);
            card.EstimatedShipCost = Math.Round(profit.FulfilmentCostTotal * count, 2);
        }

        ApplySpeed(card, bidAgainst);

        // What N of them costs in TIME. The only price this screen charges for a multi-unit lot:
        // no haircut is taken off the resale figure, because a "multi-unit discount" is a number
        // nobody measured, while the queue behind the first sale is measurable from the same
        // sell-through data already on the card.
        var (months, daysAll, absorption) = LiveLotSize.Absorption(
            count, bidAgainst.EstimatedMonthlySales, card.DaysToSell);
        units.MonthsToClear = months;
        units.DaysToSellAll = daysAll;
        units.AbsorptionNote = absorption;

        var (call, label, reason) = Judge(card);
        card.Call = call;
        card.CallLabel = label;
        card.Reason = reason;
        card.Warnings.AddRange(Warnings(card, bidAgainst));
        AttachOwnRecord(card, own);
        // How many of these the seller would then own, against how many the market clears. Read
        // after the ceiling rather than into it: this changes no price on the card, because the
        // fourth one still resells for what the comps say — it just sells in April. Both figures it
        // measures the pile against are the card's own, already computed above.
        ApplyStock(card, own, tonight, bidAgainst.EstimatedMonthlySales, card.DaysToSell, card.ActiveCompCount);
        card.LotRank = RankLot(card.Call, card.ProfitAtMaxBid);
        // Last, because it restates what everything above decided. Both exits set it, so no card
        // this method returns can reach a screen without the line that screen reads out loud.
        card.Say = LiveBidSpeech.Say(card);

        return card;
    }

    /// <summary>
    /// The seller's own record with this product, priced at the same shipping, premium and target
    /// the ceiling above it used — and never allowed to change the call.
    /// </summary>
    /// <remarks>
    /// The badge is the market's answer and stays the market's answer. What the seller's own sales
    /// are allowed to do is <b>say something</b>: a second ceiling clearly labelled as theirs, and
    /// the two or three facts that belong on the card's warning list because they change the answer
    /// to "should I bid on this" — most of all, that they already own two of these and neither has
    /// sold. A screen that quietly re-rated the call on the strength of two of the seller's own
    /// sales would be a screen that talks somebody out of a good lot because they once listed one
    /// badly.
    /// </remarks>
    private static void AttachOwnRecord(LiveBidCard card, OwnSalesEvidence? own)
    {
        if (own is null) return;

        // Priced against the PER-UNIT ceiling and the per-unit resale on a multi-unit lot. The
        // seller's own record is a record of selling one of these at a time — one listing, one
        // buyer, one fee — so measuring it against a ceiling for three of them would report their
        // own history as a third of what it is, on the card where it is the strongest evidence
        // there is. The lot warning below says which of the two scales this block is on.
        card.OwnHistory = OwnTrackRecord.Price(
            own, card.ShippingCost, card.BuyerFeePercent, card.TargetRoiPercent,
            card.Units.IsLot ? card.Units.MaxBidPerUnit : card.MaxBid,
            card.Units.IsLot ? card.Units.ResalePerUnit : card.ResalePrice);

        card.Warnings.AddRange(OwnTrackRecord.Warnings(card.OwnHistory));

        // Which scale that block is on. Said here rather than in LotWarnings because it is only
        // true once there is a record to be on a scale at all.
        if (card.Units.IsLot)
        {
            card.Warnings.Add(
                $"Your own record below is per unit — one listing, one buyer, one fee. This lot is " +
                $"{card.Units.Count} of them, so its ceiling is per unit too.");
        }
    }

    /// <summary>
    /// How many of this the seller would own after winning it, and how long that takes to clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately the last thing attached, and deliberately attached to nothing. It moves no
    /// ceiling, no resale price, no break-even and no call — <see cref="LiveTrend"/> and
    /// <see cref="LiveCondition"/> cut prices because they are about what the object fetches, and
    /// this is about what a calendar does to money. A "you already have three" haircut would be a
    /// number nobody measured, taken off the one figure on the card that comes from real sales.
    /// </para>
    /// <para>
    /// The one thing it is allowed to do is <b>say something</b>, and only when the pile crosses a
    /// bar. The seller's own record already warns about units held; what it cannot see, and what
    /// this adds, is the lots won on this tab in the last twenty minutes and the number of months
    /// the whole stack takes to turn over.
    /// </para>
    /// </remarks>
    private static void ApplyStock(
        LiveBidCard card, OwnSalesEvidence? own, LiveStockTonight tonight,
        decimal monthlySales, int? daysToSellOne, int activeComps)
    {
        card.Stock = LiveStockDepth.Read(
            card.Units.Count, own, tonight, monthlySales, daysToSellOne, activeComps);

        if (card.Stock.Warning.Length > 0) card.Warnings.Add(card.Stock.Warning);
    }

    /// <summary>
    /// The gap between one <see cref="RankLot"/> tier and the next. Wider than the largest profit
    /// the ranking will consider, which is what makes the ordering a decision about the CALL first
    /// and the money only within it — no amount of profit lifts a lot the app said stop to above one
    /// it said bid to.
    /// </summary>
    public const decimal LotRankTierStep = 1_000_000m;

    /// <summary>
    /// Where one lot belongs among the others on a show's list. Higher is worth being there for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ranked on <b>what the lot is worth</b> — the profit at its own ceiling — and not on how much
    /// room is left above the current bid. Room shrinks every time somebody else bids and the lot's
    /// worth does not, so a list ordered by room would reshuffle itself every few seconds while the
    /// seller was reading it, and would rank a lot nobody has bid on yet above a better one that the
    /// room happened to be lower on.
    /// </para>
    /// <para>
    /// The call comes first regardless. A stop is a stop: the ordering says which lots to be present
    /// for, and a lot the app has already said not to bid on does not belong above one it would.
    /// A no-data lot sits below even a stop, because a stop at least had sold history behind it.
    /// </para>
    /// <para>
    /// It lives here, next to the ceiling it is made of, rather than in the browser, so that the app
    /// has one opinion about which lot is the one to wait for. A sort key computed in JavaScript is
    /// a second opinion about money that nothing tests.
    /// </para>
    /// </remarks>
    public static decimal RankLot(string? call, decimal profitAtMaxBid) =>
        Tier(call) * LotRankTierStep + Math.Clamp(profitAtMaxBid, 0m, LotRankTierStep - 1m);

    private static int Tier(string? call) => call switch
    {
        LiveBidCalls.Bid => 3,
        LiveBidCalls.Risky => 2,
        LiveBidCalls.Stop => 1,
        _ => 0,
    };

    /// <summary>
    /// How many sold comps an analysis is standing on — the count the card prints and the count
    /// that decides whether a search was worth running at all.
    /// </summary>
    /// <remarks>
    /// Written once and read twice: here, and by the endpoint deciding whether the first search
    /// found enough to price on or whether it has to widen. A second count computed at the decision
    /// point is how a card ends up saying "4 comps" under a badge that was chosen because there
    /// were two.
    /// </remarks>
    public static int CompCountOf(MarketAnalysisResult analysis) =>
        analysis.Sources.PricedOnCompCount > 0
            ? analysis.Sources.PricedOnCompCount + analysis.Sources.TerapeakComparableCount
            : analysis.Sources.LocalComparableCount + analysis.Sources.TerapeakComparableCount;

    // The resale statistics, straight off the analysis. Deliberately a copy and not a second
    // calculation: the spread, the sell-through and the confidence on this card are the same
    // figures the Opportunity Finder shows for the same title, or they are worth nothing.
    private static void ApplyMarket(LiveBidCard card, MarketAnalysisResult analysis, DateTime now)
    {
        var estimate = analysis.PriceEstimate;
        card.PriceLow = estimate.Percentile25;
        card.PriceHigh = estimate.Percentile75;
        card.PriceFloor = estimate.MinimumRealisticPrice;
        card.PriceCeiling = estimate.MaximumRealisticPrice;

        var sellThrough = analysis.SellThrough;
        card.SellThroughRate = sellThrough.SellThroughRate;
        card.SellThroughLabel = sellThrough.Interpretation;
        card.SellThroughScore = sellThrough.SellThroughScore;
        card.SellThroughUnbounded = sellThrough.RateIsUnbounded;
        card.ActiveCompCount = sellThrough.ActiveComparableCount;
        card.EstimatedMonthlySales = sellThrough.EstimatedMonthlySales;
        card.LiquidityLevel = sellThrough.LiquidityLevel;

        card.CompCount = CompCountOf(analysis);
        card.ConfidenceScore = analysis.Confidence.Score;
        card.ConfidenceLevel = analysis.Confidence.Level;
        card.IdentityVerified = analysis.Sources.IdentityVerified;

        var (tier, note) = LocalArbitrageAnalyzer.GradeEvidence(
            analysis.Sources.PricedOnCompCount > 0
                ? analysis.Sources.PricedOnCompCount
                : analysis.Sources.LocalComparableCount,
            analysis.Sources.TerapeakComparableCount, analysis.Sources.IdentityVerified,
            analysis.Confidence.Score);
        card.EvidenceTier = tier;
        card.EvidenceNote = note;

        card.OldestCompUtc = estimate.LocalOldestSoldAtUtc;
        card.NewestCompUtc = estimate.LocalNewestSoldAtUtc;
        card.NewestCompAgeDays = AgeDays(estimate.LocalNewestSoldAtUtc, now);
        card.FreshnessNote = Freshness(card.NewestCompAgeDays, estimate.LocalOldestSoldAtUtc, now, card.CompCount);

        card.Comps = analysis.TopSoldComparables.Select(c => new LiveBidComp
        {
            Title = c.Title,
            SoldPrice = c.SoldPrice,
            Shipping = c.Shipping,
            TotalPrice = c.TotalPrice > 0 ? c.TotalPrice : Math.Round(c.SoldPrice + c.Shipping, 2),
            Condition = c.Condition ?? "",
            SoldDate = c.SoldDate,
            AgeDays = AgeDays(c.SoldDate, now),
            Url = c.ItemUrl ?? "",
        }).ToList();
    }

    private static void ApplySpeed(LiveBidCard card, ResalePricing resale)
    {
        // Costed at the ceiling rather than at the bid on screen. The bid moves and the ceiling
        // doesn't, so "how long is the money tied up" answered against the current bid would change
        // every time somebody else raises — which is not what changed.
        var estimate = DaysToCashEstimator.Estimate(
            resale.EstimatedDaysToSell, resale.EstimatedMonthlySales,
            card.ProfitAtMaxBid,
            card.MaxBid > 0m && card.ProfitAtMaxBid > 0m
                ? Math.Round(card.ProfitAtMaxBid / LandedCost(card.MaxBid, card.BuyerFeePercent, card.ShippingCost) * 100m, 1)
                : card.RoiNow);

        card.DaysToSell = estimate.DaysToSell;
        card.DaysToCash = estimate.DaysToCash;
        card.SpeedLabel = estimate.SpeedLabel;
    }

    /// <summary>
    /// The call, ordered so the reasons a ceiling cannot be trusted are reached before the ceiling
    /// itself: nothing worth bidding at any price, then the bidding having passed it, then thin
    /// evidence, and only then the number.
    /// </summary>
    public static (string Call, string Label, string Reason) Judge(LiveBidCard card)
    {
        if (card.BreakEvenBid <= 0m || card.MaxBid <= 0m)
        {
            return (LiveBidCalls.Stop, "DON'T BID", card.BreakEvenBid <= 0m
                ? "Fees and shipping eat the whole resale price — no bid makes this work."
                : $"It breaks even at {card.BreakEvenBid:C}, but nothing under that clears " +
                  $"{LocalArbitrageAnalyzer.SolidProfit:C0} — not worth the listing and the packing.");
        }

        if (card.BidWasKnown && card.CurrentBid > card.MaxBid)
        {
            return (LiveBidCalls.Stop, "STOP",
                card.CurrentBid > card.BreakEvenBid
                    ? $"The bidding is past {card.BreakEvenBid:C}, where this stops making money at all. Let it go."
                    : $"Past your {card.MaxBid:C} ceiling — there's still {Math.Max(0m, card.BreakEvenBid - card.CurrentBid):C} " +
                      "before it loses money, but not enough left to be worth the work.");
        }

        // On a lot, the ceiling is for all of them and the per-unit figure follows it. Both, always,
        // in that order: the bid on screen is a lot price, so the lot figure is the one being
        // compared against it, and the per-unit figure is the one the seller carries between lots.
        var forAll = card.Units.IsLot
            ? $" for all {card.Units.Count} ({card.Units.MaxBidPerUnit:C} each)"
            : "";

        var ceiling = $"Bid up to {card.MaxBid:C}{forAll}" +
                      (card.BuyerFeePercent > 0m || card.ShippingCost > 0m
                          ? $" ({LandedCost(card.MaxBid, card.BuyerFeePercent, card.ShippingCost):C} landed)"
                          : "");

        if (card.CompCount < MinCompsToBid || card.EvidenceTier != LocalArbitrageAnalyzer.EvidenceConfident)
        {
            return (LiveBidCalls.Risky, $"RISKY — UP TO {Badge(card.MaxBid)}",
                $"{ceiling} for {card.ProfitAtMaxBid:C} — but {Lowercase(card.EvidenceNote)}");
        }

        var headroom = card.BidWasKnown
            ? $" That's {card.Headroom:C} of room from the {card.CurrentBid:C} on screen."
            : "";

        return (LiveBidCalls.Bid, $"BID UP TO {Badge(card.MaxBid)}",
            $"{ceiling} and you clear {card.ProfitAtMaxBid:C} after eBay's cut and shipping it on." + headroom);
    }

    /// <summary>
    /// The ceiling as whole dollars for the badge — rounded DOWN, never to nearest.
    /// </summary>
    /// <remarks>
    /// The badge is the only part of this card a bidder reads at a glance, and <c>C0</c> on $67.68
    /// prints "$68" — a badge instructing a bid 32 cents above the ceiling the rest of the card
    /// spent its arithmetic protecting. The exact figure is a line below, in the reason and in the
    /// ladder; the glance version is allowed to understate and never to overstate.
    /// </remarks>
    public static string Badge(decimal maxBid) => Math.Floor(maxBid).ToString("C0");

    /// <summary>
    /// What the ceiling cannot say. Facts from the evidence, stated plainly — none of them folded
    /// into a score, because a score hides exactly the thing the bidder has seconds to check.
    /// </summary>
    public static List<string> Warnings(LiveBidCard card, ResalePricing resale)
    {
        var warnings = new List<string>();

        if (card.Call == LiveBidCalls.NoData) return warnings;

        // Before anything about the money: what question these numbers are the answer to. A ceiling
        // built on comps for the first three words of the name is a real ceiling for a slightly
        // different thing, and that is the one fact on this card that changes what all the others
        // mean. See LiveSearchQuery.Widen for the trade it is reporting.
        if (LiveSearchQuery.WidenedWarning(card.Search) is { Length: > 0 } widened) warnings.Add(widened);

        // Then which way the price has been going, for the same reason and in the same place: it is
        // not a fact about the money below, it is a fact about what the money below MEANS. A ceiling
        // priced off a median that includes six weeks of higher sales is a real ceiling for a price
        // that has since stopped being paid. See LiveTrend.
        if (card.Trend is { Warning.Length: > 0 } trend) warnings.Add(trend.Warning);

        // And then WHAT was sold at those prices. Same class of fact and the same place for it: a
        // ceiling built on a median of sealed boxes is a real ceiling for a sealed box, and the
        // thing on screen has been out of its box since 2019. This is the only line on the card
        // that can be fixed with one keystroke and re-answered with no eBay read. See LiveCondition.
        if (card.Condition is { Warning.Length: > 0 } condition) warnings.Add(condition.Warning);

        // The lot warnings come first. Everything under them is a number that means something
        // different depending on whether this is one thing or five, and a seller who reads the
        // ceiling and stops reading has to have hit this line before the ceiling made sense.
        warnings.AddRange(LotWarnings(card));

        // Then the one thing on this card that contradicts the room figure printed next to it: the
        // ceiling is above the bid and there is no press that stays under it. Said here as well as
        // on the strip because it is the case where a seller who reads "$1.00 of room" and acts on
        // it spends four dollars they were told they had. See LiveBidIncrement.
        if (card.NextBid is { Warning.Length: > 0 } press) warnings.Add(press.Warning);

        // Then what getting it delivered costs, in the two states worth interrupting for: nothing
        // was entered at all, and a second full charge for a box that is already going out. Both
        // sentences are LiveShipShare's own, so the strip and the warning list cannot describe the
        // same freight differently. It is silent on the ordinary case and on the case it just saved
        // money on — a saving is good news, and good news belongs on the strip, not in a warning.
        if (card.Ship is { Warning.Length: > 0 } freight) warnings.Add(freight.Warning);

        if (card.BuyerFeePercent <= 0m)
        {
            warnings.Add("No buyer's premium entered. Most live-selling platforms add one to the winning " +
                         "bid, and it comes straight off this margin.");
        }

        if (card.NewestCompAgeDays is int age && age > StaleCompDays)
        {
            warnings.Add($"The most recent matching sale is {age} days old — this is a price from " +
                         "then, not a price from now.");
        }

        if (card.SellThroughUnbounded)
        {
            warnings.Add("These sell, but nothing comparable is listed on eBay right now, so there is no " +
                         "sell-through rate to check the demand against.");
        }
        else if (card.SellThroughRate is decimal rate && rate < 15m)
        {
            warnings.Add($"Sell-through is {rate:0.#}% — for every one that sells there are several sitting " +
                         "unsold. Expect to wait, or to cut the price.");
        }

        // A middle half this wide is not a price, it is a range the item lands somewhere in
        // depending on condition, completeness and what the photos hid.
        if (card.PriceLow is > 0m and decimal low && card.PriceHigh is decimal high && high >= low * 2m)
        {
            warnings.Add($"Sold prices are scattered — the middle half runs {low:C} to {high:C}. Condition " +
                         "decides which end you get, and you are looking at it through a camera.");
        }

        if (!string.IsNullOrWhiteSpace(resale.DisagreementMessage))
            warnings.Add(resale.DisagreementMessage!);

        return warnings;
    }

    /// <summary>
    /// What being several things does to the answer. Said on every card that is one, because a
    /// ceiling three times the size of the last one is the single most surprising thing this screen
    /// can put in front of somebody mid-lot.
    /// </summary>
    /// <remarks>
    /// The refusals get a line too — <see cref="LiveLotUnits.CountUnstated"/> is the case where the
    /// screen is knowingly showing the wrong ceiling (one unit's, for a lot of unknown size) and the
    /// seller is the only one who can fix it, which makes it the most actionable warning here.
    /// </remarks>
    public static List<string> LotWarnings(LiveBidCard card)
    {
        var units = card.Units;
        var warnings = new List<string>();

        if (units.Refused.Length > 0) warnings.Add(units.Refused);
        if (units.CountUnstated) warnings.Add(units.UnstatedNote);

        if (!units.IsLot) return warnings;

        warnings.Add(
            $"{units.Note} It assumes you sell all {units.Count} of them — the ceiling is what the lot is " +
            $"worth, and one unsold unit is {units.ProfitPerUnit:C} of this gone.");

        // The one real cost of buying several. Only said when it is long enough to be a decision.
        if (units.MonthsToClear is decimal months && months >= LiveLotSize.SlowClearanceMonths)
            warnings.Add(units.AbsorptionNote);
        else if (units.MonthsToClear is null && units.AbsorptionNote.Length > 0)
            warnings.Add(units.AbsorptionNote);

        return warnings;
    }

    /// <summary>
    /// How old the evidence is, as a sentence. Said on every card, including the good ones: a
    /// bidder who is only told about age when it is bad has no way to know the silence means fresh.
    /// </summary>
    public static string Freshness(int? newestAgeDays, DateTime? oldest, DateTime now, int compCount)
    {
        if (compCount <= 0) return "";

        if (newestAgeDays is not int age)
        {
            return "None of the matching sales carried a date, so how current this price is cannot be checked.";
        }

        var recency = age switch
        {
            <= 0 => "The most recent matching sale was today",
            1 => "The most recent matching sale was yesterday",
            < 60 => $"The most recent matching sale was {age} days ago",
            < 365 => $"The most recent matching sale was {age / 30} month{(age / 30 == 1 ? "" : "s")} ago",
            _ => "The most recent matching sale was over a year ago",
        };

        var span = AgeDays(oldest, now) is int oldestAge && oldestAge > age
            ? $", and these {compCount} sale{(compCount == 1 ? "" : "s")} span {oldestAge - age} days"
            : "";

        return $"{recency}{span}. Sold comps, not live asking prices.";
    }

    private static int? AgeDays(DateTime? at, DateTime now) =>
        at is DateTime value ? (int)Math.Max(0, Math.Floor((now - value).TotalDays)) : null;

    // The evidence note is a sentence of its own; spliced onto the end of another one it has to
    // stop starting with a capital. Only the first letter — "eBay" and model numbers inside it
    // keep their case.
    private static string Lowercase(string sentence) =>
        sentence.Length == 0 ? sentence : char.ToLowerInvariant(sentence[0]) + sentence[1..];
}
