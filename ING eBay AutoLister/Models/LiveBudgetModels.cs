namespace ING_eBay_AutoLister.Models;

// ── The money that is actually there (see Services/LiveBudget.cs) ─────────────────────────────
//
// Every read on the live card answers the same question — what is this thing WORTH — and each one
// has sharpened it: what these fetch lately, what they fetch in this shape, what they fetch when
// yours reaches the front of the queue, what the platform bills on top. Sixteen sessions of a card
// that assumes the seller can pay whatever the answer comes to.
//
// A live show is the one sourcing channel in this app where that assumption breaks inside an hour.
// The lots come every four minutes, each individually defensible, and the app says BID UP TO on all
// of them; six good calls in a row is a seller who has committed $1,400 they have not got, on stock
// that turns into cash in forty days. Nothing on the screen has ever counted the money leaving. The
// buy sheet knows exactly what it is — it is the number the bank statement will agree with — and
// until now it was only ever read AFTER the hammer.
//
// So this is the one cut on the card that is not about the item at all. Every other read lowers the
// ceiling because of something the sold comps said; this one lowers it because the cash is gone,
// and it says so in those words rather than letting the card claim the market refused the lot.

/// <summary>What tonight's buy sheet has already committed, all in. The count is carried too — a
/// seller who has spent their budget on one lot and one who spent it on eleven are looking at very
/// different nights.</summary>
public readonly record struct LiveBudgetTonight(int Lots, decimal Spent)
{
    /// <summary>Nothing bought yet — also what a card built without a sheet gets, which is what
    /// keeps every existing test's ceiling exactly where it was.</summary>
    public static readonly LiveBudgetTonight Nothing = new(0, 0m);
}

/// <summary>The four states. Spelled once so the strip, the speech, the CSS, the log and the tests
/// agree.</summary>
public static class LiveBudgetVerdicts
{
    /// <summary>No budget was set, so nothing is capped. The card is priced exactly as it was
    /// before this file existed — what the seller has already committed tonight is still reported,
    /// because that is a fact rather than an assumption.</summary>
    public const string None = "none";

    /// <summary>A budget is set and there is more left than the ceiling needs. Nothing is cut, and
    /// the strip says how much room is left — the state most of a good night is spent in.</summary>
    public const string Clear = "clear";

    /// <summary>The cash left is less than the item is worth, so the ceiling on the card is what
    /// the wallet allows and not what the comps say. The one state that moves money.</summary>
    public const string Capped = "capped";

    /// <summary>Nothing left to bid with — the budget is committed, or what remains will not even
    /// cover the freight. The ceiling is zero and the card says why in cash, not in comps.</summary>
    public const string Spent = "spent";
}

/// <summary>
/// What the seller has left to spend tonight, and what that does to the ceiling above it. See
/// <see cref="Services.LiveBudget"/>.
/// </summary>
/// <remarks>
/// Present on every card, including the ones with no budget set. A block that only appeared once a
/// budget was entered would be a block whose silence meant both "you have plenty" and "nobody is
/// counting".
/// </remarks>
public sealed class LiveBudgetRead
{
    /// <summary>none | clear | capped | spent. See <see cref="LiveBudgetVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveBudgetVerdicts.None;

    /// <summary>True when the seller actually set a budget for tonight. Nothing is assumed: no
    /// budget caps nothing, however much has been spent.</summary>
    public bool Stated { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    /// <summary>What the seller said they would spend tonight, as they entered it.</summary>
    public decimal Budget { get; set; }

    /// <summary>What tonight's buy sheet has already committed, all in — bid, premium, tax and
    /// freight on every row. The buy sheet's own <see cref="BuySheet.Spent"/>, never re-derived.</summary>
    public decimal Committed { get; set; }

    /// <summary>How many lots that was.</summary>
    public int LotsWon { get; set; }

    /// <summary>Budget minus committed, floored at zero. What is left to land a lot with.</summary>
    public decimal Remaining { get; set; }

    /// <summary>How much of the budget is gone, as a percentage. For the bar, never for a decision —
    /// the decision is made on <see cref="Remaining"/>, which is cash.</summary>
    public decimal SpentPercent { get; set; }

    /// <summary>
    /// The ceiling the market gave, before the cash was considered. Kept because it is the whole
    /// difference between "this lot is not worth it" and "this lot is worth it and you cannot afford
    /// it" — two sentences that send a seller in opposite directions.
    /// </summary>
    public decimal MarketCeiling { get; set; }

    /// <summary>The highest bid the remaining cash actually lands, with the premium, the tax and the
    /// freight already inside it. Zero when there is nothing left.</summary>
    public decimal Affordable { get; set; }

    /// <summary>The ceiling after the cash has had its say — the number on the badge.</summary>
    public decimal Ceiling { get; set; }

    /// <summary>How much lower the ceiling is because of the cash, as a percentage of the market's
    /// own. Zero in the two states that cut nothing.</summary>
    public decimal CutPercent { get; set; }

    /// <summary>True when the ceiling on this card is the wallet's rather than the market's — the
    /// one flag the advisor acts on. False whenever the market refused the lot on its own terms: a
    /// card with no ceiling to cut is not a card this read is allowed to explain.</summary>
    public bool Applied =>
        MarketCeiling > 0m && Verdict is LiveBudgetVerdicts.Capped or LiveBudgetVerdicts.Spent;

    /// <summary>True when the ceiling was lowered but there is still a bid to make.</summary>
    public bool Capped => Applied && Verdict == LiveBudgetVerdicts.Capped;

    /// <summary>True when there is nothing left to bid with at all.</summary>
    public bool Exhausted => Applied && Verdict == LiveBudgetVerdicts.Spent;

    // ── The words ────────────────────────────────────────────────────────────
    /// <summary>The strip's one line — the state and its figure.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind the headline, and what to do about it when there is something
    /// to do.</summary>
    public string Note { get; set; } = "";

    /// <summary>
    /// The badge's own sentence, in the two states where the cash decides the call. Written here
    /// rather than in <see cref="Services.LiveBidAdvisor.Judge"/> so one file owns every word this
    /// read says — a card that stops for money must never be read as a card the market refused.
    /// </summary>
    public string Reason { get; set; } = "";

    /// <summary>The sentence for the card's warning list, in the states where it is worth
    /// interrupting for.</summary>
    public string Warning { get; set; } = "";
}
