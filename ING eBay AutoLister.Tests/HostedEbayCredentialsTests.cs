using System.Reflection;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Configuration;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Where the line falls between the eBay <i>application</i> and an eBay <i>account</i>, on a
/// deployment that has many of the second and exactly one of the first.
/// </summary>
/// <remarks>
/// <para>
/// A hosted seller has no eBay developer account. Asking them for a Client ID, a Client Secret and
/// a RuName before the product does anything is asking them to leave, so the deployment supplies
/// all three — it is the application's own registration, the thing eBay's consent screen names, and
/// it is the same for everybody by definition.
/// </para>
/// <para>
/// The tokens that come back are the opposite of that, and the distinction is the whole of this
/// file. An access token and a refresh token are one seller's eighteen-month grant to list and sell
/// on their account. A server that could read either one out of its own environment would be a
/// server holding one eBay account for the whole userbase — and the way that happens is not malice,
/// it is somebody adding two more lines to <see cref="ServerCredentials"/> that look exactly like
/// the four above them. <see cref="No_ebay_token_can_be_read_from_configuration"/> is what fails.
/// </para>
/// </remarks>
public class HostedEbayCredentialsTests
{
    private const string OwnerClientId  = "INGListi-hosted-PRD-0000000000-0000aaaa";
    private const string OwnerSecret    = "PRD-0000aaaa1111-2222-3333-4444";
    private const string OwnerRuName    = "Owner_Name-OwnerNa-INGLis-hosted-runame";

    // ── The application, which is the owner's ────────────────────────────────────────────────

    [Fact]
    public void The_deployments_ebay_application_reaches_a_user_who_configured_nothing()
    {
        var server = FromEnvironment(
            ("Credentials__EbayClientId",     OwnerClientId),
            ("Credentials__EbayClientSecret", OwnerSecret),
            ("Credentials__EbayRuName",       OwnerRuName));

        // Somebody who has just signed up: an empty row, and nothing they could have entered.
        var record = new Credentials();
        server.ApplyTo(record);

        Assert.Equal(OwnerClientId, record.EbayClientId);
        Assert.Equal(OwnerSecret,   record.EbayClientSecret);
        Assert.Equal(OwnerRuName,   record.EbayRuName);
        Assert.True(server.HasEbayApp);

        // And that is enough for a sign-in URL to be worth building, which is the entire point.
        Assert.Null(EbayAuthUrlCheck.Check(record.EbayClientId, record.EbayClientSecret,
            record.EbayRuName, sandbox: false, bindingProblem: null));
    }

    [Fact]
    public void A_deployment_that_supplies_no_ebay_application_leaves_a_users_own_alone()
    {
        // The desktop-shaped case, and anyone self-hosting with their own eBay developer account.
        // Blank configuration must mean "not configured", never "delete what they entered".
        var server = FromEnvironment(("Credentials__AnthropicApiKey", "sk-ant-owner"));

        var record = new Credentials
        {
            EbayClientId     = "THEIR-OWN-PRD-1111",
            EbayClientSecret = "their-own-secret",
            EbayRuName       = "Their_Own-TheirOw-runame",
        };
        server.ApplyTo(record);

        Assert.Equal("THEIR-OWN-PRD-1111", record.EbayClientId);
        Assert.Equal("their-own-secret",   record.EbayClientSecret);
        Assert.Equal("Their_Own-TheirOw-runame", record.EbayRuName);
        Assert.False(server.HasEbayApp);
    }

    [Fact]
    public void The_owners_client_secret_is_not_copied_into_every_users_row()
    {
        var server = FromEnvironment(
            ("Credentials__EbayClientId",     OwnerClientId),
            ("Credentials__EbayClientSecret", OwnerSecret),
            ("Credentials__EbayRuName",       OwnerRuName));

        // What a save looks like: the record was READ — so it is carrying the owner's values — and
        // is now going back to storage. Writing them down would put the owner's secret in every
        // row, where a rotation cannot reach it and a leaked database hands it out N times.
        var read = new Credentials { EbayRefreshToken = "the-sellers-own-grant" };
        server.ApplyTo(read);

        var stored = server.StrippedCopy(read);

        Assert.Equal("", stored.EbayClientId);
        Assert.Equal("", stored.EbayClientSecret);
        Assert.Equal("", stored.EbayRuName);
        // The seller's own grant is theirs and stays.
        Assert.Equal("the-sellers-own-grant", stored.EbayRefreshToken);
        // And the record the request in flight is holding is untouched.
        Assert.Equal(OwnerSecret, read.EbayClientSecret);
    }

    [Fact]
    public void A_users_own_ebay_application_survives_a_save_when_the_deployment_has_none()
    {
        var server = new ServerCredentials();
        var stored = server.StrippedCopy(new Credentials { EbayClientId = "THEIR-OWN-PRD-1111" });

        Assert.Equal("THEIR-OWN-PRD-1111", stored.EbayClientId);
    }

    // ── The tokens, which are never the owner's ──────────────────────────────────────────────

    /// <summary>
    /// The one that must never be made to pass by relaxing it. If this fails, a hosted deployment
    /// can be handed one seller's eBay account to use for everybody.
    /// </summary>
    [Fact]
    public void No_ebay_token_can_be_read_from_configuration()
    {
        var server = FromEnvironment(
            ("Credentials__EbayUserToken",       "v^1.1#i^1#an-access-token-that-must-not-be-shared"),
            ("Credentials__EbayRefreshToken",    "v^1.1#i^1#a-refresh-token-that-must-not-be-shared"),
            ("Credentials__EbayTokenType",       "User Access Token"),
            ("EBAY_USER_TOKEN",                  "another-way-to-try-it"),
            ("EBAY_REFRESH_TOKEN",               "another-way-to-try-it"));

        var record = new Credentials();
        server.ApplyTo(record);

        Assert.Equal("", record.EbayUserToken);
        Assert.Equal("", record.EbayRefreshToken);
        Assert.Equal("", record.EbayTokenType);
        Assert.Null(record.EbayTokenExpiresAt);
        Assert.Null(record.EbayRefreshTokenExpiresAt);
    }

    /// <summary>
    /// The same rule, one level up: not "no value arrived" but "there is nowhere for one to land".
    /// A property added to <see cref="ServerCredentials"/> is read from configuration by
    /// <c>FromConfiguration</c> — so the property existing at all is the defect, and this fails on
    /// the line that adds it rather than on the deployment that sets it.
    /// </summary>
    [Fact]
    public void ServerCredentials_has_no_property_that_could_hold_a_users_ebay_grant()
    {
        var forbidden = new[]
        {
            nameof(Credentials.EbayUserToken),
            nameof(Credentials.EbayRefreshToken),
            nameof(Credentials.EbayTokenType),
            nameof(Credentials.EbayTokenExpiresAt),
            nameof(Credentials.EbayRefreshTokenExpiresAt),
        };

        var present = typeof(ServerCredentials)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Intersect(forbidden, StringComparer.Ordinal)
            .ToArray();

        Assert.True(present.Length == 0,
            $"ServerCredentials must not carry {string.Join(", ", present)}. Anything on that type is " +
            "read from the deployment's own environment and handed to every signed-in user, which for an " +
            "eBay token means one seller's account being used to list on everybody's behalf. The tokens " +
            "belong in the user's encrypted row — see PerUserCredentialsSource.");
    }

    [Fact]
    public void A_user_cannot_post_the_deployments_ebay_application_out_from_under_everyone()
    {
        var server = FromEnvironment(
            ("Credentials__EbayClientId",     OwnerClientId),
            ("Credentials__EbayClientSecret", OwnerSecret),
            ("Credentials__EbayRuName",       OwnerRuName));

        using var scratch = new ScratchFolder();
        var stored = new UserCredentialsStore(Path.Combine(scratch.Path, "creds.db"),
            CredentialCipher.FromKeyMaterial("a-scratch-deployment-secret"));
        var store = new CredentialsStore(new PerUserCredentialsSource(stored, () => 7, server));

        // The Settings screen is the same screen in both builds, and on a server that box is not
        // theirs. Posting into it changes nothing — for them or for anybody else.
        store.Save(new CredentialsPatch
        {
            EbayClientId     = "MINE-PRD-9999",
            EbayClientSecret = "my-own-secret",
            EbayRuName       = "Mine-Mine-Mine-runame",
        });

        Assert.Equal(OwnerClientId, store.Get().EbayClientId);
        Assert.Equal(OwnerSecret,   store.Get().EbayClientSecret);
        Assert.Equal(OwnerRuName,   store.Get().EbayRuName);
    }

    [Fact]
    public void Two_users_of_one_deployments_ebay_application_still_have_two_accounts()
    {
        var server = FromEnvironment(
            ("Credentials__EbayClientId",     OwnerClientId),
            ("Credentials__EbayClientSecret", OwnerSecret),
            ("Credentials__EbayRuName",       OwnerRuName));

        using var scratch = new ScratchFolder();
        var stored = new UserCredentialsStore(Path.Combine(scratch.Path, "creds.db"),
            CredentialCipher.FromKeyMaterial("a-scratch-deployment-secret"));

        long who = 1;
        var store = new CredentialsStore(new PerUserCredentialsSource(stored, () => who, server));

        store.SaveOAuthTokensFull("first-access", "first-refresh", 7200, 47304000, "User Access Token");
        who = 2;
        store.SaveOAuthTokensFull("second-access", "second-refresh", 7200, 47304000, "User Access Token");

        who = 1;
        Assert.Equal("first-refresh", store.GetRefreshToken());
        Assert.Equal(OwnerClientId, store.Get().EbayClientId);   // one application…
        who = 2;
        Assert.Equal("second-refresh", store.GetRefreshToken()); // …two accounts.
        Assert.Equal(OwnerClientId, store.Get().EbayClientId);
    }

    // ── Where eBay comes back to ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_deployment_that_configures_nothing_is_the_desktop_relay_it_has_always_been()
    {
        var redirect = EbayRedirect.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Equal("https://inglisting.com/api/ebay/callback", redirect.Uri);
        Assert.False(redirect.IsConfigured);
        Assert.Null(redirect.Problem);
    }

    [Fact]
    public void A_hosted_deployment_returns_to_its_own_hostname()
    {
        var redirect = FromEnvironmentRedirect("https://app.inglisting.com/api/ebay/callback");

        Assert.Equal("https://app.inglisting.com/api/ebay/callback", redirect.Uri);
        Assert.True(redirect.IsConfigured);
        Assert.Null(redirect.Problem);

        // And that URL is what the app tells somebody to register, instead of the desktop relay.
        var problem = EbayAuthUrlCheck.Check("a-client-id", "a-secret", ruName: "",
            sandbox: false, bindingProblem: null, redirectUri: redirect.Uri);

        Assert.Equal("no_runame", problem!.Code);
        Assert.Contains("https://app.inglisting.com/api/ebay/callback", problem.NextAction, StringComparison.Ordinal);
        Assert.DoesNotContain("localhost", problem.NextAction, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_trailing_slash_and_a_query_string_are_not_two_different_registrations()
    {
        Assert.Equal("https://app.inglisting.com/api/ebay/callback",
            FromEnvironmentRedirect("https://app.inglisting.com/api/ebay/callback/").Uri);
        Assert.Equal("https://app.inglisting.com/api/ebay/callback",
            FromEnvironmentRedirect("https://app.inglisting.com/api/ebay/callback?x=1").Uri);
    }

    [Fact]
    public void A_redirect_that_is_not_a_url_is_reported_rather_than_obeyed()
    {
        var redirect = FromEnvironmentRedirect("app.inglisting.com/api/ebay/callback");

        // Falls back rather than throwing: a malformed redirect is one broken feature, and a
        // server that will not start over it is every feature broken.
        Assert.Equal(EbayRedirect.DesktopDefault, redirect.Uri);
        Assert.False(redirect.IsConfigured);
        Assert.NotNull(redirect.Problem);
        Assert.Contains(EbayRedirect.Setting, redirect.Problem, StringComparison.Ordinal);
    }

    // ── The scopes, which eBay judges all-or-nothing ─────────────────────────────────────────

    /// <summary>
    /// Measured against the live production keyset on 2026-08-13: the four default scopes reach
    /// eBay's real consent screen, and adding <c>sell.negotiation</c> turns the same URL into
    /// <c>errorOauth?errorId=invalid_scope</c> before the seller sees anything. eBay does not drop
    /// the scope it will not grant — it refuses the request. So the default set must contain
    /// nothing optional.
    /// </summary>
    [Fact]
    public void A_sign_in_asks_for_nothing_optional_by_default()
    {
        Assert.DoesNotContain(EbayAuthUrlCheck.NegotiationScope, EbayAuthUrlCheck.Scopes);

        var url = EbayAuthUrlCheck.Build(
            "https://auth.ebay.com/oauth2/authorize", "a-client-id", "a-runame", "a-state");

        Assert.DoesNotContain("sell.negotiation", url, StringComparison.Ordinal);
        // And the four the product cannot list without are all still there.
        foreach (var scope in EbayAuthUrlCheck.Scopes)
            Assert.Contains(Uri.EscapeDataString(scope), url, StringComparison.Ordinal);
    }

    [Fact]
    public void A_keyset_with_send_offer_enabled_can_be_told_to_ask_for_it()
    {
        var options = EbayScopeOptions.FromConfiguration(Build([("Ebay__RequestNegotiationScope", "true")]));
        Assert.True(options.IncludeNegotiation);

        var url = EbayAuthUrlCheck.Build(
            "https://auth.ebay.com/oauth2/authorize", "a-client-id", "a-runame", "a-state",
            options.IncludeNegotiation);

        Assert.Contains(Uri.EscapeDataString(EbayAuthUrlCheck.NegotiationScope), url, StringComparison.Ordinal);
    }

    [Fact]
    public void Anything_other_than_a_deliberate_true_leaves_it_off()
    {
        // "yes", "1", a typo, or the setting absent entirely. The failure mode of guessing wrong
        // here is every sign-in on the deployment, so only the word true counts.
        Assert.False(EbayScopeOptions.FromConfiguration(Build([])).IncludeNegotiation);
        Assert.False(EbayScopeOptions.FromConfiguration(Build([("Ebay__RequestNegotiationScope", "1")])).IncludeNegotiation);
        Assert.False(EbayScopeOptions.FromConfiguration(Build([("Ebay__RequestNegotiationScope", "yes")])).IncludeNegotiation);
        Assert.False(EbayScopeOptions.Default.IncludeNegotiation);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Configuration built the way a host builds it — <c>__</c> for the <c>:</c> separator — so
    /// that what is proved here is the name that goes in <c>/etc/ing-listing-engine/.env</c>.
    /// </summary>
    private static ServerCredentials FromEnvironment(params (string Key, string Value)[] variables) =>
        ServerCredentials.FromConfiguration(Build(variables));

    private static EbayRedirect FromEnvironmentRedirect(string value) =>
        EbayRedirect.FromConfiguration(Build([("Ebay__RedirectUri", value)]));

    private static IConfiguration Build((string Key, string Value)[] variables) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(variables.ToDictionary(v => v.Key.Replace("__", ":"), v => (string?)v.Value))
            .Build();

    private sealed class ScratchFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ing-hosted-ebay", Guid.NewGuid().ToString("N"));

        public ScratchFolder() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
