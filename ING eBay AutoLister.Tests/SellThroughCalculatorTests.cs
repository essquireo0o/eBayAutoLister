using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class SellThroughCalculatorTests
{
    private static SellThroughCalculator CreateCalculator() => new(new LiquidityScoringService());

    private static List<MarketplaceComparableResult> SoldComparables(int count, DateTime nowUtc) =>
        Enumerable.Range(0, count)
            .Select(i => new MarketplaceComparableResult
            {
                ItemId = $"sold-{i}", Title = "item", SoldPrice = 50m,
                SoldDate = nowUtc.AddDays(-i * 2), MatchScore = 80,
            })
            .ToList();

    [Fact]
    public void Calculate_TenSoldAgainstTenActive_Is100PercentSellThrough()
    {
        var calc = CreateCalculator();
        var now = DateTime.UtcNow;

        var result = calc.Calculate("query", SoldComparables(10, now), activeComparableCount: 10, nowUtc: now);

        Assert.Equal(100m, result.SellThroughRate);
        Assert.False(result.RateIsUnbounded);
        Assert.Equal("Excellent", result.Interpretation);
        Assert.Equal(100, result.SellThroughScore);
    }

    [Fact]
    public void Calculate_ZeroActiveListingsWithSoldHistory_NeverReturnsInfiniteRate()
    {
        var calc = CreateCalculator();
        var now = DateTime.UtcNow;

        var result = calc.Calculate("query", SoldComparables(5, now), activeComparableCount: 0, nowUtc: now);

        Assert.Null(result.SellThroughRate);
        Assert.True(result.RateIsUnbounded);
        // A missing denominator must NOT masquerade as "Excellent / 100%" — it is reported as
        // unverified/low-confidence so zero-competition noise can't look like a guaranteed flip.
        Assert.Contains("unverified", result.Interpretation, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(100, result.SellThroughScore);
    }

    [Fact]
    public void Calculate_ZeroActiveAndZeroSold_ReturnsUnknownNotUnbounded()
    {
        var calc = CreateCalculator();

        var result = calc.Calculate("query", [], activeComparableCount: 0);

        Assert.False(result.RateIsUnbounded);
        Assert.Equal("Unknown", result.Interpretation);
        Assert.Equal(0, result.SellThroughScore);
    }

    [Theory]
    [InlineData(1, 40, "Poor")]      // 2.5% — strictly under 5%
    [InlineData(3, 15, "Moderate")]  // 20%
    [InlineData(4, 8, "Good")]       // 50%
    [InlineData(6, 10, "Very Strong")] // 60%
    public void Calculate_InterpretationBands_MatchExpectedLabel(int sold, int active, string expectedInterpretation)
    {
        var calc = CreateCalculator();
        var now = DateTime.UtcNow;

        var result = calc.Calculate("query", SoldComparables(sold, now), active, nowUtc: now);

        Assert.Equal(expectedInterpretation, result.Interpretation);
    }

    [Fact]
    public void Calculate_LowActiveCompetition_ReducesEstimatedDaysToSell()
    {
        var calc = CreateCalculator();
        var now = DateTime.UtcNow;
        // Sparse, widely-spaced sales so the base days-to-sell estimate is large enough for the
        // competition multiplier (0.85x low / 1.2x high) to survive integer rounding.
        var sparseSold = new List<MarketplaceComparableResult>
        {
            new() { ItemId = "a", Title = "item", SoldPrice = 50m, SoldDate = now.AddDays(-30), MatchScore = 80 },
            new() { ItemId = "b", Title = "item", SoldPrice = 50m, SoldDate = now.AddDays(-90), MatchScore = 80 },
            new() { ItemId = "c", Title = "item", SoldPrice = 50m, SoldDate = now.AddDays(-150), MatchScore = 80 },
        };

        var lowCompetition = calc.Calculate("query", sparseSold, activeComparableCount: 2, nowUtc: now);
        var highCompetition = calc.Calculate("query", sparseSold, activeComparableCount: 50, nowUtc: now);

        Assert.NotNull(lowCompetition.EstimatedDaysToSell);
        Assert.NotNull(highCompetition.EstimatedDaysToSell);
        Assert.True(lowCompetition.EstimatedDaysToSell < highCompetition.EstimatedDaysToSell,
            $"expected low-competition days ({lowCompetition.EstimatedDaysToSell}) < high-competition days ({highCompetition.EstimatedDaysToSell})");
    }
}
