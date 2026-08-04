using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// A watch made from a play runs unattended and fires a desktop notification at whatever hour a
/// listing appears, with room for one sentence and no room to explain a caveat. So what is pinned
/// here is mostly what it REFUSES to create — and that the watch it does create carries the same
/// bar the target price on the board was computed from, never a friendlier one.
/// </summary>
public class PlayWatchBuilderTests
{
    private static readonly FeeProfile Fees = new();
    private static readonly JackpotHunter Hunter = new(new ProfitCalculator());
    private static readonly LocalArbitrageAnalyzer Arbitrage = new(new ProfitCalculator(), new LiquidationLotPricer(new ProfitCalculator()));

    private static ResalePricing Pricing(decimal? expected = 800m, int soldComps = 8, int confidence = 70) => new()
    {
        LookupTitle = "Bitmain Antminer S19j Pro 104TH",
        Median = expected, ExpectedSale = expected, QuickSale = expected * 0.85m,
        SoldCompCount = soldComps, ConfidenceScore = confidence, ConfidenceLevel = "Good Confidence",
        EstimatedDaysToSell = 14,
    };

    private static JackpotCandidate Candidate(string title = "Bitmain Antminer S19j Pro 104TH") => new()
    {
        NicheId = "mining", NicheLabel = "Crypto mining hardware", Probe = "antminer s19",
        Key = "s19j", LookupTitle = title, CompCount = 6, MedianSold = 800m,
        LowSold = 700m, HighSold = 900m, RecentCompCount = 4, NewestCompAgeDays = 10,
    };

    private static PlayWatchRequest RequestFrom(JackpotPlay play, string zip = "89101", int radius = 40) => new()
    {
        Product = play.Product, SearchQuery = play.SearchQuery,
        TargetBuyPrice = play.TargetBuyPrice, MaxBuyPrice = play.MaxBuyPrice,
        SoldCompCount = play.SoldCompCount, TerapeakCompCount = play.TerapeakCompCount,
        ConfidenceScore = play.ConfidenceScore, ZipCode = zip, RadiusMiles = radius,
        Sources = "craigslist,facebook",
    };

    // ── What the board is allowed to offer ────────────────────────────────────

    [Fact]
    public void APlayWithNoSupplyButBelievableEvidenceIsTheWholePointOfThisFeature()
    {
        // The "target" tier: nothing for sale today, a real price to pay when one appears. It is
        // the roll's most useful output and, until this existed, the only one nobody could act on.
        var play = Hunter.BuildPlay(Candidate(), Pricing(), [], Fees);

        Assert.Equal("target", play.Tier);
        Assert.True(play.CanWatch);
        Assert.Null(play.WatchRefusal);
    }

    [Fact]
    public void ThinEvidenceIsNeverWorthWakingSomeoneUpFor()
    {
        // Below the comp count the board itself will bet on. The arithmetic can be enormous and it
        // still must not become a 2am notification — this is the same rule
        // DealWatch.RequireConfidentEvidence exists for, applied one level earlier.
        var play = Hunter.BuildPlay(Candidate(), Pricing(soldComps: 3), [], Fees);

        Assert.False(play.CanWatch);
        Assert.Contains("3 sold comps", play.WatchRefusal);

        var (watch, refusal) = PlayWatchBuilder.Build(RequestFrom(play));
        Assert.Null(watch);
        Assert.Contains("3 sold comps", refusal);
    }

    [Fact]
    public void PlentyOfCompsThatMightNotBeTheSameProductIsAlsoRefused()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(soldComps: 20, confidence: 30), [], Fees);

        Assert.False(play.CanWatch);
        Assert.Contains("confidence", play.WatchRefusal, StringComparison.OrdinalIgnoreCase);
        // And the refusal names the real blocker rather than the comp count, which is fine here.
        Assert.DoesNotContain("sold comp", play.WatchRefusal);
    }

    [Fact]
    public void AProductNoPriceCanMakeADealOfHasNothingToWatchFor()
    {
        // A $60 item never clears the jackpot bar however cheaply it's bought, so its target price
        // is zero. Watching "at break-even" would wake the seller for a flip worth nothing.
        var play = Hunter.BuildPlay(Candidate(), Pricing(expected: 60m), [], Fees);

        Assert.Equal(0m, play.TargetBuyPrice);
        Assert.False(play.CanWatch);
        Assert.Contains("nothing to watch for", play.WatchRefusal);
    }

    [Fact]
    public void APlayWithNoSoldHistoryAtAllSaysSoRatherThanOfferingAButton()
    {
        var play = Hunter.BuildPlay(Candidate(), null, [], Fees);

        Assert.Equal("no_data", play.Tier);
        Assert.False(play.CanWatch);
        Assert.Contains("no price to watch for", play.WatchRefusal);
    }

    [Fact]
    public void APlayWithLiveSupplyCanStillBeWatchedForTheNextOne()
    {
        // One Craigslist post is one chance. A jackpot found today is exactly the product worth
        // being told about again next month, so live supply is not a reason to refuse.
        var resale = Pricing();
        var option = JackpotSourceOption.From(Arbitrage.Build(
            new LocalSupplyListing
            {
                Source = "craigslist", SourceLabel = "Craigslist", ItemId = "1",
                Title = "Antminer S19j Pro", Url = "https://lasvegas.craigslist.org/1.html",
                Price = 50m, Location = "Las Vegas, NV",
            }, resale, Fees));

        var play = Hunter.BuildPlay(Candidate(), resale, [option], Fees);

        Assert.Equal("jackpot", play.Tier);
        Assert.True(play.CanWatch);
    }

    // ── The watch it builds ───────────────────────────────────────────────────

    [Fact]
    public void TheCeilingIsThePlaysOwnTargetPriceAndTheBarIsTheAppsOwnJackpotBar()
    {
        var play = Hunter.BuildPlay(Candidate(), Pricing(), [], Fees);
        var (watch, refusal) = PlayWatchBuilder.Build(RequestFrom(play));

        Assert.Null(refusal);
        Assert.NotNull(watch);

        // Never the break-even price: paying that earns nothing.
        Assert.Equal(play.TargetBuyPrice, watch!.MaxAsk);
        Assert.NotEqual(play.MaxBuyPrice, watch.MaxAsk);

        // The bar the target price was computed from, so a watch made here can never fire on
        // something the board that made it would call a pass. Stricter than a hand-typed watch's
        // $75/40% defaults on purpose — nobody typed this one and it runs unattended.
        Assert.Equal(LocalArbitrageAnalyzer.GoldmineProfit, watch.MinNetProfit);
        Assert.Equal(LocalArbitrageAnalyzer.GoldmineRoiPercent, watch.MinRoiPercent);
        Assert.True(watch.RequireConfidentEvidence);

        Assert.Equal(play.SearchQuery, watch.Query);
        Assert.Equal("89101", watch.ZipCode);
        Assert.Equal(40, watch.RadiusMiles);
        Assert.Equal("craigslist,facebook", watch.Sources);
        Assert.True(watch.Enabled);
        Assert.Equal(DealRadarClock.DefaultIntervalMinutes, watch.IntervalMinutes);
    }

    [Fact]
    public void BuyingAtTheCeilingActuallyClearsTheBarTheWatchCarries()
    {
        // The two numbers are computed by different code (TargetBuyPrice here, LocalArbitrage's
        // verdict there). If they ever drift, the watch becomes one that can never fire.
        var resale = Pricing();
        var play = Hunter.BuildPlay(Candidate(), resale, [], Fees);
        var (watch, _) = PlayWatchBuilder.Build(RequestFrom(play));

        var atCeiling = Arbitrage.Build(
            new LocalSupplyListing
            {
                Source = "craigslist", SourceLabel = "Craigslist", ItemId = "1",
                Title = "Antminer S19j Pro", Url = "https://lasvegas.craigslist.org/1.html",
                Price = watch!.MaxAsk, Location = "Las Vegas, NV",
            }, resale, Fees);

        Assert.True(atCeiling.NetProfit >= watch.MinNetProfit);
        Assert.True(atCeiling.RoiPercent >= watch.MinRoiPercent);
    }

    [Fact]
    public void TheNameCarriesThePriceAndNeverRoundsItUp()
    {
        // "under $58" against a target of $57.94 is a name that talks the seller into overpaying.
        Assert.Equal("Dyson V11 under $57", PlayWatchBuilder.WatchName("Dyson V11", 57.94m));
        Assert.Equal("Dyson V11 under $57", PlayWatchBuilder.WatchName("  Dyson   V11 ", 57.04m));
    }

    [Fact]
    public void ALongProductTitleIsCutAtAWordRatherThanMidWord()
    {
        var name = PlayWatchBuilder.WatchName(
            "Bitmain Antminer S19j Pro 104TH Bitcoin Miner With Power Supply Included", 400m);

        Assert.StartsWith("Bitmain Antminer S19j Pro 104TH Bitcoin", name, StringComparison.Ordinal);
        Assert.Contains("…", name);
        Assert.EndsWith("under $400", name, StringComparison.Ordinal);
        // Short enough to read on a radar card, long enough to tell two products apart.
        Assert.True(name.Length <= PlayWatchBuilder.MaxProductNameLength + 12, name);
    }

    [Fact]
    public void AnUnnamedProductStillGetsAUsableName()
    {
        Assert.Equal("Under $400", PlayWatchBuilder.WatchName("   ", 400m));
    }

    // ── Not spending one of the twelve slots twice ────────────────────────────

    [Fact]
    public void TheSameSearchWrittenTwoWaysIsOneSearch()
    {
        // Rolling again re-surfaces products, and the button must not create a second watch for a
        // search the radar already runs.
        Assert.True(PlayWatchBuilder.SameSearch("dyson v11 torque", "  Dyson   V11  Torque "));
        Assert.False(PlayWatchBuilder.SameSearch("dyson v11", "dyson v15"));
        Assert.True(PlayWatchBuilder.SameSearch(null, ""));
    }

    // ── The round trip is not trusted ─────────────────────────────────────────

    [Fact]
    public void NumbersThatArriveOverHttpAreCheckedAgainRatherThanBelieved()
    {
        // The board hiding the button is a courtesy. A request naming a believable target price on
        // evidence that isn't there is refused by the same rule, not saved.
        var (watch, refusal) = PlayWatchBuilder.Build(new PlayWatchRequest
        {
            Product = "Antminer S19j Pro", SearchQuery = "antminer s19j pro",
            TargetBuyPrice = 400m, MaxBuyPrice = 700m,
            SoldCompCount = 1, ConfidenceScore = 95,
        });

        Assert.Null(watch);
        Assert.Contains("1 sold comp", refusal);
    }

    [Fact]
    public void ARequestWithNoKeywordIsRefusedRatherThanSavedAsAWatchForEverything()
    {
        var (watch, refusal) = PlayWatchBuilder.Build(new PlayWatchRequest
        {
            Product = "Something", SearchQuery = "   ",
            TargetBuyPrice = 400m, SoldCompCount = 9, ConfidenceScore = 80,
        });

        Assert.Null(watch);
        Assert.Contains("no keyword", refusal);
    }

    [Fact]
    public void TerapeakCompsCountTowardsTheEvidenceBarTheSameAsSoldComps()
    {
        // Two reads of the same market. A play priced mostly off Terapeak is not thinner evidence
        // than one priced off the comps database, and refusing it would be arbitrary.
        var (watch, refusal) = PlayWatchBuilder.Build(new PlayWatchRequest
        {
            Product = "Antminer S19j Pro", SearchQuery = "antminer s19j pro",
            TargetBuyPrice = 400m, SoldCompCount = 2, TerapeakCompCount = 4, ConfidenceScore = 80,
        });

        Assert.Null(refusal);
        Assert.NotNull(watch);
    }
}
