using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Prices every eBay Store tier against the listings this seller actually keeps live, and says
/// which one is cheapest.
/// </summary>
/// <remarks>
/// <para>
/// This is the only recurring bill in the seller's business that the app can both see and fix. It
/// is charged monthly whether anything sells or not, the seller usually chose the tier once, years
/// ago, at a listing count they no longer have — and it is decided by three published numbers, so
/// the answer is arithmetic rather than a forecast. Nothing else in this app can say "do this and
/// you keep $205 next month" with no risk attached to it.
/// </para>
/// <para>
/// Two things it will not do. It will not fold final value fees into the comparison: they are set
/// per category, eBay publishes no per-account rate, and a guess at them would move the
/// recommendation on a number the seller cannot check. And it will not treat a tier's non-fee perks
/// as worth money — they are stated on the row and left for the seller to value, because a
/// storefront page is worth a great deal to one seller and nothing at all to the next.
/// </para>
/// </remarks>
public sealed class StorePlanOptimizer
{
    public StorePlanResult Evaluate(StorePlanInputs input)
    {
        var listings = Math.Clamp(
            input.ListingsOverride ?? input.ActiveListings, 0, StorePlanCatalog.LadderCeiling);
        var annual = input.AnnualBilling;

        var current = StorePlanCatalog.Resolve(input.CurrentPlanKey);
        var best = StorePlanCatalog.Cheapest(listings, annual);
        var ladder = StorePlanCatalog.Ladder(annual);

        var currentCost = StorePlanCatalog.MonthlyCost(current, listings, annual);
        var bestCost = StorePlanCatalog.MonthlyCost(best, listings, annual);

        var result = new StorePlanResult
        {
            ListingsPerMonth = listings,
            ActiveListings = input.ActiveListings,
            ListingCountMeasured = input.ListingCountMeasured,
            UsingOverride = input.ListingsOverride is not null,
            CurrentPlanKey = current.Key,
            CurrentPlanName = current.Name,
            BillingCycle = annual ? "annual" : "monthly",
            BestPlanKey = best.Key,
            BestPlanName = best.Name,
            CurrentMonthlyCost = Money(currentCost),
            BestMonthlyCost = Money(bestCost),
            MonthlySaving = Money(Math.Max(0m, currentCost - bestCost)),
            AlreadyOnBestPlan = current.Key == best.Key,
            MonthlySales = Money(input.MonthlySales),
            RatesNote = StorePlanCatalog.RatesNote,
        };
        result.AnnualSaving = Money(result.MonthlySaving * 12m);

        foreach (var tier in StorePlanCatalog.Tiers)
        {
            var charged = StorePlanCatalog.ListingsCharged(tier, listings);
            var subscription = StorePlanCatalog.Subscription(tier, annual);
            var insertion = charged * tier.InsertionFeeAfter;
            var monthly = subscription + insertion;
            var band = ladder.TryGetValue(tier.Key, out var found) ? found : ((int From, int? To)?)null;

            result.Options.Add(new StorePlanOption
            {
                Key = tier.Key,
                Name = tier.Name,
                Subscription = Money(subscription),
                FreeListings = tier.FreeListings,
                InsertionFeeAfter = tier.InsertionFeeAfter,
                ListingsCharged = charged,
                InsertionCost = Money(insertion),
                MonthlyCost = Money(monthly),
                AnnualCost = Money(monthly * 12m),
                MonthlyDelta = Money(monthly - currentCost),
                IsCurrent = tier.Key == current.Key,
                IsBest = tier.Key == best.Key,
                AnnualBillingOnly = tier.AnnualBillingOnly,
                CheapestFrom = band?.From ?? 0,
                CheapestTo = band?.To,
                NeverCheapest = band is null,
                Unlocks = tier.Unlocks,
                Basis = BasisFor(tier, subscription, listings, charged, insertion, monthly),
            });
        }

        ApplyBillingCycle(result, best, annual);

        result.TotalAnnualSaving = Money((result.MonthlySaving + result.BillingMonthlySaving) * 12m);
        result.CostShareOfSalesPercent = input.MonthlySales > 0m
            ? Math.Round(currentCost / input.MonthlySales * 100m, 2)
            : 0m;

        result.Headline = Headline(result, listings);
        result.Detail = Detail(result, current, best, listings, annual);
        result.NextStep = NextStep(result, best, ladder, annual);
        result.Honesty = Honesty(result);

        return result;
    }

    /// <summary>
    /// The same tier billed annually rather than monthly, which is a saving with no trade-off at all
    /// — same allotment, same fees, same everything. It is reported separately from the plan change
    /// so a seller already on the right tier is not told "nothing to do" when there is still money
    /// on the table.
    /// </summary>
    private static void ApplyBillingCycle(StorePlanResult result, StorePlanTier best, bool annual)
    {
        if (annual || best.MonthlyBilling is not decimal monthly || monthly <= best.AnnualBilling)
        {
            // Either they already pay annually, or the tier costs the same both ways — "no store"
            // is the case that matters here, and telling somebody paying $0 to commit for a year
            // would be nonsense.
            result.BillingMonthlySaving = 0m;
            result.BillingNote = annual && best.AnnualBilling > 0m
                ? $"You are already on the annual rate for {best.Name}, which is the cheaper of the two."
                : "";
            return;
        }

        var saving = Money(monthly - best.AnnualBilling);
        result.BillingMonthlySaving = saving;
        result.BillingNote =
            $"{best.Name} is ${monthly:0.00} a month billed monthly and ${best.AnnualBilling:0.00} billed "
            + $"annually — same allotment, same fees, ${saving:0.00} a month less. That is ${saving * 12m:0.00} "
            + "a year for committing to a plan you were staying on anyway.";
    }

    private static string BasisFor(
        StorePlanTier tier, decimal subscription, int listings, int charged, decimal insertion, decimal monthly)
    {
        var free = $"{tier.FreeListings:N0} free";

        if (charged == 0)
            return listings == 0
                ? $"${subscription:0.00} a month. Nothing is live, so no insertion fees."
                : $"${subscription:0.00} a month covers all {listings:N0} — {free} a month, and you are under it.";

        return $"${subscription:0.00} a month, {free}, then {charged:N0} × ${tier.InsertionFeeAfter:0.00} "
             + $"= ${insertion:0.00} in insertion fees. ${monthly:0.00} a month.";
    }

    private static string Headline(StorePlanResult r, int listings)
    {
        if (listings == 0)
            return "Nothing is live yet, so every plan costs its subscription and no more. "
                 + "Come back once your listings are up and this says which tier they belong on.";

        if (r.MonthlySaving > 0m)
            return $"Move to the {r.BestPlanName} and stop paying ${r.MonthlySaving:0.00} a month you do not "
                 + $"have to — ${r.AnnualSaving:0.00} a year, on the {listings:N0} listings you already keep live.";

        if (r.BillingMonthlySaving > 0m)
            return $"{r.CurrentPlanName} is the right tier for {listings:N0} listings — but you are paying "
                 + $"monthly for it. The annual rate is ${r.BillingMonthlySaving:0.00} a month less, "
                 + $"${r.BillingMonthlySaving * 12m:0.00} a year, for the identical plan.";

        return $"{r.CurrentPlanName} is the cheapest plan there is for {listings:N0} listings. "
             + "Nothing to change.";
    }

    private static string Detail(
        StorePlanResult r, StorePlanTier current, StorePlanTier best, int listings, bool annual)
    {
        var currentLine = CurrentSentence(current, listings, annual);

        if (r.AlreadyOnBestPlan)
            return currentLine + " No other tier costs less at this listing count.";

        return currentLine + " " + BestSentence(best, listings, annual) + " That is the whole difference: "
             + $"${r.CurrentMonthlyCost:0.00} against ${r.BestMonthlyCost:0.00} a month, for the same listings, "
             + "the same fees on every sale and the same everything else.";
    }

    // Two builders rather than one with a subject slot: "no Store subscription" is a tier here, and
    // it is the one the seller is on more often than any other, so both halves of the sentence have
    // to read as English when the answer is "nothing at all".

    private static string CurrentSentence(StorePlanTier tier, int listings, bool annual)
    {
        var subscription = StorePlanCatalog.Subscription(tier, annual);
        var charged = StorePlanCatalog.ListingsCharged(tier, listings);
        var total = StorePlanCatalog.MonthlyCost(tier, listings, annual);

        var lead = tier.Key == "none"
            ? "You have no Store subscription"
            : $"You are on the {tier.Name} at ${subscription:0.00} a month";

        return charged == 0
            ? $"{lead}, and its {tier.FreeListings:N0} free listings a month cover all "
              + $"{listings:N0} of yours — ${total:0.00} a month."
            : $"{lead}, which covers {tier.FreeListings:N0} listings a month; the other {charged:N0} "
              + $"cost ${tier.InsertionFeeAfter:0.00} each to keep live — ${total:0.00} a month.";
    }

    private static string BestSentence(StorePlanTier tier, int listings, bool annual)
    {
        var subscription = StorePlanCatalog.Subscription(tier, annual);
        var charged = StorePlanCatalog.ListingsCharged(tier, listings);
        var total = StorePlanCatalog.MonthlyCost(tier, listings, annual);

        var lead = tier.Key == "none"
            ? $"Dropping the subscription leaves you the {tier.FreeListings:N0} free listings every "
              + "seller gets without paying for anything"
            : $"The {tier.Name} is ${subscription:0.00} a month, which covers {tier.FreeListings:N0} listings";

        return charged == 0
            ? $"{lead} — enough for all {listings:N0} of yours, at ${total:0.00} a month."
            : $"{lead}; the other {charged:N0} cost ${tier.InsertionFeeAfter:0.00} each — "
              + $"${total:0.00} a month.";
    }

    /// <summary>
    /// Where the next plan change lands. The point of the feature after the first switch: a seller
    /// who knows they change tier at 1,153 listings never grows quietly into the wrong one.
    /// </summary>
    private static string NextStep(
        StorePlanResult r, StorePlanTier best,
        IReadOnlyDictionary<string, (int From, int? To)> ladder, bool annual)
    {
        if (!ladder.TryGetValue(best.Key, out var band) || band.To is not int ceiling)
            return $"{best.Name} stays the cheapest plan however many more listings you put up.";

        var next = StorePlanCatalog.Cheapest(ceiling + 1, annual);
        var headroom = ceiling - r.ListingsPerMonth;

        return headroom >= 0
            ? $"{best.Name} stays cheapest up to {ceiling:N0} listings — {headroom:N0} more than you have "
              + $"now. Past that, the {next.Name} takes over."
            : $"Past {ceiling:N0} listings the {next.Name} takes over.";
    }

    private static List<string> Honesty(StorePlanResult r)
    {
        var lines = new List<string>
        {
            "Tiers are compared on the three things they actually differ on: the monthly subscription, the "
            + "free-listing allotment, and the insertion fee charged past it. Final value fees are not in "
            + "here — eBay sets those by category and publishes no per-account rate, so putting a guess at "
            + "them into this comparison would move the answer on a number you cannot check.",

            "A fixed-price listing renews every 30 days and each renewal uses one of that month's free "
            + "listings, so the number that gets charged is the number you KEEP live, not the number you "
            + "create. If you also list and sell items inside the same month, your real count is higher "
            + "than the one above and the bigger tier is worth more than it says here.",

            "Auction-style listings have their own allotments and their own fees. Nothing above counts them.",
        };

        if (!r.ListingCountMeasured)
            lines.Add("The listing count above is the one you typed, not one read from eBay. Connect eBay in "
                    + "Settings and this counts your live listings itself.");

        if (r.UsingOverride && r.ListingCountMeasured)
            lines.Add($"You are planning against {r.ListingsPerMonth:N0} listings rather than the "
                    + $"{r.ActiveListings:N0} eBay currently reports. Clear the box to go back to the real count.");

        lines.Add(StorePlanCatalog.RatesNote);
        return lines;
    }

    /// <summary>Cents. Every figure on this screen is a bill, and a bill with four decimal places is not one.</summary>
    private static decimal Money(decimal value) =>
        Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
