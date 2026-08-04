using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// If these settings do not survive a restart, every net-profit figure in the app quietly reverts
// to "eBay's cut and nothing else" — which looks like a working feature and is a wrong number.
[Collection(PooledSqliteTests.Name)]
public class FeeProfileStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"fee_profile_{Guid.NewGuid():N}.db");

    private FeeProfileStore NewStore() => new(_dbPath);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath)) File.Delete(_dbPath);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_WithNothingSaved_ReturnsTheBuiltInDefaults()
    {
        var profile = NewStore().Load();

        Assert.Equal(13.25m, profile.EbayFinalValueFeePercent);
        Assert.Equal(0m, profile.DefaultShippingCost);
        Assert.Equal(0m, profile.MinimumNetProfit);
    }

    [Fact]
    public void Save_ThenLoadFromANewStore_ReturnsEveryField()
    {
        NewStore().Save(new FeeProfile
        {
            EbayFinalValueFeePercent = 12.9m, EbayFinalValueFeeFixed = 0.30m,
            PromotedListingRatePercent = 2.5m, PaymentProcessingPercent = 1.5m,
            DefaultShippingCost = 9.45m, DefaultPackagingCost = 1.25m, DefaultLaborCost = 4m,
            ReturnReservePercent = 3m, TestingReservePercent = 1.5m,
            MinimumNetProfit = 12m, MinimumMarginPercent = 18m,
        });

        var reloaded = NewStore().Load();   // a fresh store, as if the app had restarted

        Assert.Equal(12.9m, reloaded.EbayFinalValueFeePercent);
        Assert.Equal(0.30m, reloaded.EbayFinalValueFeeFixed);
        Assert.Equal(2.5m, reloaded.PromotedListingRatePercent);
        Assert.Equal(1.5m, reloaded.PaymentProcessingPercent);
        Assert.Equal(9.45m, reloaded.DefaultShippingCost);
        Assert.Equal(1.25m, reloaded.DefaultPackagingCost);
        Assert.Equal(4m, reloaded.DefaultLaborCost);
        Assert.Equal(3m, reloaded.ReturnReservePercent);
        Assert.Equal(1.5m, reloaded.TestingReservePercent);
        Assert.Equal(12m, reloaded.MinimumNetProfit);
        Assert.Equal(18m, reloaded.MinimumMarginPercent);
    }

    [Fact]
    public void Save_IsIdempotentRatherThanAccumulating()
    {
        var store = NewStore();
        store.Save(new FeeProfile { DefaultShippingCost = 5m });
        store.Save(new FeeProfile { DefaultShippingCost = 7m });

        Assert.Equal(7m, store.Load().DefaultShippingCost);
    }

    [Fact]
    public void Save_ClampsAProfileTheMathCouldNotSurviveAndReturnsWhatWasStored()
    {
        var stored = NewStore().Save(new FeeProfile { EbayFinalValueFeePercent = 1325m, DefaultShippingCost = -3m });

        Assert.True(stored.KeepFraction > 0m);
        Assert.Equal(0m, stored.DefaultShippingCost);
        Assert.Equal(stored.EbayFinalValueFeePercent, NewStore().Load().EbayFinalValueFeePercent);
    }

    // The mutation is the whole point: every analyzer holds this one instance, so applying a saved
    // profile is what re-prices the app without a restart.
    [Fact]
    public void SaveAndApply_OverwritesTheLiveSingletonInPlace()
    {
        var live = new FeeProfile();
        NewStore().SaveAndApply(new FeeProfile { DefaultShippingCost = 11m, MinimumNetProfit = 20m }, live);

        Assert.Equal(11m, live.DefaultShippingCost);
        Assert.Equal(20m, live.MinimumNetProfit);
    }

    [Fact]
    public void Apply_LoadsTheStoredProfileIntoTheLiveSingleton()
    {
        NewStore().Save(new FeeProfile { DefaultLaborCost = 6m });

        var live = new FeeProfile();
        NewStore().Apply(live);

        Assert.Equal(6m, live.DefaultLaborCost);
    }

    [Fact]
    public void ViewRoundTrip_PreservesEveryFieldAndReportsTheCombinedRate()
    {
        var profile = new FeeProfile
        {
            EbayFinalValueFeePercent = 13m, PromotedListingRatePercent = 2m,
            PaymentProcessingPercent = 3m, ReturnReservePercent = 4m, TestingReservePercent = 1m,
            DefaultShippingCost = 9m, MinimumMarginPercent = 15m,
        };

        var view = FeeProfileStore.ToView(profile);
        var back = FeeProfileStore.FromView(view);

        Assert.Equal(23m, view.RevenueFeePercent);
        Assert.Equal(profile.RevenueFeeFraction, back.RevenueFeeFraction);
        Assert.Equal(9m, back.DefaultShippingCost);
        Assert.Equal(15m, back.MinimumMarginPercent);
    }

    // ── The Store Plan's three seller-supplied answers ───────────────────────────────────────
    // eBay's APIs do not report an account's own subscription, so which tier the seller is on is
    // something only they can say — and a screen that asked on every visit would be a form. These
    // hold the round trip, and hold the two interactions that would silently undo it.

    [Fact]
    public void LoadStorePlan_WithNothingSaved_IsNoStoreBilledAnnually()
    {
        var settings = NewStore().LoadStorePlan();

        // "No store" because it is what a seller who has never chosen is actually on; guessing a
        // paid tier would invent a saving out of a bill they are not paying.
        Assert.Equal("none", settings.PlanKey);
        Assert.True(settings.AnnualBilling);
        Assert.Null(settings.ListingsOverride);
    }

    [Fact]
    public void SaveStorePlan_ThenLoadFromANewStore_ReturnsAllThree()
    {
        NewStore().SaveStorePlan(new StorePlanSettings
        {
            PlanKey = "premium", AnnualBilling = false, ListingsOverride = 4_200,
        });

        var reloaded = NewStore().LoadStorePlan();   // a fresh store, as if the app had restarted

        Assert.Equal("premium", reloaded.PlanKey);
        Assert.False(reloaded.AnnualBilling);
        Assert.Equal(4_200, reloaded.ListingsOverride);
    }

    [Fact]
    public void SaveStorePlan_RejectsATierThatIsNotOneOfEbaysRatherThanStoringIt()
    {
        var saved = NewStore().SaveStorePlan(new StorePlanSettings { PlanKey = "platinum-deluxe" });

        Assert.Equal("none", saved.PlanKey);
        Assert.Equal("none", NewStore().LoadStorePlan().PlanKey);
    }

    [Fact]
    public void ClearingTheListingBoxGoesBackToTheCountEbayReports()
    {
        // Empty and zero are the same gesture from the seller. Storing a literal 0 would make the
        // screen plan against no listings at all and recommend cancelling a store they need.
        var store = NewStore();
        store.SaveStorePlan(new StorePlanSettings { PlanKey = "basic", ListingsOverride = 900 });
        store.SaveStorePlan(new StorePlanSettings { PlanKey = "basic", ListingsOverride = 0 });

        Assert.Null(NewStore().LoadStorePlan().ListingsOverride);
    }

    [Fact]
    public void AnOverridePastTheTopOfTheRateCardIsClampedOnTheWayIn()
    {
        var saved = NewStore().SaveStorePlan(new StorePlanSettings { ListingsOverride = 5_000_000 });

        Assert.Equal(StorePlanCatalog.LadderCeiling, saved.ListingsOverride);
    }

    [Fact]
    public void SavingFeesAndCostsDoesNotResetTheStorePlan()
    {
        // The whole reason these live beside the tax bracket rather than on FeeProfile: that object
        // round-trips through the Fees & Costs screen, which knows nothing about any of this, so a
        // seller changing their shipping cost would quietly put them back on "no store".
        var store = NewStore();
        store.SaveStorePlan(new StorePlanSettings { PlanKey = "anchor", AnnualBilling = false });

        store.Save(new FeeProfile { DefaultShippingCost = 9.45m });

        var settings = NewStore().LoadStorePlan();
        Assert.Equal("anchor", settings.PlanKey);
        Assert.False(settings.AnnualBilling);
    }

    [Fact]
    public void SavingTheStorePlanDoesNotResetTheTaxBracketOrTheFeeProfile()
    {
        // And the same thing in the other direction, since all three share one table.
        var store = NewStore();
        store.Save(new FeeProfile { DefaultShippingCost = 9.45m });
        store.SaveTaxRate(22m);

        store.SaveStorePlan(new StorePlanSettings { PlanKey = "basic" });

        Assert.Equal(22m, NewStore().LoadTaxRate());
        Assert.Equal(9.45m, NewStore().Load().DefaultShippingCost);
    }
}
