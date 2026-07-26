namespace ING_eBay_AutoLister.Models;

// ── Roll the Dice ─────────────────────────────────────────────────────────────
// Every other money feature in this app starts with the seller already knowing what to look for:
// they type "Antminer S19" into Local Deals, or drop in a supplier price list. This one starts
// with nothing. It sweeps the sold-comps database across many CATEGORIES at once, finds the
// products that actually carry a margin AND actually sell, then goes looking for where those
// products can be bought cheap right now (Craigslist/Facebook when connected, eBay Buy It Now
// otherwise) and reports what the flip is worth after fees.
//
// The numbers are the same numbers the rest of the app produces — MarketPriceEstimator's
// identity-guarded price, ProfitCalculator/FeeProfile's fees, LocalArbitrageAnalyzer's verdict
// tiers — because a "jackpot" that used its own friendlier arithmetic would be a slot machine,
// not a tool. See Services/CategorySweep.cs and Services/JackpotHunter.cs.

// One product the comps sweep found, before a cent of pricing budget is spent on it. Built purely
// from the sold rows a probe returned: enough to decide WHICH products deserve a real lookup.
public class JackpotCandidate
{
    public string NicheId { get; set; } = "";
    public string NicheLabel { get; set; } = "";
    // The keyword that surfaced it — kept so a roll can explain where each play came from.
    public string Probe { get; set; } = "";

    // Clustering signature (see JackpotHunter.ProductKey) — NOT a pricing identity.
    public string Key { get; set; } = "";
    // The cleanest title in the cluster: what the comp lookup and the sourcing search both run on.
    public string LookupTitle { get; set; } = "";
    public string ImageUrl { get; set; } = "";

    public int CompCount { get; set; }
    // Sold within JackpotHunter.RecentDays. Zero recent sales with dated comps present means the
    // demand has already been and gone.
    public int RecentCompCount { get; set; }
    public int? NewestCompAgeDays { get; set; }
    public decimal MedianSold { get; set; }
    public decimal LowSold { get; set; }
    public decimal HighSold { get; set; }

    // True when the cluster has no model-shaped token to hang an identity on ("kitchenaid mixer",
    // not "s19j"). Those clusters are looser, so they're held to a higher comp count.
    public bool LooseIdentity { get; set; }

    // Pre-check ordering heuristic ONLY — decides which candidates get a real lookup first.
    // Nothing shown to the seller is derived from it.
    public double Score { get; set; }

    // Why it was dropped before pricing, when it was.
    public string? RejectReason { get; set; }
}

// One place a play's product can actually be bought right now, priced end to end. Produced by
// running the supply listing through LocalArbitrageAnalyzer, so a Craigslist buy, a Facebook buy
// and an eBay buy are all costed by identical rules.
public class JackpotSourceOption
{
    // craigslist | facebook | ebay
    public string Source { get; set; } = "";
    public string SourceLabel { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string Location { get; set; } = "";
    public double? DistanceMiles { get; set; }
    public string PostedAgo { get; set; } = "";

    // What it costs to acquire — the local ask, or an eBay listing's price plus its shipping.
    public decimal BuyPrice { get; set; }
    public decimal? EstimatedFees { get; set; }
    public decimal? EstimatedShipCost { get; set; }
    public decimal? NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }

    // goldmine | solid | thin | pass | no_data — LocalArbitrageAnalyzer's own tiers, unchanged.
    public string Verdict { get; set; } = "no_data";
    public string VerdictNote { get; set; } = "";

    public static JackpotSourceOption From(LocalArbitrageOpportunity row) => new()
    {
        Source = row.Source, SourceLabel = row.SourceLabel, Title = row.Title, Url = row.Url,
        ImageUrl = row.ImageUrl, Location = row.Location, DistanceMiles = row.DistanceMiles,
        PostedAgo = row.PostedAgo, BuyPrice = row.LocalAsk, EstimatedFees = row.EstimatedFees,
        EstimatedShipCost = row.EstimatedShipCost, NetProfit = row.NetProfit, RoiPercent = row.RoiPercent,
        MarginPercent = row.MarginPercent, Verdict = row.Verdict, VerdictNote = row.VerdictNote,
    };
}

// One play on the jackpot board: a product worth selling, what it resells for, what to pay for it,
// and where it can be bought today.
public class JackpotPlay
{
    public string Product { get; set; } = "";
    // The title the comp lookup actually ran against, when it differs from the display name.
    public string PricedAs { get; set; } = "";
    public string NicheId { get; set; } = "";
    public string NicheLabel { get; set; } = "";
    public string Probe { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    // The short keyword a seller can paste into a local search to hunt this themselves.
    public string SearchQuery { get; set; } = "";

    // ── The eBay resale ──────────────────────────────────────────────────────
    public decimal? EbayResaleMedian { get; set; }
    public decimal? EbayExpectedSale { get; set; }
    public decimal? EbayQuickSale { get; set; }
    // hosted_comps | terapeak | hosted_comps+terapeak | none
    public string ResaleSource { get; set; } = "none";
    public int SoldCompCount { get; set; }
    public int TerapeakCompCount { get; set; }
    public int ConfidenceScore { get; set; }
    public string ConfidenceLevel { get; set; } = "Insufficient Evidence";
    public int LiquidityScore { get; set; }
    public string LiquidityLevel { get; set; } = "";
    public int OpportunityScore { get; set; }
    public string? DisagreementMessage { get; set; }

    // ── The money ────────────────────────────────────────────────────────────
    // Pay this and you break even after fees and shipping — the walk-away number.
    public decimal MaxBuyPrice { get; set; }
    // Pay this or less and the flip clears the app's own jackpot bar (see JackpotHunter).
    public decimal TargetBuyPrice { get; set; }
    // What buying at TargetBuyPrice would net — the number a "no supply found yet" play is worth
    // hunting for, as opposed to a live one where NetProfit below is real supply.
    public decimal ProfitAtTarget { get; set; }

    // From the best live source below; null when nothing is on sale right now.
    public decimal? NetProfit { get; set; }
    public decimal? RoiPercent { get; set; }
    public decimal? MarginPercent { get; set; }
    public decimal? BestBuyPrice { get; set; }
    // How long the money is tied up — profit you can't realise for six months isn't the same deal.
    public int? DaysToCash { get; set; }
    public decimal EstimatedMonthlySales { get; set; }

    public List<JackpotSourceOption> Sources { get; set; } = [];
    public bool HasLiveSupply => Sources.Count > 0;

    // jackpot | strong | target | thin | watch | pass
    public string Tier { get; set; } = "watch";
    public string TierNote { get; set; } = "";
    // Plain sentence: where this can be bought right now, or that nothing was found.
    public string WhereToLook { get; set; } = "";
}

// What one niche contributed to a roll, including the ones that had nothing — a sweep that says
// "no plays" without saying which categories it actually dug in is unauditable.
public class JackpotNicheOutcome
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public List<string> Probes { get; set; } = [];
    public int CompsScanned { get; set; }
    public int ProductsFound { get; set; }
    public int ProductsScreenedOut { get; set; }
    public int PlaysFound { get; set; }
    public string? Note { get; set; }
}

public class JackpotResult
{
    // ok | no_comps | error
    public string Status { get; set; } = "ok";
    public string? Error { get; set; }

    // The roll this was, and the one the "Roll again" button should ask for — the sweep window
    // advances with the seed, so a second roll digs different categories (see CategorySweep).
    public int Seed { get; set; }
    public int NextSeed { get; set; }
    public int RollsToCoverEverything { get; set; }
    public int NichesInUniverse { get; set; }

    public string ZipCode { get; set; } = "";
    public int RadiusMiles { get; set; }

    public List<JackpotNicheOutcome> Niches { get; set; } = [];
    public List<JackpotPlay> Plays { get; set; } = [];
    public int Count => Plays.Count;

    // ── What the roll actually did, so the numbers can be judged ─────────────
    public int CompsScanned { get; set; }
    public int ProductsConsidered { get; set; }
    public int ProductsPriced { get; set; }
    public int ProductsSourced { get; set; }
    // Priced, then thrown off the board: either no sold history could be matched, or the sweep's own
    // cluster median and the per-product estimate disagreed too far to trust either (see
    // JackpotHunter.EstimateAgreesWithSweep).
    public int ProductsDropped { get; set; }
    // Listings found on the buy side that were not credibly the product — accessories, parts,
    // fit-for items, or prices no real unit sells at (see JackpotHunter.IsPlausibleSupply).
    public int SupplyRejected { get; set; }
    public int TerapeakScrapesUsed { get; set; }
    public bool TerapeakConnected { get; set; }
    public bool SoldCompsConfigured { get; set; }
    // Which sourcing channels were actually reachable on this roll, so an empty Sources list on a
    // play reads as "nothing for sale" rather than "nothing was searched".
    public List<LocalSupplySourceOutcome> Sources { get; set; } = [];
    public bool EbaySupplySearched { get; set; }

    public int JackpotCount { get; set; }
    // Total net profit across every live source option that clears its fees — an upper bound on
    // this board if every one of them were bought and flipped, not a forecast.
    public decimal TotalPotentialProfit { get; set; }

    public string? DataWarning { get; set; }
}
