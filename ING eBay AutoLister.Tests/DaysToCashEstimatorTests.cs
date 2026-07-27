using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Days-to-cash is the number that changes which row a seller buys, so what's pinned here is the
// honesty of it: the wait always includes the days after the sale that no velocity figure covers,
// an unmeasured product is never quietly treated as a fast one, and profit-per-day is what makes a
// small quick flip beat a big slow one.
public class DaysToCashEstimatorTests
{
    [Fact]
    public void Estimate_AddsShipAndPayoutTimeToTheTimeToSell()
    {
        var estimate = DaysToCashEstimator.Estimate(estimatedDaysToSell: 10, estimatedMonthlySales: 3m);

        Assert.Equal(10, estimate.DaysToSell);
        Assert.Equal(DaysToCashEstimator.PipelineDays, estimate.PipelineDays);
        // A sale is not cash: it still has to be packed, delivered and paid out.
        Assert.Equal(10 + DaysToCashEstimator.PipelineDays, estimate.DaysToCash);
    }

    [Fact]
    public void Estimate_FallsBackToTheMonthlySalesRateWhenDaysToSellIsMissing()
    {
        // 6 a month is one every 5 days.
        var estimate = DaysToCashEstimator.Estimate(estimatedDaysToSell: null, estimatedMonthlySales: 6m);

        Assert.Equal(5, estimate.DaysToSell);
        Assert.Equal(5 + DaysToCashEstimator.PipelineDays, estimate.DaysToCash);
    }

    [Fact]
    public void Estimate_NoSoldHistoryIsUnknown_NotFast()
    {
        var estimate = DaysToCashEstimator.Estimate(estimatedDaysToSell: null, estimatedMonthlySales: 0m,
            netProfit: 120m, roiPercent: 90m);

        Assert.Null(estimate.DaysToCash);
        Assert.Null(estimate.ProfitPerDay);
        Assert.Null(estimate.AnnualizedRoiPercent);
        Assert.Equal("unknown", estimate.SpeedTier);
        // And it must sort behind everything that was actually measured, not ahead of it.
        Assert.Equal(int.MaxValue, DaysToCashEstimator.SortableDaysToCash(estimate.DaysToCash));
    }

    [Fact]
    public void Estimate_ProfitPerDayIsTheProfitSpreadOverTheWholeWait()
    {
        // 22 days to sell + 8 pipeline = 30 days; $60 over 30 days is $2/day.
        var estimate = DaysToCashEstimator.Estimate(22, 1.4m, netProfit: 60m, roiPercent: 50m);

        Assert.Equal(30, estimate.DaysToCash);
        Assert.Equal(2.00m, estimate.ProfitPerDay);
    }

    [Fact]
    public void Estimate_SmallFastFlipEarnsMorePerDayThanABigSlowOne()
    {
        var quick = DaysToCashEstimator.Estimate(estimatedDaysToSell: 7, 4m, netProfit: 40m, roiPercent: 80m);
        var fat = DaysToCashEstimator.Estimate(estimatedDaysToSell: 150, 0.2m, netProfit: 200m, roiPercent: 80m);

        // The whole premise of the feature: $40 back this month beats $200 back next spring.
        Assert.True(quick.ProfitPerDay > fat.ProfitPerDay);
        Assert.True(quick.CapitalTurnsPerYear > fat.CapitalTurnsPerYear);
    }

    [Fact]
    public void Estimate_AnnualizesRoiByHowOftenTheMoneyComesBack()
    {
        // 365 days to cash — exactly one turn a year, so the annualized rate is the ROI itself.
        var estimate = DaysToCashEstimator.Estimate(365 - DaysToCashEstimator.PipelineDays, 0.1m,
            netProfit: 50m, roiPercent: 40m);

        Assert.Equal(1.0m, estimate.CapitalTurnsPerYear);
        Assert.Equal(40m, estimate.AnnualizedRoiPercent);
    }

    [Fact]
    public void Estimate_DoesNotAnnualizeALoss()
    {
        var estimate = DaysToCashEstimator.Estimate(10, 3m, netProfit: -25m, roiPercent: -30m);

        // A loss has no rate of return to annualize, but the daily bleed is still real and reported.
        Assert.Null(estimate.AnnualizedRoiPercent);
        Assert.True(estimate.ProfitPerDay < 0);
    }

    [Theory]
    [InlineData(1, "fast")]
    [InlineData(DaysToCashEstimator.FastCashDays - DaysToCashEstimator.PipelineDays, "fast")]
    [InlineData(DaysToCashEstimator.SteadyCashDays - DaysToCashEstimator.PipelineDays, "steady")]
    [InlineData(DaysToCashEstimator.SlowCashDays - DaysToCashEstimator.PipelineDays, "slow")]
    [InlineData(200, "dead_money")]
    public void Estimate_TiersOnTheWholeWaitNotJustTheTimeToSell(int daysToSell, string expectedTier)
    {
        var estimate = DaysToCashEstimator.Estimate(daysToSell, 1m, netProfit: 50m, roiPercent: 40m);

        Assert.Equal(expectedTier, estimate.SpeedTier);
        Assert.NotEqual("", estimate.Note);
    }

    [Fact]
    public void Estimate_AnItemThatSellsInsideThePipelineIsStillNotSameDayCash()
    {
        // Sells the day it's listed — the money still can't be spent for over a week.
        var estimate = DaysToCashEstimator.Estimate(estimatedDaysToSell: 1, 30m, netProfit: 20m, roiPercent: 100m);

        Assert.Equal(1 + DaysToCashEstimator.PipelineDays, estimate.DaysToCash);
        Assert.True(estimate.DaysToCash > DaysToCashEstimator.PipelineDays);
    }

    [Fact]
    public void DaysToSell_IgnoresNonsenseInputs()
    {
        Assert.Null(DaysToCashEstimator.DaysToSell(0, 0m));
        Assert.Null(DaysToCashEstimator.DaysToSell(-5, 0m));
        // A negative velocity is not a fast seller either.
        Assert.Null(DaysToCashEstimator.DaysToSell(null, -3m));
    }
}
