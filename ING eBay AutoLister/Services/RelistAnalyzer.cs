using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Works out which of the seller's ended, unsold listings are worth putting back up, at what
/// price, and which ended auctions still have a buyer attached who already named a number.
/// </summary>
/// <remarks>
/// <para>
/// Every other money screen in this app is about a sale that has not happened yet. This one is
/// about sales that <em>nearly</em> happened and then quietly expired. That inventory is invisible
/// everywhere else — it is not in the active-listing import, it never reached Money Made, and
/// eBay's own Unsold page offers a Relist button with no price, no market read and no cost basis
/// behind it. So the default behaviour of the platform is to put the same failed price back up and
/// fail again, which is the most expensive free action in reselling: the item is already bought,
/// already photographed, already stored.
/// </para>
/// <para>
/// Two things are recovered here, and they are worth very different amounts. A <b>relist</b> is a
/// second run at a maybe. A <b>Second Chance Offer</b> is a message to somebody who publicly bid a
/// specific dollar amount on this exact item and lost — the shortest distance in the whole app
/// between one click and money in the account. The board is ranked accordingly.
/// </para>
/// <para>
/// No fee math is re-derived. Break-even comes from the same <see cref="ProfitCalculator"/> /
/// <see cref="FeeProfile"/> pair every other screen costs an item with, the floor comes from
/// <see cref="NetProceedsCalculator"/>, and the charm-price rounding is
/// <see cref="InventoryHealthAnalyzer.Charm"/> — a break-even is a break-even whichever screen
/// asks. Everything here is pure except <see cref="Build"/>, which only calls the calculator.
/// </para>
/// </remarks>
public sealed class RelistAnalyzer(ProfitCalculator profitCalc)
{
    // ── How far back eBay will still show, and still relist ──────────────────────────────────
    // GetMyeBaySelling's unsold list tops out at 60 days, and eBay's relist paths stop working
    // some time after that. Asking for more would silently return less.
    public const int MaxLookbackDays = 60;
    public const int DefaultLookbackDays = 45;

    // ── Evidence bars ────────────────────────────────────────────────────────────────────────
    // Below this many sold comps the market figure cannot carry a price change in either
    // direction — the same bar the repricer and the arbitrage verdicts use.
    public const int MinCompsForChange = 3;
    // Priced this far over the market is a straightforward diagnosis: the price was the blocker.
    private const decimal OverpricedGapPercent = 10m;
    // Priced this far UNDER the market and still unsold is evidence the comp match is wrong for
    // this exact item, not evidence to raise the price. Same reasoning as the repricer's raise guard.
    private const decimal UnderpricedGapPercent = -10m;
    // Past this the "market price" is a matching failure rather than a mispricing.
    private const decimal ImplausibleGapPercent = 300m;
    // Enough people looked at the page for "nobody saw it" to be off the table.
    public const int ViewsMeanSeen = 25;

    // ── The relist ladder, as a cut off the price that already failed ────────────────────────
    // A crowd that watched and did not buy is close, and a small step closes it. A listing nobody
    // saved needs a sharper hook. This is the same shape as WatcherOfferAdvisor's ladder and for
    // the same reason: audience size is evidence about how much discount the sale actually needs.
    private const decimal CrowdCutPercent = 3m;      // 5+ watchers
    private const decimal InterestedCutPercent = 6m; // 1-4 watchers
    private const decimal SeenNotSavedCutPercent = 8m;
    public const int CrowdWatcherCount = 5;

    // No single relist cuts deeper than this. Past it the right move is a decision the seller
    // looks at, not a default the app picked — the repricer takes the same posture.
    private const decimal MaxSingleCutPercent = 30m;
    // Below this a "new" price is the old price with extra steps.
    private const decimal MinChangePercent = 2m;
    private const decimal MinChangeDollars = 1m;

    // ── Second Chance Offers ─────────────────────────────────────────────────────────────────
    // eBay carries these four durations and no others.
    public static readonly int[] OfferDurations = [1, 3, 5, 7];
    public const int DefaultOfferDays = 3;
    public const int MaxMessageLength = 250;

    public const string DefaultSellerMessage =
        "You bid on this and just missed it. It's available at your bid price if you still want it.";

    /// <summary>Trims a seller's message to what eBay will carry.</summary>
    public static string CleanMessage(string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0) return "";
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength].TrimEnd();
    }

    /// <summary>The nearest duration eBay accepts, for a requested number of days.</summary>
    public static int NormalizeDuration(int days) =>
        OfferDurations.Contains(days) ? days : DefaultOfferDays;

    /// <summary>Whole days between two instants, or null when the date is missing.</summary>
    public static int? DaysBetween(DateTime? fromUtc, DateTime toUtc)
    {
        if (fromUtc is not DateTime from) return null;
        var days = (toUtc - DateTime.SpecifyKind(from, DateTimeKind.Utc)).TotalDays;
        return (int)Math.Max(0, Math.Floor(days));
    }

    /// <summary>
    /// Builds one judged row from an ended listing, whatever priced it, and whatever the seller paid.
    /// </summary>
    public RelistCandidate Build(
        EbayEndedListing ended, ResalePricing? resale, CostBasisEntry? cost,
        FeeProfile fees, DateTime nowUtc, int lotQuantity = 1, decimal? minNetProfitOverride = null)
    {
        var item = new RelistCandidate
        {
            ListingId = ended.ListingId,
            Sku = ended.Sku,
            Title = ended.Title,
            Url = ended.ListingUrl,
            ImageUrl = ended.ThumbnailUrl,
            Condition = ended.Condition,
            IsAuction = ended.IsAuction,
            EndPrice = ended.Price,
            Quantity = Math.Max(1, ended.QuantityUnsold > 0 ? ended.QuantityUnsold : ended.Quantity),
            EndTimeUtc = ended.EndTimeUtc,
            DaysSinceEnded = DaysBetween(ended.EndTimeUtc, nowUtc),
            WatchCount = ended.WatchCount,
            HitCount = ended.HitCount,
            BidCount = ended.BidCount,
            HighBid = ended.HighBid,
            AlreadyRelisted = !string.IsNullOrWhiteSpace(ended.RelistedItemId),
            RelistedItemId = ended.RelistedItemId,
            EndedByUser = ended.EndedByUser,
            CostBasis = cost?.TotalUnitCost,
            LotQuantity = Math.Max(1, lotQuantity),
        };

        if (ended.StartTimeUtc is DateTime start && ended.EndTimeUtc is DateTime end)
            item.DaysListed = DaysBetween(start, DateTime.SpecifyKind(end, DateTimeKind.Utc));

        // ── The market read ──────────────────────────────────────────────────────────────────
        var shipping = resale?.AvgCompShipping ?? 0m;
        if (resale is not null && resale.HasPrice)
        {
            item.PricedAs = resale.LookupTitle;
            item.MarketPrice = resale.ExpectedSale ?? resale.Median;
            item.QuickSalePrice = resale.QuickSale;
            item.SoldCompCount = resale.SoldCompCount;
            item.TerapeakCompCount = resale.TerapeakCompCount;

            var market = item.MarketPrice!.Value;
            if (market > 0m && ended.Price > 0m)
                item.PriceGapPercent = Math.Round((ended.Price - market) / market * 100m, 1);

            if (item.LotQuantity > 1)
            {
                item.MarketComparable = false;
                item.Signals.Add($"This was a lot of {item.LotQuantity}; sold comps are priced per unit, so the market figure is not a like-for-like comparison.");
            }
            else if (Math.Abs(item.PriceGapPercent ?? 0m) > ImplausibleGapPercent)
            {
                item.MarketComparable = false;
                item.Signals.Add($"The comps came back {Math.Abs(item.PriceGapPercent!.Value):0}% away from the asking price — that is a matching failure, not a mispricing.");
            }
        }
        else
        {
            item.PricedAs = resale?.LookupTitle ?? "";
        }

        // ── The floor ────────────────────────────────────────────────────────────────────────
        // Shipping is booked on both sides, identically to every other screen: the buyer pays it
        // and it costs the seller the same to ship.
        ProfitBreakdown? At(decimal salePrice) => cost is null ? null : profitCalc.Calculate(
            supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: salePrice,
            quickSalePrice: salePrice, buyerPaidShipping: shipping, fees: fees,
            actualShippingCostOverride: shipping > 0m ? shipping : null);

        if (ended.Price > 0m && At(ended.Price) is ProfitBreakdown atEnd)
        {
            item.NetProfitAtEndPrice = atEnd.NetProfitPerUnit;
            item.BreakEvenPrice = atEnd.BreakEvenSalePrice == decimal.MaxValue ? null : atEnd.BreakEvenSalePrice;
            var (floor, basis) = NetProceedsCalculator.MinimumOffer(
                item.BreakEvenPrice, fees, shipping, minNetProfitOverride);
            item.FloorPrice = floor;
            item.FloorBasis = floor is null ? "none" : basis;
        }

        // ── The price ────────────────────────────────────────────────────────────────────────
        var suggestion = SuggestRelistPrice(
            endPrice: ended.Price, market: item.MarketPrice, floorPrice: item.FloorPrice,
            watchCount: ended.WatchCount, hitCount: ended.HitCount,
            compCount: item.SoldCompCount + item.TerapeakCompCount,
            marketComparable: item.MarketComparable);

        item.RelistPrice = suggestion.Price;
        item.FloorLimited = suggestion.FloorLimited;
        item.SamePrice = suggestion.SamePrice;
        item.PriceReason = suggestion.Reason;
        if (!string.IsNullOrEmpty(suggestion.Signal)) item.Signals.Add(suggestion.Signal);

        if (suggestion.Price is decimal relist)
        {
            item.RelistChangePercent = ended.Price > 0m
                ? Math.Round((relist - ended.Price) / ended.Price * 100m, 1) : null;
            item.NetProfitAtRelist = At(relist)?.NetProfitPerUnit;
            if (cost is null)
                item.Signals.Add("No cost recorded for this one, so the relist price has not been checked against your break-even.");
        }

        var (verdict, note) = Judge(item, suggestion.Reason);
        item.Verdict = verdict;
        item.VerdictNote = note;
        return item;
    }

    /// <summary>
    /// Attaches the losing bidders on an ended auction and re-judges the row.
    /// </summary>
    /// <remarks>
    /// Bidders arrive from a separate eBay call made after the row is priced, and they can change
    /// the headline entirely — a listing that was "relist it $4 cheaper" becomes "three people bid
    /// on this and lost". Re-judging is cheaper and safer than duplicating the verdict ladder, and
    /// the price decision itself is untouched: only the label and the note move.
    /// </remarks>
    public static void ApplyBidders(RelistCandidate item, IEnumerable<SecondChanceBidder> bidders, string note = "")
    {
        item.Bidders = [.. bidders];
        item.BiddersChecked = true;
        item.BidderNote = note;

        var blocked = item.Bidders.Count(b => b.Status == "below_floor");
        if (blocked > 0 && item.SendableBidders == 0)
            item.Signals.Add($"{blocked} losing bid{(blocked == 1 ? " was" : "s were")} under what you need to clear costs, so there is no Second Chance Offer to make here.");

        var (verdict, verdictNote) = Judge(item, item.PriceReason);
        item.Verdict = verdict;
        item.VerdictNote = verdictNote;
    }

    /// <summary>The reason a relist price came out where it did — drives the verdict and the copy.</summary>
    public sealed record PriceSuggestion(
        decimal? Price, bool FloorLimited, bool SamePrice, string Reason, string Signal);

    /// <summary>
    /// What to put this back up at, given the price that already failed and everything the listing
    /// learned while it was live.
    /// </summary>
    /// <remarks>
    /// The important difference from <see cref="InventoryHealthAnalyzer.SuggestPrice"/>: there, "no
    /// change" means do nothing, because the listing is already live. Here the listing is <em>down</em>,
    /// so "no change" still means relist — putting it back up is itself the action, and it buys a
    /// fresh run in eBay search whatever the number on it says. So this never returns null for a
    /// listing that could go back up; it returns the old price and says the price was not the problem.
    /// </remarks>
    public static PriceSuggestion SuggestRelistPrice(
        decimal endPrice, decimal? market, decimal? floorPrice,
        int? watchCount, int? hitCount, int compCount, bool marketComparable)
    {
        if (endPrice <= 0m) return new PriceSuggestion(null, false, false, "no_price", "");

        var (target, reason, signal) = ProposeTarget(endPrice, market, watchCount, hitCount, compCount, marketComparable);

        // Never deeper than one step. Reached over successive relists, with the seller seeing each.
        var deepestAllowed = endPrice * (1m - MaxSingleCutPercent / 100m);
        if (target < deepestAllowed)
        {
            target = deepestAllowed;
            signal = $"Capped at a {MaxSingleCutPercent:0}% cut for one relist — if it doesn't move this time, the next scan can go further.";
        }

        // The floor binds last, because it is the only one of these that is a hard fact about the
        // seller's own money rather than a reading of the market.
        var floorLimited = false;
        if (floorPrice is decimal floor && floor > 0m && target < floor)
        {
            // A floor above the price it already failed at means the listing was under water the
            // whole time it was up. That is worth saying out loud rather than quietly relisting.
            signal = floor > endPrice
                ? $"It was listed at ${endPrice:0.00}, under the ${floor:0.00} you need to clear costs — this puts it back up at the floor instead of repeating the loss."
                : $"Held at your ${floor:0.00} floor — that is as low as this one goes without selling at a loss.";
            target = floor;
            floorLimited = true;
            if (reason is "visibility" or "no_evidence" or "under_market") reason = "floor";
        }

        var final = InventoryHealthAnalyzer.Charm(target, floorPrice);
        var change = Math.Abs(final - endPrice);

        // A change too small to change a buyer's mind is not a change. The relist still happens —
        // it just goes back up at the price it had.
        if (change < MinChangeDollars || change / endPrice * 100m < MinChangePercent)
            return new PriceSuggestion(
                Math.Round(endPrice, 2), floorLimited, true,
                reason is "above_market" or "crowd" or "interested" or "seen" ? "no_change" : reason,
                signal);

        return new PriceSuggestion(Math.Round(final, 2), floorLimited, false, reason, signal);
    }

    // The ladder itself, before the caps and the floor.
    private static (decimal Target, string Reason, string Signal) ProposeTarget(
        decimal endPrice, decimal? market, int? watchCount, int? hitCount, int compCount, bool marketComparable)
    {
        var usableMarket = marketComparable && market is > 0m && compCount >= MinCompsForChange ? market : null;

        if (usableMarket is decimal m)
        {
            var gap = (endPrice - m) / m * 100m;

            if (gap >= OverpricedGapPercent)
                return (m, "above_market",
                    $"Listed {gap:0}% above what these actually sell for — that is the reason it didn't, and it goes back up at the ${m:0.00} going rate.");

            // Under market and still unsold: the listing's own record contradicts the comps for
            // this exact item. Raising it would be acting on the one reading the evidence denies.
            if (gap <= UnderpricedGapPercent)
                return (endPrice, "under_market",
                    $"It was already {Math.Abs(gap):0}% under the ${m:0.00} market and still didn't sell — that points at the photos, the title or the condition, not the price. Relisted unchanged.");
        }

        // At or near a fair price and it still didn't sell. What happened while it was up decides
        // how hard to push, and whether the price was ever the problem at all.
        var watchers = watchCount;
        var views = hitCount;

        if (watchers is int w && w >= CrowdWatcherCount)
            return (endPrice * (1m - CrowdCutPercent / 100m), "crowd",
                $"{w} people were watching when it ended — that is a queue, not indifference. A {CrowdCutPercent:0}% step is usually all it takes.");

        if (watchers is int few && few > 0)
            return (endPrice * (1m - InterestedCutPercent / 100m), "interested",
                $"{few} watcher{(few == 1 ? "" : "s")} and no sale — interested, but not at this price.");

        if (views is int seen && seen >= ViewsMeanSeen)
            return (endPrice * (1m - SeenNotSavedCutPercent / 100m), "seen",
                $"{seen} people opened the page and not one of them saved it — they saw the price and left.");

        // Nobody watched it, and it was barely seen. A markdown here is paying for a problem the
        // data says is not price. The relist is still worth doing on its own: it is a fresh run in
        // eBay search, which is exactly what a listing nobody found needs.
        if (watchers is 0 && views is int quiet && quiet < ViewsMeanSeen)
            return (endPrice, "visibility",
                $"No watchers and only {quiet} view{(quiet == 1 ? "" : "s")} — almost nobody found it, so the price was never the blocker. Relisting gives it a fresh run in search; fixing the title and the first photo is what actually changes this one.");

        return (endPrice, "no_evidence",
            "eBay didn't report watcher or view counts for this one, so there is no evidence the price was the blocker. It goes back up unchanged.");
    }

    /// <summary>
    /// One underbidder, and the Second Chance Offer worth sending them.
    /// </summary>
    /// <remarks>
    /// eBay will not carry a Second Chance Offer above what the recipient already bid, so the
    /// bidder's own maximum is the price — there is nothing to size and no ladder to walk. The only
    /// question is whether that number clears the seller's floor.
    /// </remarks>
    public static SecondChanceBidder BuildBidder(
        string? userId, decimal? maxBid, int quantity,
        decimal? floorPrice, decimal? breakEvenPrice, FeeProfile fees)
    {
        var bidder = new SecondChanceBidder
        {
            UserId = (userId ?? "").Trim(),
            MaxBid = maxBid,
            Quantity = Math.Max(1, quantity),
        };

        // eBay masks bidder IDs on some responses for privacy. A masked ID cannot receive an offer,
        // and pretending otherwise would produce a send that fails at the API.
        if (bidder.UserId.Length == 0 || bidder.UserId.Contains('*'))
        {
            bidder.Status = "anonymous";
            bidder.Note = "eBay didn't disclose this bidder's user ID, so no offer can be addressed to them.";
            return bidder;
        }

        if (maxBid is not decimal bid || bid <= 0m)
        {
            bidder.Status = "no_bid";
            bidder.Note = "eBay didn't disclose what this bidder bid, so there is no price to offer them.";
            return bidder;
        }

        bidder.OfferPrice = Math.Round(bid, 2);
        bidder.NetProfitAtOffer = NetProceedsCalculator.NetProfitAt(bidder.OfferPrice, breakEvenPrice, fees);

        if (floorPrice is decimal floor && bidder.OfferPrice < floor)
        {
            bidder.Status = "below_floor";
            bidder.Note = $"They bid ${bidder.OfferPrice:0.00}; you need ${floor:0.00} to clear costs. eBay won't carry an offer above their bid, so there is no price that works for both of you.";
            return bidder;
        }

        bidder.Note = $"Bid ${bidder.OfferPrice:0.00} and lost. Offer it to them at exactly that.";
        return bidder;
    }

    /// <summary>
    /// The single headline label for one ended listing, ordered by what costs the seller most.
    /// </summary>
    public static (string Verdict, string Note) Judge(RelistCandidate item, string reason)
    {
        if (item.EndPrice <= 0m)
            return ("no_price", "eBay reported no price for this ended listing, so there is nothing to put back up.");

        if (item.AlreadyRelisted)
            return ("already_relisted",
                $"eBay already has a relist of this one{(string.IsNullOrEmpty(item.RelistedItemId) ? "" : $" (item {item.RelistedItemId})")} — relisting again would put a duplicate on the site.");

        if (item.EndedByUser)
            return ("ended_by_seller",
                "You ended this one early rather than letting it run out, so it isn't a sale that got away. It can still be relisted from here if you want it back up.");

        // A bidder who lost is worth more than any relist, so it leads whenever there is one.
        if (item.SendableBidders > 0)
        {
            var n = item.SendableBidders;
            return ("second_chance",
                $"{n} bidder{(n == 1 ? "" : "s")} lost this auction and {(n == 1 ? "is" : "are")} still reachable — "
                + $"{Money(item.SecondChanceValue)} of offers you can send at the price they already bid.");
        }

        // No profitable price exists. Saying so is the whole answer; a relist here is a relisted loss.
        if (item.FloorPrice is decimal floor && item.MarketComparable
            && item.MarketPrice is decimal market && market > 0m
            && item.SoldCompCount + item.TerapeakCompCount >= MinCompsForChange
            && floor > market)
            return ("underwater",
                $"You need {Money(floor)} to clear costs and these sell for about {Money(market)}. Relisting it puts the same loss back on the site — holding, bundling or a deliberate write-off are the real options.");

        if (item.BidCount > 0 && item.SendableBidders == 0 && item.BiddersChecked)
            return ("relist_cheaper", item.RelistPrice is decimal p && !item.SamePrice
                ? $"{item.BidCount} bid{(item.BidCount == 1 ? "" : "s")} but no reachable bidder. Relist at {Money(p)} — the bidding says the demand is real, it just stopped short."
                : $"{item.BidCount} bid{(item.BidCount == 1 ? "" : "s")} and no reachable bidder. Worth another run at the same price.");

        if (item.SamePrice)
            return ("relist_as_is",
                reason switch
                {
                    "visibility" => "Almost nobody saw this one. Put it back up for a fresh run in search — and rewrite the title while you do.",
                    "under_market" => "Already under market and still unsold. Back up at the same price; the fix here is the photos and the title.",
                    "floor" => $"Back up at {Money(item.RelistPrice ?? item.EndPrice)} — your floor, not the market's.",
                    "no_change" => "The market says this price is about right. Worth another run at it.",
                    _ => "No evidence the price was the blocker, so it goes back up unchanged for a second run.",
                });

        if (item.RelistPrice is decimal newPrice)
            return ("relist_cheaper",
                $"Didn't sell at {Money(item.EndPrice)}. Back up at {Money(newPrice)}"
                + (item.NetProfitAtRelist is decimal net
                    ? $" — {Money(net)} in your pocket if it sells this time."
                    : ". Record what you paid to see the profit on it."));

        return ("no_data", "Nothing matched this title in sold history, and eBay reported nothing about how the listing performed.");
    }

    /// <summary>The board totals — the size of the pile, and how much of it is actually gettable.</summary>
    public static RelistSummary Summarize(IReadOnlyList<RelistCandidate> items)
    {
        // "Asked and unsold" counts what genuinely got away: a listing the seller pulled on purpose
        // was not a failed sale, and a listing eBay has already relisted is counted on its relist.
        var lost = items.Where(i => !i.EndedByUser && !i.AlreadyRelisted).ToList();
        var ready = items.Where(i => i.CanRelist).ToList();
        var withBidders = items.Where(i => i.SendableBidders > 0).ToList();

        return new RelistSummary
        {
            Analyzed = items.Count,
            AskedAndUnsold = Math.Round(lost.Sum(i => i.EndPrice * i.Quantity), 2),
            // The only figure on this board that is money already spent rather than money not yet
            // made. Counted per unsold unit, and only where the seller actually recorded a cost.
            CashSunk = Math.Round(lost.Where(i => i.HasCostBasis).Sum(i => i.CostBasis!.Value * i.Quantity), 2),
            WithCostBasis = items.Count(i => i.HasCostBasis),

            ReadyToRelist = ready.Count,
            RelistValue = Math.Round(ready.Sum(i => (i.RelistPrice ?? 0m) * i.Quantity), 2),
            NetIfAllSell = Math.Round(
                ready.Where(i => i.NetProfitAtRelist.HasValue).Sum(i => i.NetProfitAtRelist!.Value * i.Quantity), 2),
            PriceGivenUp = Math.Round(
                ready.Where(i => i.RelistPrice < i.EndPrice).Sum(i => (i.EndPrice - i.RelistPrice!.Value) * i.Quantity), 2),

            SecondChanceListings = withBidders.Count,
            SecondChanceBidders = items.Sum(i => i.SendableBidders),
            SecondChanceValue = Math.Round(items.Sum(i => i.SecondChanceValue), 2),
            SecondChanceNet = Math.Round(
                items.SelectMany(i => i.Bidders).Where(b => b.CanSend && b.NetProfitAtOffer.HasValue)
                     .Sum(b => b.NetProfitAtOffer!.Value), 2),

            AlreadyRelisted = items.Count(i => i.AlreadyRelisted),
            Underwater = items.Count(i => i.Verdict == "underwater"),
            // Listings whose own record says the price was never the blocker. Separated out because
            // relisting these unchanged is right, and expecting a different outcome from it is not.
            NeedsWork = items.Count(i => i.Verdict == "relist_as_is"),
            NoData = items.Count(i => i.MarketPrice is null),
        };
    }

    /// <summary>
    /// Fastest money first. A lost bidder beats any relist, then the relists worth the most,
    /// then the ones that ended most recently — a listing that ended yesterday is still warm in
    /// search and its buyers are still looking.
    /// </summary>
    public static List<RelistCandidate> Rank(IEnumerable<RelistCandidate> items) =>
        items.OrderByDescending(i => i.SendableBidders > 0)
             .ThenByDescending(i => i.SecondChanceValue)
             .ThenByDescending(i => i.CanRelist)
             .ThenByDescending(i => (i.NetProfitAtRelist ?? 0m) * i.Quantity)
             .ThenByDescending(i => (i.RelistPrice ?? i.EndPrice) * i.Quantity)
             .ThenBy(i => i.DaysSinceEnded ?? int.MaxValue)
             .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
             .ToList();

    /// <summary>
    /// Which ended auctions are worth spending a bidder lookup on. One eBay call each, so the
    /// budget goes to the auctions where the losing bidders are worth the most.
    /// </summary>
    public static List<string> SelectBidderLookups(IEnumerable<EbayEndedListing> ended, int budget)
    {
        if (budget <= 0) return [];
        return ended
            .Where(e => e.IsAuction && e.BidCount > 0 && !e.EndedByUser
                        && string.IsNullOrWhiteSpace(e.RelistedItemId) && !string.IsNullOrWhiteSpace(e.ListingId))
            // The top bid is the best available estimate of what an offer to these bidders is
            // worth; the opening price stands in when eBay reported no bid amount.
            .OrderByDescending(e => e.HighBid ?? e.Price)
            .ThenByDescending(e => e.BidCount)
            .Take(budget)
            .Select(e => e.ListingId)
            .ToList();
    }

    private static string Money(decimal value) =>
        value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
}
