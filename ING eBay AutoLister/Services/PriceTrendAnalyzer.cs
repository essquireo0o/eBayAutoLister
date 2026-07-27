using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Reads the sold-comps database as a TIME SERIES instead of a snapshot: which products are
/// selling for more than they did, and selling more often, than they did a window ago.
///
/// Everything here is pure and deterministic — no I/O, no clock, no randomness. `nowUtc` is always
/// passed in, so a reading can be reproduced exactly from the rows that produced it.
///
/// The whole file is an argument with itself about when a number is real. Four things will make a
/// price-trend tool print confident nonsense, and each has a guard below:
///
///   1. <b>The database's own drift.</b> If the collector ingested twice as many rows this month,
///      every product's "sale velocity" doubles and the whole board reads as a boom. Velocity is
///      therefore measured against <see cref="BuildCorpus"/>'s scan-wide baseline, not raw.
///   2. <b>The collector stopping.</b> If ingestion halted three weeks ago, every product on earth
///      shows zero recent sales and the board reads as a market-wide collapse. The corpus reports
///      its own freshness and refuses the whole scan rather than printing that.
///   3. <b>Missing dates.</b> SoldDate is free text and absent on a real share of rows. A trend
///      read off the four rows that happened to carry one is noise. Coverage is measured and gates
///      the verdict.
///   4. <b>Mix shift.</b> A cluster that quietly took in the Pro variant this month shows a price
///      "rise" that is a change of product, not a change of price. Wide and widening dispersion
///      demotes a reading to tentative.
/// </summary>
public static class PriceTrendAnalyzer
{
    /// <summary>Length of each comparison window. The scan reads two of them back to back.</summary>
    public const int DefaultWindowDays = 45;
    public const int MinWindowDays = 14;
    public const int MaxWindowDays = 120;

    /// <summary>Sales needed in BOTH windows before a reading can be called confirmed.</summary>
    public const int MinCompsPerWindow = 4;
    /// <summary>Below this, there is nothing to compare — the reading is unreadable, not flat.</summary>
    public const int MinCompsToCompare = 2;
    /// <summary>Dated rows a cluster must have at all, before coverage even matters.</summary>
    public const int MinDatedComps = 6;
    /// <summary>Share of a cluster's comps that must carry a parseable sold date.</summary>
    public const decimal MinDatedCoveragePercent = 60m;

    /// <summary>A real price move, as opposed to the noise a dozen used-item sales make.</summary>
    public const decimal ClimbingPricePercent = 8m;
    /// <summary>Enough to say the price is firming, not enough to call it climbing on its own.</summary>
    public const decimal FirmingPricePercent = 3m;
    /// <summary>Velocity move, AFTER the corpus baseline is taken out.</summary>
    public const decimal RisingVelocityPercent = 20m;

    /// <summary>Recent-window high/low ratio above which a cluster is too dispersed to trust.</summary>
    public const decimal MaxRecentSpreadRatio = 4m;
    /// <summary>Cap on the projection: a climb is never extrapolated past half again as much.</summary>
    public const decimal MaxProjectionMultiple = 1.5m;

    // Points fed to the slope estimator. Theil–Sen is O(n²) in pairs, and the newest sales are the
    // ones a trend is about, so a huge cluster is read from its most recent slice.
    private const int MaxSlopePoints = 60;

    // ── The corpus: the scan reading its own data before it reads any product ────────────────

    /// <summary>
    /// The scan-wide baseline, built once from every comp the sweep pulled. Answers two questions
    /// that have to be settled before a single product is judged: is this data fresh enough to say
    /// anything about "recently", and did the database's own volume move between the two windows?
    /// </summary>
    public static TrendCorpus BuildCorpus(
        IEnumerable<MarketplaceComparableResult> allComps, DateTime nowUtc, int windowDays)
    {
        windowDays = ClampWindow(windowDays);
        var corpus = new TrendCorpus { WindowDays = windowDays };

        var ages = new List<int>();
        foreach (var comp in allComps)
        {
            corpus.TotalComps++;
            if (comp.SoldDate is not DateTime sold) continue;
            corpus.DatedComps++;
            ages.Add(AgeDays(sold, nowUtc));
        }

        corpus.DatedCoveragePercent = corpus.TotalComps == 0
            ? 0m
            : Math.Round(corpus.DatedComps * 100m / corpus.TotalComps, 1);
        corpus.RecentComps = ages.Count(a => a < windowDays);
        corpus.PriorComps = ages.Count(a => a >= windowDays && a < windowDays * 2);
        corpus.NewestCompAgeDays = ages.Count > 0 ? ages.Min() : null;
        corpus.VelocityChangePercent = PercentChange(corpus.PriorComps, corpus.RecentComps);

        if (corpus.TotalComps == 0)
        {
            corpus.Note = "The sold-comps database returned nothing for these categories.";
            return corpus;
        }

        if (corpus.DatedComps < MinDatedComps)
        {
            corpus.Note = $"Only {corpus.DatedComps} of {corpus.TotalComps} sold comps carry a sale date — " +
                          "there is no time series to read a trend from.";
            return corpus;
        }

        // The one that matters most. A database whose newest row predates the recent window hasn't
        // told us the market went quiet; it has told us it stopped being updated. Reporting that as
        // "every product is cooling" would be the single most expensive lie this feature could tell.
        if (corpus.NewestCompAgeDays is int newest && newest >= windowDays)
        {
            corpus.Note = $"The newest sale in the comps database is {newest} days old, which is older than the " +
                          $"{windowDays}-day window. That's a gap in the data, not a market that went quiet — " +
                          "nothing here can be read as a trend until the comps database is refreshed.";
            return corpus;
        }

        corpus.IsReadable = true;
        if (corpus.VelocityChangePercent is decimal drift && Math.Abs(drift) >= RisingVelocityPercent)
        {
            corpus.Note = $"Sales volume across everything scanned is {Describe(drift)} " +
                          $"({corpus.PriorComps} → {corpus.RecentComps} sold comps). Each product's velocity " +
                          "below has that baseline taken out, so a busier database doesn't read as rising demand.";
        }

        return corpus;
    }

    // ── The measurement ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One product's trend: two windows of sold prices and counts, a robust slope across all of it,
    /// a weekly series for the sparkline, and a verdict that says how much of it to believe.
    /// Prices are per unit, matching the rest of the app — a lot of four at $400 is a $100 comp.
    /// </summary>
    public static PriceTrendReading Measure(
        IEnumerable<MarketplaceComparableResult> comps, DateTime nowUtc, int windowDays, TrendCorpus corpus)
    {
        windowDays = ClampWindow(windowDays);
        var reading = new PriceTrendReading();

        var dated = new List<(int AgeDays, decimal UnitPrice)>();
        foreach (var comp in comps)
        {
            if (comp.SoldPrice <= 0) continue;
            reading.TotalCompCount++;
            if (comp.SoldDate is not DateTime sold) continue;
            dated.Add((AgeDays(sold, nowUtc), Math.Round(comp.SoldPrice / Math.Max(1, comp.Quantity), 2)));
        }

        reading.DatedCompCount = dated.Count;
        reading.DatedCoveragePercent = reading.TotalCompCount == 0
            ? 0m
            : Math.Round(reading.DatedCompCount * 100m / reading.TotalCompCount, 1);

        reading.Recent = Window(dated, 0, windowDays);
        reading.Prior = Window(dated, windowDays, windowDays * 2);
        reading.Series = WeeklySeries(dated, windowDays * 2);

        if (reading.Recent.SoldCount >= MinCompsToCompare && reading.Prior.SoldCount >= MinCompsToCompare)
        {
            reading.PriceChangePercent = PercentChange(reading.Prior.MedianPrice, reading.Recent.MedianPrice);
            reading.PriceChangeAmount = Math.Round(reading.Recent.MedianPrice - reading.Prior.MedianPrice, 2);
        }

        reading.VelocityChangePercent = PercentChange(reading.Prior.SoldCount, reading.Recent.SoldCount);
        reading.MarketVelocityChangePercent = corpus.VelocityChangePercent;
        reading.RelativeVelocityChangePercent = Detrend(reading.VelocityChangePercent, corpus.VelocityChangePercent);

        reading.SlopePerMonth = SlopePerMonth(dated.Where(d => d.AgeDays < windowDays * 2).ToList());

        Judge(reading, corpus, windowDays);
        reading.ProjectedPrice = Project(reading);
        return reading;
    }

    // Only the sales inside a window, summarised. A window with no sales is a real answer and
    // comes back with a zero count rather than being skipped.
    private static TrendWindow Window(List<(int AgeDays, decimal UnitPrice)> dated, int fromDaysAgo, int toDaysAgo)
    {
        var prices = dated.Where(d => d.AgeDays >= fromDaysAgo && d.AgeDays < toDaysAgo)
            .Select(d => d.UnitPrice).OrderBy(p => p).ToList();

        return new TrendWindow
        {
            FromDaysAgo = fromDaysAgo,
            ToDaysAgo = toDaysAgo,
            SoldCount = prices.Count,
            MedianPrice = prices.Count == 0 ? 0m : Math.Round(MarketplacePricingCalculator.Median(prices), 2),
            LowPrice = prices.Count == 0 ? 0m : prices[0],
            HighPrice = prices.Count == 0 ? 0m : prices[^1],
        };
    }

    /// <summary>
    /// Weekly medians, newest week last, for the sparkline. Weeks with no sales are included at a
    /// zero count so the gaps in a product's selling history are visible rather than closed up —
    /// a line drawn only through the weeks that had sales flatters every intermittent seller.
    /// </summary>
    public static List<TrendPoint> WeeklySeries(List<(int AgeDays, decimal UnitPrice)> dated, int spanDays)
    {
        var weeks = Math.Max(1, (int)Math.Ceiling(spanDays / 7.0));
        var points = new List<TrendPoint>(weeks);
        for (var w = weeks - 1; w >= 0; w--)
        {
            var prices = dated.Where(d => d.AgeDays >= w * 7 && d.AgeDays < (w + 1) * 7)
                .Select(d => d.UnitPrice).OrderBy(p => p).ToList();
            points.Add(new TrendPoint
            {
                WeeksAgo = w,
                SoldCount = prices.Count,
                MedianPrice = prices.Count == 0 ? 0m : Math.Round(MarketplacePricingCalculator.Median(prices), 2),
            });
        }
        return points;
    }

    /// <summary>
    /// Theil–Sen slope in dollars per 30 days: the MEDIAN of every pairwise slope, rather than a
    /// least-squares line. One parts-only sale or one bundle-with-extras can drag a regression line
    /// through a whole trend; it can only ever move a median of slopes by one position.
    /// Null when there aren't two distinct dates to draw a line between.
    /// </summary>
    public static decimal? SlopePerMonth(List<(int AgeDays, decimal UnitPrice)> dated)
    {
        if (dated.Count < 3) return null;

        // Newest first, then trimmed: a trend is about recent sales, and the pair count is O(n²).
        var points = dated.OrderBy(d => d.AgeDays).Take(MaxSlopePoints).ToList();

        var slopes = new List<decimal>();
        for (var i = 0; i < points.Count; i++)
        {
            for (var j = i + 1; j < points.Count; j++)
            {
                // Time runs forward as age runs DOWN, so the run is (age_i - age_j) against a rise
                // of (price_j - price_i) — a newer, dearer sale gives a positive slope.
                var dx = points[i].AgeDays - points[j].AgeDays;
                if (dx == 0) continue;                            // same day — no slope to take
                slopes.Add((points[j].UnitPrice - points[i].UnitPrice) / dx);
            }
        }

        if (slopes.Count == 0) return null;
        return Math.Round(MarketplacePricingCalculator.Median(slopes) * 30m, 2);
    }

    // ── The verdict ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Turns the measurement into a signal, a reliability and a sentence. Split out from
    /// <see cref="Measure"/> so the classification can be tested against hand-built readings.
    /// </summary>
    public static void Judge(PriceTrendReading reading, TrendCorpus corpus, int windowDays)
    {
        windowDays = ClampWindow(windowDays);

        if (!corpus.IsReadable)
        {
            reading.Signal = "unreadable";
            reading.Reliability = "unreadable";
            reading.Note = corpus.Note ?? "The comps data can't support a trend reading.";
            return;
        }

        if (reading.DatedCompCount < MinDatedComps || reading.DatedCoveragePercent < MinDatedCoveragePercent)
        {
            reading.Signal = "unreadable";
            reading.Reliability = "unreadable";
            reading.Note = $"Only {reading.DatedCompCount} of {reading.TotalCompCount} sold comps carry a date — " +
                           "not enough dated history to say which way the price is moving.";
            return;
        }

        var recent = reading.Recent;
        var prior = reading.Prior;

        // Nothing recent, against a prior window that was busy, is information — the demand has
        // been and gone. It is emphatically NOT a rise, and it is not "no data" either.
        if (recent.SoldCount == 0)
        {
            reading.Signal = prior.SoldCount >= MinCompsPerWindow ? "cooling" : "unreadable";
            reading.Reliability = prior.SoldCount >= MinCompsPerWindow ? "confirmed" : "unreadable";
            reading.Note = prior.SoldCount >= MinCompsPerWindow
                ? $"{prior.SoldCount} sold in the {windowDays} days before last, and none since."
                : "Too few dated sales in either window to compare.";
            return;
        }

        if (recent.SoldCount < MinCompsToCompare || prior.SoldCount < MinCompsToCompare)
        {
            // A product with sales only in the recent window has no baseline to be rising ABOVE.
            reading.Signal = "unreadable";
            reading.Reliability = "unreadable";
            reading.Note = prior.SoldCount < MinCompsToCompare
                ? $"Only {prior.SoldCount} dated sale(s) in the earlier window — nothing to compare this one against."
                : $"Only {recent.SoldCount} dated sale(s) in the last {windowDays} days — too thin to read.";
            return;
        }

        var price = reading.PriceChangePercent ?? 0m;
        var velocity = reading.RelativeVelocityChangePercent;

        var priceUp   = price >= ClimbingPricePercent;
        var priceFirm = price >= FirmingPricePercent;
        var priceDown = price <= -FirmingPricePercent;
        var volUp     = velocity >= RisingVelocityPercent;
        var volDown   = velocity <= -RisingVelocityPercent;

        reading.Signal = (priceUp, priceFirm, priceDown, volUp, volDown) switch
        {
            (true,  _, _, true,  _) => "rising_demand",
            (true,  _, _, _,  true) => "supply_squeeze",
            (true,  _, _, _,     _) => "price_climbing",
            (_,  true, _, true,  _) => "rising_demand",
            (_,     _, true,  _, _) => "cooling",
            (_,     _, _, true,  _) => "demand_building",
            _                       => "steady",
        };

        reading.Reliability = "confirmed";
        var caveats = new List<string>();

        // Enough sales on both sides to be a comparison rather than an anecdote.
        if (recent.SoldCount < MinCompsPerWindow || prior.SoldCount < MinCompsPerWindow)
        {
            reading.Reliability = "tentative";
            caveats.Add($"only {recent.SoldCount} recent and {prior.SoldCount} earlier sales behind it");
        }

        // Mix shift: a cluster that quietly took in a different variant shows a price rise that is
        // a change of product. Wide recent dispersion, or dispersion that widened, is the tell.
        var recentSpread = SpreadRatio(recent);
        var priorSpread = SpreadRatio(prior);
        if (recentSpread > MaxRecentSpreadRatio || (priorSpread > 0 && recentSpread > priorSpread * 2m))
        {
            reading.Reliability = "tentative";
            caveats.Add($"recent sales run {recent.LowPrice:C0}–{recent.HighPrice:C0}, wide enough that this " +
                        "could be a change of variant rather than a change of price");
        }

        // Second opinion. The window comparison sees two medians; the slope sees every sale. When
        // the line through all of it points the OTHER way, two unusual sales made the jump.
        // A flat slope is not a contradiction — a price that stepped up and held reads exactly like
        // that — so only a falling line demotes a rise.
        if (reading.Signal is "rising_demand" or "price_climbing" or "supply_squeeze"
            && reading.SlopePerMonth is < 0m)
        {
            reading.Reliability = "tentative";
            caveats.Add("the trend line across every dated sale is falling, so the jump rests on where the window was split");
        }

        reading.Note = Sentence(reading, windowDays, caveats);
    }

    private static string Sentence(PriceTrendReading reading, int windowDays, List<string> caveats)
    {
        var price = reading.PriceChangePercent ?? 0m;
        var velocity = reading.RelativeVelocityChangePercent;
        var priceMove = $"{Math.Abs(price):0.#}% {(price >= 0 ? "up" : "down")} " +
                        $"({reading.Prior.MedianPrice:C0} → {reading.Recent.MedianPrice:C0} median)";
        var volMove = velocity is decimal v
            ? $"{reading.Prior.SoldCount} → {reading.Recent.SoldCount} sales, {Describe(v)} against the rest of the scan"
            : $"{reading.Prior.SoldCount} → {reading.Recent.SoldCount} sales";

        var body = reading.Signal switch
        {
            "rising_demand"   => $"Selling for more AND selling more often: {priceMove}, {volMove}.",
            "price_climbing"  => $"Price is climbing on steady volume: {priceMove}, {volMove}.",
            "supply_squeeze"  => $"Price up but fewer changing hands: {priceMove}, {volMove}. That's supply " +
                                 "drying up rather than demand arriving — good for whoever already has one, " +
                                 "harder to buy into.",
            "demand_building" => $"Volume is moving before the price is: {volMove}, {priceMove}. This is the " +
                                 "early half of a move, not a move yet.",
            "cooling"         => $"Softening: {priceMove}, {volMove}.",
            _                 => $"Flat: {priceMove}, {volMove}.",
        };

        var slope = reading.SlopePerMonth is decimal s && s != 0m
            ? $" Trend line across every dated sale: {(s > 0 ? "+" : "")}{s:C0} a month."
            : "";

        var caveat = caveats.Count == 0 ? "" : $" Treat it as tentative — {string.Join("; ", caveats)}.";
        return body + slope + caveat;
    }

    // What the price becomes one window on if the same move repeats — clamped hard, because this is
    // the one number on the board that isn't a measurement.
    private static decimal? Project(PriceTrendReading reading)
    {
        if (!reading.IsRising || reading.Reliability == "unreadable") return null;
        if (reading.PriceChangePercent is not decimal change || change <= 0) return null;
        if (reading.Recent.MedianPrice <= 0) return null;

        var projected = reading.Recent.MedianPrice * (1m + change / 100m);

        // Never past the highest price anyone actually paid recently, and never more than half
        // again as much. One window forward, never compounded.
        var ceiling = Math.Min(
            reading.Recent.HighPrice > 0 ? reading.Recent.HighPrice : projected,
            reading.Recent.MedianPrice * MaxProjectionMultiple);

        return Math.Round(Math.Max(reading.Recent.MedianPrice, Math.Min(projected, ceiling)), 2);
    }

    // ── The board ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The verdict on a measured, priced product. The trend never makes a bad buy good: a row can
    /// only be <c>buy_now</c> if the money already works at TODAY's price and the evidence bar the
    /// rest of the app uses is cleared. Everything the trend contributes is upside on top.
    /// </summary>
    public static (string Verdict, string Note) JudgeRow(
        PriceTrendReading trend, int compCount, int confidenceScore, string confidenceLevel,
        decimal maxBuyToday, decimal targetBuyPrice, decimal trendHeadroom)
    {
        if (maxBuyToday <= 0)
            return ("pass", "Fees and shipping eat the whole sale price — no buy price makes this work, " +
                            "whichever way the price is moving.");

        // Worded from the evidence, not from the trend: this branch is reached by cooling and
        // unreadable rows too, and telling someone "the price is moving" about a product whose
        // sales stopped would be exactly backwards.
        if (compCount < LocalArbitrageAnalyzer.GoldmineMinComps || confidenceScore < LocalArbitrageAnalyzer.GoldmineMinConfidence)
            return ("thin", $"Only {compCount} sold comp{(compCount == 1 ? "" : "s")} behind this product " +
                            $"({confidenceLevel.ToLowerInvariant()}) — too little to bet money on either way. " +
                            $"Treat {maxBuyToday:C0} as indicative, not a number to bid on.");

        var headroom = trendHeadroom > 0
            ? $" If the climb holds, every unit bought today is worth about {trendHeadroom:C0} more by the time it sells."
            : "";

        return trend.Signal switch
        {
            "rising_demand" or "price_climbing" when trend.Reliability == "confirmed" =>
                ("buy_now", targetBuyPrice > 0
                    ? $"Buy under {targetBuyPrice:C0} and it clears {LocalArbitrageAnalyzer.GoldmineProfit:C0} net at " +
                      $"{LocalArbitrageAnalyzer.GoldmineRoiPercent:0}% ROI at today's price.{headroom}"
                    : $"Break-even is {maxBuyToday:C0} at today's price, so it only pays if you get it cheap.{headroom}"),

            "demand_building" when trend.Reliability == "confirmed" =>
                ("get_in_early", $"More of these are selling, but the price hasn't followed yet. Break-even today " +
                                 $"is {maxBuyToday:C0} — worth stocking at the current price, not worth chasing."),

            "supply_squeeze" =>
                ("watch", $"Fewer are changing hands at a higher price, which is scarcity rather than demand. " +
                          $"Break-even is {maxBuyToday:C0}, but supply is exactly what you'd struggle to find.{headroom}"),

            _ => ("watch", $"{trend.Note} Break-even at today's price is {maxBuyToday:C0}."),
        };
    }

    /// <summary>
    /// The projection expressed as a multiplier on today's price (1.0 = no move). The radar applies
    /// it to the price the ESTIMATOR produced rather than swapping in the cluster's own projected
    /// median: the estimator's number is the one the rest of the app prices against, so the trend
    /// contributes a ratio to it, not a replacement for it.
    /// </summary>
    public static decimal TrendMultiplier(PriceTrendReading reading)
    {
        if (reading.ProjectedPrice is not decimal projected || reading.Recent.MedianPrice <= 0) return 1m;
        var multiplier = projected / reading.Recent.MedianPrice;
        return multiplier < 1m ? 1m : Math.Min(multiplier, MaxProjectionMultiple);
    }

    /// <summary>
    /// Best believable play first. Verdict leads the sort, not the size of the percentage — the top
    /// of this board has to be the buy most worth making, not the wildest-looking number.
    /// </summary>
    public static List<TrendRadarRow> Rank(IEnumerable<TrendRadarRow> rows) =>
        rows.OrderBy(r => VerdictRank(r.Verdict))
            .ThenByDescending(r => r.TrendHeadroom + r.ProfitAtTarget)
            .ThenByDescending(r => r.Trend.PriceChangePercent ?? 0m)
            .ThenByDescending(r => r.ConfidenceScore)
            .ThenBy(r => r.Product, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static int VerdictRank(string verdict) => verdict switch
    {
        "buy_now" => 0, "get_in_early" => 1, "watch" => 2, "thin" => 3, "pass" => 4, _ => 5,
    };

    // ── Small shared arithmetic ─────────────────────────────────────────────────────────────

    public static int ClampWindow(int windowDays) => Math.Clamp(windowDays, MinWindowDays, MaxWindowDays);

    // Age in whole days, floored at zero: a comps row dated slightly in the future (timezone slop
    // in a free-text date) is today's sale, not a negative-age one that breaks every bucket.
    private static int AgeDays(DateTime soldDate, DateTime nowUtc) =>
        (int)Math.Max(0, (nowUtc - soldDate).TotalDays);

    // Percentage change from a baseline. Null when the baseline is zero — growth from nothing is
    // undefined, and printing "+∞%" or "+100%" for it would be inventing a number.
    public static decimal? PercentChange(decimal from, decimal to) =>
        from <= 0 ? null : Math.Round((to - from) / from * 100m, 1);

    /// <summary>
    /// A product's velocity change with the corpus-wide change divided out, multiplicatively —
    /// a product up 30% in a scan that is itself up 30% has not moved at all. Returns the raw
    /// figure when there is no baseline to remove.
    /// </summary>
    public static decimal? Detrend(decimal? raw, decimal? corpus)
    {
        if (raw is not decimal r) return null;
        if (corpus is not decimal c) return r;
        var denominator = 1m + c / 100m;
        if (denominator <= 0m) return r;   // corpus fell to nothing — no meaningful baseline left
        return Math.Round(((1m + r / 100m) / denominator - 1m) * 100m, 1);
    }

    private static decimal SpreadRatio(TrendWindow window) =>
        window.LowPrice <= 0 ? 0m : Math.Round(window.HighPrice / window.LowPrice, 2);

    private static string Describe(decimal percent) =>
        percent >= 0 ? $"up {percent:0.#}%" : $"down {Math.Abs(percent):0.#}%";
}
