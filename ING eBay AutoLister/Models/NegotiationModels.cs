namespace ING_eBay_AutoLister.Models;

// ── Buy-side negotiation ──────────────────────────────────────────────────────
// Every other pricing screen in this app works on the SELL side: what to list it for, when to cut
// the price, what to offer a watcher. This is the other half, and it is the cheaper half — a dollar
// talked off the buy price is a dollar of profit with no fee, no shipping and no wait attached to
// it, whereas a dollar added to the sale price arrives ~85 cents late and net of eBay's cut.
//
// So this takes a local listing that has already been priced against real sold comps
// (LocalArbitrageAnalyzer) and answers the question the seller is actually standing in a driveway
// asking: what do I open at, what do I settle at, and at what number do I put my wallet away?
// See Services/NegotiationAdvisor.cs.

// One rung of the counter-offer ladder: a price someone might land on, and exactly what it leaves.
// This is the part that gets used mid-conversation — the seller counters with a number and the
// answer to "can I say yes to that?" has to be one glance, not arithmetic.
public class NegotiationRung
{
    public string Label { get; set; } = "";
    public decimal Price { get; set; }
    public decimal NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    // great | good | thin | loss — the same bars the arbitrage verdict uses, so a price called
    // "great" here is a price that would have been badged a goldmine there.
    public string Tone { get; set; } = "";
    // Marks the rungs that came from the plan rather than from the seller: the drafted opener, the
    // ceiling, and the other side's ask.
    public bool IsOpening { get; set; }
    public bool IsCeiling { get; set; }
    public bool IsAsk { get; set; }
    public bool IsBreakEven { get; set; }
}

// One draft the seller can copy and send as-is. Kept as a list rather than three named properties
// because which drafts apply depends on the verdict — a deal that is already under target gets one
// message, a deal that needs real movement gets the whole sequence.
public class NegotiationMessage
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    // When to send it, in the seller's words ("if they say no, or counter").
    public string When { get; set; } = "";
    public string Text { get; set; } = "";
}

public class NegotiationPlan
{
    // buy_now | negotiate | must_negotiate | long_shot | walk | no_data
    public string Verdict { get; set; } = "no_data";
    public string Headline { get; set; } = "";

    // ── The four numbers ─────────────────────────────────────────────────────
    public decimal AskPrice { get; set; }
    // What to open at: low enough to leave room, high enough to get a reply instead of a block.
    public decimal? OpeningOffer { get; set; }
    public int OpeningDiscountPercent { get; set; }
    // The most that can be paid and still have this be worth doing at all — the number to stop at.
    public decimal? CeilingPrice { get; set; }
    // The price at which this stops being a flip and becomes a favour: net profit exactly zero.
    public decimal BreakEvenPrice { get; set; }
    // The price that makes it a great buy rather than merely a profitable one (the goldmine bar).
    public decimal TargetPrice { get; set; }

    // ── What the negotiation is worth ────────────────────────────────────────
    // Ask minus opening offer: profit added if they simply say yes, on top of whatever the flip
    // was already worth. Zero fees, zero shipping, zero wait — which is the whole point.
    public decimal Upside { get; set; }
    public decimal? NetAtAsk { get; set; }
    public decimal? NetAtOpening { get; set; }

    // ── The evidence ─────────────────────────────────────────────────────────
    public decimal? ResalePrice { get; set; }
    public int CompCount { get; set; }
    // False when the sold history is too thin to quote a figure at a stranger. The drafts then lead
    // on cash-and-pickup instead of on a number that can't be stood behind.
    public bool CitesComps { get; set; }
    public string EvidenceNote { get; set; } = "";

    // Why the opening number is the number — the leverage the draft was built from.
    public List<string> Signals { get; set; } = [];
    public List<NegotiationRung> Ladder { get; set; } = [];
    public List<NegotiationMessage> Messages { get; set; } = [];
}

// A deal the seller found themselves, on a site this app doesn't scan. Same advice, pasted in by
// hand: POST /api/local/negotiate.
public class NegotiationRequest
{
    public string Title { get; set; } = "";
    public decimal AskPrice { get; set; }
    // What it sells for on eBay. Required unless BreakEvenBuyPrice is supplied.
    public decimal? ResalePrice { get; set; }
    // Net proceeds after every fee and shipping cost — i.e. the most that can be paid to break even.
    // Supplied by callers that already costed it; derived from ResalePrice and the fee profile when
    // it isn't, so both routes end at the same arithmetic.
    public decimal? BreakEvenBuyPrice { get; set; }
    public int SoldCompCount { get; set; }
    public int? DaysListed { get; set; }
    public int? DaysToCash { get; set; }
    public decimal? OriginalPrice { get; set; }
    public double? DistanceMiles { get; set; }
}
