using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// One source's contribution to a blended price: how many sales it saw, what it said they were
/// worth, and how much of the final figure is its opinion.
/// </summary>
/// <param name="Value">
/// The figure this source actually put into the blend — not a headline median it didn't use. For
/// the sold-comps database that is the weighted median (recent, closely-matching and fixed-price
/// sales pull harder); for Terapeak it is the plain median, which is all Terapeak publishes.
/// </param>
/// <param name="WeightPercent">
/// Carried at full precision, not rounded to the whole number the screen shows: this is the
/// multiplier the blend actually used, and it is what makes the trail reconcile to the cent.
/// </param>
/// <param name="FoundCount">
/// How many comps the lookup returned, when that differs from <paramref name="CompCount"/>. Zero
/// when there is nothing extra to say. The gap is the identity guard, the outlier trim and the
/// strong-match filter doing their work, and it is routinely most of the search.
/// </param>
/// <param name="AsOf">
/// When this source saw the market, in words — the span the sales cover, or the date the figure was
/// taken. Empty when nothing carried a date. A weight quoted without it asks the reader to accept
/// an age adjustment whose reason is off screen.
/// </param>
/// <param name="FreshnessNote">
/// Stated only when age has already cost this source part of its pull, and says how much. Empty
/// otherwise, because "counted at 100%" is not news.
/// </param>
public sealed record PriceBasisSource(
    string Key, string Label, string ValueLabel, decimal Value, decimal WeightPercent,
    int CompCount, int FoundCount, string AsOf = "", string FreshnessNote = "");

/// <summary>
/// The arithmetic behind one resale price, and behind the confidence score printed next to it.
/// </summary>
/// <remarks>
/// <para>
/// Every money column on a deal row is derived from a single figure — the expected sale price — and
/// that figure is a weighted blend of two sources whose own numbers are overwritten the moment they
/// are combined. Until this existed, a seller could see "63% local / 37% Terapeak" and had no way
/// to learn 63% and 37% <i>of what</i>, or why the confidence beside it was 62 rather than 90.
/// </para>
/// <para>
/// So this carries the inputs, not a summary of them: each source's own figure, its weight, its
/// comp count, and the seven terms of the confidence score with the points each earned. The rule it
/// exists to keep is that <see cref="Price"/> is reproducible from <see cref="Sources"/> — multiply
/// and add, and the number on screen comes back out. A test pins exactly that, because a trail that
/// doesn't reconcile is worse than none: it invites trust it hasn't earned.
/// </para>
/// <para>Pure and total. Built once per priced product, from figures already computed.</para>
/// </remarks>
public sealed class PriceBasis
{
    public const string HostedCompsKey = "hosted_comps";
    public const string TerapeakKey = "terapeak";

    /// <summary>The expected sale price every money column on the row was computed from.</summary>
    public decimal Price { get; set; }

    /// <summary>
    /// The blend in one line, e.g. <c>$418.20 = 63% × $430.00 (eBay sold comps, 7 sold comps) +
    /// 37% × $398.00 (Terapeak, 12 sold comps)</c>. Percentages are rounded for reading;
    /// <see cref="Sources"/> carries the exact weights the arithmetic used.
    /// </summary>
    public string Arithmetic { get; set; } = "";

    public List<PriceBasisSource> Sources { get; set; } = [];

    /// <summary>
    /// The blended median, when it differs from <see cref="Price"/>. The two are different views of
    /// the same comps — the midpoint, and the midpoint after recency/match/format weighting — and a
    /// seller checking this row against an eBay sold search will find the first one, not the second.
    /// </summary>
    public decimal? MedianPrice { get; set; }

    /// <summary>What to list at: the expected sale plus the negotiation buffer already applied.</summary>
    public decimal? RecommendedListingPrice { get; set; }

    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "";
    public List<ConfidenceFactor> ConfidenceFactors { get; set; } = [];
    public string? BiggestGap { get; set; }

    /// <summary>Set when the two sources differ by more than 20% — the one caveat that outranks the score.</summary>
    public string? DisagreementMessage { get; set; }

    /// <summary>
    /// Builds the trail for a priced product. Returns null when there is no price to explain — a row
    /// the app refused to value has a reason instead of a number, and stating that reason is
    /// <see cref="ResaleValuation"/>'s job, not this one's.
    /// </summary>
    /// <param name="localCompsFound">
    /// How many comps the local lookup returned, before the identity guard and the trim.
    /// </param>
    public static PriceBasis? From(MarketAnalysisResult analysis, int localCompsFound = 0, DateTime? nowUtc = null) =>
        From(analysis.PriceEstimate, analysis.Confidence, localCompsFound, nowUtc);

    public static PriceBasis? From(
        PriceEstimate estimate, ConfidenceBreakdown confidence, int localCompsFound = 0, DateTime? nowUtc = null)
    {
        var price = estimate.ExpectedSalePrice ?? estimate.MedianPrice;
        if (price is not > 0) return null;

        var now = nowUtc ?? DateTime.UtcNow;
        var sources = new List<PriceBasisSource>();

        // The local side contributes its weighted median — the figure that entered the blend. When
        // the blend never ran (Terapeak silent) the weight is 1 and this IS the price.
        var localValue = estimate.LocalExpectedSalePrice ?? estimate.LocalMedianPrice;
        if (localValue is > 0 && estimate.LocalWeight > 0)
        {
            sources.Add(new PriceBasisSource(
                HostedCompsKey, "eBay sold comps", "weighted median", localValue.Value,
                estimate.LocalWeight * 100m, estimate.PricedOnCompCount,
                localCompsFound > estimate.PricedOnCompCount ? localCompsFound : 0,
                SoldWindow(estimate.LocalOldestSoldAtUtc, estimate.LocalNewestSoldAtUtc)));
        }

        if (estimate.TerapeakMedianPrice is > 0 && estimate.TerapeakWeight > 0)
        {
            sources.Add(new PriceBasisSource(
                TerapeakKey, "Terapeak", "median", estimate.TerapeakMedianPrice.Value,
                estimate.TerapeakWeight * 100m, estimate.TerapeakComparableCount, 0,
                ScrapedOn(estimate.TerapeakScrapedAtUtc, now),
                FreshnessNote(estimate.TerapeakFreshnessWeight)));
        }

        return new PriceBasis
        {
            Price = price.Value,
            Arithmetic = Describe(price.Value, sources),
            Sources = sources,
            MedianPrice = estimate.MedianPrice is > 0 && estimate.MedianPrice != price ? estimate.MedianPrice : null,
            RecommendedListingPrice = estimate.RecommendedListingPrice,
            ConfidenceScore = confidence.Score,
            ConfidenceLevel = confidence.Level,
            ConfidenceFactors = confidence.Factors,
            BiggestGap = confidence.BiggestGap,
            DisagreementMessage = estimate.DisagreementMessage,
        };
    }

    /// <summary>
    /// The blend as a sentence. One source states itself plainly rather than pretending to be a
    /// weighted sum of one — "100% × $430" is arithmetic theatre.
    /// </summary>
    private static string Describe(decimal price, IReadOnlyList<PriceBasisSource> sources) => sources.Count switch
    {
        0 => $"{Money(price)} — the price the money columns were computed from.",
        1 => $"{Money(price)} — the {sources[0].ValueLabel} of {Comps(sources[0])} from {sources[0].Label}.",
        _ => $"{Money(price)} = " + string.Join(
            "  +  ", sources.Select(s => $"{s.WeightPercent:0}% × {Money(s.Value)} ({s.Label}, {Comps(s)})")),
    };

    private static string Comps(PriceBasisSource s) =>
        s.CompCount == 1 ? "1 sold comp" : $"{s.CompCount} sold comps";

    /// <summary>
    /// The span the local comps cover, e.g. <c>sales dated 12 Mar – 28 Jul 2026</c>. One date when
    /// they all landed on the same day; empty when none of them carried one, which is itself worth
    /// not papering over — the confidence score has a term for exactly that.
    /// </summary>
    private static string SoldWindow(DateTime? oldest, DateTime? newest)
    {
        if (oldest is not DateTime from || newest is not DateTime to) return "";
        return from.Date == to.Date
            ? $"sales dated {Day(to)}"
            : $"sales dated {Day(from)} – {Day(to)}";
    }

    /// <summary>
    /// When the Terapeak figure was taken, with the age spelled out. The date alone makes the
    /// reader do the subtraction, and the age alone hides which snapshot this was.
    /// </summary>
    private static string ScrapedOn(DateTime? scrapedAtUtc, DateTime nowUtc)
    {
        if (scrapedAtUtc is not DateTime at) return "";

        var days = (int)Math.Floor((nowUtc - at).TotalDays);
        var age = days switch
        {
            <= 0 => "today",
            1 => "yesterday",
            < 60 => $"{days} days ago",
            < 365 => $"{days / 30} months ago",
            _ => "over a year ago",
        };
        return $"scraped {Day(at)} · {age}";
    }

    /// <summary>
    /// What age has already cost this source. The freshness step (1.0 / 0.7 / 0.4 / 0.2 at 30, 90
    /// and 180 days) is applied to the weight before it reaches this panel, so without this line the
    /// share on screen is smaller than the comp counts explain and nothing says why.
    /// </summary>
    private static string FreshnessNote(double freshnessWeight)
    {
        if (freshnessWeight is <= 0 or >= 1) return "";
        return string.Format(
            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            "counted at {0:P0} of full weight for its age", freshnessWeight);
    }

    private static string Day(DateTime value) =>
        value.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
