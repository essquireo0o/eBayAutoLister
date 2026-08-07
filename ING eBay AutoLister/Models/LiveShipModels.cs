namespace ING_eBay_AutoLister.Models;

// ── What this lot actually costs to get delivered (see Services/LiveShipShare.cs) ─────────────
//
// Every figure on the live card is built on a landed cost: bid + buyer's premium + shipping. The
// first two are exact. The third has always been a single number typed once and charged in full to
// every lot of the night — and on a live show that is simply wrong, because a live seller does not
// post twelve parcels. They post ONE box, at the end, and charge a first-item rate plus a much
// smaller rate for each extra thing in it.
//
// So a seller who wins six lots from one show at "$12 shipping" is charged $12 + 5 × $1, and this
// app has been costing every one of those six ceilings as though it were the only lot of the night.
// The error is not small and it runs one way: it makes every lot after the first look worse than it
// is, which quietly talks a seller out of exactly the lots that are cheapest for them to win.
//
// The marginal cost of winning the lot on screen is the right number for a bid decision, and it is
// the number this produces. It is not a guess: the two rates are the show's own, entered by the
// seller, and combining only happens once the buy sheet can prove a lot from THIS show has already
// been won tonight. Without all three of those the ceiling is charged exactly what it was charged
// before this file existed.

/// <summary>
/// What tonight's buy sheet has already committed to one show's shipment. Counted on the server and
/// handed in, so <see cref="Services.LiveShipShare"/> reads two numbers rather than a file.
/// </summary>
/// <remarks>
/// Unlike <see cref="LiveStockTonight"/>, listed rows are deliberately <b>counted</b>. Drafting a
/// won lot into a listing changes nothing about the box it is coming in — the seller still has
/// those lots arriving from that show in one parcel, and a lot dropped from this count would put the
/// next ceiling back on full freight for no reason the seller could see.
/// </remarks>
/// <param name="Lots">Rows on tonight's sheet from this show.</param>
/// <param name="ShippingCharged">What those rows were charged for shipping, added up.</param>
public readonly record struct LiveShipTonight(int Lots, decimal ShippingCharged)
{
    /// <summary>Nothing won from this show tonight — also what a card built without a sheet gets,
    /// and what an unnamed show always gets.</summary>
    public static readonly LiveShipTonight Nothing = new(0, 0m);
}

/// <summary>The five states. Spelled once so the strip, the speech, the CSS and the tests agree.</summary>
public static class LiveShipVerdicts
{
    /// <summary>No shipping figure was entered at all, so the ceiling is charging none.</summary>
    public const string None = "none";

    /// <summary>Shipping is being charged in full, and nothing yet says it should not be — either
    /// the show is unnamed or its extra-item rate is.</summary>
    public const string Alone = "alone";

    /// <summary>Both rates are known and this is the first lot from this show tonight. It pays the
    /// whole first-item rate; everything after it will not.</summary>
    public const string First = "first";

    /// <summary>Both rates are known and a lot from this show is already on tonight's sheet, so this
    /// one rides along in the same box at the extra-item rate. The only state that moves money.</summary>
    public const string Combined = "combined";

    /// <summary>Lots from this show are already on the sheet and no extra-item rate was entered, so
    /// the ceiling is knowingly charging full freight a second time. Warned about, never guessed at.</summary>
    public const string Unstated = "unstated";
}

/// <summary>
/// What the lot on screen adds to the seller's shipping bill, as opposed to what one lot from this
/// show costs. See <see cref="Services.LiveShipShare"/> for the three things that have to be true
/// before the two are allowed to differ.
/// </summary>
/// <remarks>
/// Present on every card, including the ones where nothing was combined and the ones where nothing
/// was entered — a block that only appears once a saving was found is a block whose silence means
/// both "you are paying full freight" and "nothing looked".
/// </remarks>
public sealed class LiveShipRead
{
    /// <summary>True when a shipping figure was stated at all. False is not an error: it is the
    /// honest state of a card whose ceiling is charging nothing to get the thing delivered.</summary>
    public bool Readable { get; set; }

    /// <summary>none | alone | first | combined | unstated. See <see cref="LiveShipVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveShipVerdicts.None;

    /// <summary>The show, as the seller wrote it. Empty when they have not said which show this is,
    /// which is the state that makes combining impossible.</summary>
    public string ShowName { get; set; } = "";

    /// <summary>True when there is a show to match tonight's sheet against.</summary>
    public bool ShowNamed => ShowName.Length > 0;

    // ── The two rates ────────────────────────────────────────────────────────
    /// <summary>What the show charges to post the first thing you win. The Shipping box.</summary>
    public decimal FirstItemShipping { get; set; }

    /// <summary>What it charges for each additional thing in the same box. Zero is a real answer —
    /// free combined shipping is the commonest live-selling arrangement there is — which is why
    /// <see cref="AdditionalStated"/> is a separate flag and not a test for zero.</summary>
    public decimal AdditionalItemShipping { get; set; }

    /// <summary>True when the seller actually entered an extra-item rate. Nothing is ever assumed
    /// about it: an unstated rate leaves the ceiling on full freight.</summary>
    public bool AdditionalStated { get; set; }

    // ── Tonight, from this show ──────────────────────────────────────────────
    /// <summary>Lots already won from this show tonight, listed ones included.</summary>
    public int LotsWonFromShow { get; set; }

    /// <summary>What those lots were charged for shipping, added up — the shipping bill so far.</summary>
    public decimal ShippingSoFar { get; set; }

    /// <summary>What that bill becomes if this lot is won: <see cref="ShippingSoFar"/> plus
    /// <see cref="Marginal"/>.</summary>
    public decimal ShippingWithThisLot { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    /// <summary>What the ceiling above was actually charged to get this lot delivered. Equal to
    /// <see cref="FirstItemShipping"/> except in the <see cref="LiveShipVerdicts.Combined"/> state.</summary>
    public decimal Marginal { get; set; }

    /// <summary>First-item rate minus what was charged, floored at zero. What combining is worth on
    /// this lot, in dollars of extra room under the ceiling.</summary>
    public decimal Saved { get; set; }

    /// <summary>True when the ceiling on this card really is higher than it would have been.</summary>
    public bool Applied => Saved > 0m;

    // ── The words ────────────────────────────────────────────────────────────
    /// <summary>The strip's one line — the state and its figure, in this show's own numbers.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind the headline, said out loud, with what to do about it when
    /// there is something to do.</summary>
    public string Note { get; set; } = "";

    /// <summary>The sentence that belongs on the card's warning list. Set in two states only: no
    /// shipping entered at all, and a second full charge for a box that is already going out.</summary>
    public string Warning { get; set; } = "";
}
