using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The undervalued-auction sniper: turns one live eBay listing plus real sold history into the
/// only two numbers a bidder needs — the most to bid, and what winning at that price is worth.
///
/// Buying on eBay to sell on eBay is the shortest arbitrage there is: no drive, no negotiation, no
/// waiting on a stranger to answer a message, and the resale side is the marketplace whose sold
/// data priced the buy. What makes it hard is that an auction's price is a moving object, and the
/// arithmetic that looks best is nearly always the arithmetic on a price that hasn't happened yet.
/// </summary>
/// <remarks>
/// Pure except for <see cref="Build"/>, which delegates every dollar to the shared
/// <see cref="ProfitCalculator"/> via <see cref="JackpotHunter.BreakEvenBuyPrice"/> — so a flip
/// bought at auction is costed by exactly the same rules as one bought off Craigslist, and the
/// thresholds that decide "worth doing" are <see cref="LocalArbitrageAnalyzer"/>'s, not a second,
/// friendlier set for the feature with a countdown on it.
/// </remarks>
public sealed class AuctionSniperAnalyzer(ProfitCalculator profitCalc, JackpotHunter hunter)
{
    /// <summary>
    /// How close to the end an auction has to be before its current price is treated as a price.
    /// Outside this window a row is <c>too_early</c> however large the apparent margin: a $12 bid
    /// with three days to run is not a $12 item, and a board that scores it as one is a board that
    /// will be wrong most times it is believed.
    /// </summary>
    public const int PriceIsRealHours = 24;

    /// <summary>Inside this, the bidding is effectively over and the clock is the story.</summary>
    public const int ClosingMinutes = 60;

    /// <summary>Bid this many seconds before the close. Bidding sooner only raises the price.</summary>
    public const int SnipeSecondsBeforeEnd = 20;

    // Evidence bars. The same ones Local Deals and Roll the Dice use — one definition of "enough
    // sold history to bet money on", not a looser one here because the row has a countdown on it.
    public const int MinCompsToBid = 3;
    public const int MinCompsToSnipe = LocalArbitrageAnalyzer.GoldmineMinComps;
    public const int MinConfidenceToSnipe = LocalArbitrageAnalyzer.GoldmineMinConfidence;

    /// <summary>A bid count at which the seller is no longer bidding against an empty room.</summary>
    public const int CrowdedBidCount = 5;
    /// <summary>Below this, the account is too new for a bargain to be taken at face value.</summary>
    public const int LowFeedbackScore = 10;
    public const decimal LowFeedbackPercent = 95m;

    public const string VerdictSnipe = "snipe";
    public const string VerdictTooEarly = "too_early";
    public const string VerdictWatch = "watch";
    public const string VerdictThin = "thin";
    public const string VerdictPass = "pass";
    public const string VerdictEnded = "ended";
    public const string VerdictNoData = "no_data";

    /// <summary>Which of the two bars set a ceiling — the return on the money, or the cash.</summary>
    public const string CeilingByRoi = "roi";
    public const string CeilingByCash = "cash";

    /// <summary>
    /// The most to bid, given the highest all-in cost that breaks even. Strictest of "clears
    /// <see cref="LocalArbitrageAnalyzer.SolidProfit"/> in cash" and "returns
    /// <see cref="LocalArbitrageAnalyzer.SolidRoiPercent"/> on the money", with the shipping the
    /// buyer pays taken off the top — shipping is part of what winning costs, so it comes out of
    /// the bid, not out of the profit afterwards.
    /// </summary>
    /// <remarks>
    /// Truncated to the cent rather than rounded. A ceiling rounded up is a ceiling that quietly
    /// gives away the last cent of the margin it exists to protect.
    /// </remarks>
    public static decimal MaxBidFor(
        decimal breakEvenAllIn, decimal shippingCost,
        decimal? targetRoiPercent = null, decimal buyerFeePercent = 0m) =>
        MaxBidDetail(breakEvenAllIn, shippingCost, targetRoiPercent, buyerFeePercent).MaxBid;

    /// <summary>
    /// The same ceiling, plus which bar produced it — one implementation, both answers, because two
    /// callers deriving "was it the cash or the percentage that stopped me?" from the same two
    /// expressions is how they end up disagreeing.
    /// </summary>
    /// <param name="targetRoiPercent">
    /// The return the bidder wants on the money. Null is the app's own
    /// <see cref="LocalArbitrageAnalyzer.SolidRoiPercent"/> bar, which is what every eBay auction
    /// row uses. The cash bar applies whatever the target is: a percentage has no size, and 300% of
    /// four dollars still doesn't pay for finding, listing and packing it.
    /// </param>
    /// <param name="buyerFeePercent">
    /// A buyer's premium charged on top of the winning bid, as a percentage of it. Zero on eBay,
    /// which charges the buyer nothing; live-selling platforms charge it, and it has to be divided
    /// back out of the ceiling or the quoted bid costs more than the arithmetic behind it allowed.
    /// </param>
    /// <param name="salesTaxPercent">
    /// Sales tax the marketplace collects on the hammer and the premium, as a percentage. Zero on
    /// eBay auctions, where this analyzer's own rows are priced; a live marketplace is a facilitator
    /// and charges it at checkout, so it multiplies the bid exactly as the premium does and divides
    /// back out of the ceiling with it. Same <c>k = (1+premium)(1+tax)</c> as
    /// <see cref="LotAnalyzer.MaxAsk"/>, which is the app's other auction ceiling.
    /// </param>
    public static (decimal MaxBid, string BoundBy) MaxBidDetail(
        decimal breakEvenAllIn, decimal shippingCost,
        decimal? targetRoiPercent = null, decimal buyerFeePercent = 0m, decimal salesTaxPercent = 0m)
    {
        if (breakEvenAllIn <= 0) return (0m, "");

        var target = Math.Max(0m, targetRoiPercent ?? LocalArbitrageAnalyzer.SolidRoiPercent);
        var byRoi = breakEvenAllIn / (1m + target / 100m);
        var byProfit = breakEvenAllIn - LocalArbitrageAnalyzer.SolidProfit;
        var boundBy = byRoi <= byProfit ? CeilingByRoi : CeilingByCash;

        // Everything above is a ceiling on the ALL-IN cost. Shipping is spent whatever the bid is,
        // so it comes off the top; the premium and the tax are both multiples of the bid, so they
        // divide out together.
        var perBidDollar = (1m + Math.Max(0m, buyerFeePercent) / 100m) * (1m + Math.Max(0m, salesTaxPercent) / 100m);
        var worthIt = (Math.Min(byRoi, byProfit) - shippingCost) / perBidDollar;
        return worthIt <= 0 ? (0m, boundBy) : (Math.Floor(worthIt * 100m) / 100m, boundBy);
    }

    /// <summary>Minutes until the close. Null when there is no end date to count to.</summary>
    public static int? MinutesToEnd(DateTime? endUtc, DateTime nowUtc) =>
        endUtc is DateTime end ? (int)Math.Round((end - nowUtc).TotalMinutes) : null;

    /// <summary>
    /// How much of the clock is left, as a word. <c>none</c> is a listing with no end date worth
    /// counting to — a Buy It Now, whose price does not move and never needed one.
    /// </summary>
    public static string TimeTierFor(int? minutesToEnd) => minutesToEnd switch
    {
        null => "none",
        <= 0 => "ended",
        <= ClosingMinutes => "closing",
        <= PriceIsRealHours * 60 => "today",
        _ => "open",
    };

    /// <summary>
    /// Whether the price on screen is the price that would be paid. Always true for a fixed-price
    /// listing; true for an auction only inside the window where the bidding is nearly over.
    /// </summary>
    public static bool PriceIsRealFor(string buyingOption, int? minutesToEnd)
    {
        if (!IsAuction(buyingOption)) return true;
        return minutesToEnd is int mins && mins > 0 && mins <= PriceIsRealHours * 60;
    }

    public static bool IsAuction(string? buyingOption) =>
        (buyingOption ?? "").Contains("AUCTION", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a listing found by a keyword search is credibly the product being priced.
    /// Delegates to <see cref="JackpotHunter.IsPlausibleSupply"/> — one identity guard in the app,
    /// not a second one wearing an auction badge — with one deliberate difference.
    /// </summary>
    /// <remarks>
    /// The price floor ("nothing this cheap is the real thing") is applied to fixed-price listings
    /// only. On an auction it is exactly backwards: auctions legitimately open at $0.99 and a
    /// genuinely cheap close is the entire point of the feature, so rejecting on price here would
    /// throw away every row worth finding. Auctions are guarded on identity alone, and a suspiciously
    /// cheap one is flagged for the seller to check rather than silently dropped — see
    /// <see cref="Build"/>'s risks.
    /// </remarks>
    public static (bool Plausible, string? Reason) IsPlausibleSnipe(
        EbayOpportunityItem item, NormalizedProduct identity, string? productTitle, decimal priceFloor)
    {
        var floor = IsAuction(item.BuyingOption) ? 0m : priceFloor;
        return JackpotHunter.IsPlausibleSupply(
            item.Title, item.Price + item.ShippingCost, identity, productTitle, floor);
    }

    /// <summary>One live listing, fully costed and judged.</summary>
    public SnipeCandidate Build(
        EbayOpportunityItem item, ResalePricing? resale, FeeProfile fees, DateTime nowUtc,
        SnipeWatchTerm? term = null)
    {
        var shipping = Math.Max(0m, item.ShippingCost);
        var minutes = MinutesToEnd(item.EndDate, nowUtc);

        var row = new SnipeCandidate
        {
            ItemId = string.IsNullOrWhiteSpace(item.ItemId) ? item.Url : item.ItemId,
            Title = item.Title,
            Url = item.Url,
            ImageUrl = item.ImageUrl,
            Condition = item.Condition,
            BuyingOption = IsAuction(item.BuyingOption) ? "AUCTION" : "FIXED_PRICE",
            BidCount = item.BidCount,
            SellerUsername = item.SellerUsername,
            SellerFeedbackScore = item.SellerFeedbackScore,
            SellerFeedbackPercent = item.SellerFeedbackPercent,
            FoundBy = term?.Term ?? "",
            CurrentPrice = item.Price,
            ShippingCost = shipping,
            ShippingStated = item.ShippingStated,
            AllInCost = Math.Round(item.Price + shipping, 2),
            EndUtc = item.EndDate,
            MinutesToEnd = minutes,
            TimeTier = TimeTierFor(minutes),
        };
        row.PriceIsReal = PriceIsRealFor(row.BuyingOption, minutes);

        if (resale is null || !resale.HasPrice)
        {
            row.Verdict = VerdictNoData;
            row.VerdictNote = "No eBay sold history matched this title, so there's no price to bid against.";
            row.PricedAs = resale?.LookupTitle ?? "";
            ApplySpeed(row, resale);
            return row;
        }

        row.PricedAs = resale.LookupTitle;
        row.MarketMedian = resale.Median;
        row.ExpectedSale = resale.ExpectedSale;
        row.QuickSale = resale.QuickSale;
        row.SoldCompCount = resale.SoldCompCount;
        row.TerapeakCompCount = resale.TerapeakCompCount;
        row.ResaleSource = LocalArbitrageAnalyzer.SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount);
        row.ConfidenceScore = resale.ConfidenceScore;
        row.ConfidenceLevel = resale.ConfidenceLevel;
        row.DisagreementMessage = resale.DisagreementMessage;
        row.LiquidityScore = resale.LiquidityScore;
        row.LiquidityLevel = resale.LiquidityLevel;

        var median = resale.Median is > 0 ? resale.Median!.Value : resale.ExpectedSale ?? 0m;
        if (median > 0 && row.AllInCost > 0)
            row.DiscountPercent = Math.Round((median - row.AllInCost) / median * 100m, 1);

        // The money. Every figure below is one subtraction away from this: net profit falls exactly
        // one dollar for every dollar paid, which is what makes the ceiling exact arithmetic rather
        // than a rule of thumb.
        var breakEvenAllIn = hunter.BreakEvenBuyPrice(resale, fees);
        row.BreakEvenBid = Math.Round(Math.Max(0m, breakEvenAllIn - shipping), 2);
        row.MaxBid = MaxBidFor(breakEvenAllIn, shipping);
        row.BidHeadroom = Math.Round(row.MaxBid.Value - item.Price, 2);
        row.ProfitAtMaxBid = Math.Round(Math.Max(0m, breakEvenAllIn - (row.MaxBid.Value + shipping)), 2);

        var expected = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median!.Value;
        var profit = profitCalc.Calculate(
            supplierUnitCost: row.AllInCost, quantity: 1, expectedSalePrice: expected,
            quickSalePrice: resale.QuickSale ?? expected,
            buyerPaidShipping: resale.AvgCompShipping, fees: fees,
            actualShippingCostOverride: resale.AvgCompShipping > 0 ? resale.AvgCompShipping : null);

        row.ProfitAtCurrentPrice = profit.NetProfitPerUnit;
        row.RoiAtCurrentPrice = profit.RoiPercent;
        row.MarginPercent = profit.MarginPercent;
        row.EstimatedFees = profit.MarketplaceFeeTotal;
        row.EstimatedShipCost = profit.FulfilmentCostTotal;

        ApplySpeed(row, resale);

        var (verdict, note) = Judge(row, resale.SoldCompCount + resale.TerapeakCompCount, resale.ConfidenceScore);
        row.Verdict = verdict;
        row.VerdictNote = note;

        // Only worth naming a moment for a row somebody would actually bid on.
        if (item.EndDate is DateTime end && IsAuction(row.BuyingOption)
            && verdict is VerdictSnipe or VerdictTooEarly && minutes is > 0)
            row.SnipeAtUtc = end.AddSeconds(-SnipeSecondsBeforeEnd);

        row.Risks = Risks(row, resale);
        return row;
    }

    private static void ApplySpeed(SnipeCandidate row, ResalePricing? resale)
    {
        var estimate = DaysToCashEstimator.Estimate(
            resale?.EstimatedDaysToSell, resale?.EstimatedMonthlySales ?? 0m,
            row.ProfitAtMaxBid ?? row.ProfitAtCurrentPrice,
            row.MaxBid is > 0 && row.ProfitAtMaxBid is > 0
                ? Math.Round(row.ProfitAtMaxBid.Value / (row.MaxBid.Value + row.ShippingCost) * 100m, 1)
                : row.RoiAtCurrentPrice);

        row.DaysToSell = estimate.DaysToSell;
        row.DaysToCash = estimate.DaysToCash;
        row.ProfitPerDay = estimate.ProfitPerDay;
        row.AnnualizedRoiPercent = estimate.AnnualizedRoiPercent;
        row.SpeedTier = estimate.SpeedTier;
        row.SpeedLabel = estimate.SpeedLabel;
        row.SpeedNote = estimate.Note;
    }

    /// <summary>
    /// The call on one row. Ordered so that the reasons a number cannot be trusted are reached
    /// before the number itself: an item that is already too expensive, then thin evidence, then
    /// the clock, and only then the arithmetic.
    /// </summary>
    public static (string Verdict, string Note) Judge(SnipeCandidate row, int compCount, int confidenceScore)
    {
        if (row.TimeTier == "ended")
            return (VerdictEnded, "This one has already closed.");

        if (row.BreakEvenBid is not > 0)
            return (VerdictPass, "Fees and shipping eat the whole resale price — no bid makes this work.");

        var profit = row.ProfitAtCurrentPrice ?? 0m;
        if (profit <= 0 || row.MaxBid is not > 0)
            return (VerdictPass, row.AllInCost > 0
                ? $"At {row.AllInCost:C} all-in it's already worth less than it costs once fees are paid."
                : "Nothing here clears its own fees.");

        if (compCount < MinCompsToBid)
            return (VerdictThin, $"Profitable on {compCount} sold comp{(compCount == 1 ? "" : "s")} — too few to bid on.");

        // The rule the whole feature rests on: an auction days out is not priced, it is merely
        // cheap so far. The ceiling still holds (it comes from the resale side, not the bid), so
        // the row is kept and reported as a thing to come back to.
        if (!row.PriceIsReal)
            return (VerdictTooEarly, row.MinutesToEnd is int mins
                ? $"{FormatSpan(mins)} left — the price isn't real yet. Bid up to {row.MaxBid:C} when it's closing."
                : $"No end time yet — bid up to {row.MaxBid:C} when it's closing.");

        var believable = compCount >= MinCompsToSnipe && confidenceScore >= MinConfidenceToSnipe;
        var headroom = row.BidHeadroom ?? 0m;

        if (profit >= LocalArbitrageAnalyzer.SolidProfit
            && row.RoiAtCurrentPrice >= LocalArbitrageAnalyzer.SolidRoiPercent
            && believable)
            return (VerdictSnipe, row.BuyingOption == "AUCTION"
                ? $"{profit:C} net at the current bid, and {headroom:C} of room before you'd stop. " +
                  $"Backed by {compCount} sold comps."
                : $"{profit:C} net at {row.AllInCost:C} all-in, backed by {compCount} sold comps.");

        if (!believable)
            return (VerdictThin, $"{profit:C} net at today's price, but the sold data behind it is weak " +
                                 $"({compCount} comp{(compCount == 1 ? "" : "s")}, {row.ConfidenceLevel.ToLowerInvariant()}).");

        return (VerdictWatch, headroom < 0
            ? $"The bidding has already passed your {row.MaxBid:C} ceiling — {profit:C} is left, which isn't worth the work."
            : $"{profit:C} net — real, but under the {LocalArbitrageAnalyzer.SolidProfit:C0} that makes a flip worth doing.");
    }

    /// <summary>
    /// Why this might be cheap for a reason. Every one of these is a fact from the listing or the
    /// evidence behind it, stated plainly — none of them is folded into a score, because a score
    /// hides exactly the thing the bidder needs to go and look at.
    /// </summary>
    public static List<string> Risks(SnipeCandidate row, ResalePricing resale)
    {
        var risks = new List<string>();

        if (row.Verdict is VerdictPass or VerdictEnded or VerdictNoData) return risks;

        if (!row.ShippingStated)
            risks.Add("Shipping isn't stated on this listing. If it turns out to be $20, take $20 off the max bid.");

        if (row.BuyingOption == "AUCTION" && row.BidCount >= CrowdedBidCount)
            risks.Add($"{row.BidCount} bids already — you'd be bidding against people who have decided they want it.");

        if (row.BuyingOption == "AUCTION" && row.TimeTier == "today" && row.MinutesToEnd is int mins)
            risks.Add($"{FormatSpan(mins)} to go. Most bidding lands in the final minutes, so expect this to climb.");

        if (row.SellerFeedbackScore < LowFeedbackScore)
            risks.Add($"The seller has {row.SellerFeedbackScore} feedback. A bargain from a new account is the oldest trick on eBay.");
        else if (row.SellerFeedbackPercent is decimal pct && pct < LowFeedbackPercent)
            risks.Add($"The seller's feedback is {pct:0.#}% — below what you'd normally buy from.");

        // Under a quarter of what these actually sell for. On a fixed-price listing this is a hard
        // reject upstream; on an auction it is the row worth looking hardest at, in both directions.
        var floor = JackpotHunter.SupplyPriceFloor(resale);
        if (floor > 0 && row.AllInCost > 0 && row.AllInCost < floor && row.PriceIsReal)
            risks.Add($"At {row.AllInCost:C} this is under a quarter of what these sell for. Read the description and " +
                      "look at the photos — something is usually wrong with one this cheap.");

        var comps = row.SoldCompCount + row.TerapeakCompCount;
        if (comps < MinCompsToSnipe || row.ConfidenceScore < MinConfidenceToSnipe)
            risks.Add($"Priced on {comps} sold comp{(comps == 1 ? "" : "s")} ({row.ConfidenceLevel.ToLowerInvariant()}) — " +
                      "treat the ceiling as indicative rather than exact.");

        if (!string.IsNullOrWhiteSpace(row.DisagreementMessage))
            risks.Add(row.DisagreementMessage!);

        return risks;
    }

    // "3h 20m", "2 days", "45m" — the countdown as a sentence, for the notes. The live ticking
    // version is the browser's job; this is what gets frozen into a verdict string.
    public static string FormatSpan(int minutes)
    {
        if (minutes <= 0) return "No time";
        if (minutes < 60) return $"{minutes}m";
        if (minutes < 60 * 24) return minutes % 60 == 0 ? $"{minutes / 60}h" : $"{minutes / 60}h {minutes % 60}m";
        var days = minutes / (60 * 24);
        var hours = minutes % (60 * 24) / 60;
        return hours == 0 ? $"{days} day{(days == 1 ? "" : "s")}" : $"{days}d {hours}h";
    }

    // ── Ranking ──────────────────────────────────────────────────────────────────────────────

    public const string SortByUrgency = "urgency";
    public const string SortByProfit = "profit";
    public const string SortByDiscount = "discount";

    public static string NormalizeSort(string? sort) => (sort ?? "").Trim().ToLowerInvariant() switch
    {
        SortByProfit or "money" or "net" => SortByProfit,
        SortByDiscount or "under" or "gap" => SortByDiscount,
        _ => SortByUrgency,
    };

    /// <summary>
    /// Rank order for the verdicts. <c>too_early</c> sits above <c>watch</c> on purpose: an auction
    /// with a real ceiling and days to run is an opportunity that hasn't happened yet, while a watch
    /// row is one the bidding has already spoiled.
    /// </summary>
    public static int VerdictRank(string verdict) => verdict switch
    {
        VerdictSnipe => 0, VerdictTooEarly => 1, VerdictWatch => 2, VerdictThin => 3,
        VerdictPass => 4, VerdictEnded => 5, _ => 6,
    };

    /// <summary>
    /// Best money first is the wrong default here, and this is the one board where that's true:
    /// the biggest margin on the screen is worth nothing if it closes in three minutes and the
    /// seller is reading about a different row. Urgency ranks what is actually still winnable.
    /// </summary>
    public static List<SnipeCandidate> Rank(IEnumerable<SnipeCandidate> rows, string? sort = null)
    {
        var ordered = rows.OrderBy(r => VerdictRank(r.Verdict));

        return NormalizeSort(sort) switch
        {
            SortByProfit => ordered
                .ThenByDescending(r => r.ProfitAtMaxBid ?? 0m)
                .ThenByDescending(r => r.ProfitAtCurrentPrice ?? 0m)
                .ThenBy(r => SortableEnd(r))
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            SortByDiscount => ordered
                .ThenByDescending(r => r.DiscountPercent ?? decimal.MinValue)
                .ThenByDescending(r => r.ProfitAtMaxBid ?? 0m)
                .ThenBy(r => SortableEnd(r))
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),

            _ => ordered
                .ThenBy(r => SortableEnd(r))
                .ThenByDescending(r => r.ProfitAtMaxBid ?? 0m)
                .ThenByDescending(r => r.ProfitAtCurrentPrice ?? 0m)
                .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    // A listing with no end date (a Buy It Now) is not urgent, so it sorts after everything on a
    // clock rather than ahead of it — never as "ending now".
    private static long SortableEnd(SnipeCandidate row) =>
        row.EndUtc is DateTime end ? end.Ticks : long.MaxValue;

    /// <summary>
    /// The board's totals. Only <c>snipe</c> rows count: a row whose price isn't real yet has no
    /// money on it to add up, and adding it anyway is how a scan starts advertising profit on
    /// auctions that will close at three times the price it quoted.
    /// </summary>
    public static SnipeSummary Summarize(IReadOnlyList<SnipeCandidate> rows, DateTime nowUtc)
    {
        var snipes = rows.Where(r => r.Verdict == VerdictSnipe).ToList();
        var ends = snipes.Where(r => r.EndUtc is DateTime e && e > nowUtc)
            .Select(r => r.EndUtc!.Value).ToList();

        return new SnipeSummary
        {
            SnipeCount = snipes.Count,
            TooEarlyCount = rows.Count(r => r.Verdict == VerdictTooEarly),
            ClosingWithinTheHour = snipes.Count(r => r.TimeTier == "closing"),
            ProfitAtCeilings = Math.Round(snipes.Sum(r => r.ProfitAtMaxBid ?? 0m), 2),
            BestProfit = snipes.Count > 0 ? snipes.Max(r => r.ProfitAtMaxBid ?? 0m) : 0m,
            CapitalToWinAll = Math.Round(snipes.Sum(r => (r.MaxBid ?? 0m) + r.ShippingCost), 2),
            NextEndUtc = ends.Count > 0 ? ends.Min() : null,
            ScannedUtc = nowUtc,
        };
    }

    // ── The watch list ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What to hunt for when the seller hasn't said. Their own completed sales, because a product
    /// they have already sold is the one product on eBay whose demand, condition, shipping cost and
    /// buyer they have first-hand knowledge of — and the one whose "I could have bought that for
    /// half of what I got" is money they have demonstrably left on the table before.
    /// </summary>
    /// <remarks>
    /// Grouped by <see cref="JackpotHunter.ProductSignature"/>, so twelve sales of one item are one
    /// watch term, not twelve. Ranked by how many times it has sold, because repeat sales are the
    /// closest thing the app has to proof that the seller can move another one.
    /// </remarks>
    public static List<SnipeWatchTerm> WatchTermsFromSales(IEnumerable<FlipRecord> flips, int max)
    {
        if (max <= 0) return [];

        var groups = new Dictionary<string, (List<string> Titles, int Sales, DateTimeOffset Newest)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var flip in flips)
        {
            // Only sales that actually happened. A cancelled order is not evidence of anything, and
            // a refunded one is evidence against.
            if (!string.Equals(flip.Status, "paid", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrWhiteSpace(flip.Title)) continue;

            var (key, _) = JackpotHunter.ProductSignature(flip.Title);
            if (string.IsNullOrWhiteSpace(key)) key = flip.Title.Trim().ToLowerInvariant();

            if (groups.TryGetValue(key, out var group))
            {
                group.Titles.Add(flip.Title);
                groups[key] = (group.Titles, group.Sales + 1,
                    flip.SoldUtc > group.Newest ? flip.SoldUtc : group.Newest);
            }
            else
            {
                groups[key] = ([flip.Title], 1, flip.SoldUtc);
            }
        }

        var terms = new List<SnipeWatchTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups.Values.OrderByDescending(g => g.Sales).ThenByDescending(g => g.Newest))
        {
            var lookupTitle = LeanestTitle(group.Titles);
            var query = JackpotHunter.ShoppingQuery(lookupTitle);
            if (string.IsNullOrWhiteSpace(query) || !seen.Add(query)) continue;

            terms.Add(new SnipeWatchTerm
            {
                Term = query,
                LookupTitle = lookupTitle,
                Source = "sold",
                SalesBehindIt = group.Sales,
                Reason = group.Sales == 1
                    ? "You've sold one of these"
                    : $"You've sold {group.Sales} of these",
            });

            if (terms.Count >= max) break;
        }

        return terms;
    }

    /// <summary>Terms typed into the box — commas or newlines, blanks dropped, duplicates collapsed.</summary>
    public static List<SnipeWatchTerm> ParseTypedTerms(string? raw, int max)
    {
        if (string.IsNullOrWhiteSpace(raw) || max <= 0) return [];

        var terms = new List<SnipeWatchTerm>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var part in raw.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var term = part.Trim();
            if (term.Length < 2 || !seen.Add(term)) continue;

            terms.Add(new SnipeWatchTerm
            {
                Term = term, LookupTitle = term, Source = "typed", Reason = "You asked for this one",
            });
            if (terms.Count >= max) break;
        }

        return terms;
    }

    // The shortest title that still says enough to price against — sold titles for one product run
    // from "Dyson V11" to "🔥DYSON V11 TORQUE DRIVE FREE SHIP🔥", and the lean one makes the better
    // search term. Same rule JackpotHunter uses when it picks a title out of a comp cluster.
    // Public because the Restock List clusters the same sold titles for the same reason, and two
    // screens picking a different title out of one cluster would search for two different things.
    public static string LeanestTitle(List<string> titles)
    {
        var usable = titles
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Where(t => MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(t)).Count >= 3)
            .OrderBy(t => t.Length)
            .ToList();

        return usable.Count > 0 ? usable[0] : titles.OrderByDescending(t => t.Length).First().Trim();
    }
}
