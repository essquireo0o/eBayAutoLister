using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

// Settings are written from six different screens, each posting only its own fields. If an absent
// field is read as an empty one, setting up the *optional* image generation strip silently deletes
// the *required* eBay business policies — and the next publish fails with an eBay error that says
// nothing about settings at all.
public class CredentialsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"creds_{Guid.NewGuid():N}.json");

    private CredentialsStore NewStore() => new(_path);

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    private CredentialsStore StoreWithFullSetup()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch
        {
            AnthropicApiKey         = "sk-ant-real-key",
            EbayClientId            = "ING-PRD-1234",
            EbayClientSecret        = "PRD-secret",
            EbayFulfillmentPolicyId = "111",
            EbayPaymentPolicyId     = "222",
            EbayReturnPolicyId      = "333",
            DefaultPostalCode       = "83702",
            DefaultWeightLbs        = 4m,
            DefaultBestOffer        = true,
        });
        return store;
    }

    // ── The bug this model exists to stop ──────────────────────────────────────

    [Fact]
    public void SavingOnlyImageGeneration_LeavesTheRequiredSetupAlone()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch
        {
            ImageGenMode    = "local_sd",
            LocalSdBackend  = "comfyui",
            LocalSdEndpoint = "http://127.0.0.1:8188",
        });

        var creds = store.Get();
        Assert.Equal("sk-ant-real-key", creds.AnthropicApiKey);
        Assert.Equal("111", creds.EbayFulfillmentPolicyId);
        Assert.Equal("222", creds.EbayPaymentPolicyId);
        Assert.Equal("333", creds.EbayReturnPolicyId);
        Assert.Equal("83702", creds.DefaultPostalCode);
        Assert.Equal(4m, creds.DefaultWeightLbs);
        Assert.True(creds.DefaultBestOffer);
        Assert.Equal("local_sd", creds.ImageGenMode);
        Assert.Equal("comfyui", creds.LocalSdBackend);
    }

    [Fact]
    public void SavingOnlyALicenseKey_LeavesTheRequiredSetupAlone()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch { LicenseKey = "ING-PRO-ABCD" });

        var creds = store.Get();
        Assert.Equal("ING-PRO-ABCD", creds.LicenseKey);
        Assert.Equal("111", creds.EbayFulfillmentPolicyId);
        Assert.Equal("83702", creds.DefaultPostalCode);
        Assert.Equal("sk-ant-real-key", creds.AnthropicApiKey);
    }

    [Fact]
    public void SavingOnlyAToken_LeavesTheListingDefaultsAlone()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch { EbayUserToken = "v^1.1#AgAAAA" });

        Assert.Equal("v^1.1#AgAAAA", store.GetUserToken());
        Assert.Equal("83702", store.Get().DefaultPostalCode);
        Assert.Equal("333", store.Get().EbayReturnPolicyId);
    }

    // ── Secrets: blank means "keep", not "clear" ───────────────────────────────

    [Fact]
    public void ABlankApiKey_KeepsTheSavedOne()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch { AnthropicApiKey = "", EbayClientSecret = "   " });

        Assert.Equal("sk-ant-real-key", store.Get().AnthropicApiKey);
        Assert.Equal("PRD-secret", store.Get().EbayClientSecret);
    }

    [Fact]
    public void APastedApiKey_IsTrimmed()
    {
        var store = NewStore();

        store.Save(new CredentialsPatch { AnthropicApiKey = "  sk-ant-pasted\n" });

        Assert.Equal("sk-ant-pasted", store.Get().AnthropicApiKey);
        Assert.True(store.GetStatus().HasAnthropicKey);
    }

    [Fact]
    public void ANewApiKey_ReplacesTheOldOne()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch { AnthropicApiKey = "sk-ant-second" });

        Assert.Equal("sk-ant-second", store.Get().AnthropicApiKey);
    }

    [Fact]
    public void AnOAuthRedirectUrlInTheTokenField_IsRefusedAndChangesNothing()
    {
        var store = StoreWithFullSetup();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Save(new CredentialsPatch { EbayUserToken = "https://inglisting.com/?code=abc" }));

        Assert.Contains("not a bearer token", ex.Message);
        Assert.Equal("", store.GetUserToken());
    }

    // ── Clearable fields still clear when they really were sent ────────────────

    [Fact]
    public void AnEmptyPolicyIdThatWasActuallySent_ClearsIt()
    {
        var store = StoreWithFullSetup();

        store.Save(new CredentialsPatch
        {
            EbayFulfillmentPolicyId = "",
            EbayPaymentPolicyId     = "222",
            EbayReturnPolicyId      = "333",
        });

        Assert.Equal("", store.Get().EbayFulfillmentPolicyId);
        Assert.Equal("222", store.Get().EbayPaymentPolicyId);
        Assert.False(store.GetStatus().HasBusinessPolicies);
    }

    [Fact]
    public void TurningImageGenerationBackOff_Sticks()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch { ImageGenMode = "dalle" });

        store.Save(new CredentialsPatch { ImageGenMode = "disabled" });

        Assert.Equal("disabled", store.Get().ImageGenMode);
    }

    [Fact]
    public void ABlankImageGenerationMode_FallsBackToDisabledRatherThanEmpty()
    {
        var store = NewStore();

        store.Save(new CredentialsPatch { ImageGenMode = "", LocalSdEndpoint = "", LocalSdBackend = "" });

        var creds = store.Get();
        Assert.Equal("disabled", creds.ImageGenMode);
        Assert.Equal("automatic1111", creds.LocalSdBackend);
        Assert.Equal("http://127.0.0.1:7860", creds.LocalSdEndpoint);
    }

    // ── Setup status: the two required steps ───────────────────────────────────

    [Fact]
    public void AFreshInstall_IsNotReadyToList()
    {
        var status = NewStore().GetStatus();

        Assert.False(status.HasAnthropicKey);
        Assert.False(status.HasBusinessPolicies);
        Assert.False(status.IsReadyToList);
    }

    [Fact]
    public void AKeyWithNoPolicies_IsNotReadyToList()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch { AnthropicApiKey = "sk-ant-key" });

        var status = store.GetStatus();
        Assert.True(status.HasAnthropicKey);
        Assert.False(status.HasBusinessPolicies);
        Assert.False(status.IsReadyToList);
    }

    [Fact]
    public void PoliciesWithNoKey_IsNotReadyToList()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch
        {
            EbayFulfillmentPolicyId = "1", EbayPaymentPolicyId = "2", EbayReturnPolicyId = "3",
        });

        var status = store.GetStatus();
        Assert.True(status.HasBusinessPolicies);
        Assert.False(status.IsReadyToList);
    }

    [Fact]
    public void AKeyAndAllThreePolicies_IsReadyToList()
    {
        var status = StoreWithFullSetup().GetStatus();

        Assert.True(status.IsReadyToList);
        Assert.True(status.HasBusinessPolicies);
    }

    [Fact]
    public void TwoOfThreePolicies_IsNotEnough()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch
        {
            AnthropicApiKey = "sk-ant-key",
            EbayFulfillmentPolicyId = "1", EbayPaymentPolicyId = "2", EbayReturnPolicyId = "",
        });

        Assert.False(store.GetStatus().HasBusinessPolicies);
        Assert.False(store.GetStatus().IsReadyToList);
    }

    // ── Round-trip: what's saved is what a restart reads back ──────────────────

    [Fact]
    public void EverySavedField_SurvivesARestart()
    {
        StoreWithFullSetup().Save(new CredentialsPatch
        {
            ImageGenMode        = "local_sd",
            LocalSdBackend      = "comfyui",
            LocalSdEndpoint     = "http://127.0.0.1:8188",
            LocalSdModelName    = "sd_xl_base_1.0.safetensors",
            ImagePromptTemplate = "studio photo of {ITEM}",
            DefaultCountry      = "US",
            DefaultHandlingTimeDays = 3,
        });

        var reopened = NewStore().Get();
        Assert.Equal("sk-ant-real-key", reopened.AnthropicApiKey);
        Assert.Equal("111", reopened.EbayFulfillmentPolicyId);
        Assert.Equal("comfyui", reopened.LocalSdBackend);
        Assert.Equal("sd_xl_base_1.0.safetensors", reopened.LocalSdModelName);
        Assert.Equal("studio photo of {ITEM}", reopened.ImagePromptTemplate);
        Assert.Equal(3, reopened.DefaultHandlingTimeDays);
        Assert.Equal("83702", reopened.DefaultPostalCode);
    }

    [Fact]
    public void ASandboxFlagThatWasNotSent_DoesNotFlipTheEnvironment()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch { EbayClientId = "ING-PRD-1", EbaySandbox = true });

        store.Save(new CredentialsPatch { AnthropicApiKey = "sk-ant-key" });

        Assert.True(store.Get().EbaySandbox);
    }

    [Fact]
    public void ASandboxFlagThatWasSent_IsHonouredWithoutResendingTheClientId()
    {
        var store = NewStore();
        store.Save(new CredentialsPatch { EbayClientId = "ING-PRD-1", EbaySandbox = true });

        store.Save(new CredentialsPatch { EbaySandbox = false });

        Assert.False(store.Get().EbaySandbox);
        Assert.Equal("ING-PRD-1", store.Get().EbayClientId);
    }
}
