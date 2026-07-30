using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class ConfidenceScoringServiceTests
{
    private static MarketAnalysisResult BaseResult() => new()
    {
        PriceEstimate = new PriceEstimate { LocalWeight = 0.6m, TerapeakWeight = 0.4m, MarketDataDisagreement = false },
        SellThrough = new SellThroughAnalysis { SoldComparableCount = 8 },
        Stability = new PriceStability { StabilityScore = 90 },
    };

    [Fact]
    public void Score_ManyStrongExactMatchesRecentAndAgreeing_IsHighConfidence()
    {
        var service = new ConfidenceScoringService();
        var result = BaseResult();

        var confidence = service.Score(result, strongComparableCount: 10, exactIdentifierMatches: 3,
            modelNumberMatches: 3, mostRecentComparableAgeDays: 5,
            conditionConsistent: true, quantityConsistent: true, categoryConsistent: true);

        Assert.True(confidence.Score >= 85, $"expected High Confidence range, got {confidence.Score}");
        Assert.Equal("High Confidence", confidence.Level);
    }

    [Fact]
    public void Score_NoComparablesAtAll_IsInsufficientEvidence()
    {
        var service = new ConfidenceScoringService();
        var result = BaseResult();
        result.SellThrough.SoldComparableCount = 0;
        result.PriceEstimate.TerapeakWeight = 0;
        result.Stability.StabilityScore = 0;

        var confidence = service.Score(result, strongComparableCount: 0, exactIdentifierMatches: 0,
            modelNumberMatches: 0, mostRecentComparableAgeDays: null,
            conditionConsistent: false, quantityConsistent: false, categoryConsistent: false);

        Assert.True(confidence.Score < 40, $"expected Insufficient Evidence range, got {confidence.Score}");
        Assert.Equal("Insufficient Evidence", confidence.Level);
    }

    [Fact]
    public void Score_MarketDataDisagreement_ScoresLowerThanAgreement()
    {
        var service = new ConfidenceScoringService();
        var agreeing = BaseResult();
        var disagreeing = BaseResult();
        disagreeing.PriceEstimate.MarketDataDisagreement = true;

        var agreeingScore = service.Score(agreeing, 6, 1, 1, 10, true, true, true);
        var disagreeingScore = service.Score(disagreeing, 6, 1, 1, 10, true, true, true);

        Assert.True(agreeingScore.Score > disagreeingScore.Score);
        Assert.Contains(disagreeingScore.Reasons, r => r.Contains("disagree", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Score_StaleData_ScoresLowerThanRecentData()
    {
        var service = new ConfidenceScoringService();
        var result = BaseResult();

        var recent = service.Score(result, 6, 1, 1, mostRecentComparableAgeDays: 10, true, true, true);
        var stale = service.Score(result, 6, 1, 1, mostRecentComparableAgeDays: 200, true, true, true);

        Assert.True(recent.Score > stale.Score);
    }

    // ── The score, taken apart ─────────────────────────────────────────────────
    //
    // A confidence number sits beside a profit figure someone is about to spend money against.
    // "62" on its own can only be accepted or ignored; the same 62 with its seven terms shown is
    // something a seller can check and act on. These pin the property that makes showing it
    // honest — the terms ARE the score, with nothing left over and nothing hidden.

    /// <summary>
    /// Seven terms, totalling exactly 100. Pinned because adding an eighth without adjusting the
    /// rest would let a full-marks item score 108 — and would draw every bar against a ceiling the
    /// arithmetic doesn't use.
    /// </summary>
    [Fact]
    public void The_terms_are_the_whole_score_and_nothing_more()
    {
        var confidence = new ConfidenceScoringService().Score(BaseResult(), 6, 1, 1, 10, true, true, true);

        Assert.Equal(7, confidence.Factors.Count);
        Assert.Equal(100.0, confidence.Factors.Sum(f => f.Possible), 3);
        Assert.Distinct(confidence.Factors.Select(f => f.Key));
    }

    /// <summary>
    /// The sum of what the terms earned is the score itself, not an approximation of it. Checked at
    /// both ends and in the middle, because a panel whose bars add to something other than the
    /// headline number is the exact failure this whole breakdown exists to avoid.
    /// </summary>
    [Theory]
    [InlineData(10, 3, 3, 5, true)]     // full marks everywhere
    [InlineData(0, 0, 0, null, false)]  // nothing at all
    [InlineData(4, 1, 2, 45, true)]     // the ordinary middle
    [InlineData(7, 2, 0, 200, false)]   // stale and inconsistent
    public void The_points_the_terms_earned_add_up_to_the_score(
        int comps, int identifiers, int models, int? ageDays, bool consistent)
    {
        var confidence = new ConfidenceScoringService().Score(
            BaseResult(), comps, identifiers, models, ageDays, consistent, consistent, consistent);

        // Each term is rounded to two decimals for display, so seven of them can drift by at most
        // 0.035 from the unrounded total the score was rounded from.
        Assert.Equal(confidence.Score, confidence.Factors.Sum(f => f.Earned), 0.6);
        Assert.All(confidence.Factors, f => Assert.InRange(f.Earned, 0, f.Possible));
    }

    [Fact]
    public void Every_term_says_what_this_row_actually_had()
    {
        var confidence = new ConfidenceScoringService().Score(BaseResult(), 8, 0, 2, 12, true, true, true);

        Assert.All(confidence.Factors, f => Assert.False(string.IsNullOrWhiteSpace(f.Detail)));
        Assert.Contains(confidence.Factors, f => f.Detail.Contains("8 strong comparable sales"));
        Assert.Contains(confidence.Factors, f => f.Detail.Contains("No comp matched on a part number"));
        Assert.Contains(confidence.Factors, f => f.Detail.Contains("12 days ago"));
    }

    /// <summary>A term that earned everything has nothing to ask for.</summary>
    [Fact]
    public void A_full_term_offers_no_lever_and_a_short_one_does()
    {
        var confidence = new ConfidenceScoringService().Score(BaseResult(), 10, 3, 1, 5, true, true, true);

        Assert.Null(confidence.Factors.Single(f => f.Key == "comps").Lever);
        Assert.Null(confidence.Factors.Single(f => f.Key == "identifier").Lever);
        Assert.NotNull(confidence.Factors.Single(f => f.Key == "model").Lever);
    }

    /// <summary>
    /// The headline gap is the one that cost the most points, not the one that looks worst. Here
    /// the like-for-like term is at zero — as red as a term gets — and the identifier term is worth
    /// four times as much and missing two thirds of it. A seller told to fix the five-point term
    /// while twenty points sit elsewhere has been sent to do the wrong thing.
    /// </summary>
    [Fact]
    public void The_biggest_gap_is_the_costliest_one_not_the_reddest_one()
    {
        var confidence = new ConfidenceScoringService().Score(
            BaseResult(), strongComparableCount: 10, exactIdentifierMatches: 1, modelNumberMatches: 3,
            mostRecentComparableAgeDays: 5,
            conditionConsistent: false, quantityConsistent: false, categoryConsistent: false);

        Assert.Equal(0, confidence.Factors.Single(f => f.Key == "consistency").Earned);
        Assert.NotNull(confidence.BiggestGap);
        Assert.Contains("part number", confidence.BiggestGap!);
        Assert.DoesNotContain("condition, quantity or category", confidence.BiggestGap!);
    }

    [Fact]
    public void A_thin_row_is_told_the_comps_are_what_is_missing()
    {
        var confidence = new ConfidenceScoringService().Score(BaseResult(), 1, 3, 3, 5, true, true, true);

        Assert.Contains("1 strong comparable sale", confidence.BiggestGap);
        Assert.Contains("10 strong comparable sales earn full marks", confidence.BiggestGap);
    }

    /// <summary>Nothing material missing, nothing to report — the panel doesn't invent an errand.</summary>
    [Fact]
    public void A_fully_evidenced_row_has_no_gap_to_report()
    {
        var result = BaseResult();
        result.Stability.StabilityScore = 100;

        var confidence = new ConfidenceScoringService().Score(result, 10, 3, 3, 5, true, true, true);

        Assert.Equal(100, confidence.Score);
        Assert.Null(confidence.BiggestGap);
    }

    /// <summary>
    /// The disagreement case says so in the term's own words, and its lever is a warning rather
    /// than an errand: there is nothing the seller can do to make two sources agree.
    /// </summary>
    [Fact]
    public void A_source_disagreement_zeroes_its_term_and_says_why()
    {
        var result = BaseResult();
        result.PriceEstimate.MarketDataDisagreement = true;

        var agreement = new ConfidenceScoringService()
            .Score(result, 6, 1, 1, 10, true, true, true)
            .Factors.Single(f => f.Key == "agreement");

        Assert.Equal(0, agreement.Earned);
        Assert.Equal(ConfidenceScoringService.AgreementPoints, agreement.Possible);
        Assert.Contains("disagree", agreement.Detail);
    }

    /// <summary>
    /// One source is neither corroborated nor contradicted, and the term says which source it was —
    /// "connect Terapeak" is useless advice on a row Terapeak already priced.
    /// </summary>
    [Fact]
    public void A_lone_source_earns_partial_credit_and_is_named()
    {
        var localOnly = BaseResult();
        localOnly.PriceEstimate.TerapeakWeight = 0m;

        var terapeakOnly = BaseResult();
        terapeakOnly.SellThrough.SoldComparableCount = 0;

        var service = new ConfidenceScoringService();
        var local = service.Score(localOnly, 6, 1, 1, 10, true, true, true).Factors.Single(f => f.Key == "agreement");
        var terapeak = service.Score(terapeakOnly, 6, 1, 1, 10, true, true, true).Factors.Single(f => f.Key == "agreement");

        Assert.InRange(local.Earned, 1, ConfidenceScoringService.AgreementPoints - 1);
        Assert.Contains("sold-comps database alone", local.Detail);
        Assert.Contains("Terapeak alone", terapeak.Detail);
    }

    /// <summary>
    /// The reasons list predates the factors and other screens still read it. Both are produced
    /// from the same pass, and neither may quietly stop being filled in.
    /// </summary>
    [Fact]
    public void The_older_reasons_list_is_still_produced_beside_the_factors()
    {
        var confidence = new ConfidenceScoringService().Score(BaseResult(), 8, 1, 1, 10, true, true, true);

        Assert.NotEmpty(confidence.Reasons);
        Assert.Contains(confidence.Reasons, r => r.Contains("8 strong comparables"));
        Assert.NotEmpty(confidence.Factors);
    }
}
