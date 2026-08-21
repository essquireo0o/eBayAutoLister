namespace ING_eBay_AutoLister.Models;

// ── Days to cash ──────────────────────────────────────────────────────────────
// Every other money number in this app answers "how much?". This one answers "how long until I
// can spend it again?" — the question that decides whether $80 of margin is worth doing. $40 back
// in two weeks beats $80 back in five months, because the first one buys the next flip and the
// second one is a shelf with money on it.
//
// See Services/DaysToCashEstimator.cs. Produced once per opportunity and carried by the local
// arbitrage rows, the category-sweep plays and the trend-radar rows alike, so "fastest profit"
// means exactly the same thing on all three boards.
public class DaysToCashEstimate
{
    // Listing → sale, from the sold-history velocity the comps lookup already measured. Null when
    // there is no dated sold history to measure — an unknown, never an optimistic default.
    public int? DaysToSell { get; set; }

    // Sale → money you can actually spend: pack and ship, transit, and eBay's payout hold. Fixed,
    // and the same for every item, but it is real time the cash is gone and a "sells in 2 days"
    // item is not a 2-day turnaround.
    public int PipelineDays { get; set; }

    // The whole wait: DaysToSell + PipelineDays. Null when velocity is unknown.
    public int? DaysToCash { get; set; }

    // The ranking number the whole feature exists for — net profit divided by the days the cash
    // is tied up. A $40 flip that clears in 20 days ($2.00/day) beats a $90 flip that takes 120
    // ($0.75/day), and no profit column can show that.
    public decimal? ProfitPerDay { get; set; }

    // How many times a year this same dollar could come back and go out again.
    public decimal? CapitalTurnsPerYear { get; set; }

    // ROI × turns per year: what the money earns if the seller keeps recycling it into this flip.
    // Simple (not compounded) and only computed on a profitable buy — annualizing a loss states a
    // rate of return that isn't one.
    public decimal? AnnualizedRoiPercent { get; set; }

    // fast | steady | slow | dead_money | unknown
    public string SpeedTier { get; set; } = "unknown";
    public string SpeedLabel { get; set; } = "Speed unknown";
    // One plain sentence about what the wait costs — shown under the number, not instead of it.
    public string Note { get; set; } = "";
}
