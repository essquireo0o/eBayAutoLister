using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The card, in one line.
/// </summary>
/// <remarks>
/// <para>
/// The arbitrage card is replaced wholesale every time the bid moves — which, during a live sale, is
/// every two or three seconds. That is fine for a card you study and wrong for a card you glance at:
/// the eye has to re-find the number it was reading, and a screen reader announces the whole thing,
/// ladder and stat tiles and comp table, several times a lot. An announcement nobody can keep up
/// with is the same as no announcement.
/// </para>
/// <para>
/// So one line carries the decision — the call, the ceiling, where the bidding is against it, and
/// what the thing resells for — and it is the only part of the screen that is a live region. It is
/// also the label a lot row is read out as, so the list and the card say the same sentence about the
/// same lot.
/// </para>
/// <para>
/// <b>It is written here rather than in the browser</b>, next to the ceiling it restates, for the
/// same reason <see cref="LiveBidAdvisor.RankLot"/> is: a sentence assembled in JavaScript out of
/// <c>maxBid</c> and <c>headroom</c> is a second opinion about money that nothing tests, and the one
/// it would disagree with is the one on the badge. The ceiling in this line is not re-rendered at
/// all — it is <see cref="LiveBidCard.CallLabel"/> verbatim.
/// </para>
/// <para>
/// <b>What it refuses to claim.</b> It never states a resale price the card does not have, never
/// states a sell-through rate with no denominator under it, and never talks about room above the
/// bid when there is no ceiling to have room under. It says nothing about how old the comps are or
/// whether they were re-read — that is the card's held-comps line, and keeping it out of here is
/// what makes the sentence identical before and after a re-price that changed nothing.
/// </para>
/// </remarks>
public static class LiveBidSpeech
{
    /// <summary>
    /// The one line for this card. Empty only for a card that is not there.
    /// </summary>
    public static string Say(LiveBidCard? card)
    {
        if (card is null) return "";

        // Nothing was priced. The line stops at that, because every other clause below would be a
        // number this card does not have — and a spoken "$0 of room" on an item with no sold history
        // is the one failure this screen exists to avoid.
        if (card.Call == LiveBidCalls.NoData)
            return Join(Headline(card), "No eBay sold history to bid against.");

        return Join(Headline(card), WhereTheBiddingIs(card), WhatItResellsFor(card));
    }

    /// <summary>
    /// The call, in the badge's own words. Not re-derived: if the badge says <c>BID UP TO $240</c>
    /// then so does the line, and the two cannot drift apart because there is only one of them.
    /// </summary>
    private static string Headline(LiveBidCard card)
    {
        var label = (card.CallLabel ?? "").Trim();
        if (label.Length == 0) label = FallbackWord(card.Call);
        return label.EndsWith('.') ? label : label + ".";
    }

    /// <summary>A card built by hand, or by something that forgot the badge. Never seen from
    /// <see cref="LiveBidAdvisor.Build"/>, which always sets one.</summary>
    private static string FallbackWord(string? call) => call switch
    {
        LiveBidCalls.Bid => "BID",
        LiveBidCalls.Risky => "RISKY",
        LiveBidCalls.Stop => "STOP",
        _ => "CAN'T PRICE IT",
    };

    /// <summary>
    /// Where the bidding stands against the ceiling. Silent when there is no ceiling — a card that
    /// says DON'T BID because fees eat the whole resale price has no line to be near, and "$180 past
    /// the ceiling" would invite the reading that some smaller bid works.
    /// </summary>
    private static string WhereTheBiddingIs(LiveBidCard card)
    {
        if (card.MaxBid <= 0m) return "";
        if (!card.BidWasKnown) return "Bidding hasn't started.";

        // Every figure in this line rounds AGAINST the bidder: the bid up, the room down, the
        // overshoot up. A line read at a glance in the two seconds before a hand goes up should
        // never be the optimistic version of the card underneath it.
        var at = Math.Ceiling(card.CurrentBid).ToString("C0");

        return card.Headroom >= 0m
            ? $"At {at}, {Math.Floor(card.Headroom).ToString("C0")} of room."
            : $"At {at} — {Math.Ceiling(-card.Headroom).ToString("C0")} past the ceiling.";
    }

    /// <summary>
    /// The resale side, in the order the decision needs it: what it sells for, whether it moves, and
    /// how much sold history is behind both. Each clause is dropped rather than dashed — a spoken
    /// line reads "—" as nothing at all, so an absent number has to be an absent clause.
    /// </summary>
    private static string WhatItResellsFor(LiveBidCard card)
    {
        if (card.ResalePrice is not { } resale || resale <= 0m) return "";

        var line = $"Resells around {Math.Floor(resale).ToString("C0")}";

        // A rate with no active listings under it is not a rate. The card shows "—" for this and the
        // line says nothing, rather than the two of them disagreeing about whether 100% happened.
        if (!card.SellThroughUnbounded && card.SellThroughRate is { } rate && rate > 0m)
            line += $", {rate:0}% sell-through";

        if (card.CompCount > 0)
            line += $", on {card.CompCount} comp{(card.CompCount == 1 ? "" : "s")}";

        return line + ".";
    }

    private static string Join(params string[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
}
