using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Which way this item's sold price has been moving — read off the same comps the ceiling is built
/// from, and the only thing on the live card that is allowed to take money OFF that ceiling.
/// </summary>
/// <remarks>
/// <para>
/// Every number on the arbitrage card is a claim about tonight made out of sales that happened over
/// the last couple of months, and the card has always said <i>how old</i> that evidence is
/// (<see cref="LiveBidAdvisor.Freshness"/>). What it has never said is which way it was <b>going</b>.
/// A median across ninety days is the right price for an item whose price has held and an
/// overstatement for one that has been sliding since — and on a live feed the seller pays the
/// difference in cash, in about eight seconds, with no chance to re-read the comps afterwards.
/// </para>
/// <para>
/// <b>Nothing here measures anything.</b> The measurement is
/// <see cref="PriceTrendAnalyzer.Measure"/>, the same Theil–Sen slope and two-window comparison the
/// Trend Radar board runs, with the same refusals: dated-coverage floors, a spread check that
/// demotes a reading when the cluster looks like it took in a different variant, and a second
/// opinion from the slope when the two window medians disagree with it. This file does three things
/// the radar does not need: it builds the corpus for a <b>single product</b>, it turns the reading
/// into the two sentences a bidder can read mid-lot, and it decides what the reading is allowed to
/// do to the money.
/// </para>
///
/// <para><b>The asymmetry.</b> A confirmed slide cuts the ceiling; a confirmed climb does not raise
/// it. That is not timidity, it is what the two mistakes cost. Refusing to raise a ceiling on a
/// climbing item costs a lot the seller could have won — invisible, and there is another lot in four
/// minutes. Failing to cut a ceiling on a sliding item costs real cash on a purchase that cannot be
/// undone, and the loss only appears weeks later when the thing sells for less than the card
/// promised. The radar is allowed to price a climb because it is shopping with a week to decide;
/// this screen has seconds and one hammer.</para>
///
/// <para><b>What it will not do.</b> Two guards matter more than the arithmetic:</para>
/// <list type="bullet">
/// <item><description>A cut needs <b>two independent readings to agree</b> — the window medians must
/// have fallen materially AND the trend line across every dated sale must not be pointing up. When
/// they disagree the ceiling is left alone and the card says they disagree, because a haircut taken
/// on the strength of where a window boundary happened to fall is a haircut that talks the seller
/// out of a good lot.</description></item>
/// <item><description>When the newest matching sale is older than the window itself, nothing is read
/// at all. From inside one product there is no way to tell an item that stopped selling from a
/// comps database that stopped being updated, and "these have stopped selling" is far too expensive
/// a thing to say about the second one. The Trend Radar can tell the difference because it has a
/// whole scan to compare against; this has one lookup.</description></item>
/// </list>
/// <para>Pure and deterministic — <c>nowUtc</c> is always passed in, so a reading can be reproduced
/// exactly from the rows that produced it. That is also what makes a re-price identical to the
/// fresh card it came from: <see cref="LiveBidBoard"/> holds the comps, and this is recomputed from
/// them rather than carried across.</para>
/// </remarks>
public static class LiveTrend
{
    /// <summary>
    /// The length of each comparison window: the last month against the month before it.
    /// </summary>
    /// <remarks>
    /// Shorter than the radar's 45 days, for two reasons that both come from this screen rather than
    /// from the statistics. A live resale decision is about what the thing fetches <i>now</i>, and
    /// the comp lookup behind this card returns at most twenty rows — two 45-day windows would spread
    /// those twenty across ninety days and leave most items unreadable, while two 30-day windows sit
    /// inside the span a liquid item's comps actually cover.
    /// </remarks>
    public const int WindowDays = 30;

    /// <summary>
    /// A real price move as opposed to the noise a dozen used-item sales make. Deliberately the same
    /// bar in both directions — <see cref="PriceTrendAnalyzer.ClimbingPricePercent"/>, the figure the
    /// radar calls a climb — so the app cannot be quicker to believe a fall than a rise.
    /// </summary>
    public const decimal MaterialMovePercent = PriceTrendAnalyzer.ClimbingPricePercent;

    /// <summary>
    /// The most the ceiling is ever cut, however far the medians fell. Past this the reading has
    /// stopped being a price move and started being a change of what is in the cluster, and a card
    /// that quietly priced a $400 item at $90 would be refusing lots on the strength of one bad
    /// window rather than on evidence.
    /// </summary>
    public const decimal MaxHaircutPercent = 35m;

    /// <summary>
    /// Read the trend for one live lot. Never null and never throws: an unreadable set of comps
    /// comes back as a read that says so, which is what lets the card print this block on every
    /// card rather than only when it has something to say.
    /// </summary>
    public static LiveTrendRead Read(
        IEnumerable<MarketplaceComparableResult>? comps, DateTime nowUtc, int windowDays = WindowDays)
    {
        var rows = comps as IReadOnlyList<MarketplaceComparableResult> ?? comps?.ToList() ?? [];
        windowDays = PriceTrendAnalyzer.ClampWindow(windowDays);

        var corpus = SoloCorpus(rows, nowUtc, windowDays);
        var reading = PriceTrendAnalyzer.Measure(rows, nowUtc, windowDays, corpus);
        return Describe(reading, windowDays);
    }

    /// <summary>
    /// The corpus for a single product's comps, which is the one thing the radar's
    /// <see cref="PriceTrendAnalyzer.BuildCorpus"/> cannot be reused for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The radar's corpus is built from every comp in a whole scan and does two jobs: it says
    /// whether the database is fresh enough to read at all, and it carries the scan-wide change in
    /// volume so one product's velocity can have the database's own drift divided out of it.
    /// </para>
    /// <para>
    /// Handed one product's own comps it would do the second job against itself — the product's
    /// velocity change measured relative to the product's velocity change, which is zero by
    /// construction, so nothing could ever be selling more often than usual. So
    /// <see cref="TrendCorpus.VelocityChangePercent"/> is left <b>null</b> here on purpose:
    /// <see cref="PriceTrendAnalyzer.Detrend"/> returns the raw figure when there is no baseline,
    /// and the card then says "4 → 9 sales" without claiming to know how that compares to the rest
    /// of the market. It doesn't.
    /// </para>
    /// <para>
    /// The freshness job still has to be done, and from one lookup it can only be done one way: if
    /// the newest matching sale is older than the window, refuse. See the class remarks.
    /// </para>
    /// </remarks>
    public static TrendCorpus SoloCorpus(
        IReadOnlyList<MarketplaceComparableResult> comps, DateTime nowUtc, int windowDays)
    {
        windowDays = PriceTrendAnalyzer.ClampWindow(windowDays);
        var corpus = new TrendCorpus { WindowDays = windowDays };

        var ages = new List<int>();
        foreach (var comp in comps)
        {
            corpus.TotalComps++;
            if (comp.SoldDate is not DateTime sold) continue;
            corpus.DatedComps++;
            ages.Add((int)Math.Max(0, (nowUtc - sold).TotalDays));
        }

        corpus.DatedCoveragePercent = corpus.TotalComps == 0
            ? 0m
            : Math.Round(corpus.DatedComps * 100m / corpus.TotalComps, 1);
        corpus.RecentComps = ages.Count(a => a < windowDays);
        corpus.PriorComps = ages.Count(a => a >= windowDays && a < windowDays * 2);
        corpus.NewestCompAgeDays = ages.Count > 0 ? ages.Min() : null;

        if (corpus.TotalComps == 0)
        {
            corpus.Note = "No sold comps came back, so there is nothing to read a trend from.";
            return corpus;
        }

        if (corpus.DatedComps < PriceTrendAnalyzer.MinDatedComps)
        {
            corpus.Note = $"Only {corpus.DatedComps} of {corpus.TotalComps} matching sales carry a date — " +
                          "not enough dated history to say which way the price has been moving.";
            return corpus;
        }

        if (corpus.NewestCompAgeDays is int newest && newest >= windowDays)
        {
            corpus.Note = $"The newest matching sale is {newest} days old, which is further back than the " +
                          $"{windowDays}-day window this reads. From one lookup there is no way to tell an item " +
                          "that stopped selling from a comps database that stopped being updated, so nothing " +
                          "here is read as a trend.";
            return corpus;
        }

        corpus.IsReadable = true;
        return corpus;
    }

    // ── The reading, in the words a bidder has time for ──────────────────────────────────────

    /// <summary>
    /// Turns the measurement into the block the card prints and the multiplier the ceiling uses.
    /// Split out from <see cref="Read"/> so the wording and the money can both be tested against
    /// hand-built readings, without a comp list that happens to produce them.
    /// </summary>
    public static LiveTrendRead Describe(PriceTrendReading reading, int windowDays = WindowDays)
    {
        windowDays = PriceTrendAnalyzer.ClampWindow(windowDays);

        var read = new LiveTrendRead
        {
            WindowDays = windowDays,
            Signal = reading.Signal,
            Reliability = reading.Reliability,
            // Readable means "there is a measured move to report", which is a stricter thing than
            // the measurement having a signal. A reading with sales before the window and none
            // inside it has a signal — the analyzer calls it cooling — and no price change at all,
            // because there is no recent median for the older one to have fallen TO. Nothing here
            // is allowed to describe that as a direction of travel. See SoloCorpus: from one
            // lookup, an item that stopped selling and a comps database that stopped being updated
            // look exactly alike, so this never says either.
            Readable = reading.Signal != "unreadable" && reading.PriceChangePercent is not null,
            Note = reading.Note,
            PriceChangePercent = reading.PriceChangePercent,
            RecentMedian = reading.Recent.MedianPrice,
            PriorMedian = reading.Prior.MedianPrice,
            RecentSold = reading.Recent.SoldCount,
            PriorSold = reading.Prior.SoldCount,
            TotalCompCount = reading.TotalCompCount,
            DatedCompCount = reading.DatedCompCount,
            SlopePerMonth = reading.SlopePerMonth,
        };

        read.Direction = DirectionOf(read, reading);
        read.Headline = Headline(read);
        ApplyMoney(read, reading);
        return read;
    }

    private static string DirectionOf(LiveTrendRead read, PriceTrendReading reading)
    {
        if (!read.Readable) return LiveTrendDirections.Unknown;

        return reading.Signal switch
        {
            "cooling" => LiveTrendDirections.Falling,
            "price_climbing" or "rising_demand" or "supply_squeeze" => LiveTrendDirections.Rising,
            // Volume moving before the price does. The price is what the ceiling is made of, and
            // the price has not moved, so this is steady — the sentence underneath says the rest.
            _ => LiveTrendDirections.Steady,
        };
    }

    private static string Headline(LiveTrendRead read)
    {
        var move = read.PriceChangePercent is decimal pct ? Math.Abs(pct) : 0m;

        return read.Direction switch
        {
            LiveTrendDirections.Falling =>
                $"Sold prices down {move:0.#}% — {read.PriorMedian:C0} → {read.RecentMedian:C0} median",
            LiveTrendDirections.Rising =>
                $"Sold prices up {move:0.#}% — {read.PriorMedian:C0} → {read.RecentMedian:C0} median",
            LiveTrendDirections.Steady =>
                $"Sold prices steady over {read.WindowDays * 2} days — {read.RecentMedian:C0} median now",
            // Two ways to have nothing to say, and they are not the same thing to a bidder: too
            // little dated history to read at all, and history that stops before the window starts.
            _ when read.PriorSold > 0 && read.RecentSold == 0 =>
                $"No sales in the last {read.WindowDays} days to compare against",
            _ => "Not enough dated sales to say which way the price is moving",
        };
    }

    // ── What the reading is allowed to do to the ceiling ─────────────────────────────────────

    /// <summary>
    /// The multiplier, and the two sentences that explain it. Every card that could be read gets a
    /// money sentence, including the ones where nothing moved: a block that only speaks when it took
    /// money off the ceiling is a block whose silence means both "steady" and "never looked".
    /// </summary>
    private static void ApplyMoney(LiveTrendRead read, PriceTrendReading reading)
    {
        if (!read.Readable)
        {
            read.MoneyNote = "The ceiling below is priced off the whole sold history — there is not enough " +
                             "dated evidence here to say whether that price has been moving.";
            return;
        }

        if (read.Direction == LiveTrendDirections.Rising)
        {
            // Deliberately no upside. See the class remarks: on a screen with seconds and one hammer,
            // paying today for a price that has not happened yet is how a good read loses money.
            read.MoneyNote = "The ceiling below is priced off the sold history as it stands. A climb never " +
                             "raises it — bidding up on a price that hasn't happened yet is paying for it twice.";
            return;
        }

        if (read.Direction != LiveTrendDirections.Falling
            || read.PriceChangePercent is not decimal change
            || change > -MaterialMovePercent)
        {
            read.MoneyNote = "The ceiling below is priced off the whole sold history, which is what these " +
                             "comps still say the item fetches.";
            return;
        }

        // A fall big enough to matter. Two gates before it is allowed to move money.
        if (read.Reliability != "confirmed")
        {
            read.MoneyNote = "The ceiling below is priced off the whole sold history. This read is too thin " +
                             "to cut it — treat the fall as a reason to look at the comps, not as a number.";
            read.Warning =
                $"Sold prices look to be sliding — {read.PriorMedian:C0} → {read.RecentMedian:C0} median over " +
                $"the last {read.WindowDays} days — but on {read.RecentSold} recent and {read.PriorSold} earlier " +
                "sales that is not firm enough to price against. The ceiling below has not been cut for it.";
            return;
        }

        // The second opinion. The window comparison sees two medians; the slope sees every dated
        // sale. A rising line under a falling pair of medians means the fall rests on where the
        // window boundary happened to land, and a ceiling cut on that would refuse a good lot.
        if (reading.SlopePerMonth is decimal slope && slope > 0m)
        {
            read.MoneyNote = "The ceiling below is priced off the whole sold history — the two ways of reading " +
                             "these comps disagree, so nothing was taken off it.";
            read.Warning =
                $"Two readings of the same comps disagree: the last {read.WindowDays} days are " +
                $"{Math.Abs(change):0.#}% below the {read.WindowDays} before them, but the trend line across " +
                $"every dated sale is rising {slope:C0} a month. The ceiling below is the full sold price — " +
                "worth opening the comps yourself before you bid to it.";
            return;
        }

        if (read.RecentMedian <= 0m || read.PriorMedian <= 0m) return;

        // The measured fall itself, floored. Not a judgement about how far it will keep going —
        // it is the ratio between what these sold for last month and what they sold for the month
        // before, applied to the price the estimator produced. The estimator's number stays the one
        // the rest of the app prices against; this contributes a ratio to it, never a replacement
        // for it. Same rule the Trend Radar follows in the other direction.
        var raw = 1m + change / 100m;
        var floor = 1m - MaxHaircutPercent / 100m;
        read.ResaleMultiplier = Math.Round(Math.Max(floor, raw), 4);
        read.Discounted = true;
        read.CutPercent = Math.Round((1m - read.ResaleMultiplier) * 100m, 1);
        read.Floored = raw < floor;

        read.MoneyNote =
            $"The ceiling below is priced {read.CutPercent:0.#}% under the whole sold history, at what these " +
            $"have actually been fetching for the last {read.WindowDays} days." +
            (read.Floored
                ? $" The fall measured further than that; the cut stops at {MaxHaircutPercent:0}%, because past " +
                  "there it stops looking like a price move and starts looking like a different item."
                : "");

        read.Warning =
            $"These are selling for less than they were: {read.PriorMedian:C0} → {read.RecentMedian:C0} median " +
            $"over the last {read.WindowDays} days, {Math.Abs(change):0.#}% down on the {read.WindowDays} before. " +
            $"The ceiling below is cut {read.CutPercent:0.#}% to match — the older comps are a price from then. " +
            "The middle-half spread and the comp list are every sale in the window, including those older ones.";
    }

    // ── Handing the cut to the money ─────────────────────────────────────────────────────────

    /// <summary>
    /// The same resale figures scaled by the trend, or the original object when nothing was cut.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the three prices the ceiling is actually built out of move — expected sale, median and
    /// quick sale. Everything else is copied across unchanged, including the percentile spread's
    /// source, the comp counts, the confidence and the sell-through: those are descriptions of
    /// sales that really happened, and scaling them would be inventing sales nobody made.
    /// </para>
    /// <para>
    /// Returning the <b>same instance</b> when the multiplier is 1 is deliberate rather than an
    /// optimisation. It is what makes "a card with no trend read is priced exactly as it was before
    /// this existed" a property of the code rather than a claim about it.
    /// </para>
    /// </remarks>
    public static ResalePricing Discount(ResalePricing resale, LiveTrendRead? trend)
    {
        if (trend is not { Discounted: true }) return resale;

        var multiplier = trend.ResaleMultiplier;
        if (multiplier <= 0m || multiplier >= 1m) return resale;

        return new ResalePricing
        {
            LookupTitle = resale.LookupTitle,
            Median = Scale(resale.Median, multiplier),
            ExpectedSale = Scale(resale.ExpectedSale, multiplier),
            QuickSale = Scale(resale.QuickSale, multiplier),
            SoldCompCount = resale.SoldCompCount,
            TerapeakCompCount = resale.TerapeakCompCount,
            SoldCompWeightPercent = resale.SoldCompWeightPercent,
            TerapeakWeightPercent = resale.TerapeakWeightPercent,
            PricedCompCount = resale.PricedCompCount,
            IdentityVerified = resale.IdentityVerified,
            AvgCompShipping = resale.AvgCompShipping,
            ConfidenceScore = resale.ConfidenceScore,
            ConfidenceLevel = resale.ConfidenceLevel,
            // The working behind the UNDISCOUNTED price, carried rather than rewritten. It is the
            // record of how the comps were blended; the cut is a separate fact, said separately.
            Basis = resale.Basis,
            DisagreementMessage = resale.DisagreementMessage,
            LiquidityScore = resale.LiquidityScore,
            LiquidityLevel = resale.LiquidityLevel,
            EstimatedDaysToSell = resale.EstimatedDaysToSell,
            EstimatedMonthlySales = resale.EstimatedMonthlySales,
            OpportunityScore = resale.OpportunityScore,
        };
    }

    private static decimal? Scale(decimal? price, decimal multiplier) =>
        price is > 0m ? Math.Round(price.Value * multiplier, 2) : price;
}
