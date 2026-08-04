using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// eBay's US Store subscription rate card, and the arithmetic of what a month on each tier costs.
/// </summary>
/// <remarks>
/// <para>
/// A rate card in code is a liability the day eBay changes it, so two things are true of this one:
/// it is small enough to check at a glance, and it is served to the screen in full
/// (<c>/api/store-plan/rates</c>) rather than hidden behind a recommendation. A seller who can see
/// the three numbers a tier is being judged on can tell in ten seconds whether they are still
/// eBay's numbers. A seller shown only "switch to Basic" cannot.
/// </para>
/// <para>
/// Only three fields per tier are used to decide anything: the subscription, the free-listing
/// allotment, and the insertion fee charged past it. Final value fees are deliberately absent —
/// they are set by category, eBay publishes no per-account rate, and folding a guess at them into
/// this comparison would swing the recommendation on a figure nobody can audit.
/// </para>
/// </remarks>
public static class StorePlanCatalog
{
    /// <summary>
    /// eBay's published US Store tiers, ordered by allotment. The order matters: it is the order the
    /// screen renders, and it is what makes the ladder below a ladder rather than a set.
    /// </summary>
    public static IReadOnlyList<StorePlanTier> Tiers { get; } =
    [
        new()
        {
            Key = "none", Name = "No store",
            MonthlyBilling = 0m, AnnualBilling = 0m,
            FreeListings = 250, InsertionFeeAfter = 0.35m,
            Unlocks = "eBay's standard zero-insertion-fee allotment, which every seller gets without paying for anything.",
        },
        new()
        {
            Key = "starter", Name = "Starter Store",
            MonthlyBilling = 7.95m, AnnualBilling = 4.95m,
            FreeListings = 250, InsertionFeeAfter = 0.30m,
            Unlocks = "A store name and a storefront page. The allotment is the same 250 you already get, so this tier only pays for itself past it.",
        },
        new()
        {
            Key = "basic", Name = "Basic Store",
            MonthlyBilling = 27.95m, AnnualBilling = 21.95m,
            FreeListings = 1_000, InsertionFeeAfter = 0.25m,
            // The one non-fee unlock this app can speak to first-hand: its own Terapeak comps path
            // gets served eBay's subscription wall on accounts below this tier.
            Unlocks = "The first tier that raises the allotment — and the tier at which eBay stops serving this app's Terapeak research a subscription wall.",
        },
        new()
        {
            Key = "premium", Name = "Premium Store",
            MonthlyBilling = 74.95m, AnnualBilling = 59.95m,
            FreeListings = 10_000, InsertionFeeAfter = 0.10m,
            Unlocks = "Ten times the allotment and the insertion fee drops to a dime. Built for sellers whose catalogue, not whose sales, is the big number.",
        },
        new()
        {
            Key = "anchor", Name = "Anchor Store",
            MonthlyBilling = 349.95m, AnnualBilling = 299.95m,
            FreeListings = 25_000, InsertionFeeAfter = 0.05m,
            Unlocks = "A nickel a listing past 25,000. Below roughly 25,000 listings this is a large monthly bill buying an allotment you are not using.",
        },
        new()
        {
            Key = "enterprise", Name = "Enterprise Store",
            MonthlyBilling = null, AnnualBilling = 2_999.95m,
            FreeListings = 100_000, InsertionFeeAfter = 0.05m,
            Unlocks = "Annual commitment only. The same nickel as Anchor, so this tier buys allotment and nothing else.",
        },
    ];

    /// <summary>Wording the screen carries beside every figure. Rates go stale; this says so.</summary>
    public const string RatesNote =
        "These are eBay's published US Store prices — the subscription, the free-listing allotment, and "
        + "the insertion fee past it. Nothing here reads your eBay account's own fee schedule, and eBay "
        + "changes these, so check them on eBay's fee pages before you switch.";

    /// <summary>The largest listing count the ladder is worked out over — Enterprise's allotment, plus room.</summary>
    public const int LadderCeiling = 110_000;

    public static StorePlanTier? Find(string? key) =>
        Tiers.FirstOrDefault(t => string.Equals(t.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The tier a key names, or "no store" — the only safe default, since it is what a seller who has never chosen is on.</summary>
    public static StorePlanTier Resolve(string? key) => Find(key) ?? Tiers[0];

    /// <summary>
    /// What the tier's subscription costs per month on the chosen cycle. A tier eBay sells annually
    /// only is priced at its annual rate either way, and flagged, rather than excluded — a seller
    /// paying monthly still deserves to be told the tier exists.
    /// </summary>
    public static decimal Subscription(StorePlanTier tier, bool annualBilling) =>
        annualBilling ? tier.AnnualBilling : tier.MonthlyBilling ?? tier.AnnualBilling;

    /// <summary>Listings past the allotment — the only ones that carry an insertion fee.</summary>
    public static int ListingsCharged(StorePlanTier tier, int listingsPerMonth) =>
        Math.Max(0, listingsPerMonth - tier.FreeListings);

    /// <summary>The whole month: subscription plus insertion fees on the overage.</summary>
    public static decimal MonthlyCost(StorePlanTier tier, int listingsPerMonth, bool annualBilling) =>
        Subscription(tier, annualBilling)
        + ListingsCharged(tier, listingsPerMonth) * tier.InsertionFeeAfter;

    /// <summary>
    /// The band of listing counts over which each tier is the cheapest one to be on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the half of the feature that keeps working after the seller has switched: it says
    /// where the next change is, so growing into a worse plan is something they see coming rather
    /// than something they find in a statement.
    /// </para>
    /// <para>
    /// Worked out by walking every listing count to <see cref="LadderCeiling"/> and asking which
    /// tier is cheapest, rather than by solving the crossovers algebraically. Each tier's cost is
    /// linear with a kink at its allotment, so the algebra is doable — but it is the kind of doable
    /// that is wrong in one edge case nobody notices for a year, and the answer here does not depend
    /// on the seller, so it is computed once per billing cycle and held. Ties go to the tier earlier
    /// in the list, which is the one with the smaller commitment.
    /// </para>
    /// </remarks>
    public static IReadOnlyDictionary<string, (int From, int? To)> Ladder(bool annualBilling) =>
        annualBilling ? AnnualLadder.Value : MonthlyLadder.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, (int From, int? To)>> AnnualLadder =
        new(() => BuildLadder(true));

    private static readonly Lazy<IReadOnlyDictionary<string, (int From, int? To)>> MonthlyLadder =
        new(() => BuildLadder(false));

    private static IReadOnlyDictionary<string, (int From, int? To)> BuildLadder(bool annualBilling)
    {
        var bands = new Dictionary<string, (int From, int? To)>(StringComparer.OrdinalIgnoreCase);

        for (var n = 0; n <= LadderCeiling; n++)
        {
            var winner = Cheapest(n, annualBilling);

            // First and last, not first-run-only: a tier that won a band, lost it and won it back
            // would otherwise be reported as ending where it first lost. The rate card as it stands
            // never does that — a tier with a bigger allotment and a smaller per-listing fee can
            // only ever overtake, never fall back — but the band is what the seller plans against,
            // so it is derived rather than assumed.
            bands[winner.Key] = bands.TryGetValue(winner.Key, out var seen) ? (seen.From, n) : (n, n);
        }

        // The last band has no ceiling: past it the seller is simply on the biggest tier there is.
        var top = bands.MaxBy(b => b.Value.From).Key;
        bands[top] = (bands[top].From, null);

        return bands;
    }

    /// <summary>The cheapest tier at a given listing count. Ties go to the smaller commitment.</summary>
    public static StorePlanTier Cheapest(int listingsPerMonth, bool annualBilling)
    {
        var winner = Tiers[0];
        var best = MonthlyCost(winner, listingsPerMonth, annualBilling);

        foreach (var tier in Tiers.Skip(1))
        {
            var cost = MonthlyCost(tier, listingsPerMonth, annualBilling);
            if (cost >= best) continue;   // ties keep the earlier, smaller-commitment tier
            winner = tier;
            best = cost;
        }

        return winner;
    }

    /// <summary>The rate card as the screen shows it — every number a recommendation was made from.</summary>
    public static object Describe() => new
    {
        note = RatesNote,
        tiers = Tiers.Select(t => new
        {
            key = t.Key,
            name = t.Name,
            monthlyBilling = t.MonthlyBilling,
            annualBilling = t.AnnualBilling,
            freeListings = t.FreeListings,
            insertionFeeAfter = t.InsertionFeeAfter,
            annualBillingOnly = t.AnnualBillingOnly,
            unlocks = t.Unlocks,
        }).ToList(),
    };
}
