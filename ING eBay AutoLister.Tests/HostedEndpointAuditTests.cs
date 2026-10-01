using ING_eBay_AutoLister.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// What the 2026-10-01 audit of every mapped endpoint found on the hosted build, pinned.
/// </summary>
/// <remarks>
/// <para>
/// Three questions were asked of all ~260 routes: can another website make a signed-in seller's
/// browser change something (CSRF), can one account read or change another's (per-user scoping),
/// and does any response carry a secret. The full list of findings, including the ones still open,
/// is in IMPROVEMENTS.md under that date. These are the tests for the ones that were fixed:
/// </para>
/// <list type="bullet">
/// <item>An eBay sign-in could be finished by — and so connect — an account that did not start it.</item>
/// <item>One process-wide eBay sign-in status and one process-wide action log were served to
/// every account.</item>
/// <item>Any account could disconnect the Facebook / Terapeak login the whole deployment shares.</item>
/// <item>The server's data folder, process id, database path, the owner's licence-key prefix and
/// eBay developer id were told to every account; an OAuth code was written to the log.</item>
/// </list>
/// <para>
/// Same shape as <see cref="LocalDraftsHostedTests"/>: ONE object, as the app has, with whoever is
/// signed in changed underneath it between assertions.
/// </para>
/// </remarks>
public class HostedEndpointAuditTests : IDisposable
{
    private const long UserA = 1;
    private const long UserB = 2;

    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(), "ing-endpoint-audit", Guid.NewGuid().ToString("N"));

    /// <summary>Who is signed in on the call in flight. Moved between assertions, never rebuilt.</summary>
    private long? _signedIn = UserA;

    public HostedEndpointAuditTests() => Directory.CreateDirectory(_scratch);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* a temp folder, not the point */ }
        GC.SuppressFinalize(this);
    }

    // ── eBay sign-in: the link at the end belongs to whoever started it ──────────────────────

    /// <summary>
    /// The attack this closes: somebody signs in to their OWN eBay account, keeps the link eBay's
    /// relay sends them back on, and gets a seller to open it. Before, the ledger only asked "did
    /// this server issue that session" — it had, to the attacker — so the seller's account was
    /// connected to the attacker's eBay and their next listing published there.
    /// </summary>
    [Fact]
    public async Task A_sign_in_link_one_account_started_does_not_connect_another_account()
    {
        var ebay = HostedService(out var stored, out var handler);

        _signedIn = UserA;
        var state = StateSentToEbay(ebay);

        _signedIn = UserB;
        var refused = await ebay.CompleteRelaySignInAsync(state, "pickup-from-a");

        Assert.Equal(EbaySignInStage.Failed, refused.Stage);
        Assert.Equal("state_mismatch", refused.Code);
        // Refused before the relay was asked for anything: the one-time pickup is still there for
        // the person it belongs to, and nothing was written to the account that opened the link.
        Assert.Equal(0, handler.Calls);
        Assert.Equal("", stored.Load(UserB).Data.EbayRefreshToken);

        _signedIn = UserA;
        handler.Then(System.Net.HttpStatusCode.OK, Tokens);
        var mine = await ebay.CompleteRelaySignInAsync(state, "pickup-from-a");

        Assert.Equal(EbaySignInStage.Connected, mine.Stage);
        Assert.Equal("refresh-a", stored.Load(UserA).Data.EbayRefreshToken);
        Assert.Equal("", stored.Load(UserB).Data.EbayRefreshToken);
    }

    /// <summary>
    /// The direct callback is a GET that stores a grant, and it used to make no check on
    /// <c>state</c> at all. This is the question Program.cs now asks first on the hosted build.
    /// </summary>
    [Fact]
    public void The_direct_callback_only_honours_a_state_this_account_was_issued()
    {
        var ebay = HostedService(out _, out _);

        _signedIn = UserA;
        var state = StateSentToEbay(ebay);
        Assert.EndsWith("h", state, StringComparison.Ordinal);

        Assert.True(ebay.IsSignInStartedHere(state));

        _signedIn = UserB;
        Assert.False(ebay.IsSignInStartedHere(state));

        // Nobody signed in, a state nobody issued, and no state at all.
        _signedIn = null;
        Assert.False(ebay.IsSignInStartedHere(state));
        _signedIn = UserA;
        Assert.False(ebay.IsSignInStartedHere("0123456789abcdef0123456789abcdefh"));
        Assert.False(ebay.IsSignInStartedHere(null));
        Assert.False(ebay.IsSignInStartedHere(""));

        // And a link works once: finishing the sign-in retires it.
        ebay.MarkDirectSignInConnected(hasRefreshToken: true, state);
        Assert.False(ebay.IsSignInStartedHere(state));
    }

    [Fact]
    public void The_callback_in_Program_cs_asks_before_it_spends_the_code()
    {
        var handler = Handler("app.MapGet(\"/api/ebay/callback\"");

        var check    = handler.IndexOf("HostedAuth.IsHostedBuild && !ebay.IsSignInStartedHere(state)", StringComparison.Ordinal);
        var exchange = handler.IndexOf("ExchangeCodeForTokenResultAsync(code)", StringComparison.Ordinal);

        Assert.True(check >= 0, "/api/ebay/callback no longer checks who started the sign-in.");
        Assert.True(exchange > check, "/api/ebay/callback exchanges the code before checking the state.");
    }

    /// <summary>
    /// <c>/api/ebay/status</c> is polled by the tab a seller is waiting in. One status for the
    /// process told that tab about whichever account had pressed Connect last.
    /// </summary>
    [Fact]
    public void Each_account_sees_only_its_own_sign_in_progress()
    {
        var ebay = HostedService(out _, out _);

        _signedIn = UserA;
        StateSentToEbay(ebay);
        Assert.Equal(EbaySignInStage.AwaitingConsent, ebay.SignInStatus.Stage);

        _signedIn = UserB;
        Assert.Equal(EbaySignInStage.Idle, ebay.SignInStatus.Stage);

        ebay.MarkDirectSignInFailed("no_code", "Declined.", "Try again.");
        Assert.Equal(EbaySignInStage.Failed, ebay.SignInStatus.Stage);

        _signedIn = UserA;
        Assert.Equal(EbaySignInStage.AwaitingConsent, ebay.SignInStatus.Stage);
    }

    [Fact]
    public void The_ledger_keeps_each_owners_sessions_apart()
    {
        var ledger = new EbayOAuthSessionLedger();
        var now = DateTimeOffset.UtcNow;

        ledger.Issue("mine", now, UserA);

        Assert.Equal(EbaySessionCheck.Valid,   ledger.Check("mine", now, UserA));
        Assert.Equal(EbaySessionCheck.Unknown, ledger.Check("mine", now, UserB));
        Assert.Equal(EbaySessionCheck.Unknown, ledger.Check("mine", now, null));

        // A busy quarter of an hour on the server: more people press Connect than the queue is
        // long. Each has their own queue, so the first one's sign-in is still there to finish.
        for (var other = 10; other < 30; other++) ledger.Issue($"s{other}", now, other);
        Assert.Equal(EbaySessionCheck.Valid, ledger.Check("mine", now, UserA));
    }

    [Fact]
    public void The_desktop_ledger_is_unchanged_when_no_owner_is_named()
    {
        var ledger = new EbayOAuthSessionLedger();
        var now = DateTimeOffset.UtcNow;

        ledger.Issue("one", now);
        Assert.Equal(EbaySessionCheck.Valid, ledger.Check("one", now));

        ledger.Consume("one", now);
        Assert.Equal(EbaySessionCheck.AlreadyUsed, ledger.Check("one", now));
        Assert.Equal(EbaySessionCheck.Expired,
            Checked(ledger, "two", now, now + EbayOAuthSessionLedger.Lifetime + TimeSpan.FromSeconds(1)));
    }

    private static EbaySessionCheck Checked(EbayOAuthSessionLedger ledger, string id, DateTimeOffset issued, DateTimeOffset asked)
    {
        ledger.Issue(id, issued);
        return ledger.Check(id, asked);
    }

    // ── The action log: /api/logs/recent ─────────────────────────────────────────────────────

    [Fact]
    public void On_a_server_each_account_reads_only_its_own_log()
    {
        var log = new ActionLog(HostedScope());

        _signedIn = UserA;
        log.Add("Info", "Listing published", "Antminer S19 95TH — item 1234");

        _signedIn = UserB;
        log.Add("Warning", "Publish failed", "Rolex Submariner — eBay refused the category");

        Assert.Equal("Publish failed", Assert.Single(log.Recent()).Title);

        _signedIn = UserA;
        Assert.Equal("Listing published", Assert.Single(log.Recent()).Title);
    }

    /// <summary>
    /// Startup and the background loops write with nobody signed in: where the data folder is,
    /// which database was found. That is the server describing itself, and no account's to read.
    /// </summary>
    [Fact]
    public void What_the_server_says_about_itself_is_shown_to_no_account()
    {
        var log = new ActionLog(HostedScope());

        _signedIn = null;
        log.Add("Info", "Data folder", "All saved data is in /var/lib/ing");
        Assert.Empty(log.Recent());

        _signedIn = UserA;
        Assert.Empty(log.Recent());

        // The owner, who has shown the admin key, reads all of it — newest first.
        log.Add("Info", "Listing published", "Antminer S19");
        var all = log.RecentForOwnerDashboard();
        Assert.Equal(["Listing published", "Data folder", "ING Listing Engine™ started"], all.Select(e => e.Title));
    }

    [Fact]
    public void A_busy_account_cannot_push_a_quiet_accounts_entries_out()
    {
        var log = new ActionLog(HostedScope());

        _signedIn = UserB;
        log.Add("Warning", "eBay needs you to sign in again", "The grant was revoked.");

        _signedIn = UserA;
        for (var i = 0; i < ActionLog.PerOwnerLimit + 50; i++) log.Add("Info", $"Scan {i}", "");

        Assert.Equal(ActionLog.PerOwnerLimit, log.Recent().Count);
        Assert.Equal($"Scan {ActionLog.PerOwnerLimit + 49}", log.Recent()[0].Title);

        _signedIn = UserB;
        Assert.Equal("eBay needs you to sign in again", Assert.Single(log.Recent()).Title);
    }

    [Fact]
    public void The_desktop_log_is_the_one_list_it_always_was()
    {
        var log = new ActionLog();
        log.Add("Info", "Listing published", "Antminer S19");

        Assert.Equal(["Listing published", "ING Listing Engine™ started"], log.Recent().Select(e => e.Title));

        for (var i = 0; i < 150; i++) log.Add("Info", $"Scan {i}", "");
        Assert.Equal(ActionLog.PerOwnerLimit, log.Recent().Count);
    }

    /// <summary>
    /// The app registers <c>AddSingleton&lt;ActionLog&gt;()</c>. This is what makes that one line
    /// pick up the per-user scope on the hosted build without Program.cs saying so.
    /// </summary>
    [Fact]
    public void The_container_hands_the_log_the_scope_it_has_registered()
    {
        var services = new ServiceCollection();
        services.AddSingleton(HostedScope());
        services.AddSingleton<ActionLog>();
        using var provider = services.BuildServiceProvider();

        var log = provider.GetRequiredService<ActionLog>();

        _signedIn = UserA;
        log.Add("Info", "Mine", "");
        _signedIn = UserB;
        Assert.Empty(log.Recent());
    }

    [Fact]
    public void The_logs_endpoint_reads_the_callers_log_and_the_owner_dashboard_reads_all_of_it()
    {
        Assert.Contains("log.Recent()", Handler("app.MapGet(\"/api/logs/recent\""));

        var owner = Handler("app.MapGet(\"/api/owner/stats\"");
        Assert.Contains("log.RecentForOwnerDashboard()", owner);
        // Still behind the admin key, checked before anything is read.
        Assert.True(owner.IndexOf("AdminKeyMatches(k)", StringComparison.Ordinal)
                  < owner.IndexOf("RecentForOwnerDashboard", StringComparison.Ordinal));
    }

    // ── The logins the whole deployment shares ───────────────────────────────────────────────

    [Fact]
    public void On_the_desktop_the_shared_login_guard_does_nothing()
    {
        Assert.Null(HostedShared.Refusal("Facebook Marketplace", hosted: false));
    }

    [Fact]
    public void On_a_server_an_account_is_refused_and_told_why()
    {
        var refusal = HostedShared.Refusal("Facebook Marketplace", hosted: true);

        Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsAssignableFrom<IStatusCodeHttpResult>(refusal).StatusCode);

        var body = System.Text.Json.JsonSerializer.Serialize(Assert.IsAssignableFrom<IValueHttpResult>(refusal).Value);
        Assert.Contains("Facebook Marketplace is connected once for everyone", body);
        Assert.Contains("\"started\":false", body);
    }

    /// <summary>
    /// Each of these deletes, replaces or drives a saved browser login. On the hosted app that
    /// login is the owner's and every account's searches run on it.
    /// </summary>
    [Theory]
    [InlineData("app.MapPost(\"/api/facebook/disconnect\"", "facebook.Disconnect()")]
    [InlineData("app.MapPost(\"/api/terapeak/disconnect\"", "terapeak.Disconnect()")]
    [InlineData("app.MapPost(\"/api/terapeak/connect\"", "terapeak.StartLogin()")]
    [InlineData("app.MapGet(\"/api/terapeak/debug-scrape\"", "terapeak.ScrapeAsync(q)")]
    public void Every_endpoint_that_changes_a_shared_login_asks_the_guard_first(string route, string action)
    {
        var handler = Handler(route);

        var guard = handler.IndexOf("HostedShared.Refusal(", StringComparison.Ordinal);
        var act   = handler.IndexOf(action, StringComparison.Ordinal);

        Assert.True(guard >= 0, $"{route} no longer asks HostedShared.Refusal.");
        Assert.True(act > guard, $"{route} acts before it asks.");
    }

    // ── What a response may say about the server and the owner ───────────────────────────────

    [Fact]
    public void A_server_path_is_told_to_the_desktop_seller_and_to_no_hosted_account()
    {
        Assert.Equal(@"C:\Users\me\AppData\Local\ING", HostedShared.ServerPath(@"C:\Users\me\AppData\Local\ING", hosted: false));
        Assert.Equal("", HostedShared.ServerPath("/var/lib/ing-listing-engine", hosted: true));
        Assert.Equal("", HostedShared.ServerPath(null, hosted: false));
    }

    [Fact]
    public void The_settings_fields_leave_out_the_owners_values_on_a_server()
    {
        static PublicFields Fields() => new()
        {
            EbayClientId      = "INGListi-hosted-PRD",
            EbayRuName        = "Nicholas-RuName",
            EbayDevId         = "dev-id-0000",
            LicenseKeyPreview = "ABCD1234****",
            HasLicenseKey     = true,
            DefaultPostalCode = "02360",
        };

        var hosted = HostedShared.WithoutDeploymentValues(Fields(), hosted: true);
        Assert.Equal("", hosted.EbayDevId);
        Assert.Equal("", hosted.LicenseKeyPreview);
        // What the page needs, and what is the seller's own, is untouched.
        Assert.Equal("INGListi-hosted-PRD", hosted.EbayClientId);
        Assert.Equal("Nicholas-RuName", hosted.EbayRuName);
        Assert.Equal("02360", hosted.DefaultPostalCode);
        Assert.True(hosted.HasLicenseKey);

        var desktop = HostedShared.WithoutDeploymentValues(Fields(), hosted: false);
        Assert.Equal("dev-id-0000", desktop.EbayDevId);
        Assert.Equal("ABCD1234****", desktop.LicenseKeyPreview);
    }

    [Fact]
    public void The_endpoints_that_named_server_paths_and_owner_values_go_through_the_guard()
    {
        Assert.Contains("HostedShared.WithoutDeploymentValues(store.GetPublicFields())", Handler("app.MapGet(\"/api/setup/fields\""));
        Assert.Contains("HostedShared.ServerPath(status.DatabasePath)", Handler("app.MapGet(\"/api/local-db/status\""));

        var identity = Handler("app.MapGet(AppInstance.IdentityPath");
        Assert.Contains("HostedShared.ServerPath(env.ContentRootPath)", identity);
        Assert.Contains("HostedShared.IsHosted() ? 0 : Environment.ProcessId", identity);
    }

    /// <summary>
    /// The pasted-redirect sign-in used to log the whole accepted URL — eBay's authorization code
    /// is in its query string — and the state. The log is read on screen, copied for support and
    /// shown on the owner dashboard.
    /// </summary>
    [Fact]
    public void No_log_line_carries_an_oauth_redirect_url_or_state()
    {
        foreach (var line in Program.Split((char)10).Where(l => l.Contains("log.Add(", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("AcceptedUrl", line);
            Assert.DoesNotContain("result.State", line);
        }
    }

    // ── The two lists the audit was taken against ────────────────────────────────────────────

    /// <summary>
    /// Everything reachable without signing in, in full. An endpoint added to this list is a
    /// decision, and this is where it has to be written down.
    /// </summary>
    [Fact]
    public void Exactly_nine_endpoints_are_open_to_somebody_who_is_not_signed_in()
    {
        var open = Sources()
            .SelectMany(source => source.Split('\n'))
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Count(line => line.Contains(".AllowAnonymous()", StringComparison.Ordinal));

        // /health, sign-up, sign-in, the CSRF token; /owner and /api/owner/stats (admin key);
        // the calibration read and write (admin key); /api/owner/programs (admin key) — the
        // dashboard's GitHub programs table, added 2026-10-01.
        Assert.Equal(9, open);
    }

    /// <summary>
    /// The CSRF check has no allow-list: every unsafe verb on every path needs the token. A path
    /// test appearing in it is how one endpoint quietly stops being protected.
    /// </summary>
    [Fact]
    public void The_csrf_check_exempts_no_path()
    {
        var csrf = ReadSource(Path.Combine("Services", "Csrf.cs"));
        var code = string.Join('\n', csrf.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.DoesNotContain("Request.Path", code);
        Assert.DoesNotContain("StartsWithSegments", code);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────────────────────

    private const string Tokens =
        """
        {"access_token":"access-a","refresh_token":"refresh-a",
         "expires_in":7200,"refresh_token_expires_in":47304000,"token_type":"User Access Token"}
        """;

    private UserScope HostedScope() => UserScope.PerUser(() => _signedIn);

    /// <summary>
    /// The hosted build: one <see cref="EbayService"/> for the process, with credentials AND the
    /// sign-in's owner both resolved from whoever <see cref="_signedIn"/> says is asking.
    /// </summary>
    private EbayService HostedService(out UserCredentialsStore stored, out ScriptedHttpHandler handler)
    {
        var server = ServerCredentials.FromConfiguration(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Credentials:EbayClientId"]     = "INGListi-hosted-PRD-0000000000-0000aaaa",
                ["Credentials:EbayClientSecret"] = "PRD-0000aaaa1111-2222-3333-4444",
                ["Credentials:EbayRuName"]       = "Nicholas_Squire-Nicholas-AutoLi-0000000",
            }).Build());

        stored = new UserCredentialsStore(
            Path.Combine(_scratch, "hosted.db"),
            CredentialCipher.FromKeyMaterial("a-scratch-deployment-secret"));

        var store = new CredentialsStore(new PerUserCredentialsSource(stored, () => _signedIn, server));

        handler = new ScriptedHttpHandler();
        return new EbayService(store, new StubHttpClientFactory(handler), new ActionLog(),
            relayReturn: EbayRelayReturn.Hosted, userScope: HostedScope());
    }

    private static string StateSentToEbay(EbayService ebay)
    {
        var result = ebay.CreateAuthorizationUrl();
        Assert.True(result.Ok, result.Problem?.Reason);
        return QueryHelpers.ParseQuery(new Uri(result.Url!).Query)["state"].ToString();
    }

    private static readonly string Program = ReadSource("Program.cs");

    /// <summary>One endpoint's source: from where it is mapped to where the next one is.</summary>
    private static string Handler(string mappedAs)
    {
        var start = Program.IndexOf(mappedAs, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Program.cs no longer maps {mappedAs}.");

        var next = Program.IndexOf("\napp.Map", start + mappedAs.Length, StringComparison.Ordinal);
        return next < 0 ? Program[start..] : Program[start..next];
    }

    /// <summary>Program.cs and every file under Services — everywhere an endpoint is mapped.</summary>
    private static IEnumerable<string> Sources()
    {
        yield return Program;
        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "ING eBay AutoLister", "Services"), "*.cs"))
            yield return File.ReadAllText(file);
    }

    private static string ReadSource(string name) =>
        File.ReadAllText(Path.Combine(RepoRoot(), "ING eBay AutoLister", name));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ING eBay AutoLister.slnx")))
            dir = dir.Parent;
        Assert.True(dir is not null, "could not find the repository root above " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
