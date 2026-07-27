using System.Collections;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Decides how to spend a fixed amount of cash across the deals in front of the seller, to end up
/// with the most money. Every other sourcing screen ranks deals one at a time; this one is the only
/// place in the app that answers the question the seller actually has at the ATM — <em>which set</em>.
/// </summary>
/// <remarks>
/// The whole service is pure: no network, no store, no clock except the date it is handed. It
/// re-prices nothing — each candidate arrives already costed by the same
/// <see cref="ProfitCalculator"/>-backed stack that costed it on the board it came from, and this
/// only chooses among them. That is deliberate: a basket whose profit figures disagreed with the
/// table the seller was just looking at would be worse than no basket at all.
///
/// The allocation is an exact 0/1 knapsack, not a greedy pick. Greedy is what a person does
/// naturally — buy the biggest profit, then the next one that fits — and it is routinely wrong,
/// because the biggest profit is usually also the biggest price. The gap between the two answers is
/// reported in dollars on every result (<see cref="BudgetComparison"/>), including when it is zero.
/// </remarks>
public sealed class SourcingBudgetOptimizer
{
    // ── What "best" can mean ──────────────────────────────────────────────────────────────────
    // Three defensible answers, and the seller picks. All three are solved on every request, so
    // the trade-off between them is visible rather than argued.
    public const string ObjectiveProfit = "profit";      // the most money, whenever it lands
    public const string ObjectiveFastCash = "fast_cash";  // the most money among deals that come back fast
    public const string ObjectivePerDay = "per_day";     // the most money per day of tied-up capital

    /// <summary>
    /// How much sold history a deal needs before the seller's cash is pointed at it. Same bar the
    /// arbitrage board uses to stop calling something a real deal, so a row badged "thin" there
    /// isn't quietly bought here.
    /// </summary>
    public const int DefaultMinComps = 3;

    // Bounds on the solve. Both exist for one reason: this runs on a click, and an unbounded
    // knapsack over an unbounded candidate list would turn a UI button into a hang.
    public const int MaxCandidates = 150;
    public const int MaxGridCells = 50_000;

    /// <summary>How much more budget the "what would another slice of cash buy" figure tests.</summary>
    public const decimal StretchFraction = 0.25m;

    public static string NormalizeObjective(string? objective) => (objective ?? "").Trim().ToLowerInvariant() switch
    {
        ObjectiveFastCash or "fast" or "speed" => ObjectiveFastCash,
        ObjectivePerDay or "perday" or "rate" => ObjectivePerDay,
        _ => ObjectiveProfit,
    };

    private static readonly string[] AllObjectives = [ObjectiveProfit, ObjectiveFastCash, ObjectivePerDay];

    public BudgetPlanResult Plan(BudgetPlanRequest request, DateTime? today = null)
    {
        var when = today ?? DateTime.Today;
        var budget = Math.Round(Math.Max(0m, request.Budget), 2);
        var reserve = Math.Round(Math.Max(0m, request.Reserve), 2);
        var spendable = Math.Round(Math.Max(0m, budget - reserve), 2);

        var result = new BudgetPlanResult
        {
            Budget = budget,
            Reserve = reserve,
            Spendable = spendable,
        };

        if (budget <= 0)
        {
            result.Status = "no_budget";
            result.Message = "Tell me what you have to spend and I'll work out where it goes furthest.";
            return result;
        }

        if (spendable <= 0)
        {
            result.Status = "no_budget";
            // Not an error — the seller said to hold it all back, and the honest answer is that
            // there is nothing to allocate, not a basket bought with the reserve.
            result.Message = $"You're holding back all {Money(budget)}, so there's nothing left to buy with.";
            return result;
        }

        var (candidates, duplicates) = Dedupe(request.Candidates);
        result.CandidatesConsidered = candidates.Count;
        result.DuplicatesMerged = duplicates;
        result.TrackedDealsIncluded = candidates.Count(c => c.Origin == BudgetOrigins.Tracked);

        if (candidates.Count == 0)
        {
            result.Status = "no_candidates";
            result.Message = "There are no deals to allocate against yet. Run a local scan, or track " +
                             "the deals you're weighing up, and this will spend the money across them.";
            return result;
        }

        var minComps = Math.Max(0, request.MinCompCount ?? DefaultMinComps);
        var horizon = request.MaxDaysToCash is int h && h > 0 ? h : (int?)null;

        var eligible = new List<BudgetCandidate>();
        var skipped = new List<BudgetSkip>();
        // Deals priced a little OVER the budget stay in the pool. They can never enter the basket
        // — the solve only ever reads the answer back at the seller's real capacity — but keeping
        // them is what makes "another $125 would buy $190 more" a measured figure rather than a
        // guess, and what lets the board name the deal to buy first if cash frees up.
        var poolLimit = Math.Round(spendable * (1m + StretchFraction), 2);

        foreach (var candidate in candidates)
        {
            var skip = Screen(candidate, spendable, poolLimit, minComps, request.IncludeThin, horizon);
            if (skip is null) eligible.Add(candidate); else skipped.Add(skip);
        }

        // The knapsack is exact, which means its cost is the candidate count times the budget grid.
        // Past the cap the least promising per dollar are set aside — and said so, rather than
        // dropped quietly.
        if (eligible.Count > MaxCandidates)
        {
            var ordered = eligible
                .OrderByDescending(c => c.TotalCost <= 0 ? decimal.MaxValue : c.TotalProfit / c.TotalCost)
                .ThenByDescending(c => c.TotalProfit)
                .ToList();
            foreach (var extra in ordered.Skip(MaxCandidates))
                skipped.Add(SkipFor(extra, "capped",
                    $"Set aside to keep the solve honest and quick — only the {MaxCandidates} best-value " +
                    "deals per dollar are allocated across."));
            eligible = ordered.Take(MaxCandidates).ToList();
            result.Notes.Add($"{candidates.Count} deals came in; the {MaxCandidates} with the best profit " +
                             "per dollar were the ones the budget was solved over.");
        }

        result.EligibleCount = eligible.Count;

        if (eligible.Count == 0)
        {
            result.Status = "nothing_affordable";
            result.Message = NothingAffordableMessage(skipped, spendable);
            result.LeftOut = RankSkips(skipped);
            return result;
        }

        // Everything in the pool is worth buying and none of it fits. That is a different answer
        // from "nothing here is worth buying", and it gets the number the seller actually needs:
        // what the cheapest real deal costs.
        if (!eligible.Any(c => c.TotalCost <= spendable))
        {
            result.Status = "nothing_affordable";
            var cheapest = eligible.Min(c => c.TotalCost);
            result.Message = $"Nothing here fits {Money(spendable)}. The cheapest deal worth buying is {Money(cheapest)}.";
            result.LeftOut = RankSkips([.. skipped, .. eligible.Select(c => SkipFor(c, "over_budget",
                $"{Money(c.TotalCost)} — {Money(c.TotalCost - spendable)} more than you have to spend."))]);
            return result;
        }

        var grid = BudgetGrid.For(spendable);
        var solved = AllObjectives.ToDictionary(o => o, o => Solve(o, eligible, grid, spendable, when));

        // Every plan reports the budget it was given as well as the part of it it was allowed to
        // spend, so a held-back reserve stays visible on the plan rather than only in the request.
        foreach (var entry in solved.Values) entry.Plan.Budget = budget;

        var chosen = NormalizeObjective(request.Objective);
        result.Plan = solved[chosen].Plan;
        result.Alternatives = AllObjectives.Where(o => o != chosen).Select(o => solved[o].Plan).ToList();

        // Always measured against the profit plan, whichever objective the seller is looking at:
        // "buying down the list" is a way of spending money, and the only fair thing to compare it
        // with is the plan that is trying to do the same thing better.
        result.Comparison = CompareWithBuyingDownTheList(eligible, spendable, solved[ObjectiveProfit].Plan);
        result.Stretch = StretchFor(solved[ObjectiveProfit], spendable);

        var picked = result.Plan.Picks.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missed in eligible.Where(c => !picked.Contains(DedupeKey(c))))
            skipped.Add(NotPickedSkip(missed, result.Plan, chosen));

        result.LeftOut = RankSkips(skipped);
        result.Message = result.Plan.Headline;

        if (result.TrackedDealsIncluded > 0)
            result.Notes.Add($"{result.TrackedDealsIncluded} tracked deal(s) are in the pool. Their profit " +
                             "figures are the forecast frozen when you tracked them, not a fresh comp lookup.");

        return result;
    }

    // ── Screening ─────────────────────────────────────────────────────────────────────────────
    // Which deals the seller's cash is allowed near at all. Everything rejected here keeps its
    // reason: on a sourcing screen, "we left this one out" is information the seller needs, and a
    // silent drop is how a real deal gets missed.
    private static BudgetSkip? Screen(
        BudgetCandidate candidate, decimal spendable, decimal poolLimit, int minComps, bool includeThin, int? horizon)
    {
        if (candidate.BuyPrice < 0)
            return SkipFor(candidate, "no_price", "No asking price, so there's nothing to allocate against it.");

        if (candidate.TotalProfit <= 0)
            return SkipFor(candidate, "loses_money",
                $"Sells for less than the {Money(candidate.TotalCost)} it costs once fees and shipping are paid.");

        // The evidence gate. A scanned row has to show the sold history its profit figure rests on.
        // A tracked deal carrying no comp count is a number the seller entered or accepted
        // themselves, and this screen does not get to overrule them about their own deal — it
        // labels the pick's origin instead, so the weaker claim is visible rather than blocked.
        var vouchedForByTheSeller = candidate.Origin == BudgetOrigins.Tracked && candidate.CompCount <= 0;

        if (!includeThin && !vouchedForByTheSeller && (candidate.Verdict == "no_data" || candidate.CompCount < minComps))
            return SkipFor(candidate, "thin_evidence",
                candidate.CompCount <= 0
                    ? "No sold history behind the profit figure — not somewhere to point real money."
                    : $"Only {candidate.CompCount} sold comp{(candidate.CompCount == 1 ? "" : "s")} behind it — " +
                      "too thin to spend on. Tick \"include thin deals\" if you want it in the pool anyway.");

        if (horizon is int limit)
        {
            if (candidate.DaysToCash is not int days)
                return SkipFor(candidate, "too_slow",
                    $"No measured selling speed, so it can't be promised inside {limit} days.");
            if (days > limit)
                return SkipFor(candidate, "too_slow", $"~{days} days to cash — past the {limit} you asked for.");
        }

        // Out of reach entirely — not merely unaffordable today, but far enough past the budget
        // that no realistic stretch of it reaches. Anything between the budget and that line stays
        // in the pool, unbuyable but visible, as the deal to buy first if more cash turns up.
        if (candidate.TotalCost > poolLimit)
            return SkipFor(candidate, "over_budget",
                $"{Money(candidate.TotalCost)} is well past the {Money(spendable)} you have to spend.");

        return null;
    }

    private static BudgetSkip SkipFor(BudgetCandidate candidate, string code, string reason) => new()
    {
        Id = DedupeKey(candidate),
        Title = candidate.Title,
        SourceLabel = candidate.SourceLabel,
        Url = candidate.Url,
        BuyPrice = candidate.TotalCost,
        NetProfit = candidate.TotalProfit,
        DaysToCash = candidate.DaysToCash,
        ReasonCode = code,
        Reason = reason,
    };

    // A profitable deal the basket still didn't take. Three honest reasons, and they are three
    // different pieces of news: "buy it next", "this objective can't rank it", and "your money
    // does better elsewhere".
    private static BudgetSkip NotPickedSkip(BudgetCandidate candidate, BudgetPlan plan, string objective)
    {
        if (!InObjectivePool(objective, candidate))
        {
            var reason = objective == ObjectiveFastCash
                ? candidate.DaysToCash is int days
                    ? $"~{days} days to cash — outside the {DaysToCashEstimator.FastCashDays}-day window this basket buys inside."
                    : "No measured selling speed, so it can't be promised as fast cash."
                : "No measured selling speed, so there's no per-day rate to rank it by.";
            return SkipFor(candidate, "objective_excluded",
                $"{reason} It's in the \"{ObjectiveLabel(ObjectiveProfit)}\" basket instead.");
        }

        var code = candidate.TotalCost > plan.Leftover ? "not_enough_left" : "crowded_out";
        return SkipFor(candidate, code, code == "not_enough_left"
            ? $"Needs {Money(candidate.TotalCost)} and the basket leaves {Money(plan.Leftover)} — " +
              "the first thing to buy if you free up more cash."
            : $"Affordable, but the same {Money(candidate.TotalCost)} makes more spread across the picks above.");
    }

    /// <summary>
    /// Whether a deal can honestly be ranked under this objective at all. Speed it doesn't have is
    /// never assumed in either direction — an unmeasured deal is not fast, and it is not dead.
    /// </summary>
    private static bool InObjectivePool(string objective, BudgetCandidate candidate) => objective switch
    {
        ObjectiveFastCash => candidate.DaysToCash is int days && days <= DaysToCashEstimator.FastCashDays,
        ObjectivePerDay => candidate.DaysToCash is > 0,
        _ => true,
    };

    private static List<BudgetSkip> RankSkips(List<BudgetSkip> skips) => skips
        // The near-misses first: a real deal the seller might still want is more useful than a
        // list of things that lose money.
        .OrderBy(s => s.ReasonCode switch
        {
            "not_enough_left" or "crowded_out" => 0,
            "objective_excluded" => 1,
            _ => 2,
        })
        .ThenByDescending(s => s.NetProfit ?? 0m)
        .Take(12)
        .ToList();

    private static string NothingAffordableMessage(List<BudgetSkip> skipped, decimal spendable)
    {
        var overBudget = skipped.Count(s => s.ReasonCode == "over_budget");
        var thin = skipped.Count(s => s.ReasonCode == "thin_evidence");
        var slow = skipped.Count(s => s.ReasonCode == "too_slow");
        var losers = skipped.Count(s => s.ReasonCode == "loses_money");

        if (overBudget > 0 && overBudget >= thin + slow + losers)
        {
            var cheapest = skipped.Where(s => s.ReasonCode == "over_budget").Min(s => s.BuyPrice);
            return $"Nothing here fits {Money(spendable)}. The cheapest deal worth buying is {Money(cheapest)}.";
        }
        if (slow > 0 && slow >= thin + losers)
            return $"Nothing here comes back inside the window you set. Widen it and there is money to spend.";
        if (thin > 0 && thin >= losers)
            return "Everything profitable here rests on too little sold history to spend real money on. " +
                   "Scan a bit wider, or tick \"include thin deals\" if you want to take the risk knowingly.";
        return "Nothing in this pool clears its fees, so there's no basket to build. That's a real answer — " +
               "this search has no flip worth your cash.";
    }

    // ── Deduping ──────────────────────────────────────────────────────────────────────────────
    // The same post can arrive twice: once from the live scan and once from the pipeline, because
    // the seller tracked it earlier. Buying it twice would double its cost AND its profit in every
    // total on the board. The live scan wins — its price is what the post says today.
    private static (List<BudgetCandidate> Candidates, int Duplicates) Dedupe(List<BudgetCandidate>? input)
    {
        var kept = new List<BudgetCandidate>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var duplicates = 0;

        foreach (var candidate in input ?? [])
        {
            if (candidate is null) continue;
            var key = DedupeKey(candidate);
            if (!seen.TryGetValue(key, out var index))
            {
                seen[key] = kept.Count;
                kept.Add(candidate);
                continue;
            }

            duplicates++;
            if (candidate.Origin == BudgetOrigins.Scan && kept[index].Origin != BudgetOrigins.Scan)
                kept[index] = candidate;
        }

        return (kept, duplicates);
    }

    /// <summary>Stable identity for one buyable thing, across the boards it can arrive from.</summary>
    public static string DedupeKey(BudgetCandidate candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Source) && !string.IsNullOrWhiteSpace(candidate.Id))
            return $"{candidate.Source.Trim()}|{candidate.Id.Trim()}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(candidate.Url)) return candidate.Url.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(candidate.Id)) return candidate.Id.Trim().ToLowerInvariant();
        return $"{candidate.Title.Trim()}|{candidate.BuyPrice}".ToLowerInvariant();
    }

    // ── The solve ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// One solved objective. The value frontier stays here rather than on the plan: it is 50,000
    /// numbers the browser has no use for, and the one question it answers ("what would more money
    /// buy?") is answered into a sentence before the result leaves this service.
    /// </summary>
    private sealed record SolvedPlan(BudgetPlan Plan, long[] Frontier, int Capacity, int StretchCapacity);

    private static SolvedPlan Solve(
        string objective, List<BudgetCandidate> eligible, BudgetGrid grid, decimal spendable, DateTime today)
    {
        var plan = new BudgetPlan
        {
            Objective = objective,
            ObjectiveLabel = ObjectiveLabel(objective),
            ObjectiveNote = ObjectiveNote(objective),
            Budget = spendable,
            Spendable = spendable,
        };

        // Each objective gets the pool it can honestly answer for. A deal with no measured speed
        // has no per-day rate and no promise of fast cash, so it sits those two out rather than
        // being scored as if it were instant or as if it were dead.
        var pool = eligible.Where(c => InObjectivePool(objective, c)).ToList();

        if (pool.Count == 0)
        {
            plan.Leftover = spendable;
            plan.Headline = objective switch
            {
                ObjectiveFastCash => $"Nothing here turns your money around inside {DaysToCashEstimator.FastCashDays} days.",
                ObjectivePerDay => "None of these has a measured selling speed, so there's no per-day rate to rank them by.",
                _ => "No basket to build from these.",
            };
            return new SolvedPlan(plan, [], grid.Capacity, grid.StretchCapacity);
        }

        var weights = pool.Select(grid.Weight).ToArray();
        var values = pool.Select(c => Value(objective, c)).ToArray();
        var (chosen, frontier) = Knapsack(weights, values, grid.StretchCapacity, grid.Capacity);

        var picks = chosen.Select(i => pool[i]).ToList();
        Finish(plan, picks, spendable, today);
        return new SolvedPlan(plan, frontier, grid.Capacity, grid.StretchCapacity);
    }

    // What one line is worth under each definition of best, in integer hundredths so the knapsack
    // stays exact. Per-day divides the money by the wait — the rate the capital earns at.
    private static long Value(string objective, BudgetCandidate candidate) => objective switch
    {
        ObjectivePerDay => (long)Math.Round(candidate.TotalProfit * 100m / Math.Max(1, candidate.DaysToCash ?? 1)),
        _ => (long)Math.Round(candidate.TotalProfit * 100m),
    };

    /// <summary>
    /// Exact 0/1 knapsack. Returns the chosen items at <paramref name="readCapacity"/> and the whole
    /// value frontier, which is what makes "another $100 would add $X" answerable without a re-solve.
    /// </summary>
    /// <remarks>
    /// Ties are broken towards the cheaper basket: two ways to make the same money are not equally
    /// good, because the one that spends less leaves cash for the deal that turns up tomorrow.
    /// </remarks>
    private static (List<int> Chosen, long[] Frontier) Knapsack(
        int[] weights, long[] values, int capacity, int readCapacity)
    {
        var best = new long[capacity + 1];
        var cost = new long[capacity + 1];
        var take = new BitArray[weights.Length];

        for (var i = 0; i < weights.Length; i++)
        {
            take[i] = new BitArray(capacity + 1);
            var w = weights[i];
            var v = values[i];
            if (w > capacity || v <= 0) continue;

            for (var c = capacity; c >= w; c--)
            {
                var candidateValue = best[c - w] + v;
                var candidateCost = cost[c - w] + w;
                if (candidateValue > best[c] || (candidateValue == best[c] && candidateCost < cost[c]))
                {
                    best[c] = candidateValue;
                    cost[c] = candidateCost;
                    take[i][c] = true;
                }
            }
        }

        var chosen = new List<int>();
        var at = Math.Clamp(readCapacity, 0, capacity);
        for (var i = weights.Length - 1; i >= 0; i--)
        {
            if (!take[i][at]) continue;
            chosen.Add(i);
            at -= weights[i];
        }
        chosen.Reverse();
        return (chosen, best);
    }

    // ── Turning a chosen set into a plan someone can act on ───────────────────────────────────

    private static void Finish(BudgetPlan plan, List<BudgetCandidate> picks, decimal spendable, DateTime today)
    {
        // Presented in the order the seller should work them: fastest money back first among the
        // chosen, because the first cash back is what funds the rest of the week.
        var ordered = picks
            .OrderBy(c => DaysToCashEstimator.SortableDaysToCash(c.DaysToCash))
            .ThenByDescending(c => c.TotalProfit)
            .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rank = 1;
        decimal runningSpend = 0, runningProfit = 0;

        foreach (var candidate in ordered)
        {
            var spend = candidate.TotalCost;
            var profit = candidate.TotalProfit;
            runningSpend += spend;
            runningProfit += profit;

            var (tier, label) = candidate.DaysToCash is int d
                ? DaysToCashEstimator.TierFor(d)
                : ("unknown", "Speed unknown");

            var pick = new BudgetPick
            {
                Rank = rank++,
                Id = DedupeKey(candidate),
                Title = candidate.Title,
                Source = candidate.Source,
                SourceLabel = candidate.SourceLabel,
                Url = candidate.Url,
                ImageUrl = candidate.ImageUrl,
                Location = candidate.Location,
                DistanceMiles = candidate.DistanceMiles,
                Origin = candidate.Origin,
                OriginLabel = BudgetOrigins.Label(candidate.Origin),
                BuyPrice = candidate.BuyPrice,
                Quantity = Math.Max(1, candidate.Quantity),
                Spend = spend,
                NetProfit = candidate.NetProfit,
                TotalNetProfit = profit,
                // Re-derived from the two numbers on this line rather than carried across from the
                // board, so the percentage on the pick can never disagree with the dollars beside it.
                RoiPercent = spend > 0 ? Math.Round(profit / spend * 100m, 1) : null,
                DaysToCash = candidate.DaysToCash,
                ProfitPerDay = candidate.DaysToCash is > 0 ? Math.Round(profit / candidate.DaysToCash!.Value, 2) : null,
                SpeedTier = tier,
                SpeedLabel = label,
                CumulativeSpend = Math.Round(runningSpend, 2),
                CumulativeProfit = Math.Round(runningProfit, 2),
                MaxBuyPrice = candidate.MaxBuyPrice,
                TargetOffer = candidate.TargetOffer,
                CompCount = candidate.CompCount,
                ConfidenceScore = candidate.ConfidenceScore,
                Verdict = candidate.Verdict,
            };

            if (candidate.TargetOffer is decimal target && target > 0 && target < candidate.BuyPrice)
                pick.NegotiationUpside = Math.Round((candidate.BuyPrice - target) * pick.Quantity, 2);

            pick.Why = WhyPicked(pick);
            plan.Picks.Add(pick);
        }

        plan.CapitalDeployed = Math.Round(runningSpend, 2);
        plan.Leftover = Math.Round(spendable - plan.CapitalDeployed, 2);
        plan.TotalNetProfit = Math.Round(runningProfit, 2);
        plan.BlendedRoiPercent = plan.CapitalDeployed > 0
            ? Math.Round(plan.TotalNetProfit / plan.CapitalDeployed * 100m, 1) : null;

        plan.NegotiationUpside = Math.Round(plan.Picks.Sum(p => p.NegotiationUpside ?? 0m), 2);
        plan.NegotiableCount = plan.Picks.Count(p => p.NegotiationUpside > 0);

        var timed = plan.Picks.Where(p => p.DaysToCash is > 0).ToList();
        plan.UnknownSpeedCount = plan.Picks.Count - timed.Count;

        if (timed.Count > 0)
        {
            plan.FastestDaysToCash = timed.Min(p => p.DaysToCash!.Value);
            plan.SlowestDaysToCash = timed.Max(p => p.DaysToCash!.Value);
            plan.FirstCashBackBy = today.AddDays(plan.FastestDaysToCash!.Value).ToString("MMM d");

            // Weighted by the CAPITAL in each line, not by the number of lines: the question is how
            // long the money is gone, and a $400 slow flip ties up more of it than a $20 fast one.
            var weightedSpend = timed.Sum(p => p.Spend);
            if (weightedSpend > 0)
            {
                var weightedDays = Math.Round(timed.Sum(p => p.Spend * p.DaysToCash!.Value) / weightedSpend, 1);
                plan.WeightedDaysToCash = weightedDays;
                if (weightedDays > 0)
                {
                    plan.CapitalTurnsPerYear = Math.Round(365m / weightedDays, 1);
                    plan.ProfitPerDay = Math.Round(plan.TotalNetProfit / weightedDays, 2);
                    if (plan.BlendedRoiPercent is > 0)
                        plan.AnnualizedRoiPercent = Math.Min(DaysToCashEstimator.MaxAnnualizedRoiPercent,
                            Math.Round(plan.BlendedRoiPercent.Value * 365m / weightedDays, 0));
                }
            }
        }

        // A date is only promised when every line in the basket has a measured speed behind it.
        // One unmeasured deal and the honest answer is "we don't know when the last of it lands".
        if (plan.Picks.Count > 0 && plan.UnknownSpeedCount == 0)
            plan.AllCashBackBy = today.AddDays(plan.SlowestDaysToCash!.Value).ToString("MMM d");

        plan.Headline = Headline(plan);
        plan.Note = PlanNote(plan);
    }

    private static string WhyPicked(BudgetPick pick)
    {
        var parts = new List<string>
        {
            $"{Money(pick.TotalNetProfit)} net on a {Money(pick.Spend)} buy" +
            (pick.RoiPercent is decimal roi ? $" ({roi:0}% ROI)" : ""),
        };

        if (pick.DaysToCash is int days)
            parts.Add($"cash back in ~{days} days" +
                      (pick.ProfitPerDay is > 0 ? $" — {Money(pick.ProfitPerDay!.Value)} a day while it's tied up" : ""));
        else
            parts.Add("no measured selling speed, so the wait is unknown");

        if (pick.CompCount > 0) parts.Add($"{pick.CompCount} sold comps behind it");
        return string.Join(" · ", parts) + ".";
    }

    private static string Headline(BudgetPlan plan)
    {
        if (plan.Picks.Count == 0)
            return $"Nothing in this pool is worth spending the {Money(plan.Spendable)} on.";

        var head = $"Buy these {plan.Picks.Count} for {Money(plan.CapitalDeployed)} → {Money(plan.TotalNetProfit)} net" +
                   (plan.BlendedRoiPercent is decimal roi ? $", {roi:0}% on the cash you put in" : "") + ".";

        if (plan.AllCashBackBy is string by) return $"{head} All of it back by {by}.";
        if (plan.FirstCashBackBy is string first)
            return $"{head} First money back around {first}; {plan.UnknownSpeedCount} of them have no measured speed.";
        return $"{head} None of these has a measured selling speed, so there's no date on the money.";
    }

    private static string PlanNote(BudgetPlan plan)
    {
        if (plan.Picks.Count == 0) return "";

        var bits = new List<string>();
        if (plan.Leftover > 0) bits.Add($"{Money(plan.Leftover)} of your budget stays in your pocket");
        if (plan.WeightedDaysToCash is decimal days)
            bits.Add($"the money is tied up about {days:0.#} days on average, weighted by what each one costs");
        if (plan.CapitalTurnsPerYear is decimal turns)
            bits.Add($"roughly {turns:0.#} turns of this cash a year at that pace");
        if (plan.NegotiationUpside > 0)
            bits.Add($"{Money(plan.NegotiationUpside)} more if all {plan.NegotiableCount} sellers took your opening offer");

        if (bits.Count == 0) return "";
        var joined = string.Join(" · ", bits);
        return char.ToUpperInvariant(joined[0]) + joined[1..] + ".";
    }

    private static string ObjectiveLabel(string objective) => objective switch
    {
        ObjectiveFastCash => "Fastest cash back",
        ObjectivePerDay => "Hardest-working cash",
        _ => "Most money",
    };

    private static string ObjectiveNote(string objective) => objective switch
    {
        ObjectiveFastCash =>
            $"Only deals whose money is back inside {DaysToCashEstimator.FastCashDays} days, then the most " +
            "profit that fits among those. Less total money, sooner, and available to spend again.",
        ObjectivePerDay =>
            "The most profit per day your cash is tied up. Deals with no measured selling speed can't be " +
            "ranked this way and sit this one out.",
        _ => "The largest total profit the budget can buy, whenever each piece of it lands.",
    };

    // ── What the seller would otherwise have done ─────────────────────────────────────────────
    // The whole justification for this screen, computed honestly: buy down the ranked board taking
    // whatever still fits. That is what a person does, and it is what the optimizer has to beat.
    private static BudgetComparison CompareWithBuyingDownTheList(
        List<BudgetCandidate> eligible, decimal spendable, BudgetPlan plan)
    {
        decimal spent = 0, profit = 0;
        var picks = 0;

        foreach (var candidate in eligible.OrderByDescending(c => c.TotalProfit)
                     .ThenBy(c => c.TotalCost)
                     .ThenBy(c => c.Title, StringComparer.OrdinalIgnoreCase))
        {
            if (spent + candidate.TotalCost > spendable) continue;
            spent += candidate.TotalCost;
            profit += candidate.TotalProfit;
            picks++;
        }

        var comparison = new BudgetComparison
        {
            Picks = picks,
            CapitalDeployed = Math.Round(spent, 2),
            TotalNetProfit = Math.Round(profit, 2),
            Leftover = Math.Round(spendable - spent, 2),
            ExtraProfit = Math.Round(plan.TotalNetProfit - profit, 2),
        };

        comparison.ExtraProfitPercent = profit > 0
            ? Math.Round(comparison.ExtraProfit / profit * 100m, 1) : null;

        comparison.Note = comparison.ExtraProfit > 0
            ? $"Buying straight down the list spends {Money(comparison.CapitalDeployed)} for " +
              $"{Money(comparison.TotalNetProfit)}. This basket makes {Money(comparison.ExtraProfit)} more " +
              "out of the same money."
            : "Buying straight down the list happens to land on the same money here — this basket has " +
              "nothing extra to add on top of it.";

        return comparison;
    }

    // The marginal value of more cash, read straight off the knapsack's own frontier. Answers the
    // question that follows every budget: "what if I had a bit more?"
    private static BudgetStretch? StretchFor(SolvedPlan solved, decimal spendable)
    {
        if (solved.Frontier is not { Length: > 0 } frontier || solved.Capacity <= 0) return null;

        var at = Math.Clamp(solved.Capacity, 0, frontier.Length - 1);
        var stretchAt = Math.Clamp(solved.StretchCapacity, 0, frontier.Length - 1);
        var extraProfit = Math.Round((frontier[stretchAt] - frontier[at]) / 100m, 2);
        var extraBudget = Math.Round(spendable * StretchFraction, 2);
        if (extraBudget <= 0) return null;

        return new BudgetStretch
        {
            ExtraBudget = extraBudget,
            ExtraProfit = extraProfit,
            Note = extraProfit > 0
                ? $"Another {Money(extraBudget)} would buy {Money(extraProfit)} more profit out of the same deals."
                : $"Another {Money(extraBudget)} buys nothing extra here — this pool is already fully bought.",
        };
    }

    private static string Money(decimal value) =>
        value == Math.Floor(value) ? $"${value:N0}" : $"${value:N2}";

    // ── The budget grid ───────────────────────────────────────────────────────────────────────
    // The knapsack needs integer weights, so the budget is divided into cells. Cells are cents when
    // the budget is small enough for that to fit the solve, and coarser on a big budget. Item costs
    // always round UP into the grid, so the basket can never cost more than the seller said they
    // have — the rounding error is spent on safety, not on optimism.
    private sealed record BudgetGrid(int UnitCents, int Capacity, int StretchCapacity)
    {
        public static BudgetGrid For(decimal spendable)
        {
            var cents = (long)Math.Round(spendable * 100m);
            var stretchCents = (long)Math.Round(spendable * (1m + StretchFraction) * 100m);
            var unit = (int)Math.Max(1, Math.Ceiling(stretchCents / (decimal)MaxGridCells));
            return new BudgetGrid(unit, (int)(cents / unit), (int)(stretchCents / unit));
        }

        public int Weight(BudgetCandidate candidate) =>
            (int)Math.Ceiling((long)Math.Round(candidate.TotalCost * 100m) / (decimal)UnitCents);
    }
}
