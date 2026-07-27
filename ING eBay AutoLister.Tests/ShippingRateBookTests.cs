using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class ShippingRateBookTests
{
    private static List<ZoneShare> Mix(params (int Zone, decimal Share)[] slices) =>
        slices.Select(s => new ZoneShare { Zone = s.Zone, SharePercent = s.Share }).ToList();

    private static readonly List<ZoneShare> Nationwide = ShippingZones.MixFor("43004");

    private static PackageSpec Box(decimal weightOz, decimal l = 10m, decimal w = 8m, decimal h = 4m) =>
        new() { WeightOz = weightOz, LengthIn = l, WidthIn = w, HeightIn = h, Source = "measured" };

    private static ShippingServiceQuote Of(List<ShippingServiceQuote> quotes, string code) =>
        quotes.Single(q => q.Code == code);

    [Fact]
    public void Cost_RisesWithBothWeightAndDistance()
    {
        var light = Of(ShippingRateBook.QuoteAll(Box(8m), Nationwide), "usps_ground_advantage");
        var heavy = Of(ShippingRateBook.QuoteAll(Box(160m), Nationwide), "usps_ground_advantage");

        Assert.True(heavy.ExpectedCost > light.ExpectedCost);
        Assert.True(light.FarthestZoneCost > light.NearestZoneCost);
        Assert.True(heavy.ZoneSpread > light.ZoneSpread,
            "A heavier package must be more sensitive to distance, not less.");
    }

    [Fact]
    public void Cost_IsMonotonicInWeightAcrossTheWholeRateCard()
    {
        decimal previous = 0m;
        for (var oz = 4m; oz <= 1000m; oz += 4m)
        {
            var quote = Of(ShippingRateBook.QuoteAll(Box(oz), Mix((5, 100m))), "usps_ground_advantage");
            Assert.True(quote.Eligible, $"Ground Advantage should carry {oz} oz.");
            Assert.True(quote.ExpectedCost >= previous,
                $"Cost fell going from just under {oz} oz to {oz} oz ({previous} -> {quote.ExpectedCost}).");
            previous = quote.ExpectedCost;
        }
    }

    [Fact]
    public void FlatRate_CostsTheSameInEveryZone()
    {
        var quote = Of(ShippingRateBook.QuoteAll(Box(160m, 10m, 8m, 5m), Nationwide), "usps_flat_medium");

        Assert.True(quote.Eligible);
        Assert.True(quote.IsFlatRate);
        Assert.Equal(0m, quote.ZoneSpread);
        Assert.Equal(17.60m, quote.ExpectedCost);
    }

    // The reason flat rate exists in this engine at all: a small heavy box crossing the country.
    [Fact]
    public void FlatRate_BeatsWeightBasedPricingOnSmallHeavyBoxes()
    {
        var package = Box(240m, 10m, 8m, 5m);   // 15 lb in a medium-flat-rate-sized box
        var quotes = ShippingRateBook.QuoteAll(package, Mix((8, 100m)));

        var flat = Of(quotes, "usps_flat_medium");
        var ground = Of(quotes, "usps_ground_advantage");

        Assert.True(flat.Eligible);
        Assert.True(flat.ExpectedCost < ground.ExpectedCost,
            $"Flat rate should win here: {flat.ExpectedCost} vs {ground.ExpectedCost}.");
    }

    [Fact]
    public void FlatRate_IsRejectedWhenTheBoxDoesNotFitInAnyOrientation()
    {
        var tooLong = Box(32m, 14m, 8m, 5m);   // 14" exceeds the medium box's 11" longest side
        var quote = Of(ShippingRateBook.QuoteAll(tooLong, Nationwide), "usps_flat_medium");

        Assert.False(quote.Eligible);
        Assert.Contains("Does not fit", quote.IneligibleReason);
    }

    [Fact]
    public void FlatRate_FitsWhenTheBoxOnlyWorksRotated()
    {
        // 5 x 11 x 8 is the medium box (11 x 8.5 x 5.5) with the axes shuffled.
        var quote = Of(ShippingRateBook.QuoteAll(Box(64m, 5m, 11m, 8m), Nationwide), "usps_flat_medium");
        Assert.True(quote.Eligible, quote.IneligibleReason);
    }

    [Fact]
    public void DimensionalWeight_BillsForTheAirInABigLightBox()
    {
        // 24 x 20 x 16 = 7680 cu in. Over a cubic foot, so USPS bills 7680/166 = 47 lb, not 3 lb.
        var bigAndLight = Box(48m, 24m, 20m, 16m);
        var quote = Of(ShippingRateBook.QuoteAll(bigAndLight, Nationwide), "usps_ground_advantage");

        Assert.True(quote.DimWeightApplied);
        Assert.Equal(47m * 16m, quote.BillableWeightOz);
        Assert.True(quote.BillableWeightOz > bigAndLight.WeightOz * 10m);
    }

    [Fact]
    public void DimensionalWeight_DoesNotApplyBelowOneCubicFootOnUsps()
    {
        var small = Box(16m, 10m, 8m, 4m);   // 320 cu in
        var quote = Of(ShippingRateBook.QuoteAll(small, Nationwide), "usps_ground_advantage");

        Assert.False(quote.DimWeightApplied);
        Assert.Equal(16m, quote.BillableWeightOz);
    }

    [Fact]
    public void DimensionalWeight_AppliesAtAnySizeOnUps()
    {
        var small = Box(16m, 12m, 10m, 8m);   // 960 cu in — under a cubic foot
        var quotes = ShippingRateBook.QuoteAll(small, Nationwide);

        Assert.False(Of(quotes, "usps_ground_advantage").DimWeightApplied);
        Assert.True(Of(quotes, "ups_ground").DimWeightApplied,
            "UPS applies dimensional weight without a one-cubic-foot threshold.");
    }

    [Fact]
    public void WeightCeiling_RejectsUspsAndLeavesUpsToCarryIt()
    {
        var miner = Box(704m, 22m, 16m, 14m);   // 44 lb
        var overweight = Box(1600m, 22m, 16m, 14m);   // 100 lb

        Assert.True(Of(ShippingRateBook.QuoteAll(miner, Nationwide), "usps_ground_advantage").Eligible);

        var quotes = ShippingRateBook.QuoteAll(overweight, Nationwide);
        Assert.False(Of(quotes, "usps_ground_advantage").Eligible);
        Assert.Contains("70 lb limit", Of(quotes, "usps_ground_advantage").IneligibleReason);
        Assert.True(Of(quotes, "ups_ground").Eligible, "UPS should still take a 100 lb box.");
    }

    // An ounce-scale cap has to be stated in ounces. The eBay Standard Envelope's 4 oz limit
    // rendered as "0.3 lb" reads as a rounding artefact rather than as the rule it actually is.
    [Fact]
    public void WeightCeiling_IsStatedInOuncesForTheOunceScaleServices()
    {
        var card = new PackageSpec { WeightOz = 9m, LengthIn = 6m, WidthIn = 4m, HeightIn = 0.2m, Profile = "trading card" };
        var quote = Of(ShippingRateBook.QuoteAll(card, Nationwide, "Trading Cards"), "ebay_standard_envelope");

        Assert.False(quote.Eligible);
        Assert.Contains("4 oz limit", quote.IneligibleReason);
        Assert.DoesNotContain("lb", quote.IneligibleReason);
    }

    [Fact]
    public void StandardEnvelope_IsOfferedOnlyForThinLightRestrictedGoods()
    {
        var card = new PackageSpec { WeightOz = 1m, LengthIn = 6m, WidthIn = 4m, HeightIn = 0.2m, Profile = "trading card" };
        var eligible = Of(ShippingRateBook.QuoteAll(card, Nationwide, "Trading Cards"), "ebay_standard_envelope");
        Assert.True(eligible.Eligible, eligible.IneligibleReason);
        Assert.Equal(0.62m, eligible.ExpectedCost);
        Assert.Equal(0m, eligible.ZoneSpread);

        // Same package, wrong goods.
        var wrongCategory = new PackageSpec { WeightOz = 1m, LengthIn = 6m, WidthIn = 4m, HeightIn = 0.2m, Profile = "jewelry" };
        Assert.False(Of(ShippingRateBook.QuoteAll(wrongCategory, Nationwide, "Fine Jewelry"), "ebay_standard_envelope").Eligible);

        // Right goods, too thick.
        var thick = new PackageSpec { WeightOz = 3m, LengthIn = 6m, WidthIn = 4m, HeightIn = 1m, Profile = "trading card" };
        var tooThick = Of(ShippingRateBook.QuoteAll(thick, Nationwide, "Trading Cards"), "ebay_standard_envelope");
        Assert.False(tooThick.Eligible);
        Assert.Contains("thick", tooThick.IneligibleReason);
    }

    [Fact]
    public void OversizeBoxes_CarryASurchargeOnTopOfTheWeightRate()
    {
        var normal = Box(320m, 20m, 14m, 10m);
        var oversize = Box(320m, 52m, 14m, 10m);   // longest side past 48"

        var normalQuote = Of(ShippingRateBook.QuoteAll(normal, Nationwide), "ups_ground");
        var oversizeQuote = Of(ShippingRateBook.QuoteAll(oversize, Nationwide), "ups_ground");

        Assert.Equal(0m, normalQuote.SurchargeAmount);
        Assert.True(oversizeQuote.SurchargeAmount > 0m);
        Assert.Contains("surcharge", oversizeQuote.SurchargeReason, StringComparison.OrdinalIgnoreCase);
        Assert.True(oversizeQuote.ExpectedCost > normalQuote.ExpectedCost);
    }

    [Fact]
    public void GirthLimit_RejectsALongThinBoxThatIsInsideEveryOtherLimit()
    {
        // 100 x 8 x 8: five real pounds, 39 lb dimensional (inside the 70 lb ceiling), and the
        // longest side is under 108" — so the only rule left to catch it is length plus girth,
        // 100 + 2*(8+8) = 132, past the 130" limit.
        var pole = Box(80m, 100m, 8m, 8m);
        var quote = Of(ShippingRateBook.QuoteAll(pole, Nationwide), "usps_ground_advantage");

        Assert.False(quote.Eligible);
        Assert.Contains("girth", quote.IneligibleReason, StringComparison.OrdinalIgnoreCase);
    }

    // Dimensional weight is checked before the size limits, because a box can be inside every
    // linear rule and still be too heavy once the air in it is billed.
    [Fact]
    public void WeightCeiling_CatchesABoxMadeHeavyOnlyByItsDimensions()
    {
        var bigAndLight = Box(80m, 100m, 12m, 12m);   // 14,400 cu in -> 87 lb dimensional
        var quote = Of(ShippingRateBook.QuoteAll(bigAndLight, Nationwide), "usps_ground_advantage");

        Assert.False(quote.Eligible);
        Assert.Contains("dimensional weight", quote.IneligibleReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ZeroWeight_IsNotPricedAsFreeShipping()
    {
        var quotes = ShippingRateBook.QuoteAll(new PackageSpec(), Nationwide);
        Assert.All(quotes, q => Assert.False(q.Eligible));
        Assert.All(quotes, q => Assert.Equal(0m, q.ExpectedCost));
    }

    [Fact]
    public void ExpectedCost_SitsBetweenTheNearAndFarZoneCosts()
    {
        var quote = Of(ShippingRateBook.QuoteAll(Box(96m), Nationwide), "usps_ground_advantage");
        Assert.InRange(quote.ExpectedCost, quote.NearestZoneCost, quote.FarthestZoneCost);
    }

    [Fact]
    public void Describe_ListsEveryServiceForTheReferenceTable()
    {
        var described = ShippingRateBook.Describe();
        Assert.True(described.Count >= 8);
    }
}
