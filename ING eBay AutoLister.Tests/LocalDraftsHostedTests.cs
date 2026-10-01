using System.Net;
using System.Net.Http.Json;
using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// Saved drafts on the hosted build: they answer at all, and each seller only ever gets their own.
/// </summary>
/// <remarks>
/// <para>
/// <c>GET /api/local-drafts/list</c> answered 500 on app.inglisting.com from the day it went up.
/// <see cref="DraftStore"/> kept its files in <c>Desktop\eBayListing</c>; the container has no
/// Desktop, .NET answers an empty string for a special folder that does not exist, and the folder
/// became the relative path <c>eBayListing</c> — <c>/app/eBayListing</c>, which the account the
/// image runs as may not create (<c>UnauthorizedAccessException</c>, read out of the container's
/// own log). And the store knew nothing about users, so a writable folder would have been one
/// shared folder: every seller's unpublished drafts, photos included, listed to every other seller.
/// </para>
/// <para>
/// The store tests drive ONE store while changing who is signed in, because that is what the app
/// has — a singleton store and a user resolved per request. The HTTP tests put the app's own
/// <see cref="DraftEndpoints"/> behind the app's own hosted sign-in, so the handlers under test are
/// the ones Program.cs maps and not a copy of them.
/// </para>
/// <para>
/// What a Windows test run cannot show is the missing Desktop itself; <c>deploy-local-drafts-check.sh</c>
/// asks the same questions of the real image in a throwaway container.
/// </para>
/// </remarks>
[Collection(PooledSqliteTests.Name)]
public class LocalDraftsHostedTests : IDisposable
{
    private const long UserA = 1;
    private const long UserB = 2;

    private const string Password = "a-long-enough-password";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ing-local-drafts", Guid.NewGuid().ToString("N"));

    /// <summary>Who is signed in on the call in flight. Moved between assertions, never rebuilt.</summary>
    private long? _signedIn = UserA;

    private DraftStore HostedStore() => new(UserScope.PerUser(() => _signedIn), _root);

    // ── The store ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_hosted_sellers_drafts_live_in_their_own_folder_under_the_data_root()
    {
        var store = HostedStore();

        _signedIn = UserA;
        var folder = store.EnsureFolder();
        var filename = store.SaveDraft(Draft("Antminer S19 95TH"));

        // An absolute path under the root the store was given. The 500 was a relative one: it meant
        // "wherever the process happened to start", and on the server that is a read-only folder.
        Assert.True(Path.IsPathRooted(folder), folder);
        Assert.Equal(Path.Combine(_root, "1"), folder);
        Assert.True(File.Exists(Path.Combine(_root, "1", filename)));
    }

    [Fact]
    public void One_sellers_drafts_are_invisible_to_another_through_every_method()
    {
        var store = HostedStore();

        _signedIn = UserA;
        var mine = store.SaveDraft(Draft("Antminer S19 95TH"));

        _signedIn = UserB;
        Assert.Empty(store.ListDrafts());
        Assert.Null(store.LoadDraft(mine));
        store.DeleteDraft(mine);

        // Their own draft is a second file, even under the very same name.
        store.SaveDraft(Draft("Their own S19", filename: mine));
        Assert.Equal("Their own S19", Assert.Single(store.ListDrafts()).Title);

        // And none of that reaching over the fence touched the draft it could not see.
        _signedIn = UserA;
        Assert.Equal("Antminer S19 95TH", Assert.Single(store.ListDrafts()).Title);
        Assert.Equal("Antminer S19 95TH", store.LoadDraft(mine)!.Title);
    }

    [Theory]
    [InlineData("../1/{0}")]
    [InlineData("..\\1\\{0}")]
    [InlineData("/1/{0}")]
    public void A_filename_cannot_climb_into_another_sellers_folder(string shape)
    {
        var store = HostedStore();

        _signedIn = UserA;
        var mine = store.SaveDraft(Draft("Antminer S19 95TH"));
        var reach = string.Format(shape, mine);

        _signedIn = UserB;
        Assert.Null(store.LoadDraft(reach));
        store.DeleteDraft(reach);
        store.SaveDraft(Draft("Overwrite attempt", filename: reach));

        _signedIn = UserA;
        Assert.Equal("Antminer S19 95TH", store.LoadDraft(mine)!.Title);
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "1")));
    }

    [Fact]
    public void With_nobody_signed_in_it_reads_nothing_and_refuses_to_save()
    {
        var store = HostedStore();

        _signedIn = UserA;
        var mine = store.SaveDraft(Draft("Antminer S19 95TH"));

        // Background work: no HttpContext, so no user. It must not be handed somebody's drafts, and
        // it must not be told a draft was saved when there was nowhere to put it.
        _signedIn = null;
        Assert.Empty(store.ListDrafts());
        Assert.Null(store.LoadDraft(mine));
        store.DeleteDraft(mine);
        Assert.Equal("", store.EnsureFolder());
        Assert.Throws<InvalidOperationException>(() => store.SaveDraft(Draft("Nobody's")));

        // One folder on disk, the first seller's, with their draft still in it.
        Assert.Equal([Path.Combine(_root, "1")], Directory.GetDirectories(_root));
        Assert.Single(Directory.GetFiles(Path.Combine(_root, "1")));
    }

    [Fact]
    public void The_list_names_each_draft_and_says_when_it_was_saved()
    {
        var store = HostedStore();
        store.SaveDraft(Draft("Antminer S19 95TH"));
        store.SaveDraft(Draft("Whatsminer M30S"));
        // A file put in the folder by hand, in the casing the browser would write.
        File.WriteAllText(Path.Combine(_root, "1", "by_hand.json"),
            """{ "title": "Saved by hand", "savedAt": "2026-01-01T00:00:00.0000000+00:00" }""");

        var list = store.ListDrafts();

        // Found while writing these tests: the list read "title" out of files the store itself
        // writes as "Title", so every draft was "Untitled" with no date and the sort did nothing.
        Assert.Equal(["Antminer S19 95TH", "Saved by hand", "Whatsminer M30S"], list.Select(d => d.Title).Order());
        Assert.All(list, d => Assert.NotEqual("", d.SavedAt));
        Assert.Equal(list.OrderByDescending(d => d.SavedAt), list);
        Assert.Equal("Saved by hand", list[^1].Title);
    }

    [Fact]
    public void The_desktop_build_still_keeps_one_sellers_drafts_in_one_place()
    {
        // Not asked for its folder here: that would create Desktop\eBayListing on whoever runs the
        // tests. That it is not per-user is the whole of what the desktop build promises.
        Assert.False(new DraftStore().IsPerUser);
        Assert.False(new DraftStore(UserScope.Desktop, _root).IsPerUser);
        Assert.True(HostedStore().IsPerUser);
    }

    // ── End to end, through two browsers ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_drafts_list_answers_a_signed_in_seller_instead_of_500()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");

        var response = await seller.Client.GetAsync("/api/local-drafts/list");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("[]", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Two_signed_in_sellers_have_two_different_sets_of_drafts()
    {
        await using var server = await StartAsync();
        var first  = await server.SignUpAsync("first@example.com");
        var second = await server.SignUpAsync("second@example.com");

        var mine = await first.SaveAsync("Antminer S19 95TH");

        Assert.Equal("Antminer S19 95TH", Assert.Single(await first.ListAsync()).Title);
        Assert.Equal("Antminer S19 95TH", (await first.LoadAsync(mine))!.Title);

        // The filename is guessable — it is the title and the second it was saved.
        Assert.Empty(await second.ListAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await second.Client.GetAsync("/api/local-drafts/load/" + mine)).StatusCode);
        await second.Client.DeleteAsync("/api/local-drafts/delete/" + mine);

        Assert.Equal("Antminer S19 95TH", Assert.Single(await first.ListAsync()).Title);

        // "Clear all drafts" in one seller's browser is exactly this loop. It empties their list.
        await first.Client.DeleteAsync("/api/local-drafts/delete/" + mine);
        Assert.Empty(await first.ListAsync());
    }

    [Fact]
    public async Task A_seller_signing_back_in_still_has_their_drafts()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.SaveAsync("Antminer S19 95TH");

        // Per-user is not per-session.
        var returning = await server.SignInAsync("seller@example.com");

        Assert.Equal("Antminer S19 95TH", Assert.Single(await returning.ListAsync()).Title);
    }

    [Fact]
    public async Task A_hosted_seller_is_not_told_a_path_on_the_servers_disk()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");

        var answer = await seller.Client.GetFromJsonAsync<FolderResponse>("/api/local-drafts/ensure-folder");

        Assert.Equal("", answer!.Path);
    }

    [Fact]
    public async Task Without_a_session_the_drafts_are_closed()
    {
        await using var server = await StartAsync();
        var seller = await server.SignUpAsync("seller@example.com");
        await seller.SaveAsync("Antminer S19 95TH");

        var stranger = server.NewClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await stranger.GetAsync("/api/local-drafts/list")).StatusCode);
    }

    // ── The app is the thing standing behind it ──────────────────────────────────────────────

    [Fact]
    public void Program_maps_the_draft_endpoints_from_the_one_place_these_tests_exercise()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Program.cs"));

        Assert.Contains("DraftEndpoints.Map(app)", program, StringComparison.Ordinal);
        // A second copy mapped inline would be the one that runs, and the one nothing here covers.
        Assert.DoesNotContain("(\"/api/local-drafts/", program, StringComparison.Ordinal);
        // The scope the store asks for has to be registered before the store can be built with it.
        Assert.InRange(program.IndexOf("PerUserData.AddUserScope(builder)", StringComparison.Ordinal),
                       0, program.IndexOf("AddSingleton<DraftStore>()", StringComparison.Ordinal));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────────────────────

    private static DraftFile Draft(string title, string? filename = null) => new()
    {
        Title = title,
        Filename = filename,
        Data = new PostListingRequest(),
    };

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder, not the point */ }
        GC.SuppressFinalize(this);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }

    // ── The server under test ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The app's own hosted wiring — accounts, the per-request user, the sign-in gate — around the
    /// app's own draft endpoints, with the drafts root moved to a throwaway folder.
    /// </summary>
    private static async Task<DraftServer> StartAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ing-local-drafts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = root,
            EnvironmentName = "Production",
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(new UserStore(Path.Combine(root, "App_Data", "users.db")));

        PerUserData.AddUserScope(builder, hosted: true);
        HostedAuth.AddAccounts(builder, hosted: true, secureCookie: false);

        // Not for the drafts: the sign-in throttle keeps its failure counts in the app database.
        builder.Services.AddSingleton<ListingDatabase>();
        builder.Services.AddSingleton<ActionLog>();
        builder.Services.AddSingleton(sp =>
            new DraftStore(sp.GetRequiredService<UserScope>(), Path.Combine(root, "App_Data", "drafts")));

        var app = builder.Build();
        HostedAuth.UseSignedInUser(app, hosted: true);
        HostedAuth.RequireSignIn(app, hosted: true);
        HostedAuth.MapAccountEndpoints(app, hosted: true);

        DraftEndpoints.Map(app);

        await app.StartAsync();
        return new DraftServer(app, root);
    }

    private sealed record SavedResponse(string Filename);

    private sealed record FolderResponse(string Path);

    /// <summary>One signed-in seller, with their own cookie jar.</summary>
    private sealed class Seller(HttpClient client)
    {
        public HttpClient Client => client;

        public async Task<string> SaveAsync(string title)
        {
            var response = await client.PostAsJsonAsync("/api/local-drafts/save", new { title, data = new { } });
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<SavedResponse>())!.Filename;
        }

        public async Task<List<DraftSummary>> ListAsync() =>
            (await client.GetFromJsonAsync<List<DraftSummary>>("/api/local-drafts/list"))!;

        public async Task<DraftFile?> LoadAsync(string filename) =>
            await client.GetFromJsonAsync<DraftFile>("/api/local-drafts/load/" + filename);
    }

    private sealed class DraftServer(WebApplication app, string root) : IAsyncDisposable
    {
        private readonly List<HttpClient> _clients = [];

        /// <remarks>
        /// Two calls, because signing up does not sign you in — see the same helper in
        /// <see cref="PerUserDataTests"/> for why.
        /// </remarks>
        public async Task<Seller> SignUpAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignUpApi, new { email, password = Password, name = "Dana Ellis" });
            response.EnsureSuccessStatusCode();

            var signIn = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password });
            signIn.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        public async Task<Seller> SignInAsync(string email)
        {
            var client   = NewClient();
            var response = await client.PostAsJsonAsync(HostedAuth.SignInApi, new { email, password = Password });
            response.EnsureSuccessStatusCode();
            return new Seller(client);
        }

        /// <summary>A separate cookie jar per client — which is what makes them different people.</summary>
        public HttpClient NewClient()
        {
            // The jar is shared with CsrfClientHandler so it can echo the antiforgery cookie back,
            // the one browser behaviour a bare HttpClient lacks and the server insists on.
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
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, recursive: true); } catch { /* a temp folder, not the point */ }
        }
    }
}
