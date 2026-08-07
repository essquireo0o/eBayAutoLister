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
            return Join(Headline(card), HowMany(card), "No eBay sold history to bid against.",
                YourOwnRecord(card), HowManyYoudHave(card));

        return Join(Headline(card), HowMany(card), WhichWayItsGoing(card), WhatKindOfOne(card),
            WhatItShipsFor(card), WhereTheBiddingIs(card), HowManyPressesLeft(card),
            WhatItResellsFor(card), YourOwnRecord(card), HowManyYoudHave(card));
    }

    /// <summary>
    /// What this one costs to get delivered, said fifth — after the two cuts and before the bidding,
    /// because like them it is a fact about what the ceiling the seller has just heard already
    /// accounts for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two states, and they are the two where the freight is not what the seller typed. The first is
    /// the good one, and it is the only good news in this whole line: this lot rides in a box that is
    /// already going out, so the ceiling is higher than the one they heard on the first lot of the
    /// night. A seller who does not know that will bid it like the first one.
    /// </para>
    /// <para>
    /// The second is its opposite — repeated wins from one show with no extra-item rate entered, so
    /// the ceiling is knowingly charging full freight for a box already on its way. That one states
    /// no figure at all: nothing has been measured, and the fix is a box on the screen.
    /// </para>
    /// <para>
    /// Silent on every card charged the ordinary way, which is nearly all of them, and silent on the
    /// first lot of a show — "this one pays full shipping" is what every card has always meant. One
    /// dollar figure at most: a second number in a spoken line is a second number to mishear.
    /// </para>
    /// </remarks>
    private static string WhatItShipsFor(LiveBidCard card)
    {
        if (card.Ship is not { } ship) return "";

        return ship.Verdict switch
        {
            LiveShipVerdicts.Combined when ship.Marginal <= 0m =>
                $"Ships free with {Lots(ship.LotsWonFromShow)} you've won here.",
            // Rounded UP, like every other cost in this line.
            LiveShipVerdicts.Combined =>
                $"Ships with {Lots(ship.LotsWonFromShow)} you've won here for " +
                $"{Math.Ceiling(ship.Marginal).ToString("C0")}.",
            LiveShipVerdicts.Unstated =>
                $"You've won {(ship.LotsWonFromShow == 1 ? "1 lot" : $"{ship.LotsWonFromShow} lots")} here " +
                "already and this is still charged full shipping.",
            _ => "",
        };
    }

    private static string Lots(int count) => count == 1 ? "the other lot" : $"the other {count} lots";

    /// <summary>
    /// How many of these winning it would make, said last — after everything the market and the
    /// seller's own book have to say about the object, because it is the one clause that is not
    /// about the object at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two states, and they are the two the badge cannot express. A live host puts up the same
    /// product every four minutes and each one is individually a <c>BID UP TO $90</c>; the app has
    /// no way to say "and that is the fourth" except here. So it speaks when the pile has crossed
    /// into months of stock, and when there is a pile with no dated history to measure it against.
    /// </para>
    /// <para>
    /// Silent on every card where the seller is buying their first one, and on every card where the
    /// market clears the stack comfortably — which is nearly all of them. A clause on every lot in
    /// exchange for a count the strip already carries would cost the line the one thing it is for.
    /// It never repeats the ceiling and never states a price: nothing about a shelf changes what the
    /// thing is worth.
    /// </para>
    /// </remarks>
    private static string HowManyYoudHave(LiveBidCard card)
    {
        if (card.Stock is not { } stock || !stock.AlreadyStocked) return "";

        return stock.Verdict switch
        {
            LiveStockVerdicts.Flooded or LiveStockVerdicts.Deep =>
                $"That makes {stock.UnitsAfter} of these — {LiveStockDepth.MonthsInWords(stock.MonthsToClear ?? 0m)} " +
                "of stock.",
            LiveStockVerdicts.Blind when stock.UnitsAfter >= LiveStockDepth.BlindPileUnits =>
                $"That makes {stock.UnitsAfter} of these, and nothing dated says how fast they clear.",
            _ => "",
        };
    }

    /// <summary>
    /// That the comps were not the same condition as the thing on screen, said fourth — after the
    /// trend, before any dollar figure it changes the meaning of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two states, both rare, both of which change what the hand does. The first is the cut: the
    /// badge the seller just heard is lower than the comps under it and they are owed the reason.
    /// The second is the sharper one — the lot is in worse shape than nearly everything it was
    /// priced off and there were too few matching sales to correct it by, so the ceiling is
    /// knowingly a better-condition price. That is the one moment on this screen where the number
    /// in the badge is optimistic and only this sentence says so.
    /// </para>
    /// <para>
    /// Silent everywhere else, including the very common "nothing said what condition this is".
    /// Mixed comps are the normal case, and a clause on every lot in exchange for a caveat the
    /// card's own strip already carries would cost the line the thing it is for.
    /// </para>
    /// </remarks>
    private static string WhatKindOfOne(LiveBidCard card)
    {
        if (card.Condition is not { Readable: true } cond) return "";
        if (cond.Band == LiveConditionBands.Unstated) return "";

        var lot = LiveCondition.ShortLabel(cond.Band);

        if (cond.Discounted)
            return $"Priced as {lot} — ceiling already cut {Math.Round(cond.CutPercent):0}% for it.";

        // Worse than the comps, with nothing honest to correct the ceiling by.
        if (cond.MatchedComps < LiveCondition.MinBandComps
            && LiveCondition.Rank(cond.DominantBand) > LiveCondition.Rank(cond.Band))
        {
            return $"That ceiling is a {LiveCondition.ShortLabel(cond.DominantBand)} price and this one is " +
                   $"{lot} — bid under it.";
        }

        return "";
    }

    /// <summary>
    /// Whether there is a press left to make, said immediately after where the bidding stands —
    /// because it is the one clause that can contradict the one before it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "At $45, $1 of room" is true and is not permission: bids go up in fives, so pressing costs
    /// $50. Those are the only two states this speaks in — the last press, and no press at all —
    /// because they are the only two where hearing the sentence changes what the hand does. A count
    /// spoken on every card ("six more bids") would be a clause on every lot in exchange for
    /// information the room figure already carried.
    /// </para>
    /// <para>
    /// It says nothing when the bidding is already past the ceiling: <see cref="WhereTheBiddingIs"/>
    /// has just said so in dollars, and a second refusal in the same breath is the same fact twice.
    /// </para>
    /// </remarks>
    private static string HowManyPressesLeft(LiveBidCard card) => card.NextBid?.Verdict switch
    {
        LiveNextBidVerdicts.Last => "Last bid you can make.",
        LiveNextBidVerdicts.Stop => "The next bid is past the ceiling — don't press.",
        _ => "",
    };

    /// <summary>
    /// That the sold price has been moving, said third — after the badge and the unit count, before
    /// any figure it changes the meaning of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately silent on most cards. It speaks in exactly one case: the one where the trend
    /// actually took money off the ceiling, so the badge the seller just heard is lower than the
    /// comps under it would suggest and they are owed the reason. A steady item, a climbing one, a
    /// fall too thin to price against and one with no dated history all say nothing here — a line
    /// that grew a clause for every reading would stop being glanceable, which is the only thing it
    /// is for. The card's own trend strip carries the rest.
    /// </para>
    /// <para>
    /// It never states the cut as a new price. The ceiling in the badge is already the cut one, and
    /// a second dollar figure in a spoken line is a second number to mishear.
    /// </para>
    /// </remarks>
    private static string WhichWayItsGoing(LiveBidCard card)
    {
        if (card.Trend is not { Discounted: true } trend) return "";

        return $"Prices are sliding — ceiling already cut {Math.Round(trend.CutPercent):0}% for it.";
    }

    /// <summary>
    /// That this is several things, said second — immediately after the badge and before any other
    /// number, because it is what every figure after it is a figure ABOUT.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A ceiling three times the size of the last lot's is the most surprising thing this line can
    /// say, and the surprise is the point: a seller who hears "BID UP TO $240" on a miner they know
    /// goes for $80 needs the next two words to be "for three".
    /// </para>
    /// <para>
    /// The other case it speaks is the one where the screen knows it is showing the wrong number —
    /// a name that says "bundle" without saying how many, priced as one. That is not a caveat to
    /// leave in a warning list nobody reads mid-lot.
    /// </para>
    /// </remarks>
    private static string HowMany(LiveBidCard card)
    {
        var units = card.Units;
        if (units is null) return "";

        if (units.IsLot)
        {
            // Rounded down, like every other figure in this line.
            var each = units.MaxBidPerUnit > 0m
                ? $", {Math.Floor(units.MaxBidPerUnit).ToString("C0")} each"
                : "";
            return $"For all {units.Count}{each}.";
        }

        return units.CountUnstated
            ? "This reads as more than one — priced as ONE. Set the quantity."
            : "";
    }

    /// <summary>
    /// What the seller themselves has done with this product, when they have done it more than once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Last, and short, and only on a proven record. It is the strongest evidence on the card — the
    /// fee eBay actually charged this seller, on this product, in their own listings — so it earns a
    /// place in the one line that gets read out; but a line that grew a clause for every seller who
    /// once sold something similar would stop being glanceable, which is the only thing it is for.
    /// </para>
    /// <para>
    /// It states the seller's own ceiling only when that ceiling is the one that matters: below the
    /// badge, where the badge is the optimistic number, or the only one there is because the comps
    /// priced nothing. Otherwise the badge already said it.
    /// </para>
    /// </remarks>
    private static string YourOwnRecord(LiveBidCard card)
    {
        if (card.OwnHistory is not { } own) return "";
        if (own.Verdict != OwnTrackVerdicts.Proven || own.UnitsSold <= 0) return "";

        var line = $"You've sold {own.UnitsSold}";

        if (own.AverageNetProfit is { } net && net > 0m)
            line += $", {Math.Floor(net).ToString("C0")} net each";

        // Rounded down, like every other figure in this line: a ceiling spoken at a glance is
        // allowed to understate and never to overstate.
        if (own.OwnMaxBid > 0m && (own.CeilingIsLower || own.OwnIsTheOnlyCeiling))
            line += $" — on your own prices, stop at {Math.Floor(own.OwnMaxBid).ToString("C0")}";

        return line + ".";
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
