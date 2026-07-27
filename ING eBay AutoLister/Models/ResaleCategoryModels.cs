namespace ING_eBay_AutoLister.Models;

// ── Categories: the same board, for things that aren't small parcels ──────────
// Everything the local-arbitrage pipeline knew until now assumed one shape of flip: a thing you
// can pick up, box, and post — priced against eBay sold comps and costed with eBay's percentage
// final value fee plus a shipping label. That assumption is wrong in three separate ways for the
// categories where the biggest local money actually is (cars, boats, RVs, trailers, powersports,
// tractors, appliances, furniture), and each way is expensive:
//
//   1. SOURCING — those things live on their own classifieds boards, and a keyword search of the
//      for-sale board misses most of them. See ResaleCategory.CraigslistBoard.
//   2. VALUATION — the sold-comps database this app reads is electronics-heavy. Ask it what a
//      truck sells for and it answers with the price of a tow hitch. See Services/ResaleValuation.cs,
//      which refuses rather than answers when the comps are for a different KIND of thing.
//   3. THE MONEY — a car is collected, not shipped; eBay Motors charges a flat vehicle fee rather
//      than the percentage one; and there is a title to transfer. See Services/CategoryCosts.cs.
//
// The types here are what those three answers look like on a row.

/// <summary>
/// Year / make / model / mileage, where the listing said them.
///
/// Vehicle titles are the one place in local classifieds where the useful facts are reliably IN the
/// title — "2011 Toyota Tundra 4x4 crew cab 137k miles" carries the identity, the age and the wear
/// in eleven words. Everything here is null or empty when the listing didn't say, and nothing is
/// ever guessed: an unstated mileage is unknown, never zero.
/// </summary>
public class VehicleDetails
{
    public int? Year { get; set; }
    public string Make { get; set; } = "";
    public string Model { get; set; } = "";
    /// <summary>Odometer, in miles. Reads "137k", "137,000 miles" and "137000 mi" alike.</summary>
    public int? Mileage { get; set; }
    /// <summary>Hours on the clock — how tractors, excavators and boats state their wear instead of miles.</summary>
    public int? EngineHours { get; set; }

    /// <summary>"2011 Toyota Tundra" — the identity, without the trim and the sales talk.</summary>
    public string Label =>
        string.Join(' ', new[] { Year?.ToString() ?? "", Make, Model }.Where(p => p.Length > 0));

    /// <summary>
    /// True when the listing named enough to identify the vehicle rather than merely mention one.
    /// A year and a make is the bar: "Ford" alone is a brand a floor mat can carry, and "2011"
    /// alone is a number that appears in model names.
    /// </summary>
    public bool IsIdentified => Year is not null && Make.Length > 0;

    /// <summary>"137k miles" / "1,240 hours" / "" — the wear, in the unit the thing states it in.</summary>
    public string WearText =>
        Mileage is int mi ? $"{mi:N0} miles"
        : EngineHours is int hrs ? $"{hrs:N0} hours"
        : "";
}

/// <summary>How a row was costed, and why those costs and not the parcel ones.</summary>
/// <remarks>
/// Attached to every priced row, including the ordinary parcel ones, so the money columns always
/// say which model produced them. A net profit computed with no eBay fee and no shipping is a
/// perfectly good number and a completely misleading one if the row doesn't say that is what
/// happened — see <see cref="Services.CategoryCosts"/>.
/// </remarks>
public class CategoryEconomics
{
    public string CategoryId { get; set; } = "";
    public string CategoryLabel { get; set; } = "";
    /// <summary>"eBay" | "eBay Motors" | "eBay, local pickup" — where this is being sold, and how it leaves.</summary>
    public string ChannelLabel { get; set; } = "";
    /// <summary>"13.25% + $0.40 final value fee" / "$125 flat vehicle fee" — the fee basis, in words.</summary>
    public string FeeBasis { get; set; } = "";
    /// <summary>False when the buyer collects — no label, no box, and no shipping in the profit.</summary>
    public bool ShipsToBuyer { get; set; } = true;

    /// <summary>Title / registration transfer on a titled thing. Zero on everything else.</summary>
    public decimal TitleCost { get; set; }
    /// <summary>Getting it home, when it can't be driven or carried. Zero by default — see CategoryCosts.</summary>
    public decimal TransportCost { get; set; }
    /// <summary>Title + transport. Deducted from net profit, and never counted as a marketplace fee.</summary>
    public decimal ExtraCostTotal => Math.Round(TitleCost + TransportCost, 2);

    /// <summary>One sentence naming every departure from the parcel model, for the row's tooltip.</summary>
    public string Note { get; set; } = "";
}

/// <summary>
/// Where this row's resale price came from — or, just as often and just as usefully, the reason
/// there isn't one.
/// </summary>
/// <remarks>
/// <para>
/// The rule this type exists to enforce: <b>never invent a resale price.</b> The sold-comps database
/// will happily answer "what does a 2011 Tundra sell for" with the median of four tow hitches, and
/// that answer costs nothing to produce, looks exactly like a real one, and would put a $7,900
/// "profit" on a board a seller spends money from.
/// </para>
/// <para>
/// So a valuation either has comps that survived a category-appropriate check — in which case it
/// says which source and how strong — or it has none, in which case the row keeps its listing, its
/// ask and its category, shows a dash where the money is, and carries a prefilled sold-listings
/// search the seller can run themselves. See Services/ResaleValuation.cs.
/// </para>
/// </remarks>
public class ResaleValuation
{
    /// <summary>comps | manual — whether there is a price behind this row at all.</summary>
    public string Status { get; set; } = ValuationStatuses.Manual;
    /// <summary>Which provider answered: ebay_comps | ebay_motors | bulky_local.</summary>
    public string ProviderId { get; set; } = "";
    /// <summary>"eBay sold comps" / "eBay Motors sold comps" / "value it by hand" — shown on the row.</summary>
    public string SourceLabel { get; set; } = "";
    /// <summary>confident | low | none — the same vocabulary the evidence tiering uses.</summary>
    public string Confidence { get; set; } = LocalArbitrageEvidence.None;
    /// <summary>Why the price is trusted, or why there isn't one. Always in this row's own numbers.</summary>
    public string Note { get; set; } = "";
    /// <summary>A prefilled sold-listings search for the seller to run. The whole answer on a manual row.</summary>
    public string LookupUrl { get; set; } = "";
    /// <summary>What that search asks for — "2011 Toyota Tundra", not the seller's ad copy.</summary>
    public string LookupQuery { get; set; } = "";

    public bool HasPrice => Status == ValuationStatuses.Comps;
}

public static class ValuationStatuses
{
    /// <summary>Priced off sold history that passed this category's own check.</summary>
    public const string Comps = "comps";
    /// <summary>No usable comps for this KIND of thing. The row shows dashes and a search link.</summary>
    public const string Manual = "manual";
}

/// <summary>
/// The evidence vocabulary, in Models so <see cref="ResaleValuation"/> can name a tier without
/// depending on the analyzer. The constants on
/// <see cref="Services.LocalArbitrageAnalyzer"/> forward to these, so there is still one spelling
/// of "confident" in the app.
/// </summary>
public static class LocalArbitrageEvidence
{
    public const string Confident = "confident";
    public const string Low = "low";
    public const string None = "none";
}

/// <summary>One category as the picker sees it.</summary>
public class ResaleCategoryInfo
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>The heading it sits under in the picker: Anything | Vehicles | Big items | Small items.</summary>
    public string Group { get; set; } = "";
    /// <summary>What changes when this is picked — the board searched, the fees, the valuation.</summary>
    public string Hint { get; set; } = "";
    /// <summary>True when this app has no sold-comp source for the category and will say so per row.</summary>
    public bool ValuedByHand { get; set; }
    /// <summary>True when only the classifieds sources can narrow to this board — see ILocalSupplySource.</summary>
    public bool BoardSearchable { get; set; }
}

/// <summary>What one category contributed to a scan, for the board's category filter.</summary>
public class ResaleCategoryTally
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Count { get; set; }
    /// <summary>Rows in this category that got a real resale price. The rest are manual.</summary>
    public int PricedCount { get; set; }
}
