using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Turns a matched-and-scored set of local comparables, plus (optionally) a Terapeak lookup, into
// the full PriceEstimate the spec calls for — median/weighted-median/trimmed-mean/P25/P75/
// quick-sale/expected-sale/recommended-listing/high-price-target — instead of a raw average.
// Reuses MarketplacePricingCalculator for outlier removal and percentile/median math (not
// reimplemented here); adds per-unit quantity normalization, buying-format-aware weighting, and
// the adaptive local-vs-Terapeak blend + disagreement detection the calculator alone doesn't do.
public sealed class MarketPriceEstimator(TerapeakMarketService terapeakMarket)
{
    private const int StrongMatchThreshold = 50; // MatchConfidence floor for a comp to count as "strong"
    private const int RecentDays = 90;

    public async Task<PriceEstimate> EstimateAsync(
        NormalizedProduct target, IReadOnlyList<MarketplaceComparableResult> localComparables,
        string rawQueryForTerapeak, string? listingType, bool allowRealTerapeakScrape,
        int? activeCompetitionCount = null, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;

        // Per-unit normalization: a "lot of 10" sold at $500 is a $50 comparable, not a $500 one.
        var perUnit = localComparables.Select(NormalizeToUnitPrice).ToList();
        // Identity guard (general, all items): when the target has a real model/part identifier,
        // restrict the pricing set to comps whose title actually contains it — so a cheap item
        // can't be priced off comps of a different, often pricier product that merely shares a
        // brand/keyword (e.g. a $74 "FANUC GP50 fuse" priced off $1050 FANUC drives). Falls back to
        // the unfiltered set when the guard would leave too few comps to trust.
        var identityFiltered = ApplyIdentityGuard(target, perUnit);
        var trimmed = MarketplacePricingCalculator.RemovePriceOutliers(identityFiltered);

        var strongRecent = trimmed
            .Where(c => c.MatchScore >= StrongMatchThreshold && (c.SoldDate is null || (now - c.SoldDate.Value).TotalDays <= RecentDays))
            .ToList();
        // Widen the window when recent strong comps are too sparse to trust — this is the "expand
        // the historical window" behavior the spec asks for when a product has very few recent
        // sales (confidence is lowered separately, by ConfidenceScoringService).
        var strongForStats = strongRecent.Count >= 3 ? strongRecent : trimmed.Where(c => c.MatchScore >= StrongMatchThreshold).ToList();
        if (strongForStats.Count == 0) strongForStats = trimmed;

        var prices = strongForStats.Select(c => c.SoldPrice).OrderBy(p => p).ToList();
        var weighted = strongForStats.Select(c => (c.SoldPrice, Weight: WeightFor(c, listingType, now))).ToList();

        var estimate = new PriceEstimate();
        if (prices.Count > 0)
        {
            estimate.MedianPrice = Math.Round(MarketplacePricingCalculator.Median(prices), 2);
            estimate.WeightedMedianPrice = Math.Round(MarketplacePricingCalculator.WeightedMedian(weighted), 2);
            estimate.TrimmedMeanPrice = Math.Round(prices.Average(), 2);
            estimate.Percentile25 = Math.Round(MarketplacePricingCalculator.Percentile(prices, 0.25), 2);
            estimate.Percentile75 = Math.Round(MarketplacePricingCalculator.Percentile(prices, 0.75), 2);
            estimate.MinimumRealisticPrice = prices.Min();
            estimate.MaximumRealisticPrice = prices.Max();

            estimate.QuickSalePrice = estimate.Percentile25;
            estimate.ExpectedSalePrice = estimate.WeightedMedianPrice;
            // Negotiation buffer: modest by default, scaled down as active competition rises (a
            // crowded market punishes overpricing), scaled up when competition is known to be low.
            var bufferPercent = activeCompetitionCount switch
            {
                null => 0.05m,
                <= 2 => 0.08m,
                <= 15 => 0.05m,
                _ => 0.02m,
            };
            estimate.RecommendedListingPrice = Math.Round(estimate.ExpectedSalePrice!.Value * (1 + bufferPercent), 2);
            // Raw P75 — display-gating ("only show when low competition/strong demand/superior
            // condition") is the caller's responsibility (Program.cs / OpportunityScoringService),
            // since competition/demand data lives outside this estimator.
            estimate.HighPriceTarget = estimate.Percentile75;
        }

        // ── Terapeak — validates current pricing, on-demand only (never bulk/background) ──────
        var terapeakResult = await terapeakMarket.GetAsync(target, rawQueryForTerapeak, allowRealTerapeakScrape, ct: ct);

        ApplyAdaptiveWeighting(estimate, strongForStats.Count, terapeakResult);

        return estimate;
    }

    // Keep only comps whose title contains one of the target's distinctive model/part tokens
    // (a token with a digit, or length >= 4). General across categories; a no-op when the target
    // has no usable identifier, and it never zeroes out the estimate (falls back if < 3 survive).
    // Pure/testable: takes only the target identity and the comps, not the estimate.
    public static List<MarketplaceComparableResult> ApplyIdentityGuard(
        NormalizedProduct target, List<MarketplaceComparableResult> comps)
    {
        var tokens = new[] { target.Model, target.PartNumber }
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .SelectMany(s => Norm(s).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 3 && (t.Any(char.IsDigit) || t.Length >= 4))
            .Distinct()
            .ToList();
        if (tokens.Count == 0) return comps;

        var kept = comps.Where(c => { var t = Norm(c.Title); return tokens.Any(t.Contains); }).ToList();
        return kept.Count >= 3 ? kept : comps;
    }

    private static string Norm(string? s) =>
        new string((s ?? "").ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ').ToArray());

    private static MarketplaceComparableResult NormalizeToUnitPrice(MarketplaceComparableResult c)
    {
        if (c.Quantity <= 1) return c;
        return new MarketplaceComparableResult
        {
            ItemId = c.ItemId, Title = c.Title, SoldPrice = Math.Round(c.SoldPrice / c.Quantity, 2),
            Shipping = c.Shipping, TotalPrice = Math.Round((c.SoldPrice + c.Shipping) / c.Quantity, 2),
            Condition = c.Condition, SoldDate = c.SoldDate, Seller = c.Seller, ItemUrl = c.ItemUrl,
            ImageUrl = c.ImageUrl, MatchScore = c.MatchScore, Epid = c.Epid, IsFixedPrice = c.IsFixedPrice,
            SellerFeedbackCount = c.SellerFeedbackCount, SellerPositiveFeedbackPercent = c.SellerPositiveFeedbackPercent,
            Quantity = c.Quantity,
        };
    }

    // Same match-strength/recency nudges MarketplacePricingCalculator.WeightFor already applies,
    // plus buying-format: when the target will be listed Buy-It-Now, a fixed-price comp is a
    // better predictor than an auction result (which can swing on bidding dynamics).
    private static double WeightFor(MarketplaceComparableResult item, string? listingType, DateTime nowUtc)
    {
        var weight = 1.0;
        weight *= item.MatchScore switch { >= 90 => 2.0, >= 70 => 1.3, _ => 1.0 };
        if (item.SoldDate is DateTime sold)
        {
            var ageDays = (nowUtc - sold).TotalDays;
            weight *= ageDays switch { <= 30 => 1.3, <= 90 => 1.1, _ => 1.0 };
        }
        if (string.Equals(listingType, "FIXED_PRICE", StringComparison.OrdinalIgnoreCase) && item.IsFixedPrice)
            weight *= 1.25;
        return weight;
    }

    // Adaptive local-vs-Terapeak blend per the spec's table, plus a >20%-median-disagreement flag
    // so the caller shows both estimates and lowers confidence instead of silently picking one.
    private static void ApplyAdaptiveWeighting(PriceEstimate estimate, int localStrongCount, TerapeakMarketResult? terapeak)
    {
        var terapeakStrongCount = terapeak?.Data.Count ?? 0;
        estimate.TerapeakComparableCount = terapeakStrongCount;

        if (terapeak is null || terapeak.Data.Median <= 0)
        {
            estimate.LocalWeight = estimate.MedianPrice is > 0 ? 1.0m : 0m;
            estimate.TerapeakWeight = 0m;
            return;
        }

        var localMedian = estimate.MedianPrice ?? 0m;
        var terapeakMedian = terapeak.Data.Median > 0 ? terapeak.Data.Median : terapeak.Data.Average;

        // Robust dispersion for each source, kept on a comparable scale:
        //   local    -> inter-quartile range (P75 - P25)
        //   Terapeak -> half its min..max range (we only get min/max, not quartiles; half-range
        //               keeps it roughly IQR-comparable and less inflated by a single outlier).
        var localSpread = Math.Max(0m, (estimate.Percentile75 ?? 0m) - (estimate.Percentile25 ?? 0m));
        var terapeakSpread = terapeak.Data.Max > terapeak.Data.Min
            ? (terapeak.Data.Max - terapeak.Data.Min) * 0.5m
            : 0m;

        var (localWeight, terapeakWeight) = ResolveWeights(
            estimate.MedianPrice is > 0, localStrongCount, terapeakStrongCount, terapeak.FreshnessWeight,
            localMedian, localSpread, terapeakMedian, terapeakSpread);

        estimate.LocalWeight = localWeight;
        estimate.TerapeakWeight = terapeakWeight;

        var blendedMedian = localMedian * localWeight + terapeakMedian * terapeakWeight;
        var blendedExpected = (estimate.ExpectedSalePrice ?? localMedian) * localWeight + terapeakMedian * terapeakWeight;

        var (disagree, message) = DetectDisagreement(localMedian, terapeakMedian);
        estimate.MarketDataDisagreement = disagree;
        estimate.DisagreementMessage = message;

        estimate.MedianPrice = Math.Round(blendedMedian, 2);
        estimate.ExpectedSalePrice = Math.Round(blendedExpected, 2);
        estimate.RecommendedListingPrice = Math.Round(blendedExpected * 1.05m, 2);
        if (terapeak.Data.AvgShipping > 0 && estimate.QuickSalePrice is null)
            estimate.QuickSalePrice = Math.Round(blendedMedian * 0.85m, 2);
    }

    // Adaptive local-vs-Terapeak blend weights per the spec's table. Pure/testable: takes only
    // the counts/flags the decision depends on, not the estimate/result objects themselves.
    public static (decimal LocalWeight, decimal TerapeakWeight) ResolveWeights(
        bool hasLocalMedian, int localStrongCount, int terapeakStrongCount, double terapeakFreshnessWeight,
        decimal localMedian = 0m, decimal localSpread = 0m,
        decimal terapeakMedian = 0m, decimal terapeakSpread = 0m)
    {
        // No usable estimate on one side -> the other carries everything.
        if (!hasLocalMedian || localStrongCount <= 0) return (0m, 1m);
        if (terapeakStrongCount <= 0)                 return (1m, 0m);

        // Precision-weighted blend — a smooth stand-in for inverse-variance weighting. Each
        // source's pull ~ its sample size x its reliability, where reliability falls as that
        // source's own spread widens relative to its median (a noisy source counts for less).
        // Spread is optional: when unknown it is neutral, so the blend degrades to a smooth
        // sample-size ratio — no arbitrary bucket cliffs like the old 3/10-comp step table.
        var localPrecision    = localStrongCount    * Reliability(localMedian,    localSpread);
        var terapeakPrecision = terapeakStrongCount * Reliability(terapeakMedian, terapeakSpread);

        // Freshness decays Terapeak's effective weight — a stale scrape is worth less, and the
        // lost share flows back to local rather than vanishing.
        terapeakPrecision *= Math.Clamp((decimal)terapeakFreshnessWeight, 0m, 1m);

        var total = localPrecision + terapeakPrecision;
        if (total <= 0m) return (1m, 0m);

        var terapeakWeight = terapeakPrecision / total;
        // Never let one real source completely erase the other — keep both visible in the estimate.
        terapeakWeight = Math.Clamp(terapeakWeight, 0.15m, 0.85m);
        return (1m - terapeakWeight, terapeakWeight);
    }

    // Reliability multiplier in (0,1]: 1 when spread is unknown/zero, shrinking smoothly as the
    // relative spread (spread / median) grows. Bounded and monotonic, so a single wide-ranging
    // source is down-weighted without ever dominating or disappearing.
    private static decimal Reliability(decimal median, decimal spread)
    {
        if (median <= 0m || spread <= 0m) return 1m;
        return 1m / (1m + spread / median);
    }

    // >20% median disagreement between the two sources — flagged so the caller shows both
    // estimates and lowers confidence instead of silently picking one. Pure/testable.
    public static (bool Disagree, string? Message) DetectDisagreement(decimal localMedian, decimal terapeakMedian)
    {
        if (localMedian <= 0 || terapeakMedian <= 0) return (false, null);

        var diff = Math.Abs(localMedian - terapeakMedian) / Math.Max(localMedian, terapeakMedian);
        if (diff <= 0.20m) return (false, null);

        return (true,
            $"Local sold-history median (${localMedian:0.00}) and Terapeak median (${terapeakMedian:0.00}) " +
            $"differ by {diff:P0} — showing both, confidence lowered rather than picking one.");
    }
}
