using System.Globalization;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Decides whether a listing is actually free, what kind of free it is, and how long there is to
/// act on it. Pure — no HTTP and no clock beyond the one passed in — so every rule below is a unit
/// test rather than a guess about a live site.
/// </summary>
/// <remarks>
/// <para>
/// This class is almost entirely refusal, and deliberately so. On the boards it reads, most things
/// described as free are not an item you can sell: they are free shipping, a free gift with a
/// purchase, a free trial, a pile of firewood, a sweepstakes entry, or a broken appliance somebody
/// wants hauled away. A $0 cost basis makes ROI unbounded, which means <b>anything this class lets
/// through lands at the very top of the profit ranking</b>. There is no board where a wrong answer
/// is more visible or more expensive, so every ambiguity resolves toward dropping the row.
/// </para>
/// <para>
/// The refusal lists it reuses rather than rewrites are <see cref="LiquidationSelectors"/>'
/// — a broken free fridge and a broken auction lot are the same refusal for the same reason, and
/// two lists would eventually disagree about it.
/// </para>
/// </remarks>
public static class FreebieClassifier
{
    /// <summary>
    /// One listing, classified — or null when it is not a free, sellable, single object.
    /// Null is the common case and the correct one.
    /// </summary>
    /// <param name="title">The listing's own title.</param>
    /// <param name="prose">Body text, where the source has one. Read for rebate and expiry wording.</param>
    /// <param name="price">The parsed price, if any. Null means the source printed none.</param>
    /// <param name="fromFreeBoard">
    /// True when the listing came from a board that only carries free items (Craigslist's free-stuff
    /// category). Those posts are free by construction and rarely print the word at all — "Oak wall
    /// unit" on the free board is a free oak wall unit — so requiring a free signal there would
    /// throw away most of the best supply on this feature.
    /// </param>
    public static FreebieDetails? Classify(
        string? title, string? prose, decimal? price, bool fromFreeBoard, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;

        var text = $"{title} {FirstLine(prose)}";

        if (IsNotSellable(text)) return null;

        // A deadline that has already passed is not a deal, and a board that ranks one is telling
        // the seller to go and buy something that no longer exists. Two of the live sample's own
        // titles had expiry dates behind them the day they were pulled.
        var (expiresUtc, expiresText) = ReadExpiry(text, nowUtc);
        if (expiresUtc is DateTime expiry && expiry < nowUtc.Date) return null;

        var details = ReadKind(text, price, fromFreeBoard);
        if (details is null) return null;

        details.ExpiresUtc = expiresUtc;
        details.ExpiresText = expiresText;
        details.IsBulky = FreebieSelectors.Bulky.IsMatch(title);
        details.IsPickup = fromFreeBoard;

        // Collecting it in person has no shipping cost to be unknown about; online, an unstated
        // delivery charge on a $0 item is not a detail, it IS the cost basis.
        details.DeliveryCostKnown = fromFreeBoard || FreebieSelectors.FreeDelivery.IsMatch(text);

        details.Urgency = ReadUrgency(text, expiresUtc, fromFreeBoard, nowUtc);
        details.KindLabel = LabelFor(details);
        return details;
    }

    // ── Refusal ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// True when the listing is free but is not a single sellable object. Ordered cheapest test
    /// first; every one of these was chosen against live data rather than imagined.
    /// </summary>
    public static bool IsNotSellable(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;

        // A pile, a curb alert, a garage being emptied. Genuinely worth driving to and impossible to
        // price: there is no one item to look a comp up for, so the row would be a confident number
        // about nothing.
        if (FreebieSelectors.PileNotAProduct.IsMatch(text)) return true;

        // Replicas. eBay prohibits them outright, and priced against genuine sold comps a free one
        // reads as the best flip on the board.
        if (FreebieSelectors.Counterfeit.IsMatch(text)) return true;

        // Free because it is broken. The sold history it would be priced against is history for
        // items that work. Both lists apply: the auction board's narrow one, plus the wider one a
        // free board earns — damage is the commonest reason a thing is being given away, and people
        // say so in the title.
        if (LiquidationSelectors.ForPartsOnly.IsMatch(text)) return true;
        if (FreebieSelectors.FreeBecauseBroken.IsMatch(text)) return true;

        // No single identifiable product: "assorted", "misc", "grab bag", "and more".
        if (LiquidationSelectors.AssortedContents.IsMatch(text)) return true;

        // Free time, access or credit rather than a free thing.
        if (FreebieSelectors.FreeButNotAnObject.IsMatch(text)) return true;

        if (FreebieSelectors.NoResaleValuePhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Firearms, alcohol, livestock, vehicles, gift cards — things eBay does not allow at all.
        // Shared with the liquidation board rather than restated, so the two can never disagree.
        return LiquidationSelectors.NotFlippablePhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    // ── Which kind of free ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Free, free-after-coupon, free-after-rebate or near-free — or null when the listing is none of
    /// them, which for a deal feed is the overwhelming majority of entries.
    /// </summary>
    /// <remarks>
    /// The rebate branch is decided by <b>arithmetic, not wording</b>: "free after rebate" in a title
    /// is marketing, but "$49.99 with a $49.99 rebate" is a fact. A $59.99 case with a $10 rebate
    /// says "after rebate" in exactly the same words and is not free by $49.99, and only the
    /// subtraction can tell the two apart.
    /// </remarks>
    public static FreebieDetails? ReadKind(string text, decimal? price, bool fromFreeBoard)
    {
        // "Free shipping" and "free gift w/ purchase" describe something other than the item, so
        // they come out of the text entirely before anything asks whether the item is free. Removing
        // rather than refusing, because "Free 65in TV + free shipping" is a genuine freebie whose
        // title happens to contain both.
        var stripped = FreebieSelectors.FreeWithPurchase.Replace(
            FreebieSelectors.FreeDelivery.Replace(text, " "), " ");

        var saysFree = FreebieSelectors.FreeItemSignal.IsMatch(stripped);

        // The free board's own posts are free whether or not they use the word.
        if (fromFreeBoard)
        {
            // A priced post that wandered onto a free board is not a freebie. Above the near-free
            // ceiling it is an ordinary classified, which the Deal Scanner already ranks.
            if (price is > FreebieSelectors.NearFreeCeiling) return null;

            return price is > 0m
                ? new FreebieDetails { Kind = FreebieKinds.NearFree, ListPrice = price.Value }
                : new FreebieDetails { Kind = FreebieKinds.Free };
        }

        // ── Rebate: real money leaves the wallet first ───────────────────────────────────────────
        if (FreebieSelectors.AfterRebate.IsMatch(text))
        {
            // Without a price there is nothing to subtract the rebate from, so there is no way to
            // know whether this ends at $0 or at $40. Refused rather than assumed.
            if (price is not > 0m) return null;

            var stated = ReadRebateAmount(text);
            // No stated amount, but the listing says the item ends up free: the rebate is the price.
            var rebate = stated ?? (saysFree ? price.Value : 0m);
            if (rebate <= 0m) return null;

            rebate = Math.Min(rebate, price.Value);
            if (price.Value - rebate > FreebieSelectors.NearFreeCeiling) return null;

            // Fronting $40 for six weeks is a freebie with a wait. Fronting $600 is a loan, and it
            // belongs on a board that ranks by capital at risk rather than on this one.
            if (price.Value > FreebieSelectors.MaxFrontedOutlay) return null;

            var via = FreebieSelectors.RebateVia.Match(text);
            return new FreebieDetails
            {
                Kind = FreebieKinds.FreeAfterRebate,
                ListPrice = price.Value,
                RebateAmount = rebate,
                RebateVia = via.Success ? via.Groups[1].Value : "",
            };
        }

        // ── Coupon: nothing is fronted, but the price only exists with the code ─────────────────
        if (saysFree && FreebieSelectors.AfterCoupon.IsMatch(text) && price is not > FreebieSelectors.NearFreeCeiling)
        {
            return new FreebieDetails
            {
                Kind = FreebieKinds.FreeAfterCoupon,
                ListPrice = price ?? 0m,
                RequiresCoupon = true,
            };
        }

        // ── Outright free, online ────────────────────────────────────────────────────────────────
        if (saysFree && price is not > 0m) return new FreebieDetails { Kind = FreebieKinds.Free };

        // ── Near free: a real price small enough that it is the same trade ──────────────────────
        if (price is > 0m and <= FreebieSelectors.NearFreeCeiling)
            return new FreebieDetails { Kind = FreebieKinds.NearFree, ListPrice = price.Value };

        return null;
    }

    /// <summary>The rebate's stated size, or null when the listing named no figure.</summary>
    public static decimal? ReadRebateAmount(string? text)
    {
        var match = FreebieSelectors.RebateAmount.Match(text ?? "");
        if (!match.Success) return null;

        // Two alternations, either of which may be the one that captured.
        var raw = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        return decimal.TryParse(raw.Replace(",", ""), NumberStyles.Any, CultureInfo.InvariantCulture, out var value)
            && value > 0m ? value : null;
    }

    // ── The clock ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stated deadline, read out of the listing's own words. Returns the date and the wording it
    /// came from, so the row can quote the seller's source rather than a reformatted guess.
    /// </summary>
    /// <remarks>
    /// A date with no year is resolved to whichever side of today it is nearer: "exp 1/5" read in
    /// December means next January, and "exp 12/25" read in January means last December — the second
    /// of which is what makes an already-dead offer detectable at all.
    /// </remarks>
    public static (DateTime? ExpiresUtc, string Text) ReadExpiry(string? text, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, "");

        var match = FreebieSelectors.ExpiryDate.Match(text);
        if (match.Success
            && int.TryParse(match.Groups[1].Value, out var month)
            && int.TryParse(match.Groups[2].Value, out var day)
            && month is >= 1 and <= 12 && day is >= 1 and <= 31)
        {
            var stated = match.Groups[3].Success && int.TryParse(match.Groups[3].Value, out var y)
                ? (y < 100 ? 2000 + y : y)
                : (int?)null;

            try
            {
                var date = new DateTime(stated ?? nowUtc.Year, month, day, 0, 0, 0, DateTimeKind.Utc);

                // Rolled forward only when the year was NOT stated: a bare "1/5" seen in December is
                // next year's, while an explicit "7/19/26" is exactly what it says even once past.
                if (stated is null && date < nowUtc.Date.AddDays(-RollForwardDays)) date = date.AddYears(1);

                return (date, match.Value.Trim());
            }
            catch (ArgumentOutOfRangeException)
            {
                // 2/30 and friends. A date that isn't one is no deadline at all.
            }
        }

        var today = FreebieSelectors.ExpiryToday.Match(text);
        return today.Success ? (null, today.Value.Trim()) : (null, "");
    }

    // How far back a year-less date is still read as this year's rather than next year's. Wide
    // enough that a coupon which expired a fortnight ago is still recognised as expired, narrow
    // enough that a genuine upcoming date is not dragged backwards.
    private const int RollForwardDays = 45;

    /// <summary>How much time is left, in the only three bands a seller acts differently on.</summary>
    public static string ReadUrgency(string? text, DateTime? expiresUtc, bool fromFreeBoard, DateTime nowUtc)
    {
        if (expiresUtc is DateTime expiry)
        {
            var days = (expiry.Date - nowUtc.Date).TotalDays;
            if (days <= 1) return FreebieUrgency.Today;
            if (days <= 7) return FreebieUrgency.ThisWeek;
        }

        if (!string.IsNullOrWhiteSpace(text) && FreebieSelectors.ExpiryToday.IsMatch(text))
            return FreebieUrgency.Today;

        // No clock, but no queue either. A free post has no deadline printed on it and is gone the
        // moment somebody drives over — which is a shorter deadline than most stated ones.
        return fromFreeBoard ? FreebieUrgency.FirstCome : FreebieUrgency.Unknown;
    }

    // ── Titles ───────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Strips the free-wording out of a title so the comp lookup sees the product.
    ///
    /// "COUCH - BIG AND COMFORTABLE AND FREE" has to reach <see cref="ProductNormalizer"/> as
    /// "COUCH - BIG AND COMFORTABLE", or "free" becomes part of the product identity and the search
    /// goes looking for sold listings of a free couch. Same job <see cref="DealFeedParser.CleanTitle"/>
    /// does for a price, and <see cref="CraigslistParser"/>'s does for a neighbourhood suffix.
    /// </summary>
    public static string CleanTitle(string? rawTitle)
    {
        var title = (rawTitle ?? "").Trim();
        if (title.Length == 0) return "";

        // Order matters: the qualified forms go first, so removing a bare "free" never strips the
        // "free" out of "free shipping" and leaves a dangling "shipping" in the product name.
        title = FreebieSelectors.FreeDelivery.Replace(title, " ");
        title = FreebieSelectors.FreeWithPurchase.Replace(title, " ");
        title = BareFreeWording.Replace(title, " ");
        // The rebate and coupon clauses, and the price they hang off. All of it is the offer, none
        // of it is the thing being sold.
        title = FreebieSelectors.OfferNoise.Replace(title, " ");

        title = Whitespace.Replace(title, " ");

        // Cutting a word out of the middle of a phrase leaves the join behind. "COUCH - BIG AND
        // COMFORTABLE AND FREE" became "…COMFORTABLE AND" and "2 for $0.38 AC + AR" became
        // "2 for +", both live this session. A trailing conjunction or stray operator is not part of
        // any product name, and it reaches the comp matcher as if it were.
        title = DanglingJoin.Replace(title, "");
        title = Whitespace.Replace(title, " ");

        return title.Trim(' ', ',', '-', '–', '—', '|', ':', '!', '.', '+', '&').Trim();
    }

    // Applied repeatedly, because removing one join can expose another ("… for + +").
    private static readonly Regex DanglingJoin = new(
        @"(?:\s*[-–—|:,+&]\s*|\s+(?:and|or|w/|with|for|at|plus|via|after)\s*)+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Only the wording, never a word inside a product name. "Freedom", "FreeSync" and "Free Willy"
    // survive on the word boundaries; the hyphenated compounds need the extra guard, because
    // "BPA-free jugs" came back live as "BPA- jugs" — a product nothing will ever comp.
    private static readonly Regex BareFreeWording = new(
        @"\b(?:100%\s*off|(?<![A-Za-z]-)free\s+to\s+(?:a\s+)?(?:good\s+)?(?:home|veteran|vet)|" +
        @"(?<![A-Za-z]-)free(?:bie)?s?|giving\s+away|give\s?away|gratis|no\s+charge|curb\s*alert|" +
        @"must\s+go|need(?:s)?\s+gone|first\s+come(?:,?\s*first\s+served)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Whitespace = new(@"\s{2,}", RegexOptions.Compiled);

    // The item, the price and the offer are all in the opening line of these bodies; everything
    // after it is specs and forum chatter. Same cut, and the same reason, as DealFeedParser's.
    private const int OpeningLineChars = 220;

    private static string FirstLine(string? text)
    {
        var trimmed = (text ?? "").Trim();
        if (trimmed.Length == 0) return "";

        var stop = trimmed.IndexOfAny(['\n', '\r']);
        if (stop > 20) trimmed = trimmed[..stop];

        return trimmed.Length > OpeningLineChars ? trimmed[..OpeningLineChars] : trimmed;
    }

    // ── How the row says what it is ──────────────────────────────────────────────────────────────

    private static string LabelFor(FreebieDetails details) => details.Kind switch
    {
        FreebieKinds.FreeAfterRebate => details.RebateAmount > 0
            ? $"Free after ${details.RebateAmount:0.##} rebate"
            : "Free after rebate",
        FreebieKinds.FreeAfterCoupon => "Free with code",
        FreebieKinds.NearFree => $"${details.ListPrice:0.##} — near free",
        _ => "Free",
    };
}
