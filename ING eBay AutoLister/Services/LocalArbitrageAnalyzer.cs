using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// The resale half of one arbitrage row, flattened out of whatever priced it — the hosted
// sold-comps database, Terapeak, or a blend of the two (MarketPriceEstimator decides the
// weighting; this just carries the answer). Kept as its own small type so the profit math
// below is testable without constructing a whole MarketAnalysisResult.
public sealed class ResalePricing
{
    public string LookupTitle { get; set; } = "";
    public decimal? Median { get; set; }
    public decimal? ExpectedSale { get; set; }
    public decimal? QuickSale { get; set; }
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public decimal SoldCompWeightPercent { get; set; }
    public decimal TerapeakWeightPercent { get; set; }
    // What buyers paid for shipping on the matched comps. Booked as revenue AND as cost
    // (see LocalArbitrageAnalyzer.Build) rather than as either one alone.
    public decimal AvgCompShipping { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public string? DisagreementMessage { get; set; }
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";

    public bool HasPrice => ExpectedSale is > 0 || Median is > 0;

    public static ResalePricing From(MarketAnalysisResult analysis, string lookupTitle)
    {
        var comps = analysis.TopSoldComparables;
        return new ResalePricing
        {
            LookupTitle = lookupTitle,
            Median = analysis.PriceEstimate.MedianPrice,
            ExpectedSale = analysis.PriceEstimate.ExpectedSalePrice,
            QuickSale = analysis.PriceEstimate.QuickSalePrice,
            SoldCompCount = analysis.Sources.LocalComparableCount,
            TerapeakCompCount = analysis.Sources.TerapeakComparableCount,
            SoldCompWeightPercent = Math.Round(analysis.Sources.LocalWeightPercent, 0),
            TerapeakWeightPercent = Math.Round(analysis.Sources.TerapeakWeightPercent, 0),
            AvgCompShipping = comps.Count > 0 ? Math.Round(comps.Average(c => c.Shipping), 2) : 0m,
            ConfidenceScore = analysis.Confidence.Score,
            ConfidenceLevel = analysis.Confidence.Level,
            DisagreementMessage = analysis.PriceEstimate.DisagreementMessage,
            LiquidityScore = analysis.SellThrough.LiquidityScore,
            LiquidityLevel = analysis.SellThrough.LiquidityLevel,
        };
    }
}

// Several Marketplace tiles for the same product share one comp lookup: the resale side is a
// property of the product, not of who is selling it locally, and pricing five listings of the
// same drill five times would spend five times the lookups for one answer.
public sealed class LocalArbitrageGroup
{
    public string Key { get; set; } = "";
    // The fullest title in the group — Marketplace titles for the same item range from
    // "Antminer S19j Pro 104TH miner" to "miner", and the comp matcher can only work with
    // what it's given.
    public string LookupTitle { get; set; } = "";
    public List<FacebookMarketplaceListing> Listings { get; set; } = [];

    public decimal LowestAsk => Listings.Where(l => l.Price is > 0).Select(l => l.Price!.Value)
        .DefaultIfEmpty(0m).Min();
}

/// <summary>
/// Ranks local Marketplace supply by what it's actually worth flipping: net profit after eBay
/// fees, ROI and margin against real sold data, not the gross spread the per-card check shows.
///
/// Everything here is pure except <see cref="Build"/>, which delegates the money to the shared
/// <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/> so a local flip is costed by exactly
/// the same rules as a dropship or supplier-file item.
/// </summary>
public sealed class LocalArbitrageAnalyzer(ProfitCalculator profitCalc)
{
    // A "goldmine" has to be earned on both axes — a big multiple AND enough sold history to
    // believe it. Thin data gets the honest label instead of the green badge.
    private const decimal GoldmineRoiPercent = 75m;
    private const decimal GoldmineProfit = 75m;
    private const int GoldmineMinComps = 5;
    private const int GoldmineMinConfidence = 50;
    private const decimal SolidRoiPercent = 30m;
    private const decimal SolidProfit = 25m;
    // Below this the sold history is too sparse to call anything, however good the arithmetic.
    private const int ThinCompCount = 3;

    public LocalArbitrageOpportunity Build(FacebookMarketplaceListing listing, ResalePricing? resale, FeeProfile fees)
    {
        var localAsk = listing.Price ?? 0m;
        var row = new LocalArbitrageOpportunity
        {
            ItemId = listing.ItemId,
            Title = listing.Title,
            Url = listing.Url,
            ImageUrl = listing.ImageUrl,
            LocalAsk = localAsk,
            OriginalPrice = listing.OriginalPrice,
            Location = listing.Location,
            DistanceMiles = listing.DistanceMiles,
            PostedAgo = listing.PostedAgo,
        };

        if (resale is null || !resale.HasPrice)
        {
            row.Verdict = "no_data";
            row.VerdictNote = "No eBay sold history matched this title.";
            row.PricedAs = resale?.LookupTitle ?? "";
            return row;
        }

        row.PricedAs = resale.LookupTitle;
        row.EbayResaleMedian = resale.Median;
        row.EbayExpectedSale = resale.ExpectedSale;
        row.EbayQuickSale = resale.QuickSale;
        row.SoldCompCount = resale.SoldCompCount;
        row.TerapeakCompCount = resale.TerapeakCompCount;
        row.SoldCompWeightPercent = resale.SoldCompWeightPercent;
        row.TerapeakWeightPercent = resale.TerapeakWeightPercent;
        row.ResaleSource = SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount);
        row.ConfidenceScore = resale.ConfidenceScore;
        row.ConfidenceLevel = resale.ConfidenceLevel;
        row.DisagreementMessage = resale.DisagreementMessage;
        row.LiquidityScore = resale.LiquidityScore;
        row.LiquidityLevel = resale.LiquidityLevel;

        var expected = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median!.Value;

        // Shipping is booked on both sides: buyers paid it (revenue, and eBay charges its final
        // value fee on it) and it costs the seller the same amount to actually ship. Booking it
        // on one side only is how a profit estimate ends up either inflated or double-charged.
        // When the comps sold with free shipping there is no observed figure, so this falls back
        // to FeeProfile.DefaultShippingCost like every other profit path in the app.
        var profit = profitCalc.Calculate(
            supplierUnitCost: localAsk, quantity: 1, expectedSalePrice: expected,
            quickSalePrice: resale.QuickSale ?? expected,
            buyerPaidShipping: resale.AvgCompShipping, fees: fees,
            actualShippingCostOverride: resale.AvgCompShipping > 0 ? resale.AvgCompShipping : null);

        row.EstimatedFees = Math.Round(profit.EbayFees + profit.PromotedListingFees + profit.OtherCosts, 2);
        row.EstimatedShipCost = Math.Round(profit.ActualShippingCost + profit.PackagingCost + profit.LaborCost, 2);
        row.NetProfit = profit.NetProfitPerUnit;
        row.RoiPercent = profit.RoiPercent;
        row.MarginPercent = profit.MarginPercent;
        // Net profit falls exactly one dollar for every dollar more paid locally, so the
        // break-even ask is the ask plus the profit — the number to negotiate against.
        row.MaxBuyPrice = Math.Round(localAsk + profit.NetProfitPerUnit, 2);

        var (verdict, note) = Judge(profit.NetProfitPerUnit, profit.RoiPercent, localAsk,
            resale.SoldCompCount + resale.TerapeakCompCount, resale.ConfidenceScore);
        row.Verdict = verdict;
        row.VerdictNote = note;
        return row;
    }

    // Which pricing sources actually contributed — "hosted comps + Terapeak" is a materially
    // stronger claim than either alone, so the row says which one it is rather than "eBay data".
    public static string SourceLabel(int soldCompCount, int terapeakCount) =>
        (soldCompCount > 0, terapeakCount > 0) switch
        {
            (true, true) => "hosted_comps+terapeak",
            (true, false) => "hosted_comps",
            (false, true) => "terapeak",
            _ => "none",
        };

    // Pure verdict tiering. A free item has no cost basis so ROI is undefined rather than zero —
    // treated as unbounded, which is what "free" actually means for a flip.
    public static (string Verdict, string Note) Judge(
        decimal netProfit, decimal? roiPercent, decimal localAsk, int compCount, int confidenceScore)
    {
        if (netProfit <= 0)
            return ("pass", $"Sells for less than the ${localAsk:0.##} ask once fees are paid.");

        var roi = roiPercent ?? (localAsk <= 0 ? decimal.MaxValue : 0m);

        if (compCount < ThinCompCount)
            return ("thin", $"Profitable on {compCount} sold comp{(compCount == 1 ? "" : "s")} — too few to trust yet.");

        if (roi >= GoldmineRoiPercent && netProfit >= GoldmineProfit
            && compCount >= GoldmineMinComps && confidenceScore >= GoldmineMinConfidence)
            return ("goldmine", $"${netProfit:0.##} net on a ${localAsk:0.##} buy, backed by {compCount} sold comps.");

        if (roi >= SolidRoiPercent && netProfit >= SolidProfit)
            return ("solid", $"${netProfit:0.##} net after fees ({roi:0}% ROI).");

        return ("thin", confidenceScore < GoldmineMinConfidence
            ? $"${netProfit:0.##} net, but the sold data behind it is weak."
            : $"${netProfit:0.##} net — real, but a thin margin for the drive.");
    }

    // One comp lookup per product, not per tile. Keyed by the caller (a normalized product
    // signature), with the fullest title in each group used for the lookup.
    public static List<LocalArbitrageGroup> GroupByProduct(
        IEnumerable<FacebookMarketplaceListing> listings, Func<FacebookMarketplaceListing, string> keySelector)
    {
        var groups = new List<LocalArbitrageGroup>();
        var byKey = new Dictionary<string, LocalArbitrageGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var listing in listings)
        {
            if (string.IsNullOrWhiteSpace(listing.Title)) continue;
            // A blank key means the normalizer found nothing to key on — fall back to the title
            // so those listings stay separate instead of collapsing into one bogus group.
            var key = keySelector(listing);
            if (string.IsNullOrWhiteSpace(key)) key = listing.Title.Trim().ToLowerInvariant();

            if (!byKey.TryGetValue(key, out var group))
            {
                group = new LocalArbitrageGroup { Key = key, LookupTitle = listing.Title };
                byKey[key] = group;
                groups.Add(group);
            }
            group.Listings.Add(listing);
            if (listing.Title.Length > group.LookupTitle.Length) group.LookupTitle = listing.Title;
        }

        return groups;
    }

    // Rations the Terapeak scrape budget across products, because a real scrape is a browser
    // page load against a logged-in session — never something to spend once per search result.
    //
    // Corroborating a promising estimate comes first (that's the row someone is about to spend
    // money on); products the comps database couldn't price at all come second, biggest local
    // ask first, since an unpriced $900 item is worth a look and an unpriced $20 one is not.
    public static List<string> SelectScrapeTargets(
        IEnumerable<(string Key, decimal? PreliminaryProfit, bool HasTerapeak, decimal LocalAsk)> groups, int budget)
    {
        if (budget <= 0) return [];

        var candidates = groups.Where(g => !g.HasTerapeak).ToList();

        var promising = candidates
            .Where(g => g.PreliminaryProfit is > 0)
            .OrderByDescending(g => g.PreliminaryProfit!.Value)
            .Select(g => g.Key);

        var unpriced = candidates
            .Where(g => g.PreliminaryProfit is null)
            .OrderByDescending(g => g.LocalAsk)
            .Select(g => g.Key);

        return promising.Concat(unpriced).Take(budget).ToList();
    }

    // Best money first. Rows that couldn't be priced sort last rather than being dropped —
    // "we couldn't price this one" is information, and silently hiding listings from a
    // sourcing search is how a real deal gets missed.
    public static List<LocalArbitrageOpportunity> Rank(IEnumerable<LocalArbitrageOpportunity> rows) =>
        rows.OrderByDescending(r => r.NetProfit.HasValue)
            .ThenByDescending(r => r.NetProfit ?? 0m)
            .ThenByDescending(r => r.RoiPercent ?? 0m)
            .ThenBy(r => r.DistanceMiles ?? double.MaxValue)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
