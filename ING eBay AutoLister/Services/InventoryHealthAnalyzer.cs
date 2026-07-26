using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Judges the seller's own live eBay listings against what the market is paying today, and works
/// out what each one should be priced at to actually sell — without ever recommending a price that
/// loses money.
/// </summary>
/// <remarks>
/// <para>
/// Every other pricing surface in this app looks forward at something the seller has not bought
/// yet. This one looks backward at inventory they already own and are already paying to hold, which
/// is where a working reseller's capital actually goes to die: a listing sits at a price that was
/// right on the day it went up, the market drifts down under it, and nothing in eBay Seller Hub
/// ever says so. Seller Hub's own Markdown Manager takes a blanket percentage off with no market
/// data and no floor; this takes the market price the rest of the app already computes and the
/// seller's own cost basis, and lands on a number that both clears inventory and clears cost.
/// </para>
/// <para>
/// Everything here is pure except <see cref="Build"/>, which delegates the money to the shared
/// <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/>. A held item is costed by exactly the
/// same rules as a local flip or a dropship — a break-even is a break-even.
/// </para>
/// </remarks>
public sealed class InventoryHealthAnalyzer(ProfitCalculator profitCalc)
{
    // ── Age bands ────────────────────────────────────────────────────────────────────────────
    // A markdown ladder, not a cliff. Under a month a listing has not had a fair run at its price
    // and cutting it just donates margin; past that, each band asks for a little more of a
    // discount to today's market, because the evidence that the price is the blocker keeps growing.
    public const int FreshDays = 30;
    public const int StaleDays = 90;
    public const int DeadDays = 180;

    // ── Gap thresholds, as a percent of the market price ─────────────────────────────────────
    private const decimal OverpricedGapPercent = 15m;
    private const decimal UnderpricedGapPercent = -10m;
    // Above this a listing is mispriced rather than new, and the fresh-listing grace period stops
    // applying — 30% over market on day 9 is not a listing that needs more time.
    private const decimal GrosslyOverpricedGapPercent = 25m;

    // ── Guard rails on the recommendation itself ─────────────────────────────────────────────
    // No single revision may cut more than this off the current price. A tool that answers a
    // 90-day-old listing with "minus 60%" is a panic button, not a repricer; deep cuts should be
    // reached over successive scans, with the seller seeing each step.
    private const decimal MaxSingleCutPercent = 35m;
    // And no single revision may raise by more than this, for the same reason in reverse.
    private const decimal MaxSingleRaisePercent = 25m;
    // Below this, a revision is not worth making: it will not change a buyer's mind and it churns
    // the listing for nothing.
    private const decimal MinChangePercent = 2m;
    private const decimal MinChangeDollars = 1m;

    // ── Demand evidence ──────────────────────────────────────────────────────────────────────
    // Watchers are the only free demand signal eBay gives on a listing that has not sold. Enough
    // of them at a fair price means the item is close, and a markdown there gives away margin the
    // seller did not have to spend.
    private const int WatchersMeanClose = 3;
    private const int WatchersMeanStrong = 5;

    // No price change is recommended off fewer sold comps than this, in either direction — the
    // same evidence bar the local-arbitrage verdicts use.
    private const int MinCompsForChange = 3;
    // A raise makes a stronger claim than a markdown does, so it also has to clear a confidence bar.
    private const int MinConfidenceForRaise = 40;

    // Past this, the "market price" is a comp-matching failure, not a mispricing. Nobody lists at
    // four times the going rate by accident, and a real-inventory scan turned up exactly this:
    // lot listings and unusual models matching a stray cheap accessory. Acting on that number
    // would recommend a confident markdown off a comparison that never held.
    private const decimal ImplausibleGapPercent = 300m;

    /// <summary>
    /// Builds one judged row from a live listing, whatever priced it, and whatever the seller paid.
    /// </summary>
    public InventoryHealthItem Build(
        EbayListingSummary listing, ResalePricing? resale, CostBasisEntry? cost,
        FeeProfile fees, DateTime nowUtc, int lotQuantity = 1)
    {
        var quantity = Math.Max(1, listing.Quantity);
        var item = new InventoryHealthItem
        {
            ListingId = listing.ListingId,
            Sku = listing.Sku,
            Title = listing.Title,
            Url = listing.ListingUrl,
            ImageUrl = listing.ThumbnailUrl,
            Condition = listing.Condition,
            Category = listing.Category,
            ListPrice = listing.Price,
            Quantity = quantity,
            WatchCount = listing.WatchCount,
            QuantitySold = listing.QuantitySold,
            StartTimeUtc = listing.StartTimeUtc,
            DaysListed = DaysListed(listing.StartTimeUtc, nowUtc),
            CostBasis = cost?.TotalUnitCost,
            CostBasisNote = cost?.Note ?? "",
            LotQuantity = Math.Max(1, lotQuantity),
        };

        if (item.DaysListed is int lifeDays && lifeDays > 0 && listing.QuantitySold > 0)
            item.SalesPerMonth = Math.Round(listing.QuantitySold * 30m / lifeDays, 2);

        if (item.DaysListed is null)
            item.Signals.Add("eBay did not report a start date for this listing, so its age is unknown.");

        // Capital is measured at the truest basis available. What was paid is the money actually
        // sunk; the market value is only a proxy for it, and the asking price is the weakest proxy
        // of the three because it is the number being questioned.
        (item.CapitalTiedUp, item.CapitalBasis) = (cost?.TotalUnitCost, resale?.ExpectedSale ?? resale?.Median) switch
        {
            (decimal paid, _) => (Math.Round(paid * quantity, 2), "cost_basis"),
            (null, decimal marketValue) => (Math.Round(marketValue * quantity, 2), "market_value"),
            _ => (Math.Round(listing.Price * quantity, 2), "list_price"),
        };

        if (resale is null || !resale.HasPrice)
        {
            item.Verdict = "no_data";
            item.VerdictNote = "No eBay sold history matched this title, so there is nothing to price it against.";
            item.PricedAs = resale?.LookupTitle ?? "";
            if (item.DaysListed >= StaleDays)
                item.Signals.Add($"Listed {item.DaysListed} days with no sold comps to check it against.");
            return item;
        }

        item.PricedAs = resale.LookupTitle;
        item.MarketPrice = resale.ExpectedSale ?? resale.Median;
        item.MarketMedian = resale.Median;
        item.QuickSalePrice = resale.QuickSale;
        item.SoldCompCount = resale.SoldCompCount;
        item.TerapeakCompCount = resale.TerapeakCompCount;
        item.ResaleSource = LocalArbitrageAnalyzer.SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount);
        item.ConfidenceScore = resale.ConfidenceScore;
        item.ConfidenceLevel = resale.ConfidenceLevel;
        item.LiquidityScore = resale.LiquidityScore;
        item.LiquidityLevel = resale.LiquidityLevel;

        var market = item.MarketPrice!.Value;
        item.PriceGapPercent = market > 0 ? Math.Round((listing.Price - market) / market * 100m, 1) : null;

        // Is the market figure actually comparable with the asking price? Two ways it is not, both
        // found by running this over a real inventory rather than reasoned about in advance.
        if (item.LotQuantity > 1)
        {
            item.MarketComparable = false;
            item.Signals.Add($"This listing is a lot of {item.LotQuantity}; sold comps are priced per unit, so the market figure is not a like-for-like comparison.");
        }
        else if (Math.Abs(item.PriceGapPercent ?? 0m) > ImplausibleGapPercent)
        {
            item.MarketComparable = false;
            item.Signals.Add($"The comps came back {Math.Abs(item.PriceGapPercent!.Value):0}% away from the asking price — that is a matching failure, not a mispricing. Check what it was priced as.");
        }

        // Shipping is booked on both sides — buyers pay it (revenue, and eBay charges its final
        // value fee on it) and it costs the seller the same to ship. Identical treatment to
        // LocalArbitrageAnalyzer.Build, so the same item costs the same whichever screen asks.
        var shipping = resale.AvgCompShipping;
        ProfitBreakdown? At(decimal salePrice) => cost is null ? null : profitCalc.Calculate(
            supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: salePrice,
            quickSalePrice: salePrice, buyerPaidShipping: shipping, fees: fees,
            actualShippingCostOverride: shipping > 0 ? shipping : null);

        if (At(listing.Price) is ProfitBreakdown atList)
        {
            item.NetProfitAtListPrice = atList.NetProfitPerUnit;
            // The lowest price at which this item still breaks even after every fee. This is the
            // hard floor the markdown ladder is not allowed through.
            item.BreakEvenPrice = atList.BreakEvenSalePrice == decimal.MaxValue ? null : atList.BreakEvenSalePrice;
            // The break-even says the sale stops losing money; the seller's floor policy says it
            // starts being worth making. The markdown ladder is bounded by the second, so a
            // repricing run cannot walk an item down to a technically-profitable $0.40.
            var (floor, basis) = NetProceedsCalculator.MinimumOffer(item.BreakEvenPrice, fees, shipping);
            item.MinimumOfferPrice = floor;
            item.MinimumOfferBasis = basis;
            item.NetProfitAtMinimumOffer = NetProceedsCalculator.NetProfitAt(floor ?? 0m, item.BreakEvenPrice, fees);
        }

        var suggestion = SuggestPrice(
            listPrice: listing.Price, market: market, quickSale: resale.QuickSale,
            floorPrice: item.MinimumOfferPrice ?? item.BreakEvenPrice, daysListed: item.DaysListed, watchCount: listing.WatchCount,
            compCount: resale.SoldCompCount + resale.TerapeakCompCount, confidenceScore: resale.ConfidenceScore,
            marketComparable: item.MarketComparable, quantitySold: listing.QuantitySold);

        item.SuggestedPrice = suggestion.Price;
        item.FloorLimited = suggestion.FloorLimited;
        item.RequiresReview = suggestion.RequiresReview;
        if (!string.IsNullOrEmpty(suggestion.Signal)) item.Signals.Add(suggestion.Signal);

        if (suggestion.Price is decimal newPrice)
        {
            item.SuggestedChangePercent = listing.Price > 0
                ? Math.Round((newPrice - listing.Price) / listing.Price * 100m, 1) : null;
            item.NetProfitAtSuggested = At(newPrice)?.NetProfitPerUnit;
            // Only said on rows that are actually recommending a price. On a row nothing is being
            // done to, the empty cost cell already says it, and repeating it on every listing in
            // the table buries the signals that do carry information.
            if (cost is null)
                item.Signals.Add("No cost basis recorded, so this price has not been checked against your break-even.");
        }

        var (verdict, note) = Judge(item, market);
        item.Verdict = verdict;
        item.VerdictNote = note;
        return item;
    }

    /// <summary>Whole days a listing has been live, or null when eBay reported no start date.</summary>
    public static int? DaysListed(DateTime? startUtc, DateTime nowUtc)
    {
        if (startUtc is not DateTime start) return null;
        var days = (nowUtc - DateTime.SpecifyKind(start, DateTimeKind.Utc)).TotalDays;
        // A start date in the future is clock skew, not a negative age.
        return (int)Math.Max(0, Math.Floor(days));
    }

    /// <summary>
    /// The recommended price for one listing, or null when the honest answer is "leave it alone".
    /// </summary>
    /// <remarks>
    /// The order matters. The ladder proposes a target from the market price and the listing's age;
    /// demand evidence then pulls that target back up; the break-even floor and the single-step cap
    /// bound it; and finally a change too small to matter is discarded rather than shipped.
    /// </remarks>
    public static (decimal? Price, bool FloorLimited, bool RequiresReview, string Signal) SuggestPrice(
        decimal listPrice, decimal market, decimal? quickSale, decimal? floorPrice,
        int? daysListed, int watchCount, int compCount, int confidenceScore,
        bool marketComparable = true, int quantitySold = 0)
    {
        if (market <= 0 || listPrice <= 0) return (null, false, false, "");

        // A comparison that failed produces no recommendation at all, and no reasoning about the
        // market either. Everything below this line treats the market price as authoritative, and
        // it is only authoritative if it matched.
        if (!marketComparable) return (null, false, false, "");

        // A listing that has actually sold units at this price has settled the question the comps
        // were only estimating. Age stops meaning "stuck" on a multi-quantity listing — it means
        // how long the listing has been up, not how long the stock has sat — so the markdown
        // ladder is switched off entirely. Found on a real listing: 44 units sold, 64 watchers,
        // 38% "above market", and the ladder wanted to cut $105 off each of the 80 remaining.
        if (quantitySold > 0 && listPrice >= market)
            return (null, false, false,
                $"{quantitySold} units have already sold at this price — the market figure is behind what this listing actually gets.");

        // Below this the sold history cannot carry a price change in either direction.
        if (compCount < MinCompsForChange)
            return (null, false, false,
                $"Only {compCount} sold comp{(compCount == 1 ? "" : "s")} — too little sold data to recommend a price change.");

        var gapPercent = (listPrice - market) / market * 100m;

        // The break-even sits above what the market pays: no price both sells and profits. Saying
        // so is the whole answer — a "suggested price" here would just be a suggested loss.
        if (floorPrice is decimal floor && floor > market)
            return (null, true, false,
                $"Break-even is ${floor:0.00} but the market pays about ${market:0.00} — there is no profitable price for this one.");

        // Enough watchers at a roughly fair price means the item is close. Cutting here spends
        // margin to buy something the listing was already going to get.
        if (watchCount >= WatchersMeanClose && Math.Abs(gapPercent) <= 10m)
            return (null, false, false,
                $"{watchCount} watchers at a market-fair price — this one is close; hold the price.");

        var target = LadderTarget(market, quickSale, daysListed, gapPercent);
        if (target is null) return (null, false, false, "");

        var signal = "";
        var floorLimited = false;

        // Strong watcher interest caps how far down the ladder is allowed to go: an audience that
        // size says the price is the only remaining blocker, and meeting the market clears that
        // without going under it.
        if (watchCount >= WatchersMeanStrong && target < market)
        {
            target = market;
            signal = $"{watchCount} watchers — held at the market price rather than discounted below it.";
        }

        // A raise is a different kind of claim from a markdown and is treated as one.
        var isRaise = target > listPrice;
        var requiresReview = false;
        if (isRaise)
        {
            // Below market and still unsold after three months is evidence that the comp match is
            // wrong for this exact item — a different condition, missing parts, weaker photos —
            // not evidence that the price is too low. Raising it would be acting on the one
            // reading the listing's own history contradicts.
            if (daysListed >= StaleDays)
                return (null, false, false,
                    $"Priced {Math.Abs(gapPercent):0}% under market and still unsold after {daysListed} days — check the comp match before raising it.");

            if (confidenceScore < MinConfidenceForRaise)
                return (null, false, false,
                    "Looks under market, but on too little sold data to recommend raising the price.");

            target = Math.Min(target.Value, listPrice * (1m + MaxSingleRaisePercent / 100m));
            requiresReview = true;
            signal = "A price rise — review this one individually; it is never included in a bulk apply.";
        }
        else
        {
            var deepestAllowed = listPrice * (1m - MaxSingleCutPercent / 100m);
            if (target < deepestAllowed)
            {
                target = deepestAllowed;
                signal = $"Capped at a {MaxSingleCutPercent:0}% cut for one revision — re-run the scan after this one to go further.";
            }

            if (floorPrice is decimal f && target < f)
            {
                target = f;
                floorLimited = true;
                signal = $"Held at the ${f:0.00} break-even — this is as low as this item goes without selling at a loss.";
            }
        }

        var final = Charm(target.Value, floorPrice);

        var change = Math.Abs(final - listPrice);
        if (change < MinChangeDollars || change / listPrice * 100m < MinChangePercent)
            return (null, floorLimited, false, "");

        return (final, floorLimited, requiresReview, signal);
    }

    // The markdown ladder itself. Each band is a fraction of today's market price, so the
    // recommendation tracks the market rather than compounding blind percentages off the seller's
    // own asking price.
    private static decimal? LadderTarget(decimal market, decimal? quickSale, int? daysListed, decimal gapPercent)
    {
        // A brand new listing gets to keep its price — the grace period exists so a listing that
        // has not had its run is never cut prematurely. Two things still get through it:
        //   * a price so far over market that age is plainly not the problem, and
        //   * a price meaningfully UNDER market, which the grace period has no bearing on at all.
        //     A fresh listing priced too low is the one case where waiting costs money rather than
        //     saving it: once it sells, the difference is gone and cannot be recovered.
        if (daysListed is int d && d < FreshDays)
            return gapPercent >= GrosslyOverpricedGapPercent || gapPercent <= UnderpricedGapPercent
                ? market : null;

        // Age unknown: the price gap can still be corrected, but there is no basis for an
        // aging discount on top of it, so this stops at the market price.
        if (daysListed is null) return market;

        return daysListed switch
        {
            < 60 => market,
            < 90 => market * 0.97m,
            < 120 => market * 0.94m,
            < DeadDays => market * 0.90m,
            // Past six months the goal changes from "sell it well" to "get the money back out".
            // The estimator's own quick-sale price is used when it is the more aggressive of the
            // two, since it is derived from the comps rather than from a flat percentage.
            _ => Math.Min(market * 0.85m, quickSale ?? decimal.MaxValue),
        };
    }

    // Prices ending in .99 outsell round numbers, and eBay's own sorted-by-price results reward
    // being a cent under a threshold. Always rounds DOWN to the charm point, which errs slightly
    // cheap on a markdown and slightly conservative on a raise — the safe direction in both cases.
    // Never crosses the break-even floor to get there, and leaves an already-charmed price alone.
    public static decimal Charm(decimal price, decimal? floorPrice)
    {
        if (price <= 1m) return Math.Round(price, 2);

        var cents = price - Math.Floor(price);
        if (Math.Abs(cents - 0.99m) < 0.005m) return Math.Round(price, 2);

        var charmed = Math.Floor(price) - 0.01m;
        if (charmed <= 0m) return Math.Round(price, 2);
        if (floorPrice is decimal floor && charmed < floor) return Math.Round(price, 2);
        return charmed;
    }

    /// <summary>
    /// The single headline label for a listing, plus every other observation as a signal. Ordered
    /// by what costs the seller the most, not by what is easiest to detect.
    /// </summary>
    public static (string Verdict, string Note) Judge(InventoryHealthItem item, decimal market)
    {
        var days = item.DaysListed;
        var gap = item.PriceGapPercent ?? 0m;

        if (days >= StaleDays) item.Signals.Add($"Listed {days} days.");
        if (item.WatchCount > 0) item.Signals.Add($"{item.WatchCount} watcher{(item.WatchCount == 1 ? "" : "s")}.");
        if (item.QuantitySold > 0)
            item.Signals.Add(item.SalesPerMonth is decimal rate
                ? $"{item.QuantitySold} sold from this listing — about {rate:0.#}/month over its life (eBay reports no sale dates, so that is a lifetime average)."
                : $"{item.QuantitySold} sold from this listing.");
        if (item.SoldCompCount + item.TerapeakCompCount < 3)
            item.Signals.Add("Thin sold history — treat the market price as indicative.");

        // Units have actually moved, so this listing is not stuck however old it is, and that is
        // true whether or not the comps matched — it is a fact about the listing's own record.
        // Calling a listing that sold 44 units "138 days unsold" is not a wording slip: it is the
        // difference between leaving a proven seller alone and cutting the margin off every unit
        // still on the shelf.
        if (item.IsSelling)
            return ("selling", item.SalesPerMonth is decimal soldRate
                ? $"{item.QuantitySold} sold at this price, about {soldRate:0.#} a month — this one is working. Leave it alone."
                : $"{item.QuantitySold} sold at this price — this one is working. Leave it alone.");

        // Every verdict below this line is a claim about the price gap, and a gap computed from a
        // comparison that failed is not a fact about the listing. Age still is, so an old listing
        // is still reported as old — it just isn't accused of being overpriced on bad evidence.
        if (!item.MarketComparable)
        {
            var why = item.LotQuantity > 1
                ? $"a lot of {item.LotQuantity} against per-unit sold comps"
                : "the sold comps matched something else";
            return days >= StaleDays
                ? ("stale", $"{days} days unsold. No price recommendation — {why}, so the market figure can't be compared to the asking price.")
                : ("no_data", $"Couldn't be priced reliably — {why}.");
        }

        if (item.BreakEvenPrice is decimal breakEven && breakEven > market)
            return ("underwater",
                $"Break-even is ${breakEven:0.00}; the market pays about ${market:0.00}. Holding, bundling or a deliberate loss are the real options.");

        if (days >= DeadDays && item.WatchCount == 0 && gap > 0)
            return ("dead_capital",
                $"{days} days live, no watchers, and {gap:0}% above market — this is money parked, not inventory working.");

        if (days >= StaleDays)
            return ("stale", gap > 0
                ? $"{days} days unsold at {gap:0}% above market."
                : $"{days} days unsold even at market price — the price may not be the blocker here.");

        if (gap >= OverpricedGapPercent)
            return ("overpriced", $"Listed {gap:0}% above what comparable items are actually selling for.");

        if (gap <= UnderpricedGapPercent && item.SoldCompCount + item.TerapeakCompCount >= MinCompsForChange)
            return ("underpriced",
                $"Listed {Math.Abs(gap):0}% below market — about ${Math.Round((market - item.ListPrice) * item.Quantity, 2):0.00} left on the table if it sells as listed.");

        if (days is int fresh and < FreshDays)
            return ("fresh", $"{fresh} days old and priced in line with the market — give it time.");

        return ("priced_right", "Priced in line with what comparable items are selling for.");
    }

    /// <summary>Portfolio-level totals — the view eBay Seller Hub never shows the seller.</summary>
    public static InventoryHealthSummary Summarize(IReadOnlyList<InventoryHealthItem> items)
    {
        var summary = new InventoryHealthSummary
        {
            ListingsAnalyzed = items.Count,
            ListingsPriced = items.Count(i => i.MarketPrice.HasValue),
            WithCostBasis = items.Count(i => i.HasCostBasis),
            TotalListedValue = Math.Round(items.Sum(i => i.ListPrice * i.Quantity), 2),
            TotalCapitalTiedUp = Math.Round(items.Sum(i => i.CapitalTiedUp), 2),
            NoDataCount = items.Count(i => i.Verdict == "no_data"),
            UnderwaterCount = items.Count(i => i.Verdict == "underwater"),
            PricedRightCount = items.Count(i => i.Verdict is "priced_right" or "fresh" or "selling"),
        };

        // An old listing that is still selling units is not stale capital — its stock is turning.
        // Counting it would inflate the one headline number the seller acts on.
        var stale = items.Where(i => i.DaysListed >= StaleDays && !i.IsSelling).ToList();
        summary.StaleCount = stale.Count;
        summary.StaleCapital = Math.Round(stale.Sum(i => i.CapitalTiedUp), 2);

        var dead = items.Where(i => i.Verdict == "dead_capital").ToList();
        summary.DeadCapitalCount = dead.Count;
        summary.DeadCapital = Math.Round(dead.Sum(i => i.CapitalTiedUp), 2);

        // Gap-based totals count only rows whose market comparison actually held — a lot listing
        // measured against per-unit comps would otherwise contribute a five-figure fiction to the
        // headline "priced above market" number.
        var over = items.Where(i => i.MarketComparable
                                 && (i.Verdict == "overpriced" || i.PriceGapPercent >= OverpricedGapPercent)).ToList();
        summary.OverpricedCount = over.Count;
        summary.TotalAboveMarket = Math.Round(
            over.Sum(i => (i.ListPrice - (i.MarketPrice ?? i.ListPrice)) * i.Quantity), 2);

        var under = items.Where(i => i.Verdict == "underpriced").ToList();
        summary.UnderpricedCount = under.Count;
        summary.MoneyLeftOnTable = Math.Round(
            under.Sum(i => ((i.MarketPrice ?? i.ListPrice) - i.ListPrice) * i.Quantity), 2);

        // Only the markdowns. Raises need per-item review and are excluded from the bulk figure
        // for the same reason they are excluded from the bulk apply.
        var candidates = items.Where(i => i.HasRecommendation && !i.RequiresReview).ToList();
        summary.RepriceCandidates = candidates.Count;
        summary.ProjectedNetIfRepricedSells = Math.Round(
            candidates.Where(i => i.NetProfitAtSuggested.HasValue)
                      .Sum(i => i.NetProfitAtSuggested!.Value * i.Quantity), 2);

        var ages = items.Where(i => i.DaysListed.HasValue).Select(i => i.DaysListed!.Value).OrderBy(d => d).ToList();
        summary.MedianDaysListed = ages.Count == 0 ? null
            : ages.Count % 2 == 1 ? ages[ages.Count / 2]
            : (ages[ages.Count / 2 - 1] + ages[ages.Count / 2]) / 2;
        summary.UnknownAgeCount = items.Count(i => i.DaysListed is null);

        return summary;
    }

    /// <summary>
    /// Biggest stuck money first. Listings with an actionable recommendation lead, then the ones
    /// holding the most capital, then the oldest — so the top of the table is always the thing
    /// worth doing something about this morning.
    /// </summary>
    public static List<InventoryHealthItem> Rank(IEnumerable<InventoryHealthItem> items) =>
        items.OrderByDescending(i => i.HasRecommendation && !i.RequiresReview)
             .ThenByDescending(i => i.HasRecommendation)
             .ThenByDescending(i => i.CapitalTiedUp)
             .ThenByDescending(i => i.DaysListed ?? -1)
             .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>
    /// Which products are worth spending a real Terapeak scrape on, given the scan already has a
    /// preliminary price for each from the sold-comps database.
    /// </summary>
    /// <remarks>
    /// Ordered by money at stake rather than by how wrong the price looks: a 40% gap on a $12 item
    /// is not worth a page load, and a 16% gap on a $1,400 one is. Products Terapeak has already
    /// cached are excluded — that data is already in hand and free.
    /// </remarks>
    public static List<string> SelectScrapeTargets(
        IEnumerable<(string Key, decimal? DollarsAtStake, bool HasTerapeak, bool Unpriced)> groups, int budget)
    {
        if (budget <= 0) return [];

        var candidates = groups.Where(g => !g.HasTerapeak).ToList();

        var mispriced = candidates
            .Where(g => !g.Unpriced && g.DollarsAtStake is > 0)
            .OrderByDescending(g => g.DollarsAtStake!.Value)
            .Select(g => g.Key);

        // Then the ones the comps database could not price at all, most valuable first — an
        // unpriceable $900 listing is worth a look and an unpriceable $9 one is not.
        var unpriced = candidates
            .Where(g => g.Unpriced)
            .OrderByDescending(g => g.DollarsAtStake ?? 0m)
            .Select(g => g.Key);

        return mispriced.Concat(unpriced).Take(budget).ToList();
    }
}
