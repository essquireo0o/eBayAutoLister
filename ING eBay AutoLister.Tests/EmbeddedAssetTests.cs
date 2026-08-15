using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// That the files the pages ask for are actually inside the shipped assembly, and that the browser
/// can get them without being signed in.
/// </summary>
/// <remarks>
/// <para>
/// The UI is served from an <see cref="EmbeddedFileProvider"/> and from nothing else — there is no
/// wwwroot on disk in the container. So a file is shipped if and only if the project file names it
/// as an <c>EmbeddedResource</c>, and a page that references one that is not named there does not
/// get a 404: the request falls past static files to the sign-in gate and comes back as a 302 to
/// signin.html. The browser then has the login page's HTML with a JavaScript content type, refuses
/// to run it, and reports one line in a console nobody is reading.
/// </para>
/// <para>
/// This happened. csrf.js was written, referenced by all five pages, tested on its own merits, and
/// left out of the project file — so the hosted deployment ran for a day with no antiforgery client
/// at all. window.fetch was never wrapped, no unsafe request ever carried
/// <see cref="Csrf.HeaderName"/>, and every one of them was refused: "This request could not be
/// verified as coming from the app", including the sign-in that locked the owner out of the app.
/// Every existing test passed throughout. They read the asset off disk in the repository, where it
/// had been correct all along, and the one thing none of them asked was whether it shipped.
/// </para>
/// <para>
/// So these read out of the assembly, and through a real server, rather than off disk.
/// </para>
/// </remarks>
public class EmbeddedAssetTests
{
    /// <summary>Where <c>Program.cs</c> points the embedded provider. Same string, deliberately.</summary>
    private const string ResourceNamespace = "ING_eBay_AutoLister.wwwroot";

    private static readonly Assembly App = typeof(Csrf).Assembly;

    /// <summary>The pages the app serves. Each is embedded, and each pulls in assets of its own.</summary>
    private static readonly string[] Pages =
        ["index.html", "editor.html", "reauth.html", "signin.html", "signup.html"];

    /// <summary>
    /// <c>src="app.js?v=145"</c> and <c>href="style.css?v=130"</c> — the name, without the stamp,
    /// and only for the relative ones. An absolute URL is somebody else's server to serve.
    /// </summary>
    private static readonly Regex LocalAsset =
        new("(?:src|href)\\s*=\\s*\"(?!https?:|//|data:|#)([^\"?]+)", RegexOptions.Compiled);

    [Fact]
    public void Every_asset_a_page_asks_for_is_in_the_assembly()
    {
        var embedded = App.GetManifestResourceNames().ToHashSet(StringComparer.Ordinal);
        var missing  = new List<string>();

        foreach (var page in Pages)
            foreach (Match match in LocalAsset.Matches(ReadEmbedded(page)))
            {
                var asset = match.Groups[1].Value.TrimStart('/');

                // Only files that would come out of wwwroot. A link to another page's route, or to
                // an api path, is not an asset and has nothing to be embedded.
                if (!asset.Contains('.') || asset.Contains('/')) continue;

                if (!embedded.Contains($"{ResourceNamespace}.{asset}"))
                    missing.Add($"{page} references {asset}");
            }

        Assert.True(missing.Count == 0,
            "These files are referenced by a page but are not embedded in the assembly, so the " +
            "server has no copy to serve and the browser will be handed the sign-in page instead. " +
            "Add an <EmbeddedResource Include=\"wwwroot\\…\" /> for each in the project file:" +
            Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public async Task The_sign_in_page_carries_the_token_on_submit()
    {
        await using var server = await StartAsync();
        var client = server.NewClient();

        // Exactly what a browser does, in order, with nobody signed in: fetch the page, then fetch
        // every script it names.
        var page = await client.GetAsync(HostedAuth.SignInPath);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var scripts = Regex.Matches(await page.Content.ReadAsStringAsync(), """<script[^>]+src="([^"?]+)""")
                           .Select(m => m.Groups[1].Value)
                           .ToArray();
        Assert.Contains("csrf.js", scripts);

        foreach (var script in scripts)
        {
            var response = await client.GetAsync("/" + script);

            // A 302 here is the whole bug: it means the file is not embedded, so the request went
            // past static files to the sign-in gate, and the browser is about to run a login page.
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"GET /{script} answered {(int)response.StatusCode} " +
                $"{(response.Headers.Location is null ? "" : "-> " + response.Headers.Location)}. " +
                "The sign-in page cannot load its own scripts, so its form will post without a " +
                "token and be refused. Almost always a missing <EmbeddedResource> in the csproj.");

            Assert.Contains("javascript", response.Content.Headers.ContentType?.MediaType ?? "");
        }

        // And the script it got is one that puts the token on the POST the form is about to make.
        var csrf = await client.GetStringAsync("/csrf.js");
        Assert.Contains("window.fetch =", csrf, StringComparison.Ordinal);
        Assert.Contains(Csrf.HeaderName, csrf, StringComparison.Ordinal);
        Assert.Contains(Csrf.CookieName, csrf, StringComparison.Ordinal);
        Assert.Contains(Csrf.TokenPath, csrf, StringComparison.Ordinal);
    }

    [Fact]
    public void The_token_script_is_loaded_before_anything_that_fetches()
    {
        // app.js takes its own reference to window.fetch at parse time — see the header of csrf.js
        // — so a wrapper installed after it has run is a wrapper those calls never reach. The tag
        // has to come first in the document, not merely be present in it.
        foreach (var page in Pages)
        {
            var html  = ReadEmbedded(page);
            var token = html.IndexOf("csrf.js", StringComparison.Ordinal);

            Assert.True(token >= 0, $"{page} does not load csrf.js, so nothing it posts will be accepted.");

            var others = Regex.Matches(html, """<script[^>]+src="([^"?]+)""")
                              .Where(m => !m.Groups[1].Value.Contains("csrf.js", StringComparison.Ordinal));

            foreach (var other in others)
                Assert.True(token < other.Index,
                    $"{page} loads {other.Groups[1].Value} before csrf.js. Whatever copy of " +
                    "window.fetch that file takes will be the unwrapped one.");
        }
    }

    [Fact]
    public async Task A_page_can_be_reached_without_signing_in_and_so_can_its_scripts()
    {
        // The sign-up page has the same problem as the sign-in page and is reached by people who
        // have no session at all, which is the case where a gate is most likely to be in the way.
        await using var server = await StartAsync();
        var client = server.NewClient();

        foreach (var path in new[] { HostedAuth.SignUpPath, "/csrf.js", "/style.css", "/favicon.svg" })
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(path)).StatusCode);
    }

    private static string ReadEmbedded(string name)
    {
        using var stream = App.GetManifestResourceStream($"{ResourceNamespace}.{name}");
        Assert.True(stream is not null,
            $"wwwroot\\{name} is not embedded in the assembly. Add an <EmbeddedResource Include> " +
            "for it in \"ING eBay AutoLister.csproj\" — without one it is not shipped at all.");

        return new StreamReader(stream!).ReadToEnd();
    }

    // ── Fixture ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The hosted pipeline in the order <c>Program.cs</c> builds it, with the embedded static files
    /// in the middle. The order is the point: the gate reads the cookie, static files answer, and
    /// the fallback authorization policy only ever sees what static files did not.
    /// </summary>
    private static async Task<AssetServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-asset-tests", Guid.NewGuid().ToString("N"));
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
        HostedAuth.UseSignedInUser(app, hosted: true, secureCookie: false);

        var embedded = new EmbeddedFileProvider(App, ResourceNamespace);
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded, DefaultFileNames = ["index.html"] });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });

        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);

        await app.StartAsync();
        return new AssetServer(app, root);
    }

    private sealed class AssetServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        /// <summary>Signed in to nothing, and not following redirects — a 302 has to be visible.</summary>
        public HttpClient NewClient()
        {
            var client = new HttpClient(new HttpClientHandler
            {
                UseCookies        = true,
                CookieContainer   = new CookieContainer(),
                AllowAutoRedirect = false,
            })
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
}
