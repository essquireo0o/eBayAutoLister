using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// Turns the lot list off a live show — pasted, exactly as it was written by somebody who was not
/// thinking about a search box — into the titles the eBay sold-comp lookup can actually run on.
/// </summary>
/// <remarks>
/// <para>
/// The card answers "should I bid on the thing on screen". This answers the question before it:
/// <b>which of the next dozen lots should I be here for.</b> A live show publishes what is coming —
/// in the show notes, in the pinned comment, in the seller's own list — and every one of those lines
/// is written for a human: <c>"3) Bitmain Antminer S19j Pro 104TH — starting at $250"</c>. Handing
/// that to a comp search returns nothing, because the lot number and the asking price are not part
/// of what the thing is.
/// </para>
/// <para>
/// So the line is cleaned rather than the seller retyping twelve of them under time pressure, and
/// what is stripped off is kept where it is worth something: the price on the line becomes the lot's
/// opening bid, which is the one number the card needs and the one the seller would otherwise type
/// again.
/// </para>
/// <para>
/// Nothing here prices anything. Every lot goes through the same <c>/api/whatsnot/bid</c> the typed
/// item does — one path to a ceiling, one opinion per item. This is a parser, and the reason it is
/// C# rather than three lines of JavaScript is that "what did the app decide this line means" is a
/// question with money behind it, and it belongs somewhere it can be tested.
/// </para>
/// </remarks>
public static class LiveLotList
{
    /// <summary>
    /// How many lots a list is cut to. Deliberately <see cref="LiveBidBoard.Capacity"/> and not a
    /// number of its own: every lot priced here holds its comps on that board, and a thirteenth
    /// would evict the first one's while the seller was still deciding about it. A list that quietly
    /// went longer than the board would produce rows that stop being clickable in the order they
    /// were priced, which is the least explicable failure this screen could have.
    /// </summary>
    public const int MaxLots = LiveBidBoard.Capacity;

    /// <summary>How much pasted text is read at all. A lot list is a dozen lines; anything past this
    /// is a document, and reading all of it would spend the seller's seconds finding that out.</summary>
    public const int MaxCharacters = 40_000;

    /// <summary>How many lines are examined. See <see cref="MaxCharacters"/>.</summary>
    public const int MaxLines = 400;

    /// <summary>Shorter than this and there is nothing for a sold search to match on.</summary>
    public const int MinTitleLength = 3;

    // "3.", "12)", "Lot 3:", "#4 —", "No. 7 -". The number must be FOLLOWED by punctuation or be
    // introduced by a word, or "1975 Topps complete set" loses its year and "2 x Antminer S9" loses
    // the quantity — both of which change what the search is for.
    private static readonly Regex LotMarker = new(
        @"^(?:(?:lot|item|no\.?)\s*)?#?\s*\d{1,3}\s*[.)\]:\-–—]\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // The price at the END of the line. Leading prices are left alone on purpose: a "$100 Amazon
    // gift card" is named after its face value and stripping it would price the wrong thing.
    private static readonly Regex TrailingPrice = new(
        @"\$\s*(?<amt>\d[\d,]*(?:\.\d{1,2})?)\s*(?:usd)?\s*[)\]]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // What was holding the price on: "— starting at", "· now", "(", ",". Only ever run after a
    // price was actually removed, so "Xbox Series X Now" keeps its last word.
    private static readonly Regex PriceLeadIn = new(
        @"(?:[\s\-–—•|,:;(\[]+|\b(?:starting|opening|current|now|at|bid|bids|from|only|price|asking|for|go(?:es)?)\b)+$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);

    /// <summary>Characters that decorate a list and never name a thing. Trimmed off both ends.</summary>
    private const string Decoration = " \t -–—•*>»«·|~=_\"'“”";

    /// <summary>
    /// Reads a pasted lot list into the lots it names, in the order they were written.
    /// </summary>
    public static LiveLotPlan Parse(string? text)
    {
        var raw = text ?? "";
        if (raw.Length > MaxCharacters) raw = raw[..MaxCharacters];

        var lots = new List<LiveLot>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int duplicates = 0, unusable = 0, overflow = 0, read = 0;

        foreach (var line in raw.Split('\n'))
        {
            if (read >= MaxLines) break;
            var source = line.Replace("\r", "").Trim();
            if (source.Length == 0) continue;   // blank lines separate a list, they are not entries
            read++;

            var (title, opening) = Clean(source);

            if (title.Length < MinTitleLength || !title.Any(char.IsLetter)) { unusable++; continue; }
            if (!seen.Add(title)) { duplicates++; continue; }

            // Counted, not kept. The list is cut at the board's capacity, and the seller is told how
            // many were left off rather than the list quietly ending.
            if (lots.Count >= MaxLots) { overflow++; continue; }

            lots.Add(new LiveLot { Line = source, Title = title, OpeningBid = opening });
        }

        return new LiveLotPlan
        {
            Lots = lots,
            DuplicatesDropped = duplicates,
            UnusableDropped = unusable,
            OverflowDropped = overflow,
            Note = Describe(lots.Count, duplicates, unusable, overflow),
        };
    }

    /// <summary>
    /// One line, reduced to what it names and what it costs. Public because what the app decided a
    /// line meant is the thing worth testing about it.
    /// </summary>
    public static (string Title, decimal? OpeningBid) Clean(string line)
    {
        var work = Whitespace.Replace(line ?? "", " ").Trim(Decoration.ToCharArray());
        work = LotMarker.Replace(work, "", 1).Trim(Decoration.ToCharArray());

        decimal? opening = null;
        var priced = TrailingPrice.Match(work);
        if (priced.Success)
        {
            // A price that will not parse is left ON the line rather than silently discarded — if
            // the app could not read it, it has no business deciding it was not part of the name.
            if (decimal.TryParse(priced.Groups["amt"].Value.Replace(",", ""),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out var amount)
                && amount > 0m)
            {
                opening = amount;
                work = PriceLeadIn.Replace(work[..priced.Index], "", 1);
            }
        }

        return (work.Trim(Decoration.ToCharArray()), opening);
    }

    /// <summary>
    /// What happened to the paste, in words. Written here rather than in the browser so the reason
    /// the list stops at <see cref="MaxLots"/> has exactly one wording in the app.
    /// </summary>
    private static string Describe(int kept, int duplicates, int unusable, int overflow)
    {
        var parts = new List<string>
        {
            kept == 0
                ? "Nothing on that list to price."
                : $"{kept} lot{(kept == 1 ? "" : "s")} to price, in the order they were listed.",
        };

        if (duplicates > 0)
            parts.Add($"{duplicates} repeat{(duplicates == 1 ? "" : "s")} skipped.");

        if (unusable > 0)
            parts.Add($"{unusable} line{(unusable == 1 ? " had" : "s had")} nothing to search on.");

        if (overflow > 0)
            parts.Add($"{overflow} more than the {MaxLots} this holds — the comps behind a " +
                      $"{MaxLots + 1}th lot would push the first one's out while you were still " +
                      "deciding about it, so the list stops here.");

        return string.Join(" ", parts);
    }
}

/// <summary>One lot off a pasted list, before anything has been priced.</summary>
public sealed class LiveLot
{
    /// <summary>The line as it was pasted. Kept so the row can show what it was read FROM — a
    /// cleaned title that dropped the wrong half of a line is only findable next to its original.</summary>
    public string Line { get; init; } = "";

    /// <summary>What the sold search runs on.</summary>
    public string Title { get; init; } = "";

    /// <summary>The price that was on the line, when there was one. It is where the bidding starts,
    /// not what the lot is worth — the card decides that, off eBay.</summary>
    public decimal? OpeningBid { get; init; }
}

/// <summary>A pasted lot list, read. See <see cref="LiveLotList"/>.</summary>
public sealed class LiveLotPlan
{
    public List<LiveLot> Lots { get; init; } = [];

    /// <summary>Lines naming a lot already on the list. A show repeats itself and a list pasted
    /// twice is the commonest paste there is.</summary>
    public int DuplicatesDropped { get; init; }

    /// <summary>Lines with nothing searchable left after cleaning — a bare price, a row of
    /// asterisks, a lot number on its own.</summary>
    public int UnusableDropped { get; init; }

    /// <summary>Lots past <see cref="LiveLotList.MaxLots"/>.</summary>
    public int OverflowDropped { get; init; }

    /// <summary>All of the above in one sentence, for the screen.</summary>
    public string Note { get; init; } = "";

    /// <summary>The cap, so the screen never has to hard-code it.</summary>
    public int MaxLots => LiveLotList.MaxLots;
}
