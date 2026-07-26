using System.Text.Json;

namespace ING_eBay_AutoLister.Services;

public class Credentials
{
    public string AnthropicApiKey { get; set; } = "";
    public string OpenAiApiKey { get; set; } = "";

    // Image generation settings
    public string ImageGenMode { get; set; } = "disabled"; // disabled | local_sd | dalle
    public string LocalSdEndpoint { get; set; } = "http://127.0.0.1:7860";
    public string LocalSdBackend { get; set; } = "automatic1111"; // automatic1111 | comfyui
    public string LocalSdModelName { get; set; } = "";
    public string ImagePromptTemplate { get; set; } = "";

    public string EbayClientId { get; set; } = "";
    public string EbayDevId { get; set; } = "";
    public string EbayClientSecret { get; set; } = "";
    public string EbayRuName { get; set; } = "";
    public bool EbaySandbox { get; set; } = false;
    public string EbayFulfillmentPolicyId { get; set; } = "";
    public string EbayPaymentPolicyId { get; set; } = "";
    public string EbayReturnPolicyId { get; set; } = "";
    public string EbayUserToken { get; set; } = "";
    public string EbayRefreshToken { get; set; } = "";
    public DateTimeOffset? EbayTokenExpiresAt { get; set; }
    public DateTimeOffset? EbayRefreshTokenExpiresAt { get; set; }
    public string EbayTokenType { get; set; } = "";

    // Listing defaults — pre-fill every new listing
    public string DefaultPostalCode { get; set; } = "";
    public string DefaultCountry { get; set; } = "US";
    public string DefaultPackageType { get; set; } = "PACKAGE_THICK_ENVELOPE";
    public int    DefaultHandlingTimeDays { get; set; } = 1;
    public decimal DefaultWeightLbs { get; set; }
    public decimal DefaultWeightOz  { get; set; }
    public decimal DefaultLengthIn  { get; set; }
    public decimal DefaultWidthIn   { get; set; }
    public decimal DefaultHeightIn  { get; set; }
    public string  DefaultFulfillmentPolicyId { get; set; } = "";
    public bool    DefaultBestOffer { get; set; }

    // License
    public string LicenseKey  { get; set; } = "";
    public DateTimeOffset? InstallDate { get; set; }

    // Stripe
    public string StripeSecretKey      { get; set; } = "";
    public string StripePublishableKey { get; set; } = "";
    public string StripeWebhookSecret  { get; set; } = "";

    // Owner dashboard
    public string AdminKey { get; set; } = "";

    // Hosted sold-comps API (comps.php on inglisting.com -> ing_sold_listings MariaDB). When Url is
    // set, the app queries the hosted marketplace instead of the local C:\INGListing Marketplace.db.
    public string MarketCompsApiUrl { get; set; } = "";
    public string MarketCompsApiKey { get; set; } = "";
}

public class CredentialsStore
{
    private readonly string _filePath;
    private Credentials _data;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public CredentialsStore(IWebHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, "credentials.json");
        _data = Load();
    }

    public Credentials Get() => _data;

    public void Save(Credentials creds)
    {
        // Secrets: only overwrite when non-empty
        if (!string.IsNullOrWhiteSpace(creds.AnthropicApiKey))  _data.AnthropicApiKey  = creds.AnthropicApiKey;
        if (!string.IsNullOrWhiteSpace(creds.OpenAiApiKey))     _data.OpenAiApiKey     = creds.OpenAiApiKey;
        // Image generation — non-secret settings always update; endpoint only when non-empty
        _data.ImageGenMode   = creds.ImageGenMode ?? "disabled";
        _data.LocalSdBackend = creds.LocalSdBackend ?? "automatic1111";
        _data.LocalSdModelName = creds.LocalSdModelName ?? "";
        if (!string.IsNullOrWhiteSpace(creds.LocalSdEndpoint))      _data.LocalSdEndpoint      = creds.LocalSdEndpoint;
        if (!string.IsNullOrWhiteSpace(creds.ImagePromptTemplate))   _data.ImagePromptTemplate  = creds.ImagePromptTemplate;
        if (!string.IsNullOrWhiteSpace(creds.EbayClientSecret)) _data.EbayClientSecret = creds.EbayClientSecret;
        if (!string.IsNullOrWhiteSpace(creds.EbayUserToken))
        {
            // Reject OAuth redirect URLs — they are NOT bearer tokens
            if (creds.EbayUserToken.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The pasted value is an OAuth redirect URL, not a bearer token. " +
                    "Use the 'Paste eBay Token' button and paste the full URL — it will be exchanged automatically.");
            _data.EbayUserToken = creds.EbayUserToken;
        }
        if (!string.IsNullOrWhiteSpace(creds.EbayRefreshToken)) _data.EbayRefreshToken = creds.EbayRefreshToken;
        // Non-secret strings: merge — only update when non-empty so a blank settings save cannot wipe production credentials
        if (!string.IsNullOrWhiteSpace(creds.EbayClientId))     _data.EbayClientId     = creds.EbayClientId;
        if (!string.IsNullOrWhiteSpace(creds.EbayDevId))        _data.EbayDevId        = creds.EbayDevId;
        if (!string.IsNullOrWhiteSpace(creds.EbayRuName))       _data.EbayRuName       = creds.EbayRuName;
        // EbaySandbox: only update when ClientId is also being explicitly provided
        if (!string.IsNullOrWhiteSpace(creds.EbayClientId))     _data.EbaySandbox      = creds.EbaySandbox;
        // Policy IDs: always update — these are legitimately clearable
        _data.EbayFulfillmentPolicyId = creds.EbayFulfillmentPolicyId;
        _data.EbayPaymentPolicyId     = creds.EbayPaymentPolicyId;
        _data.EbayReturnPolicyId      = creds.EbayReturnPolicyId;
        // Listing defaults: always update
        _data.DefaultPostalCode       = creds.DefaultPostalCode ?? "";
        _data.DefaultCountry          = string.IsNullOrWhiteSpace(creds.DefaultCountry) ? "US" : creds.DefaultCountry;
        _data.DefaultPackageType      = string.IsNullOrWhiteSpace(creds.DefaultPackageType) ? "PACKAGE_THICK_ENVELOPE" : creds.DefaultPackageType;
        _data.DefaultHandlingTimeDays = creds.DefaultHandlingTimeDays > 0 ? creds.DefaultHandlingTimeDays : 1;
        _data.DefaultWeightLbs             = creds.DefaultWeightLbs;
        _data.DefaultWeightOz              = creds.DefaultWeightOz;
        _data.DefaultLengthIn              = creds.DefaultLengthIn;
        _data.DefaultWidthIn               = creds.DefaultWidthIn;
        _data.DefaultHeightIn              = creds.DefaultHeightIn;
        _data.DefaultFulfillmentPolicyId   = creds.DefaultFulfillmentPolicyId ?? "";
        _data.DefaultBestOffer             = creds.DefaultBestOffer;
        if (!string.IsNullOrWhiteSpace(creds.LicenseKey))          _data.LicenseKey          = creds.LicenseKey;
        if (!string.IsNullOrWhiteSpace(creds.StripeSecretKey))      _data.StripeSecretKey      = creds.StripeSecretKey;
        if (!string.IsNullOrWhiteSpace(creds.StripePublishableKey)) _data.StripePublishableKey = creds.StripePublishableKey;
        if (!string.IsNullOrWhiteSpace(creds.StripeWebhookSecret))  _data.StripeWebhookSecret  = creds.StripeWebhookSecret;
        if (!string.IsNullOrWhiteSpace(creds.MarketCompsApiUrl))     _data.MarketCompsApiUrl     = creds.MarketCompsApiUrl;
        if (!string.IsNullOrWhiteSpace(creds.MarketCompsApiKey))     _data.MarketCompsApiKey     = creds.MarketCompsApiKey;
        Persist();
    }

    public SetupStatus GetStatus() => new()
    {
        HasAnthropicKey     = !string.IsNullOrWhiteSpace(_data.AnthropicApiKey),
        HasEbayClientId     = !string.IsNullOrWhiteSpace(_data.EbayClientId),
        HasEbayClientSecret = !string.IsNullOrWhiteSpace(_data.EbayClientSecret),
        HasEbayRuName       = !string.IsNullOrWhiteSpace(_data.EbayRuName),
        HasEbayUserToken    = !string.IsNullOrWhiteSpace(_data.EbayUserToken),
        HasEbayRefreshToken = !string.IsNullOrWhiteSpace(_data.EbayRefreshToken),
        EbaySandbox         = _data.EbaySandbox
    };

    public void SaveOAuthTokens(string accessToken, string refreshToken) =>
        SaveOAuthTokensFull(accessToken, refreshToken, 0, 0, "");

    public void SaveOAuthTokensFull(string accessToken, string refreshToken, int accessExpiresIn, int refreshExpiresIn, string tokenType)
    {
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _data.EbayUserToken = accessToken.Trim();
            _data.EbayTokenExpiresAt = accessExpiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(accessExpiresIn)
                : (DateTimeOffset?)null;
        }
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            _data.EbayRefreshToken = refreshToken.Trim();
            _data.EbayRefreshTokenExpiresAt = refreshExpiresIn > 0
                ? DateTimeOffset.UtcNow.AddSeconds(refreshExpiresIn)
                : (DateTimeOffset?)null;
        }
        if (!string.IsNullOrWhiteSpace(tokenType))
            _data.EbayTokenType = tokenType;
        Persist();
    }

    public void SaveRefreshedAccessToken(string accessToken, int expiresIn)
    {
        _data.EbayUserToken = accessToken.Trim();
        _data.EbayTokenExpiresAt = expiresIn > 0
            ? DateTimeOffset.UtcNow.AddSeconds(expiresIn)
            : (DateTimeOffset?)null;
        Persist();
    }

    public void ClearEbayTokens()
    {
        _data.EbayUserToken = "";
        _data.EbayRefreshToken = "";
        _data.EbayTokenExpiresAt = null;
        _data.EbayRefreshTokenExpiresAt = null;
        _data.EbayTokenType = "";
        Persist();
    }

    public string GetUserToken()    => _data.EbayUserToken;
    public string GetRefreshToken() => _data.EbayRefreshToken;

    public bool IsAccessTokenExpired()
    {
        if (string.IsNullOrWhiteSpace(_data.EbayUserToken)) return true;
        if (_data.EbayTokenExpiresAt == null) return false;
        return DateTimeOffset.UtcNow >= _data.EbayTokenExpiresAt.Value.AddSeconds(-90);
    }

    public void EnsureInstallDate()
    {
        if (_data.InstallDate == null)
        {
            _data.InstallDate = DateTimeOffset.UtcNow;
            Persist();
        }
    }

    public string EnsureAdminKey()
    {
        if (string.IsNullOrWhiteSpace(_data.AdminKey))
        {
            _data.AdminKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLower();
            Persist();
        }
        return _data.AdminKey;
    }

    public int TrialDaysRemaining()
    {
        if (_data.InstallDate == null) return 30;
        var elapsed = (DateTimeOffset.UtcNow - _data.InstallDate.Value).TotalDays;
        return Math.Max(0, 30 - (int)elapsed);
    }

    public bool IsTrialExpired() => TrialDaysRemaining() == 0;

    public bool IsAccessTokenExpiringSoon(int minutes = 20)
    {
        if (string.IsNullOrWhiteSpace(_data.EbayUserToken)) return false;
        if (_data.EbayTokenExpiresAt == null) return false;
        return DateTimeOffset.UtcNow >= _data.EbayTokenExpiresAt.Value.AddMinutes(-minutes);
    }

    public bool HasValidRefreshToken()
    {
        if (string.IsNullOrWhiteSpace(_data.EbayRefreshToken)) return false;
        if (_data.EbayRefreshTokenExpiresAt == null) return true; // no expiry recorded — assume valid
        return DateTimeOffset.UtcNow < _data.EbayRefreshTokenExpiresAt.Value.AddDays(-1);
    }

    public PublicFields GetPublicFields() => new()
    {
        EbayClientId            = _data.EbayClientId,
        EbayDevId               = _data.EbayDevId,
        EbayRuName              = _data.EbayRuName,
        EbaySandbox             = _data.EbaySandbox,
        EbayFulfillmentPolicyId = _data.EbayFulfillmentPolicyId,
        EbayPaymentPolicyId     = _data.EbayPaymentPolicyId,
        EbayReturnPolicyId      = _data.EbayReturnPolicyId,
        HasAnthropicKey         = !string.IsNullOrWhiteSpace(_data.AnthropicApiKey),
        HasOpenAiKey            = !string.IsNullOrWhiteSpace(_data.OpenAiApiKey),
        ImageGenMode            = _data.ImageGenMode ?? "disabled",
        LocalSdEndpoint         = _data.LocalSdEndpoint ?? "http://127.0.0.1:7860",
        LocalSdBackend          = _data.LocalSdBackend ?? "automatic1111",
        LocalSdModelName        = _data.LocalSdModelName ?? "",
        ImagePromptTemplate     = _data.ImagePromptTemplate ?? "",
        HasEbayClientSecret     = !string.IsNullOrWhiteSpace(_data.EbayClientSecret),
        HasEbayUserToken        = !string.IsNullOrWhiteSpace(_data.EbayUserToken),
        HasEbayRefreshToken     = !string.IsNullOrWhiteSpace(_data.EbayRefreshToken),
        EbayTokenExpiresAt      = _data.EbayTokenExpiresAt?.ToString("u"),
        DefaultPostalCode       = _data.DefaultPostalCode,
        DefaultCountry          = _data.DefaultCountry.Length > 0 ? _data.DefaultCountry : "US",
        DefaultPackageType      = _data.DefaultPackageType.Length > 0 ? _data.DefaultPackageType : "PACKAGE_THICK_ENVELOPE",
        DefaultHandlingTimeDays = _data.DefaultHandlingTimeDays > 0 ? _data.DefaultHandlingTimeDays : 1,
        DefaultWeightLbs             = _data.DefaultWeightLbs,
        DefaultWeightOz              = _data.DefaultWeightOz,
        DefaultLengthIn              = _data.DefaultLengthIn,
        DefaultWidthIn               = _data.DefaultWidthIn,
        DefaultHeightIn              = _data.DefaultHeightIn,
        DefaultFulfillmentPolicyId   = _data.DefaultFulfillmentPolicyId,
        DefaultBestOffer             = _data.DefaultBestOffer,
        HasLicenseKey                = !string.IsNullOrWhiteSpace(_data.LicenseKey),
        LicenseKeyPreview            = PreviewLicenseKey(_data.LicenseKey),
    };

    private static string PreviewLicenseKey(string key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key[..Math.Min(8, key.Length)] + "****";

    private void Persist() =>
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_data, _opts));

    private Credentials Load()
    {
        if (!File.Exists(_filePath)) return new();
        try { return JsonSerializer.Deserialize<Credentials>(File.ReadAllText(_filePath)) ?? new(); }
        catch { return new(); }
    }
}

public class SetupStatus
{
    public bool HasAnthropicKey { get; set; }
    public bool HasEbayClientId { get; set; }
    public bool HasEbayClientSecret { get; set; }
    public bool HasEbayRuName { get; set; }
    public bool HasEbayUserToken { get; set; }
    public bool HasEbayRefreshToken { get; set; }
    public bool EbaySandbox { get; set; }
    public bool IsComplete => HasAnthropicKey && HasEbayClientId && HasEbayClientSecret && (HasEbayRuName || !EbaySandbox);
}

public class PublicFields
{
    public string EbayClientId { get; set; } = "";
    public string EbayDevId { get; set; } = "";
    public string EbayRuName { get; set; } = "";
    public bool EbaySandbox { get; set; }
    public string EbayFulfillmentPolicyId { get; set; } = "";
    public string EbayPaymentPolicyId { get; set; } = "";
    public string EbayReturnPolicyId { get; set; } = "";
    public bool HasAnthropicKey { get; set; }
    public bool HasOpenAiKey { get; set; }
    public string ImageGenMode { get; set; } = "disabled";
    public string LocalSdEndpoint { get; set; } = "http://127.0.0.1:7860";
    public string LocalSdBackend { get; set; } = "automatic1111";
    public string LocalSdModelName { get; set; } = "";
    public string ImagePromptTemplate { get; set; } = "";
    public bool HasEbayClientSecret { get; set; }
    public bool HasEbayUserToken { get; set; }
    public bool HasEbayRefreshToken { get; set; }
    public string? EbayTokenExpiresAt { get; set; }

    // Listing defaults
    public string DefaultPostalCode { get; set; } = "";
    public string DefaultCountry { get; set; } = "US";
    public string DefaultPackageType { get; set; } = "PACKAGE_THICK_ENVELOPE";
    public int    DefaultHandlingTimeDays { get; set; } = 1;
    public decimal DefaultWeightLbs { get; set; }
    public decimal DefaultWeightOz  { get; set; }
    public decimal DefaultLengthIn  { get; set; }
    public decimal DefaultWidthIn   { get; set; }
    public decimal DefaultHeightIn  { get; set; }
    public string  DefaultFulfillmentPolicyId { get; set; } = "";
    public bool    DefaultBestOffer { get; set; }

    // License
    public bool   HasLicenseKey      { get; set; }
    public string LicenseKeyPreview  { get; set; } = "";
}
