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
/// Proves that a page on another site cannot make this one act, even holding a browser that is
/// signed in — and that the app's own pages still can.
/// </summary>
/// <remarks>
/// The clients here are built by hand rather than through the harness used elsewhere, because
/// <see cref="CsrfClientHandler"/> exists to supply the token automatically and every test below is
/// about what happens when it is absent, stale or wrong. A real Kestrel again: the check is
/// middleware and its position in the pipeline is half of what makes it work.
/// </remarks>
public class CsrfTests
{
    private const string Email    = "seller@example.com";
    private const string Password = "a-long-enough-password";

    /// <summary>A state-changing endpoint that has nothing to do with auth. Stands in for the other 96.</summary>
    private const string StateChangingEndpoint = "/api/earnings/log";

    [Fact]
    public async Task A_signed_in_session_cannot_be_used_by_a_request_that_has_no_token()
    {
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        // The exact shape of the attack: the browser is signed in, the cookie goes along, and the
        // request still fails — because the one thing the attacker's page cannot do is read the
        // token out of a cookie belonging to another origin.
        var forged = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };
        var response = await client.SendAsync(forged);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("token", response.Headers.GetValues(Csrf.RequiredHeader).Single());
    }

    [Fact]
    public async Task The_same_request_with_the_token_is_allowed()
    {
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        var allowed = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };
        allowed.Headers.Add(Csrf.HeaderName, server.TokenFor(client));

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(allowed)).StatusCode);
    }

    [Fact]
    public async Task A_token_that_is_not_the_one_in_the_cookie_is_refused()
    {
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        // Guessing is the only other option once the cookie cannot be read, and the token is 256
        // bits. This is that guess.
        var forged = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };
        forged.Headers.Add(Csrf.HeaderName, Csrf.Issue());

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forged)).StatusCode);
    }

    [Fact]
    public async Task A_request_that_names_another_origin_is_refused_even_with_a_good_token()
    {
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        // Origin is set by the browser and not by the page, so this is the half of the check an
        // attacker cannot talk their way past. It is checked in front of the token, so a token
        // that has somehow leaked is still not enough.
        var crossSite = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };
        crossSite.Headers.Add("Origin", "https://evil.example");
        crossSite.Headers.Add(Csrf.HeaderName, server.TokenFor(client));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(crossSite)).StatusCode);
    }

    [Theory]
    [InlineData("https://app.inglisting.com.evil.test")]  // ours as a prefix
    [InlineData("https://evil.test/?x=http://127.0.0.1")] // ours in the query
    [InlineData("null")]                                  // a sandboxed iframe or a file:// page
    public async Task An_origin_that_only_looks_like_ours_is_refused(string origin)
    {
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        var request = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };
        request.Headers.Add("Origin", origin);
        request.Headers.Add(Csrf.HeaderName, server.TokenFor(client));

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Signing_in_is_itself_protected()
    {
        // Login CSRF: an attacker's page silently signs the victim's browser into an account the
        // attacker controls, and everything the victim then does — every listing, every eBay
        // connection — happens in a session the attacker can also open.
        await using var server = await StartAsync();
        var client = server.NewClient();

        var request = new HttpRequestMessage(HttpMethod.Post, HostedAuth.SignInApi)
        {
            Content = JsonContent.Create(new { email = Email, password = Password }),
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task Reading_is_never_blocked_by_it()
    {
        // A GET changes nothing, and making the seller's dashboard depend on a token would mean a
        // stale one shows them an empty app rather than an error they can act on.
        await using var server = await StartAsync();
        var client = await server.SignedInClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/earnings/summary")).StatusCode);
    }

    [Fact]
    public async Task A_page_that_has_never_been_here_is_issued_a_token_on_the_way_in()
    {
        // The token has to arrive without anybody asking, or the sign-in page is a form that
        // cannot be submitted until something else has happened first. Any safe request does it —
        // the health check here, because this fixture mounts no static files; in the real app it
        // is the GET of signin.html itself.
        await using var server = await StartAsync();
        var client = server.NewClient();

        var response = await client.GetAsync(HostedAuth.HealthPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
                        cookie => cookie.StartsWith(Csrf.CookieName + "=", StringComparison.Ordinal));

        // And the endpoint a page with no token goes to hands back the same value it just set.
        var token = await client.GetFromJsonAsync<TokenResponse>(Csrf.TokenPath);
        Assert.Equal(server.TokenFor(client), token!.Token);
        Assert.NotEmpty(token.Token);
    }

    [Fact]
    public async Task The_desktop_build_asks_for_no_token_at_all()
    {
        // One seller on a loopback port. There is no other origin to defend against, and a token
        // requirement would break every existing desktop caller for nothing.
        await using var server = await StartAsync(hosted: false);
        var client = server.NewClient();

        var request = new HttpRequestMessage(HttpMethod.Post, StateChangingEndpoint)
        {
            Content = JsonContent.Create(new { title = "Antminer S19", salePrice = 1000m }),
        };

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
    }

    /// <summary>What <see cref="Csrf.TokenPath"/> answers.</summary>
    private sealed record TokenResponse(string Token);

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────

    private static async Task<CsrfServer> StartAsync(bool hosted = true)
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-csrf-tests", Guid.NewGuid().ToString("N"));
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

        // Loopback http, so a Secure cookie would never come back. Same reason as HostedAuthTests.
        HostedAuth.AddAccounts(builder, hosted, secureCookie: false);

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted);
        HostedAuth.RequireSignIn(app, hosted);
        HostedAuth.MapAccountEndpoints(app, hosted);

        // Stand-ins for the app's 92 MapPost and 5 MapDelete endpoints, and one GET beside them.
        app.MapPost(StateChangingEndpoint, () => Results.Ok(new { logged = true }));
        app.MapGet("/api/earnings/summary", () => Results.Ok(new { total = 0 }));

        await app.StartAsync();
        return new CsrfServer(app, root, hosted);
    }

    private sealed class CsrfServer(WebApplication app, string root, bool hosted) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];
        private readonly Dictionary<HttpClient, CookieContainer> _jars = [];

        /// <summary>
        /// A client with a cookie jar and NO automatic token handling. That absence is the point:
        /// every test here supplies the token by hand, or deliberately does not.
        /// </summary>
        public HttpClient NewClient()
        {
            var jar = new CookieContainer();
            var client = new HttpClient(new HttpClientHandler
            {
                UseCookies        = true,
                CookieContainer   = jar,
                AllowAutoRedirect = false,
            })
            {
                BaseAddress = new Uri(app.Urls.First()),
            };

            _clients.Add(client);
            _jars[client] = jar;
            return client;
        }

        /// <summary>The token the server has issued this client, which its own page would read.</summary>
        public string TokenFor(HttpClient client) =>
            _jars[client].GetCookies(client.BaseAddress!)[Csrf.CookieName]?.Value ?? string.Empty;

        /// <summary>
        /// A client that is signed in — which takes a token, because signing in is itself a POST.
        /// Done the long way round rather than through a helper so that what these tests are about
        /// is not quietly performed for them.
        /// </summary>
        public async Task<HttpClient> SignedInClientAsync()
        {
            var client = NewClient();

            // A GET first, which is what issues the token.
            await client.GetAsync(HostedAuth.SignInPath);

            await PostWithTokenAsync(client, HostedAuth.SignUpApi,
                new { email = Email, password = Password, name = "Dana Ellis" });
            var signIn = await PostWithTokenAsync(client, HostedAuth.SignInApi,
                new { email = Email, password = Password });

            if (hosted) signIn.EnsureSuccessStatusCode();
            return client;
        }

        private async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string path, object body)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            var token   = TokenFor(client);
            if (!string.IsNullOrEmpty(token)) request.Headers.Add(Csrf.HeaderName, token);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var client in _clients) client.Dispose();
            await app.StopAsync();
            await app.DisposeAsync();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
