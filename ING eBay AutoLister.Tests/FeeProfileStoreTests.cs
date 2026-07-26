using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// If these settings do not survive a restart, every net-profit figure in the app quietly reverts
// to "eBay's cut and nothing else" — which looks like a working feature and is a wrong number.
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
}
