using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Data.Sqlite;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The trail behind a resale price, and the one promise it makes: <b>the price reconciles</b>.
///
/// Multiply each source's figure by the weight the trail carries, add them up, and the number the
/// money columns were computed from comes back out. That is the whole reason this exists — a
/// breakdown that doesn't add up is worse than none, because it invites a seller to trust a figure
/// on the strength of working that is wrong. So the reconciliation is pinned here on every shape
/// the pipeline can produce: two sources, either one alone, and a row nothing priced.
///
/// The second thing pinned is what each source's figure MEANS. The local side contributes its
/// weighted median (recency, match strength and buying format already applied) and not its plain
/// median, because the weighted one is what the blend consumed — quoting the plain median beside a
/// weight that was never applied to it would produce a trail that reads correct and doesn't add up.
/// </summary>
[Collection(PooledSqliteTests.Name)]
public class PriceBasisTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-pricebasis-" + Guid.NewGuid().ToString("N"));

    public PriceBasisTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A PriceEstimate shaped exactly the way MarketPriceEstimator leaves one after a two-source
    /// blend: the headline figures already blended, the pre-blend inputs kept beside them.
    /// </summary>
    private static PriceEstimate Blended(
        decimal localExpected = 430m, decimal localMedian = 425m, int localPriced = 7,
        decimal terapeakMedian = 398m, int terapeakComps = 12,
        decimal localWeight = 0.63m)
    {
        var terapeakWeight = 1m - localWeight;
        var blendedExpected = Math.Round(localExpected * localWeight + terapeakMedian * terapeakWeight, 2);
        var blendedMedian = Math.Round(localMedian * localWeight + terapeakMedian * terapeakWeight, 2);
        return new PriceEstimate
        {
            MedianPrice = blendedMedian,
            ExpectedSalePrice = blendedExpected,
            RecommendedListingPrice = Math.Round(blendedExpected * 1.05m, 2),
            LocalMedianPrice = localMedian,
            LocalExpectedSalePrice = localExpected,
            TerapeakMedianPrice = terapeakMedian,
            LocalWeight = localWeight,
            TerapeakWeight = terapeakWeight,
            PricedOnCompCount = localPriced,
            TerapeakComparableCount = terapeakComps,
        };
    }

    /// <summary>The single-source shape: local comps only, Terapeak silent.</summary>
    private static PriceEstimate LocalOnly(decimal expected = 430m, decimal median = 425m, int priced = 7) => new()
    {
        MedianPrice = median,
        ExpectedSalePrice = expected,
        RecommendedListingPrice = Math.Round(expected * 1.05m, 2),
        LocalMedianPrice = median,
        LocalExpectedSalePrice = expected,
        LocalWeight = 1m,
        TerapeakWeight = 0m,
        PricedOnCompCount = priced,
    };

    private static ConfidenceBreakdown Confidence(int score = 72) =>
        new ConfidenceScoringService().Score(
            new MarketAnalysisResult
            {
                PriceEstimate = new PriceEstimate { TerapeakWeight = 0.37m },
                SellThrough = new SellThroughAnalysis { SoldComparableCount = 7 },
                Stability = new PriceStability { StabilityScore = score },
            },
            strongComparableCount: 7, exactIdentifierMatches: 1, modelNumberMatches: 2,
            mostRecentComparableAgeDays: 12, conditionConsistent: true, quantityConsistent: true,
            categoryConsistent: true);

    /// <summary>Multiply and add, the way a seller reading the panel would.</summary>
    private static decimal Reconstruct(PriceBasis basis) =>
        basis.Sources.Sum(s => s.Value * s.WeightPercent / 100m);

    // ── The promise ────────────────────────────────────────────────────────────

    [Fact]
    public void The_blended_price_reconciles_from_the_sources_it_names()
    {
        var basis = PriceBasis.From(Blended(), Confidence())!;

        Assert.Equal(2, basis.Sources.Count);
        Assert.Equal(basis.Price, Reconstruct(basis), 2);
    }

    /// <summary>
    /// The weights are carried unrounded on purpose. A blend at 63.4157% shown as "63%" is fine to
    /// read and wrong to compute with — off by dollars on a four-figure item — so the display
    /// rounds and the model does not.
    /// </summary>
    [Fact]
    public void An_awkward_weight_still_reconciles_to_the_cent()
    {
        var basis = PriceBasis.From(Blended(localWeight: 0.634157m), Confidence())!;

        Assert.Equal(basis.Price, Reconstruct(basis), 2);
        Assert.NotEqual(Math.Round(basis.Sources[0].WeightPercent), basis.Sources[0].WeightPercent);
    }

    [Fact]
    public void A_single_source_price_reconciles_too_and_carries_all_the_weight()
    {
        var basis = PriceBasis.From(LocalOnly(), Confidence())!;

        var only = Assert.Single(basis.Sources);
        Assert.Equal(PriceBasis.HostedCompsKey, only.Key);
        Assert.Equal(100m, only.WeightPercent);
        Assert.Equal(basis.Price, Reconstruct(basis), 2);
    }

    [Fact]
    public void A_terapeak_only_price_names_terapeak_and_nothing_else()
    {
        // What the estimator produces when the local lookup found nothing it could price off:
        // the expected sale IS Terapeak's median, and the local side never entered the blend.
        var estimate = new PriceEstimate
        {
            MedianPrice = 398m, ExpectedSalePrice = 398m,
            TerapeakMedianPrice = 398m, TerapeakComparableCount = 12,
            LocalWeight = 0m, TerapeakWeight = 1m, PricedOnCompCount = 0,
        };

        var basis = PriceBasis.From(estimate, Confidence())!;

        var only = Assert.Single(basis.Sources);
        Assert.Equal(PriceBasis.TerapeakKey, only.Key);
        Assert.Equal(398m, only.Value);
        Assert.Equal(basis.Price, Reconstruct(basis), 2);
    }

    // ── What each figure is ────────────────────────────────────────────────────

    /// <summary>
    /// The local row states the WEIGHTED median, because that is the number the blend multiplied.
    /// Quoting the plain median here would produce a trail that reads plausibly and is off by the
    /// difference between the two — which is exactly the error the panel exists to make impossible.
    /// </summary>
    [Fact]
    public void The_local_figure_is_the_one_that_entered_the_blend_not_the_plain_median()
    {
        var estimate = Blended(localExpected: 430m, localMedian: 425m);

        var basis = PriceBasis.From(estimate, Confidence())!;

        Assert.Equal(430m, basis.Sources[0].Value);
        Assert.Equal("weighted median", basis.Sources[0].ValueLabel);
        Assert.Equal(basis.Price, Reconstruct(basis), 2);
    }

    /// <summary>
    /// The plain median is still shown — separately, and only when it differs. It is what a seller
    /// checking this row against an eBay sold search will actually find, and quietly omitting it
    /// makes the app's figure look like a number eBay never printed.
    /// </summary>
    [Fact]
    public void The_plain_median_is_carried_beside_the_price_when_the_two_differ()
    {
        var differs = PriceBasis.From(Blended(localExpected: 430m, localMedian: 425m), Confidence())!;
        Assert.NotNull(differs.MedianPrice);
        Assert.NotEqual(differs.Price, differs.MedianPrice!.Value);

        // Same figure twice is noise, not transparency.
        var same = PriceBasis.From(LocalOnly(expected: 430m, median: 430m), Confidence())!;
        Assert.Null(same.MedianPrice);
    }

    /// <summary>
    /// "7 of 19 comps". The gap is the identity guard, the outlier trim and the strong-match filter,
    /// and only the first number backs the price — a twelve-comp search that priced off one of them
    /// is the single most common way this app publishes a 698% return.
    /// </summary>
    [Fact]
    public void The_comps_that_priced_it_are_stated_against_the_comps_the_search_returned()
    {
        var narrowed = PriceBasis.From(LocalOnly(priced: 7), Confidence(), localCompsFound: 19)!;
        Assert.Equal(7, narrowed.Sources[0].CompCount);
        Assert.Equal(19, narrowed.Sources[0].FoundCount);

        // Nothing was dropped, so there is no second number to state.
        var whole = PriceBasis.From(LocalOnly(priced: 7), Confidence(), localCompsFound: 7)!;
        Assert.Equal(0, whole.Sources[0].FoundCount);
    }

    /// <summary>
    /// A Terapeak median that carried zero weight is not evidence, and printing it beside "0%"
    /// invites it to be read as a second opinion the price rests on. It rests on nothing.
    /// </summary>
    [Fact]
    public void A_source_that_carried_no_weight_is_not_listed()
    {
        var estimate = LocalOnly();
        estimate.TerapeakMedianPrice = 398m;   // scraped, and then given no weight
        estimate.TerapeakComparableCount = 0;

        var basis = PriceBasis.From(estimate, Confidence())!;

        Assert.Single(basis.Sources);
        Assert.Equal(PriceBasis.HostedCompsKey, basis.Sources[0].Key);
    }

    // ── The sentence ───────────────────────────────────────────────────────────

    [Fact]
    public void The_blend_sentence_states_both_figures_both_shares_and_the_result()
    {
        var basis = PriceBasis.From(Blended(localExpected: 430m, terapeakMedian: 398m, localWeight: 0.63m), Confidence())!;

        Assert.Contains("$430.00", basis.Arithmetic);
        Assert.Contains("$398.00", basis.Arithmetic);
        Assert.Contains("63%", basis.Arithmetic);
        Assert.Contains("37%", basis.Arithmetic);
        Assert.Contains("eBay sold comps", basis.Arithmetic);
        Assert.Contains("Terapeak", basis.Arithmetic);
        Assert.Contains(basis.Price.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("en-US")),
            basis.Arithmetic);
    }

    /// <summary>One source says so plainly. "100% × $430" is arithmetic theatre.</summary>
    [Fact]
    public void A_single_source_sentence_does_not_pretend_to_be_a_sum()
    {
        var basis = PriceBasis.From(LocalOnly(priced: 1), Confidence())!;

        Assert.DoesNotContain("100%", basis.Arithmetic);
        Assert.DoesNotContain(" = ", basis.Arithmetic);
        Assert.Contains("1 sold comp", basis.Arithmetic);
        Assert.DoesNotContain("1 sold comps", basis.Arithmetic);
    }

    // ── What it refuses to explain ─────────────────────────────────────────────

    /// <summary>
    /// A row nothing could price has a REASON, not a number, and stating that reason belongs to
    /// ResaleValuation. A trail here would be an explanation of a price that doesn't exist.
    /// </summary>
    [Fact]
    public void There_is_no_trail_for_a_price_that_was_never_produced()
    {
        Assert.Null(PriceBasis.From(new PriceEstimate(), Confidence()));
        Assert.Null(PriceBasis.From(new PriceEstimate { MedianPrice = 0m }, Confidence()));
    }

    /// <summary>
    /// The estimator sets ExpectedSalePrice from the weighted median, but the trend projection and
    /// the negotiation endpoint hand-build estimates carrying only a median. Those still get a
    /// trail — off the figure they do have.
    /// </summary>
    [Fact]
    public void A_median_with_no_expected_sale_is_still_explained()
    {
        var estimate = new PriceEstimate
        {
            MedianPrice = 500m, LocalMedianPrice = 500m, LocalWeight = 1m, PricedOnCompCount = 4,
        };

        var basis = PriceBasis.From(estimate, Confidence())!;

        Assert.Equal(500m, basis.Price);
        Assert.Equal(500m, Assert.Single(basis.Sources).Value);
    }

    // ── The confidence half ────────────────────────────────────────────────────

    [Fact]
    public void The_confidence_score_arrives_with_its_terms_and_its_biggest_gap()
    {
        var confidence = Confidence();
        var basis = PriceBasis.From(Blended(), confidence)!;

        Assert.Equal(confidence.Score, basis.ConfidenceScore);
        Assert.Equal(confidence.Level, basis.ConfidenceLevel);
        Assert.Equal(7, basis.ConfidenceFactors.Count);
        Assert.Equal(confidence.BiggestGap, basis.BiggestGap);
    }

    [Fact]
    public void A_source_disagreement_travels_with_the_trail()
    {
        var estimate = Blended();
        estimate.MarketDataDisagreement = true;
        estimate.DisagreementMessage = "Local sold-history median ($430.00) and Terapeak median ($398.00) differ by 22%";

        var basis = PriceBasis.From(estimate, Confidence())!;

        Assert.Contains("differ by 22%", basis.DisagreementMessage);
    }

    // ── End to end, through the real estimator ─────────────────────────────────

    /// <summary>
    /// Not a hand-built estimate: real comps through MarketPriceEstimator, whose blend overwrites
    /// the very figures the trail reports. Terapeak is unreachable here (no session file, and no
    /// scrape is ever permitted), which is the single-source path every scan takes when the seller
    /// has not connected it — and the path the trail has to survive.
    /// </summary>
    [Fact]
    public async Task A_real_estimate_explains_itself_and_still_adds_up()
    {
        var log = new ActionLog();
        var cache = new TerapeakPriceCache(new ListingDatabase(new StubWebHostEnvironment { ContentRootPath = _root }));
        var market = new TerapeakMarketService(
            new TerapeakService(Path.Combine(_root, "no-session.json"), Path.Combine(_root, "profile"), log),
            cache, log);
        var estimator = new MarketPriceEstimator(market);

        // Deliberately not five interchangeable sales: the two dearest are the closest matches, so
        // the weighted median lands above the plain one and the trail has to quote the right one.
        var comps = new[] { (380m, 60), (400m, 60), (410m, 60), (425m, 95), (440m, 95) }
            .Select((c, i) => new MarketplaceComparableResult
            {
                ItemId = $"c{i}", Title = "Bitmain Antminer S19j Pro 104TH", SoldPrice = c.Item1,
                TotalPrice = c.Item1, MatchScore = c.Item2, SoldDate = DateTime.UtcNow.AddDays(-5 - i),
            })
            .ToList();

        var estimate = await estimator.EstimateAsync(
            new NormalizedProduct { Brand = "Bitmain", Model = "S19j Pro" },
            comps, "Bitmain Antminer S19j Pro 104TH", "FIXED_PRICE", allowRealTerapeakScrape: false);

        var basis = PriceBasis.From(estimate, Confidence(), localCompsFound: comps.Count)!;

        // The figure the money is computed from, explained by the source that produced it.
        Assert.Equal(estimate.ExpectedSalePrice, basis.Price);
        var only = Assert.Single(basis.Sources);
        Assert.Equal(PriceBasis.HostedCompsKey, only.Key);
        Assert.Equal(5, only.CompCount);
        Assert.Equal(basis.Price, Reconstruct(basis), 2);
        // The weighted median is not the plain median on this set — which is why the trail has to
        // quote the one the blend used.
        Assert.Equal(410m, basis.MedianPrice);
        Assert.NotEqual(basis.MedianPrice, basis.Price);
    }

    // ── How old the evidence is ────────────────────────────────────────────────
    // Every weight in this panel is already age-adjusted: recent local comps pull harder in the
    // weighted median, and Terapeak's share is stepped down as its scrape ages. A reader shown the
    // percentages and not the dates is being asked to accept an adjustment whose reason is off
    // screen — and, worse, to read a price built from months-old sales as a price for today.

    private static readonly DateTime Now = new(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_local_source_states_the_span_its_sales_cover()
    {
        var estimate = LocalOnly();
        estimate.LocalOldestSoldAtUtc = new DateTime(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);
        estimate.LocalNewestSoldAtUtc = new DateTime(2026, 7, 28, 0, 0, 0, DateTimeKind.Utc);

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal("sales dated 12 Mar 2026 – 28 Jul 2026", basis.Sources[0].AsOf);
    }

    [Fact]
    public void Comps_that_all_sold_on_one_day_state_that_day_rather_than_a_span_of_none()
    {
        var estimate = LocalOnly();
        estimate.LocalOldestSoldAtUtc = new DateTime(2026, 7, 28, 3, 0, 0, DateTimeKind.Utc);
        estimate.LocalNewestSoldAtUtc = new DateTime(2026, 7, 28, 19, 0, 0, DateTimeKind.Utc);

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal("sales dated 28 Jul 2026", basis.Sources[0].AsOf);
    }

    /// <summary>
    /// Undated comps say nothing rather than something. The confidence score has a term for exactly
    /// this ("No comp carries a sale date"), and inventing a window here would contradict it.
    /// </summary>
    [Fact]
    public void Comps_with_no_dates_leave_the_line_off()
    {
        var basis = PriceBasis.From(LocalOnly(), Confidence(), nowUtc: Now)!;

        Assert.Equal("", basis.Sources[0].AsOf);
    }

    /// <summary>
    /// Terapeak's figure is a snapshot, so it carries the date it was taken and how long ago that
    /// was. The date alone makes the reader do the subtraction; the age alone hides which snapshot
    /// this is.
    /// </summary>
    [Theory]
    [InlineData(0, "scraped 3 Aug 2026 · today")]
    [InlineData(1, "scraped 2 Aug 2026 · yesterday")]
    [InlineData(9, "scraped 25 Jul 2026 · 9 days ago")]
    [InlineData(120, "scraped 5 Apr 2026 · 4 months ago")]
    [InlineData(500, "scraped 21 Mar 2025 · over a year ago")]
    public void The_terapeak_source_states_when_it_was_scraped_and_how_long_ago(int ageDays, string expected)
    {
        var estimate = Blended();
        estimate.TerapeakScrapedAtUtc = Now.AddDays(-ageDays);

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal(expected, basis.Sources[1].AsOf);
    }

    /// <summary>
    /// When age has already cost a source part of its pull, the panel says how much. Without it the
    /// share on screen is smaller than the comp counts explain and nothing accounts for the gap.
    /// </summary>
    [Fact]
    public void A_stale_terapeak_figure_says_what_its_age_already_cost_it()
    {
        var estimate = Blended();
        estimate.TerapeakScrapedAtUtc = Now.AddDays(-120);
        estimate.TerapeakFreshnessWeight = 0.4;

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal("counted at 40% of full weight for its age", basis.Sources[1].FreshnessNote);
    }

    /// <summary>"Counted at 100%" is not news, and a panel that says it teaches the reader to skim.</summary>
    [Fact]
    public void A_fresh_figure_says_nothing_about_its_freshness()
    {
        var estimate = Blended();
        estimate.TerapeakScrapedAtUtc = Now.AddHours(-6);

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal("", basis.Sources[1].FreshnessNote);
    }

    /// <summary>
    /// None of this touches the arithmetic. The dates are stated beside the figures, never folded
    /// into them, so the promise this whole class exists to keep still holds.
    /// </summary>
    [Fact]
    public void Dating_the_sources_does_not_disturb_the_reconciliation()
    {
        var estimate = Blended();
        estimate.LocalOldestSoldAtUtc = Now.AddDays(-200);
        estimate.LocalNewestSoldAtUtc = Now.AddDays(-3);
        estimate.TerapeakScrapedAtUtc = Now.AddDays(-120);
        estimate.TerapeakFreshnessWeight = 0.4;

        var basis = PriceBasis.From(estimate, Confidence(), nowUtc: Now)!;

        Assert.Equal(basis.Price, Reconstruct(basis), 2);
    }

    // ── The row it ends up on ──────────────────────────────────────────────────

    private static readonly LocalArbitrageAnalyzer Analyzer =
        new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static LocalSupplyListing Listing(decimal price) => new()
    {
        Source = "facebook", SourceLabel = "Facebook Marketplace", ItemId = "1",
        Title = "Bitmain Antminer S19j Pro", Url = "https://example.test/1", Price = price,
        Location = "Las Vegas, NV",
    };

    [Fact]
    public void The_trail_reaches_the_deal_row_that_shows_the_price()
    {
        var analysis = new MarketAnalysisResult
        {
            PriceEstimate = Blended(),
            SellThrough = new SellThroughAnalysis { SoldComparableCount = 7 },
            Stability = new PriceStability { StabilityScore = 72 },
            Sources = new SourceBreakdown { LocalComparableCount = 19, TerapeakComparableCount = 12 },
        };
        var resale = ResalePricing.From(analysis, "Bitmain Antminer S19j Pro 104TH");

        var row = Analyzer.Build(Listing(150m), resale, new FeeProfile());

        Assert.NotNull(row.PriceBasis);
        Assert.Equal(row.EbayExpectedSale, row.PriceBasis!.Price);
        Assert.Equal(2, row.PriceBasis.Sources.Count);
        // The count the search returned reaches the panel too, so the narrowing is visible there
        // and not only in the row's evidence line.
        Assert.Equal(19, row.PriceBasis.Sources[0].FoundCount);
    }

    /// <summary>
    /// The hand-built pricings — the trend projection, the negotiation endpoint — never ran a
    /// lookup, so they have a price and no working to show. The row must render, not throw.
    /// </summary>
    [Fact]
    public void A_pricing_that_never_ran_a_lookup_leaves_the_panel_off_rather_than_failing()
    {
        var resale = new ResalePricing
        {
            LookupTitle = "Bitmain Antminer S19j Pro", Median = 400m, ExpectedSale = 400m,
            SoldCompCount = 6, PricedCompCount = 6, ConfidenceScore = 70,
        };

        var row = Analyzer.Build(Listing(150m), resale, new FeeProfile());

        Assert.Equal(400m, row.EbayExpectedSale);
        Assert.Null(row.PriceBasis);
    }

    [Fact]
    public void A_row_nothing_priced_carries_no_trail()
    {
        var row = Analyzer.Build(Listing(150m), resale: null, new FeeProfile());

        Assert.Equal("no_data", row.Verdict);
        Assert.Null(row.PriceBasis);
    }
}
