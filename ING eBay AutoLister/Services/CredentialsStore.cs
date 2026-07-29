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

    /// When eBay last refused the stored grant outright (invalid_grant). Set only by
    /// <see cref="CredentialsStore.MarkEbayReauthRequired"/>, and persisted so the reason survives
    /// the restart that a seller's first instinct is to try. Null means no such refusal is known.
    public DateTimeOffset? EbayReauthRequiredAt { get; set; }

    /// The seller-facing reason the connection died. Written by this app, never copied from an eBay
    /// response body — this string is served from a diagnostics endpoint.
    public string EbayReauthReason { get; set; } = "";

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

/// <summary>
/// A partial settings save: every property is nullable, and <c>null</c> means "this screen wasn't
/// showing that field — leave it alone".
///
/// Settings are written from six places (the setup modal, the image-generation strip, listing
/// defaults, fees, the license box, the pasted eBay token), and each posts only the fields it owns.
/// Binding those posts to <see cref="Credentials"/> made an absent field indistinguishable from a
/// deliberately emptied one, so the fields that are legitimately clearable — the business policy
/// IDs, the listing defaults, the image-generation mode — were reset to blank on every save that
/// didn't happen to include them. Saving the optional image generation settings wiped the required
/// eBay policies; activating a license wiped both.
/// </summary>
public class CredentialsPatch
{
    public string? AnthropicApiKey { get; set; }
    public string? OpenAiApiKey { get; set; }

    public string? ImageGenMode { get; set; }
    public string? LocalSdEndpoint { get; set; }
    public string? LocalSdBackend { get; set; }
    public string? LocalSdModelName { get; set; }
    public string? ImagePromptTemplate { get; set; }

    public string? EbayClientId { get; set; }
    public string? EbayDevId { get; set; }
    public string? EbayClientSecret { get; set; }
    public string? EbayRuName { get; set; }
    public bool?   EbaySandbox { get; set; }
    public string? EbayFulfillmentPolicyId { get; set; }
    public string? EbayPaymentPolicyId { get; set; }
    public string? EbayReturnPolicyId { get; set; }
    public string? EbayUserToken { get; set; }
    public string? EbayRefreshToken { get; set; }

    public string?  DefaultPostalCode { get; set; }
    public string?  DefaultCountry { get; set; }
    public string?  DefaultPackageType { get; set; }
    public int?     DefaultHandlingTimeDays { get; set; }
    public decimal? DefaultWeightLbs { get; set; }
    public decimal? DefaultWeightOz { get; set; }
    public decimal? DefaultLengthIn { get; set; }
    public decimal? DefaultWidthIn { get; set; }
    public decimal? DefaultHeightIn { get; set; }
    public string?  DefaultFulfillmentPolicyId { get; set; }
    public bool?    DefaultBestOffer { get; set; }

    public string? LicenseKey { get; set; }
    public string? StripeSecretKey { get; set; }
    public string? StripePublishableKey { get; set; }
    public string? StripeWebhookSecret { get; set; }
    public string? MarketCompsApiUrl { get; set; }
    public string? MarketCompsApiKey { get; set; }
}

public class CredentialsStore
{
    private readonly string _filePath;
    private Credentials _data;
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    public CredentialsStore(IWebHostEnvironment env)
        : this(Path.Combine(env.ContentRootPath, "credentials.json")) { }

    public CredentialsStore(string filePath)
    {
        _filePath = filePath;
        _data = Load();
    }

    public Credentials Get() => _data;

    public void Save(CredentialsPatch patch)
    {
        // Secrets: present but blank means "keep what's stored". These fields are never rendered
        // back into the page — they show a "(saved)" placeholder — so an untouched one posts empty.
        SetSecret(patch.AnthropicApiKey,      v => _data.AnthropicApiKey      = v);
        SetSecret(patch.OpenAiApiKey,         v => _data.OpenAiApiKey         = v);
        SetSecret(patch.EbayClientSecret,     v => _data.EbayClientSecret     = v);
        SetSecret(patch.EbayRefreshToken,     v => _data.EbayRefreshToken     = v);
        SetSecret(patch.LicenseKey,           v => _data.LicenseKey           = v);
        SetSecret(patch.StripeSecretKey,      v => _data.StripeSecretKey      = v);
        SetSecret(patch.StripePublishableKey, v => _data.StripePublishableKey = v);
        SetSecret(patch.StripeWebhookSecret,  v => _data.StripeWebhookSecret  = v);
        SetSecret(patch.MarketCompsApiUrl,    v => _data.MarketCompsApiUrl    = v);
        SetSecret(patch.MarketCompsApiKey,    v => _data.MarketCompsApiKey    = v);

        if (!string.IsNullOrWhiteSpace(patch.EbayUserToken))
        {
            // Reject OAuth redirect URLs — they are NOT bearer tokens
            if (patch.EbayUserToken.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The pasted value is an OAuth redirect URL, not a bearer token. " +
                    "Use the 'Paste eBay Token' button and paste the full URL — it will be exchanged automatically.");
            _data.EbayUserToken = patch.EbayUserToken.Trim();
        }

        // eBay app identifiers: also keep-if-blank. They sit behind "Advanced" and are usually
        // pre-configured, so an empty box there means "I didn't touch this", never "delete it".
        SetSecret(patch.EbayClientId, v => _data.EbayClientId = v);
        SetSecret(patch.EbayDevId,    v => _data.EbayDevId    = v);
        SetSecret(patch.EbayRuName,   v => _data.EbayRuName   = v);
        if (patch.EbaySandbox is { } sandbox) _data.EbaySandbox = sandbox;

        // Business policies: clearable, so an empty string that was actually sent does clear them.
        if (patch.EbayFulfillmentPolicyId is not null) _data.EbayFulfillmentPolicyId = patch.EbayFulfillmentPolicyId.Trim();
        if (patch.EbayPaymentPolicyId     is not null) _data.EbayPaymentPolicyId     = patch.EbayPaymentPolicyId.Trim();
        if (patch.EbayReturnPolicyId      is not null) _data.EbayReturnPolicyId      = patch.EbayReturnPolicyId.Trim();

        // Image generation — optional, and saved from its own strip on the Settings page.
        if (patch.ImageGenMode        is not null) _data.ImageGenMode     = Fallback(patch.ImageGenMode, "disabled");
        if (patch.LocalSdBackend      is not null) _data.LocalSdBackend   = Fallback(patch.LocalSdBackend, "automatic1111");
        if (patch.LocalSdModelName    is not null) _data.LocalSdModelName = patch.LocalSdModelName.Trim();
        if (patch.LocalSdEndpoint     is not null) _data.LocalSdEndpoint  = Fallback(patch.LocalSdEndpoint, "http://127.0.0.1:7860");
        if (patch.ImagePromptTemplate is not null) _data.ImagePromptTemplate = patch.ImagePromptTemplate.Trim();

        // Listing defaults: clearable too — a seller who empties the ZIP means it.
        if (patch.DefaultPostalCode       is not null) _data.DefaultPostalCode  = patch.DefaultPostalCode.Trim();
        if (patch.DefaultCountry          is not null) _data.DefaultCountry     = Fallback(patch.DefaultCountry, "US");
        if (patch.DefaultPackageType      is not null) _data.DefaultPackageType = Fallback(patch.DefaultPackageType, "PACKAGE_THICK_ENVELOPE");
        if (patch.DefaultHandlingTimeDays is { } days) _data.DefaultHandlingTimeDays = days > 0 ? days : 1;
        if (patch.DefaultWeightLbs is { } lbs)    _data.DefaultWeightLbs = lbs;
        if (patch.DefaultWeightOz  is { } oz)     _data.DefaultWeightOz  = oz;
        if (patch.DefaultLengthIn  is { } length) _data.DefaultLengthIn  = length;
        if (patch.DefaultWidthIn   is { } width)  _data.DefaultWidthIn   = width;
        if (patch.DefaultHeightIn  is { } height) _data.DefaultHeightIn  = height;
        if (patch.DefaultFulfillmentPolicyId is not null) _data.DefaultFulfillmentPolicyId = patch.DefaultFulfillmentPolicyId.Trim();
        if (patch.DefaultBestOffer is { } bestOffer)      _data.DefaultBestOffer           = bestOffer;

        Persist();
    }

    private static void SetSecret(string? value, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public SetupStatus GetStatus() => new()
    {
        HasAnthropicKey     = !string.IsNullOrWhiteSpace(_data.AnthropicApiKey),
        HasEbayClientId     = !string.IsNullOrWhiteSpace(_data.EbayClientId),
        HasEbayClientSecret = !string.IsNullOrWhiteSpace(_data.EbayClientSecret),
        HasEbayRuName       = !string.IsNullOrWhiteSpace(_data.EbayRuName),
        HasEbayUserToken    = !string.IsNullOrWhiteSpace(_data.EbayUserToken),
        HasEbayRefreshToken = !string.IsNullOrWhiteSpace(_data.EbayRefreshToken),
        HasBusinessPolicies = !string.IsNullOrWhiteSpace(_data.EbayFulfillmentPolicyId)
                           && !string.IsNullOrWhiteSpace(_data.EbayPaymentPolicyId)
                           && !string.IsNullOrWhiteSpace(_data.EbayReturnPolicyId),
        HasOpenAiKey        = !string.IsNullOrWhiteSpace(_data.OpenAiApiKey),
        EbaySandbox         = _data.EbaySandbox
    };

    public void SaveOAuthTokens(string accessToken, string refreshToken) =>
        SaveOAuthTokensFull(accessToken, refreshToken, 0, 0, "");

    /// <summary>
    /// Stores what a sign-in or a code exchange came back with, and — when a refresh token was part
    /// of it — retires any earlier "you must sign in again" verdict, because the seller just did.
    /// </summary>
    public void SaveOAuthTokensFull(string accessToken, string refreshToken, int accessExpiresIn, int refreshExpiresIn, string tokenType)
    {
        var now = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            _data.EbayUserToken = accessToken.Trim();
            _data.EbayTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(accessExpiresIn, now);
        }
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            _data.EbayRefreshToken = refreshToken.Trim();
            _data.EbayRefreshTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(refreshExpiresIn, now);
            _data.EbayReauthRequiredAt = null;
            _data.EbayReauthReason = "";
        }
        if (!string.IsNullOrWhiteSpace(tokenType))
            _data.EbayTokenType = tokenType;
        Persist();
    }

    public void SaveRefreshedAccessToken(string accessToken, int expiresIn)
    {
        _data.EbayUserToken = accessToken.Trim();
        _data.EbayTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(expiresIn, DateTimeOffset.UtcNow);
        // A refresh that worked is proof the grant is alive, whatever an earlier failure recorded.
        _data.EbayReauthRequiredAt = null;
        _data.EbayReauthReason = "";
        Persist();
    }

    public void ClearEbayTokens()
    {
        _data.EbayUserToken = "";
        _data.EbayRefreshToken = "";
        _data.EbayTokenExpiresAt = null;
        _data.EbayRefreshTokenExpiresAt = null;
        _data.EbayTokenType = "";
        _data.EbayReauthRequiredAt = null;
        _data.EbayReauthReason = "";
        Persist();
    }

    /// <summary>
    /// eBay has refused the grant itself, so the stored tokens are worthless and only a fresh
    /// sign-in helps. The reason is kept, because otherwise the seller finds an app that has
    /// silently disconnected itself and no account of why.
    /// </summary>
    /// <remarks>
    /// The only caller is the <c>invalid_grant</c> branch of the token refresh. Every other failure
    /// — a timeout, a 500, a refused Client Secret — leaves the refresh token exactly where it is:
    /// see <see cref="EbayRefreshClassifier"/> for why that distinction is worth this much care.
    /// </remarks>
    public void MarkEbayReauthRequired(string reason)
    {
        _data.EbayUserToken = "";
        _data.EbayRefreshToken = "";
        _data.EbayTokenExpiresAt = null;
        _data.EbayRefreshTokenExpiresAt = null;
        _data.EbayTokenType = "";
        _data.EbayReauthRequiredAt = DateTimeOffset.UtcNow;
        _data.EbayReauthReason = reason;
        Persist();
    }

    public bool IsEbayReauthRequired => _data.EbayReauthRequiredAt is not null;

    public string GetUserToken()    => _data.EbayUserToken;
    public string GetRefreshToken() => _data.EbayRefreshToken;

    public bool IsAccessTokenExpired() => EbayTokenExpiry.IsAccessTokenExpired(
        _data.EbayUserToken, _data.EbayTokenExpiresAt,
        !string.IsNullOrWhiteSpace(_data.EbayRefreshToken), DateTimeOffset.UtcNow);

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

    /// <summary>
    /// True when the background loop should top the access token up now. See
    /// <see cref="EbayTokenExpiry.ShouldRefreshNow"/> — in particular for why a missing access
    /// token or a missing expiry counts as "yes" rather than "nothing to do".
    /// </summary>
    public bool ShouldRefreshAccessToken(int minutes = 20) => EbayTokenExpiry.ShouldRefreshNow(
        _data.EbayUserToken, _data.EbayTokenExpiresAt,
        _data.EbayRefreshToken, _data.EbayRefreshTokenExpiresAt,
        DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes));

    public bool HasValidRefreshToken() => EbayTokenExpiry.IsRefreshTokenUsable(
        _data.EbayRefreshToken, _data.EbayRefreshTokenExpiresAt, DateTimeOffset.UtcNow);

    public PublicFields GetPublicFields() => new()
    {
        EbayClientId            = _data.EbayClientId,
        EbayDevId               = _data.EbayDevId,
        EbayRuName              = _data.EbayRuName,
        EbaySandbox             = _data.EbaySandbox,
        EbayFulfillmentPolicyId = _data.EbayFulfillmentPolicyId,
        EbayPaymentPolicyId     = _data.EbayPaymentPolicyId,
        EbayReturnPolicyId      = _data.EbayReturnPolicyId,
        HasBusinessPolicies     = !string.IsNullOrWhiteSpace(_data.EbayFulfillmentPolicyId)
                               && !string.IsNullOrWhiteSpace(_data.EbayPaymentPolicyId)
                               && !string.IsNullOrWhiteSpace(_data.EbayReturnPolicyId),
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

    /// <summary>
    /// Set when the file on disk existed but could not be read. While true, nothing is written
    /// back — see <see cref="Persist"/>. This is the difference between "a bad read cost you one
    /// session" and "a bad read cost you every account you have ever connected".
    /// </summary>
    private bool _loadFailedWithFilePresent;

    /// <summary>True when this store refused to load an existing file and is protecting it from being overwritten.</summary>
    public bool IsProtectingUnreadableFile => _loadFailedWithFilePresent;

    private void Persist()
    {
        // The one case where saving is the wrong thing to do. If a file exists that we could not
        // parse, the in-memory state is empty defaults — and writing those over the file would
        // destroy the only copy of the seller's tokens to record that we failed to read them.
        if (_loadFailedWithFilePresent) return;

        // Atomic: the path never contains a partially-written file, and the previous contents are
        // kept as .bak. See AtomicFile for why WriteAllText was not safe enough for this file.
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(_data, _opts));
    }

    private Credentials Load()
    {
        var fileExisted = File.Exists(_filePath) || File.Exists(AtomicFile.BackupPathFor(_filePath));

        // Falls through to the .bak when the main file is missing, empty, or not valid JSON —
        // a half-written file from before atomic saves existed lands exactly here.
        var text = AtomicFile.ReadWithRecovery(_filePath, IsParseableCredentials);
        if (text is not null)
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<Credentials>(text);
                if (loaded is not null) return loaded;
            }
            catch (JsonException) { /* falls through to the guard below */ }
        }

        // Nothing readable. If there was never a file, this is a first run and empty is correct.
        // If there WAS one, refuse to overwrite it and say so.
        _loadFailedWithFilePresent = fileExisted;
        return new();
    }

    private static bool IsParseableCredentials(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException) { return false; }
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
    public bool HasBusinessPolicies { get; set; }
    public bool HasOpenAiKey { get; set; }
    public bool EbaySandbox { get; set; }
    public bool IsComplete => HasAnthropicKey && HasEbayClientId && HasEbayClientSecret && (HasEbayRuName || !EbaySandbox);

    /// The two things a seller actually has to do: pay for the AI, and tell eBay how the item
    /// ships, is paid for and is returned. Everything else on the Settings screen is optional or
    /// already filled in for them.
    public bool IsReadyToList => HasAnthropicKey && HasBusinessPolicies;
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
    public bool HasBusinessPolicies { get; set; }
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
