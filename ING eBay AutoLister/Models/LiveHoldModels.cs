namespace ING_eBay_AutoLister.Models;

// ── What the queue costs (see Services/LiveHoldCost.cs) ───────────────────────────────────────
//
// LiveStockDepth answers "how many of these would I then have, and how long does that take to
// clear". It deliberately takes nothing off any price, and its reasoning was right at the time: a
// "you already have three" haircut is a number nobody measured, and the fourth one really does
// resell for what the comps say — it just sells in April.
//
// The half of that sentence nobody priced is APRIL. "It sells for what the comps say" is only true
// if the comps still say it in April, and the card already measures whether they will:
// LiveTrendRead.SlopePerMonth is a Theil-Sen line in dollars per month across every dated sale.
// Multiply a measured slide by a measured wait and the result is not a rule of thumb about
// duplicates — it is two figures already on the card, multiplied together.
//
// So this is the missing haircut, and it is emphatically NOT "you have three of them". A pile of a
// product whose price is flat costs nothing here, however deep. What costs is holding a SLIDING
// product long enough for the slide to happen to it.

/// <summary>The seven answers. See <see cref="Services.LiveHoldCost"/> for the gates between
/// them — only the last one is allowed to touch a price.</summary>
public static class LiveHoldVerdicts
{
    /// <summary>One of it, and nothing of it ahead of it. There is no queue, so there is no wait
    /// to price. Nearly every card.</summary>
    public const string Solo = "solo";

    /// <summary>A queue, but a short one — this lot reaches the front well inside the bar.</summary>
    public const string Quick = "quick";

    /// <summary>A real wait, and no dated sold history to say what the price does across it.</summary>
    public const string Blind = "blind";

    /// <summary>A real wait, measured, and the price is not falling. Nothing taken off — and
    /// nothing added when it is climbing, which is the same asymmetry the trend read has.</summary>
    public const string Steady = "steady";

    /// <summary>A real wait and an apparent slide, on a reading too thin to price against.</summary>
    public const string Unsure = "unsure";

    /// <summary>A real wait and a confirmed measured slide. The one state that cuts.</summary>
    public const string Priced = "priced";

    /// <summary>Nothing could be worked out — no clearance rate, or no resale price to erode.</summary>
    public const string None = "none";
}

/// <summary>
/// How long this lot's units wait behind everything already queued in front of them, and what the
/// measured price slide does to them over that wait. See <see cref="Services.LiveHoldCost"/>.
/// </summary>
/// <remarks>
/// Present on every card, including the ordinary "one of them and nothing on the shelf" — a block
/// that only appears once the app found something to charge for is a block whose absence means both
/// "there is no queue" and "nothing looked", and only one of those is safe to press bid on.
/// </remarks>
public sealed class LiveHoldRead
{
    /// <summary>True when the wait could be worked out at all — including when it is genuinely
    /// zero. False means no clearance rate came back, so the queue could not be turned into months;
    /// it is not an error, and it must never be read as "no wait".</summary>
    public bool Readable { get; set; }

    /// <summary>solo | quick | blind | steady | unsure | priced | none. See
    /// <see cref="LiveHoldVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveHoldVerdicts.None;

    /// <summary>The strip's one line, in this item's own numbers.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind the headline, said out loud.</summary>
    public string Note { get; set; } = "";

    /// <summary>What the ceiling below was priced at, and why. Said in every state, including the
    /// ones that took nothing off — a money line that only appears when money moved is a money line
    /// whose silence means both "nothing to charge" and "never looked".</summary>
    public string MoneyNote { get; set; } = "";

    /// <summary>The sentence for the card's warning list. Empty unless the wait crossed a bar with
    /// a slide under it — this block is silent on the ordinary card by design.</summary>
    public string Warning { get; set; } = "";

    // ── The queue ────────────────────────────────────────────────────────────
    /// <summary>Units in the lot on screen. Never below 1.</summary>
    public int LotUnits { get; set; } = 1;

    /// <summary>Units of this product already queued in front of it — the shelf plus tonight's
    /// wins. The same two counts <see cref="LiveStockRead"/> shows as bars.</summary>
    public int UnitsAhead { get; set; }

    /// <summary>Everything, once this lot is won.</summary>
    public int UnitsAfter { get; set; }

    /// <summary>How many of these eBay clears a month. Zero means nothing dated said.</summary>
    public decimal MonthlySales { get; set; }

    // ── The wait ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Months this lot's AVERAGE unit waits beyond a unit with nothing in front of it. Null when
    /// there is no clearance rate to work it out from.
    /// </summary>
    /// <remarks>
    /// The average of the lot's own slice of the queue, not the whole pile's clearing time: the
    /// seller is deciding about THIS lot, and its units are the ones at the back.
    /// </remarks>
    public decimal? WaitMonths { get; set; }

    /// <summary>The wait actually priced — <see cref="WaitMonths"/> held at
    /// <see cref="Services.LiveHoldCost.MaxProjectedMonths"/>.</summary>
    public decimal? ProjectedMonths { get; set; }

    /// <summary>True when the wait ran past what the slide's evidence can be projected across, so
    /// the figure below is knowingly the gentle one.</summary>
    public bool Capped { get; set; }

    // ── The slide ────────────────────────────────────────────────────────────
    /// <summary>Dollars a month coming off the price, as a POSITIVE figure. Null unless a falling
    /// line was measured. Straight from <see cref="LiveTrendRead.SlopePerMonth"/>, negated.</summary>
    public decimal? DeclinePerMonth { get; set; }

    /// <summary>What the wait costs one unit: the slide times the projected months.</summary>
    public decimal? ErosionPerUnit { get; set; }

    // ── What it did to the money ─────────────────────────────────────────────
    /// <summary>The ratio applied to the three prices the ceiling is built from. Exactly 1 in every
    /// state but <see cref="LiveHoldVerdicts.Priced"/>.</summary>
    public decimal ResaleMultiplier { get; set; } = 1m;

    /// <summary>True when the ceiling was actually cut for the wait.</summary>
    public bool Discounted { get; set; }

    /// <summary>How much came off, as a percentage, for the tile beside the resale price.</summary>
    public decimal CutPercent { get; set; }

    /// <summary>True when the projection measured further than
    /// <see cref="Services.LiveHoldCost.MaxHaircutPercent"/> and the cut was held at it.</summary>
    public bool Floored { get; set; }
}
