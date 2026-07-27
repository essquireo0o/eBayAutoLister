namespace ING_eBay_AutoLister.Models;

// ── Where to sell highest (see Services/WhereToSellAnalyzer.cs) ──────────────────────────────
// The app has always answered "what is this worth?" with an eBay number, because eBay is the only
// place it lists. But the fee load differs enormously between venues — eBay takes 13.25% plus a
// per-order fee and the shipping label, a local cash sale takes nothing — so the venue that shows
// the highest price and the venue that hands over the most money are routinely not the same one.
//
// These types carry that comparison. Every price on them is labelled with where it came from and
// what kind of evidence it is, because the whole answer turns on that: an eBay figure is what
// buyers PAID, a local figure is what sellers are ASKING, and treating those as the same number is
// how a seller gets talked off a marketplace that was paying them more.

// What one deduction costs at one venue. Same shape as NetQuoteLine — the zero lines are kept
// deliberately, since "$0.00 shipping" is the entire argument for a local pickup sale.
public class VenueCostLine(string key, string label, decimal amount, string detail)
{
    public string Key { get; set; } = key;
    public string Label { get; set; } = label;
    public decimal Amount { get; set; } = amount;
    public string Detail { get; set; } = detail;
}

// One venue's answer for one item: what it likely sells for there, what is taken out of that, and
// what is left. Ordered by NetProceeds by the analyzer, but a venue is only allowed to WIN if its
// evidence is real — see Rankable.
public class VenueOutlook
{
    public string Venue { get; set; } = "";        // ebay | facebook | craigslist | mercari
    public string VenueLabel { get; set; } = "";
    public string SaleMode { get; set; } = "";      // shipped | local_pickup
    public string SaleModeLabel { get; set; } = "";
    public string CreateUrl { get; set; } = "";
    public string SearchUrl { get; set; } = "";     // what was actually searched to price this venue

    // ── Price side ───────────────────────────────────────────────────────────────────────────
    public decimal? ExpectedPrice { get; set; }
    public decimal BuyerPaidShipping { get; set; }
    public decimal? GrossRevenue { get; set; }
    /// <summary>sold | asking | none — the single most important word on the row.</summary>
    public string EvidenceKind { get; set; } = "none";
    public string EvidenceLabel { get; set; } = "";
    public int SampleCount { get; set; }
    public int RawResultCount { get; set; }
    /// <summary>The observed median before any ask-to-sold haircut, so the haircut is checkable.</summary>
    public decimal? ObservedMedian { get; set; }
    public decimal? ObservedLow { get; set; }
    public decimal? ObservedHigh { get; set; }
    public decimal? RealizationFactor { get; set; }
    public string PriceBasis { get; set; } = "";

    // ── Money side ───────────────────────────────────────────────────────────────────────────
    public decimal? Fees { get; set; }
    public decimal? FulfilmentCost { get; set; }
    public decimal? TotalDeductions { get; set; }
    public decimal? NetProceeds { get; set; }
    public decimal? FeeLoadPercent { get; set; }
    public decimal? NetProfit { get; set; }         // null until the seller says what they paid
    public decimal? MarginPercent { get; set; }
    public decimal? RoiPercent { get; set; }
    public List<VenueCostLine> Costs { get; set; } = [];
    public string FeeNote { get; set; } = "";

    /// <summary>
    /// The price this venue has to fetch to hand over as much as eBay does. Pure fee arithmetic —
    /// available even where there is no demand data at all, which is what makes it worth showing
    /// for Mercari.
    /// </summary>
    public decimal? PriceToBeatEbay { get; set; }
    public string? PriceToBeatEbayNote { get; set; }

    // ── Time side ────────────────────────────────────────────────────────────────────────────
    /// <summary>Days between "it sold" and "the money is spendable" — 0 for a cash handoff.</summary>
    public int CashPipelineDays { get; set; }
    public int? DaysToSell { get; set; }
    public int? DaysToCash { get; set; }
    public decimal? NetPerDay { get; set; }
    public string SpeedTier { get; set; } = "unknown";
    public string SpeedLabel { get; set; } = "";
    public string SpeedNote { get; set; } = "";

    // ── Judgement ────────────────────────────────────────────────────────────────────────────
    public int Confidence { get; set; }
    public string ConfidenceLevel { get; set; } = "";
    /// <summary>False when the evidence is too thin to let this venue beat another one.</summary>
    public bool Rankable { get; set; }
    public int Rank { get; set; }
    /// <summary>Net here minus net on eBay. The number the whole screen exists to show.</summary>
    public decimal? NetVsEbay { get; set; }
    /// <summary>best | close | lower | thin | no_data | unavailable</summary>
    public string Verdict { get; set; } = "no_data";
    public string Note { get; set; } = "";
    public string? Status { get; set; }             // ok | not_connected | session_expired | error
    public string? Error { get; set; }
}

// The whole answer for one item.
public class WhereToSellReport
{
    /// <summary>ok | no_query | error</summary>
    public string Status { get; set; } = "ok";
    public string Query { get; set; } = "";
    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }
    public decimal? UnitCost { get; set; }
    public bool HasCostBasis { get; set; }

    public List<VenueOutlook> Venues { get; set; } = [];

    public string? BestVenue { get; set; }
    public string? BestVenueLabel { get; set; }
    public decimal? BestNet { get; set; }
    public decimal? EbayNet { get; set; }
    /// <summary>What moving off eBay is worth on this one item. Never negative.</summary>
    public decimal? ExtraVsEbay { get; set; }

    /// <summary>move | stay_on_ebay | too_close | no_data</summary>
    public string Verdict { get; set; } = "no_data";
    public string Headline { get; set; } = "";
    public string Subhead { get; set; } = "";

    public List<LocalSupplySourceOutcome> Sources { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? Error { get; set; }
}
