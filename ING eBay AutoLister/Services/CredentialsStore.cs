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

    // Live sold-comps lookups (see OpenWebNinjaClient). One key, the owner's, against a paid and
    // finite 50,000 calls shared with the bulk collector — so it belongs in configuration and never
    // in the repository, and on a server it arrives as Credentials__OpenWebNinjaApiKey. Blank means
    // live lookups are unavailable and every price comes from stored comps, which is what the app
    // did before them.
    public string OpenWebNinjaApiKey { get; set; } = "";

    // On-demand comps scraping (see OnDemandCompsScraper). DEAD as of 2026-08-14: the scraper was
    // replaced by the API above and nothing reads these two any more. Left in place so removing the
    // scraper is one reviewed change rather than a surprise inside this one. The scraper is the collector's own
    // toolchain — a separate checkout with its own venv and Playwright install — so it is pointed
    // at rather than shipped. Blank means "use the default location"; a machine without it there
    // simply prices from stored comps, which is what the app did before live lookups existed.
    public string CompsScraperDir { get; set; } = "";
    public string CompsScraperPython { get; set; } = "";
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
    public string? OpenWebNinjaApiKey { get; set; }
    public string? CompsScraperDir { get; set; }
    public string? CompsScraperPython { get; set; }
}

/// <summary>
/// Where one caller's credentials are read from and written to. Two implementations:
/// <see cref="FileCredentialsSource"/>, which is the desktop app's single credentials.json, and
/// <see cref="PerUserCredentialsSource"/>, which is one encrypted row per signed-in user.
/// </summary>
/// <remarks>
/// This interface exists so that the ~60 endpoints and ten services which take a
/// <see cref="CredentialsStore"/> never learn which build they are running in. Every one of them
/// asks the same store the same question; what changed under HOSTED is only where the answer comes
/// from, and that it is a different answer for each person asking.
/// </remarks>
public interface ICredentialsSource
{
    /// <summary>The credentials this call is about.</summary>
    Credentials Read();

    /// <summary>Persists a record that came from <see cref="Read"/>.</summary>
    void Write(Credentials data);

    /// <summary>
    /// True when there is stored data that could not be read and is being protected from being
    /// overwritten with empty defaults. See <see cref="FileCredentialsSource.Load"/>.
    /// </summary>
    bool IsProtectingUnreadableData { get; }
}

public class CredentialsStore
{
    private readonly ICredentialsSource _source;

    public CredentialsStore(IWebHostEnvironment env)
        : this(Path.Combine(env.ContentRootPath, "credentials.json")) { }

    public CredentialsStore(string filePath) : this(new FileCredentialsSource(filePath)) { }

    /// <summary>
    /// The hosted build's entry point: the same store over per-user storage. See
    /// <see cref="PerUserCredentials.AddCredentials"/> for which of the two Program.cs picks.
    /// </summary>
    public CredentialsStore(ICredentialsSource source) => _source = source;

    public Credentials Get() => _source.Read();

    public void Save(CredentialsPatch patch)
    {
        var data = _source.Read();

        // Secrets: present but blank means "keep what's stored". These fields are never rendered
        // back into the page — they show a "(saved)" placeholder — so an untouched one posts empty.
        SetSecret(patch.AnthropicApiKey,      v => data.AnthropicApiKey      = v);
        SetSecret(patch.OpenAiApiKey,         v => data.OpenAiApiKey         = v);
        SetSecret(patch.EbayClientSecret,     v => data.EbayClientSecret     = v);
        SetSecret(patch.EbayRefreshToken,     v => data.EbayRefreshToken     = v);
        SetSecret(patch.LicenseKey,           v => data.LicenseKey           = v);
        SetSecret(patch.StripeSecretKey,      v => data.StripeSecretKey      = v);
        SetSecret(patch.StripePublishableKey, v => data.StripePublishableKey = v);
        SetSecret(patch.StripeWebhookSecret,  v => data.StripeWebhookSecret  = v);
        SetSecret(patch.MarketCompsApiUrl,    v => data.MarketCompsApiUrl    = v);
        SetSecret(patch.MarketCompsApiKey,    v => data.MarketCompsApiKey    = v);
        SetSecret(patch.OpenWebNinjaApiKey,   v => data.OpenWebNinjaApiKey   = v);
        SetSecret(patch.CompsScraperDir,      v => data.CompsScraperDir      = v);
        SetSecret(patch.CompsScraperPython,   v => data.CompsScraperPython   = v);

        if (!string.IsNullOrWhiteSpace(patch.EbayUserToken))
        {
            // Reject OAuth redirect URLs — they are NOT bearer tokens
            if (patch.EbayUserToken.TrimStart().StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The pasted value is an OAuth redirect URL, not a bearer token. " +
                    "Use the 'Paste eBay Token' button and paste the full URL — it will be exchanged automatically.");
            data.EbayUserToken = patch.EbayUserToken.Trim();
        }

        // eBay app identifiers: also keep-if-blank. They sit behind "Advanced" and are usually
        // pre-configured, so an empty box there means "I didn't touch this", never "delete it".
        SetSecret(patch.EbayClientId, v => data.EbayClientId = v);
        SetSecret(patch.EbayDevId,    v => data.EbayDevId    = v);
        SetSecret(patch.EbayRuName,   v => data.EbayRuName   = v);
        if (patch.EbaySandbox is { } sandbox) data.EbaySandbox = sandbox;

        // Business policies: clearable, so an empty string that was actually sent does clear them.
        if (patch.EbayFulfillmentPolicyId is not null) data.EbayFulfillmentPolicyId = patch.EbayFulfillmentPolicyId.Trim();
        if (patch.EbayPaymentPolicyId     is not null) data.EbayPaymentPolicyId     = patch.EbayPaymentPolicyId.Trim();
        if (patch.EbayReturnPolicyId      is not null) data.EbayReturnPolicyId      = patch.EbayReturnPolicyId.Trim();

        // Image generation — optional, and saved from its own strip on the Settings page.
        if (patch.ImageGenMode        is not null) data.ImageGenMode     = Fallback(patch.ImageGenMode, "disabled");
        if (patch.LocalSdBackend      is not null) data.LocalSdBackend   = Fallback(patch.LocalSdBackend, "automatic1111");
        if (patch.LocalSdModelName    is not null) data.LocalSdModelName = patch.LocalSdModelName.Trim();
        if (patch.LocalSdEndpoint     is not null) data.LocalSdEndpoint  = Fallback(patch.LocalSdEndpoint, "http://127.0.0.1:7860");
        if (patch.ImagePromptTemplate is not null) data.ImagePromptTemplate = patch.ImagePromptTemplate.Trim();

        // Listing defaults: clearable too — a seller who empties the ZIP means it.
        if (patch.DefaultPostalCode       is not null) data.DefaultPostalCode  = patch.DefaultPostalCode.Trim();
        if (patch.DefaultCountry          is not null) data.DefaultCountry     = Fallback(patch.DefaultCountry, "US");
        if (patch.DefaultPackageType      is not null) data.DefaultPackageType = Fallback(patch.DefaultPackageType, "PACKAGE_THICK_ENVELOPE");
        if (patch.DefaultHandlingTimeDays is { } days) data.DefaultHandlingTimeDays = days > 0 ? days : 1;
        if (patch.DefaultWeightLbs is { } lbs)    data.DefaultWeightLbs = lbs;
        if (patch.DefaultWeightOz  is { } oz)     data.DefaultWeightOz  = oz;
        if (patch.DefaultLengthIn  is { } length) data.DefaultLengthIn  = length;
        if (patch.DefaultWidthIn   is { } width)  data.DefaultWidthIn   = width;
        if (patch.DefaultHeightIn  is { } height) data.DefaultHeightIn  = height;
        if (patch.DefaultFulfillmentPolicyId is not null) data.DefaultFulfillmentPolicyId = patch.DefaultFulfillmentPolicyId.Trim();
        if (patch.DefaultBestOffer is { } bestOffer)      data.DefaultBestOffer           = bestOffer;

        _source.Write(data);
    }

    private static void SetSecret(string? value, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(value)) assign(value.Trim());
    }

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    public SetupStatus GetStatus()
    {
        var data = _source.Read();
        return new()
        {
            HasAnthropicKey     = !string.IsNullOrWhiteSpace(data.AnthropicApiKey),
            HasEbayClientId     = !string.IsNullOrWhiteSpace(data.EbayClientId),
            HasEbayClientSecret = !string.IsNullOrWhiteSpace(data.EbayClientSecret),
            HasEbayRuName       = !string.IsNullOrWhiteSpace(data.EbayRuName),
            HasEbayUserToken    = !string.IsNullOrWhiteSpace(data.EbayUserToken),
            HasEbayRefreshToken = !string.IsNullOrWhiteSpace(data.EbayRefreshToken),
            HasBusinessPolicies = !string.IsNullOrWhiteSpace(data.EbayFulfillmentPolicyId)
                               && !string.IsNullOrWhiteSpace(data.EbayPaymentPolicyId)
                               && !string.IsNullOrWhiteSpace(data.EbayReturnPolicyId),
            HasOpenAiKey        = !string.IsNullOrWhiteSpace(data.OpenAiApiKey),
            EbaySandbox         = data.EbaySandbox
        };
    }

    public void SaveOAuthTokens(string accessToken, string refreshToken) =>
        SaveOAuthTokensFull(accessToken, refreshToken, 0, 0, "");

    /// <summary>
    /// Stores what a sign-in or a code exchange came back with, and — when a refresh token was part
    /// of it — retires any earlier "you must sign in again" verdict, because the seller just did.
    /// </summary>
    public void SaveOAuthTokensFull(string accessToken, string refreshToken, int accessExpiresIn, int refreshExpiresIn, string tokenType)
    {
        var data = _source.Read();
        var now  = DateTimeOffset.UtcNow;

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            data.EbayUserToken = accessToken.Trim();
            data.EbayTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(accessExpiresIn, now);
        }
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            data.EbayRefreshToken = refreshToken.Trim();
            data.EbayRefreshTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(refreshExpiresIn, now);
            data.EbayReauthRequiredAt = null;
            data.EbayReauthReason = "";
        }
        if (!string.IsNullOrWhiteSpace(tokenType))
            data.EbayTokenType = tokenType;
        _source.Write(data);
    }

    public void SaveRefreshedAccessToken(string accessToken, int expiresIn)
    {
        var data = _source.Read();
        data.EbayUserToken = accessToken.Trim();
        data.EbayTokenExpiresAt = EbayTokenExpiry.FromExpiresIn(expiresIn, DateTimeOffset.UtcNow);
        // A refresh that worked is proof the grant is alive, whatever an earlier failure recorded.
        data.EbayReauthRequiredAt = null;
        data.EbayReauthReason = "";
        _source.Write(data);
    }

    public void ClearEbayTokens()
    {
        var data = _source.Read();
        data.EbayUserToken = "";
        data.EbayRefreshToken = "";
        data.EbayTokenExpiresAt = null;
        data.EbayRefreshTokenExpiresAt = null;
        data.EbayTokenType = "";
        data.EbayReauthRequiredAt = null;
        data.EbayReauthReason = "";
        _source.Write(data);
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
        var data = _source.Read();
        data.EbayUserToken = "";
        data.EbayRefreshToken = "";
        data.EbayTokenExpiresAt = null;
        data.EbayRefreshTokenExpiresAt = null;
        data.EbayTokenType = "";
        data.EbayReauthRequiredAt = DateTimeOffset.UtcNow;
        data.EbayReauthReason = reason;
        _source.Write(data);
    }

    public bool IsEbayReauthRequired => _source.Read().EbayReauthRequiredAt is not null;

    public string GetUserToken()    => _source.Read().EbayUserToken;
    public string GetRefreshToken() => _source.Read().EbayRefreshToken;

    public bool IsAccessTokenExpired()
    {
        var data = _source.Read();
        return EbayTokenExpiry.IsAccessTokenExpired(
            data.EbayUserToken, data.EbayTokenExpiresAt,
            !string.IsNullOrWhiteSpace(data.EbayRefreshToken), DateTimeOffset.UtcNow);
    }

    public void EnsureInstallDate()
    {
        var data = _source.Read();
        if (data.InstallDate == null)
        {
            data.InstallDate = DateTimeOffset.UtcNow;
            _source.Write(data);
        }
    }

    public string EnsureAdminKey()
    {
        var data = _source.Read();
        if (string.IsNullOrWhiteSpace(data.AdminKey))
        {
            data.AdminKey = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLower();
            _source.Write(data);
        }
        return data.AdminKey;
    }

    /// <summary>
    /// Whether <paramref name="offered"/> is the admin key, compared without leaking how much of it
    /// was right. The one way the owner dashboard is allowed to check the key in its URL.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key is 128 bits from the CSPRNG (see <see cref="EnsureAdminKey"/>), so it is not going to
    /// be guessed outright. What a plain <c>!=</c> gives away is different and worse: string
    /// comparison returns at the first byte that differs, so a wrong key that shares a prefix with
    /// the real one takes measurably longer to refuse than one that does not. That turns 2^128 into
    /// thirty-two rounds of sixteen guesses, one hex digit at a time, and the dashboard hands over
    /// every seller's usage and the whole recent action log.
    /// </para>
    /// <para>
    /// Timing signal over a network is noisy and this attack is not easy. It is also free to
    /// remove, and "hard to exploit" is a description of the attacker's afternoon rather than a
    /// property of the code.
    /// </para>
    /// </remarks>
    public bool AdminKeyMatches(string? offered) =>
        !string.IsNullOrWhiteSpace(offered) && Csrf.FixedTimeEquals(offered, EnsureAdminKey());

    public int TrialDaysRemaining()
    {
        var installDate = _source.Read().InstallDate;
        if (installDate == null) return 30;
        var elapsed = (DateTimeOffset.UtcNow - installDate.Value).TotalDays;
        return Math.Max(0, 30 - (int)elapsed);
    }

    public bool IsTrialExpired() => TrialDaysRemaining() == 0;

    /// <summary>
    /// True when the background loop should top the access token up now. See
    /// <see cref="EbayTokenExpiry.ShouldRefreshNow"/> — in particular for why a missing access
    /// token or a missing expiry counts as "yes" rather than "nothing to do".
    /// </summary>
    public bool ShouldRefreshAccessToken(int minutes = 20)
    {
        var data = _source.Read();
        return EbayTokenExpiry.ShouldRefreshNow(
            data.EbayUserToken, data.EbayTokenExpiresAt,
            data.EbayRefreshToken, data.EbayRefreshTokenExpiresAt,
            DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes));
    }

    public bool HasValidRefreshToken()
    {
        var data = _source.Read();
        return EbayTokenExpiry.IsRefreshTokenUsable(
            data.EbayRefreshToken, data.EbayRefreshTokenExpiresAt, DateTimeOffset.UtcNow);
    }

    public PublicFields GetPublicFields()
    {
        var data = _source.Read();
        return new()
        {
            EbayClientId            = data.EbayClientId,
            EbayDevId               = data.EbayDevId,
            EbayRuName              = data.EbayRuName,
            EbaySandbox             = data.EbaySandbox,
            EbayFulfillmentPolicyId = data.EbayFulfillmentPolicyId,
            EbayPaymentPolicyId     = data.EbayPaymentPolicyId,
            EbayReturnPolicyId      = data.EbayReturnPolicyId,
            HasBusinessPolicies     = !string.IsNullOrWhiteSpace(data.EbayFulfillmentPolicyId)
                                   && !string.IsNullOrWhiteSpace(data.EbayPaymentPolicyId)
                                   && !string.IsNullOrWhiteSpace(data.EbayReturnPolicyId),
            HasAnthropicKey         = !string.IsNullOrWhiteSpace(data.AnthropicApiKey),
            HasOpenAiKey            = !string.IsNullOrWhiteSpace(data.OpenAiApiKey),
            ImageGenMode            = data.ImageGenMode ?? "disabled",
            LocalSdEndpoint         = data.LocalSdEndpoint ?? "http://127.0.0.1:7860",
            LocalSdBackend          = data.LocalSdBackend ?? "automatic1111",
            LocalSdModelName        = data.LocalSdModelName ?? "",
            ImagePromptTemplate     = data.ImagePromptTemplate ?? "",
            HasEbayClientSecret     = !string.IsNullOrWhiteSpace(data.EbayClientSecret),
            HasEbayUserToken        = !string.IsNullOrWhiteSpace(data.EbayUserToken),
            HasEbayRefreshToken     = !string.IsNullOrWhiteSpace(data.EbayRefreshToken),
            EbayTokenExpiresAt      = data.EbayTokenExpiresAt?.ToString("u"),
            DefaultPostalCode       = data.DefaultPostalCode,
            DefaultCountry          = data.DefaultCountry.Length > 0 ? data.DefaultCountry : "US",
            DefaultPackageType      = data.DefaultPackageType.Length > 0 ? data.DefaultPackageType : "PACKAGE_THICK_ENVELOPE",
            DefaultHandlingTimeDays = data.DefaultHandlingTimeDays > 0 ? data.DefaultHandlingTimeDays : 1,
            DefaultWeightLbs             = data.DefaultWeightLbs,
            DefaultWeightOz              = data.DefaultWeightOz,
            DefaultLengthIn              = data.DefaultLengthIn,
            DefaultWidthIn               = data.DefaultWidthIn,
            DefaultHeightIn              = data.DefaultHeightIn,
            DefaultFulfillmentPolicyId   = data.DefaultFulfillmentPolicyId,
            DefaultBestOffer             = data.DefaultBestOffer,
            HasLicenseKey                = !string.IsNullOrWhiteSpace(data.LicenseKey),
            LicenseKeyPreview            = PreviewLicenseKey(data.LicenseKey),
        };
    }

    private static string PreviewLicenseKey(string key) =>
        string.IsNullOrWhiteSpace(key) ? "" : key[..Math.Min(8, key.Length)] + "****";

    /// <summary>True when this store refused to load existing data and is protecting it from being overwritten.</summary>
    public bool IsProtectingUnreadableFile => _source.IsProtectingUnreadableData;
}

/// <summary>
/// The desktop app's credentials: one credentials.json, held in memory, rewritten on every save.
/// Unchanged behaviour — this is the code that has always been inside
/// <see cref="CredentialsStore"/>, moved behind <see cref="ICredentialsSource"/> so that the hosted
/// build can put a per-user store in its place without a single call site knowing.
/// </summary>
/// <remarks>
/// <see cref="Read"/> deliberately returns the same live instance every time. Callers have always
/// been able to hold onto what <c>Get()</c> gave them, and a copy per call would turn that into a
/// stale read on the desktop build for no benefit — there is only ever one seller here.
/// </remarks>
public sealed class FileCredentialsSource : ICredentialsSource
{
    private static readonly JsonSerializerOptions _opts = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly Credentials _data;

    /// <summary>
    /// Set when the file on disk existed but could not be read. While true, nothing is written
    /// back — see <see cref="Write"/>. This is the difference between "a bad read cost you one
    /// session" and "a bad read cost you every account you have ever connected".
    /// </summary>
    private bool _loadFailedWithFilePresent;

    public FileCredentialsSource(string filePath)
    {
        _filePath = filePath;
        _data     = Load();
    }

    public bool IsProtectingUnreadableData => _loadFailedWithFilePresent;

    public Credentials Read() => _data;

    public void Write(Credentials data)
    {
        // The one case where saving is the wrong thing to do. If a file exists that we could not
        // parse, the in-memory state is empty defaults — and writing those over the file would
        // destroy the only copy of the seller's tokens to record that we failed to read them.
        if (_loadFailedWithFilePresent) return;

        // Atomic: the path never contains a partially-written file, and the previous contents are
        // kept as .bak. See AtomicFile for why WriteAllText was not safe enough for this file.
        AtomicFile.WriteAllText(_filePath, JsonSerializer.Serialize(data, _opts));
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
