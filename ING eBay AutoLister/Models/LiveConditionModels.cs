namespace ING_eBay_AutoLister.Models;

// ── What condition the lot is in, and what condition the comps were in ────────────────────────
// See Services/LiveCondition.cs. The one thing the live card has never asked about the item it is
// pricing: a sold-comp median blended across sealed boxes and beat-up used units is the right
// price for neither, and on a live feed the seller is looking at exactly which one it is.

/// <summary>The app's condition ladder, worst to best. One vocabulary, used for the lot and for
/// the comps it is priced off, so the two can be compared at all.</summary>
public static class LiveConditionBands
{
    public const string Unstated = "unstated";
    public const string Broken = "broken";
    public const string Used = "used";
    public const string LikeNew = "likenew";
    public const string New = "new";
}

/// <summary>Where the lot's own condition came from.</summary>
public static class LiveConditionSources
{
    /// <summary>Nothing said. The card asks rather than assuming.</summary>
    public const string Unstated = "unstated";
    /// <summary>Read out of the lot's name.</summary>
    public const string Title = "title";
    /// <summary>The seller picked it, looking at the thing on screen. Outranks the name.</summary>
    public const string Seller = "seller";
}

/// <summary>One condition band inside the sold comps behind a card.</summary>
public sealed class LiveConditionBandRead
{
    public string Band { get; set; } = LiveConditionBands.Unstated;
    public string Label { get; set; } = "";
    public int Count { get; set; }
    /// <summary>The median sold price of just these — the same field and the same median function
    /// the price estimator uses, so the ratio between two bands is a condition ratio and not a
    /// difference in how two numbers were worked out.</summary>
    public decimal Median { get; set; }
    /// <summary>This band's share of the comps that stated a condition at all.</summary>
    public decimal SharePercent { get; set; }
    /// <summary>True for the band the lot on screen is in — the rows the ceiling should be
    /// standing on.</summary>
    public bool IsThisLot { get; set; }
}

/// <summary>
/// What condition the lot is in, what condition the sold comps were in, and what the gap between
/// those two did to the ceiling. See <see cref="Services.LiveCondition"/>.
/// </summary>
/// <remarks>
/// Present on every priced card, including the ones with nothing to report. A block that only
/// appears when the app found a condition mismatch is a block whose silence means both "the comps
/// are the right condition" and "nothing looked" — and those two send a bidder in opposite
/// directions with a hammer coming down.
/// </remarks>
public sealed class LiveConditionRead
{
    // ── What is being bid on ─────────────────────────────────────────────────
    /// <summary>new | likenew | used | broken | unstated.</summary>
    public string Band { get; set; } = LiveConditionBands.Unstated;
    public string BandLabel { get; set; } = "";
    /// <summary>seller | title | unstated.</summary>
    public string Source { get; set; } = LiveConditionSources.Unstated;
    /// <summary>The words in the lot's name that said so. Empty when the seller picked it.</summary>
    public string Evidence { get; set; } = "";

    // ── What the comps were ──────────────────────────────────────────────────
    /// <summary>True when enough of the sold comps stated a condition to read them as bands. False
    /// is not a failure — plenty of sold rows carry no condition, and the card says so instead of
    /// pretending the blend it priced off was a single-condition price.</summary>
    public bool Readable { get; set; }
    public int TotalComps { get; set; }
    public int ClassifiedComps { get; set; }
    public decimal CoveragePercent { get; set; }

    /// <summary>The bands present in the comps, best first. Empty when nothing was readable.</summary>
    public List<LiveConditionBandRead> Bands { get; set; } = [];
    public string DominantBand { get; set; } = "";
    public string DominantLabel { get; set; } = "";
    /// <summary>True when the comps span more than one band — which is when the median above them
    /// stops being a price for any one condition.</summary>
    public bool Mixed { get; set; }

    /// <summary>The median across every comp that stated a condition. The figure the ratio below is
    /// measured against, and the reason it is not the card's headline resale price: this is a plain
    /// median of the classified rows, and the resale price is the estimator's weighted blend.</summary>
    public decimal AllMedian { get; set; }
    public int MatchedComps { get; set; }
    public decimal MatchedMedian { get; set; }

    // ── What it did to the money ─────────────────────────────────────────────
    public decimal ResaleMultiplier { get; set; } = 1m;
    public bool Discounted { get; set; }
    public decimal CutPercent { get; set; }
    /// <summary>True when the measured gap was wider than the cut is allowed to be.</summary>
    public bool Floored { get; set; }

    // ── Words ────────────────────────────────────────────────────────────────
    /// <summary>The one line on the strip. Never empty on a priced card.</summary>
    public string Headline { get; set; } = "";
    /// <summary>What the ceiling below the strip is priced off. Said on every card.</summary>
    public string MoneyNote { get; set; } = "";
    /// <summary>Only when the seller has to do something about it.</summary>
    public string Warning { get; set; } = "";
}
