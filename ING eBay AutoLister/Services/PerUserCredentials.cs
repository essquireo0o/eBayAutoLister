using System.Text.Json;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// The credentials a hosted deployment's owner supplies once, for everybody — and which therefore
/// must never come out of a user's own record.
/// </summary>
/// <remarks>
/// <para>
/// The Anthropic key is the reason this class exists. On the hosted trial the owner is paying for
/// every user's analysis, so the key is theirs, it lives in the host's configuration, and nobody
/// signing up is asked for one. That is deliberate — see the hosted task list — and it is the only
/// thing in this app where "shared" is the right answer.
/// </para>
/// <para>
/// The rest are shared for the same reason rather than by accident: the Stripe keys and the admin
/// key are the owner's business, the comps API and scraper are the deployment's own infrastructure,
/// and the licence is the deployment's.
/// </para>
/// <para>
/// <b>The eBay line, which is drawn between two things that look alike.</b> The Client ID, Client
/// Secret and RuName are the <i>application's</i> registration with eBay — one developer account,
/// one set of keys, one consent screen saying "ING Listing Engine wants access". They are shared,
/// and they must be: a hosted seller has no eBay developer account, cannot make one in the middle
/// of signing up, and would otherwise be asked to paste three values they have never heard of
/// before the product does anything. The OAuth tokens are the opposite — they are the seller's own
/// eighteen-month grant to sell on their account — and they stay in that seller's encrypted row,
/// with the business policies and the sandbox flag. They are absent from this list on purpose and
/// <see cref="PerUserData"/> is the reason it matters: sharing them would let whoever connected
/// eBay first publish listings on everybody else's behalf.
/// </para>
/// <para>
/// The app identifiers are overlaid <i>only when the deployment configured them</i>, unlike the
/// fields above. A desktop-shaped deployment that sets none of them leaves a user's own values
/// alone, which is what keeps "bring your own eBay app" working for anyone running this themselves.
/// </para>
/// <para>
/// These fields are read-only to a user. <see cref="StrippedCopy"/> blanks them out of whatever is
/// about to be written to a user's row, and <see cref="ApplyTo"/> puts the configured values back
/// on the way out — so a hosted user who posts an Anthropic key to <c>/api/setup/save</c> changes
/// nothing, for themselves or for anybody else.
/// </para>
/// </remarks>
public sealed class ServerCredentials
{
    /// <summary>The configuration section these are read from. Set them in the host's environment.</summary>
    public const string Section = "Credentials";

    public string AnthropicApiKey      { get; init; } = "";
    public string OpenAiApiKey         { get; init; } = "";
    public string StripeSecretKey      { get; init; } = "";
    public string StripePublishableKey { get; init; } = "";
    public string StripeWebhookSecret  { get; init; } = "";
    public string AdminKey             { get; init; } = "";
    public string MarketCompsApiUrl    { get; init; } = "";
    public string MarketCompsApiKey    { get; init; } = "";

    /// <summary>
    /// The live sold-comps API key. Shared for the same reason the Anthropic key is: it is the
    /// owner's, it is paid for, and there are only 50,000 calls on it — so nobody signing up is
    /// asked for one and no user's row may hold one. What rations it per account is
    /// <see cref="LiveCompsBudget"/>.
    /// </summary>
    public string OpenWebNinjaApiKey  { get; init; } = "";

    public string CompsScraperDir      { get; init; } = "";
    public string CompsScraperPython   { get; init; } = "";
    public string LicenseKey           { get; init; } = "";

    // ── The eBay application's own registration ──────────────────────────────────────────────
    // Shared, and only these three. Every token below them in Credentials — EbayUserToken,
    // EbayRefreshToken, the expiries — is deliberately not here and must never be added: a server
    // that could read a user access token out of its own environment is a server with one eBay
    // account for everybody. HostedEbayCredentialsTests fails if one appears.

    public string EbayClientId     { get; init; } = "";
    public string EbayClientSecret { get; init; } = "";
    public string EbayRuName       { get; init; } = "";

    /// <summary>Trading-API only; not part of OAuth. Shared with the rest of the registration.</summary>
    public string EbayDevId        { get; init; } = "";

    /// <summary>True when this deployment supplies the eBay application, so no user is asked for one.</summary>
    public bool HasEbayApp =>
        !string.IsNullOrWhiteSpace(EbayClientId) && !string.IsNullOrWhiteSpace(EbayClientSecret);

    /// <summary>
    /// Reads the owner's values out of configuration. The two keys anyone actually sets by hand —
    /// the Anthropic and OpenAI keys — are also accepted under their conventional flat environment
    /// variable names, because that is the form they arrive in from every host's settings screen
    /// and from a local <c>.env</c>.
    /// </summary>
    public static ServerCredentials FromConfiguration(IConfiguration configuration) => new()
    {
        EbayClientId         = Read(configuration, "EbayClientId"),
        EbayClientSecret     = Read(configuration, "EbayClientSecret"),
        EbayRuName           = Read(configuration, "EbayRuName"),
        EbayDevId            = Read(configuration, "EbayDevId"),
        AnthropicApiKey      = Read(configuration, "AnthropicApiKey", "ANTHROPIC_API_KEY"),
        OpenAiApiKey         = Read(configuration, "OpenAiApiKey", "OPENAI_API_KEY"),
        StripeSecretKey      = Read(configuration, "StripeSecretKey"),
        StripePublishableKey = Read(configuration, "StripePublishableKey"),
        StripeWebhookSecret  = Read(configuration, "StripeWebhookSecret"),
        // Generated when the owner has not set one, so the owner dashboard at /owner?k= is reachable
        // on a fresh deployment. In memory only: it is not a user's to store and not a user's to see.
        AdminKey             = Fallback(Read(configuration, "AdminKey"),
                                        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()),
        MarketCompsApiUrl    = Read(configuration, "MarketCompsApiUrl"),
        MarketCompsApiKey    = Read(configuration, "MarketCompsApiKey"),
        OpenWebNinjaApiKey   = Read(configuration, "OpenWebNinjaApiKey", "OPENWEBNINJA_API_KEY"),
        CompsScraperDir      = Read(configuration, "CompsScraperDir"),
        CompsScraperPython   = Read(configuration, "CompsScraperPython"),
        LicenseKey           = Read(configuration, "LicenseKey"),
    };

    /// <summary>Overwrites the shared fields on a record read out of one user's row.</summary>
    public void ApplyTo(Credentials record)
    {
        record.AnthropicApiKey      = AnthropicApiKey;
        record.OpenAiApiKey         = OpenAiApiKey;
        record.StripeSecretKey      = StripeSecretKey;
        record.StripePublishableKey = StripePublishableKey;
        record.StripeWebhookSecret  = StripeWebhookSecret;
        record.AdminKey             = AdminKey;
        record.MarketCompsApiUrl    = MarketCompsApiUrl;
        record.MarketCompsApiKey    = MarketCompsApiKey;
        record.OpenWebNinjaApiKey   = OpenWebNinjaApiKey;
        record.CompsScraperDir      = CompsScraperDir;
        record.CompsScraperPython   = CompsScraperPython;
        record.LicenseKey           = LicenseKey;

        // Only when this deployment actually has an eBay application. Blank here means "not
        // configured", and overwriting a user's own Client ID with nothing would disconnect the
        // person running this for themselves the moment they upgraded.
        Overlay(EbayClientId,     v => record.EbayClientId     = v);
        Overlay(EbayClientSecret, v => record.EbayClientSecret = v);
        Overlay(EbayRuName,       v => record.EbayRuName       = v);
        Overlay(EbayDevId,        v => record.EbayDevId        = v);
    }

    private static void Overlay(string value, Action<string> assign)
    {
        if (!string.IsNullOrWhiteSpace(value)) assign(value);
    }

    /// <summary>
    /// A copy of <paramref name="record"/> with every shared field blanked, ready to be stored.
    /// A copy rather than the record itself: the caller is holding the object it just read, and
    /// blanking that in place would take the owner's AI key out from under the request in flight.
    /// </summary>
    /// <remarks>
    /// An instance method because which fields are shared is no longer fixed: the eBay application
    /// identifiers are shared only where a deployment supplies them, and blanking them everywhere
    /// would delete the Client ID of anyone running this with their own eBay app. What this stops
    /// is the round trip — read the owner's Client Secret, save the Settings screen, and the owner's
    /// secret is now copied into every user's row, where a rotation cannot reach it.
    /// </remarks>
    public Credentials StrippedCopy(Credentials record)
    {
        var copy = JsonSerializer.Deserialize<Credentials>(JsonSerializer.Serialize(record)) ?? new Credentials();

        copy.AnthropicApiKey      = "";
        copy.OpenAiApiKey         = "";
        copy.StripeSecretKey      = "";
        copy.StripePublishableKey = "";
        copy.StripeWebhookSecret  = "";
        copy.AdminKey             = "";
        copy.MarketCompsApiUrl    = "";
        copy.MarketCompsApiKey    = "";
        copy.OpenWebNinjaApiKey   = "";
        copy.CompsScraperDir      = "";
        copy.CompsScraperPython   = "";
        copy.LicenseKey           = "";

        Overlay(EbayClientId,     _ => copy.EbayClientId     = "");
        Overlay(EbayClientSecret, _ => copy.EbayClientSecret = "");
        Overlay(EbayRuName,       _ => copy.EbayRuName       = "");
        Overlay(EbayDevId,        _ => copy.EbayDevId        = "");

        return copy;
    }

    private static string Read(IConfiguration configuration, string name, string? environmentVariable = null) =>
        (configuration[$"{Section}:{name}"]
         ?? (environmentVariable is null ? null : configuration[environmentVariable])
         ?? "").Trim();

    private static string Fallback(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}

/// <summary>
/// The hosted build's credentials: the signed-in user's own encrypted row, with the owner's shared
/// server-side fields laid over the top.
/// </summary>
/// <remarks>
/// <para>
/// Every read asks who is making this request and goes to that person's row. Nothing is cached
/// between calls, which is the whole point — a cached record on a process-wide store is exactly
/// how one user ends up publishing with another user's eBay token.
/// </para>
/// <para>
/// <b>Why the store is still a singleton in the container.</b> A scoped registration would be the
/// obvious way to say "per request", and it is not available here: ten services and three
/// background workers take a <see cref="CredentialsStore"/> in their constructors and are
/// singletons themselves, and ASP.NET Core refuses — correctly — to hand a scoped service to a
/// singleton. Turning all of them scoped would ripple through the whole app for no gain, because
/// what has to be per-request is the credentials, not the object that fetches them. So the store is
/// one instance and the answer it gives is resolved per request, through
/// <see cref="IHttpContextAccessor"/>.
/// </para>
/// <para>
/// <b>When nobody is signed in.</b> Background work — the token-refresh loop, the earnings import,
/// the SEO rewrite job — runs with no HttpContext and so with no user. Those calls get the shared
/// server-side fields and an otherwise empty record: no eBay tokens, and writes dropped on the
/// floor. That is the only safe answer. Picking a user to act as would be arbitrary, and writing
/// into whichever row was last touched is how a background job hands one seller's refresh token to
/// another. The visible consequence is that hosted background jobs do no eBay work until they are
/// made user-aware, which is the next task in the hosted list rather than something silently broken
/// here.
/// </para>
/// </remarks>
public sealed class PerUserCredentialsSource : ICredentialsSource
{
    private readonly UserCredentialsStore _stored;
    private readonly Func<long?> _currentUserId;
    private readonly ServerCredentials _server;

    /// <summary>
    /// Users whose stored row would not decrypt. Writing for them is refused for as long as this
    /// process lives — see <see cref="UserCredentialsStore"/> for why an unreadable row is
    /// protected rather than replaced.
    /// </summary>
    private readonly HashSet<long> _unreadable = [];
    private readonly object _gate = new();

    public PerUserCredentialsSource(UserCredentialsStore stored, Func<long?> currentUserId, ServerCredentials server)
    {
        _stored        = stored;
        _currentUserId = currentUserId;
        _server        = server;
    }

    public Credentials Read()
    {
        var record = new Credentials();

        if (_currentUserId() is { } userId)
        {
            var stored = _stored.Load(userId);
            record = stored.Data;
            if (stored.Unreadable) lock (_gate) _unreadable.Add(userId);
        }

        _server.ApplyTo(record);
        return record;
    }

    public void Write(Credentials data)
    {
        if (_currentUserId() is not { } userId) return;
        lock (_gate) { if (_unreadable.Contains(userId)) return; }

        _stored.Save(userId, _server.StrippedCopy(data));
    }

    public bool IsProtectingUnreadableData
    {
        get
        {
            if (_currentUserId() is not { } userId) return false;
            lock (_gate) return _unreadable.Contains(userId);
        }
    }
}

/// <summary>
/// Which of the two credential stores this build runs on. One line in Program.cs, and the
/// difference between one eBay account for the whole internet and one each.
/// </summary>
public static class PerUserCredentials
{
    /// <summary>
    /// Registers the <see cref="CredentialsStore"/> every endpoint and service injects: the single
    /// credentials.json in the desktop build, per-user encrypted rows in the hosted one.
    /// </summary>
    /// <param name="desktopFilePath">
    /// Where the desktop build's credentials.json lives. Passed in rather than derived here because
    /// it is an absolute path from <see cref="AppPaths"/> and must not follow ContentRootPath: that
    /// file holds an eighteen-month eBay grant only the seller, in a browser, can replace.
    /// </param>
    /// <param name="hosted">Overrides <see cref="HostedAuth.IsHostedBuild"/>. Tests pass both.</param>
    public static void AddCredentials(WebApplicationBuilder builder, string desktopFilePath, bool? hosted = null)
    {
        if (!(hosted ?? HostedAuth.IsHostedBuild))
        {
            builder.Services.AddSingleton(new CredentialsStore(desktopFilePath));
            return;
        }

        // How the store learns who is asking. The accessor is the only way to reach the request
        // from a singleton, and it is why this can stay one instance — see PerUserCredentialsSource.
        builder.Services.AddHttpContextAccessor();

        // Constructed here, at startup, rather than lazily on the first save: a hosted deployment
        // with no encryption key configured must fail while somebody is watching the logs, not at
        // the moment it would otherwise have written a stranger's eBay token to disk in the clear.
        var cipher = CredentialCipher.FromConfiguration(builder.Configuration);
        var server = ServerCredentials.FromConfiguration(builder.Configuration);

        // TryAdd, so a test can point the table at a scratch database. Same pattern as the users
        // table in HostedAuth.AddAccounts, and for the same reason.
        builder.Services.TryAddSingleton(sp => new UserCredentialsStore(
            sp.GetRequiredService<ListingDatabase>(), cipher));

        // Through HostedAuth, so which claim carries the id is decided in one place — the same
        // place that puts it there when somebody signs in.
        builder.Services.AddSingleton(sp => new CredentialsStore(new PerUserCredentialsSource(
            sp.GetRequiredService<UserCredentialsStore>(),
            () => HostedAuth.CurrentUserId(sp.GetRequiredService<IHttpContextAccessor>()),
            server)));
    }
}
