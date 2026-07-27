namespace ING_eBay_AutoLister.Models;

// ── Rising-Demand / Price-Trend Radar ─────────────────────────────────────────
// Every pricing screen in this app answers "what is this worth?" — a snapshot, in the present
// tense. A sourcer's money is made in the tense before that: buying the thing whose price is on
// its way up, at yesterday's price. The sold-comps database already holds the evidence (a sold
// price AND a sold date on every row); nothing had ever read it as a time series.
//
// This does. It sweeps the same category universe Roll the Dice uses, measures each product's
// recent sold prices and sale VELOCITY against the window before it, and ranks what is climbing.
//
// Two rules keep it from being a horoscope, and both are enforced in PriceTrendAnalyzer:
//   * Velocity is measured RELATIVE to the whole scan. A comps database that ingested twice as
//     many rows this month makes every product on earth look like it's booming; the corpus
//     baseline is subtracted before anything is called rising.
//   * The buy price is computed from TODAY's sold price, never from the projection. The trend is
//     reported as upside on a buy that already works — it is never the reason a buy works.

// One side of the comparison: what sold in a window, and for how much.
public class TrendWindow
{
    // Days ago this window covers — Recent is 0..WindowDays, Prior is WindowDays..2x.
    public int FromDaysAgo { get; set; }
    public int ToDaysAgo { get; set; }
    public int SoldCount { get; set; }
    public decimal MedianPrice { get; set; }
    public decimal LowPrice { get; set; }
    public decimal HighPrice { get; set; }
}

// One point on the little sparkline: a week's worth of sales. Weekly rather than daily because a
// product that sells three a month has no daily series to draw.
public class TrendPoint
{
    public int WeeksAgo { get; set; }
    public int SoldCount { get; set; }
    public decimal MedianPrice { get; set; }
}

// The measurement itself — pure arithmetic over one product's dated sold comps. Carries no money:
// the profit half is added by the radar from the app's normal pricing pipeline.
public class PriceTrendReading
{
    public TrendWindow Recent { get; set; } = new();
    public TrendWindow Prior { get; set; } = new();

    // ── Coverage: how much of the history could actually be read ─────────────
    // SoldDate is free text in the comps table and is missing on a real share of rows. A trend
    // computed from the 4 rows that happened to carry a date is noise wearing a percentage sign,
    // so the coverage is measured, reported, and gates the verdict.
    public int TotalCompCount { get; set; }
    public int DatedCompCount { get; set; }
    public decimal DatedCoveragePercent { get; set; }

    // ── The move ─────────────────────────────────────────────────────────────
    public decimal? PriceChangePercent { get; set; }
    public decimal? PriceChangeAmount { get; set; }
    // Theil–Sen slope over every dated comp, in dollars per 30 days. Median-of-pairwise-slopes,
    // so one mispriced comp can't tilt the line the way least-squares would.
    public decimal? SlopePerMonth { get; set; }

    // Raw change in units sold between the two windows.
    public decimal? VelocityChangePercent { get; set; }
    // The same change across every comp the scan pulled — the database's own drift.
    public decimal? MarketVelocityChangePercent { get; set; }
    // Velocity change with the corpus baseline taken out. THIS is the one the verdict uses.
    public decimal? RelativeVelocityChangePercent { get; set; }

    public List<TrendPoint> Series { get; set; } = [];

    // ── The verdict ──────────────────────────────────────────────────────────
    // rising_demand | demand_building | price_climbing | supply_squeeze | steady | cooling | unreadable
    public string Signal { get; set; } = "unreadable";
    // confirmed | tentative | unreadable
    public string Reliability { get; set; } = "unreadable";
    // Plain sentence saying what the numbers mean — and, when unreadable, exactly what was missing.
    public string Note { get; set; } = "";

    public bool IsRising => Signal is "rising_demand" or "demand_building" or "price_climbing" or "supply_squeeze";

    // What the price would be one window from now if the move continued at the same rate. Never
    // compounded, and never above the highest price anyone actually paid in the recent window —
    // extrapolating past every real sale is fiction, not forecasting.
    public decimal? ProjectedPrice { get; set; }
}

// One product on the radar: what it is, what its price is doing, and what that's worth.
public class TrendRadarRow
{
    public string Product { get; set; } = "";
    // The title the comp lookup actually ran against, when it differs from the display name.
    public string PricedAs { get; set; } = "";
    public string NicheId { get; set; } = "";
    public string NicheLabel { get; set; } = "";
    public string Probe { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    // Short keyword to paste into Local Deals and go and find one.
    public string SearchQuery { get; set; } = "";

    public PriceTrendReading Trend { get; set; } = new();

    // ── What it sells for now ────────────────────────────────────────────────
    public decimal? EbayExpectedSale { get; set; }
    public decimal? EbayMedian { get; set; }
    public decimal? EbayQuickSale { get; set; }
    public string ResaleSource { get; set; } = "none";
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";
    public int? DaysToCash { get; set; }
    public decimal EstimatedMonthlySales { get; set; }
    public string? DisagreementMessage { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    // Break-even buy price at TODAY's sold price. The walk-away number.
    public decimal MaxBuyToday { get; set; }
    // Break-even buy price if the climb holds for one more window. The gap between the two is
    // what getting ahead of the move is worth per unit — reported as upside, never as the basis
    // for what to pay.
    public decimal MaxBuyIfTrendHolds { get; set; }
    public decimal TrendHeadroom { get; set; }
    // Pay this or less and the flip clears the app's own goldmine bar at today's price.
    public decimal TargetBuyPrice { get; set; }
    public decimal ProfitAtTarget { get; set; }
    public decimal MarginAtTargetPercent { get; set; }

    // buy_now | get_in_early | watch | thin | pass
    public string Verdict { get; set; } = "watch";
    public string VerdictNote { get; set; } = "";
}

// What one niche contributed, including the ones with nothing — a scan that reports "no products
// rising" without saying where it looked is unauditable.
public class TrendNicheOutcome
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Probes { get; set; } = [];
    public int CompsScanned { get; set; }
    public int ProductsFound { get; set; }
    public int ProductsScreenedOut { get; set; }
    public int RisingFound { get; set; }
    public string? Note { get; set; }
}

// The corpus-wide read of the scan's own data: how fresh it is, and how its overall volume moved
// between the two windows. Both are honesty guards rather than features — see PriceTrendAnalyzer.
public class TrendCorpus
{
    public int WindowDays { get; set; }
    public int TotalComps { get; set; }
    public int DatedComps { get; set; }
    public int RecentComps { get; set; }
    public int PriorComps { get; set; }
    public decimal DatedCoveragePercent { get; set; }
    public int? NewestCompAgeDays { get; set; }
    public decimal? VelocityChangePercent { get; set; }
    // False when the data itself can't support a trend read at all — nothing dated, or the whole
    // database stopped being updated before the recent window even started.
    public bool IsReadable { get; set; }
    public string? Note { get; set; }
}

public class TrendRadarResult
{
    // ok | no_comps | stale_data | error
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }

    public int Seed { get; set; }
    public int NextSeed { get; set; }
    public int RollsToCoverEverything { get; set; }
    public int NichesInUniverse { get; set; }
    public int WindowDays { get; set; }
    // rising | all
    public string Direction { get; set; } = "rising";

    public TrendCorpus Corpus { get; set; } = new();
    public List<TrendNicheOutcome> Niches { get; set; } = [];
    public List<TrendRadarRow> Rows { get; set; } = [];
    public int Count => Rows.Count;

    // ── What the scan actually did ───────────────────────────────────────────
    public int CompsScanned { get; set; }
    public int ProductsConsidered { get; set; }
    public int ProductsMeasured { get; set; }
    public int ProductsRising { get; set; }
    public int ProductsPriced { get; set; }
    public int ProductsDropped { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }
    public bool SoldCompsConfigured { get; set; }

    public int BuyNowCount { get; set; }
    // Added up across every row worth buying: what the climb is worth per unit, if you get one.
    // An upper bound on this board, not a forecast — and it only ever counts confirmed rises.
    public decimal TotalTrendHeadroom { get; set; }

    public string? DataWarning { get; set; }
}
