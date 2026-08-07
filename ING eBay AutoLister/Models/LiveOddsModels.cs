namespace ING_eBay_AutoLister.Models;

// ── How often the evidence would actually have paid for this win (see Services/LiveOdds.cs) ───
//
// Every figure on the live card is a MIDDLE. The resale price is the expected sale, the spread is
// the middle half, the ceiling is what a bid has to stay under for the middle to clear a target —
// and a middle is a number half the sales came in under. That is fine on a board where the seller
// picks the best of forty rows and lives with the average; it is a different thing entirely when
// the seller is about to buy ONE object, once, in the next eleven seconds, and either that one
// resells above what winning cost or it does not.
//
// The card already holds the only evidence that can answer it: the actual sold rows the price was
// computed from. So this counts them. Each past sale is re-priced to what THIS one would fetch —
// the same stacked cut the ceiling was built with, so the two can never disagree — and compared
// against the one figure that decides the win: the price a unit has to reach to break even at the
// bid on screen. Nine of twelve cleared it, or three of twelve did.
//
// It moves as the bidding moves, which is the whole point. The ceiling is a line the bid crosses
// once; this is a number that falls every time somebody else raises, and a seller watching "9 of 12"
// become "4 of 12" is watching the arbitrage close in the unit that actually decides it.
//
// Nothing here prices anything, and nothing here moves a ceiling. It is a count of sales that
// really happened, against a break-even the app already computed.

/// <summary>The five answers. See <see cref="Services.LiveOdds"/> for the bars between them.</summary>
public static class LiveOddsVerdicts
{
    /// <summary>Most of the past sales cleared what winning costs.</summary>
    public const string Strong = "strong";

    /// <summary>Enough of them did to be worth the risk, and enough did not to say so.</summary>
    public const string Even = "even";

    /// <summary>Most of them did NOT. The ceiling is real and the middle is a long way above the
    /// bottom of the spread.</summary>
    public const string Long = "long";

    /// <summary>Too few sales to call a count of them odds. Reported anyway — three of four is
    /// still three of four — but never as a rate.</summary>
    public const string Thin = "thin";

    /// <summary>Nothing to count: no sold rows, or no break-even to measure them against.</summary>
    public const string None = "none";
}

/// <summary>
/// How many of the sold comps behind this card would have covered what winning it costs.
/// See <see cref="Services.LiveOdds"/> for what it refuses to claim.
/// </summary>
public sealed class LiveOddsRead
{
    /// <summary>True when there were priced sold rows AND a break-even to measure them against.
    /// False is not an error — it is the honest state of a card nothing could count.</summary>
    public bool Readable { get; set; }

    /// <summary>strong | even | long | thin | none. See <see cref="LiveOddsVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveOddsVerdicts.None;

    /// <summary>The strip's one line — the count and what it is a count of.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind it, said out loud.</summary>
    public string Note { get; set; } = "";

    /// <summary>The sentence for the card's warning list. Empty unless most of the past sales came
    /// in under what winning costs — this block is silent on a card that is fine.</summary>
    public string Warning { get; set; } = "";

    // ── The count ────────────────────────────────────────────────────────────
    /// <summary>Past sales that cleared <see cref="NeedPerUnit"/>.</summary>
    public int Covered { get; set; }

    /// <summary>Past sales counted. Every sold row with a price on it, not the five the table
    /// shows — the question is what the whole distribution did.</summary>
    public int Total { get; set; }

    /// <summary>Covered ÷ Total, whole percent. Zero when nothing was counted; a percentage is
    /// only CLAIMED when <see cref="Stated"/> is true.</summary>
    public int Percent { get; set; }

    /// <summary>True when there were enough sales for the percentage to mean anything — the same
    /// bar the app refuses to bid under. False leaves the count on screen and drops the rate.</summary>
    public bool Stated { get; set; }

    // ── What it was counted against ──────────────────────────────────────────
    /// <summary>What one unit has to fetch for this win to break even, after eBay's cut, the
    /// packing, the labour and posting it on. <see cref="ProfitBreakdown.BreakEvenSalePrice"/>,
    /// not a second arithmetic.</summary>
    public decimal NeedPerUnit { get; set; }

    /// <summary>The bid these odds are for.</summary>
    public decimal AtBid { get; set; }

    /// <summary>True when nobody has bid yet, so the count is for a win at the card's own ceiling
    /// rather than at a price on screen. Said out loud — the two mean different things.</summary>
    public bool AtCeiling { get; set; }

    /// <summary>What winning at <see cref="AtBid"/> costs per unit, all in — the bid, the premium,
    /// the tax and the freight, divided by the units in the lot.</summary>
    public decimal LandedPerUnit { get; set; }

    /// <summary>Units in the lot on screen. The count is per unit, because the sold rows are.</summary>
    public int LotUnits { get; set; } = 1;

    // ── The distribution the count came out of ───────────────────────────────
    /// <summary>The cheapest re-priced past sale.</summary>
    public decimal LowSale { get; set; }

    /// <summary>The middle one.</summary>
    public decimal TypicalSale { get; set; }

    /// <summary>The dearest.</summary>
    public decimal HighSale { get; set; }

    /// <summary>What each past sale was multiplied by to become "what yours would fetch" — the
    /// trend, condition and shelf-time cuts the ceiling above was built with, stacked. 1 when none
    /// of them fired, which is most cards.</summary>
    public decimal RepriceRatio { get; set; } = 1m;

    /// <summary>That ratio as a whole-percent cut, for the strip. Zero when nothing was cut.</summary>
    public int CutPercent { get; set; }

    /// <summary>True when the comps were re-priced at all.</summary>
    public bool Repriced => CutPercent > 0;
}
