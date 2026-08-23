using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

public class CouponDiscoveryTests
{
    [Fact]
    public void A_massive_percentage_code_becomes_a_ranked_lead()
    {
        var lead = CouponService.ToDiscoveryOpportunity(Code(35m), 20);

        Assert.NotNull(lead);
        Assert.Equal(35m, lead!.EffectiveDiscountPercent);
        Assert.Equal("35% off", lead.DiscountLabel);
        Assert.True(lead.OpportunityScore >= 60);
    }

    [Fact]
    public void A_large_dollar_code_is_kept_when_the_threshold_makes_it_meaningful()
    {
        var offer = Code(100m, CouponKinds.AmountOff);
        offer.MinSpend = 400m;

        var lead = CouponService.ToDiscoveryOpportunity(offer, 20);

        Assert.NotNull(lead);
        Assert.Equal(25m, lead!.EffectiveDiscountPercent);
        Assert.Contains("$100", lead.DiscountLabel);
    }

    [Fact]
    public void Ordinary_noise_and_non_checkout_savings_do_not_pad_the_section()
    {
        Assert.Null(CouponService.ToDiscoveryOpportunity(Code(10m), 20));

        var cashback = Code(40m, CouponKinds.Cashback);
        Assert.Null(CouponService.ToDiscoveryOpportunity(cashback, 20));

        var noCode = Code(40m);
        noCode.Code = "";
        Assert.Null(CouponService.ToDiscoveryOpportunity(noCode, 20));
    }

    [Fact]
    public void Expired_codes_never_become_buying_leads()
    {
        var offer = Code(50m);
        offer.ExpiresUtc = DateTime.UtcNow.AddMinutes(-1);

        Assert.Null(CouponService.ToDiscoveryOpportunity(offer, 20));
    }

    [Fact]
    public void A_past_date_range_in_the_headline_cannot_hide_missing_expiry_metadata()
    {
        var offer = Code(50m);
        offer.Title = "Macy's beauty products 50% Off (Aug. 7 - Aug. 16)";

        Assert.True(CouponService.HasPastDateRange(offer.Title, new DateTime(2026, 8, 23)));
        Assert.Null(CouponService.ToDiscoveryOpportunity(offer, 20));
    }

    [Fact]
    public void A_current_or_future_date_range_remains_eligible()
    {
        Assert.False(CouponService.HasPastDateRange(
            "Holiday sale (Dec. 28 - Jan. 5)", new DateTime(2026, 12, 30)));
        Assert.False(CouponService.HasPastDateRange(
            "Summer sale (Aug. 20 - Aug. 30)", new DateTime(2026, 8, 23)));
    }

    [Fact]
    public void Checkout_instructions_are_removed_before_ebay_pricing()
    {
        var offer = Code(30m);
        offer.Title = "Samsung 990 Pro 2TB SSD with promo code FAST30 at checkout";

        Assert.Equal("Samsung 990 Pro 2TB SSD", CouponService.ProductQuery(offer));
    }

    [Fact]
    public void The_opportunity_finder_has_a_dedicated_coupon_discovery_and_resale_handoff()
    {
        var html = ReadSource(Path.Combine("wwwroot", "index.html"));
        var js = ReadSource(Path.Combine("wwwroot", "app.js"));
        var program = ReadSource("Program.cs");

        Assert.Contains("id=\"coupon-hunt-title\"", html);
        Assert.Contains("id=\"coupon-hunt-btn\"", html);
        Assert.Contains("/api/coupons/opportunities", program);
        Assert.Contains("10 * 60 * 1000", js);
        Assert.Contains("data-coupon-price", js);
        Assert.Contains("Discount ≠ profit", html);
    }

    private static CouponOffer Code(decimal value, string kind = CouponKinds.PercentOff) => new()
    {
        MerchantId = "newegg",
        MerchantLabel = "Newegg",
        Kind = kind,
        Code = "SAVEBIG",
        Value = value,
        AppliesToOrder = false,
        Title = "Samsung 990 Pro 2TB SSD",
        Url = "https://example.test/deal",
        SourceLabel = "Test feed",
        Confidence = CouponConfidence.High,
        PublishedUtc = DateTime.UtcNow.AddHours(-2),
    };

    private static string ReadSource(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;

        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        var path = Path.Combine(dir!.FullName, "ING eBay AutoLister", relative);
        Assert.True(File.Exists(path), "missing source: " + path);
        return File.ReadAllText(path);
    }
}
