using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Works out what discount to offer the people already watching each listing — deep enough to
/// convert, never deep enough to sell below the seller's own profit floor.
/// </summary>
/// <remarks>
/// <para>
/// A markdown gives margin to everyone, including the buyer who would have paid full price. An
/// offer to watchers gives it only to people who already found the item and hesitated, only for a
/// couple of days, and only if they accept — the public price never moves. That difference is why
/// this ladder is deliberately NOT the one in <see cref="InventoryHealthAnalyzer"/>: that one holds
/// the price when watchers are interested, because a public cut there would be paying for demand
/// the listing already had. Here the watchers are precisely who the discount is aimed at, so their
/// number tunes the size of the offer instead of cancelling it.
/// </para>
/// <para>
/// Everything is pure and the money is borrowed, not re-derived: break-even comes from the same
/// <see cref="ProfitCalculator"/>/<see cref="FeeProfile"/> pair every other screen costs an item
/// with, and market and quick-sale prices come from the inventory-health scan, which got them from
/// <see cref="MarketPriceEstimator"/>. A break-even is a break-even whichever screen asks.
/// </para>
/// </remarks>
public static class WatcherOfferAdvisor
{
    // ── eBay's rules ─────────────────────────────────────────────────────────────────────────
    // eBay will not carry an offer worth less than 5% off the listed price.
    public const int EbayMinDiscountPercent = 5;

    // ── Ours ─────────────────────────────────────────────────────────────────────────────────
    // No single offer goes deeper than this. Past a quarter off, the right move is a repricing
    // decision the seller looks at, not a default the app picked — the same posture the repricer
    // takes with its cap on one revision.
    public const int MaxDiscountPercent = 25;
    // Without a cost basis there is no floor, so there is no way to know a 20% offer isn't a 20%
    // loss. The offer still gets sent — it just stays shallow, and says why.
    public const int NoCostBasisCapPercent = 10;
    // A listing already priced under the market doesn't need a deep offer to look like a deal;
    // the watchers are hesitating over something other than price.
    public const int AlreadyUnderMarketCapPercent = 8;
    // Below this the listing is already cheaper than comps.
    private const decimal UnderMarketFactor = 0.95m;

    // ── The ladder ───────────────────────────────────────────────────────────────────────────
    // Every step is additive on top of eBay's 5% minimum, in percentage points.
    private const int AgedThirtyDaysBonus = 3;
    private const int AgedNinetyDaysBonus = 5;
    private const int AgedSixMonthsBonus = 8;
    // A crowd is evidence the item is wanted, so it takes less to close. A single watcher is a
    // maybe, and a maybe needs a sharper hook.
    private const int CrowdedDiscount = 2;      // 10+ watchers
    private const int InterestedDiscount = 1;   // 5-9 watchers
    private const int ThinInterestBonus = 2;    // 1-2 watchers

    public const int StrongWatcherCount = 5;
    public const int CrowdWatcherCount = 10;

    /// <summary>The offer decision for one listing.</summary>
    public sealed record Suggestion(
        int? DiscountPercent, decimal? OfferPrice, bool FloorLimited,
        string Verdict, string Note, IReadOnlyList<string> Signals);

    /// <summary>
    /// The lowest price that still leaves <paramref name="minNetProfit"/> in the seller's pocket
    /// after every fee, given the break-even.
    /// </summary>
    /// <remarks>
    /// Delegates to <see cref="NetProceedsCalculator"/>, which is where this math now lives so the
    /// floor quoted here and the floor the listing editor shows are the same calculation. It used
    /// to have its own fee fraction, which — like ProfitCalculator's break-even — left payment
    /// processing out, so a seller who billed processing separately negotiated against a floor
    /// that was slightly too low.
    /// </remarks>
    public static decimal? ProfitFloorPrice(decimal? breakEvenPrice, decimal minNetProfit, FeeProfile fees) =>
        NetProceedsCalculator.ProfitFloorPrice(breakEvenPrice, minNetProfit, fees);

    /// <summary>Take-home on a sale at <paramref name="price"/>, or null with no break-even.</summary>
    public static decimal? NetProfitAt(decimal price, decimal? breakEvenPrice, FeeProfile fees) =>
        NetProceedsCalculator.NetProfitAt(price, breakEvenPrice, fees);

    /// <summary>
    /// The binding floor for an offer, and what set it. The quick-sale price counts as a floor in
    /// its own right: it is the price the comps say moves this item without any offer at all, so
    /// discounting under it is paying for a sale the listing was going to get anyway.
    /// </summary>
    public static (decimal? Price, string Basis) Floor(
        decimal? breakEvenPrice, decimal minNetProfit, decimal? quickSalePrice,
        bool marketComparable, FeeProfile fees)
    {
        var profitFloor = ProfitFloorPrice(breakEvenPrice, minNetProfit, fees);
        var quickFloor = marketComparable && quickSalePrice is > 0m ? quickSalePrice : null;

        return (profitFloor, quickFloor) switch
        {
            (null, null) => (null, "none"),
            (decimal p, null) => (p, minNetProfit > 0m ? "profit" : "break_even"),
            (null, decimal q) => (q, "quick_sale"),
            (decimal p, decimal q) => q > p ? (q, "quick_sale") : (p, minNetProfit > 0m ? "profit" : "break_even"),
        };
    }

    /// <summary>
    /// The offer to send on one listing, or the reason there isn't one.
    /// </summary>
    /// <remarks>
    /// Order matters. The ladder proposes a depth from age, audience size and the gap to market;
    /// the "already cheap" and "no cost basis" caps pull it back; the floor bounds it last, because
    /// the floor is the only one of the three that is a hard fact about the seller's money.
    /// </remarks>
    public static Suggestion Suggest(
        decimal listPrice, int watchCount, int? daysListed, decimal? marketPrice, decimal? quickSalePrice,
        decimal? floorPrice, string floorBasis, bool hasCostBasis, bool? eligible, string eligibilityNote,
        bool marketComparable = true)
    {
        var signals = new List<string>();

        if (listPrice <= 0m)
            return new Suggestion(null, null, false, "not_ready",
                "eBay reported no price for this listing, so there is nothing to discount.", signals);

        // An offer goes to the people watching. With nobody watching there is no one to send it to,
        // and eBay will not accept the call either.
        if (watchCount <= 0)
            return new Suggestion(null, null, false, "no_watchers",
                "Nobody is watching this one yet. Offers only reach watchers — this listing needs views first.", signals);

        if (eligible == false)
            return new Suggestion(null, null, false, "not_eligible",
                string.IsNullOrWhiteSpace(eligibilityNote)
                    ? "eBay doesn't currently list this one as eligible for an offer to watchers."
                    : eligibilityNote, signals);

        if (eligible is null)
            signals.Add("eBay's eligibility list couldn't be read this scan, so this offer may still be refused when sent.");

        // ── The ladder ───────────────────────────────────────────────────────────────────────
        var discount = EbayMinDiscountPercent;

        discount += daysListed switch
        {
            null => 0,
            < InventoryHealthAnalyzer.FreshDays => 0,
            < InventoryHealthAnalyzer.StaleDays => AgedThirtyDaysBonus,
            < InventoryHealthAnalyzer.DeadDays => AgedNinetyDaysBonus,
            _ => AgedSixMonthsBonus,
        };

        discount += watchCount switch
        {
            >= CrowdWatcherCount => -CrowdedDiscount,
            >= StrongWatcherCount => -InterestedDiscount,
            <= 2 => ThinInterestBonus,
            _ => 0,
        };

        // Priced above the market the watchers can see for themselves: the offer has to at least
        // reach the going rate, or it is a discount to a price that was never competitive.
        var usableMarket = marketComparable && marketPrice is > 0m ? marketPrice : null;
        if (usableMarket is decimal market)
        {
            if (listPrice > market)
            {
                var toMarket = (int)Math.Ceiling((listPrice - market) / listPrice * 100m);
                if (toMarket > discount)
                {
                    discount = toMarket;
                    signals.Add($"Listed above the ${market:0.00} going rate — the offer is sized to close that gap.");
                }
            }
            else if (listPrice <= market * UnderMarketFactor && discount > AlreadyUnderMarketCapPercent)
            {
                discount = AlreadyUnderMarketCapPercent;
                signals.Add("Already priced under market — a nudge is enough; there is no need to pay for a deeper one.");
            }
        }

        if (!hasCostBasis && discount > NoCostBasisCapPercent)
        {
            discount = NoCostBasisCapPercent;
            signals.Add($"No cost recorded, so this offer is capped at {NoCostBasisCapPercent}% — record what you paid in Inventory Health to go deeper safely.");
        }

        discount = Math.Clamp(discount, EbayMinDiscountPercent, MaxDiscountPercent);

        // ── The floor ────────────────────────────────────────────────────────────────────────
        var floorLimited = false;
        if (floorPrice is decimal floor && floor > 0m)
        {
            var offerAtLadder = OfferPriceFor(listPrice, discount);
            if (offerAtLadder < floor)
            {
                // The deepest whole-percent offer that still clears the floor.
                var deepest = (int)Math.Floor((listPrice - floor) / listPrice * 100m);
                if (deepest < EbayMinDiscountPercent)
                    return new Suggestion(null, null, true, "no_room",
                        $"Even eBay's smallest 5% offer (${OfferPriceFor(listPrice, EbayMinDiscountPercent):0.00}) lands under your ${floor:0.00} {FloorWords(floorBasis)}.",
                        signals);

                discount = deepest;
                floorLimited = true;
                signals.Add($"Held at {discount}% by your ${floor:0.00} {FloorWords(floorBasis)} — this is as far as this one goes.");
            }
        }

        var offerPrice = OfferPriceFor(listPrice, discount);
        // Said about this row and true of this row. An earlier draft called every crowded listing
        // "the warmest audience on your board", which was false on all but one of them.
        var note = $"{watchCount} watcher{(watchCount == 1 ? "" : "s")} — offer {discount}% off at ${offerPrice:0.00}."
                 + (watchCount >= CrowdWatcherCount ? " A crowd this size takes less of a discount to close, not more." : "");

        return new Suggestion(discount, offerPrice, floorLimited, "ready", note, signals);
    }

    /// <summary>The price a watcher sees, rounded the way eBay applies the percentage.</summary>
    public static decimal OfferPriceFor(decimal listPrice, int discountPercent) =>
        Math.Round(listPrice * (100m - discountPercent) / 100m, 2, MidpointRounding.AwayFromZero);

    private static string FloorWords(string basis) => basis switch
    {
        "profit" => "minimum-profit floor",
        "break_even" => "break-even",
        "quick_sale" => "quick-sale price (what the comps say it moves at anyway)",
        _ => "floor",
    };

    /// <summary>
    /// Turns one judged inventory row into an offer row. The market read, the break-even and the
    /// cost basis all arrive already computed by the inventory-health scan — this adds only the
    /// offer decision on top of them.
    /// </summary>
    public static WatcherOfferItem Build(
        InventoryHealthItem health, bool? eligible, string eligibilityNote,
        decimal minNetProfit, FeeProfile fees)
    {
        var (floorPrice, floorBasis) = Floor(
            health.BreakEvenPrice, minNetProfit, health.QuickSalePrice, health.MarketComparable, fees);

        var suggestion = Suggest(
            listPrice: health.ListPrice, watchCount: health.WatchCount, daysListed: health.DaysListed,
            marketPrice: health.MarketPrice, quickSalePrice: health.QuickSalePrice,
            floorPrice: floorPrice, floorBasis: floorBasis, hasCostBasis: health.HasCostBasis,
            eligible: eligible, eligibilityNote: eligibilityNote, marketComparable: health.MarketComparable);

        var item = new WatcherOfferItem
        {
            ListingId = health.ListingId,
            Sku = health.Sku,
            Title = health.Title,
            Url = health.Url,
            ImageUrl = health.ImageUrl,
            Condition = health.Condition,
            ListPrice = health.ListPrice,
            Quantity = health.Quantity,
            WatchCount = health.WatchCount,
            DaysListed = health.DaysListed,
            MarketPrice = health.MarketPrice,
            QuickSalePrice = health.QuickSalePrice,
            SoldCompCount = health.SoldCompCount,
            TerapeakCompCount = health.TerapeakCompCount,
            PricedAs = health.PricedAs,
            MarketComparable = health.MarketComparable,
            CostBasis = health.CostBasis,
            BreakEvenPrice = health.BreakEvenPrice,
            FloorPrice = floorPrice,
            FloorBasis = floorBasis,
            Eligible = eligible,
            EligibilityNote = eligibilityNote,
            NetProfitAtListPrice = health.NetProfitAtListPrice,
            DiscountPercent = suggestion.DiscountPercent,
            OfferPrice = suggestion.OfferPrice,
            FloorLimited = suggestion.FloorLimited,
            Verdict = suggestion.Verdict,
            VerdictNote = suggestion.Note,
            Signals = [.. suggestion.Signals],
        };

        if (suggestion.OfferPrice is decimal offer)
        {
            item.NetProfitAtOffer = NetProfitAt(offer, health.BreakEvenPrice, fees);
            item.MarginGivenUp = Math.Round(health.ListPrice - offer, 2);
        }

        if (health.DaysListed >= InventoryHealthAnalyzer.StaleDays && health.WatchCount > 0)
            item.Signals.Add($"{health.DaysListed} days live with {health.WatchCount} watcher{(health.WatchCount == 1 ? "" : "s")} still on it — interested, but not at this price.");

        return item;
    }

    /// <summary>The board totals — how much warm demand is sitting there unconverted.</summary>
    public static WatcherOfferSummary Summarize(IReadOnlyList<WatcherOfferItem> items)
    {
        var ready = items.Where(i => i.CanSend).ToList();

        return new WatcherOfferSummary
        {
            ListingsAnalyzed = items.Count,
            ListingsWithWatchers = items.Count(i => i.WatchCount > 0),
            TotalWatchers = items.Sum(i => i.WatchCount),
            ReadyToSend = ready.Count,
            WatchersReachable = ready.Sum(i => i.WatchCount),
            AverageDiscountPercent = ready.Count == 0 ? 0m
                : Math.Round((decimal)ready.Average(i => i.DiscountPercent!.Value), 1),
            // One unit each, not one per watcher: a listing with 30 watchers does not sell 30 units
            // because an offer went out, and a headline that says so would be a lie with a dollar
            // sign in front of it.
            RevenueIfOneEachAccepts = Math.Round(ready.Sum(i => i.OfferPrice ?? 0m), 2),
            NetIfOneEachAccepts = Math.Round(ready.Where(i => i.NetProfitAtOffer.HasValue).Sum(i => i.NetProfitAtOffer!.Value), 2),
            MarginGivenUpIfAllAccept = Math.Round(ready.Sum(i => i.MarginGivenUp ?? 0m), 2),
            BlockedByFloor = items.Count(i => i.Verdict == "no_room"),
            NotEligible = items.Count(i => i.Verdict == "not_eligible"),
            WithoutCostBasis = items.Count(i => !i.HasCostBasis),
        };
    }

    /// <summary>
    /// Warmest, biggest money first. A ready offer with twelve watchers on a $400 item is the one
    /// worth sending before breakfast; a ready offer with one watcher on a $9 item is not.
    /// </summary>
    public static List<WatcherOfferItem> Rank(IEnumerable<WatcherOfferItem> items) =>
        items.OrderByDescending(i => i.CanSend)
             .ThenByDescending(i => i.WatchCount * (i.NetProfitAtOffer ?? i.OfferPrice ?? i.ListPrice))
             .ThenByDescending(i => i.WatchCount)
             .ThenByDescending(i => i.ListPrice)
             .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
             .ToList();

    // eBay shows this message to every watcher who gets the offer. Kept short, specific and free
    // of pressure tactics — it is the seller's name on it.
    public const string DefaultMessage =
        "Thanks for watching! Here's a private discount on this item — it's good for the next 48 hours.";

    public const int MaxMessageLength = 250;

    /// <summary>Trims a seller's message to what eBay will carry.</summary>
    public static string CleanMessage(string? message)
    {
        var text = (message ?? "").Trim();
        if (text.Length == 0) return "";
        return text.Length <= MaxMessageLength ? text : text[..MaxMessageLength].TrimEnd();
    }
}
