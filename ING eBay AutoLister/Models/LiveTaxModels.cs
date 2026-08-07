namespace ING_eBay_AutoLister.Models;

// ── The last un-costed part of winning a lot (see Services/LiveSalesTax.cs) ───────────────────
//
// Every dollar on the live card hangs off a landed cost, and for fifteen sessions that cost has been
// three things: the bid, the platform's premium, and getting it delivered. A live marketplace in the
// United States charges a fourth. It is a marketplace facilitator, so it collects the buyer's state
// and local sales tax on the order and remits it — the seller does not get to decline it at checkout,
// and it lands on the same card as the bid.
//
// It is not small. The premium this card already charges is around 8%; combined sales tax across the
// US averages about 7.5% and runs past 10% in parts of Louisiana, Tennessee and Alabama. So the card
// has been leaving out a cost roughly the size of the largest one it charges, and leaving it out in
// the one direction that matters: a ceiling that is too high is a ceiling that says yes to a lot the
// seller loses money on.
//
// The one thing that makes it a read rather than a constant is that a reseller can be exempt. Whatnot
// takes a resale certificate, and a seller who has filed one pays no tax on anything they buy to
// resell — which is most of this app's users, on most of these lots. So this is a state, not a rate,
// and the state that charges nothing is a real answer rather than a missing one.

/// <summary>The four states. Spelled once so the strip, the CSS, the log and the tests agree.</summary>
public static class LiveTaxVerdicts
{
    /// <summary>Nothing was entered, so the ceiling is costing this lot as though no tax were
    /// charged on it. The most expensive silence left on the card.</summary>
    public const string None = "none";

    /// <summary>A resale certificate is on file with the platform, so nothing is charged. The
    /// commonest true answer for a reseller, and the reason this is a state and not a rate.</summary>
    public const string Exempt = "exempt";

    /// <summary>A rate of zero was entered — one of the five states that charges no sales tax at
    /// all. A stated zero is an answer; an empty box is not.</summary>
    public const string Free = "free";

    /// <summary>A real rate, charged on the hammer plus the premium. The one state that moves
    /// money.</summary>
    public const string Charged = "charged";
}

/// <summary>
/// What sales tax adds to winning the lot on screen, and what charging it takes off the ceiling. See
/// <see cref="Services.LiveSalesTax"/>.
/// </summary>
/// <remarks>
/// Present on every card, including the ones charged nothing. A block that only appeared once tax was
/// entered would be a block whose silence meant both "you are exempt" and "nobody ever asked".
/// </remarks>
public sealed class LiveTaxRead
{
    /// <summary>none | exempt | free | charged. See <see cref="LiveTaxVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveTaxVerdicts.None;

    /// <summary>True when the seller actually entered a rate. Zero is a real answer — five states
    /// charge no sales tax — which is why this is a flag and not a test for zero.</summary>
    public bool Stated { get; set; }

    /// <summary>True when the seller has said a resale certificate is on file with the platform.
    /// Outranks any rate entered: an exempt buyer is charged nothing whatever the state's rate is.</summary>
    public bool Exempt { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    /// <summary>The rate the ceiling was actually charged at. Zero in three of the four states, and
    /// zero is what makes those three cards identical to the cards this file did not exist for.</summary>
    public decimal RatePercent { get; set; }

    /// <summary>What the tax is charged ON: the bid plus the buyer's premium. Shipping is not in it
    /// — see <see cref="Services.LiveSalesTax"/> for why this app taxes the hammer and the premium
    /// and nothing else.</summary>
    public decimal TaxableBase { get; set; }

    /// <summary>The tax on the bid currently on screen, in cash. Zero before the bidding starts,
    /// because there is nothing yet to charge it on.</summary>
    public decimal Charged { get; set; }

    /// <summary>
    /// How much lower the ceiling is because of the tax, as a percentage of what it would otherwise
    /// have been.
    /// </summary>
    /// <remarks>
    /// Not the rate. The tax multiplies the whole landed cost, so the highest affordable bid divides
    /// by <c>1 + rate</c> — a 7.5% rate takes 6.98% off the ceiling, not 7.5%. Computed here rather
    /// than in the browser so the tag beside the strip and the ceiling under it cannot disagree.
    /// </remarks>
    public decimal CutPercent { get; set; }

    /// <summary>True when the ceiling on this card really is lower than it would have been.</summary>
    public bool Applied => RatePercent > 0m;

    /// <summary>
    /// The <see cref="LiveTaxVerdicts.None"/> state only: what tax at the US average rate would cost
    /// on the bid currently on screen. Zero everywhere else, and never charged to anything — it is
    /// the size of the silence, shown so the seller can see whether filling the box in is worth the
    /// four seconds.
    /// </summary>
    public decimal Exposure { get; set; }

    // ── The words ────────────────────────────────────────────────────────────
    /// <summary>The strip's one line — the state and its figure.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind the headline, and what to do about it when there is something
    /// to do.</summary>
    public string Note { get; set; } = "";

    /// <summary>The sentence that belongs on the card's warning list. Set in one state only —
    /// nothing entered — because that is the only one where the ceiling above is wrong.</summary>
    public string Warning { get; set; } = "";
}
