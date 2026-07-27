using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Gets the money back out of stock that has stopped moving: a dated ladder of price drops per
/// aging listing, and bundles that pair a slow mover with something that already sells.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="InventoryHealthAnalyzer"/> answers "what should this be priced at today", and on an
/// old listing it deliberately answers with one capped step and "re-run the scan to go further".
/// That is the right answer for a repricer and the wrong one for dead stock, because the seller
/// has to remember to come back — and the whole failure mode of aging inventory is that nobody
/// comes back. This turns that single step into a plan with dates on it: the drops are decided
/// once, in advance, while the seller is looking at what the item is actually costing them.
/// </para>
/// <para>
/// Nothing here invents a price or a fee. The ladder is bounded by the same break-even and floor
/// policy the repricer uses, the charm rounding is <see cref="InventoryHealthAnalyzer.Charm"/>, and
/// every take-home figure comes from <see cref="NetProceedsCalculator"/> and
/// <see cref="ProfitCalculator"/> — so a rescue price is costed by exactly the same rules as a
/// local flip or a dropship. Capital that has been sitting for six months is still not a reason to
/// recommend a loss.
/// </para>
/// </remarks>
public sealed class AgingInventoryRescuer(ProfitCalculator profitCalc)
{
    // ── When a listing becomes a rescue case ─────────────────────────────────────────────────
    // The same 90-day line Inventory Health calls stale, so the two boards cannot disagree about
    // which listings are stuck.
    public const int DefaultStaleAfterDays = InventoryHealthAnalyzer.StaleDays;

    // ── Shape of the ladder ──────────────────────────────────────────────────────────────────
    // Long enough that each price gets a real run in front of buyers, short enough that the plan
    // finishes inside a quarter. eBay's own search rewards a recently-revised listing, so a drop
    // every fortnight also keeps the item resurfacing.
    public const int DefaultStepIntervalDays = 14;
    private const int UrgentStepIntervalDays = 10;

    private const int MaxSteps = 4;
    // A step smaller than this does not change a buyer's mind and churns the listing for nothing —
    // the same bar the repricer applies to a single revision.
    private const decimal MinStepPercent = 3m;
    private const decimal MinStepDollars = 1m;

    // The deepest the ladder aims, as a fraction of today's market price, when the comps offer no
    // quick-sale figure of their own. Matches the 180-day rung of the repricer's ladder: past six
    // months the goal changes from selling it well to getting the money back out.
    private const decimal ClearanceFraction = 0.85m;

    // ── Bundles ──────────────────────────────────────────────────────────────────────────────
    // What makes a partner "fast". Any one of these is evidence the item moves; none of them is
    // inferred from an item merely being new.
    private const int FastWatcherCount = 3;
    private const int FastDaysToSell = DaysToCashEstimator.FastCashDays;

    // A partner has to be worth something next to the item it is carrying. A $4 cable does not pull
    // a $900 miner out of the warehouse, and pairing them just discounts the cable.
    private const decimal MinPartnerPriceRatio = 0.10m;

    private const int MaxBundles = 12;

    /// <summary>
    /// Builds the whole board from an inventory-health scan: a plan for every listing old enough to
    /// need one, plus bundle pairings across the same inventory.
    /// </summary>
    public RescueResult Build(
        IReadOnlyList<InventoryHealthItem> items, FeeProfile fees, DateTime nowUtc,
        int staleAfterDays = DefaultStaleAfterDays, int maxBundles = MaxBundles)
    {
        staleAfterDays = Math.Max(1, staleAfterDays);

        var result = new RescueResult
        {
            StaleAfterDays = staleAfterDays,
            StepIntervalDays = DefaultStepIntervalDays,
        };

        var stuck = items.Where(i => IsStuck(i, staleAfterDays)).ToList();

        result.Plans = stuck
            .Select(i => BuildPlan(i, fees, nowUtc))
            .OrderByDescending(p => p.HasPlan)
            .ThenByDescending(p => UrgencyRank(p.Urgency))
            .ThenByDescending(p => p.CapitalTiedUp)
            .ThenByDescending(p => p.DaysListed ?? -1)
            .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        result.Bundles = FindBundles(items, fees, nowUtc, staleAfterDays, maxBundles);
        result.Summary = Summarize(stuck, result.Plans, result.Bundles);
        return result;
    }

    /// <summary>
    /// Whether a listing is stuck capital: old enough, and not moving. A multi-quantity listing
    /// that is still selling units is old but not stuck — its stock is turning, and marking it down
    /// would spend margin on every remaining unit to fix a problem it does not have.
    /// </summary>
    public static bool IsStuck(InventoryHealthItem item, int staleAfterDays) =>
        item.DaysListed >= staleAfterDays && !item.IsSelling;

    // ── The plan ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Builds the dated markdown ladder for one aging listing.</summary>
    public RescuePlan BuildPlan(InventoryHealthItem item, FeeProfile fees, DateTime nowUtc)
    {
        var plan = new RescuePlan
        {
            ListingId = item.ListingId,
            Sku = item.Sku,
            Title = item.Title,
            Url = item.Url,
            ImageUrl = item.ImageUrl,
            Category = item.Category,
            ListPrice = item.ListPrice,
            Quantity = item.Quantity,
            DaysListed = item.DaysListed,
            WatchCount = item.WatchCount,
            CapitalTiedUp = item.CapitalTiedUp,
            CapitalBasis = item.CapitalBasis,
            MarketPrice = item.MarketPrice,
            QuickSalePrice = item.QuickSalePrice,
            FloorPrice = item.MinimumOfferPrice ?? item.BreakEvenPrice,
            FloorBasis = item.MinimumOfferBasis,
            Verdict = item.Verdict,
            Urgency = UrgencyFor(item),
        };

        plan.Why = WhyStuck(item);

        // A comparison that failed is not a reason to cut a price. The listing is still reported as
        // stuck — that is a fact about its age, not about the comps — but the plan says so instead
        // of laddering off a number that never matched. Same rule as the repricer.
        if (!item.MarketComparable)
        {
            plan.Headline = "No plan — this one couldn't be priced against the market.";
            plan.Signals.Add(item.LotQuantity > 1
                ? $"This is a lot of {item.LotQuantity} and sold comps are per unit, so there is no like-for-like price to ladder down to. Price it by hand, or split the lot."
                : "The sold comps matched something else, so a markdown would be guesswork. Check what it was priced as before dropping the price.");
            return plan;
        }

        if (item.MarketPrice is not decimal market || market <= 0m || item.ListPrice <= 0m)
        {
            plan.Headline = "No plan — no sold history to price against.";
            plan.Signals.Add("Nothing in the sold data matched this listing, so there is no market price to walk down to. A bundle may still move it.");
            return plan;
        }

        // The break-even is above what the market pays: every rung of every ladder is a loss, so
        // there is no honest markdown plan. Saying that is the answer — and it is exactly the case
        // a bundle exists for, so the board points there rather than stopping.
        if (plan.FloorPrice is decimal floorAboveMarket && floorAboveMarket > market)
        {
            plan.Headline = "No markdown can save this one — it is underwater.";
            plan.Signals.Add($"Your floor is ${floorAboveMarket:0.00} but the market pays about ${market:0.00}. Dropping the price only sets the size of the loss. Bundle it, hold it, or clear it deliberately.");
            return plan;
        }

        var clearance = ClearanceTarget(market, item.QuickSalePrice, plan.FloorPrice);

        if (clearance >= item.ListPrice)
        {
            plan.Headline = "Already at clearance — the price is not what is holding this one.";
            plan.Signals.Add(plan.FloorPrice is decimal f && f >= item.ListPrice
                ? $"It is already at your ${f:0.00} floor. Any further drop sells it at a loss, so the real options are a bundle, or accepting one."
                : "It is already priced at or under what a quick sale goes for, and it still is not moving. Photos, title and shipping cost are the next things to look at — or bundle it.");
            return plan;
        }

        var interval = plan.Urgency == "critical" ? UrgentStepIntervalDays : DefaultStepIntervalDays;
        var stepCount = StepCountFor(plan.Urgency);

        plan.Steps = BuildSteps(
            listPrice: item.ListPrice, clearance: clearance, floorPrice: plan.FloorPrice,
            breakEven: item.BreakEvenPrice, stepCount: stepCount, intervalDays: interval,
            daysListed: item.DaysListed, fees: fees, nowUtc: nowUtc);

        if (plan.Steps.Count == 0)
        {
            plan.Headline = "No plan — there is not enough room between this price and your floor to be worth a drop.";
            plan.Signals.Add("Every step the ladder tried was too small to change a buyer's mind. A bundle is the better lever here.");
            return plan;
        }

        var last = plan.Steps[^1];
        plan.FinalPrice = last.Price;
        plan.ClearByUtc = last.OnUtc;
        plan.PlanDays = last.DaysFromNow;
        plan.CashAtFinalStep = last.NetProfit;
        plan.ProfitGivenUp = item.NetProfitAtListPrice is decimal atList && last.NetProfit is decimal atLast
            ? Math.Round(atList - atLast, 2)
            : null;

        plan.Headline = Headline(plan, item);

        if (item.WatchCount > 0)
            plan.Signals.Add($"{item.WatchCount} {(item.WatchCount == 1 ? "person is" : "people are")} watching this — the first drop is visible to them as a price cut on something they already saved.");

        if (!item.HasCostBasis)
            plan.Signals.Add("No cost basis recorded for this listing, so these prices have not been checked against what you actually paid. The ladder stops at the market's quick-sale price instead of at your break-even.");

        if (last.IsFloor)
            plan.Signals.Add($"The last step lands on your ${plan.FloorPrice:0.00} floor — that is as low as this item goes without selling at a loss.");

        return plan;
    }

    /// <summary>
    /// The price the ladder is walking toward: a genuine clearance number from the comps, never
    /// below whatever floor the seller's costs and policy set.
    /// </summary>
    public static decimal ClearanceTarget(decimal market, decimal? quickSale, decimal? floorPrice)
    {
        // The estimator's own quick-sale figure when it is the more aggressive of the two, because
        // it comes from the comps rather than from a flat percentage of them.
        var target = Math.Min(market * ClearanceFraction, quickSale ?? decimal.MaxValue);
        if (floorPrice is decimal floor) target = Math.Max(target, floor);
        return Math.Round(target, 2);
    }

    // How many rungs. Older stock gets fewer and deeper ones: at six months the seller is no longer
    // optimising the sale price, they are buying their capital back.
    private static int StepCountFor(string urgency) => urgency switch
    {
        "critical" => 2,
        "high" => 3,
        _ => MaxSteps,
    };

    /// <summary>
    /// Walks the price from today's ask down to the clearance target in evenly spaced drops, dated
    /// forward from now. Steps too small to matter are dropped rather than shipped.
    /// </summary>
    public static List<RescueStep> BuildSteps(
        decimal listPrice, decimal clearance, decimal? floorPrice, decimal? breakEven,
        int stepCount, int intervalDays, int? daysListed, FeeProfile fees, DateTime nowUtc)
    {
        var steps = new List<RescueStep>();
        if (listPrice <= 0m || clearance >= listPrice || stepCount <= 0) return steps;

        var span = listPrice - clearance;
        var previous = listPrice;

        for (var i = 1; i <= stepCount; i++)
        {
            // Linear rather than compounding: the seller can read "a quarter of the way down each
            // time" off the table, and a ladder they can explain is one they will actually follow.
            var raw = listPrice - span * i / stepCount;

            // The last rung is the clearance target exactly, so rounding can never leave the plan
            // stopping a few cents short of the number the whole plan was aimed at.
            if (i == stepCount) raw = clearance;

            var price = InventoryHealthAnalyzer.Charm(raw, floorPrice);
            if (floorPrice is decimal floor && price < floor) price = Math.Round(floor, 2);

            // Too small a move from the price the buyer is currently looking at.
            var move = previous - price;
            if (move < MinStepDollars || (previous > 0m && move / previous * 100m < MinStepPercent))
                continue;

            var daysFromNow = (i - 1) * intervalDays;
            steps.Add(new RescueStep
            {
                StepNumber = steps.Count + 1,
                OnUtc = nowUtc.AddDays(daysFromNow),
                DaysFromNow = daysFromNow,
                ListingAgeAtStep = daysListed is int age ? age + daysFromNow : null,
                Price = price,
                PercentOffListPrice = Math.Round((listPrice - price) / listPrice * 100m, 1),
                NetProfit = NetProceedsCalculator.NetProfitAt(price, breakEven, fees),
                IsFloor = floorPrice is decimal f && price <= f + 0.01m,
                Note = daysFromNow == 0
                    ? "Do this one now."
                    : $"If it is still unsold in {daysFromNow} days.",
            });

            previous = price;
        }

        // Skipping the early rungs must not leave the plan waiting weeks to make a drop it has
        // already decided is worth making. When the first worthwhile step is the one four weeks out,
        // that step is what the seller does today, and the rest keep their spacing behind it.
        if (steps.Count > 0 && steps[0].DaysFromNow > 0)
        {
            var shift = steps[0].DaysFromNow;
            foreach (var step in steps)
            {
                step.DaysFromNow -= shift;
                step.OnUtc = nowUtc.AddDays(step.DaysFromNow);
                step.ListingAgeAtStep = daysListed is int age ? age + step.DaysFromNow : null;
                step.Note = step.DaysFromNow == 0
                    ? "Do this one now."
                    : $"If it is still unsold in {step.DaysFromNow} days.";
            }
        }

        return steps;
    }

    // ── Bundles ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Pairs each stuck listing with the best fast-moving partner available, one listing to one
    /// bundle, biggest trapped capital first.
    /// </summary>
    /// <remarks>
    /// The pairing is greedy rather than globally optimal on purpose: the seller works the list from
    /// the top, and an arrangement that frees the most capital on row one is worth more than one
    /// that is a few dollars better across a list they will not finish.
    /// </remarks>
    public List<BundleSuggestion> FindBundles(
        IReadOnlyList<InventoryHealthItem> items, FeeProfile fees, DateTime nowUtc,
        int staleAfterDays, int maxBundles)
    {
        var suggestions = new List<BundleSuggestion>();
        if (maxBundles <= 0) return suggestions;

        var slowMovers = items
            .Where(i => IsStuck(i, staleAfterDays) && i.ListPrice > 0m)
            .OrderByDescending(i => i.CapitalTiedUp)
            .ThenByDescending(i => i.DaysListed ?? -1)
            .ToList();

        var fastMovers = items.Where(i => IsFastMover(i, staleAfterDays) && i.ListPrice > 0m).ToList();
        if (slowMovers.Count == 0 || fastMovers.Count == 0) return suggestions;

        // One listing appears in at most one suggestion. A fast mover anchoring four bundles would
        // read as four opportunities and be one item that can only be sold once.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slow in slowMovers)
        {
            if (suggestions.Count >= maxBundles) break;
            if (used.Contains(slow.ListingId)) continue;

            var partner = fastMovers
                .Where(f => !used.Contains(f.ListingId)
                         && !string.Equals(f.ListingId, slow.ListingId, StringComparison.OrdinalIgnoreCase)
                         && CategoriesFit(slow.Category, f.Category)
                         && f.ListPrice >= slow.ListPrice * MinPartnerPriceRatio)
                // Closest in value first: a bundle reads as a deal when the two halves look like
                // they belong to the same purchase, and as a bribe when they do not.
                .OrderBy(f => Math.Abs(f.ListPrice - slow.ListPrice))
                .ThenByDescending(f => SpeedRank(f))
                .FirstOrDefault();

            if (partner is null) continue;

            var bundle = BuildBundle(slow, partner, fees);
            if (bundle is null) continue;

            suggestions.Add(bundle);
            used.Add(slow.ListingId);
            used.Add(partner.ListingId);
        }

        return suggestions
            .OrderByDescending(b => b.CapitalFreed)
            .ThenByDescending(b => b.IncrementalNet ?? b.AddedRevenue)
            .ToList();
    }

    /// <summary>
    /// Evidence that an item moves. Sales first — an item that has actually sold units has settled
    /// the question. Watchers and measured velocity are weaker but real. Nothing counts a listing as
    /// fast just for being new.
    /// </summary>
    public static bool IsFastMover(InventoryHealthItem item, int staleAfterDays)
    {
        if (item.IsSelling) return true;
        // Past the stale line with nothing sold, this item is a rescue case itself and cannot carry
        // anything. Two slow movers in a box is a bigger slow mover.
        if (item.DaysListed >= staleAfterDays) return false;
        if (item.WatchCount >= FastWatcherCount) return true;
        return item.EstimatedDaysToSell is int days && days > 0 && days <= FastDaysToSell;
    }

    private static int SpeedRank(InventoryHealthItem item) =>
        item.IsSelling ? 3 : item.EstimatedDaysToSell is int d && d > 0 && d <= FastDaysToSell ? 2 : 1;

    /// <summary>
    /// Whether two listings plausibly belong in the same box. A buyer bundles things they were
    /// shopping for together; when eBay reports no category for one side there is nothing to
    /// contradict, so the pairing is allowed and the row says the check could not be made.
    /// </summary>
    public static bool CategoriesFit(string? a, string? b) =>
        string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) ||
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Prices one pairing and decides whether it is worth suggesting. Returns null when the bundle
    /// would not actually leave the seller better off than selling the fast item on its own.
    /// </summary>
    public BundleSuggestion? BuildBundle(InventoryHealthItem slow, InventoryHealthItem fast, FeeProfile fees)
    {
        // The slow half goes in at its clearance price — the same number its markdown ladder walks
        // toward, so the two halves of this board cannot recommend two different values for one
        // item. The gain over the ladder is that the seller reaches that price inside a bundle
        // instead of publicly cutting the standalone listing.
        var slowFloor = slow.MinimumOfferPrice ?? slow.BreakEvenPrice;
        var contribution = slow.MarketPrice is decimal market && market > 0m
            ? ClearanceTarget(market, slow.QuickSalePrice, slowFloor)
            : slowFloor ?? Math.Round(slow.ListPrice * ClearanceFraction, 2);

        // Never sell the slow half for more inside the bundle than it is already failing to sell
        // for outside it — that is not a discount, and nobody buys it.
        contribution = Math.Min(contribution, slow.ListPrice);
        if (contribution <= 0m) return null;

        var componentValue = Math.Round(slow.ListPrice + fast.ListPrice, 2);
        var bundlePrice = InventoryHealthAnalyzer.Charm(fast.ListPrice + contribution, floorPrice: null);
        if (bundlePrice <= fast.ListPrice) return null;

        var suggestion = new BundleSuggestion
        {
            SlowListingId = slow.ListingId,
            SlowTitle = slow.Title,
            SlowPrice = slow.ListPrice,
            SlowDaysListed = slow.DaysListed,
            SlowCapital = slow.CapitalTiedUp,
            SlowContribution = contribution,

            FastListingId = fast.ListingId,
            FastTitle = fast.Title,
            FastPrice = fast.ListPrice,
            FastEvidence = SpeedEvidence(fast),

            Category = string.IsNullOrWhiteSpace(slow.Category) ? fast.Category : slow.Category,
            SameCategory = !string.IsNullOrWhiteSpace(slow.Category)
                        && !string.IsNullOrWhiteSpace(fast.Category)
                        && string.Equals(slow.Category.Trim(), fast.Category.Trim(), StringComparison.OrdinalIgnoreCase),

            ComponentValue = componentValue,
            BundlePrice = bundlePrice,
            DiscountPercent = componentValue > 0m
                ? Math.Round((componentValue - bundlePrice) / componentValue * 100m, 1) : 0m,

            // One order instead of two: eBay's fixed per-order fee, one label, one box, one trip.
            SavedByShippingTogether = Math.Round(
                fees.EbayFinalValueFeeFixed + fees.DefaultShippingCost
                + fees.DefaultPackagingCost + fees.DefaultLaborCost, 2),

            AddedRevenue = Math.Round(bundlePrice - fast.ListPrice, 2),
            CapitalFreed = slow.CapitalTiedUp,
            SuggestedTitle = BundleTitle(fast.Title, slow.Title),
        };

        // The honest score, when both halves have a recorded cost: what the bundle nets against what
        // actually happens today — the fast item sells on its own, and the slow one keeps sitting.
        if (slow.CostBasis is decimal slowCost && fast.CostBasis is decimal fastCost)
        {
            suggestion.HasCostBasis = true;
            suggestion.NetIfBundleSells = NetAt(bundlePrice, slowCost + fastCost, fees);
            suggestion.NetIfFastSellsAlone = NetAt(fast.ListPrice, fastCost, fees);
            suggestion.IncrementalNet = Math.Round(
                suggestion.NetIfBundleSells!.Value - suggestion.NetIfFastSellsAlone!.Value, 2);

            // The slow item's cost is already spent either way, so a bundle that still nets less
            // than selling the fast item alone is a bundle that costs money to offer.
            if (suggestion.IncrementalNet <= 0m) return null;
        }
        else
        {
            suggestion.Signals.Add("No cost basis on both halves, so this shows the extra revenue rather than the extra profit. Record what you paid to see the net.");
        }

        suggestion.Rationale = BundleRationale(suggestion, slow);

        if (!suggestion.SameCategory)
            suggestion.Signals.Add("eBay reports these two in different categories, or reports none — check they are things one buyer would want together before listing the bundle.");

        if (slowFloor is decimal floor && contribution <= floor + 0.01m)
            suggestion.Signals.Add($"The slow half is going in at your ${floor:0.00} floor — this bundle clears it without going under it.");

        return suggestion;
    }

    // Net take-home on a single sale at a price, costed by the same calculator every other board
    // uses. Buyer-paid shipping is zero here: a bundle is a new listing with no comps behind it, so
    // there is no observed shipping figure to book on either side.
    private decimal NetAt(decimal price, decimal unitCost, FeeProfile fees) =>
        profitCalc.Calculate(
            supplierUnitCost: unitCost, quantity: 1, expectedSalePrice: price,
            quickSalePrice: price, buyerPaidShipping: 0m, fees: fees).NetProfitPerUnit;

    private static string SpeedEvidence(InventoryHealthItem item)
    {
        if (item.IsSelling)
            return item.SalesPerMonth is decimal rate
                ? $"{item.QuantitySold} sold, about {rate:0.#} a month"
                : $"{item.QuantitySold} sold from this listing";
        if (item.EstimatedDaysToSell is int days && days > 0 && days <= FastDaysToSell)
            return $"sold comps say about {days} days to sell";
        return $"{item.WatchCount} watcher{(item.WatchCount == 1 ? "" : "s")}";
    }

    // eBay's search reads left to right and buyers scan the first few words, so the item that
    // actually gets searched for leads and the slow one rides along as the bonus.
    private static string BundleTitle(string fastTitle, string slowTitle)
    {
        var lead = Truncate(fastTitle.Trim(), 45);
        var rider = Truncate(slowTitle.Trim(), 25);
        return Truncate($"{lead} + {rider} Bundle Lot", 80);
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max].TrimEnd();

    private static string BundleRationale(BundleSuggestion b, InventoryHealthItem slow)
    {
        var age = slow.DaysListed is int days ? $"{days} days" : "months";
        var money = b.IncrementalNet is decimal net
            ? $"nets {net:C0} more than selling the {b.FastTitle.Split(' ').FirstOrDefault()} on its own"
            : $"adds {b.AddedRevenue:C0} of revenue over selling the fast one alone";

        return $"The slow half has sat {age}. Attached to something that already sells, it goes out at "
             + $"{b.SlowContribution:C0} instead of {b.SlowPrice:C0} — and the pair {money}, "
             + $"because one order pays {b.SavedByShippingTogether:C2} of per-order costs instead of two.";
    }

    // ── Framing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>How urgently this money needs to come back — the sort order of the whole board.</summary>
    public static string UrgencyFor(InventoryHealthItem item) =>
        item.Verdict == "dead_capital" || item.DaysListed >= InventoryHealthAnalyzer.DeadDays ? "critical"
        : item.DaysListed >= 120 ? "high"
        : "watch";

    private static int UrgencyRank(string urgency) => urgency switch
    {
        "critical" => 3, "high" => 2, _ => 1,
    };

    private static string WhyStuck(InventoryHealthItem item)
    {
        var age = item.DaysListed is int days ? $"{days} days" : "an unknown length of time";
        var gap = item.PriceGapPercent;

        if (!item.MarketComparable) return $"Live {age} with no sale, and no comparable market price to judge it by.";
        if (item.WatchCount == 0 && gap > 0m)
            return $"Live {age}, nobody watching, and {gap:0}% above what these actually sell for.";
        if (item.WatchCount > 0 && gap > 0m)
            return $"Live {age} at {gap:0}% above market. People are watching, so the interest is there and the price is the blocker.";
        if (gap <= 0m)
            return $"Live {age} even at or below market price — the price may not be the only blocker, but it is the one you control today.";
        return $"Live {age} without selling.";
    }

    private static string Headline(RescuePlan plan, InventoryHealthItem item)
    {
        var first = plan.Steps[0];
        var cash = plan.CashAtFinalStep is decimal net && net > 0m
            ? $" Worst case at the end of the plan you still keep {net:C0}."
            : "";

        return plan.Urgency == "critical"
            ? $"Drop to {first.Price:C0} today, {plan.FinalPrice:C0} by {plan.ClearByUtc:MMM d} — {item.CapitalTiedUp:C0} has been parked here long enough.{cash}"
            : $"Drop to {first.Price:C0} today and step down to {plan.FinalPrice:C0} over {plan.PlanDays} days to free {item.CapitalTiedUp:C0}.{cash}";
    }

    // ── Totals ───────────────────────────────────────────────────────────────────────────────

    /// <summary>The headline money: what is stuck, what the plans get back, what the bundles add.</summary>
    public static RescueSummary Summarize(
        IReadOnlyList<InventoryHealthItem> stuck, IReadOnlyList<RescuePlan> plans,
        IReadOnlyList<BundleSuggestion> bundles)
    {
        var summary = new RescueSummary
        {
            StaleListings = stuck.Count,
            TrappedCapital = Math.Round(stuck.Sum(i => i.CapitalTiedUp), 2),
            // Max over int? is null for an empty sequence, which is the honest answer here.
            OldestDaysListed = stuck.Select(i => i.DaysListed).Max(),
        };

        var ages = stuck.Where(i => i.DaysListed.HasValue).Select(i => i.DaysListed!.Value).OrderBy(d => d).ToList();
        summary.MedianDaysListed = ages.Count == 0 ? null
            : ages.Count % 2 == 1 ? ages[ages.Count / 2]
            : (ages[ages.Count / 2 - 1] + ages[ages.Count / 2]) / 2;

        var withPlan = plans.Where(p => p.HasPlan).ToList();
        summary.PlansReady = withPlan.Count;
        summary.NoPlanCount = plans.Count - withPlan.Count;
        summary.StepsDueNow = withPlan.Count(p => p.FirstStep?.DaysFromNow == 0);
        summary.CapitalUnderPlan = Math.Round(withPlan.Sum(p => p.CapitalTiedUp), 2);

        // Conditional on a sale, and labelled that way everywhere it is shown — the same posture
        // Inventory Health takes with ProjectedNetIfRepricedSells.
        summary.CashIfEveryPlanClears = Math.Round(
            withPlan.Where(p => p.CashAtFinalStep.HasValue).Sum(p => p.CashAtFinalStep!.Value), 2);
        summary.ProfitGivenUpIfEveryPlanClears = Math.Round(
            withPlan.Where(p => p.ProfitGivenUp.HasValue).Sum(p => p.ProfitGivenUp!.Value), 2);

        summary.BundlesFound = bundles.Count;
        summary.CapitalFreedByBundles = Math.Round(bundles.Sum(b => b.CapitalFreed), 2);
        summary.IncrementalNetFromBundles = Math.Round(
            bundles.Where(b => b.IncrementalNet.HasValue).Sum(b => b.IncrementalNet!.Value), 2);
        summary.AddedRevenueFromBundles = Math.Round(bundles.Sum(b => b.AddedRevenue), 2);

        return summary;
    }
}
