using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How many of the sold comps behind this card would actually have covered what winning it costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every number on the live card is a middle. The resale price is
/// the expected sale, the spread is the middle half, the ceiling is the bid that keeps the middle
/// clearing a target return. A middle is the right answer to "what are these worth" and a
/// dangerous answer to "should I bid on this one", because half the sales came in under it — and
/// the seller is not buying a distribution, they are buying one object, once, in the next eleven
/// seconds. It either resells above what winning cost or it does not.
/// </para>
/// <para>
/// <b>It needs no lookup.</b> The rows are already in hand: the same sold history the price came
/// out of, held on <see cref="LiveBidBoard"/> for as long as the lot is on screen. So this
/// re-answers on a held-comps re-price exactly as it does on a fresh one, in the milliseconds a
/// climbing bid leaves — and it is the one figure on the card that MOVES with the bidding. The
/// ceiling is a line the bid crosses once; this is a count that falls every time somebody else
/// raises, and watching <c>9 of 12</c> become <c>4 of 12</c> is watching the arbitrage close in the
/// unit that decides it.
/// </para>
/// <para>
/// <b>It borrows both of its numbers and invents neither.</b> The bar each past sale is measured
/// against is <see cref="ProfitBreakdown.BreakEvenSalePrice"/> — the app's own "what does this have
/// to sell for", solved by <see cref="ProfitCalculator"/> from the landed cost the card already
/// computed, with the same fees, packing, labour and postage every other screen charges. And each
/// past sale is re-priced by the same stacked cut the ceiling was built with
/// (<see cref="LiveTrend"/> × <see cref="LiveCondition"/> × <see cref="LiveHoldCost"/>), taken from
/// the prices themselves rather than re-derived, so the count and the ceiling can never be arguing
/// about different items.
/// </para>
/// <para>
/// <b>It prices nothing and cuts nothing.</b> No ceiling, no resale figure, no median, no
/// sell-through and no call moves because of anything here — a card with long odds is a card whose
/// ceiling is correct and whose spread is wide, and shading the ceiling for it would be charging
/// twice for the scatter the middle-half warning already reports. What it spends is the seller's
/// attention, and only when most of the evidence is under the line.
/// </para>
/// </remarks>
public static class LiveOdds
{
    /// <summary>
    /// Sales below which a count of them is not a rate. Deliberately above
    /// <see cref="AuctionSniperAnalyzer.MinCompsToBid"/>: three comps are enough to price an item,
    /// and four are not enough for a percentage — one row moves it twenty-five points. The count
    /// itself is still shown at any size, because "two of three" is a true sentence; what is
    /// withheld is the claim that it is odds.
    /// </summary>
    public const int MinSalesToRate = 5;

    /// <summary>At or above this share covered, the evidence is behind the bid.</summary>
    public const int StrongPercent = 70;

    /// <summary>Below this share covered, most of the evidence is under the line and the card says
    /// so on its warning list.</summary>
    public const int EvenPercent = 40;

    /// <summary>
    /// What one unit has to sell for so that winning at this landed cost breaks even.
    /// </summary>
    /// <remarks>
    /// One line, and it is here rather than inline in the advisor so that the file that counts the
    /// comps against a bar is the file that says where the bar came from. It is
    /// <see cref="ProfitCalculator"/>'s own answer — the same fees, packaging, labour, reserves and
    /// postage the ceiling was built from — and not an inversion of it written a second time. The
    /// sale price handed in does not affect the result (the break-even is solved algebraically, not
    /// searched) and is passed for the sake of a calculator call that describes the real listing.
    /// </remarks>
    public static decimal NeedPerUnit(
        ProfitCalculator calc, decimal landedPerUnit, ResalePricing resale, FeeProfile fees)
    {
        var expected = resale.ExpectedSale ?? resale.Median ?? 0m;
        var shipping = Math.Max(0m, resale.AvgCompShipping);
        return calc.Calculate(
            supplierUnitCost: landedPerUnit, quantity: 1, expectedSalePrice: expected,
            quickSalePrice: resale.QuickSale ?? expected, buyerPaidShipping: shipping, fees: fees,
            actualShippingCostOverride: shipping > 0m ? shipping : null).BreakEvenSalePrice;
    }

    /// <summary>
    /// What every past sale has to be multiplied by to become "what yours would fetch".
    /// </summary>
    /// <remarks>
    /// Read off the two prices rather than re-multiplying the three cuts. The ceiling was built
    /// from <paramref name="after"/>; a ratio rebuilt out of <c>LiveTrend.ResaleMultiplier</c> and
    /// its two siblings would be a second opinion about the same cut, and the first time one of
    /// those reads changed its guards the count on this strip would quietly stop matching the
    /// number above it. Never above 1: all three cuts only ever cut.
    /// </remarks>
    public static decimal RepriceRatio(ResalePricing before, ResalePricing after)
    {
        var from = before.ExpectedSale ?? before.Median ?? 0m;
        var to = after.ExpectedSale ?? after.Median ?? 0m;
        if (from <= 0m || to <= 0m) return 1m;
        return Math.Min(1m, to / from);
    }

    /// <summary>
    /// Counts the sold rows that cover the win.
    /// </summary>
    /// <param name="comps">
    /// Every sold row the price came out of (<see cref="MarketAnalysisResult.AllSoldComparables"/>),
    /// not the five the comp table shows. The question is what the whole distribution did, and five
    /// rows chosen by match score are not a distribution.
    /// </param>
    /// <param name="needPerUnit">The sale price one unit has to reach — <see cref="NeedPerUnit"/>.</param>
    /// <param name="repriceRatio">The stacked cut — <see cref="RepriceRatio"/>.</param>
    /// <param name="atBid">The bid being counted for: the one on screen, or the ceiling when nobody
    /// has bid yet.</param>
    /// <param name="atCeiling">True when <paramref name="atBid"/> is the card's ceiling rather than
    /// a live bid. Changes only the wording, and it has to: "if you win at $46" and "if you win at
    /// your $46 ceiling" are different promises.</param>
    /// <param name="landedPerUnit">What winning at that bid costs per unit, all in.</param>
    /// <param name="lotUnits">Units in the lot, so the sentence can say which scale it is on.</param>
    public static LiveOddsRead Read(
        IReadOnlyList<MarketplaceComparableResult>? comps, decimal needPerUnit, decimal repriceRatio,
        decimal atBid, bool atCeiling, decimal landedPerUnit, int lotUnits)
    {
        var ratio = repriceRatio is > 0m and <= 1m ? repriceRatio : 1m;
        var read = new LiveOddsRead
        {
            AtBid = Math.Round(Math.Max(0m, atBid), 2),
            AtCeiling = atCeiling,
            LandedPerUnit = Math.Round(Math.Max(0m, landedPerUnit), 2),
            LotUnits = Math.Max(1, lotUnits),
            NeedPerUnit = needPerUnit is > 0m and < 1_000_000m ? Math.Round(needPerUnit, 2) : 0m,
            RepriceRatio = ratio,
            CutPercent = (int)Math.Round((1m - ratio) * 100m, MidpointRounding.AwayFromZero),
        };

        // Nothing to measure against. The break-even comes back as decimal.MaxValue when the fee
        // profile eats the whole sale — a real state, already reported by the card's own DON'T BID —
        // and as zero or less when there is no price at all. Either way a count against it would be
        // a count against a number nobody can act on.
        if (read.NeedPerUnit <= 0m) return read;

        // Per unit, because the sold rows are per unit and this card's ceiling may be a lot's. The
        // rule for a comp that was itself a multi-pack is MarketPriceEstimator's own — one place in
        // the app decides what one of a lot sold for.
        var prices = (comps ?? [])
            .Select(c => MarketPriceEstimator.UnitSoldPrice(c))
            .Where(p => p > 0m)
            .Select(p => Math.Round(p * ratio, 2))
            .OrderBy(p => p)
            .ToList();

        if (prices.Count == 0) return read;

        read.Readable = true;
        read.Total = prices.Count;
        read.Covered = prices.Count(p => p >= read.NeedPerUnit);
        read.Percent = (int)Math.Round(read.Covered * 100m / prices.Count, MidpointRounding.AwayFromZero);
        read.LowSale = prices[0];
        read.HighSale = prices[^1];
        read.TypicalSale = Median(prices);
        read.Stated = prices.Count >= MinSalesToRate;

        read.Verdict = !read.Stated ? LiveOddsVerdicts.Thin
            : read.Percent >= StrongPercent ? LiveOddsVerdicts.Strong
            : read.Percent >= EvenPercent ? LiveOddsVerdicts.Even
            : LiveOddsVerdicts.Long;

        Say(read);
        return read;
    }

    /// <summary>
    /// The three sentences. Written here, next to the count, for the reason every other block on
    /// this card writes its own: a sentence assembled in the browser out of <c>covered</c> and
    /// <c>total</c> is a second opinion about evidence, and it is the one on screen.
    /// </summary>
    private static void Say(LiveOddsRead read)
    {
        var win = read.AtCeiling
            ? $"a win at your {read.AtBid:C} ceiling"
            : $"a {read.AtBid:C} win";

        read.Headline = read.Verdict switch
        {
            LiveOddsVerdicts.Thin =>
                $"{read.Covered} of {read.Total} past sales cover {win} — too few to be odds",
            LiveOddsVerdicts.Strong =>
                $"{read.Covered} of {read.Total} past sales cover {win}",
            // "Only" on both of the states worth hesitating over, because the count alone reads as
            // neutral at a glance and these two are not.
            _ => $"only {read.Covered} of {read.Total} past sales cover {win}",
        };

        var each = read.LotUnits > 1 ? "Each of them" : "It";
        var repriced = read.Repriced
            ? $", re-priced {read.CutPercent}% to what yours is worth"
            : "";

        read.Note =
            $"{each} has to fetch {read.NeedPerUnit:C} for this to break even after eBay's cut, the packing " +
            $"and posting it on. These {read.Total} sold between {read.LowSale:C} and {read.HighSale:C}" +
            $"{repriced}, typically {read.TypicalSale:C}.";

        // One state warns, and it is the one where the ceiling above is right and still the wrong
        // thing to act on alone: the middle clears the bar and most of the evidence does not. The
        // even state stays off the warning list on purpose — a coin flip that the seller can see on
        // the strip is a decision, not an interruption — and the strong and thin states have nothing
        // to interrupt for.
        if (read.Verdict != LiveOddsVerdicts.Long) return;

        var under = read.Total - read.Covered;
        read.Warning =
            $"Only {read.Covered} of {read.Total} past sales would have covered {win}: one has to fetch " +
            $"{read.NeedPerUnit:C} to break even and {under} of them sold under that. The ceiling is priced " +
            "off the middle of the spread, and this one lands somewhere in it.";
    }

    /// <summary>The middle of an already-sorted list. Averaged across the two middles on an even
    /// count, which is the median every other screen in this app means by the word.</summary>
    private static decimal Median(List<decimal> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : Math.Round((sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m, 2);
}
