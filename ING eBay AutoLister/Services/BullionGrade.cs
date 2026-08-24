using System.Text.RegularExpressions;

namespace ING_eBay_AutoLister.Services;

/// <summary>Which side of the bullion line a title falls on.</summary>
public enum BullionGrade
{
    /// <summary>The title never names a precious metal. Nothing here applies.</summary>
    NotMetal,

    /// <summary>Names one, but says nothing that settles solid metal against a plated surface.</summary>
    Unknown,

    /// <summary>States a weight or a hallmark and no plating language — the metal IS the product.</summary>
    Solid,

    /// <summary>The metal is a surface: plated, clad, filled, foil, layered, a replica or a tribute.</summary>
    Novelty,
}

/// <summary>
/// Solid precious metal and a plated souvenir of it are two different markets that share every word.
/// </summary>
/// <remarks>
/// <para>
/// <b>The failure.</b> A board row for <i>1 OZ Gold USA 100 Dollar Bullion Bar</i> came back at
/// <b>$6.99</b> off two sold comps. Every word in that title also appears on the novelty bars —
/// gold-clad brass stamped like a banknote — that sell for the price of a sandwich, so the comp
/// search matched them and the estimate was theirs. An ounce of gold was $4,604 that day.
/// </para>
/// <para>
/// Which of the two that particular row actually was is not the interesting question, and this
/// class deliberately does not answer it. Both readings are a disaster, in opposite directions:
/// </para>
/// <list type="bullet">
///   <item>real bar, novelty comps → the app tells the owner a $4,600 bar resells for $6.99, and
///   a genuine find is passed over as junk;</item>
///   <item>novelty bar, bullion comps → the app tells the owner a $7 souvenir resells for $4,600
///   and to bid up to two thousand for it. This is the direction that empties a bank account.</item>
/// </list>
/// <para>
/// So the rule is not "detect fakes". It is <b>price each population off its own kind</b>: a title
/// that states solid metal is never compared with one that states a coating, in either direction.
/// Where a title settles nothing — plain "gold ring", no weight, no hallmark — the grade is
/// <see cref="BullionGrade.Unknown"/> and nothing is excluded, because a guess here costs more
/// than a miss.
/// </para>
/// <para>
/// <b>On the vocabulary.</b> Two words that look obvious are deliberately absent. Bare "tone" is
/// not a marker: a <i>toned</i> Morgan dollar is solid silver whose surface has coloured with age,
/// and it is worth more for it, so only the compound forms ("goldtone", "silver tone") count.
/// Bare "plate" is only a soft marker: "Sterling Silver Plate" is usually silverplate flatware but
/// is sometimes a solid sterling dish, so a stated hallmark overrides it, while "plated" — which
/// is never anything else — does not care what else the title claims.
/// </para>
/// <para>
/// Pure, static and total: it takes a title and returns a verdict, so both sides of a comparison
/// can be graded from the raw text without a service, a network call or a spot price.
/// </para>
/// </remarks>
public static partial class Bullion
{
    // Only titles that name one of these are graded at all — every marker below is ambiguous or
    // outright wrong on an item that has nothing to do with precious metal ("Space Gray", "clad
    // brake rotor", "silver Ford Focus"), and the metal word is what makes them safe to read.
    private static readonly string[] Metals = ["gold", "silver", "platinum", "palladium"];

    // "The metal is a coating." Absolute: a stated karat does not make a plated chain solid — it
    // describes the plating. This is the more expensive direction to get wrong, so it wins.
    private static readonly string[] Coating =
    [
        "plated", "plating", "electroplate", "electroplated", "goldplate", "silverplate",
        "gold filled", "silver filled", "gold-filled", "gold fill", "rolled gold", "hge",
        "vermeil", "clad", "layered", "dipped", "overlay", "flashed",
        "goldtone", "silvertone", "gold tone", "silver tone", "gold-tone", "silver-tone",
        "gold foil", "silver foil", "gold leaf", "24k foil", "gold colored", "gold coloured",
        "replica", "novelty", "tribute", "fantasy", "imitation", "faux", "not real",
        "gold plated", "silver plated",
    ];

    // "The metal is a coating, unless the title also states a hallmark." A sterling serving plate
    // is solid; silverplate flatware is not; both are written "silver plate".
    private static readonly string[] SoftCoating = ["gold plate", "silver plate"];

    // A weight in any unit a dealer writes. Solid metal is sold by weight — a souvenir almost
    // never states one, because its weight is brass.
    [GeneratedRegex(@"\b\d+(?:\.\d+)?\s*(?:grams?|gm?s?\b|g\b|ozt\b|oz\.?t\b|troy\s*ounces?|ounces?|oz\b|dwt\b|pennyweights?|grains?|kilograms?|kg\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeightRx();

    // A stated purity: 14k / 18kt / .999 / 925 / sterling.
    [GeneratedRegex(@"\b(?:10|12|14|18|21|22|24)\s*(?:k|kt|karat|carat)\b|(?<![\d.])\.9\d{2,3}(?![\d])|\b9(?:25|99|58|50)\b|\bsterling\b|\bfine\s+(?:gold|silver|platinum)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HallmarkRx();

    /// <summary>What the title says the metal in this item is.</summary>
    public static BullionGrade Grade(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return BullionGrade.NotMetal;

        var text = " " + title.ToLowerInvariant().Replace('⁄', '/') + " ";
        if (!Metals.Any(m => text.Contains(m, StringComparison.Ordinal)))
            return BullionGrade.NotMetal;

        if (Coating.Any(c => text.Contains(c, StringComparison.Ordinal)))
            return BullionGrade.Novelty;

        var hallmarked = HallmarkRx().IsMatch(text);

        if (!hallmarked && SoftCoating.Any(c => text.Contains(c, StringComparison.Ordinal)))
            return BullionGrade.Novelty;

        // Solid needs the title to commit to something checkable — a weight or a hallmark. "Gold
        // ring" commits to nothing and stays Unknown, which excludes no comps at all.
        return hallmarked || WeightRx().IsMatch(text) ? BullionGrade.Solid : BullionGrade.Unknown;
    }

    /// <summary>
    /// True when these two titles describe metal of opposite kinds — one solid, one a coating.
    /// </summary>
    /// <remarks>
    /// Both sides must have committed to something. Solid-vs-Unknown is not a conflict: a comp
    /// whose title is merely vaguer than the target's is still evidence, and throwing it away
    /// leaves rows with no price at all.
    /// </remarks>
    public static bool Conflict(BullionGrade a, BullionGrade b) =>
        (a is BullionGrade.Solid && b is BullionGrade.Novelty)
        || (a is BullionGrade.Novelty && b is BullionGrade.Solid);
}
