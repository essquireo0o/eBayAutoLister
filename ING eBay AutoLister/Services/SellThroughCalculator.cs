using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// SellThroughRate = matched Sold comparables / matched Active comparables, guarded against
// divide-by-zero, plus a capped 0-100 SellThroughScore and the plain-language interpretation the
// spec calls for. Wraps LiquidityScoringService.Assess (reused as-is) for sales velocity and a
// base days-to-sell estimate, then nudges days-to-sell by competition/price-position — a distinct
// metric from LiquidityScoringService's own internal LiquidityScore (velocity+trend+competition
// weights), not a replacement for it.
public sealed class SellThroughCalculator(LiquidityScoringService liquidity)
{
    // (Percent sell-through, Score) control points — piecewise-linear interpolation between them.
    // Matches the spec's bands: 100%+=Excellent(100), 60-99%=Very Strong(~85), 35-59%=Good(~55),
    // 15-34%=Moderate(~30), 5-14%=Weak(~12), <5%=Poor(~3). 100% itself already caps at 100 (not
    // just "approaching" it) — "100% or higher: excellent" per the spec.
    private static readonly (decimal Pct, int Score)[] ScoreCurve =
    [
        (0m, 0), (5m, 3), (15m, 12), (35m, 30), (60m, 55), (100m, 100),
    ];

    public SellThroughAnalysis Calculate(
        string productQuery, IReadOnlyList<MarketplaceComparableResult> soldComparables,
        int activeComparableCount, decimal? candidatePrice = null, decimal? medianActivePrice = null,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var assessment = liquidity.Assess(productQuery, soldComparables, activeComparableCount, now);
        var soldCount = soldComparables.Count;

        var analysis = new SellThroughAnalysis
        {
            SoldComparableCount = soldCount,
            ActiveComparableCount = Math.Max(0, activeComparableCount),
            EstimatedMonthlySales = assessment.SalesVelocity,
            EstimatedDaysToSell = NudgeDaysToSell(assessment.EstimatedDaysToSell, activeComparableCount, candidatePrice, medianActivePrice),
        };

        if (activeComparableCount <= 0)
        {
            // Sold history exists but nothing is currently listed — the sell-through DENOMINATOR is
            // missing, so we cannot honestly report a rate. Do NOT claim "100% / Excellent" off a
            // non-existent denominator (that made zero-competition noise masquerade as a top
            // opportunity). Surface it as unverified/low-confidence instead. RateIsUnbounded lets
            // the UI render "—" rather than a fake percentage.
            analysis.RateIsUnbounded = soldCount > 0;
            analysis.SellThroughRate = null;
            analysis.SellThroughScore = soldCount > 0 ? 40 : 0;
            analysis.Interpretation = soldCount > 0 ? "Unverified — no active comps to measure against" : "Unknown";
            return analysis;
        }

        var ratePercent = Math.Round((decimal)soldCount / activeComparableCount * 100m, 1);
        analysis.SellThroughRate = ratePercent;
        analysis.SellThroughScore = ScoreFor(ratePercent);
        analysis.Interpretation = InterpretationFor(ratePercent);
        return analysis;
    }

    private static int? NudgeDaysToSell(int? baseDays, int activeComparableCount, decimal? candidatePrice, decimal? medianActivePrice)
    {
        if (baseDays is not int days) return null;

        var multiplier = 1.0;
        if (medianActivePrice is decimal med && med > 0 && candidatePrice is decimal price)
            multiplier *= price <= med ? 0.85 : 1.15; // priced at/below the going rate sells faster

        multiplier *= activeComparableCount switch
        {
            > 30 => 1.2,             // crowded market — more competing listings, slower to sell
            > 0 and <= 3 => 0.85,    // very little competition — sells faster
            _ => 1.0,
        };

        return Math.Max(1, (int)Math.Round(days * multiplier));
    }

    private static int ScoreFor(decimal ratePercent)
    {
        if (ratePercent <= ScoreCurve[0].Pct) return ScoreCurve[0].Score;
        if (ratePercent >= ScoreCurve[^1].Pct) return 100;

        for (var i = 1; i < ScoreCurve.Length; i++)
        {
            if (ratePercent > ScoreCurve[i].Pct) continue;
            var (loPct, loScore) = ScoreCurve[i - 1];
            var (hiPct, hiScore) = ScoreCurve[i];
            var fraction = (ratePercent - loPct) / (hiPct - loPct);
            return (int)Math.Round(loScore + (hiScore - loScore) * fraction);
        }
        return 100;
    }

    private static string InterpretationFor(decimal ratePercent) => ratePercent switch
    {
        >= 100 => "Excellent",
        >= 60 => "Very Strong",
        >= 35 => "Good",
        >= 15 => "Moderate",
        >= 5 => "Weak",
        _ => "Poor",
    };
}
