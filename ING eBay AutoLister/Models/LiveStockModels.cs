namespace ING_eBay_AutoLister.Models;

// ── How many of these you would then own (see Services/LiveStockDepth.cs) ─────────────────────
//
// Every other block on the live card asks whether THIS lot is worth its price. That is the right
// question for the first one and the wrong question for the fourth: at a live sale the same product
// comes up again and again — the host has a pallet of them — and each one is individually a good
// buy at the price on screen. Six good buys of one product is not six times the profit, because
// they queue behind each other in the same demand. It is one profit and five months of a shelf.
//
// The card already knows how fast eBay clears one of these; it has never once known how many the
// seller was about to have. Both halves of that count already exist in this app and neither has
// ever been read at bid time:
//
//   * the ones already on the shelf — bought or listed, not yet sold, on the Deal Pipeline;
//   * the ones won tonight, on this tab, twenty minutes ago (Services/LiveBuySheet.cs).
//
// Nothing here prices anything. Saturation is a claim about TIME, not about price: the fourth one
// still resells for what the comps say, it just sells in April. So the ceiling, the resale figure
// and the spread are untouched, and what this block spends is the seller's attention.

/// <summary>
/// What tonight's buy sheet already holds of the product on screen. Counted on the server and
/// handed in, so <see cref="Services.LiveStockDepth"/> reads a number rather than a file.
/// </summary>
/// <param name="Units">Units of it won tonight and not yet listed.</param>
/// <param name="Lots">How many rows on the sheet those units came from.</param>
public readonly record struct LiveStockTonight(int Units, int Lots)
{
    /// <summary>Nothing of it won tonight — also what a card built without a sheet gets.</summary>
    public static readonly LiveStockTonight Nothing = new(0, 0);
}

/// <summary>Where one part of the pile came from — a bar on the strip.</summary>
public sealed class LiveStockSource
{
    /// <summary>shelf | tonight | lot. Spelled in <see cref="LiveStockSources"/>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>What it is called on screen: "On the shelf", "Won tonight", "This lot".</summary>
    public string Label { get; set; } = "";

    /// <summary>Units in this part. Never below 1 — a zero part is not on the strip at all.</summary>
    public int Units { get; set; }

    /// <summary>The one-line explanation, shown as the bar's tooltip.</summary>
    public string Detail { get; set; } = "";
}

/// <summary>The three places a unit of this product can be. Spelled once so the sentence, the CSS
/// and the tests agree.</summary>
public static class LiveStockSources
{
    /// <summary>Bought or listed and not yet sold — the Deal Pipeline's count.</summary>
    public const string Shelf = "shelf";

    /// <summary>Won on this tab tonight and not yet listed.</summary>
    public const string Tonight = "tonight";

    /// <summary>The lot on screen, which is not owned yet.</summary>
    public const string Lot = "lot";
}

/// <summary>The five answers. See <see cref="Services.LiveStockDepth"/> for the bars between them.</summary>
public static class LiveStockVerdicts
{
    /// <summary>One of them, none held. The ordinary case, and nothing to say beyond that.</summary>
    public const string Single = "single";

    /// <summary>More than one, and the market clears them well inside the bar.</summary>
    public const string Clear = "clear";

    /// <summary>Months of stock. Worth saying out loud before the hand presses bid.</summary>
    public const string Deep = "deep";

    /// <summary>So many months that the money is parked rather than working.</summary>
    public const string Flooded = "flooded";

    /// <summary>A real pile and no dated sold history to measure it against.</summary>
    public const string Blind = "blind";

    /// <summary>Nothing could be counted at all.</summary>
    public const string None = "none";
}

/// <summary>
/// How many of this product the seller would own if they won the lot on screen, and how long eBay
/// takes to absorb that many. See <see cref="Services.LiveStockDepth"/> for what it refuses to claim.
/// </summary>
/// <remarks>
/// Present on every priced card, including the ordinary "one of them and none on the shelf" — a
/// block that only appears once the app has found a pile is a block whose absence means both
/// "you're clear" and "nothing looked", and the second of those is the expensive reading.
/// </remarks>
public sealed class LiveStockRead
{
    /// <summary>True when there was a pile to count and a rate to measure it against. False is not
    /// an error: it is the honest state of a card whose sold history carried no dates.</summary>
    public bool Readable { get; set; }

    /// <summary>single | clear | deep | flooded | blind | none. See <see cref="LiveStockVerdicts"/>.</summary>
    public string Verdict { get; set; } = LiveStockVerdicts.None;

    /// <summary>The strip's one line — the count and what it means, in this item's own numbers.</summary>
    public string Headline { get; set; } = "";

    /// <summary>The arithmetic behind the headline, said out loud. Empty only when there is none.</summary>
    public string Note { get; set; } = "";

    /// <summary>The sentence that belongs on the card's warning list. Empty unless the pile crossed
    /// a bar — this block is silent on the ordinary case by design.</summary>
    public string Warning { get; set; } = "";

    // ── The pile ─────────────────────────────────────────────────────────────
    /// <summary>Units in the lot on screen. Never below 1.</summary>
    public int LotUnits { get; set; } = 1;

    /// <summary>Units already bought and not yet sold, off the Deal Pipeline
    /// (<see cref="OwnSalesEvidence.UnitsHeld"/>).</summary>
    public int UnitsHeld { get; set; }

    /// <summary>Units won on this tab tonight that have not been listed yet. Listed ones are on the
    /// pipeline and are already inside <see cref="UnitsHeld"/>.</summary>
    public int WonTonight { get; set; }

    /// <summary>How many rows on tonight's sheet those units came from.</summary>
    public int LotsWonTonight { get; set; }

    /// <summary>Everything above, added up. What winning this makes it.</summary>
    public int UnitsAfter { get; set; } = 1;

    /// <summary>True when there was already stock of this before the lot on screen.</summary>
    public bool AlreadyStocked => UnitsHeld > 0 || WonTonight > 0;

    /// <summary>The bars on the strip, biggest question first: shelf, tonight, this lot.</summary>
    public List<LiveStockSource> Sources { get; set; } = [];

    // ── What the market does with them ───────────────────────────────────────
    /// <summary>How many of these eBay clears a month, across everybody. The card's own figure.</summary>
    public decimal MonthlySales { get; set; }

    /// <summary>Units listed against you right now. Reported, never priced off.</summary>
    public int ActiveCompCount { get; set; }

    /// <summary>UnitsAfter ÷ MonthlySales. Null when nothing dated says how fast they move.</summary>
    public decimal? MonthsToClear { get; set; }

    /// <summary>When the LAST one turns into cash, in days. The first one sells at the market's
    /// ordinary speed; each one after it waits for the demand the one before it used up.</summary>
    public int? DaysToClearAll { get; set; }

    // ── How much of this to believe ──────────────────────────────────────────
    /// <summary>False when the seller's own book could not be read at all, so the shelf count is
    /// missing rather than zero. Said out loud — an unread shelf must never read as an empty one.</summary>
    public bool ShelfRead { get; set; }

    /// <summary>True when the product was matched on two ordinary words with no model designator in
    /// them, so the pile may be somebody else's product. Reported, never suppressed.</summary>
    public bool IdentityIsLoose { get; set; }
}
