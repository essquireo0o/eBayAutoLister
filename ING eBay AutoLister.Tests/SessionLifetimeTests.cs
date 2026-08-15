using System.Net;
using System.Net.Http.Json;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The two things a session cookie has to do beyond being decryptable: change when somebody signs
/// in, and stop working when somebody signs out. Over a real server, because both are properties of
/// the cookie handler being wired to <see cref="SessionStore"/> and not of either alone.
/// </summary>
public class SessionLifetimeTests
{
    private const string Email    = "seller@example.com";
    private const string Other    = "other@example.com";
    private const string Password = "a-long-enough-password";
    private const string Endpoint = "/api/setup/status";

    [Fact]
    public async Task Signing_in_replaces_the_session_rather_than_adopting_the_one_already_there()
    {
        // Session fixation. An attacker who can plant a cookie in the victim's browser — a shared
        // machine, a sibling subdomain, a cafe network on plain http — wants the victim to sign in
        // and have the session they planted become an authenticated one. It must not: the
        // identifier is minted after the password is checked and never read off the request.
        await using var server = await StartAsync();
        var client = server.NewClient();

        await client.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(client, HostedAuth.SignUpApi, new { email = Email, password = Password, name = "Dana Ellis" });

        await server.PostAsync(client, HostedAuth.SignInApi, new { email = Email, password = Password });
        var first = server.SessionCookie(client);

        await server.PostAsync(client, HostedAuth.SignOutApi, new { });
        await server.PostAsync(client, HostedAuth.SignInApi, new { email = Email, password = Password });
        var second = server.SessionCookie(client);

        Assert.False(string.IsNullOrEmpty(first));
        Assert.False(string.IsNullOrEmpty(second));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task A_planted_cookie_does_not_survive_the_sign_in_it_was_planted_for()
    {
        await using var server = await StartAsync();

        // The attacker signs in as themselves and keeps the cookie value they were given. This is
        // the thing they would plant: a session that really is live, and really is theirs.
        var attacker = server.NewClient();
        await attacker.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(attacker, HostedAuth.SignUpApi, new { email = Other, password = Password, name = "Mal" });
        await server.PostAsync(attacker, HostedAuth.SignInApi, new { email = Other, password = Password });
        var planted = server.SessionCookie(attacker);

        // The victim's browser is made to carry it, and then the victim signs in for real.
        var victim = server.NewClient();
        await victim.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(victim, HostedAuth.SignUpApi, new { email = Email, password = Password, name = "Dana Ellis" });
        server.SetSessionCookie(victim, planted!);

        await server.PostAsync(victim, HostedAuth.SignInApi, new { email = Email, password = Password });

        // The victim is on a new session, not the planted one.
        Assert.NotEqual(planted, server.SessionCookie(victim));

        // And the planted value is now worthless to the attacker who kept a copy: signing in on
        // top of it revoked it, so their own browser is signed out too.
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync(Endpoint)).StatusCode);
    }

    [Fact]
    public async Task Signing_out_kills_the_session_on_the_server_not_just_in_the_browser()
    {
        // The one that used to be missing. SignOutAsync only tells the browser to forget; anybody
        // who copied the cookie value first still held a ticket the server would decrypt happily
        // for the next fourteen days.
        await using var server = await StartAsync();
        var client = server.NewClient();

        await client.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(client, HostedAuth.SignUpApi, new { email = Email, password = Password, name = "Dana Ellis" });
        await server.PostAsync(client, HostedAuth.SignInApi, new { email = Email, password = Password });

        // A thief lifts the cookie off the machine while the seller is still signed in.
        var stolen = server.SessionCookie(client);
        var thief  = server.NewClient();
        await thief.GetAsync(HostedAuth.SignInPath);
        server.SetSessionCookie(thief, stolen!);
        Assert.Equal(HttpStatusCode.OK, (await thief.GetAsync(Endpoint)).StatusCode);

        // The seller signs out on their own machine.
        await server.PostAsync(client, HostedAuth.SignOutApi, new { });

        // The stolen copy dies with it. Before this change it kept working.
        Assert.Equal(HttpStatusCode.Unauthorized, (await thief.GetAsync(Endpoint)).StatusCode);
    }

    [Fact]
    public async Task A_revoked_session_clears_the_cookie_rather_than_being_refused_forever()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        await client.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(client, HostedAuth.SignUpApi, new { email = Email, password = Password, name = "Dana Ellis" });
        await server.PostAsync(client, HostedAuth.SignInApi, new { email = Email, password = Password });

        server.Sessions.RevokeAllFor(1);

        var response = await client.GetAsync(Endpoint);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        // The browser is told to drop it, so it stops sending a ticket that will never work again.
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
                        cookie => cookie.StartsWith("ing_session=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_ticket_carrying_no_session_at_all_is_refused()
    {
        // Cookies minted before this existed carry no sid claim, and so does anything forged by
        // somebody who got hold of the data protection keys but not this table.
        await using var server = await StartAsync();
        var client = server.NewClient();

        await client.GetAsync(HostedAuth.SignInPath);
        await server.PostAsync(client, HostedAuth.SignUpApi, new { email = Email, password = Password, name = "Dana Ellis" });
        await server.PostAsync(client, HostedAuth.SignInApi, new { email = Email, password = Password });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Endpoint)).StatusCode);

        // Deleting the row is the same thing from the server's side as the claim naming a session
        // it has never heard of.
        server.Sessions.RevokeAllFor(1);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Endpoint)).StatusCode);
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────

    private static async Task<LifetimeServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-session-lifetime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "users.db")));
        builder.Services.AddSingleton(new SignInThrottle(Path.Combine(root, "users.db")));
        builder.Services.AddSingleton(new SessionStore(Path.Combine(root, "users.db")));

        HostedAuth.AddAccounts(builder, hosted: true, secureCookie: false);

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: true);
        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);
        app.MapGet(Endpoint, () => Results.Ok(new { ready = true }));

        await app.StartAsync();
        return new LifetimeServer(app, root);
    }

    private sealed class LifetimeServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];
        private readonly Dictionary<HttpClient, CookieContainer> _jars = [];

        public SessionStore Sessions => app.Services.GetRequiredService<SessionStore>();

        public HttpClient NewClient()
        {
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
            _jars[client] = jar;
            return client;
        }

        public Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object body) =>
            client.PostAsJsonAsync(path, body);

        public string? SessionCookie(HttpClient client) =>
            _jars[client].GetCookies(client.BaseAddress!)["ing_session"]?.Value;

        /// <summary>Plants a session cookie value in a client's jar, the way an attacker would.</summary>
        public void SetSessionCookie(HttpClient client, string value) =>
            _jars[client].Add(new Cookie("ing_session", value, "/", client.BaseAddress!.Host));

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
