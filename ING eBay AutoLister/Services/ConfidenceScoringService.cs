using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// A genuinely separate 0-100 confidence score — how much to trust the price/profit/opportunity
// numbers, as distinct from how good the opportunity itself looks. Never folded into
// OpportunityScoringService's output; when local and Terapeak disagree, this is what surfaces
// "both estimates shown, confidence lowered" instead of the caller silently picking one.
//
// The score is a weighted sum of the seven terms below and nothing else, and every one of them is
// reported back with the points it earned (ConfidenceBreakdown.Factors). A confidence number that
// cannot be taken apart is worth about as much as no number at all: it is shown beside a profit
// figure somebody is about to spend real money against, and "62" on its own gives them nothing to
// check, nothing to argue with and nothing to act on.
public sealed class ConfidenceScoringService
{
    // The seven terms and what each is worth. They total exactly 100 — asserted in the tests, since
    // a term added without adjusting the rest would silently make a full-marks item score 108.
    public const double CompsPoints       = 25;
    public const double IdentifierPoints  = 20;
    public const double ModelPoints       = 15;
    public const double AgreementPoints   = 15;
    public const double RecencyPoints     = 10;
    public const double SpreadPoints      = 10;
    public const double ConsistencyPoints = 5;

    // Comps and identifier/model matches stop earning here. Stated as constants because the panel
    // quotes them back at the seller ("full marks at 10") and a bar drawn against a different
    // ceiling than the one the arithmetic uses is worse than no bar.
    public const int CompsForFullMarks       = 10;
    public const int IdentifiersForFullMarks = 3;
    public const int ModelsForFullMarks      = 3;

    public ConfidenceBreakdown Score(
        MarketAnalysisResult result, int strongComparableCount, int exactIdentifierMatches,
        int modelNumberMatches, int? mostRecentComparableAgeDays,
        bool conditionConsistent, bool quantityConsistent, bool categoryConsistent)
    {
        var reasons = new List<string>();
        var factors = new List<ConfidenceFactor>();
        double points = 0;

        var comparablePoints = Math.Min(strongComparableCount, CompsForFullMarks) / (double)CompsForFullMarks * CompsPoints;
        points += comparablePoints;
        reasons.Add(strongComparableCount >= 5
            ? $"{strongComparableCount} strong comparables"
            : $"Only {strongComparableCount} strong comparable(s)");
        factors.Add(Factor("comps", "Comparable sales", comparablePoints, CompsPoints,
            strongComparableCount switch
            {
                0 => "No sold comp matched this item well enough to price it",
                1 => "1 strong comparable sale",
                _ => $"{strongComparableCount} strong comparable sales",
            },
            strongComparableCount >= CompsForFullMarks ? null
                : $"{CompsForFullMarks} strong comparable sales earn full marks here; this has {strongComparableCount}."));

        var identifierPoints = Math.Min(exactIdentifierMatches, IdentifiersForFullMarks) / (double)IdentifiersForFullMarks * IdentifierPoints;
        points += identifierPoints;
        if (exactIdentifierMatches > 0) reasons.Add($"{exactIdentifierMatches} exact-identifier match(es)");
        factors.Add(Factor("identifier", "Exact identifier matches", identifierPoints, IdentifierPoints,
            exactIdentifierMatches > 0
                ? $"{exactIdentifierMatches} comp{(exactIdentifierMatches == 1 ? "" : "s")} matched on a part number or catalog id"
                : "No comp matched on a part number or catalog id",
            exactIdentifierMatches >= IdentifiersForFullMarks ? null
                : "Comps carrying this item's own part number are the strongest evidence there is — " +
                  "a fuller title, part number included, finds more of them."));

        var modelPoints = Math.Min(modelNumberMatches, ModelsForFullMarks) / (double)ModelsForFullMarks * ModelPoints;
        points += modelPoints;
        if (modelNumberMatches > 0) reasons.Add($"{modelNumberMatches} exact model-number match(es)");
        factors.Add(Factor("model", "Model-number matches", modelPoints, ModelPoints,
            modelNumberMatches > 0
                ? $"{modelNumberMatches} comp{(modelNumberMatches == 1 ? "" : "s")} matched on the exact model"
                : "No comp matched on the exact model number",
            modelNumberMatches >= ModelsForFullMarks ? null
                : "Nothing sold recently under this exact model number, so the price rests on close relatives."));

        double agreementPoints;
        string agreementDetail;
        string? agreementLever;
        var hasLocal = result.SellThrough.SoldComparableCount > 0;
        var hasTerapeak = result.PriceEstimate.TerapeakWeight > 0;
        if (result.PriceEstimate.MarketDataDisagreement)
        {
            agreementPoints = 0;
            reasons.Add("Local sold-history and Terapeak medians disagree by more than 20%");
            agreementDetail = "The two sold-data sources disagree by more than 20%";
            agreementLever = "Both estimates are shown rather than one being picked — read the comps yourself " +
                             "before trusting either end of that range.";
        }
        else if (hasLocal && hasTerapeak)
        {
            agreementPoints = AgreementPoints;
            reasons.Add("Local sold-history and Terapeak data agree");
            agreementDetail = "Sold-comps database and Terapeak agree on the price";
            agreementLever = null;
        }
        else
        {
            // Half marks, near enough: one source that nothing contradicts is neither corroborated
            // nor in doubt, and scoring it as agreement would let a lone source certify itself.
            agreementPoints = 7;
            agreementDetail = hasTerapeak
                ? "Priced by Terapeak alone — no second source to check it against"
                : hasLocal
                    ? "Priced by the sold-comps database alone — no second source to check it against"
                    : "Neither sold-data source could price this item";
            agreementLever = "A second source has to agree before this term is full — connect Terapeak, or " +
                             "scan again once the comps database has more of this category.";
        }
        points += agreementPoints;
        factors.Add(Factor("agreement", "Source agreement", agreementPoints, AgreementPoints, agreementDetail, agreementLever));

        var recencyPoints = mostRecentComparableAgeDays switch
        {
            null => 0.0,
            <= 30 => 10.0,
            <= 90 => 6.0,
            <= 180 => 3.0,
            _ => 1.0,
        };
        points += recencyPoints;
        if (mostRecentComparableAgeDays is > 90) reasons.Add("Most recent comparable is over 90 days old");
        factors.Add(Factor("recency", "How recent the sales are", recencyPoints, RecencyPoints,
            mostRecentComparableAgeDays switch
            {
                null => "No comp carries a sale date",
                0 => "One of these sold today",
                1 => "Most recent sale was yesterday",
                var d => $"Most recent sale was {d} days ago",
            },
            recencyPoints >= RecencyPoints ? null
                : mostRecentComparableAgeDays is null
                    ? "Undated comps can't be checked for staleness — the price behind them may be a year old."
                    : "A sale inside the last 30 days earns full marks; this market has had time to move since."));

        var variancePoints = result.Stability.StabilityScore / 100.0 * SpreadPoints;
        points += variancePoints;
        if (result.Stability.StabilityScore < 40) reasons.Add("Wide price spread among comparables");
        factors.Add(Factor("spread", "How tightly the comps agree", variancePoints, SpreadPoints,
            result.Stability.StabilityScore switch
            {
                >= 80 => $"Comps sold in a narrow band (stability {result.Stability.StabilityScore}/100)",
                >= 40 => $"Comps are somewhat scattered (stability {result.Stability.StabilityScore}/100)",
                _ => $"Comps are all over the place (stability {result.Stability.StabilityScore}/100)",
            },
            result.Stability.StabilityScore >= 100 ? null
                : "The same item sells for very different amounts, so any single figure is a midpoint " +
                  "rather than the price."));

        var consistencyCount = (conditionConsistent ? 1 : 0) + (quantityConsistent ? 1 : 0) + (categoryConsistent ? 1 : 0);
        var consistencyPoints = consistencyCount / 3.0 * ConsistencyPoints;
        points += consistencyPoints;
        if (consistencyCount < 3) reasons.Add("Condition, quantity, or category inconsistency among comparables");
        factors.Add(Factor("consistency", "Like-for-like comps", consistencyPoints, ConsistencyPoints,
            consistencyCount == 3
                ? "Comps match this item on condition, quantity and category"
                : "Comps differ from this item in condition, quantity or category",
            consistencyCount == 3 ? null
                : "Some of these comps are a different condition, quantity or category than the item."));

        var score = (int)Math.Round(Math.Clamp(points, 0, 100));
        return new ConfidenceBreakdown
        {
            Score = score,
            Level = score switch
            {
                >= 85 => "High Confidence",
                >= 65 => "Good Confidence",
                >= 40 => "Limited Confidence",
                _ => "Insufficient Evidence",
            },
            Reasons = reasons,
            Factors = factors,
            BiggestGap = BiggestGap(factors),
        };
    }

    private static ConfidenceFactor Factor(
        string key, string label, double earned, double possible, string detail, string? lever) =>
        new(key, label, Math.Round(Math.Clamp(earned, 0, possible), 2), possible, detail, lever);

    /// <summary>
    /// The term that lost the most points, phrased as the thing that would win them back. Null when
    /// nothing material is missing.
    /// </summary>
    /// <remarks>
    /// Ranked by points lost rather than by how bad each term looks on its own: a term worth five
    /// points cannot be the headline while twenty are missing somewhere else, however red it is.
    /// Ties go to the earlier term, which is the more valuable one — the list is in weight order.
    /// </remarks>
    public static string? BiggestGap(IReadOnlyList<ConfidenceFactor> factors)
    {
        ConfidenceFactor? worst = null;
        var worstGap = 0.5; // sub-half-point gaps are rounding, not a finding
        foreach (var f in factors)
        {
            if (f.Lever is null) continue;
            var gap = f.Possible - f.Earned;
            if (gap > worstGap) { worstGap = gap; worst = f; }
        }
        return worst is null ? null : $"{worst.Detail}. {worst.Lever}";
    }
}
