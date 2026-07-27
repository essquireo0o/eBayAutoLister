using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns stored deals plus the completed sales from Money Made into the pipeline board: where each
/// flip is, what money is in motion, what has actually come back, and what to do next.
/// </summary>
/// <remarks>
/// Pure — no database, no clock, no eBay. The current time is passed in so a board can be tested
/// at any point in a deal's life, which is the only way the stall and overdue rules can be checked
/// at all.
///
/// It computes no profit of its own. Realized profit arrives already worked out in
/// <see cref="FlipProfit"/>, which carries the fee eBay actually charged; this file only decides
/// which sales belong to which deal, and refuses to guess when it can't tell.
/// </remarks>
public sealed class DealPipelineCalculator
{
    /// <summary>Days a bought deal may sit unlisted before it counts as stalled capital.</summary>
    /// <remarks>
    /// Three days, not one. Photographing and writing a listing is real work and a same-week
    /// turnaround is normal; flagging a deal the morning after it was bought would train the seller
    /// to ignore the flag, which is how the genuinely stuck $1,200 goes unnoticed.
    /// </remarks>
    public const int ListingGraceDays = 3;

    /// <summary>Days unlisted before the stall is urgent rather than a nudge.</summary>
    public const int ListingUrgentDays = 14;

    /// <summary>How long a listing gets before "hasn't sold" becomes a problem, absent a forecast.</summary>
    /// <remarks>
    /// Only used when the deal carries no days-to-cash projection. Where it does, the deal's own
    /// forecast is the deadline — a part that was always going to take four months is not overdue
    /// at day 46, and holding every listing to one number is how a repricing prompt becomes noise.
    /// </remarks>
    public const int DefaultSellWindowDays = 45;

    /// <summary>Days a sourced deal sits before it's worth asking whether it's still available.</summary>
    public const int SourcedStaleDays = 7;

    public DealPipelineResult Build(
        IReadOnlyList<DealRecord> deals, IReadOnlyList<FlipProfit> flips, DateTimeOffset now)
    {
        var paidFlips = flips.Where(f => f.Status == "paid").OrderBy(f => f.SoldUtc).ToList();
        var claimed = new HashSet<long>();

        // Oldest deal first, so when two deals could claim the same sale the one that was in the
        // pipeline first gets it. Arbitrary either way, but stable — a board whose totals shuffle
        // between two refreshes is a board nobody trusts.
        var cards = deals
            .OrderBy(d => d.CreatedUtc).ThenBy(d => d.Id)
            .Select(deal => BuildCard(deal, paidFlips, claimed, now))
            .ToList();

        var result = new DealPipelineResult
        {
            Deals = cards.OrderByDescending(c => SortWeight(c)).ThenByDescending(c => c.Deal.UpdatedUtc).ToList(),
            Stages = BuildStages(cards),
            Summary = BuildSummary(cards, now),
        };

        result.NextActions = cards
            .Where(c => c.NextAction is not null)
            .Select(c => c.NextAction!)
            .OrderByDescending(a => UrgencyRank(a.Urgency))
            .ThenByDescending(a => a.AmountAtStake)
            .ToList();

        result.Honesty = BuildHonesty(result.Summary);
        return result;
    }

    // ── One card ──────────────────────────────────────────────────────────────────────────────

    private DealCard BuildCard(
        DealRecord deal, IReadOnlyList<FlipProfit> paidFlips, HashSet<long> claimed, DateTimeOffset now)
    {
        var card = new DealCard { Deal = deal };

        var matched = MatchSales(deal, paidFlips, claimed);
        foreach (var flip in matched) claimed.Add(flip.Id);

        card.FlipIds = matched.Select(f => f.Id).ToList();
        card.UnitsSold = matched.Sum(f => Math.Max(1, f.Quantity));
        card.RealizedRevenue = Round(matched.Sum(f => f.GrossRevenue));
        card.SalesAwaitingCost = matched.Count(f => f.NetProfit is null);

        var withProfit = matched.Where(f => f.NetProfit.HasValue).ToList();
        card.RealizedProfit = withProfit.Count > 0 ? Round(withProfit.Sum(f => f.NetProfit!.Value)) : null;

        // A matched eBay sale is the strongest evidence there is about where a deal stands, and it
        // beats whatever the seller last clicked. Nothing is written back — the stored stage stays
        // the record of what was said, and the card says the move wasn't theirs.
        //
        // Only a FULLY sold deal moves, though. Two units of a four-unit lot is not a closed deal,
        // and calling it one retires the other two units' capital from the board — the seller's
        // money would vanish from "at risk" while it was still sitting in their garage.
        card.Stage = deal.Stage;
        if (card.UnitsSold >= deal.Quantity && deal.Stage is DealStages.Sourced or DealStages.Bought or DealStages.Listed)
        {
            card.Stage = DealStages.Sold;
            card.StageAutoDerived = true;
        }
        card.StageLabel = DealStages.Label(card.Stage);

        // ── The money ─────────────────────────────────────────────────────────────────────────

        if (deal.PurchasePrice.HasValue)
            card.CapitalSpent = Round(deal.PurchasePrice.Value * deal.Quantity + deal.PurchaseExtraCost);

        var unsoldFraction = deal.Quantity <= 0
            ? 0m
            : Math.Clamp((deal.Quantity - card.UnitsSold) / (decimal)deal.Quantity, 0m, 1m);

        // Capital comes off the board as the units sell, not when a card gets moved. Two
        // exceptions, both of them "this money is settled": a write-off is a loss the seller can't
        // get back, and a deal the seller closed by hand with no sales data to contradict them is
        // one we take at their word.
        card.CapitalAtRisk = card.Stage == DealStages.Dropped || (card.Stage == DealStages.Sold && matched.Count == 0)
            ? 0m
            : Round(card.CapitalSpent * unsoldFraction);

        if (deal.ProjectedNetProfit.HasValue)
        {
            card.ForecastProfit = Round(deal.ProjectedNetProfit.Value * deal.Quantity);

            if (deal.PurchasePrice.HasValue)
            {
                // Net profit moves exactly one dollar per dollar paid — the identity behind the
                // max-buy price in LocalArbitrageAnalyzer — so rebasing on the real purchase price
                // is arithmetic, not a second forecast. The sale-side estimate is untouched.
                var perUnit = deal.AskPrice.HasValue
                    ? deal.ProjectedNetProfit.Value + (deal.AskPrice.Value - deal.PurchasePrice.Value)
                    : deal.ProjectedNetProfit.Value;
                card.ExpectedProfit = Round(perUnit * deal.Quantity - deal.PurchaseExtraCost);
            }
        }

        if (deal.AskPrice.HasValue && deal.PurchasePrice.HasValue)
            card.NegotiatedSaving = Round((deal.AskPrice.Value - deal.PurchasePrice.Value) * deal.Quantity);

        // Graded only when the deal is fully closed. A 3-of-10 partial sale measured against a
        // 10-unit forecast reports a 70% miss on a deal that is going perfectly.
        if (card.Stage == DealStages.Sold && card.RealizedProfit.HasValue && card.ExpectedProfit.HasValue
            && card.SalesAwaitingCost == 0 && card.UnitsSold >= deal.Quantity)
        {
            card.ProfitVariance = Round(card.RealizedProfit.Value - card.ExpectedProfit.Value);
            if (card.ExpectedProfit.Value != 0)
                card.ProfitVariancePercent = Math.Round(
                    card.ProfitVariance.Value / Math.Abs(card.ExpectedProfit.Value) * 100m, 1);
        }

        // ── Time ──────────────────────────────────────────────────────────────────────────────

        card.DaysInStage = DaysBetween(StageSince(deal, card, matched), now);
        card.DaysTracked = DaysBetween(deal.CreatedUtc, now);

        var lastSale = matched.Count > 0 ? matched.Max(f => f.SoldUtc) : (DateTimeOffset?)null;
        if (lastSale.HasValue && deal.BoughtUtc.HasValue && lastSale > deal.BoughtUtc)
            card.DaysToCashActual = DaysBetween(deal.BoughtUtc.Value, lastSale.Value);

        card.Flags = BuildFlags(card);
        card.NextAction = BuildAction(card, now);
        return card;
    }

    /// <summary>
    /// Finds the completed sales that belong to a deal, joining on listing ID and SKU.
    /// </summary>
    /// <remarks>
    /// Two rules stop this from inventing money:
    ///
    ///   * <b>Capped at the deal's quantity.</b> A listing ID that has sold fourteen times over two
    ///     years is fourteen sales; a one-unit deal against that listing owns exactly one of them.
    ///     Attributing all fourteen would report a single $60 flip as $840 of realized profit.
    ///   * <b>Bounded to the deal's own lifetime.</b> Only sales on or after the earliest date the
    ///     deal knows about count, so tracking a relisted item today doesn't claim last year's sales
    ///     of the same listing.
    ///
    /// A sale already claimed by an earlier deal is never claimed again, so two deals against one
    /// listing split its sales instead of both counting all of them.
    /// </remarks>
    public static List<FlipProfit> MatchSales(
        DealRecord deal, IReadOnlyList<FlipProfit> paidFlips, HashSet<long>? claimed = null)
    {
        var hasListing = !string.IsNullOrWhiteSpace(deal.ListingId);
        var hasSku = !string.IsNullOrWhiteSpace(deal.Sku);
        if (!hasListing && !hasSku) return [];

        var since = EarliestKnownDate(deal);

        var candidates = paidFlips
            .Where(f => claimed is null || !claimed.Contains(f.Id))
            .Where(f => f.SoldUtc >= since)
            .Where(f => (hasListing && string.Equals(f.ListingId, deal.ListingId, StringComparison.OrdinalIgnoreCase))
                     || (hasSku && string.Equals(f.Sku, deal.Sku, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(f => f.SoldUtc)
            .ToList();

        var taken = new List<FlipProfit>();
        var units = 0;
        foreach (var flip in candidates)
        {
            if (units >= deal.Quantity) break;
            taken.Add(flip);
            units += Math.Max(1, flip.Quantity);
        }
        return taken;
    }

    // The lower bound on which sales a deal may claim. Bought first, because that is when the
    // seller owned the thing: a deal entered retroactively for a purchase made in March can
    // legitimately claim an April sale, and keying off the row's creation date would refuse it.
    private static DateTimeOffset EarliestKnownDate(DealRecord deal)
    {
        var dates = new[] { deal.BoughtUtc, deal.ListedUtc, deal.SoldUtc, deal.CreatedUtc }
            .Where(d => d.HasValue && d.Value != default)
            .Select(d => d!.Value)
            .ToList();
        return dates.Count > 0 ? dates.Min() : DateTimeOffset.MinValue;
    }

    private static DateTimeOffset StageSince(DealRecord deal, DealCard card, List<FlipProfit> matched)
    {
        // A card the sales data moved was never clicked, so the stored stage timestamp describes
        // the stage it left, not the one it's in.
        if (card.StageAutoDerived && matched.Count > 0) return matched.Max(f => f.SoldUtc);

        var stamped = card.Stage switch
        {
            DealStages.Sold => deal.SoldUtc,
            DealStages.Listed => deal.ListedUtc,
            DealStages.Bought => deal.BoughtUtc,
            _ => null,
        };

        // The date the stage was REACHED wins over the date the card was clicked, and the two are
        // different exactly when it matters most: a seller entering a purchase they made in January
        // has had that money out for six months, and measuring from the click reports zero days in
        // stage — hiding the largest pile of stalled cash on the board behind the newest card.
        // A repeat move within the same stage re-stamps its own date, so nothing goes stale here.
        return stamped ?? (deal.StageChangedUtc == default ? deal.CreatedUtc : deal.StageChangedUtc);
    }

    // ── Flags: things worth knowing that aren't actions ───────────────────────────────────────

    private static List<string> BuildFlags(DealCard card)
    {
        var deal = card.Deal;
        var flags = new List<string>();

        // The most expensive mistake this app can catch after the fact: the seller went to the
        // driveway with a ceiling and came back having paid past it.
        if (deal.PurchasePrice.HasValue && deal.MaxBuyPrice is > 0 && deal.PurchasePrice > deal.MaxBuyPrice)
        {
            var over = Round(deal.PurchasePrice.Value - deal.MaxBuyPrice.Value);
            flags.Add($"Paid {over:C0} over the {deal.MaxBuyPrice:C0} break-even ceiling — this one has to sell above the forecast to make money.");
        }

        if (card.NegotiatedSaving is > 0)
            flags.Add($"Haggled {card.NegotiatedSaving:C0} off the asking price — that's profit with no fee and no wait on it.");

        if (deal.ProjectedNetProfit is <= 0 && card.Stage == DealStages.Sourced)
            flags.Add("The forecast says this doesn't clear its costs at the asking price.");

        if (card.Stage == DealStages.Sold && card.ProfitVariance.HasValue)
        {
            var v = card.ProfitVariance.Value;
            flags.Add(v >= 0
                ? $"Beat its forecast by {v:C0}."
                : $"Came in {Math.Abs(v):C0} under its forecast.");
        }

        if (card.UnitsSold > 0 && card.UnitsSold < deal.Quantity)
            flags.Add($"{card.UnitsSold} of {deal.Quantity} sold — {card.CapitalAtRisk:C0} still out.");

        return flags;
    }

    // ── The one thing to do about this deal next ──────────────────────────────────────────────

    private static DealAction? BuildAction(DealCard card, DateTimeOffset now)
    {
        var deal = card.Deal;
        DealAction Action(string label, string detail, string urgency, decimal amount, string target = "pipeline") =>
            new()
            {
                DealId = deal.Id, Title = deal.Title, Stage = card.Stage,
                Label = label, Detail = detail, Urgency = urgency,
                AmountAtStake = Round(amount), Target = target,
            };

        switch (card.Stage)
        {
            case DealStages.Sourced:
            {
                // Nothing to chase on a deal the forecast already says loses money. The most useful
                // thing a pipeline can do with a bad deal is not nag anyone to go and buy it.
                if (deal.ProjectedNetProfit is <= 0) return null;

                var upside = card.ForecastProfit ?? 0m;
                var ceiling = deal.MaxBuyPrice is > 0 ? $" Don't go above {deal.MaxBuyPrice:C0}." : "";

                if (card.DaysInStage >= SourcedStaleDays * 3)
                    return Action("Buy it or drop it",
                        $"Tracked {card.DaysInStage} days ago and still sitting here. {upside:C0} projected — decide, or clear it off the board.",
                        "normal", upside, "source");

                if (card.DaysInStage >= SourcedStaleDays)
                    return Action("Check it's still there",
                        $"Found {card.DaysInStage} days ago. Local posts go stale fast, and {upside:C0} of projected profit goes with it.{ceiling}",
                        "normal", upside, "source");

                return Action("Go get it",
                    $"{upside:C0} projected profit.{ceiling}",
                    "normal", upside, "source");
            }

            case DealStages.Bought:
            {
                // The single most valuable prompt on the board. Money that has left the bank and
                // isn't listed yet is earning nothing, and nothing else in the app notices.
                var urgency = card.DaysInStage >= ListingUrgentDays ? "urgent"
                            : card.DaysInStage >= ListingGraceDays ? "warn" : "normal";

                var detail = card.DaysInStage >= ListingGraceDays
                    ? $"{card.CapitalSpent:C0} has been sitting unlisted for {card.DaysInStage} days. It earns nothing until it's up."
                    : $"{card.CapitalSpent:C0} spent. It starts working the day it's listed.";

                return Action("List it", detail, urgency, card.CapitalSpent, "listings");
            }

            case DealStages.Listed:
            {
                // Without a listing ID nothing can join the sale back to this deal, so the card
                // would sit in Listed forever and the realized profit would never appear.
                if (string.IsNullOrWhiteSpace(deal.ListingId) && string.IsNullOrWhiteSpace(deal.Sku))
                    return Action("Add the eBay listing ID",
                        "Without it the sale can't find its way back to this deal, and the profit never lands on the board.",
                        "warn", card.ExpectedProfit ?? card.CapitalSpent);

                var window = deal.ProjectedDaysToCash is > 0 ? deal.ProjectedDaysToCash.Value : DefaultSellWindowDays;
                if (card.DaysInStage <= window) return null;

                var basis = deal.ProjectedDaysToCash is > 0
                    ? $"the {window} days this was forecast to take"
                    : $"{window} days";

                return Action("Reprice it or send offers",
                    $"Listed {card.DaysInStage} days — past {basis}. {card.CapitalAtRisk:C0} is still tied up in it.",
                    card.DaysInStage > window * 2 ? "urgent" : "warn",
                    card.CapitalAtRisk, "inventory");
            }

            case DealStages.Sold:
            {
                // Real profit the seller has already made that isn't being counted, because the one
                // number eBay can't supply is missing — and here, the pipeline already has it.
                if (card.SalesAwaitingCost > 0 && deal.PurchasePrice.HasValue)
                    return Action("Apply what you paid",
                        $"{card.SalesAwaitingCost} sale{(card.SalesAwaitingCost == 1 ? "" : "s")} of this item {(card.SalesAwaitingCost == 1 ? "isn't" : "aren't")} counted in Money Made yet. You paid {deal.PurchasePrice:C2} — one click prices {(card.SalesAwaitingCost == 1 ? "it" : "them")}.",
                        "warn", card.RealizedRevenue, "pipeline");

                if (card.SalesAwaitingCost > 0 || (!deal.PurchasePrice.HasValue && card.RealizedRevenue > 0))
                    return Action("Record what you paid",
                        $"{card.RealizedRevenue:C0} came in on this deal, but with no record of what it cost, none of it can be counted as profit.",
                        "warn", card.RealizedRevenue, "pipeline");

                return null;
            }

            default:
                return null;
        }
    }

    // ── Board columns ─────────────────────────────────────────────────────────────────────────

    private static List<DealStageSummary> BuildStages(List<DealCard> cards) =>
        DealStages.Board.Select(stage =>
        {
            var inStage = cards.Where(c => c.Stage == stage).ToList();
            return new DealStageSummary
            {
                Stage = stage,
                Label = DealStages.Label(stage),
                Count = inStage.Count,
                Units = inStage.Sum(c => c.Quantity),
                Capital = Round(inStage.Sum(c => stage == DealStages.Sold ? c.CapitalSpent : c.CapitalAtRisk)),
                // Sourced shows the forecast as made; past that it's rebased on what was paid, and
                // a deal with no forecast contributes nothing rather than a zero that reads as one.
                ProjectedProfit = Round(inStage.Sum(c => (stage == DealStages.Sourced ? c.ForecastProfit : c.ExpectedProfit) ?? 0m)),
                RealizedProfit = Round(inStage.Sum(c => c.RealizedProfit ?? 0m)),
            };
        }).ToList();

    // ── The headline ──────────────────────────────────────────────────────────────────────────

    private static DealPipelineSummary BuildSummary(List<DealCard> cards, DateTimeOffset now)
    {
        var summary = new DealPipelineSummary
        {
            TotalDeals = cards.Count,
            ActiveDeals = cards.Count(c => c.Stage is DealStages.Sourced or DealStages.Bought or DealStages.Listed),
            CapitalAtRisk = Round(cards.Sum(c => c.CapitalAtRisk)),
            CapitalDeployedAllTime = Round(cards.Sum(c => c.CapitalSpent)),
            // Only money already spent counts as "in motion". A forecast on something nobody has
            // bought yet is a shopping list, and adding the two together is how a pipeline ends up
            // reporting profit on deals that never happened.
            ProjectedProfitInMotion = Round(cards
                .Where(c => c.Stage is DealStages.Bought or DealStages.Listed)
                .Sum(c => c.ExpectedProfit ?? 0m)),
            ProjectedProfitSourced = Round(cards
                .Where(c => c.Stage == DealStages.Sourced)
                .Sum(c => c.ForecastProfit ?? 0m)),
            RealizedProfit = Round(cards.Sum(c => c.RealizedProfit ?? 0m)),
            RealizedRevenue = Round(cards.Sum(c => c.RealizedRevenue)),
            SalesAwaitingCost = cards.Sum(c => c.SalesAwaitingCost),
        };

        var closed = cards.Where(c => c.Stage == DealStages.Sold && c.RealizedProfit.HasValue).ToList();
        summary.DealsClosed = closed.Count;
        summary.DealsProfitable = closed.Count(c => c.RealizedProfit > 0);
        summary.DealsAtALoss = closed.Count(c => c.RealizedProfit < 0);

        var graded = cards.Where(c => c.ProfitVariance.HasValue).ToList();
        summary.GradedDeals = graded.Count;
        summary.GradedForecastProfit = Round(graded.Sum(c => c.ExpectedProfit ?? 0m));
        summary.GradedRealizedProfit = Round(graded.Sum(c => c.RealizedProfit ?? 0m));
        summary.ForecastDelta = Round(summary.GradedRealizedProfit - summary.GradedForecastProfit);
        // A percentage against a zero or negative forecast is noise, not a grade.
        if (graded.Count > 0 && summary.GradedForecastProfit > 0)
            summary.ForecastAccuracyPercent =
                Math.Round(summary.GradedRealizedProfit / summary.GradedForecastProfit * 100m, 1);

        var stalled = cards.Where(c => c.Stage == DealStages.Bought && c.DaysInStage >= ListingGraceDays).ToList();
        summary.StalledDeals = stalled.Count;
        summary.StalledCapital = Round(stalled.Sum(c => c.CapitalAtRisk));

        var overdue = cards.Where(c => c.Stage == DealStages.Listed
            && c.DaysInStage > (c.Deal.ProjectedDaysToCash is > 0 ? c.Deal.ProjectedDaysToCash!.Value : DefaultSellWindowDays)).ToList();
        summary.OverdueDeals = overdue.Count;
        summary.OverdueCapital = Round(overdue.Sum(c => c.CapitalAtRisk));

        // Three is the smallest sample where a median means anything; below that it is just one of
        // the two numbers, wearing a statistic's name.
        var cycles = cards.Where(c => c.DaysToCashActual.HasValue).Select(c => c.DaysToCashActual!.Value).OrderBy(d => d).ToList();
        if (cycles.Count >= 3) summary.MedianDaysToCash = cycles[cycles.Count / 2];

        if (cards.Count > 0)
        {
            summary.FirstDealUtc = cards.Min(c => c.Deal.CreatedUtc);
            summary.LastActivityUtc = cards.Max(c => c.Deal.UpdatedUtc);
        }

        return summary;
    }

    private static List<string> BuildHonesty(DealPipelineSummary s)
    {
        var lines = new List<string>
        {
            "Projected profit is a forecast from sold comps at the moment the deal was tracked. It is never added to what you've actually made, on this page or anywhere else in the app.",
            $"{s.CapitalAtRisk:C0} at risk is money you told us you actually paid — purchase price plus the extras — on deals that haven't come back yet.",
        };

        if (s.RealizedProfit != 0 || s.DealsClosed > 0)
            lines.Add("Realized profit comes from your completed sales in Money Made, which use the fee eBay actually charged rather than an estimated rate.");

        if (s.GradedDeals > 0 && s.ForecastAccuracyPercent.HasValue)
        {
            var pct = s.ForecastAccuracyPercent.Value;
            var verdict = pct >= 98m && pct <= 102m ? "came in on the money"
                : pct > 102m ? $"came in {Math.Round(pct - 100m)}% better than forecast"
                : $"came in {Math.Round(100m - pct)}% under forecast";
            lines.Add($"Across {s.GradedDeals} closed deal{(s.GradedDeals == 1 ? "" : "s")} with a forecast, the app's projections {verdict} — {s.GradedRealizedProfit:C0} realized against {s.GradedForecastProfit:C0} projected.");
        }
        else if (s.DealsClosed > 0)
        {
            lines.Add("No closed deal yet has both a forecast and a recorded cost, so there's nothing to grade the projections against. Once one does, this line reports how close they were.");
        }

        if (s.SalesAwaitingCost > 0)
            lines.Add($"{s.SalesAwaitingCost} completed sale{(s.SalesAwaitingCost == 1 ? "" : "s")} on this board {(s.SalesAwaitingCost == 1 ? "has" : "have")} no cost recorded, so {(s.SalesAwaitingCost == 1 ? "its" : "their")} profit isn't counted anywhere yet.");

        lines.Add("Your own time and driving aren't costed here — only money that left your account.");
        return lines;
    }

    // ── Ordering ──────────────────────────────────────────────────────────────────────────────

    // Cards sort by what they're worth doing something about: an urgent action outranks a big
    // number, because the point of the board is the next move, not the leaderboard.
    private static decimal SortWeight(DealCard card)
    {
        var urgency = UrgencyRank(card.NextAction?.Urgency ?? "none") * 1_000_000m;
        var money = Math.Max(card.CapitalAtRisk, Math.Abs(card.ExpectedProfit ?? card.ForecastProfit ?? 0m));
        return urgency + Math.Min(money, 999_999m);
    }

    private static int UrgencyRank(string urgency) => urgency switch
    {
        "urgent" => 3,
        "warn" => 2,
        "normal" => 1,
        _ => 0,
    };

    private static int DaysBetween(DateTimeOffset from, DateTimeOffset to) =>
        from == default || to < from ? 0 : (int)Math.Floor((to - from).TotalDays);

    private static decimal Round(decimal value) => Math.Round(value, 2);
}
