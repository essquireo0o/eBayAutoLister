using System.Net;
using System.Net.Http.Json;
using System.Text;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Proves the thing that decides whether a second person can be allowed to sign up: two signed-in
/// sellers have two different eBay accounts, and neither can read the other's.
/// </summary>
/// <remarks>
/// <para>
/// Until this change <see cref="CredentialsStore"/> was one process-wide object over one
/// credentials.json. On a desktop machine that is the product's configuration. On a server it is
/// one eBay refresh token — an eighteen-month grant to sell on somebody's account — handed to
/// whoever signs up next, and one set of business policies, and one Settings screen showing all of
/// it to everybody.
/// </para>
/// <para>
/// The end-to-end tests below start a real Kestrel server and drive it through two cookie jars,
/// because "who is asking" is answered from the request in flight. A unit test that called the
/// store twice with a stubbed user id would pass just as happily against a store that cached the
/// first seller's record and handed it to the second, which is the failure that matters here.
/// </para>
/// </remarks>
public class PerUserCredentialsTests
{
    private const string Password = "a-long-enough-password";

    /// <summary>Not a real key, and not a real anything — a temp deployment's scratch secret.</summary>
    private const string EncryptionKey = "test-encryption-key-not-a-real-one";

    /// <summary>The owner's, shared, from configuration. Nobody signing up is asked for one.</summary>
    private const string OwnerAiKey = "sk-ant-owner-key-for-tests";

    // ── The acceptance ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Two_signed_in_sellers_have_two_different_ebay_accounts()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        await first.SaveAsync(new { ebayClientId = "FIRST-PRD-1111", ebayClientSecret = "first-secret" });
        await second.SaveAsync(new { ebayClientId = "SECOND-PRD-2222", ebayClientSecret = "second-secret" });

        Assert.Equal("FIRST-PRD-1111",  (await first.FieldsAsync()).EbayClientId);
        Assert.Equal("SECOND-PRD-2222", (await second.FieldsAsync()).EbayClientId);
    }

    [Fact]
    public async Task One_sellers_ebay_tokens_are_never_handed_to_another()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        await first.SaveAsync(new { ebayRefreshToken = "first-refresh-token", ebayUserToken = "first-user-token" });
        await second.SaveAsync(new { ebayRefreshToken = "second-refresh-token", ebayUserToken = "second-user-token" });

        var firstSecrets  = await first.SecretsAsync();
        var secondSecrets = await second.SecretsAsync();

        Assert.Equal("first-refresh-token",  firstSecrets.EbayRefreshToken);
        Assert.Equal("first-user-token",     firstSecrets.EbayUserToken);
        Assert.Equal("second-refresh-token", secondSecrets.EbayRefreshToken);
        Assert.Equal("second-user-token",    secondSecrets.EbayUserToken);
    }

    [Fact]
    public async Task A_seller_who_has_connected_nothing_inherits_nobody_elses_connection()
    {
        await using var server = await StartAsync();
        var connected = await server.SignUpAsync("connected@example.com");
        await connected.SaveAsync(new
        {
            ebayClientId            = "CONNECTED-PRD-1111",
            ebayRefreshToken        = "a-live-refresh-token",
            ebayFulfillmentPolicyId = "111",
        });

        // Signs up second, having configured nothing. The old shared store would have shown them a
        // fully connected app and let them publish on the first seller's account.
        var fresh = await server.SignUpAsync("fresh@example.com");

        var fields  = await fresh.FieldsAsync();
        var secrets = await fresh.SecretsAsync();

        Assert.Equal("", fields.EbayClientId);
        Assert.Equal("", fields.EbayFulfillmentPolicyId);
        Assert.False(fields.HasEbayRefreshToken);
        Assert.Equal("", secrets.EbayRefreshToken);
    }

    [Fact]
    public async Task A_seller_signing_back_in_still_has_the_account_they_connected()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.SaveAsync(new { ebayClientId = "SELLER-PRD-1111", ebayRefreshToken = "the-refresh-token" });

        // Per-user does not mean per-session: the row outlives the cookie, and a seller who signs
        // out has not disconnected eBay.
        var returning = await server.SignInAsync("seller@example.com");

        Assert.Equal("SELLER-PRD-1111", (await returning.FieldsAsync()).EbayClientId);
        Assert.Equal("the-refresh-token", (await returning.SecretsAsync()).EbayRefreshToken);
    }

    [Fact]
    public async Task Nobody_signed_in_reads_nobodys_credentials()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.SaveAsync(new { ebayRefreshToken = "the-refresh-token" });

        // The endpoints are closed anyway — this is the belt to that pair of braces. Under HOSTED a
        // caller with no session gets a 401 rather than the last seller who happened to save.
        var anonymous = server.Anonymous();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/setup/fields")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/test/secrets")).StatusCode);
    }

    // ── The owner's key, which is the one thing that stays shared ────────────────────────────

    [Fact]
    public async Task The_owners_ai_key_is_shared_and_is_not_a_users_to_change()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        // Both get the owner's key without ever being asked for one — that is what pays for the
        // hosted trial.
        Assert.Equal(OwnerAiKey, (await first.SecretsAsync()).AnthropicApiKey);
        Assert.Equal(OwnerAiKey, (await second.SecretsAsync()).AnthropicApiKey);
        Assert.True((await first.FieldsAsync()).HasAnthropicKey);

        // And a user posting one of their own changes nothing, for themselves or for anybody else.
        // The Settings screen is the same screen in both builds; on a server that box is not theirs.
        await first.SaveAsync(new { anthropicApiKey = "sk-ant-somebody-elses-idea" });

        Assert.Equal(OwnerAiKey, (await first.SecretsAsync()).AnthropicApiKey);
        Assert.Equal(OwnerAiKey, (await second.SecretsAsync()).AnthropicApiKey);
    }

    // ── At rest ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task An_ebay_refresh_token_is_not_in_the_database_in_the_clear()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.SaveAsync(new { ebayRefreshToken = "v1.a-refresh-token-worth-stealing" });

        var stored = server.StoredTextFor(seller.UserId);
        Assert.NotNull(stored);
        Assert.DoesNotContain("a-refresh-token-worth-stealing", stored, StringComparison.Ordinal);

        // Not just the column — the whole file. A copy of the database is the realistic way this
        // leaks, and SQLite is perfectly capable of leaving an older plaintext page behind.
        Assert.DoesNotContain("a-refresh-token-worth-stealing", server.DatabaseBytesAsText(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_written_with_one_key_cannot_be_read_with_another()
    {
        using var scratch = new ScratchDatabase();
        var written = new UserCredentialsStore(scratch.Path, CredentialCipher.FromKeyMaterial(EncryptionKey));
        written.Save(7, new Credentials { EbayRefreshToken = "the-refresh-token" });

        var withAnotherKey = new UserCredentialsStore(scratch.Path,
            CredentialCipher.FromKeyMaterial("a-completely-different-deployment-secret"));

        var read = withAnotherKey.Load(7);

        Assert.True(read.Unreadable);
        Assert.Equal("", read.Data.EbayRefreshToken);
    }

    [Fact]
    public void A_row_that_will_not_decrypt_is_protected_rather_than_replaced()
    {
        using var scratch = new ScratchDatabase();
        var written = new UserCredentialsStore(scratch.Path, CredentialCipher.FromKeyMaterial(EncryptionKey));
        written.Save(7, new Credentials { EbayRefreshToken = "the-refresh-token" });
        var before = written.ReadStoredText(7);

        var wrongKey = new UserCredentialsStore(scratch.Path,
            CredentialCipher.FromKeyMaterial("a-completely-different-deployment-secret"));
        var store = new CredentialsStore(new PerUserCredentialsSource(wrongKey, () => 7, new ServerCredentials()));

        // Reading first is what notices. Then a save — the thing that would have written empty
        // defaults over the only copy of an eighteen-month eBay grant.
        Assert.Equal("", store.GetRefreshToken());
        Assert.True(store.IsProtectingUnreadableFile);
        store.Save(new CredentialsPatch { EbayClientId = "SOMETHING-NEW" });

        Assert.Equal(before, wrongKey.ReadStoredText(7));
        // And with the right key back, the seller's connection is still there.
        Assert.Equal("the-refresh-token", written.Load(7).Data.EbayRefreshToken);
    }

    // ── Background work, which has no user ───────────────────────────────────────────────────

    [Fact]
    public void A_background_job_with_no_signed_in_user_acts_for_nobody()
    {
        using var scratch = new ScratchDatabase();
        var users = new UserCredentialsStore(scratch.Path, CredentialCipher.FromKeyMaterial(EncryptionKey));
        users.Save(7, new Credentials { EbayRefreshToken = "the-sellers-refresh-token" });

        // No HttpContext, so no user: the token-refresh loop, the earnings import, the SEO job.
        var server = new ServerCredentials { AnthropicApiKey = OwnerAiKey };
        var store  = new CredentialsStore(new PerUserCredentialsSource(users, () => null, server));

        // It gets the server's own settings and nobody's eBay account — never the last seller who
        // happened to save. Picking one would be picking whose account to publish on.
        Assert.Equal(OwnerAiKey, store.Get().AnthropicApiKey);
        Assert.Equal("", store.GetRefreshToken());

        store.SaveOAuthTokensFull("stray-access-token", "stray-refresh-token", 7200, 0, "User Access Token");
        Assert.Equal("the-sellers-refresh-token", users.Load(7).Data.EbayRefreshToken);
    }

    // ── The desktop build, which is not to change ────────────────────────────────────────────

    [Fact]
    public void The_desktop_build_still_keeps_one_file_for_the_one_seller()
    {
        using var scratch = new ScratchDatabase();
        var file = Path.Combine(Path.GetDirectoryName(scratch.Path)!, "credentials.json");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = scratch.Folder });
        builder.Logging.ClearProviders();
        PerUserCredentials.AddCredentials(builder, file, hosted: false);
        using var app = builder.Build();

        var store = app.Services.GetRequiredService<CredentialsStore>();
        store.Save(new CredentialsPatch { EbayClientId = "ING-PRD-1234", AnthropicApiKey = "sk-ant-the-sellers-own-key" });

        // Their file, their machine, their own AI key — no user id, no database, no encryption key
        // to configure. And the same instance every time it is asked for.
        Assert.True(File.Exists(file));
        Assert.Contains("ING-PRD-1234", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal("sk-ant-the-sellers-own-key", store.Get().AnthropicApiKey);
        Assert.Same(store, app.Services.GetRequiredService<CredentialsStore>());
    }

    [Fact]
    public void A_hosted_build_with_nowhere_to_get_an_encryption_key_refuses_to_start()
    {
        using var scratch = new ScratchDatabase();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = scratch.Folder });
        builder.Logging.ClearProviders();

        // Loudly, at startup, and naming the setting — not quietly at the first token write, with
        // somebody's eBay grant going to disk in the clear.
        var refused = Assert.Throws<InvalidOperationException>(() =>
            PerUserCredentials.AddCredentials(builder, "unused.json", hosted: true));

        Assert.Contains(CredentialCipher.KeySetting, refused.Message, StringComparison.Ordinal);
        Assert.Contains(CredentialCipher.KeyEnvironmentVariable, refused.Message, StringComparison.Ordinal);
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own credential and auth wiring around three stand-in endpoints: the two the
    /// Settings screen really uses, and one that hands back the secrets themselves.
    /// </summary>
    private static async Task<CredentialServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-per-user-credentials", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        // What a hosted deployment puts in its host's environment settings, and nowhere else.
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [CredentialCipher.KeySetting]                            = EncryptionKey,
            [$"{ServerCredentials.Section}:AnthropicApiKey"]          = OwnerAiKey,
        });

        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "App_Data", "users.db")));
        builder.Services.AddSingleton<ListingDatabase>();

        PerUserCredentials.AddCredentials(builder, Path.Combine(root, "credentials.json"), hosted: true);
        HostedAuth.AddAccounts(builder, hosted: true, secureCookie: false);

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: true);
        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);

        // The two the Settings screen posts to and reads back, copied from Program.cs.
        app.MapPost("/api/setup/save", (CredentialsPatch body, CredentialsStore store) => SaveAndReport(body, store));
        app.MapGet("/api/setup/fields", (CredentialsStore store) => Results.Ok(store.GetPublicFields()));

        // No such endpoint exists in the app, and none should: the secrets go to eBay and to
        // Anthropic, never to a browser. It is here because the public fields only say WHETHER a
        // token is stored, and what this test has to prove is whose.
        app.MapGet("/api/test/secrets", (CredentialsStore store) => Results.Ok(new
        {
            ebayUserToken    = store.GetUserToken(),
            ebayRefreshToken = store.GetRefreshToken(),
            anthropicApiKey  = store.Get().AnthropicApiKey,
        }));

        await app.StartAsync();
        return new CredentialServer(app, root);
    }

    private static IResult SaveAndReport(CredentialsPatch body, CredentialsStore store)
    {
        store.Save(body);
        return Results.Ok(store.GetStatus());
    }

    private sealed record FieldsResponse(
        string EbayClientId, string EbayFulfillmentPolicyId, bool HasEbayRefreshToken, bool HasAnthropicKey);

    private sealed record SecretsResponse(string EbayUserToken, string EbayRefreshToken, string AnthropicApiKey);

    private sealed record MeResponse(bool SignedIn, string Email, long UserId);

    /// <summary>One signed-in seller, with their own cookie jar.</summary>
    private sealed class Seller(HttpClient client, long userId) : IDisposable
    {
        public long UserId { get; } = userId;

        public async Task SaveAsync(object patch)
        {
            var response = await client.PostAsJsonAsync("/api/setup/save", patch);
            response.EnsureSuccessStatusCode();
        }

        public async Task<FieldsResponse> FieldsAsync() =>
            (await client.GetFromJsonAsync<FieldsResponse>("/api/setup/fields"))!;

        public async Task<SecretsResponse> SecretsAsync() =>
            (await client.GetFromJsonAsync<SecretsResponse>("/api/test/secrets"))!;

        public void Dispose() => client.Dispose();
    }

    private sealed class CredentialServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        /// <summary>What is actually stored for this user, ciphertext and all.</summary>
        public string? StoredTextFor(long userId) =>
            app.Services.GetRequiredService<UserCredentialsStore>().ReadStoredText(userId);

        /// <summary>
        /// The whole database file, as bytes read back as text — pages, freelist and all. Opened
        /// sharing, because SQLite's connection pool still has the file open.
        /// </summary>
        public string DatabaseBytesAsText()
        {
            using var file = new FileStream(app.Services.GetRequiredService<ListingDatabase>().DatabasePath,
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var bytes = new MemoryStream();
            file.CopyTo(bytes);
            return Encoding.Latin1.GetString(bytes.ToArray());
        }

        public HttpClient Anonymous() => NewClient();

        /// <remarks>
        /// Two calls, because signing up no longer signs you in — the endpoint answers a new
        /// address and an already-registered one identically so that it cannot be used to find
        /// out which addresses exist, and a session cookie on one branch would give that away.
        /// These tests want a signed-in seller, so they sign in.
        /// </remarks>
        public async Task<Seller> SignUpAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();

            var signIn = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password });
            signIn.EnsureSuccessStatusCode();
            return new Seller(client, await UserIdAsync(client));
        }

        public async Task<Seller> SignInAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();
            return new Seller(client, await UserIdAsync(client));
        }

        private static async Task<long> UserIdAsync(HttpClient client) =>
            (await client.GetFromJsonAsync<MeResponse>(HostedAuth.MeApi))!.UserId;

        /// <summary>A separate cookie jar per client — which is what makes them different people.</summary>
        private HttpClient NewClient()
        {
            // The jar is held onto so CsrfClientHandler can read the antiforgery cookie out of it
            // and echo it back, which is the one browser behaviour a bare HttpClient lacks and the
            // server now insists on. See CsrfClientHandler.
            var jar = new CookieContainer();
            var client = new HttpClient(new CsrfClientHandler(jar, new HttpClientHandler
            {
                UseCookies        = true,
                CookieContainer   = jar,
                AllowAutoRedirect = false,
            }))
            {
                BaseAddress = new Uri(app.Urls.First()),
            };
            _clients.Add(client);
            return client;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }

    /// <summary>A throwaway SQLite file for the tests that do not need a whole server.</summary>
    private sealed class ScratchDatabase : IDisposable
    {
        public string Folder { get; }
        public string Path   { get; }

        public ScratchDatabase()
        {
            Folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ing-per-user-credentials", Guid.NewGuid().ToString("N"));
            Path   = System.IO.Path.Combine(Folder, "credentials-test.db");
            Directory.CreateDirectory(Folder);
        }

        public void Dispose()
        {
            try { Directory.Delete(Folder, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
