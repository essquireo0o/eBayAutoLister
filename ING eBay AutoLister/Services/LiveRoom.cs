using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// What the other bidders have actually been paying — the first read on this card that is about the
/// <b>room</b> rather than about the item.
/// </summary>
/// <remarks>
/// <para>
/// <b>The question this answers.</b> Nineteen sessions of the live card have sharpened one number:
/// the highest bid worth making. Every one of them assumed that a bid worth making is a bid that can
/// be made. On a live show it frequently is not. The card says <c>BID UP TO $240</c>, the seller
/// waits four minutes, and the lot goes for $310 to somebody who is not doing arithmetic. Repeat
/// that for two hours and the night's return on the seller's time is zero, with a screen full of
/// perfectly correct ceilings to show for it.
/// </para>
/// <para>
/// The opposite failure is quieter and costs more. A room that has been clearing at 60% of the app's
/// ceilings is a room where the seller has been bidding to a ceiling they never needed to reach — a
/// margin given away on every win, on evidence that was sitting in front of them.
/// </para>
/// <para>
/// <b>The evidence is the lots that got away.</b> A seller prices thirty lots in a show and wins
/// four. The hammer prices of the other twenty-six are the only direct measurement of the room there
/// is, and the app has been discarding every one of them. <see cref="LiveRoomBook"/> writes them
/// down — one press, on a card already on screen — and this counts them.
/// </para>
/// <para>
/// <b>The wins are counted too, and that is not a detail.</b> A seller wins the lots that go cheap.
/// A rate built only from the losses would be measuring the top tail of its own distribution and
/// would report every room as hotter than it is. So <see cref="LiveBuySheet.WinsOnShow"/> is folded
/// in on equal terms, and only the <i>label</i> on the strip distinguishes them.
/// </para>
/// <para>
/// <b>It prices nothing and cuts nothing.</b> No ceiling, no resale figure, no break-even and no
/// call moves because of anything here. A hot room does not make the item worth less — the ceiling
/// is exactly right and the lot is simply not purchasable at it, which is a different fact and is
/// owed a different sentence. What this spends is the seller's attention, and only in the one state
/// where the answer is "leave".
/// </para>
/// <para>
/// Pure, and it costs no lookup and no clock: the outcomes are one cached read of a list already in
/// memory. So it re-answers on a held-comps re-price in the microseconds a climbing bid leaves.
/// </para>
/// </remarks>
public static class LiveRoom
{
    /// <summary>
    /// Lots below which a clearing ratio is not a rate. Deliberately the same bar
    /// <see cref="WhereToSellAnalyzer.MinLocalSamples"/> refuses to crown a venue on: two lots are
    /// an anecdote about two bidders, and one of them moves the median forty points. The COUNT is
    /// still reported at any size, because "both lots here went over your ceiling" is a true
    /// sentence; what is withheld is the claim that it is a rate.
    /// </summary>
    public const int MinLotsToRate = 3;

    /// <summary>
    /// At or below this share of the ceiling, the room is worth being in: there is real daylight
    /// between what the lots go for and what they are worth. Deliberately well under 1 — a room
    /// clearing at 95% of the ceiling is a room the seller is winning nothing extra in.
    /// </summary>
    public const decimal CheapRatio = 0.85m;

    /// <summary>
    /// Above this share, the room is buying at or over what the app says the lots are worth. Set
    /// slightly above 1 rather than at it: a room clearing at exactly the ceiling is <c>tight</c>,
    /// which is a room the seller can still win in by bidding to the number on screen.
    /// </summary>
    public const decimal HotRatio = 1.02m;

    /// <summary>
    /// How far back a hammer price is still evidence about tonight's room. A show's audience is not
    /// a fixed thing — the same host draws a different crowd on a Saturday than on a Tuesday — and a
    /// clearing rate built out of a month of history would be a claim about a room that no longer
    /// exists. Enforced by <see cref="LiveRoomBook"/> at read time rather than here, so this stays
    /// pure.
    /// </summary>
    public const int EvidenceDays = 14;

    /// <summary>
    /// Combines the lots that got away with the lots that were won, into the one list this reads.
    /// </summary>
    /// <remarks>
    /// Two stores, one question. It lives here rather than in either store because neither of them
    /// should know about the other: the buy sheet is a record of money that left the account and the
    /// room book is a record of prices somebody else paid, and the only place those two are the same
    /// kind of fact is inside this file.
    /// </remarks>
    public static LiveRoomTonight Tonight(
        IReadOnlyList<LiveRoomLot>? passed, IReadOnlyList<LiveRoomLot>? won)
    {
        var lots = new List<LiveRoomLot>((passed?.Count ?? 0) + (won?.Count ?? 0));
        if (passed is not null) lots.AddRange(passed);
        if (won is not null) lots.AddRange(won);
        return lots.Count == 0 ? LiveRoomTonight.Nothing : new LiveRoomTonight(lots);
    }

    /// <summary>
    /// Reads what this show's room has been paying, and what that says about the lot on screen.
    /// </summary>
    /// <param name="show">
    /// The show, as the seller named it. Empty is the commonest reason this read has nothing to say,
    /// and it says so plainly rather than combining every room the seller has ever been in.
    /// </param>
    /// <param name="tonight">
    /// Every lot on that show whose hammer price is known — <see cref="Tonight"/>. Default is
    /// nothing, so a card built without a room book carries an unread block and no other change.
    /// </param>
    /// <param name="marketCeiling">
    /// What the comps said to stop at, before the seller's wallet was considered. The MARKET's
    /// figure on purpose: the recorded ceilings are market ceilings too
    /// (<see cref="Models.LivePassRequest.AsBid"/>), and comparing tonight's budget-capped number
    /// against them would report a thin wallet as a hot room.
    /// </param>
    public static LiveRoomRead Read(string? show, LiveRoomTonight tonight, decimal marketCeiling)
    {
        var name = (show ?? "").Trim();
        var read = new LiveRoomRead
        {
            ShowName = name,
            Ceiling = Math.Max(0m, marketCeiling),
        };

        var lots = tonight.Watched;
        read.Watched = lots.Count;
        read.Won = lots.Count(l => l.Won);

        // Nothing to say, in the two ways that happen. They are different sentences: a seller who
        // has not named the show can fix it in one keystroke, and a seller who has named it and
        // recorded nothing is being told what the button is for.
        if (name.Length == 0 || lots.Count == 0)
        {
            read.Verdict = LiveRoomVerdicts.Unread;
            (read.Headline, read.Note) = name.Length == 0
                ? ("No show named — the room isn't being measured",
                   "Name the show and this counts what its lots actually hammer for, against what the " +
                   "app said to stop at. Nothing is combined across shows: a room is one host's audience.")
                : ($"Nothing recorded yet on {name}",
                   "When a lot goes to somebody else, put what it went for in the bid box and press " +
                   "🔨 Went for. After three the strip says what this room clears at — which is the one " +
                   "thing on this card the comps cannot tell you.");
            return read;
        }

        // A ceiling of zero is a card that refused the lot outright — fees ate the whole resale
        // price, or nothing priced it. The hammer price is real and there is no line to measure it
        // against, so the lot is watched and never rated.
        var ratios = lots
            .Where(l => l.Ceiling > 0m && l.Hammer > 0m)
            .Select(l => l.Hammer / l.Ceiling)
            .OrderBy(r => r)
            .ToList();

        read.Rated = ratios.Count;
        read.OverCeiling = lots.Count(l => l.Ceiling > 0m && l.Hammer > l.Ceiling);

        if (ratios.Count == 0)
        {
            read.Verdict = LiveRoomVerdicts.Thin;
            read.Headline = $"{Lots(read.Watched)} watched on {name} — none of them had a ceiling";
            read.Note = "Every lot recorded here was one the comps refused to price, so there is no " +
                        "line to measure what the room paid against.";
            return read;
        }

        read.ClearingRatio = Median(ratios);
        read.ClearingPercent = (int)Math.Round(read.ClearingRatio * 100m, MidpointRounding.AwayFromZero);
        read.Readable = ratios.Count >= MinLotsToRate;

        if (read.Ceiling > 0m && read.Readable)
        {
            read.ExpectedHammer = Math.Round(read.Ceiling * read.ClearingRatio, 2);
            read.RoomOverExpected = Math.Round(read.Ceiling - read.ExpectedHammer, 2);
        }

        read.Verdict = !read.Readable ? LiveRoomVerdicts.Thin
            : read.ClearingRatio <= CheapRatio ? LiveRoomVerdicts.Cheap
            : read.ClearingRatio >= HotRatio ? LiveRoomVerdicts.Hot
            : LiveRoomVerdicts.Tight;

        Say(read, name);
        return read;
    }

    /// <summary>
    /// The four sentences. Written here, beside the count, for the reason every other block on this
    /// card writes its own: a sentence assembled in the browser out of <c>clearingPercent</c> and
    /// <c>ceiling</c> is a second opinion about money, and it is the one on screen.
    /// </summary>
    private static void Say(LiveRoomRead read, string show)
    {
        var split = read.Won > 0
            ? $"{Lots(read.Watched)} watched here — {read.Won} you won, {read.Watched - read.Won} to the room"
            : $"{Lots(read.Watched)} watched here, all to the room";

        // The thin state states the count and refuses the rate. It is the state every show starts
        // in and it is the one where a confident-looking percentage would do the most damage.
        if (!read.Readable)
        {
            read.Verdict = LiveRoomVerdicts.Thin;
            read.Headline = $"{Lots(read.Rated)} priced to the hammer on {show} — too few to be a rate";
            read.Note = Sentences(
                $"{split}.",
                Landed(read),
                $"{MinLotsToRate} rated lots is where this starts calling the room; " +
                "record the next one and it will.");
            return;
        }

        var over = read.OverCeiling > 0
            ? $" {read.OverCeiling} of {read.Rated} hammered above it."
            : " None of them hammered above it.";

        read.Headline = read.Verdict switch
        {
            LiveRoomVerdicts.Cheap =>
                $"This room clears at {read.ClearingPercent}% of your ceilings",
            LiveRoomVerdicts.Hot =>
                $"This room clears at {read.ClearingPercent}% of your ceilings — above them",
            _ => $"This room clears at {read.ClearingPercent}% of your ceilings — right at them",
        };

        var measured =
            $"{split}. The middle one went for {read.ClearingPercent}% of what the app said to stop " +
            $"at.{over}";

        read.Note = read.Verdict switch
        {
            LiveRoomVerdicts.Cheap => Sentences(measured, Landed(read),
                "There is daylight here — the ceiling above is what the lot is worth, not what it " +
                "takes to win it."),

            LiveRoomVerdicts.Hot => Sentences(measured, Landed(read),
                "Every ceiling on this screen is a true ceiling for a lot this room will outbid you " +
                "on. The evening is the thing being spent."),

            _ => Sentences(measured, Landed(read),
                "Winning here means bidding to the ceiling, so the margin is whatever your target " +
                "return was and no more."),
        };

        // One state warns, and it is the one where the card is entirely right and acting on it is
        // still a waste of the night. The cheap state is good news and good news belongs on the
        // strip; the tight state is the ordinary shape of a live auction and warning about it would
        // be warning about every show there is.
        if (read.Verdict != LiveRoomVerdicts.Hot) return;

        read.Warning =
            $"Lots on {show} have been hammering at {read.ClearingPercent}% of the ceilings this app " +
            $"gives — {read.OverCeiling} of the last {read.Rated} went above. Nothing is wrong with " +
            "this lot or its price; you are in a room that outbids it.";
    }

    /// <summary>
    /// Where this lot is likely to land, as a clause. Only stated when there is both a rate and a
    /// ceiling for it to be a share of — an expected hammer price on a card with no ceiling would be
    /// a projection off nothing.
    /// </summary>
    private static string Landed(LiveRoomRead read)
    {
        if (read.ExpectedHammer <= 0m) return "";

        return read.RoomOverExpected >= 0m
            ? $"On that record this one lands around {Money(read.ExpectedHammer)}, " +
              $"{Money(read.RoomOverExpected)} under your {Money(read.Ceiling)} ceiling."
            : $"On that record this one lands around {Money(read.ExpectedHammer)}, " +
              $"{Money(-read.RoomOverExpected)} past your {Money(read.Ceiling)} ceiling.";
    }

    /// <summary>Joins the clauses that are there and drops the ones that are not. A clause that is
    /// sometimes empty spliced in with a space is how a note ends up with a double space in it, on
    /// exactly the state a seller reads most often.</summary>
    private static string Sentences(params string[] parts) =>
        string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Lots, counted in words, because "1 lots" on a live card is the sort of thing that
    /// makes a seller distrust the figure beside it.</summary>
    public static string Lots(int count) => count == 1 ? "1 lot" : $"{count} lots";

    /// <summary>The middle of an already-sorted list. Averaged across the two middles on an even
    /// count, which is the median every other screen in this app means by the word.</summary>
    private static decimal Median(List<decimal> sorted) =>
        sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2m;

    /// <summary>Every dollar figure on this strip, in one place — cents only when there are any,
    /// the same rule <see cref="LiveBudget"/> follows.</summary>
    private static string Money(decimal amount) =>
        amount == Math.Truncate(amount) ? amount.ToString("C0") : amount.ToString("C");
}
