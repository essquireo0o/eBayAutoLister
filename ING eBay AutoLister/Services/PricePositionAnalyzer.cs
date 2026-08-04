using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Puts the seller's own live listings next to the listings a buyer sees beside them, and answers
/// the question no sold-comp can: <i>when somebody searches for this today, how many cheaper copies
/// of it do they scroll past before they reach yours?</i>
/// </summary>
/// <remarks>
/// <para>
/// Every pricing surface in this app runs on <b>sold</b> comps — <see cref="MarketPriceEstimator"/>,
/// <see cref="InventoryHealthAnalyzer"/>, <see cref="AgingInventoryRescuer"/>, the Terapeak path,
/// all of it. Sold comps answer "what do buyers pay for this", and they are the right basis for
/// deciding what to buy. They are the wrong basis for deciding why something has not sold, because
/// a listing can be priced perfectly against every sale of the last sixty days and still be eighth
/// on a shelf of eight. The buyer never sees the sold history. They see the shelf.
/// </para>
/// <para>
/// The comparison is on <b>delivered price</b> — item plus shipping — because that is what the
/// buyer pays and what eBay's cheapest-first sort orders on. A $79 listing with $18 shipping is
/// behind an $89 one that ships free, and comparing asking prices says the opposite.
/// </para>
/// <para>
/// Nothing here re-derives money. Break-even, the seller's floor and take-home all come from
/// <see cref="NetProceedsCalculator"/>, which is the same calculation the listing editor and the
/// offers board negotiate against — so a price this screen recommends cannot be one another screen
/// would call a loss.
/// </para>
/// <para>
/// The board makes no eBay call of its own and holds no state: it is handed the seller's listing
/// and whatever the live search returned, and it judges them. Everything it refuses to say is a
/// rule in this file rather than a check in a UI somebody can move.
/// </para>
/// </remarks>
public sealed class PricePositionAnalyzer(ProductNormalizer normalizer, NetProceedsCalculator netCalc)
{
    // ── The two bases a row can be judged on ─────────────────────────────────────────────────
    // Delivered price is the real one. Item price is the fallback for a listing whose own shipping
    // charge eBay did not report, and the row says which one it used — an unknown shipping charge
    // is not free shipping, and quietly treating it as zero would make the seller look cheaper than
    // they are on exactly the rows the board is about to recommend a price cut on.
    public const string Delivered = "delivered";
    public const string ItemPrice = "item_price";

    // ── How much of a market it takes before "you are 7th" means anything ────────────────────
    // Two rivals is an anecdote. Called at two, half the board would be "you are 2nd of 2", which
    // is a sentence with no information in it and a markdown attached.
    public const int MinRivalsForPosition = 3;

    // ── The band around the cheapest credible rival that counts as being on the shelf ─────────
    // Inside it, a buyer picks on feedback, returns policy and photos rather than on price, and
    // cutting further donates margin to a sale the listing was already in the running for.
    public const decimal CompetitiveBandPercent = 5m;

    // ── The outlier rule ─────────────────────────────────────────────────────────────────────
    // The cheapest listing on a shelf is quite often broken, mislabelled, a photo of the box, or a
    // seller with 0 feedback who will never ship it. Chasing that price is how a working reseller
    // destroys a good margin in an afternoon. A rival below this fraction of the median is stepped
    // over, and the row says it was — the target becomes the cheapest listing a seller could
    // plausibly be losing the sale to.
    public const decimal OutlierFloorFraction = 0.65m;

    // ── When "nobody has seen it" is allowed to be the answer ────────────────────────────────
    // A listing three days old has not had a fair run in front of anybody, so its view count says
    // nothing. Past a fortnight it does.
    public const int MinDaysBeforeVisibilityVerdict = 14;
    // Roughly one view every two and a half days. Under that, a buyer rejecting the price is not
    // what is happening, because buyers are not arriving.
    public const int LowViewsPerMonth = 12;

    // A rival priced at or under this is a listing error, a case, or a manual — not the product.
    private const decimal ImplausiblyCheapFraction = 0.15m;

    /// <summary>
    /// The search that finds this listing's shelf. Deliberately <see cref="JackpotHunter.ShoppingQuery"/>,
    /// which is what the sourcing boards search with — a product the seller is told to go and buy
    /// and a product they are told they are eighth on must be the same search, or the two screens
    /// are describing different shelves.
    /// </summary>
    public static string ShelfQuery(string? title) => JackpotHunter.ShoppingQuery(title, maxWords: 6);

    /// <summary>A row for a listing whose live search could not be run at all.</summary>
    public PricePositionRow Failed(EbayListingSummary listing, string reason, DateTime nowUtc)
    {
        var row = Describe(listing, nowUtc);
        row.Verdict = "lookup_failed";
        row.Blocker = "none";
        row.Headline = "The live listings for this one could not be read, so it has no position on this board.";
        row.Cautions.Add(reason);
        return row;
    }

    /// <summary>
    /// Where one live listing sits, given whatever the live search returned for it.
    /// </summary>
    /// <param name="viewsReported">
    /// Whether ANY listing in this scan came back with a view count. eBay only returns
    /// <c>HitCount</c> on some accounts, and a zero from an API that reports nothing is not a zero —
    /// with this false the board never blames visibility for anything.
    /// </param>
    public PricePositionRow Build(
        EbayListingSummary listing, IReadOnlyList<EbayOpportunityItem> found, CostBasisEntry? cost,
        FeeProfile fees, DateTime nowUtc, bool viewsReported)
    {
        var row = Describe(listing, nowUtc);
        row.ViewsKnown = viewsReported;

        // The seller's own shipping charge decides the basis, and the basis decides which rivals
        // can be compared at all.
        row.Basis = listing.ShippingCostKnown ? Delivered : ItemPrice;
        row.MyShipping = listing.ShippingCostKnown ? Math.Max(0m, listing.ShippingCost) : 0m;
        row.MyShippingKnown = listing.ShippingCostKnown;
        row.MyComparedPrice = row.Basis == Delivered
            ? Math.Round(listing.Price + row.MyShipping, 2)
            : Math.Round(listing.Price, 2);

        row.Rivals = Screen(listing, found, row.Basis, row.MyComparedPrice);
        row.RivalsFound = row.Rivals.Count;

        var counted = row.Rivals.Where(r => r.Counted && r.DeliveredPrice is > 0m)
            .OrderBy(r => r.DeliveredPrice!.Value).ToList();
        row.RivalsCounted = counted.Count;

        // ── The money, borrowed rather than re-derived ────────────────────────────────────────
        var quote = netCalc.Quote(
            askPrice: listing.Price, unitCost: cost?.TotalUnitCost, fees: fees,
            buyerPaidShipping: row.MyShipping, quantity: 1);
        row.HasCostBasis = cost is not null && cost.TotalUnitCost > 0m;
        if (row.HasCostBasis)
        {
            row.FloorPrice = quote.MinimumOfferPrice;
            row.FloorBasis = quote.MinimumOfferBasis;
            row.NetProfitNow = quote.NetProfit;
        }

        if (counted.Count == 0)
        {
            row.Verdict = "alone";
            row.Headline = row.RivalsFound > 0
                ? "Nothing on this shelf could be compared like-for-like, so there is no position to report."
                : "Nobody else is selling this right now. Being the only one is pricing power — this is not a listing to cut.";
            ApplyVisibility(row);
            return row;
        }

        row.CheapestRival = counted[0].DeliveredPrice;
        row.MedianRival = Median(counted.Select(r => r.DeliveredPrice!.Value).ToList());

        if (counted.Count < MinRivalsForPosition)
        {
            row.Verdict = "thin_market";
            row.Headline = counted.Count == 1
                ? $"One other listing, at {Money(row.CheapestRival!.Value)}. One is not a shelf — it is a coincidence."
                : $"Only {counted.Count} comparable listings, from {Money(row.CheapestRival!.Value)}. Too few to call a position on.";
            ApplyVisibility(row);
            return row;
        }

        // ── The target: the cheapest rival worth pricing against ─────────────────────────────
        var floorForOutliers = row.MedianRival!.Value * OutlierFloorFraction;
        var target = counted.FirstOrDefault(r => r.DeliveredPrice!.Value >= floorForOutliers) ?? counted[^1];
        row.TargetRival = target.DeliveredPrice;
        row.TargetSkippedAnOutlier = target.DeliveredPrice != row.CheapestRival;

        // Rank on the shelf a buyer sorts cheapest-first: everybody strictly cheaper, plus one.
        row.Rank = counted.Count(r => r.DeliveredPrice!.Value < row.MyComparedPrice) + 1;

        var targetPrice = row.TargetRival!.Value;
        row.PremiumPercent = targetPrice > 0m
            ? Math.Round((row.MyComparedPrice - targetPrice) / targetPrice * 100m, 1)
            : null;

        if (row.TargetSkippedAnOutlier)
            row.Cautions.Add($"The cheapest one at {Money(row.CheapestRival!.Value)} is far under the rest of the shelf — "
                + "broken, mislabelled or a seller who will not ship it. This is priced against "
                + $"{Money(targetPrice)} instead.");

        // A cent under the target is what puts the listing first in a cheapest-first sort. It is
        // reported as the arithmetic answer, not as an instruction: the verdicts below decide
        // whether going there is a good idea, and the floor decides whether it is allowed at all.
        var lead = Math.Round(targetPrice - 0.01m, 2);
        var itemLead = row.Basis == Delivered ? Math.Round(lead - row.MyShipping, 2) : lead;
        if (itemLead > 0m)
        {
            row.PriceToLead = lead;
            row.ItemPriceToLead = itemLead;
            if (row.HasCostBasis)
                row.NetProfitAtLeadPrice = NetProceedsCalculator.NetProfitAt(itemLead, quote.BreakEvenPrice, fees);
        }
        else
        {
            row.Cautions.Add($"You charge {Money(row.MyShipping)} shipping, which is more than the whole shelf's "
                + "cheapest delivered price — there is no asking price that gets you to the front.");
        }

        var withinBand = row.PremiumPercent is decimal p && p <= CompetitiveBandPercent;
        var affordable = !row.HasCostBasis || (row.FloorPrice is decimal f && row.ItemPriceToLead is decimal il && il >= f);

        if (row.MyComparedPrice <= targetPrice)
        {
            row.Verdict = "leading";
            row.Headline = $"You are the cheapest of {counted.Count + 1} at {Money(row.MyComparedPrice)}"
                + (row.Basis == Delivered ? " delivered." : ".")
                + " Price is not what is holding this one up.";
        }
        else if (withinBand)
        {
            row.Verdict = "competitive";
            row.Headline = $"{Ordinal(row.Rank!.Value)} of {counted.Count + 1}, {Money(row.MyComparedPrice - targetPrice)} over the front. "
                + "Close enough that the buyer is choosing on feedback and photos, not on price.";
        }
        else if (!affordable)
        {
            row.Verdict = "cant_win";
            row.Blocker = "supply";
            row.Headline = $"The shelf starts at {Money(targetPrice)} and you cannot go there without losing money — "
                + $"your floor is {Money(row.FloorPrice!.Value)}. This is a buying problem, not a pricing one.";
            row.Cautions.Add("Somebody is sourcing this cheaper than you are. Cutting to match them turns a slow listing "
                + "into a fast loss.");
        }
        else
        {
            row.Verdict = "priced_out";
            row.Blocker = "price";
            row.Headline = $"{Ordinal(row.Rank!.Value)} of {counted.Count + 1} — {row.PremiumPercent:0.#}% over the front of the shelf. "
                + $"{row.Rank!.Value - 1} cheaper listing{(row.Rank!.Value - 1 == 1 ? "" : "s")} are seen before yours.";
        }

        if (row.Verdict != "cant_win" && !row.HasCostBasis && row.ItemPriceToLead is not null)
            row.Cautions.Add("No cost is recorded for this listing, so nothing here knows whether that price still "
                + "makes money. Enter what you paid and the board will draw the line for you.");

        if (row.Verdict == "priced_out" && row.WatchCount >= 3)
            row.Cautions.Add($"{row.WatchCount} people are watching it at your price — they found it and stopped at the "
                + "number. An offer to watchers gets the sale without moving the public price.");

        ApplyVisibility(row);
        return row;
    }

    // ── The second axis ──────────────────────────────────────────────────────────────────────
    // Position explains a listing buyers reject. It explains nothing about a listing buyers never
    // reach, and those are the two failures that look identical from Seller Hub: no sale either
    // way. A listing that is already the cheapest on the shelf and has had four views in six weeks
    // does not have a price problem, and telling its owner to cut is telling them to give away
    // margin to fix something else entirely.
    private static void ApplyVisibility(PricePositionRow row)
    {
        if (row.Blocker is "price" or "supply") return;
        if (!row.ViewsKnown) return;
        if (row.DaysListed is not int days || days < MinDaysBeforeVisibilityVerdict) return;

        var viewsPerMonth = row.ViewCount * 30m / days;
        if (viewsPerMonth >= LowViewsPerMonth) return;

        row.Blocker = "visibility";
        row.Cautions.Add($"{row.ViewCount} view{(row.ViewCount == 1 ? "" : "s")} in {days} days. "
            + "Nobody is rejecting this price — nobody is reaching it. That is a title and item-specifics "
            + "problem, and Listing Copilot is the screen for it.");
    }

    /// <summary>
    /// Which of the listings the live search returned are actually the same shelf, and which are
    /// only context.
    /// </summary>
    /// <remarks>
    /// Every rule here makes the board's numbers smaller and its position weaker, which is the
    /// point: a rival that is not the product makes the seller look expensive against something
    /// they were never competing with, and the recommendation attached to that is a real price cut.
    /// </remarks>
    public List<PriceRival> Screen(
        EbayListingSummary mine, IReadOnlyList<EbayOpportunityItem> found, string basis, decimal myComparedPrice)
    {
        var target = normalizer.Normalize(mine.Title);
        var myQuantity = Math.Max(1, target.Quantity);
        var myBucket = ConditionBucket(mine.Condition);

        var rivals = new List<PriceRival>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in found)
        {
            // The seller's own listing is not competition, and eBay returns it like any other.
            if (!string.IsNullOrWhiteSpace(item.ItemId) &&
                string.Equals(item.ItemId, mine.ListingId, StringComparison.OrdinalIgnoreCase)) continue;
            if (item.Price <= 0m) continue;
            if (!string.IsNullOrWhiteSpace(item.ItemId) && !seen.Add(item.ItemId)) continue;

            var rival = new PriceRival
            {
                ItemId = item.ItemId,
                Title = item.Title,
                Url = item.Url,
                SellerUsername = item.SellerUsername,
                SellerFeedbackScore = item.SellerFeedbackScore,
                Condition = item.Condition,
                Price = Math.Round(item.Price, 2),
                ShippingCost = Math.Round(Math.Max(0m, item.ShippingCost), 2),
                ShippingStated = item.ShippingStated,
            };

            rival.SkipReason = SkipReason(item, target, myQuantity, myBucket, basis, myComparedPrice);
            rival.Counted = rival.SkipReason is null;
            rival.DeliveredPrice = basis == Delivered
                ? (item.ShippingStated ? Math.Round(rival.Price + rival.ShippingCost, 2) : null)
                : rival.Price;

            // Belt and braces: a counted rival with no comparable price would rank as free.
            if (rival.DeliveredPrice is null or <= 0m && rival.Counted)
            {
                rival.Counted = false;
                rival.SkipReason ??= "No comparable price.";
            }

            rivals.Add(rival);
        }

        // Cheapest first, then the context rows — the same order the buyer's screen is in.
        return rivals
            .OrderBy(r => r.Counted ? 0 : 1)
            .ThenBy(r => r.DeliveredPrice ?? r.Price)
            .ToList();
    }

    private string? SkipReason(
        EbayOpportunityItem item, NormalizedProduct target, int myQuantity, string myBucket,
        string basis, decimal myComparedPrice)
    {
        // A repair service or a manual borrows the product's identifiers without being the product.
        // Priced at a dollar to sort first, one of these alone can make a fairly-priced listing look
        // 4,000% over the market. See NonItemListingDetector.
        if (NonItemListingDetector.Detect(item.Title) is string junk) return junk;

        // A bid in progress is not an asking price. An auction sitting at $9 on day one of seven
        // is not a $9 competitor, and half the board would be built out of them.
        if (item.BuyingOption.Contains("AUCTION", StringComparison.OrdinalIgnoreCase))
            return "An auction still running — a current bid is not an asking price.";

        var candidate = normalizer.Normalize(item.Title);

        // A lot of ten is not a rival to one, in either direction.
        var theirQuantity = Math.Max(1, candidate.Quantity);
        if (theirQuantity != myQuantity)
            return theirQuantity > myQuantity
                ? $"A lot of {theirQuantity}, not a single unit."
                : $"{theirQuantity} unit{(theirQuantity == 1 ? "" : "s")} against your {myQuantity}.";

        // Different product entirely. Part number first, because it is the only identifier in a
        // title that is ever exact; the model number is the next best thing.
        if (!string.IsNullOrWhiteSpace(target.PartNumber) && !string.IsNullOrWhiteSpace(candidate.PartNumber)
            && !ComparableMatcher.PartNumberMatch(target.PartNumber, candidate.PartNumber))
            return $"A different part number ({candidate.PartNumber}).";

        if (!string.IsNullOrWhiteSpace(target.Model) && !string.IsNullOrWhiteSpace(candidate.Model)
            && !string.Equals(target.Model, candidate.Model, StringComparison.OrdinalIgnoreCase))
            return $"A different model ({candidate.Model}).";

        // The listing is FOR a part of the product rather than the product — a filter, a bracket,
        // a replacement tank. It carries the same brand and model and is a fraction of the price.
        if (candidate.IsAccessoryListing && !target.IsAccessoryListing)
            return "An accessory for it, not the item.";

        // Condition is not a tie-break, it is a different product to a buyer. A for-parts unit at
        // $40 is not competition for a working one, and pricing against it is how a seller ends up
        // selling working stock at scrap.
        var theirBucket = ConditionBucket(item.Condition);
        if (myBucket != "unknown" && theirBucket != "unknown" && myBucket != theirBucket)
            return $"{item.Condition} against your {myBucket}.";

        // On the delivered basis, a rival who states no shipping cost has no delivered price. Local
        // pickup and freight items land here, and calling their shipping free would put a $900
        // pallet at the front of the shelf.
        if (basis == Delivered && !item.ShippingStated)
            return "No shipping cost stated, so there is no delivered price to compare.";

        // Last guard, and the one that catches what the title rules miss: a price this far under
        // the seller's own is not the same item at a better price, it is a different item.
        if (myComparedPrice > 0m && item.Price < myComparedPrice * ImplausiblyCheapFraction)
            return "Too cheap to be the same item — priced like a part or a listing error.";

        return null;
    }

    /// <summary>
    /// The board's order: the listings with the most money standing behind cheaper copies of
    /// themselves, first.
    /// </summary>
    /// <remarks>
    /// Not "most over the market by percent". A 60% premium on a $14 item is $8 and a 12% premium
    /// on a $1,900 miner is $228, and a seller with twenty minutes should be looking at the second
    /// one. Rows the board has no position for sink below every row it does, because a row with no
    /// answer is not an instruction.
    /// </remarks>
    public static List<PricePositionRow> Rank(IEnumerable<PricePositionRow> rows) =>
        rows.OrderBy(r => VerdictOrder(r.Verdict))
            .ThenByDescending(r => r.Verdict is "priced_out" or "cant_win"
                ? OverpricedBy(r) * Math.Max(1, r.Quantity)
                : 0m)
            .ThenByDescending(r => r.CapitalListed)
            .ToList();

    private static decimal OverpricedBy(PricePositionRow r) =>
        r.TargetRival is decimal t && r.MyComparedPrice > t ? r.MyComparedPrice - t : 0m;

    private static int VerdictOrder(string verdict) => verdict switch
    {
        "priced_out" => 0,
        "cant_win" => 1,
        "competitive" => 2,
        "leading" => 3,
        "alone" => 4,
        "thin_market" => 5,
        _ => 6,
    };

    public static PricePositionSummary Summarize(IReadOnlyList<PricePositionRow> rows)
    {
        var summary = new PricePositionSummary { Rows = rows.Count };

        foreach (var r in rows)
        {
            if (r.Verdict == "priced_out") summary.PricedOut++;
            if (r.Verdict == "leading") summary.Leading++;
            if (r.Verdict == "cant_win") summary.CantWin++;
            if (r.Verdict == "alone") summary.Alone++;
            if (r.Blocker == "visibility") summary.VisibilityBlocked++;
        }

        var pricedOut = rows.Where(r => r.Verdict == "priced_out").ToList();
        summary.CapitalBehindTheShelf = Math.Round(pricedOut.Sum(r => r.MyPrice * Math.Max(1, r.Quantity)), 2);

        // What the seller would still take home if every priced-out listing moved to the front of
        // its shelf. Deliberately the profit AFTER the cut, not the size of the cut: the cut is a
        // cost and printing it as a headline would be selling the seller their own markdown.
        // Rows with no cost basis are counted separately rather than assumed to be worth nothing —
        // a figure that quietly excludes half the board is a figure the seller cannot use.
        var costed = pricedOut.Where(r => r.HasCostBasis && r.NetProfitAtLeadPrice is not null).ToList();
        summary.PricedOutWithoutCost = pricedOut.Count - costed.Count;
        if (costed.Count > 0)
            summary.ProfitStillOnTheTable = Math.Round(
                costed.Sum(r => Math.Max(0m, r.NetProfitAtLeadPrice!.Value) * Math.Max(1, r.Quantity)), 2);

        var worst = pricedOut.Where(r => r.PremiumPercent is not null)
            .OrderByDescending(r => r.PremiumPercent!.Value).FirstOrDefault();
        if (worst is not null)
        {
            summary.WorstPremiumPercent = worst.PremiumPercent;
            summary.WorstPremiumTitle = worst.Title;
        }

        return summary;
    }

    // ── Shared plumbing ──────────────────────────────────────────────────────────────────────

    private PricePositionRow Describe(EbayListingSummary listing, DateTime nowUtc) => new()
    {
        ListingId = listing.ListingId,
        Sku = listing.Sku,
        Title = listing.Title,
        ListingUrl = listing.ListingUrl,
        ThumbnailUrl = listing.ThumbnailUrl,
        SearchQuery = ShelfQuery(listing.Title),
        MyPrice = Math.Round(listing.Price, 2),
        MyComparedPrice = Math.Round(listing.Price, 2),
        Quantity = Math.Max(1, listing.Quantity),
        WatchCount = listing.WatchCount,
        ViewCount = listing.HitCount,
        DaysListed = InventoryHealthAnalyzer.DaysListed(listing.StartTimeUtc, nowUtc),
        CapitalListed = Math.Round(listing.Price * Math.Max(1, listing.Quantity), 2),
        Basis = ItemPrice,
        Verdict = "alone",
    };

    /// <summary>
    /// New, used, refurbished, for-parts — or unknown, which never rejects anybody. eBay's
    /// condition strings vary by category and a wrong bucket silently empties a shelf.
    /// </summary>
    public static string ConditionBucket(string? condition)
    {
        var text = (condition ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return "unknown";

        if (text.Contains("parts") || text.Contains("not working") || text.Contains("salvage")) return "for parts";
        if (text.Contains("refurb") || text.Contains("renewed") || text.Contains("reconditioned")) return "refurbished";
        if (text.Contains("new")) return "new";
        if (text.Contains("open box")) return "new";
        if (text.Contains("used") || text.Contains("pre-owned") || text.Contains("preowned")
            || text.Contains("good") || text.Contains("acceptable")) return "used";

        return "unknown";
    }

    private static decimal Median(List<decimal> sorted)
    {
        if (sorted.Count == 0) return 0m;
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[mid]
            : Math.Round((sorted[mid - 1] + sorted[mid]) / 2m, 2);
    }

    private static string Ordinal(int n) => n switch
    {
        1 => "1st", 2 => "2nd", 3 => "3rd",
        _ when n % 100 is >= 11 and <= 13 => $"{n}th",
        _ when n % 10 == 1 => $"{n}st",
        _ when n % 10 == 2 => $"{n}nd",
        _ when n % 10 == 3 => $"{n}rd",
        _ => $"{n}th",
    };

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
