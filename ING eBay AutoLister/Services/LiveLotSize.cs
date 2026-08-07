using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// How many things is the lot on screen? Read off its own name, and only ever from a count the
/// name actually states.
/// </summary>
/// <remarks>
/// <para>
/// Every number the live card has ever shown is a <b>per-unit</b> number. The sold comps behind it
/// are normalised to one unit by <see cref="MarketPriceEstimator"/> — "lot of 10 sold at $500" is a
/// $50 comparable — so the resale price, the break-even and the ceiling are all the answer for one
/// of the thing. That is right for the lot a live show sells most often and silently wrong for the
/// one it sells second-most often:
/// </para>
/// <code>
///   3x Bitmain Antminer S9        (4) Goldshell Mini Doge      LOT OF 5 GPU RISERS
/// </code>
/// <para>
/// A ceiling of $48 on a lot of three $48 miners is not a small error. It is the error that makes
/// the seller pass on the best lot of the night, every time one comes up, while the card sits there
/// looking exactly as confident as it does on a single unit.
/// </para>
/// <para>
/// <b>Stated counts only.</b> The two mistakes are not symmetrical. Reading a count that is not
/// there multiplies the ceiling by a number nobody wrote down and the seller overpays; missing one
/// leaves the ceiling where it has always been and they pass on a good lot. So a word that means
/// "more than one" without saying how many — <i>bundle</i>, <i>mystery lot</i>, <i>pair</i> — never
/// produces a count here. It produces a <see cref="LiveLotUnits.CountUnstated"/> prompt, and the
/// seller types the number the host just said out loud.
/// </para>
/// <para>
/// <b>Deliberately stricter than <see cref="LiquidationParser.ReadUnits"/></b>, which reads the same
/// idea off a liquidation listing. That one reads "36 pack", "24 ct" and "12 count" as counts, and
/// on a pallet of dish soap it is right. On a live show it would be catastrophic, because live
/// shows sell trading cards, where <i>pack</i>, <i>box</i> and <i>case</i> are the names of the
/// products: a "36 pack booster box" is one box, and multiplying its ceiling by thirty-six is how
/// this feature would get somebody to bid $7,000 on a $200 item. The pattern that is safe in both
/// places — <see cref="LiquidationSelectors.LeadingCount"/>, a bracketed count in front of the
/// product — is shared rather than copied; the vocabulary that is not safe is refused here, and a
/// test pins the divergence against that exact booster box.
/// </para>
/// <para>Pure. Nothing here prices anything — it returns a count and the sentences that explain it,
/// and <see cref="LiveBidAdvisor"/> does the money.</para>
/// </remarks>
public static partial class LiveLotSize
{
    /// <summary>
    /// The largest count worth believing off a live lot's name. Far below
    /// <see cref="LiquidationSelectors.MaxCredibleUnits"/> (500), because this is one seller holding
    /// the lot up to a camera rather than a pallet on a dock — above this the figure is a model
    /// number, a wattage or a card count that a count pattern caught by accident, and the seller can
    /// still type it by hand.
    /// </summary>
    public const int MaxCredibleUnits = 60;

    /// <summary>Where the count came from. The screen says which, every time.</summary>
    public const string SourceSingle = "single";
    public const string SourceTitle = "title";
    public const string SourceSeller = "seller";

    /// <summary>
    /// "3x", "x3", "3 x" — the dominant multi-unit convention on a live feed, where the seller is
    /// typing a lot name one-handed between lots.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both boundaries matter. <c>1080x1920</c>, <c>4x4</c> and <c>2x4</c> are not counts and none
    /// of them match, because a digit on the far side of the x is not a word boundary.
    /// </para>
    /// <para>
    /// The leading boundary is "not a letter, digit or decimal point", written as a lookbehind so it
    /// is not part of the evidence quoted back to the seller. It was a list of the punctuation a
    /// count is usually written after, and a live seller decorates the front of a lot name — a
    /// <c>🔥3x</c> was read as one item, on exactly the lot this reader exists for. The decimal point
    /// stays excluded on purpose: <c>1.5x</c> is a magnification, not one and a half of something.
    /// </para>
    /// </remarks>
    [GeneratedRegex(@"(?<![\w.])(\d{1,3})\s*x(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex CountXRegex();

    [GeneratedRegex(@"(?<![\w.])x\s*(\d{1,3})(?![\w])", RegexOptions.IgnoreCase)]
    private static partial Regex XCountRegex();

    /// <summary>
    /// "lot of 5", "set of 3", "bundle of 4".
    /// </summary>
    /// <remarks>
    /// The same idea as <see cref="LiquidationSelectors.CountOf"/> with two of its nouns removed:
    /// <c>case of N</c> and <c>box of N</c>. On a pallet listing those state a quantity; on a live
    /// show "box of 24" is a sealed trading-card box, which is <b>one</b> item whose comps are comps
    /// for that box. Both still reach the seller — as a prompt, through
    /// <see cref="BulkWithoutCountRegex"/> — rather than as a silent multiplication by 24.
    /// </remarks>
    [GeneratedRegex(@"\b(?:lots?|sets?|bundles?)\s+of\s+(\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountOfRegex();

    /// <summary>"3 pcs", "4 pieces", "6 units". Not "pack", "box", "case" or "count" — see the type
    /// remarks for what those words name on a live show.</summary>
    [GeneratedRegex(@"\b(\d{1,3})\s*[-\s]?(?:pcs?|pieces?|units?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountPiecesRegex();

    /// <summary>
    /// Words that turn an "N x" into a specification rather than a count. A 16x PCIe riser and a
    /// 10x zoom lens are one item each, and both are things a live electronics show sells.
    /// </summary>
    [GeneratedRegex(@"\b(?:zoom|optical|magnificat\w*|pcie|pci-?e|riser|slot|speed|dvd|cd|blu-?ray|burner|drive|scope)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecXContextRegex();

    /// <summary>
    /// "It's several of them" said without a number. Every one of these is a prompt, never a count.
    /// </summary>
    /// <remarks>
    /// <c>pair</c> is in here rather than being read as two on purpose: a pair of AirPods is one
    /// product and a pair of speakers is two, and this cannot tell them apart. The host can, and
    /// they are talking right now.
    /// </remarks>
    /// <remarks>
    /// <c>lot</c> is read as bulk wording anywhere in the name — "MYSTERY MINER LOT" is how a live
    /// seller writes it — except when a number follows it, which is a lot <i>number</i>: "Lot 12:
    /// Antminer S9" is one miner and the twelfth thing to come up tonight.
    /// </remarks>
    [GeneratedRegex(@"\b(?:lots?\b(?!\s*\d)|bundles?(?:\s+of)?|sets?\s+of|grab\s+bag|"
        + @"pairs?(?:\s+of)?|multiple|several|assorted|"
        + @"cases?\s+of|boxes?\s+of)\b", RegexOptions.IgnoreCase)]
    private static partial Regex BulkWithoutCountRegex();

    /// <summary>
    /// What the lot's name says it is, plus the seller's own answer when they gave one.
    /// </summary>
    /// <param name="title">The lot name the comps were looked up on.</param>
    /// <param name="sellerCount">
    /// The Qty box. Outranks everything read off the name — the seller can hear the host, and this
    /// reader cannot.
    /// </param>
    public static LiveLotUnits Read(string? title, int? sellerCount)
    {
        var text = (title ?? "").Trim();

        // The seller's own number wins, including their "1": that is the undo for a count this
        // reader got wrong, and it has to outrank the reader that got it wrong.
        if (sellerCount is int typed && typed >= 1)
        {
            var count = Math.Min(typed, MaxCredibleUnits);
            return new LiveLotUnits
            {
                Count = count,
                Source = SourceSeller,
                Evidence = "",
                // Worded as "the box says so" rather than "you said so": the box is also where the
                // screen carries a count forward when the lot's NAME changes under it — taking the
                // photo's name for a lot the title had counted — and a sentence claiming the seller
                // typed something they did not is the kind of small lie that costs a screen its
                // credibility on the one night it matters.
                Note = count > 1
                    ? $"Priced as {count} units — the quantity box says so. Every figure below is for the whole lot."
                    : "Priced as a single item — the quantity box says 1, which outranks the lot's name.",
                CountUnstated = false,
                Refused = typed > MaxCredibleUnits
                    ? $"{typed:N0} is more than this screen will price in one lot — capped at {MaxCredibleUnits}. "
                      + "A lot that big is a pallet, and the Liquidation Lot Analyzer is the screen for it."
                    : "",
            };
        }

        var (stated, evidence) = ReadStated(text);

        if (stated > MaxCredibleUnits)
        {
            // A number that large in a lot name is a spec — 104TH, 3080, 12000mAh — that a count
            // pattern caught. Priced as one, and said out loud, because silently pricing it as one
            // is indistinguishable from not having looked.
            return new LiveLotUnits
            {
                Count = 1,
                Source = SourceSingle,
                Evidence = evidence,
                Note = "Priced as a single item.",
                CountUnstated = false,
                Refused = $"\"{evidence}\" reads as {stated:N0} units, which is a model number or a spec rather "
                    + $"than a quantity — so it was ignored. If it really is {stated:N0} of them, set the quantity.",
            };
        }

        if (stated > 1)
        {
            return new LiveLotUnits
            {
                Count = stated,
                Source = SourceTitle,
                Evidence = evidence,
                Note = $"Priced as {stated} units — \"{evidence}\" in the lot's name. "
                     + "Every figure below is for the whole lot. Not a lot? Set the quantity to 1.",
                CountUnstated = false,
            };
        }

        if (BulkWithoutCountRegex().IsMatch(text))
        {
            // Before asking, check for a count stated somewhere the patterns above do not reach:
            // "LOT OF (2) Antminer S9" says exactly how many it is. Safe only inside bulk wording —
            // in an arbitrary name a bracketed number could be anything — which is the same rule,
            // and the same regex, LiquidationParser.ReadUnits reaches this way.
            var bracketed = LiquidationSelectors.BracketedCount.Match(text);
            if (bracketed.Success && int.TryParse(bracketed.Groups[1].Value, out var inside)
                && inside > 1 && inside <= MaxCredibleUnits)
            {
                return new LiveLotUnits
                {
                    Count = inside,
                    Source = SourceTitle,
                    Evidence = bracketed.Value.Trim(),
                    Note = $"Priced as {inside} units — \"{bracketed.Value.Trim()}\" inside the lot's name. "
                         + "Every figure below is for the whole lot. Not a lot? Set the quantity to 1.",
                    CountUnstated = false,
                };
            }

            // Bulk wording with no figure in it. This is the case the seller can fix in one
            // keystroke and nothing else on the screen can fix at all, so it asks rather than
            // guessing.
            return new LiveLotUnits
            {
                Count = 1,
                Source = SourceSingle,
                Evidence = "",
                Note = "Priced as a single item.",
                CountUnstated = true,
                UnstatedNote = "This reads as more than one item, but the name never says how many — so it is "
                    + "priced as ONE, and the ceiling below is the ceiling for one. Ask the host how many are "
                    + "in it and set the quantity; the card re-answers without re-reading eBay.",
            };
        }

        return new LiveLotUnits
        {
            Count = 1,
            Source = SourceSingle,
            Evidence = "",
            Note = "Priced as a single item.",
            CountUnstated = false,
        };
    }

    /// <summary>
    /// The count the name states, and the text that stated it. Zero when it states none.
    /// </summary>
    /// <remarks>
    /// Ordered most explicit first, and the first match wins rather than the largest: a name that
    /// says "(2)" at the front and mentions "3 pcs" of something else in the middle is describing
    /// two of something, and the leading bracket is the convention that means the lot.
    /// </remarks>
    private static (int Count, string Evidence) ReadStated(string text)
    {
        if (text.Length == 0) return (0, "");

        // "(4) Goldshell Mini Doge" — measured as the dominant convention at auction, and it reads
        // the same on a live feed.
        var leading = LiquidationSelectors.LeadingCount.Match(text);
        if (leading.Success && int.TryParse(leading.Groups[1].Value, out var front) && front > 0)
            return (front, leading.Value.Trim());

        // "lot of 5", "set of 3", "bundle of 4".
        var countOf = CountOfRegex().Match(text);
        if (countOf.Success && int.TryParse(countOf.Groups[1].Value, out var many) && many > 0)
            return (many, countOf.Value.Trim());

        foreach (var match in new[] { CountXRegex().Match(text), XCountRegex().Match(text) })
        {
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var times) || times <= 0) continue;
            // "16x PCIe riser" is one riser. A count next to a word that makes it a specification
            // is not read at all rather than read and warned about — the warning would be on the
            // card that already multiplied the money by sixteen.
            if (SpecXContextRegex().IsMatch(text)) continue;
            return (times, match.Value.Trim());
        }

        var pieces = CountPiecesRegex().Match(text);
        if (pieces.Success && int.TryParse(pieces.Groups[1].Value, out var pcs) && pcs > 0)
            return (pcs, pieces.Value.Trim());

        return (0, "");
    }

    /// <summary>
    /// How long it takes to sell all of them, and whether that is a sentence worth saying.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the honest cost of a multi-unit lot, and it is the only one this screen charges for.
    /// Five identical items do not sell five times as fast as one — they queue behind each other in
    /// the same demand — so the money is in it for longer, and the ceiling on the card says nothing
    /// about that.
    /// </para>
    /// <para>
    /// No haircut is taken off the resale price for it. A "multi-unit discount" would be a number
    /// nobody measured, quietly shading the one figure on the card that comes from real sales; the
    /// time is measurable from the same sell-through data the card already shows, so the time is
    /// what gets said.
    /// </para>
    /// </remarks>
    public static (decimal? MonthsToClear, int? DaysToSellAll, string Note) Absorption(
        int units, decimal monthlySales, int? daysToSellOne)
    {
        if (units <= 1) return (null, daysToSellOne, "");

        if (monthlySales <= 0m)
        {
            return (null, null,
                $"No dated sold history, so there is no way to say how long {units} of them take to sell. "
                + "That is the risk on a multi-unit lot: they queue behind each other in the same demand.");
        }

        var months = Math.Round(units / monthlySales, 1);
        // The last unit's wait, not the first: the first one sells at the market's ordinary speed,
        // and each one after it waits for the demand the one before it used up.
        var days = (int)Math.Ceiling((daysToSellOne ?? 0) + (units - 1) / monthlySales * 30m);

        var note = $"About {monthlySales:0.#} of these sell a month on eBay, so {units} of them is roughly "
                 + $"{Months(months)} of selling — the last one is cash in about {days} days, not {daysToSellOne ?? 0}.";

        return (months, days, note);
    }

    /// <summary>Long enough that the money being tied up is the thing worth saying out loud.</summary>
    public const decimal SlowClearanceMonths = 2m;

    private static string Months(decimal months) => months switch
    {
        < 1m => "under a month",
        < 1.5m => "about a month",
        _ => $"about {months:0.#} months",
    };
}
