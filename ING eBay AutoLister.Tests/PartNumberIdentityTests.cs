using ING_eBay_AutoLister.Services;
using Xunit;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The rule that decides whether two part numbers are the same product.
/// </summary>
/// <remarks>
/// Every one of these came off the live board on 2026-07-29. A Fanuc connector plug listed at $36
/// was matched to servo amplifiers that sold for $550-$1,256, because the tolerant model comparison
/// (60% token overlap, prefix matching) was also being used for part numbers: [a06b, 6130, k200] and
/// [a06b, 6130, h002] share two of three segments. It scored the top exact-identifier tier and the
/// board called it the day's best deal at 1187% ROI.
/// </remarks>
public class PartNumberIdentityTests
{
    [Theory]
    [InlineData("A06B-6130-K200", "A06B-6130-H002")]   // connector plug vs servo amplifier
    [InlineData("A06B-6130-K200", "A06B-6130-H003")]
    [InlineData("A06B-6079-H206", "A06B-6079-H106")]
    [InlineData("A20B-2002-0521", "A20B-2002-0520")]
    [InlineData("WS-C3850-48P-S", "WS-C3850-24P-S")]
    public void Two_parts_that_only_share_a_prefix_are_not_the_same_part(string a, string b)
        => Assert.False(ComparableMatcher.PartNumberMatch(a, b));

    [Theory]
    [InlineData("A06B-6130-H002", "A06B-6130-H002")]
    [InlineData("A06B-6130-H002", "A06B6130H002")]     // sellers drop the hyphens
    [InlineData("a06b-6130-h002", "A06B-6130-H002")]   // and the case
    [InlineData("A06B 6130 H002", "A06B-6130-H002")]   // and use spaces
    public void The_same_part_written_differently_still_matches(string a, string b)
        => Assert.True(ComparableMatcher.PartNumberMatch(a, b));

    [Theory]
    [InlineData(null, "A06B-6130-H002")]
    [InlineData("A06B-6130-H002", null)]
    [InlineData("", "A06B-6130-H002")]
    [InlineData("   ", "A06B-6130-H002")]
    public void A_missing_part_number_is_not_a_match(string? a, string? b)
    {
        // Absent is not the same as equal. A candidate with no extractable part number has to be
        // judged on model, brand and specs instead of being handed the top identifier tier.
        Assert.False(ComparableMatcher.PartNumberMatch(a, b));
    }

    [Fact]
    public void The_suffix_is_the_product()
    {
        // The whole point, stated once: everything before the last segment is a family, and a family
        // contains a $36 plug and a $1,256 drive. A wrong comp is worse than no comp - no comp shows
        // as "no sold data" and gets checked by hand, a wrong one gets bought.
        Assert.False(ComparableMatcher.PartNumberMatch("A06B-6130-K200", "A06B-6130-H002"));
        Assert.True(ComparableMatcher.PartNumberMatch("A06B-6130-K200", "A06B-6130-K200"));
    }
}
