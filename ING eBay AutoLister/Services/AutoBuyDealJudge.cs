using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Decides whether a candidate listing is a genuine, profitable deal before Auto-Buy is allowed to
/// spend on it — the difference between an auto-buyer that grabs anything under a number and one that
/// only takes flips that actually make money.
/// </summary>
/// <remarks>
/// <para>Two gates, both explainable:</para>
/// <list type="number">
///   <item><b>Red flags.</b> The title and condition are read for the words that mean "not the clean
///     working unit you think you're buying" — for parts, not working, as-is, cracked, untested. Some
///     are hard stops; the rest drop a clean Buy to Caution so a person looks first.</item>
///   <item><b>The money.</b> The item is priced against real eBay <i>sold</i> comps: the median sold
///     price is what it should fetch again, minus eBay + payment fees, minus the buy price, is the
///     profit. Below the comp-count floor it can never say Buy — one lucky sale is not a market.</item>
/// </list>
/// <para>The core decision is a pure function of (item, comps, thresholds), so it is fully testable
/// with no network. The instance method just fetches the comps first.</para>
/// </remarks>
public sealed class AutoBuyDealJudge(EbayService ebay, IMarketplaceRepository marketplace)
{
    /// <summary>eBay final value + payment processing, blended. A safe default across most categories.</summary>
    public const decimal FeeRate = 0.1325m;

    /// <summary>The per-order fixed fee eBay adds on top of the percentage.</summary>
    public const decimal FeeFixed = 0.40m;

    /// <summary>Fewer sold comps than this and the judge will not commit to Buy — the market is unproven.</summary>
    public const int MinCompsToTrust = 3;

    /// <summary>Default floor: a flip worth less than this in cash is not worth the handling.</summary>
    public const decimal DefaultMinNetProfit = 15m;

    /// <summary>Default floor: below this return on the money, the capital is better left in the account.</summary>
    public const decimal DefaultMinRoiPercent = 20m;

    /// <summary>Hard stops — a title with one of these is almost never the working unit a rule wants.</summary>
    public static readonly string[] HardRedFlags =
        ["for parts", "not working", "parts only", "parts/repair", "does not work", "doesn't work",
         "won't power", "not functional", "non working", "non-working", "as-is", "as is", "salvage",
         "broken", "dead", "defective"];

    /// <summary>Soft flags — reason to look, not to refuse: a clean Buy becomes a Caution.</summary>
    public static readonly string[] SoftRedFlags =
        ["untested", "read description", "read carefully", "please read", "cracked", "damaged",
         "no returns", "sold as is", "repair", "faulty", "issue", "spares"];

    /// <summary>
    /// Fetches sold comps for the item, then judges it. The query prefers the rule's own search so
    /// comps line up with what the rule is hunting; it falls back to the listing title.
    /// </summary>
    public async Task<AutoBuyJudgment> JudgeAsync(
        EbayOpportunityItem item, string? ruleQuery, decimal minNetProfit, decimal minRoiPercent,
        CancellationToken ct = default)
    {
        var query = !string.IsNullOrWhiteSpace(ruleQuery) ? ruleQuery! : item.Title;
        var prices = new List<decimal>();

        // Live sold search first — freshest when it is up.
        try
        {
            var live = await ebay.SearchSoldCompsAsync(query);
            if (live?.Items is { Count: > 0 } items)
                prices.AddRange(items.Select(c => c.Price).Where(p => p > 0));
        }
        catch { /* live source down — the stored history below carries it */ }

        // The seller's own stored sold-comps database — the same source /api/sold-comps blends in.
        // It keeps the judge seeing a market when the live eBay endpoint is refusing requests; without
        // it, a down live source makes every candidate look unpriceable.
        try
        {
            var stored = await marketplace.SearchByKeywordAsync(query, limit: 24, ct: ct);
            prices.AddRange(stored.Select(c => c.SoldPrice).Where(p => p > 0));
        }
        catch { /* stored repo unavailable — fall through with whatever the live source gave */ }

        var comps = prices.Count > 0
            ? new SoldCompsResult { Median = Median(prices), Average = prices.Average(), Count = prices.Count }
            : null;
        return Judge(item, comps, minNetProfit, minRoiPercent);
    }

    /// <summary>
    /// The median of a set of sold prices. The judge values against the median, not the average, so
    /// one $5,000 outlier in a pile of $300 sales cannot make a $300 item look like a windfall.
    /// </summary>
    public static decimal Median(IReadOnlyList<decimal> prices)
    {
        if (prices.Count == 0) return 0m;
        var sorted = prices.OrderBy(p => p).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : Math.Round((sorted[mid - 1] + sorted[mid]) / 2m, 2);
    }

    /// <summary>
    /// The whole decision, pure. No network, no clock — a test hands it an item, a set of comps and the
    /// thresholds and gets back the same verdict the live path would.
    /// </summary>
    public static AutoBuyJudgment Judge(
        EbayOpportunityItem item, SoldCompsResult? comps, decimal minNetProfit, decimal minRoiPercent)
    {
        var price = item.Price;
        var flags = RedFlagsIn($"{item.Title} {item.Condition}");
        var hardStop = flags.Any(f => HardRedFlags.Contains(f, StringComparer.OrdinalIgnoreCase));

        var count = comps?.Count ?? 0;
        var resale = comps?.Median ?? 0m;
        var fees = resale > 0 ? Math.Round(resale * FeeRate + FeeFixed, 2) : 0m;
        var net = resale > 0 ? Math.Round(resale - fees - price, 2) : 0m;
        var roi = price > 0 && resale > 0 ? Math.Round(net / price * 100m, 1) : 0m;

        // A hard red flag ends it regardless of the math — a "for parts" unit priced as a deal is a
        // trap, not a deal.
        if (hardStop)
            return new AutoBuyJudgment(AutoBuyVerdict.Skip, price, count, resale, fees, net, roi, flags,
                $"Skip: the listing says \"{flags.First(f => HardRedFlags.Contains(f, StringComparer.OrdinalIgnoreCase))}\".");

        // No market to price against — never Buy on that.
        if (count < MinCompsToTrust)
            return new AutoBuyJudgment(AutoBuyVerdict.Caution, price, count, resale, fees, net, roi, flags,
                count == 0
                    ? "Caution: no sold comps found, so the resale value is unknown."
                    : $"Caution: only {count} sold comp(s) — too thin to trust the price.");

        // The money didn't clear the floors.
        if (net < minNetProfit || roi < minRoiPercent)
            return new AutoBuyJudgment(AutoBuyVerdict.Skip, price, count, resale, fees, net, roi, flags,
                $"Skip: resells around {resale:C0}, leaving {net:C0} ({roi:0}% ROI) after fees — under your {minNetProfit:C0}/{minRoiPercent:0}% floor.");

        // Profitable, but a soft flag says a person should glance first.
        if (flags.Count > 0)
            return new AutoBuyJudgment(AutoBuyVerdict.Caution, price, count, resale, fees, net, roi, flags,
                $"Caution: {net:C0} profit ({roi:0}% ROI) looks good, but the listing says \"{flags[0]}\" — worth a look.");

        return new AutoBuyJudgment(AutoBuyVerdict.Buy, price, count, resale, fees, net, roi, flags,
            $"Buy: resells around {resale:C0}, about {net:C0} profit ({roi:0}% ROI) after fees, on {count} sold comps.");
    }

    /// <summary>Every trouble word present in the text, in the order hard-then-soft, de-duplicated.</summary>
    public static IReadOnlyList<string> RedFlagsIn(string text)
    {
        var hay = (text ?? "").ToLowerInvariant();
        var found = new List<string>();
        foreach (var w in HardRedFlags.Concat(SoftRedFlags))
            if (hay.Contains(w) && !found.Contains(w)) found.Add(w);
        return found;
    }
}
