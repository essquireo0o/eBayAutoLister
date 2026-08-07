using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The one thing on the live arbitrage card allowed to take money off the ceiling.
/// </summary>
/// <remarks>
/// Most of what is pinned here is a <b>refusal</b>, because that is where the money is. A cut taken
/// on thin evidence talks a seller out of a lot that was fine; a cut not taken on a sliding item
/// costs cash on a purchase that cannot be undone. So the tests are mostly about the gates: the
/// two-window comparison has to be confirmed, the trend line across every dated sale has to agree,
/// a climb may never raise anything, the fall is floored, and a comps set whose newest sale predates
/// the window is refused outright rather than reported as a market that went quiet.
/// </remarks>
public class LiveTrendTests
{
    private static readonly DateTime Now = new(2026, 8, 6, 12, 0, 0, DateTimeKind.Utc);

    private static MarketplaceComparableResult Sale(int daysAgo, decimal price, int quantity = 1) => new()
    {
        ItemId = $"c{daysAgo}-{price}",
        Title = "Bitmain Antminer S19j Pro 104TH",
        SoldPrice = price,
        TotalPrice = price,
        SoldDate = Now.AddDays(-daysAgo),
        Quantity = quantity,
    };

    /// <summary>Five sales inside the last window and five in the one before it, at two prices.</summary>
    private static List<MarketplaceComparableResult> Sliding(decimal recent, decimal prior)
    {
        var comps = new List<MarketplaceComparableResult>();
        var recentDays = new[] { 3, 8, 14, 20, 27 };
        var priorDays = new[] { 33, 39, 45, 51, 57 };

        // Spread a couple of dollars around each median so the pairwise slope has distinct dates to
        // work with, and so the dispersion guard sees a believable cluster rather than one price.
        for (var i = 0; i < recentDays.Length; i++) comps.Add(Sale(recentDays[i], recent + (2 - i)));
        for (var i = 0; i < priorDays.Length; i++) comps.Add(Sale(priorDays[i], prior + (2 - i)));
        return comps;
    }

    // ── The measurement, end to end ──────────────────────────────────────────────────────────

    [Fact]
    public void A_confirmed_slide_cuts_the_ceiling_by_exactly_what_the_medians_fell()
    {
        var read = LiveTrend.Read(Sliding(recent: 140m, prior: 200m), Now);

        Assert.True(read.Readable);
        Assert.Equal(LiveTrendDirections.Falling, read.Direction);
        Assert.Equal("confirmed", read.Reliability);
        Assert.Equal(-30m, read.PriceChangePercent);

        // 30% down, so the resale the ceiling is built on is 70% of what it was. Not a judgement
        // about how far the fall will keep going — the ratio between two measured medians.
        Assert.True(read.Discounted);
        Assert.Equal(0.70m, read.ResaleMultiplier);
        Assert.Equal(30m, read.CutPercent);
        Assert.False(read.Floored);
    }

    [Fact]
    public void The_cut_stops_at_the_floor_however_far_the_medians_fell()
    {
        var read = LiveTrend.Read(Sliding(recent: 60m, prior: 200m), Now);

        Assert.True(read.Discounted);
        Assert.Equal(LiveTrend.MaxHaircutPercent, read.CutPercent);
        Assert.Equal(0.65m, read.ResaleMultiplier);
        Assert.True(read.Floored);
        // And it says it stopped, rather than quietly presenting a floored number as a measurement.
        Assert.Contains("stops at", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The asymmetry the whole file is built around. A climb is reported and never priced: on a
    /// screen with seconds and one hammer, bidding up on a price that has not happened yet is paying
    /// for it twice.
    /// </summary>
    [Fact]
    public void A_climb_is_reported_and_never_raises_the_ceiling()
    {
        var read = LiveTrend.Read(Sliding(recent: 200m, prior: 140m), Now);

        Assert.Equal(LiveTrendDirections.Rising, read.Direction);
        Assert.True(read.PriceChangePercent > 0m);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.False(read.Discounted);
        Assert.Equal("", read.Warning);
        Assert.Contains("never raises", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_price_that_held_is_steady_and_moves_nothing()
    {
        var read = LiveTrend.Read(Sliding(recent: 200m, prior: 202m), Now);

        Assert.Equal(LiveTrendDirections.Steady, read.Direction);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Equal("", read.Warning);
        // Still says something. A block that only speaks when it moved money is a block whose
        // silence means both "these are holding their price" and "nothing looked".
        Assert.NotEqual("", read.MoneyNote);
        Assert.NotEqual("", read.Headline);
    }

    /// <summary>
    /// A fall past the bar but not far past it is not a fall. The same 8% the radar calls a climb,
    /// deliberately, so the app cannot be quicker to believe a drop than a rise.
    /// </summary>
    [Fact]
    public void A_move_smaller_than_the_bar_cuts_nothing()
    {
        var read = LiveTrend.Read(Sliding(recent: 190m, prior: 200m), Now);   // 5% down

        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Equal("", read.Warning);
    }

    // ── The refusals ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The single most expensive lie this feature could tell. The comps database behind this app is
    /// filled by a scraper with fragile session cookies; when it stops, every product on earth shows
    /// no recent sales. From one lookup that is indistinguishable from an item whose demand dried
    /// up, so neither is claimed.
    /// </summary>
    [Fact]
    public void Comps_that_all_predate_the_window_are_refused_rather_than_read_as_a_collapse()
    {
        var stale = new List<MarketplaceComparableResult>
        {
            Sale(62, 200m), Sale(68, 201m), Sale(74, 199m),
            Sale(80, 202m), Sale(86, 198m), Sale(92, 200m), Sale(99, 201m),
        };

        var read = LiveTrend.Read(stale, Now);

        Assert.False(read.Readable);
        Assert.Equal(LiveTrendDirections.Unknown, read.Direction);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Equal("", read.Warning);
        Assert.Contains("stopped being updated", read.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Undated_comps_are_not_a_trend()
    {
        var undated = Enumerable.Range(0, 12)
            .Select(i => new MarketplaceComparableResult { ItemId = $"u{i}", SoldPrice = 200m, SoldDate = null })
            .ToList();

        var read = LiveTrend.Read(undated, Now);

        Assert.False(read.Readable);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Contains("date", read.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_comps_at_all_reads_as_unknown_and_never_throws()
    {
        foreach (var read in new[] { LiveTrend.Read(null, Now), LiveTrend.Read([], Now) })
        {
            Assert.False(read.Readable);
            Assert.Equal(LiveTrendDirections.Unknown, read.Direction);
            Assert.Equal(1m, read.ResaleMultiplier);
            Assert.False(read.Discounted);
            Assert.NotEqual("", read.Headline);
            Assert.NotEqual("", read.MoneyNote);
        }
    }

    /// <summary>
    /// A fall measured off two sales on one side of the window is arithmetic, not evidence. It is
    /// still SAID — the seller should go and look — but it does not move a dollar.
    /// </summary>
    [Fact]
    public void A_slide_on_too_few_sales_warns_and_cuts_nothing()
    {
        var thin = new List<MarketplaceComparableResult>
        {
            // Two each side: enough to compare, not enough to be confirmed.
            Sale(4, 139m), Sale(19, 141m),
            Sale(36, 199m), Sale(52, 201m),
            // Padding so the dated-comp floor is cleared without adding to either window.
            Sale(64, 200m), Sale(70, 200m), Sale(76, 200m),
        };

        var read = LiveTrend.Read(thin, Now);

        Assert.Equal(LiveTrendDirections.Falling, read.Direction);
        Assert.Equal("tentative", read.Reliability);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Contains("not been cut", read.Warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The second opinion, and the reason it exists: two window medians see where a boundary
    /// happened to fall, and the Theil–Sen line sees every dated sale. When they point opposite
    /// ways the cut is refused and the disagreement is printed, because a haircut taken on the
    /// strength of a window boundary is a haircut that refuses a perfectly good lot.
    /// </summary>
    [Fact]
    public void A_rising_trend_line_refuses_the_cut_the_window_medians_asked_for()
    {
        var reading = new PriceTrendReading
        {
            Signal = "cooling",
            Reliability = "confirmed",
            PriceChangePercent = -25m,
            Recent = new TrendWindow { SoldCount = 6, MedianPrice = 150m, LowPrice = 140m, HighPrice = 160m },
            Prior = new TrendWindow { SoldCount = 6, MedianPrice = 200m, LowPrice = 190m, HighPrice = 210m },
            SlopePerMonth = 12m,
            TotalCompCount = 12,
            DatedCompCount = 12,
        };

        var read = LiveTrend.Describe(reading);

        Assert.Equal(LiveTrendDirections.Falling, read.Direction);
        Assert.False(read.Discounted);
        Assert.Equal(1m, read.ResaleMultiplier);
        Assert.Contains("disagree", read.Warning, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disagree", read.MoneyNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_falling_trend_line_lets_the_cut_through()
    {
        var reading = new PriceTrendReading
        {
            Signal = "cooling",
            Reliability = "confirmed",
            PriceChangePercent = -25m,
            Recent = new TrendWindow { SoldCount = 6, MedianPrice = 150m, LowPrice = 140m, HighPrice = 160m },
            Prior = new TrendWindow { SoldCount = 6, MedianPrice = 200m, LowPrice = 190m, HighPrice = 210m },
            SlopePerMonth = -18m,
            TotalCompCount = 12,
            DatedCompCount = 12,
        };

        var read = LiveTrend.Describe(reading);

        Assert.True(read.Discounted);
        Assert.Equal(0.75m, read.ResaleMultiplier);
    }

    /// <summary>
    /// Sales before the window and none inside it. The analyzer calls that cooling; this refuses to
    /// call it a direction, because there is no recent median for the older one to have fallen TO —
    /// and because it is the shape a stalled comps database makes.
    /// </summary>
    [Fact]
    public void Sales_that_stop_before_the_window_are_not_reported_as_a_direction()
    {
        var reading = new PriceTrendReading
        {
            Signal = "cooling",
            Reliability = "confirmed",
            PriceChangePercent = null,
            Recent = new TrendWindow { SoldCount = 0 },
            Prior = new TrendWindow { SoldCount = 7, MedianPrice = 200m, LowPrice = 190m, HighPrice = 210m },
            TotalCompCount = 7,
            DatedCompCount = 7,
        };

        var read = LiveTrend.Describe(reading);

        Assert.False(read.Readable);
        Assert.Equal(LiveTrendDirections.Unknown, read.Direction);
        Assert.False(read.Discounted);
        Assert.Equal("", read.Warning);
    }

    // ── The corpus for one product ───────────────────────────────────────────────────────────

    /// <summary>
    /// The radar divides the scan's own change in volume out of each product's. Handed one product's
    /// comps that baseline would be the product itself, and nothing could ever be selling faster than
    /// usual. So there is no baseline, and the card never claims to know how this compares to the
    /// rest of the market.
    /// </summary>
    [Fact]
    public void One_products_corpus_carries_no_velocity_baseline_to_divide_itself_by()
    {
        var corpus = LiveTrend.SoloCorpus(Sliding(140m, 200m), Now, LiveTrend.WindowDays);

        Assert.True(corpus.IsReadable);
        Assert.Null(corpus.VelocityChangePercent);
        Assert.Equal(5, corpus.RecentComps);
        Assert.Equal(5, corpus.PriorComps);
    }

    [Fact]
    public void The_window_is_a_month_against_the_month_before_it()
    {
        Assert.Equal(30, LiveTrend.WindowDays);
        // The same bar the radar calls a climb, in both directions.
        Assert.Equal(PriceTrendAnalyzer.ClimbingPricePercent, LiveTrend.MaterialMovePercent);
    }

    // ── Handing the cut to the money ─────────────────────────────────────────────────────────

    private static ResalePricing Resale() => new()
    {
        LookupTitle = "Bitmain Antminer S19j Pro 104TH",
        ExpectedSale = 200m, Median = 210m, QuickSale = 170m,
        AvgCompShipping = 14m, SoldCompCount = 10, PricedCompCount = 9, TerapeakCompCount = 2,
        ConfidenceScore = 71, ConfidenceLevel = "Good", IdentityVerified = true,
        EstimatedDaysToSell = 14, EstimatedMonthlySales = 4m, LiquidityLevel = "Fast Mover",
        DisagreementMessage = "Terapeak and the sold comps disagree by 22%.",
    };

    /// <summary>
    /// A card with nothing to read is priced by exactly the arithmetic it was priced by before this
    /// existed — and that is a property of the code rather than a claim about it, because the same
    /// object comes back.
    /// </summary>
    [Fact]
    public void Nothing_to_read_returns_the_very_same_resale_object()
    {
        var resale = Resale();

        Assert.Same(resale, LiveTrend.Discount(resale, null));
        Assert.Same(resale, LiveTrend.Discount(resale, new LiveTrendRead()));
        Assert.Same(resale, LiveTrend.Discount(resale, LiveTrend.Read([], Now)));
        Assert.Same(resale, LiveTrend.Discount(resale, LiveTrend.Read(Sliding(200m, 140m), Now)));
    }

    [Fact]
    public void Only_the_three_prices_the_ceiling_is_built_from_move()
    {
        var resale = Resale();
        var cut = LiveTrend.Discount(resale, LiveTrend.Read(Sliding(recent: 140m, prior: 200m), Now));

        Assert.NotSame(resale, cut);
        Assert.Equal(140m, cut.ExpectedSale);   // 200 × 0.70
        Assert.Equal(147m, cut.Median);         // 210 × 0.70
        Assert.Equal(119m, cut.QuickSale);      // 170 × 0.70

        // Everything the comps DESCRIBE survives untouched. Scaling a comp count or a confidence
        // score would be inventing sales nobody made.
        Assert.Equal(resale.LookupTitle, cut.LookupTitle);
        Assert.Equal(resale.AvgCompShipping, cut.AvgCompShipping);
        Assert.Equal(resale.SoldCompCount, cut.SoldCompCount);
        Assert.Equal(resale.PricedCompCount, cut.PricedCompCount);
        Assert.Equal(resale.TerapeakCompCount, cut.TerapeakCompCount);
        Assert.Equal(resale.EvidenceCompCount, cut.EvidenceCompCount);
        Assert.Equal(resale.ConfidenceScore, cut.ConfidenceScore);
        Assert.Equal(resale.ConfidenceLevel, cut.ConfidenceLevel);
        Assert.Equal(resale.IdentityVerified, cut.IdentityVerified);
        Assert.Equal(resale.EstimatedDaysToSell, cut.EstimatedDaysToSell);
        Assert.Equal(resale.EstimatedMonthlySales, cut.EstimatedMonthlySales);
        Assert.Equal(resale.LiquidityLevel, cut.LiquidityLevel);
        Assert.Equal(resale.DisagreementMessage, cut.DisagreementMessage);
    }

    /// <summary>The read is a pure function of the rows and the clock, which is what makes a
    /// re-price against held comps the same answer rather than a cheaper approximation of it.</summary>
    [Fact]
    public void The_same_rows_and_the_same_clock_give_the_same_read()
    {
        var comps = Sliding(140m, 200m);
        var first = LiveTrend.Read(comps, Now);
        var second = LiveTrend.Read(comps, Now);

        Assert.Equal(first.ResaleMultiplier, second.ResaleMultiplier);
        Assert.Equal(first.Headline, second.Headline);
        Assert.Equal(first.Warning, second.Warning);
        Assert.Equal(first.Direction, second.Direction);
    }

    /// <summary>Sold comps are per unit everywhere in this app, and a lot row is one sale of
    /// several things. The measurement divides it back down — so a lot of four at $560 is a $140
    /// comp and not a market that quadrupled.</summary>
    [Fact]
    public void A_lot_row_is_read_at_its_per_unit_price()
    {
        var comps = Sliding(recent: 140m, prior: 200m);
        var asLots = comps.Select(c => Sale(
            (int)Math.Round((Now - c.SoldDate!.Value).TotalDays), c.SoldPrice * 4m, quantity: 4)).ToList();

        var read = LiveTrend.Read(asLots, Now);

        Assert.Equal(140m, read.RecentMedian);
        Assert.Equal(200m, read.PriorMedian);
        Assert.Equal(0.70m, read.ResaleMultiplier);
    }
}
