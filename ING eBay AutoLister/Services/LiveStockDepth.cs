using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How many of this you would then own, and how long eBay takes to absorb that many.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Every other block on the live card asks whether the lot on
/// screen is worth its price, and every one of them answers correctly about a lot the seller has
/// never owned one of. Live selling breaks that assumption harder than any other sourcing channel
/// in this app: the host has a pallet of the thing and puts one up every four minutes, each
/// individually cheap, each individually a <c>BID UP TO $90</c>. Win six and the app has said yes
/// six times to six defensible purchases — and the seller now owns six of a product eBay clears two
/// of a month, which is one flip and five months of a shelf.
/// </para>
/// <para>
/// <b>Both halves of the count already existed and neither was ever read at bid time.</b> The ones
/// bought and unsold live on the Deal Pipeline and arrive here inside
/// <see cref="OwnSalesEvidence.UnitsHeld"/>. The ones won <i>tonight, on this tab, twenty minutes
/// ago</i> live on <see cref="LiveBuySheet"/> — and that is the count nothing else on the card can
/// possibly see, because those lots are still in their boxes and have never touched the pipeline.
/// The four-in-one-show case is the whole reason this file exists.
/// </para>
/// <para>
/// <b>Nothing here prices anything.</b> Saturation is a claim about <i>time</i>, not about price:
/// the fourth one still resells for exactly what the comps say, it just sells in April. So unlike
/// <see cref="LiveTrend"/> and <see cref="LiveCondition"/> — which cut the ceiling because they are
/// about what the thing fetches — this takes nothing off any figure on the card. A "you already
/// have three" haircut would be a number nobody measured, shading the one price on the screen that
/// comes from real sales. What it spends is the seller's attention, which is the honest currency
/// for a fact about a calendar.
/// </para>
/// <para>
/// <b>It costs no lookup and no clock.</b> The market's clearance rate and the days-to-sell are the
/// card's own figures, already computed; the shelf count arrives with the seller's own record, which
/// is read once and held between bids; tonight's count is one cached JSON read. So this re-answers
/// on a held-comps re-price exactly as it does on a fresh one, in the milliseconds a climbing bid
/// leaves.
/// </para>
/// </remarks>
public static class LiveStockDepth
{
    /// <summary>
    /// Months of stock past which the money being parked is the thing worth saying out loud. The
    /// same bar <see cref="LiveLotSize.SlowClearanceMonths"/> draws for a single multi-unit lot,
    /// because it is the same fact about the same demand — this one just counts the shelf too.
    /// </summary>
    public const decimal DeepMonths = LiveLotSize.SlowClearanceMonths;

    /// <summary>
    /// Months of stock past which this has stopped being a flip. A quarter of a year is longer than
    /// the sold window every comp on the card came out of, so at this depth the last unit will be
    /// sold into a market nothing on this screen has any evidence about.
    /// </summary>
    public const decimal FloodedMonths = 4m;

    /// <summary>
    /// Units past which "no dated sold history" stops being a footnote and becomes a warning. One
    /// spare of an unmeasurable product is a normal risk; a pile of them is an unmeasurable pile.
    /// </summary>
    public const int BlindPileUnits = 3;

    /// <summary>
    /// Reads the pile and what the market does with it.
    /// </summary>
    /// <param name="lotUnits">Units in the lot on screen — <see cref="LiveLotUnits.Count"/>.</param>
    /// <param name="own">The seller's own record, for the shelf count alone. Null means their book
    /// could not be read, which is said out loud rather than counted as a zero.</param>
    /// <param name="tonight">What tonight's buy sheet already holds of it.</param>
    /// <param name="monthlySales">How many of these eBay clears a month. Zero means nothing dated.</param>
    /// <param name="daysToSellOne">How long one of them takes, when the card knows.</param>
    /// <param name="activeCompCount">Units listed against the seller right now. Reported only.</param>
    public static LiveStockRead Read(
        int lotUnits, OwnSalesEvidence? own, LiveStockTonight tonight,
        decimal monthlySales, int? daysToSellOne, int activeCompCount)
    {
        var lot = Math.Max(1, lotUnits);
        var shelf = Math.Max(0, own?.UnitsHeld ?? 0);
        var won = Math.Max(0, tonight.Units);
        var lots = Math.Max(0, tonight.Lots);
        var after = lot + shelf + won;

        var read = new LiveStockRead
        {
            LotUnits = lot,
            UnitsHeld = shelf,
            WonTonight = won,
            LotsWonTonight = lots,
            UnitsAfter = after,
            MonthlySales = monthlySales > 0m ? monthlySales : 0m,
            ActiveCompCount = Math.Max(0, activeCompCount),
            ShelfRead = own is not null,
            IdentityIsLoose = own?.IdentityIsLoose == true,
            Sources = Bars(lot, shelf, won, lots),
        };

        // The same arithmetic a multi-unit lot's own absorption note is built from, called rather
        // than re-derived: one formula in the app for "how long do N of these take", so the strip
        // and the units block can never disagree about the same pile.
        var (months, days, _) = LiveLotSize.Absorption(after, read.MonthlySales, daysToSellOne);
        read.MonthsToClear = months;
        read.DaysToClearAll = days;

        Judge(read);
        return read;
    }

    /// <summary>
    /// The bars, biggest question first: what is already yours, then what tonight has already
    /// committed, then the lot that is not yours yet.
    /// </summary>
    /// <remarks>
    /// Zero parts are left off entirely — a bar reading "0" is a bar that has to be read before it
    /// can be ignored. And a single item with nothing behind it gets <b>no bars at all</b>: one bar
    /// saying "This lot 1" is a picture of the number already in the sentence above it, on the card
    /// where the seller has the least attention to spare. The headline still says the state, so
    /// nothing goes unsaid — only undrawn.
    /// </remarks>
    private static List<LiveStockSource> Bars(int lot, int shelf, int won, int lots)
    {
        var bars = new List<LiveStockSource>();
        if (shelf == 0 && won == 0 && lot <= 1) return bars;

        if (shelf > 0)
        {
            bars.Add(new LiveStockSource
            {
                Kind = LiveStockSources.Shelf,
                Label = "On the shelf",
                Units = shelf,
                Detail = $"{Units(shelf)} bought and not yet sold, on your Deal Pipeline.",
            });
        }

        if (won > 0)
        {
            bars.Add(new LiveStockSource
            {
                Kind = LiveStockSources.Tonight,
                Label = "Won tonight",
                Units = won,
                Detail = $"{Units(won)} on tonight's buy sheet across {Lots(lots)}, still in the box. " +
                         "Listed ones are counted on the shelf instead, never twice.",
            });
        }

        bars.Add(new LiveStockSource
        {
            Kind = LiveStockSources.Lot,
            Label = "This lot",
            Units = lot,
            Detail = lot == 1
                ? "The one on screen, which is not yours yet."
                : $"The {lot} on screen, which are not yours yet.",
        });

        return bars;
    }

    /// <summary>
    /// Which of the five states this is, and every sentence it says in that state.
    /// </summary>
    private static void Judge(LiveStockRead read)
    {
        var pile = read.UnitsAfter;
        var stocked = read.AlreadyStocked;

        // Nothing was countable. Only reachable when the seller's own book failed to read AND the
        // sheet holds none AND the lot is a single — in every other case there is a real number
        // above, and a number is worth showing even when half of it is missing.
        if (!read.ShelfRead && !stocked && pile <= 1)
        {
            read.Readable = false;
            read.Verdict = LiveStockVerdicts.None;
            read.Headline = "How many you already have wasn't read";
            read.Note = "Your own sales book could not be opened for this one, so nothing here can say "
                      + "whether you are already sitting on a stack of them.";
            return;
        }

        // One of them and none anywhere. The ordinary case, said rather than left blank: a strip
        // that goes quiet here is a strip whose silence means both "you're clear" and "nothing
        // looked", and only one of those is worth pressing bid on.
        if (pile <= 1)
        {
            read.Readable = true;
            read.Verdict = LiveStockVerdicts.Single;
            read.Headline = "Your only one";
            read.Note = "Nothing of this on your shelf and none won tonight, so it sells at the market's own "
                      + "speed" + (read.DaysToClearAll is { } d ? $" — about {d} days." : ".");
            Append(read);
            return;
        }

        // A pile and no rate to measure it against. The blindest state on the card and the only one
        // where the honest answer is that there is no answer.
        if (read.MonthsToClear is not { } months || read.MonthlySales <= 0m)
        {
            read.Readable = false;
            read.Verdict = LiveStockVerdicts.Blind;
            read.Headline = $"You'd have {pile} and nothing says how fast they clear";
            read.Note = "No dated sold history, so there is no way to say whether that is a fortnight of "
                      + "stock or a year of it. They queue behind each other in the same demand either way.";

            if (pile >= BlindPileUnits)
            {
                read.Warning = $"Winning this makes {Units(pile)} of these — {Pile(read)} — and no dated sold "
                             + "history says how many eBay clears a month. Nothing on this card can tell you "
                             + "whether that stack turns over or sits.";
            }

            Append(read);
            return;
        }

        read.Readable = true;

        // The pile in time. Every sentence below is built from the same two figures, so the badge,
        // the headline and the warning cannot say different things about one stack.
        var span = MonthsInWords(months);
        var last = read.DaysToClearAll is { } all ? $" and the last one is cash in about {all} days" : "";
        var rate = $"About {read.MonthlySales:0.#} of these sell a month on eBay";

        if (months >= FloodedMonths)
        {
            read.Verdict = LiveStockVerdicts.Flooded;
            read.Headline = $"You'd have {pile} — {span} of stock";
            read.Note = $"{rate}, so {pile} of them is roughly {span} of selling{last}. That is not {pile} "
                      + $"flips; it is one flip and {span} of shelf.";
            read.Warning = $"Winning this makes {Units(pile)} of these — {Pile(read)}. {rate}, so the money is "
                         + $"parked for {span} before the last one turns over. Bid on this one as if it were "
                         + "the last of them you can actually sell.";
        }
        else if (months >= DeepMonths)
        {
            read.Verdict = LiveStockVerdicts.Deep;
            read.Headline = $"You'd have {pile} — {span} of stock";
            read.Note = $"{rate}, so {pile} of them is roughly {span} of selling{last}. The profit on each is "
                      + "real; what it costs is the months the cash spends on a shelf.";
            read.Warning = $"Winning this makes {Units(pile)} of these — {Pile(read)}. {rate}, so that is "
                         + $"roughly {span} of selling{last}. Each one still makes its profit — they just "
                         + "queue behind each other for it.";
        }
        else
        {
            read.Verdict = LiveStockVerdicts.Clear;
            read.Headline = $"You'd have {pile} — {span} of stock";
            read.Note = $"{rate}, so {pile} of them is roughly {span} of selling{last}. The market absorbs "
                      + "that without them queueing up on you.";
        }

        Append(read);
    }

    /// <summary>The caveats, appended to whatever was just said. Every one of them is a reason to
    /// trust the count above less, and none of them is allowed to be the reason it goes unsaid.</summary>
    private static void Append(LiveStockRead read)
    {
        // What the shelf count is missing. An unread book must never read as an empty one — that is
        // the failure that turns "you have four already" into silence.
        if (!read.ShelfRead)
        {
            read.Note += " Your own sales book could not be read, so the count above is tonight's buy sheet "
                       + "alone — anything already on your shelf is missing from it.";
        }

        // Who else is holding them. Reported beside the pile and never inside it: these are other
        // people's units, and the clearance rate above is already measured against them.
        if (read.ActiveCompCount > 0 && read.MonthlySales > 0m)
        {
            read.Note += $" {read.ActiveCompCount} of them are listed on eBay right now, ahead of yours.";
        }

        // The match may be somebody else's product. Said last, because it qualifies everything
        // before it — including, deliberately, the warning.
        if (read.IdentityIsLoose && read.AlreadyStocked)
        {
            const string caveat = " These were matched on ordinary words with no model number among them, "
                                + "so some of what you are holding may be a different product.";
            read.Note += caveat;
            if (read.Warning.Length > 0) read.Warning += caveat;
        }
    }

    /// <summary>The pile broken back down, for a warning that has to stand on its own away from
    /// the bars. Only the parts that exist are named.</summary>
    private static string Pile(LiveStockRead read)
    {
        var parts = new List<string>();
        if (read.UnitsHeld > 0) parts.Add($"{read.UnitsHeld} on the shelf");
        if (read.WonTonight > 0) parts.Add($"{read.WonTonight} won tonight");
        parts.Add(read.LotUnits == 1 ? "one in this lot" : $"{read.LotUnits} in this lot");
        return string.Join(", ", parts);
    }

    /// <summary>Months, in the words the units block already uses for the same span.</summary>
    public static string MonthsInWords(decimal months) => LiveLotSize.MonthsInWords(months);

    private static string Units(int count) => count == 1 ? "one" : count.ToString();

    private static string Lots(int count) => count == 1 ? "1 lot" : $"{count} lots";
}
