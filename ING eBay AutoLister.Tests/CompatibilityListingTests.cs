using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Every title here is real, taken from a live "fanuc" eBay Scanner run on 2026-08-11. The three
// accessories at the top of that board were bought-price $38/$60/$46 and priced against sold TEACH
// PENDANTS, reporting $981 / $1,152 / $560 of profit at up to 2589% ROI. They passed every identity
// check because the pendant's part number really is in the title — it is there to say what the
// strap fits.
public class CompatibilityListingTests
{
    private static bool IsCompat(string title) =>
        ProductNormalizer.DetectCompatibilityListing(title.ToLowerInvariant());

    [Theory]
    // The three rows the seller circled.
    [InlineData("1Pc For FANUC A05B-2518-C202 robot Teach Pendant new Wrist Strap hand grip tape")]
    [InlineData("For Fanuc A05B-2255-C102#ESW teach pendant keypad SWE2 overlay+rubber sleeve New")]
    [InlineData("Membrane Keypad for Fanuc A05B-2518-C202#EAW Teach Pendant AWE2 Overlay")]
    // Other shapes of the same claim.
    [InlineData("Replacement for Fanuc A06B-6079-H206 Servo Amplifier Cover")]
    [InlineData("Cable compatible with Fanuc A660-2005-T501 controller")]
    [InlineData("Keypad Suitable For Fanuc A05B-2301-C305 Pendant")]
    [InlineData("Membrane to fit A05B-2518-C202 pendant")]
    public void CompatibilityTitles_AreDetected(string title) => Assert.True(IsCompat(title));

    [Theory]
    // The honest rows from the same board — these must be untouched.
    [InlineData("Fanuc A06B-0041-B605#S042 aiSR 30/3000 AC Servo Motor 149 V 4.3 kW A06B (VT)")]
    [InlineData("Fanuc Pulsecoder aA1000i A860-2000-T301 *90 DAY WARRANTY*")]
    [InlineData("FANUC aA2/3000 A06B-0373-B175 AC SERVO MOTOR 0.5KW 129V 3000RPM 2.6A 3PH (32079)")]
    // "for" followed by a plain noun is a use-case, not a compatibility claim.
    [InlineData("Fanuc A06B-0373-B175 Servo Motor for CNC machining")]
    [InlineData("Antminer S19j Pro 104TH for home mining")]
    [InlineData("Lot of 4 Fanuc drives for sale")]
    // Words that merely contain the marker must not trigger it.
    [InlineData("Platform A05B-2518-C202 teach pendant, before service")]
    public void RealProductTitles_AreNotFlagged(string title) => Assert.False(IsCompat(title));

    [Fact]
    public void TheWristStrap_IsNoLongerComparableToATeachPendant()
    {
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        var matcher = new ComparableMatcher(normalizer);

        var strap = normalizer.Normalize(
            "1Pc For FANUC A05B-2518-C202 robot Teach Pendant new Wrist Strap hand grip tape");

        var pendantComp = new Models.MarketplaceComparableResult
        {
            ItemId = "1", Title = "FANUC A05B-2518-C202 Teach Pendant", SoldPrice = 1019m, TotalPrice = 1019m,
        };

        var match = matcher.Match(strap, pendantComp);

        Assert.True(match.Excluded);
        Assert.Contains("accessory", match.ExclusionReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ARealPendant_IsStillComparableToARealPendant()
    {
        // The guard must not cost the honest case its comps.
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        var matcher = new ComparableMatcher(normalizer);

        var pendant = normalizer.Normalize("FANUC A05B-2518-C202 Teach Pendant Tested");
        var comp = new Models.MarketplaceComparableResult
        {
            ItemId = "2", Title = "FANUC A05B-2518-C202 Teach Pendant", SoldPrice = 1019m, TotalPrice = 1019m,
        };

        Assert.False(matcher.Match(pendant, comp).Excluded);
    }

    [Fact]
    public void APendant_IsNotPricedOffStrapSales_Either()
    {
        // The reverse direction: cheap accessory comps must not drag a real unit's price down.
        var normalizer = new ProductNormalizer(new ProductIdentityExtractor());
        var matcher = new ComparableMatcher(normalizer);

        var pendant = normalizer.Normalize("FANUC A05B-2518-C202 Teach Pendant Tested");
        var strapComp = new Models.MarketplaceComparableResult
        {
            ItemId = "3", SoldPrice = 38m, TotalPrice = 38m,
            Title = "1Pc For FANUC A05B-2518-C202 robot Teach Pendant new Wrist Strap hand grip tape",
        };

        Assert.True(matcher.Match(pendant, strapComp).Excluded);
    }
}
