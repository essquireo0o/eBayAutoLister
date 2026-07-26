using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// "Roll again" is the whole promise of the feature: a second roll has to dig somewhere new, and a
// handful of rolls has to have dug everywhere. That's a property of the rotation, so it's pinned
// here rather than left to whatever the sweep happened to return on the day.
public class CategorySweepTests
{
    [Fact]
    public void Universe_HasUniqueNichesAndProbes()
    {
        var ids = CategorySweep.Universe.Select(n => n.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var probes = CategorySweep.Universe.SelectMany(n => n.Probes).ToList();
        Assert.Equal(probes.Count, probes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(CategorySweep.Universe, n =>
        {
            Assert.NotEmpty(n.Probes);
            Assert.False(string.IsNullOrWhiteSpace(n.Label));
        });
    }

    // A probe of nothing but generic words matches nothing (see MarketplaceMatcher.ImportantWords),
    // so every probe has to carry at least two words worth searching on.
    [Fact]
    public void Universe_ProbesAreSpecificEnoughToMatchOn()
    {
        Assert.All(CategorySweep.Universe.SelectMany(n => n.Probes), probe =>
            Assert.True(MarketplaceMatcher.ImportantWords(MarketplaceMatcher.Normalize(probe)).Count >= 2,
                $"Probe \"{probe}\" has fewer than two matchable words."));
    }

    [Fact]
    public void Select_IsDeterministicForASeed()
    {
        var first = CategorySweep.Select(7, 4).Select(n => n.Id);
        var again = CategorySweep.Select(7, 4).Select(n => n.Id);
        Assert.Equal(first, again);
    }

    [Fact]
    public void Select_ConsecutiveRollsDigDifferentCategories()
    {
        var firstRoll = CategorySweep.Select(0, 4).Select(n => n.Id).ToList();
        var secondRoll = CategorySweep.Select(1, 4).Select(n => n.Id).ToList();

        Assert.Equal(4, firstRoll.Count);
        Assert.Empty(firstRoll.Intersect(secondRoll));
    }

    [Fact]
    public void Select_AFullLapCoversEveryCategory()
    {
        const int perRoll = 4;
        var covered = Enumerable.Range(0, CategorySweep.RollsToCoverEverything(perRoll))
            .SelectMany(seed => CategorySweep.Select(seed, perRoll))
            .Select(n => n.Id)
            .Distinct()
            .ToList();

        Assert.Equal(CategorySweep.Universe.Count, covered.Count);
    }

    [Fact]
    public void Select_NeverRepeatsANicheWithinOneRoll()
    {
        var roll = CategorySweep.Select(3, 8).Select(n => n.Id).ToList();
        Assert.Equal(roll.Count, roll.Distinct().Count());
    }

    // Seeds arrive from a URL, so a negative or absurd one has to be a position on the wheel, not
    // an index off the end of the array.
    [Theory]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    [InlineData(int.MaxValue)]
    public void Select_HandlesAnySeedTheClientSends(int seed)
    {
        var roll = CategorySweep.Select(seed, 4);
        Assert.Equal(4, roll.Count);
        Assert.All(roll, n => Assert.Contains(n, CategorySweep.Universe));
    }

    [Fact]
    public void Select_ClampsTheWindowToTheUniverse()
    {
        Assert.Equal(CategorySweep.Universe.Count, CategorySweep.Select(0, 999).Count);
        Assert.Single(CategorySweep.Select(0, 0));
    }

    [Fact]
    public void ProbesFor_RotatesWithTheSeedSoARepeatVisitDigsElsewhere()
    {
        var niche = CategorySweep.Universe.First(n => n.Probes.Length >= 2);

        Assert.NotEqual(CategorySweep.ProbesFor(niche, 0, 1), CategorySweep.ProbesFor(niche, 1, 1));
        Assert.Equal(CategorySweep.ProbesFor(niche, 0, 1), CategorySweep.ProbesFor(niche, 0, 1));
    }

    [Fact]
    public void ProbesFor_NeverAsksForMoreProbesThanTheNicheHas()
    {
        var niche = CategorySweep.Universe[0];
        var probes = CategorySweep.ProbesFor(niche, 0, 99);

        Assert.Equal(niche.Probes.Length, probes.Count);
        Assert.Equal(probes.Count, probes.Distinct().Count());
    }

    [Fact]
    public void NextSeed_WrapsInsteadOfOverflowing()
    {
        Assert.Equal(1, CategorySweep.NextSeed(0));
        Assert.Equal(0, CategorySweep.NextSeed(int.MaxValue));
    }

    [Fact]
    public void RollsToCoverEverything_RoundsUpSoNothingIsLeftOut()
    {
        Assert.Equal(CategorySweep.Universe.Count, CategorySweep.RollsToCoverEverything(1));
        Assert.Equal((int)Math.Ceiling(CategorySweep.Universe.Count / 3.0), CategorySweep.RollsToCoverEverything(3));
        Assert.Equal(1, CategorySweep.RollsToCoverEverything(CategorySweep.Universe.Count));
    }
}
