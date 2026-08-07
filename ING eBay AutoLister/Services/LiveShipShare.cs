using ING_eBay_AutoLister.Models;
using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What winning the lot on screen adds to the shipping bill, as opposed to what one lot from this
/// show costs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every dollar on this card hangs off a landed cost — bid, plus
/// the platform's premium, plus shipping — and two of those three are exact. The third has been a
/// single figure typed once and charged in full to every lot of the night. A live seller does not
/// post twelve parcels: they post <i>one box</i>, at the end of the show, and charge a first-item
/// rate plus a much smaller rate for each additional thing in it. So a seller who wins six lots at
/// "$12 shipping" pays $12 + 5 × $1, and this app has been costing all six ceilings at $12.
/// </para>
/// <para>
/// <b>The error runs one way.</b> Overcharging shipping lowers a ceiling, so every lot after the
/// first has looked worse than it is — and the ones it makes look worst are the cheap ones, where
/// eleven dollars is most of the margin. That is precisely backwards: on a live show the second lot
/// from a seller you are already buying from is the cheapest thing on the screen, and it is the one
/// the app has been quietly arguing against.
/// </para>
/// <para>
/// <b>Nothing here is guessed.</b> This is the only block on the card that can <i>raise</i> a
/// ceiling, so it is gated on three facts, all of which the seller can see: the show is named, its
/// extra-item rate is entered, and tonight's buy sheet already holds a lot from that same show.
/// Miss any one and the marginal cost is the full first-item rate, which is exactly what the ceiling
/// was charged before this file existed. It never assumes a show combines, never assumes a rate, and
/// never carries a saving across shows.
/// </para>
/// <para>
/// <b>Zero is an answer and blank is not.</b> Free combined shipping is the commonest live-selling
/// arrangement there is, so an entered <c>0</c> means "the extras ride free" and an empty box means
/// "nobody said" — the same distinction the buyer's-premium box already draws, and the reason
/// <see cref="LiveShipRead.AdditionalStated"/> is a flag rather than a test for zero.
/// </para>
/// <para>
/// Pure, and it costs no lookup and no clock: the two rates come off the request and the count comes
/// off a cached list. So it re-answers on a held-comps re-price exactly as it does on a fresh price.
/// </para>
/// </remarks>
public static class LiveShipShare
{
    /// <summary>How much of a show name is kept. A handle is a word; anything past this is a pasted
    /// page title, and it is cut rather than refused so a long paste still matches itself.</summary>
    public const int MaxShowKeyLength = 80;

    // A pasted show address rather than a typed handle. Everything up to and including the last
    // slash goes, so "https://www.whatnot.com/live/abc-123?ref=x" and "abc-123" are one show — which
    // is what lets the browser fill this box from the address it is already reading the show from.
    private static readonly Regex UrlLead = new(
        @"^\s*(?:https?://)?(?:[\w-]+\.)*whatnot\.com/[^\s]*?/?(?<key>[^/?#\s]+)/?(?:[?#].*)?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>
    /// The key two rows are the same show by. Public because "did the app think these were the same
    /// show" is the question with money behind it.
    /// </summary>
    /// <remarks>
    /// Deliberately forgiving in the directions a seller varies and nowhere else: case, surrounding
    /// spaces, a leading <c>@</c> on a handle, and the show's own address instead of its name. It
    /// does not stem, split or fuzzy-match — two different shows sharing a word must never share a
    /// box, because the cost of a wrong match here is a ceiling that is too high.
    /// </remarks>
    public static string NormalizeShow(string? raw)
    {
        var text = Whitespace.Replace(raw ?? "", " ").Trim();
        if (text.Length == 0) return "";

        if (UrlLead.Match(text) is { Success: true } url) text = url.Groups["key"].Value;

        text = text.TrimStart('@').Trim();
        if (text.Length > MaxShowKeyLength) text = text[..MaxShowKeyLength];

        return text.ToLowerInvariant();
    }

    /// <summary>The show as it belongs on screen: the seller's own wording, tidied and cut, never
    /// lower-cased. The key above is for matching; this is for reading.</summary>
    public static string DisplayShow(string? raw)
    {
        var text = Whitespace.Replace(raw ?? "", " ").Trim();
        if (text.Length == 0) return "";

        if (UrlLead.Match(text) is { Success: true } url) text = url.Groups["key"].Value;

        text = text.TrimStart('@').Trim();
        return text.Length > MaxShowKeyLength ? text[..MaxShowKeyLength] : text;
    }

    /// <summary>
    /// Reads what this lot really adds to the shipping bill.
    /// </summary>
    /// <param name="showName">Which show this is, as the seller wrote it. Empty means they have not
    /// said, and nothing is combined — an unnamed show could be any show.</param>
    /// <param name="firstItemShipping">What the show charges to post the first thing won.</param>
    /// <param name="additionalItemShipping">What it charges for each extra thing in the same box.
    /// Null is "nobody said"; zero is "they ride free".</param>
    /// <param name="tonight">What tonight's sheet already holds from this show
    /// (<see cref="LiveBuySheet.ShippingOnShow"/>).</param>
    public static LiveShipRead Read(
        string? showName, decimal firstItemShipping, decimal? additionalItemShipping,
        LiveShipTonight tonight)
    {
        var first = Math.Max(0m, firstItemShipping);
        var show = DisplayShow(showName);
        var stated = additionalItemShipping is not null;
        var extra = Math.Max(0m, additionalItemShipping ?? 0m);
        var lots = Math.Max(0, tonight.Lots);

        var read = new LiveShipRead
        {
            ShowName = show,
            FirstItemShipping = first,
            AdditionalItemShipping = extra,
            AdditionalStated = stated,
            LotsWonFromShow = lots,
            ShippingSoFar = Math.Round(Math.Max(0m, tonight.ShippingCharged), 2),
            // Full freight until something proves otherwise. Every path below either leaves this
            // alone or lowers it, which is what makes "a card that fails any gate is priced exactly
            // as it was before this existed" a property of the code rather than a promise.
            Marginal = first,
            Readable = first > 0m,
        };

        Judge(read);

        read.Saved = Math.Round(Math.Max(0m, first - read.Marginal), 2);
        read.ShippingWithThisLot = Math.Round(read.ShippingSoFar + read.Marginal, 2);
        return read;
    }

    /// <summary>Which of the five states this is, and every sentence it says in that state.</summary>
    private static void Judge(LiveShipRead read)
    {
        var first = read.FirstItemShipping;
        var show = read.ShowName;

        // Nothing was entered, so the ceiling is charging nothing to get the thing delivered. The
        // most expensive silence on this card: shipping on a live show is real money and it comes
        // off the bid, not off the profit afterwards.
        if (!read.Readable)
        {
            read.Verdict = LiveShipVerdicts.None;
            read.Headline = "Shipping not entered";
            read.Note = "The ceiling above is costing this lot as though it arrived free. Live sellers charge "
                      + "shipping on top of the winning bid — put what this show charges in the Shipping box, "
                      + "and what it charges for each extra lot beside it.";
            read.Warning = "No shipping cost entered. Live sellers charge it on top of the bid — if it turns "
                         + "out to be $12, take $12 off the ceiling.";
            return;
        }

        // The state the seller is the only one who can fix: they are winning repeatedly from one
        // show, nothing has said what an extra lot costs, and the ceiling is charging full freight
        // again for a box that is already going out. Nothing is assumed on their behalf — an
        // unstated rate really could be the full rate again, and a saving invented here would be a
        // ceiling nobody could defend. So the money is left alone and the sentence is loud.
        if (show.Length > 0 && !read.AdditionalStated && read.LotsWonFromShow > 0)
        {
            read.Verdict = LiveShipVerdicts.Unstated;
            read.Headline = $"Charged the full {Money(first)} again";
            read.Note = $"{Lots(read.LotsWonFromShow)} from {show} {Was(read.LotsWonFromShow)} already on "
                      + $"tonight's sheet, and this lot's ceiling is being costed as though it shipped on its "
                      + "own. Live sellers post one box per show — say what this one charges for each extra "
                      + "lot and the ceiling above goes up.";
            read.Warning = $"You've already won {Lots(read.LotsWonFromShow)} from {show} tonight and the "
                         + $"ceiling above is charging the full {Money(first)} shipping again. Live sellers post "
                         + "one box per show — put what this one charges for each extra lot in the box beside "
                         + "Shipping and this ceiling goes up.";
            return;
        }

        // Shipping is stated and nothing says a second lot would ride along with the first, so the
        // full rate stands. Two ways to be here, and they have different next moves.
        if (show.Length == 0 || !read.AdditionalStated)
        {
            read.Verdict = LiveShipVerdicts.Alone;
            read.Headline = $"Full shipping — {Money(first)}";
            read.Note = show.Length == 0
                ? $"Every lot is being charged the whole {Money(first)}. Name the show and say what it charges "
                + "for each extra lot, and everything you win after the first one will be costed at that rate "
                + "instead — a live seller posts one box per show, not one per lot."
                : $"Every lot from {show} is being charged the whole {Money(first)}. Say what the show charges "
                + "for each extra lot and this comes down for everything after your first win — a live seller "
                + "posts one box per show, not one per lot.";
            return;
        }

        // Both rates known, nothing won from this show yet. The first lot really does pay full
        // freight — and it is worth saying that the ones after it will not, because that is what
        // makes the first win of a show the expensive one and everything after it cheap.
        if (read.LotsWonFromShow == 0)
        {
            read.Verdict = LiveShipVerdicts.First;
            read.Headline = $"First lot from {show} — the full {Money(first)}";
            read.Note = read.AdditionalItemShipping <= 0m
                ? $"This one carries the whole {Money(first)}. Anything else you win from {show} tonight ships "
                + "free in the same box, so those ceilings will be higher than this one."
                : $"This one carries the whole {Money(first)}. Anything else you win from {show} tonight ships "
                + $"for {Money(read.AdditionalItemShipping)} in the same box, so those ceilings will be "
                + $"{Money(first - read.AdditionalItemShipping)} higher than this one.";
            return;
        }

        // A lot from this show is already on tonight's sheet, and the seller has said what an extra
        // one costs. This is the one state that moves money.
        read.Verdict = LiveShipVerdicts.Combined;
        read.Marginal = read.AdditionalItemShipping;

        var already = read.LotsWonFromShow == 1 ? "your other lot" : $"your other {read.LotsWonFromShow} lots";

        read.Headline = read.Marginal <= 0m
            ? $"Ships free with {already}"
            : $"Ships with {already} — {Money(read.Marginal)}, not {Money(first)}";

        read.Note = $"{Lots(read.LotsWonFromShow)} from {show} {Was(read.LotsWonFromShow)} already on tonight's "
                  + $"sheet, so this one goes in the same box. The ceiling above was costed at "
                  + $"{Money(read.Marginal)} to deliver rather than {Money(first)}";

        var saving = Math.Max(0m, first - read.Marginal);
        read.Note += saving > 0m
            ? $", which is {Money(saving)} more you can bid on it than the first one of the night."
            : " — this show charges no less for an extra lot, so there is nothing to be gained by combining it.";
    }

    private static string Lots(int count) => count == 1 ? "1 lot" : $"{count} lots";

    private static string Was(int count) => count == 1 ? "is" : "are";

    /// <summary>Every dollar figure on this strip, in one place. Cents are shown when there are any:
    /// an extra-item rate is routinely $1.50, and rounding it away would misstate the one number
    /// this whole block exists to introduce.</summary>
    private static string Money(decimal amount) =>
        amount == Math.Truncate(amount) ? amount.ToString("C0") : amount.ToString("C");
}
