using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

// ── Crash logging ────────────────────────────────────────────────────────────
// Writes crash.log next to the exe before the process dies so the cause is
// visible even when there is no console window to read.
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        File.AppendAllText(Path.Combine(dir, "crash.log"),
            $"{DateTime.Now:u}: {e.ExceptionObject}\n---\n");
    }
    catch { }
};

// ── Service mode detection ────────────────────────────────────────────────────
// When launched by the Windows SCM, run headless (no tray icon, no browser).
// Interactive launches (double-click, startup shortcut) get the full tray UI.
bool isWindowsService = WindowsServiceHelpers.IsWindowsService();

// ── Dev port override ─────────────────────────────────────────────────────────
// Set AUTOLISTER_DEV_PORT to run a second, independent instance side-by-side
// with the installed Windows service (e.g. while iterating on source without
// touching the service's port 9332).
var port    = Environment.GetEnvironmentVariable("AUTOLISTER_DEV_PORT") ?? "9332";
var baseUrl = $"http://localhost:{port}";
var isDevPort = port != "9332";

// ── Elevated helper: add inglist.com → 127.0.0.1 to hosts ──────────
// The installer re-launches with this flag as admin. After adding the entry the
// process exits immediately — it is not the long-running server instance.
// (removed) --add-local-dns hosts-file writer — see note near app startup: the
// hosts write is gone entirely to avoid antivirus/EDR flagging. App runs on
// http://localhost:9332.

// ── Post-install helper: just open the web UI, then exit ──────────────────────
// The MSI runs the exe with this flag when the install finishes. It does NOT bind
// a port or start the tray/server (the installed Windows service already owns
// port 9332), so there's no conflict — it only pops the browser to the running
// app so the user lands on the page immediately after installing.
if (args.Contains("--open-browser"))
{
    try
    {
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(baseUrl) { UseShellExecute = true });
    }
    catch { }
    return;
}

// ── Single-instance / already-running guard ───────────────────────────────────
// Service mode: SCM guarantees a single instance — skip the mutex entirely.
// Interactive mode:
//   1. Acquire mutex so only one tray instance runs at a time.
//   2. If the Windows service is already serving on 9332, show a tray icon
//      without starting a second web server (opens browser immediately).
//   3. Otherwise start the server ourselves, then show the tray icon.
System.Threading.Mutex? _mutex = null;
if (!isWindowsService)
{
    _mutex = new System.Threading.Mutex(true, $"ING-AutoLister-{port}", out var isFirstInstance);
    if (!isFirstInstance)
    {
        // Another interactive instance (tray) is already running — just open browser
        System.Diagnostics.Process.Start(
            new System.Diagnostics.ProcessStartInfo(baseUrl) { UseShellExecute = true });
        _mutex.Dispose();
        return;
    }

    // Check whether the Windows service is already hosting the web server.
    // Skipped when running on a dev override port — that's a deliberate
    // side-by-side instance, not a duplicate of the service.
    bool serverAlive = false;
    if (!isDevPort)
    {
        try
        {
            using var pingHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            serverAlive = (await pingHttp.GetAsync($"{baseUrl}/api/setup/status")).IsSuccessStatusCode;
        }
        catch { }
    }

    if (serverAlive)
    {
        // Service is running — show tray icon as a UI helper (don't start another server)
        OpenBrowser();
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var trayIconSvc = new System.Windows.Forms.NotifyIcon
        {
            Icon    = CreateAppIcon(),
            Text    = $"ING AutoLister  •  localhost:{port}",
            Visible = true,
        };
        var ctxMenuSvc = new System.Windows.Forms.ContextMenuStrip();
        ctxMenuSvc.Items.Add("Open ING AutoLister", null, (_, _) => OpenBrowser());
        ctxMenuSvc.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        ctxMenuSvc.Items.Add("Close Tray Icon", null, (_, _) =>
        {
            trayIconSvc.Visible = false;
            System.Windows.Forms.Application.ExitThread();
        });
        trayIconSvc.ContextMenuStrip = ctxMenuSvc;
        trayIconSvc.DoubleClick     += (_, _) => OpenBrowser();
        System.Windows.Forms.Application.Run();
        _mutex.Dispose();
        return;
    }
}

// ── Data directory ───────────────────────────────────────────────────────────
// For portable / perUser installs the exe lives in a writable folder, so data
// stays next to the exe (original behaviour).  For perMachine / Program Files
// installs the exe directory is read-only for regular users, so user data goes
// to %LOCALAPPDATA%\ING AutoLister instead.
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
var pf     = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
var pf86   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
var isSystemInstall = !string.IsNullOrEmpty(pf) &&
    (exeDir.StartsWith(pf,   StringComparison.OrdinalIgnoreCase) ||
     exeDir.StartsWith(pf86, StringComparison.OrdinalIgnoreCase));
var dataDir = isWindowsService
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ING AutoLister")
    : isSystemInstall
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ING AutoLister")
        : exeDir;
Directory.CreateDirectory(dataDir);

// On the first run after a system install, seed the pre-configured
// credentials.json from the Program Files template into the user's data folder.
if (isSystemInstall)
{
    var credsDest = Path.Combine(dataDir, "credentials.json");
    var credsSrc  = Path.Combine(exeDir,  "credentials.json");
    if (!File.Exists(credsDest) && File.Exists(credsSrc))
        File.Copy(credsSrc, credsDest);
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = dataDir
});
builder.Host.UseWindowsService();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<CredentialsStore>();
builder.Services.AddSingleton<ClaudeService>();
builder.Services.AddSingleton<EbayService>();
builder.Services.AddSingleton<ListingDatabase>();
builder.Services.AddSingleton<ImageGenerationService>();
builder.Services.AddSingleton<PhotoLibrary>();
builder.Services.AddSingleton<ActionLog>();
builder.Services.AddSingleton<DraftStore>();
// Crash recovery and duplicate-publish protection. Singletons, and PublishGuard must be one:
// its in-flight leases live in memory, and a per-request instance would hold no lease at all.
builder.Services.AddSingleton<WorkRecoveryStore>();
builder.Services.AddSingleton<PublishGuard>();
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddSingleton<StripeService>();
builder.Services.AddSingleton<AnalyticsStore>();
builder.Services.AddSingleton<TerapeakService>();
builder.Services.AddSingleton<TerapeakPriceCache>();
// Local sourcing — Facebook Marketplace has no public search API, so this uses the same
// saved-browser-session pattern as Terapeak (one visible login to the seller's own account,
// then headless reads). User-driven only: never scheduled, never a side effect of anything
// else. See FacebookMarketplaceService.
builder.Services.AddSingleton<FacebookMarketplaceService>();
// Craigslist is the same job with none of that machinery: public search, no login, an RSS feed
// and craigslist's own postal+distance filter. See CraigslistService.
builder.Services.AddSingleton<CraigslistService>();
// Both sites behind one interface, so the arbitrage pipeline (grouping → comp lookup → profit →
// ranking) never learns which site a listing came from and a third source is a registration.
// Order matters: no-login sources first, so a seller who has connected nothing still gets
// results from a default search. See ILocalSupplySource / LocalSupplySources.
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<CraigslistService>());
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<FacebookMarketplaceService>());
builder.Services.AddSingleton<LocalSupplySources>();
// Local sold-history lookup — read-only against the externally-maintained Marketplace.db at
// C:\INGListing\Data\Marketplace.db (populated by a separate collector process). Feeds the
// Opportunity Finder's Supplier File Analyzer with real local comps before falling back to
// Terapeak. See ExternalMarketplaceDb / MarketplaceRepository for the read-only guarantees.
builder.Services.AddSingleton<ExternalMarketplaceDb>();
builder.Services.AddSingleton<MarketplaceRepository>();
// Hosted sold-comps path — queries comps.php on inglisting.com (ing_sold_listings MariaDB) and
// reuses the exact same ComparableMatcher scoring as the local repo. Selected below when
// MarketCompsApiUrl is configured; otherwise the local Marketplace.db repository is used.
builder.Services.AddSingleton<HostedMarketplaceClient>();
builder.Services.AddSingleton<HostedMarketplaceRepository>();
builder.Services.AddSingleton<IMarketplaceRepository>(sp =>
    !string.IsNullOrWhiteSpace(sp.GetRequiredService<CredentialsStore>().Get().MarketCompsApiUrl)
        ? sp.GetRequiredService<HostedMarketplaceRepository>()
        : sp.GetRequiredService<MarketplaceRepository>());
// Structured brand/model/part-number extraction from free-text titles — see
// ProductIdentityExtractor. Stateless; used before every local sold-history search.
builder.Services.AddSingleton<ProductIdentityExtractor>();
// Sell-through / liquidity scoring — how fast an item is likely to sell, from local sold-history
// date density only. See LiquidityScoringConfig for every tunable threshold/weight.
builder.Services.AddSingleton<LiquidityScoringConfig>();
builder.Services.AddSingleton<LiquidityScoringService>();
// Real product-matching and scoring engine (see the plan this was built from): normalizes a raw
// title into brand/model/spec/quantity/negative-keywords (ProductNormalizer), scores one
// SoldListings candidate against that identity with the weighted point table + hard exclusions
// (ComparableMatcher), turns matched comparables + Terapeak into a full price estimate
// (MarketPriceEstimator, via TerapeakMarketService — lazy/rationed, never bulk-queries Terapeak),
// computes SellThroughRate=Sold/Active (SellThroughCalculator), fee/profit math off a configurable
// FeeProfile (ProfitCalculator), and the final Opportunity/Confidence scores (OpportunityScoringService/
// ConfidenceScoringService).
builder.Services.AddSingleton<ProductNormalizer>();
builder.Services.AddSingleton<ComparableMatcher>();
builder.Services.AddSingleton<TerapeakMarketService>();
builder.Services.AddSingleton<MarketPriceEstimator>();
builder.Services.AddSingleton<SellThroughCalculator>();
builder.Services.AddSingleton<FeeProfile>();
builder.Services.AddSingleton<ProfitCalculator>();
// The seller's real fee/cost assumptions, persisted, and read into the FeeProfile singleton at
// startup (see the FeeProfileStore.Apply call after the app is built). Every screen that costs an
// item shares that one instance, so saving the Fees & Costs form re-prices the whole app at once.
builder.Services.AddSingleton<FeeProfileStore>();
// All-in net proceeds, break-even and the minimum offer to accept — the one calculation behind
// every price the app shows, on the sourcing screens and in the listing editor alike.
builder.Services.AddSingleton<NetProceedsCalculator>();
builder.Services.AddSingleton<OpportunityScoringService>();
builder.Services.AddSingleton<ConfidenceScoringService>();
builder.Services.AddSingleton<CrossListingFeeProfile>();
builder.Services.AddSingleton<CrossListingExporter>();
// Local arbitrage — prices each Facebook Marketplace result against the sold-comps database and
// Terapeak, then ranks by net profit after fees. Pure ranking/verdict logic plus ProfitCalculator;
// the pricing lookups themselves are orchestrated in FindLocalArbitrageAsync below.
builder.Services.AddSingleton<LocalArbitrageAnalyzer>();
// Roll the Dice — the cross-category sweep. Mines the sold-comps database for products that carry
// a margin AND actually sell, then reuses LocalArbitrageAnalyzer above to cost buying each of them
// wherever supply exists (local classifieds, or eBay Buy It Now). Pure clustering/screening/verdict
// logic; the sweep itself is orchestrated in RollTheDiceAsync below.
builder.Services.AddSingleton<JackpotHunter>();
// Inventory health — the same pricing stack pointed at listings the seller ALREADY owns, to find
// the ones whose price the market has drifted out from under. CostBasisStore holds the one number
// eBay can never supply (what the seller paid), which is what turns a markdown suggestion into a
// break-even-checked one.
builder.Services.AddSingleton<CostBasisStore>();
builder.Services.AddSingleton<InventoryHealthAnalyzer>();
// Liquidation lots — the same pricing stack pointed at a whole pallet at once. LotAnalyzer owns
// only the part that is specific to buying in bulk: recovery by grade, per-unit fees across every
// unit on the manifest, the ask allocated across the lines, the max bid solved exactly, and which
// few lines actually carry the value. Orchestrated in AnalyzeLotAsync below.
builder.Services.AddSingleton<LotAnalyzer>();
// Promoted Listings ROI — the ad rate that maximises take-home rather than the one eBay suggests
// from what the rest of the category is paying. Shares ProfitCalculator/FeeProfile with every other
// money screen, so the margin an ad rate is measured against is the same margin the editor shows.
builder.Services.AddSingleton<PromotedListingAdvisor>();

// CORS: lets the standalone admin panel (a local file, e.g. on G:\) fetch the
// owner API cross-origin. The owner/stats endpoint is still gated by the admin
// key, so opening it to any origin only exposes what an admin-key holder can
// already read. This is a loopback desktop app, not a public server.
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();

// Serve UI files from embedded resources (bundled inside the exe)
{
    var asm = typeof(Program).Assembly;
    var ns  = "ING_eBay_AutoLister.wwwroot";
    var embedded = new Microsoft.Extensions.FileProviders.EmbeddedFileProvider(asm, ns);
    app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = embedded, DefaultFileNames = ["index.html"] });
    app.UseStaticFiles(new StaticFileOptions { FileProvider = embedded });
}

// Serve generated-photos from a writable folder next to the exe
{
    var photosDir = Path.Combine(app.Environment.ContentRootPath, "generated-photos");
    Directory.CreateDirectory(photosDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(photosDir),
        RequestPath  = "/generated-photos"
    });
}

// Serve the representative-photo library (seller's real per-model stock photos) from photos/.
{
    var libDir = Path.Combine(app.Environment.ContentRootPath, "photos");
    Directory.CreateDirectory(libDir);
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(libDir),
        RequestPath  = "/photos"
    });
}

// Stripe keys are configured via the Settings page and stored in credentials.json

// Record install date on first run
app.Services.GetRequiredService<CredentialsStore>().EnsureInstallDate();

// Load the seller's saved fee/cost assumptions into the FeeProfile singleton before anything is
// priced. Without this every net-profit figure in the app silently reverts to the hardcoded
// defaults — eBay's cut and nothing else — on each restart.
try
{
    app.Services.GetRequiredService<FeeProfileStore>().Apply(app.Services.GetRequiredService<FeeProfile>());
}
catch (Exception ex)
{
    // A first run creates the table; a corrupted one falls back to the built-in defaults rather
    // than refusing to start. The defaults are conservative, not wrong — just incomplete.
    app.Services.GetRequiredService<ActionLog>()
        .Add("Warning", "Fee settings not loaded", $"Using default fee assumptions — {ex.Message}");
}

// Marketplace database startup check — confirms the local sold-history lookup is usable, and
// adds any of its (non-destructive) indexes that are still missing. Never fatal: Opportunity
// Finder works fine without this, just without local comps until the file/table is available.
// Backgrounded like the license check below: on a large Marketplace.db, GetSoldListingsCount's
// full COUNT(*) scan and EnsureIndexes' CREATE INDEX can take a while, and neither should delay
// the server from accepting requests — Opportunity Finder just falls back to Terapeak until this
// finishes.
_ = Task.Run(() =>
{
    var marketplaceLog = app.Services.GetRequiredService<ActionLog>();
    try
    {
        var externalDb = app.Services.GetRequiredService<ExternalMarketplaceDb>();
        if (!externalDb.DatabaseFileExists)
        {
            marketplaceLog.Add("Warning", "Marketplace database not found", $"No file at {externalDb.DatabasePath}.");
        }
        else if (!externalDb.SoldListingsTableExists())
        {
            marketplaceLog.Add("Warning", "Marketplace database connected", "SoldListings table not found.");
        }
        else
        {
            var count = externalDb.GetSoldListingsCount();
            marketplaceLog.Add("Info", "Marketplace database connected", $"Marketplace database connected: {count} sold listings");

            var (attempted, error, created) = externalDb.EnsureIndexes();
            if (!attempted)
                marketplaceLog.Add("Warning", "Marketplace index check skipped", error ?? "Unknown error.");
            else if (created.Count > 0)
                marketplaceLog.Add("Info", "Marketplace indexes added", string.Join(", ", created));
        }
    }
    catch (Exception ex)
    {
        marketplaceLog.Add("Warning", "Marketplace database connection failed", ex.Message);
    }
});

// Background license check — non-blocking, runs after startup
_ = Task.Run(async () =>
{
    await Task.Delay(2000);
    await app.Services.GetRequiredService<LicenseService>().CheckAsync();
});

// ── Background maintenance loop ───────────────────────────────────────────
_ = Task.Run(async () =>
{
    await Task.Delay(10_000); // wait for startup

    while (true)
    {
        try
        {
            // 1. Proactive eBay token refresh — top up 20 min before expiry
            var store = app.Services.GetRequiredService<CredentialsStore>();
            if (store.IsAccessTokenExpiringSoon(minutes: 20) && store.HasValidRefreshToken())
            {
                try
                {
                    var ebay = app.Services.GetRequiredService<EbayService>();
                    await ebay.ProactiveTokenRefreshAsync();
                }
                catch { /* non-fatal — will retry next cycle */ }
            }

            // 2. Generated-photos cleanup — keep newest 300, delete the rest
            var photosDir = Path.Combine(
                app.Services.GetRequiredService<IWebHostEnvironment>().WebRootPath,
                "generated-photos");
            if (Directory.Exists(photosDir))
            {
                var files = new DirectoryInfo(photosDir)
                    .GetFiles("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .ToList();
                foreach (var old in files.Skip(300))
                {
                    try { old.Delete(); } catch { /* skip locked files */ }
                }
            }
        }
        catch { /* maintenance loop must never crash */ }

        await Task.Delay(TimeSpan.FromMinutes(5));
    }
});

// Auto-connect-on-startup, the continuous background scanner, and the Gem Radar feature built on
// top of it were removed at the user's request (2026-07-15/16): unattended, continuous automated
// access to Terapeak/Seller Hub is a real eBay ToS risk (the User Agreement's "no robot, spider,
// scraper, or other automated means" clause), and all of it existed purely to keep that running
// 24/7 without the user asking each time. Full history is in git if this is ever wanted back.
// Terapeak is still fully usable — connect manually from Settings or the Opportunity Finder
// banner (a real visible browser window, since only a person can clear eBay's login captcha/
// security challenge), and on-demand pricing lookups from Opportunity Finder / New Listing still
// work, since those only ever run when a person actually asks for them.

// ── Trial ─────────────────────────────────────────────────────────
app.MapGet("/api/trial/status", (CredentialsStore store, LicenseService license) =>
{
    store.EnsureInstallDate();
    var lic = license.Current;
    // Any valid license (beta key now grants "pro" — see LicenseService.CheckAsync) = unlimited access
    if (lic.Valid && lic.Tier is "free" or "pro")
        return Results.Ok(new { daysRemaining = 9999, expired = false, licensed = true, tier = "beta" });
    var days    = store.TrialDaysRemaining();
    var expired = days <= 0;
    return Results.Ok(new { daysRemaining = days, expired, licensed = !expired, tier = expired ? "expired" : "trial" });
});

// ── License ───────────────────────────────────────────────────────
app.MapGet("/api/license/status", (LicenseService license) => Results.Ok(license.Current));

app.MapPost("/api/license/activate", async (LicenseService license) =>
    Results.Ok(await license.CheckAsync()));

// ── Stripe ────────────────────────────────────────────────────────
app.MapGet("/api/stripe/config", (StripeService stripe) =>
    Results.Ok(new { configured = stripe.IsConfigured, publishableKey = stripe.PublishableKey }));

app.MapPost("/api/stripe/checkout", async (StripeService stripe, HttpContext ctx) =>
{
    if (!stripe.IsConfigured)
        return Results.BadRequest(new { error = "Stripe not configured." });
    try
    {
        var successUrl = "https://ingmining.com/autolister-pro-success?session_id={CHECKOUT_SESSION_ID}";
        var cancelUrl  = $"{ctx.Request.Scheme}://{ctx.Request.Host}/";
        var url = await stripe.CreateProCheckoutSessionAsync(successUrl, cancelUrl);
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/stripe/checkout/annual", async (StripeService stripe, HttpContext ctx) =>
{
    if (!stripe.IsConfigured)
        return Results.BadRequest(new { error = "Stripe not configured." });
    try
    {
        var successUrl = "https://ingmining.com/autolister-pro-success?session_id={CHECKOUT_SESSION_ID}";
        var cancelUrl  = $"{ctx.Request.Scheme}://{ctx.Request.Host}/";
        var url = await stripe.CreateProAnnualCheckoutSessionAsync(successUrl, cancelUrl);
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Setup / credentials ───────────────────────────────────────────
app.MapGet("/api/setup/status", (CredentialsStore store) => Results.Ok(store.GetStatus()));
app.MapGet("/api/setup/fields", (CredentialsStore store) => Results.Ok(store.GetPublicFields()));

app.MapPost("/api/setup/save", (Credentials body, CredentialsStore store) =>
{
    store.Save(body);
    return Results.Ok(store.GetStatus());
});

// ── Trial guard ───────────────────────────────────────────────────
static IResult? TrialGuard(CredentialsStore store, LicenseService license)
{
    return null; // Freeware — no restrictions
}

// ── Failure reporting ─────────────────────────────────────────────
// Every critical path answers with a body the page can render, whatever went wrong inside it.
//
// The failure this replaces: an unhandled exception reached ASP.NET's edge, which answered 500 with
// an HTML error page. Every caller in app.js does `await res.text()` or `.json()` on the failure
// path, so the seller's "AI analysis failed" message was either a stack trace or a fragment of HTML
// — and on /api/analyze and /api/listing/update there was no try/catch at all, so that was the
// normal outcome of a rate limit or an expired eBay token.
//
// `error` and `details` stay at the top level because existing callers read exactly those two
// fields; `failure` is the added structure new UI reads. Nothing that already worked is moved.
static IResult FailureJson(FailureInfo failure) => Results.Json(new
{
    ok = false,
    error = failure.Headline,
    details = string.IsNullOrWhiteSpace(failure.Technical) ? failure.WhatHappened : failure.Technical,
    failure = new
    {
        kind = failure.Kind.ToString(),
        domain = failure.Domain.ToString(),
        headline = failure.Headline,
        whatHappened = failure.WhatHappened,
        whatToDo = failure.WhatToDo,
        retryable = failure.Retryable,
        retryAfterSeconds = failure.RetryAfterSeconds,
        fixAction = failure.FixAction,
        attempts = failure.Attempts,
        workPreserved = failure.WorkPreserved,
        technical = failure.Technical,
    },
}, statusCode: 400);

// Something the seller supplied is missing or unusable. Same shape as a real failure so one bit of
// UI renders both, but never retryable — repeating the same bad input repeats the same answer.
static IResult BadInputJson(string headline, string whatHappened, string whatToDo) =>
    FailureJson(new FailureInfo
    {
        Kind = FailureKind.BadInput,
        Headline = headline,
        WhatHappened = whatHappened,
        WhatToDo = whatToDo,
        Retryable = false,
    });

// Runs an endpoint body so no exception can escape as a 500. A cancelled request is rethrown
// untouched: the browser has gone, and there is nothing left to render a message for.
static async Task<IResult> Guarded<T>(FailureDomain domain, string operation, ActionLog log, Func<Task<T>> work)
{
    try
    {
        return Results.Ok(await work());
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, domain);
        log.Add("Warning", operation + " failed", $"{failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }
}

// ── AI analysis ───────────────────────────────────────────────────
app.MapPost("/api/analyze", async (AnalyzeRequest req, ClaudeService claude, CredentialsStore store,
    LicenseService license, ActionLog log, CancellationToken ct) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrEmpty(req.ImageBase64))
        return BadInputJson("No photo reached the app",
            "The image did not arrive with the request, so there was nothing to analyse.",
            "Drop the photo in again, or use Browse rather than paste.");

    return await Guarded(FailureDomain.Ai, "AI listing from photo", log,
        () => claude.AnalyzeImageAsync(req.ImageBase64, req.MimeType, ct));
});

app.MapPost("/api/analyze-url", async (AnalyzeUrlRequest req, ClaudeService claude, EbayService ebay, IHttpClientFactory httpFactory, IWebHostEnvironment env, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.Url))
        return Results.BadRequest(new { error = "URL is required" });

    try
    {
        // ── eBay listing URL → use eBay API directly (no scraping needed) ──
        var ebayItemId = ExtractEbayItemId(req.Url);
        if (!string.IsNullOrEmpty(ebayItemId))
        {
            log.Add("Info", "Analyze eBay item", ebayItemId);
            var item = await ebay.GetItemAsync(ebayItemId);

            // Save first image locally so the photo grid can show it
            if (item.ImageUrls.Count > 0)
            {
                try
                {
                    var http2 = httpFactory.CreateClient();
                    var imgBytes2 = await http2.GetByteArrayAsync(item.ImageUrls[0]);
                    var photosDir = System.IO.Path.Combine(env.ContentRootPath, "generated-photos");
                    System.IO.Directory.CreateDirectory(photosDir);
                    var ext2  = item.ImageUrls[0].Contains(".png") ? "png" : "jpg";
                    var file2 = $"ebay_{Guid.NewGuid():N}.{ext2}";
                    await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(photosDir, file2), imgBytes2);
                    var ebayLocalUrl = $"/generated-photos/{file2}";
                    item.ImageUrls.Insert(0, ebayLocalUrl);
                }
                catch { /* non-fatal */ }
            }

            // Rewrite the description with Claude SEO template — the original seller's
            // description is often plain text or poorly formatted HTML
            try
            {
                log.Add("Info", "Rewriting eBay description with Claude SEO template", ebayItemId);
                var improved = await claude.ImproveSeoAsync(new ImproveSeoRequest
                {
                    Title         = item.Title ?? "",
                    Subtitle      = item.Subtitle ?? "",
                    Category      = item.Category ?? "",
                    Condition     = item.Condition ?? "",
                    Brand         = item.Brand ?? "",
                    Price         = item.Price,
                    Description   = item.Description ?? "",
                    ItemSpecifics = item.ItemSpecifics ?? [],
                    Quantity                 = item.Quantity,
                    WeightLbs                = item.WeightLbs,
                    WeightOz                 = item.WeightOz,
                    PackageLengthIn          = item.PackageLengthIn,
                    PackageWidthIn           = item.PackageWidthIn,
                    PackageHeightIn          = item.PackageHeightIn,
                    HandlingTimeBusinessDays = item.HandlingTimeBusinessDays,
                    ItemLocationPostalCode   = item.ItemLocationPostalCode ?? "",
                    ImageUrls                = item.ImageUrls,
                });
                // Keep original structured data — only take the rewritten description and title
                item.Description = improved.Description;
                if (!string.IsNullOrWhiteSpace(improved.Title)) item.Title = improved.Title;
            }
            catch (Exception ex)
            {
                // Non-fatal — return original data if Claude rewrite fails
                log.Add("Warning", "eBay description SEO rewrite failed", ex.Message);
            }

            return Results.Ok(item);
        }

        // ── General URL → headless screenshot + Claude vision ────────────────
        log.Add("Info", "Analyze URL (headless screenshot)", req.Url[..Math.Min(80, req.Url.Length)]);

        var (screenshotB64, productImageUrl) = await TakeHeadlessScreenshot(req.Url, log);
        if (string.IsNullOrEmpty(screenshotB64))
            return Results.BadRequest(new { error = "Could not load the page — try copying the product image and dropping it into the Product Photo zone instead." });

        var photosDir2 = System.IO.Path.Combine(env.ContentRootPath, "generated-photos");
        System.IO.Directory.CreateDirectory(photosDir2);

        // Save full screenshot for AI analysis
        var ssFile = $"url_{Guid.NewGuid():N}.png";
        await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(photosDir2, ssFile), Convert.FromBase64String(screenshotB64));

        var listing2 = await claude.AnalyzeImageAsync(screenshotB64, "image/png");
        listing2.ImageUrls.Insert(0, $"/generated-photos/{ssFile}");

        // Fetch and save the clean product image (best source for BG removal — put at index 0)
        var productImageFetched = false;
        var candidateUrls = new List<string?> { productImageUrl };

        // Also try OG image
        try
        {
            var http3 = httpFactory.CreateClient();
            http3.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 Chrome/120.0");
            http3.Timeout = TimeSpan.FromSeconds(6);
            var html3 = await http3.GetStringAsync(req.Url);
            candidateUrls.Add(ExtractPrimaryImageUrl(html3));
        }
        catch { }

        foreach (var candidateUrl in candidateUrls)
        {
            if (string.IsNullOrWhiteSpace(candidateUrl) || productImageFetched) continue;
            try
            {
                var http4 = httpFactory.CreateClient();
                http4.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 Chrome/120.0");
                http4.Timeout = TimeSpan.FromSeconds(10);
                var imgBytes = await http4.GetByteArrayAsync(candidateUrl);
                var ext = candidateUrl.Contains(".png") ? "png" : "jpg";
                var prodFile = $"prod_{Guid.NewGuid():N}.{ext}";
                await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(photosDir2, prodFile), imgBytes);
                listing2.ImageUrls.Insert(0, $"/generated-photos/{prodFile}");
                productImageFetched = true;
                log.Add("Info", "Product image saved for BG removal", prodFile);
            }
            catch { }
        }

        return Results.Ok(listing2);

    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        // Domain is Ai rather than Photos: the request may fail on the page load or on the model,
        // and the AI branch already reports a network failure honestly. What matters is that a
        // rate-limited model here reads as "wait a moment", not as "that URL is broken".
        var failure = FailureTranslator.Translate(ex, FailureDomain.Ai);
        log.Add("Warning", "Analyze URL failed", $"{failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }
});

static async Task<(string Screenshot, string? ProductImage)> TakeHeadlessScreenshot(string url, ActionLog log)
{
    var playwrightDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "npm", "node_modules", "playwright");

    var escapedUrl = url.Replace("\\", "\\\\").Replace("'", "\\'");
    var pwPath = playwrightDir.Replace("\\", "\\\\");
    var script =
        $"const {{ chromium }} = require('{pwPath}');\n" +
        "(async () => {\n" +
        "  const browser = await chromium.launch({ headless: true,\n" +
        "    args: ['--disable-blink-features=AutomationControlled','--no-sandbox'] });\n" +
        "  const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 },\n" +
        "    userAgent: 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',\n" +
        "    locale: 'en-US', timezoneId: 'America/New_York' });\n" +
        "  await ctx.addInitScript(() => { Object.defineProperty(navigator,'webdriver',{get:()=>undefined}); });\n" +
        "  const page = await ctx.newPage();\n" +
        "  try {\n" +
        $"    await page.goto('{escapedUrl}', {{ waitUntil: 'domcontentloaded', timeout: 25000 }});\n" +
        "    await page.waitForTimeout(2500);\n" +
        "    for (const sel of ['button:has-text(\"Continue shopping\")','input[value*=\"Continue\"]','#continueShopping',\n" +
        "        'button[aria-label*=\"close\" i]','button[aria-label*=\"dismiss\" i]','button[aria-label*=\"accept\" i]',\n" +
        "        '[class*=\"modal-close\"]','[class*=\"popup-close\"]','[class*=\"close-modal\"]',\n" +
        "        'button:has-text(\"Accept all\")','button:has-text(\"Accept cookies\")','button:has-text(\"Got it\")','button:has-text(\"I agree\")']) {\n" +
        "      const btn = await page.$(sel).catch(()=>null);\n" +
        "      if (btn) { try { await btn.click(); await page.waitForTimeout(500); } catch(_) {} }\n" +
        "    }\n" +
        // Strip watermarks, cookie banners, consent overlays, and popups via CSS injection
        "    await page.addStyleTag({ content: [\n" +
        "      '[class*=\"watermark\" i],[id*=\"watermark\" i],[class*=\"water-mark\" i],[id*=\"water-mark\" i],',\n" +
        "      '.WatermarkContainer,[class*=\"WatermarkImage\"],[class*=\"wm-overlay\"],[class*=\"img-protection\"],',\n" +
        "      '[class*=\"cookie-banner\" i],[class*=\"cookie-bar\" i],[class*=\"cookie-notice\" i],[class*=\"cookie-wall\" i],',\n" +
        "      '[id*=\"cookie-banner\" i],[id*=\"cookie-bar\" i],[id*=\"cookie-notice\" i],',\n" +
        "      '[class*=\"consent-banner\" i],[class*=\"gdpr-banner\" i],[class*=\"gdpr-notice\" i],',\n" +
        "      '#onetrust-banner-sdk,#onetrust-consent-sdk,[class*=\"CookieBanner\"],[class*=\"cookieBanner\"],',\n" +
        "      '.cc-window,.cc-banner,[class*=\"CybotCookiebot\"],[id*=\"CybotCookiebot\"],',\n" +
        "      '.ReactModal__Overlay,.modal-backdrop,[class*=\"modal-overlay\" i],[class*=\"popup-overlay\" i],',\n" +
        "      '[class*=\"interstitial\" i],[class*=\"newsletter-popup\" i],[class*=\"email-popup\" i]',\n" +
        "      '{display:none!important;opacity:0!important;pointer-events:none!important}'\n" +
        "    ].join('') }).catch(()=>{});\n" +
        // JS pass: remove absolutely/fixed-positioned text overlays on top of product images
        // (seller brand names, company logos overlaid as HTML elements over the image)
        "    await page.evaluate(() => {\n" +
        "      try {\n" +
        "        const imgs = Array.from(document.querySelectorAll('img'))\n" +
        "          .filter(img => img.naturalWidth >= 200 && img.naturalHeight >= 200);\n" +
        "        for (const img of imgs) {\n" +
        "          const imgRect = img.getBoundingClientRect();\n" +
        "          if (!imgRect.width || !imgRect.height) continue;\n" +
        "          const container = img.closest('[class*=\"product\"],[class*=\"gallery\"],[class*=\"image-wrap\"],[class*=\"img-wrap\"],[class*=\"photo\"]') || img.parentElement;\n" +
        "          if (!container) continue;\n" +
        "          const candidates = container.querySelectorAll('*');\n" +
        "          for (const el of candidates) {\n" +
        "            if (el === img || el.tagName === 'IMG' || el.querySelector('img')) continue;\n" +
        "            const st = window.getComputedStyle(el);\n" +
        "            if (st.position !== 'absolute' && st.position !== 'fixed') continue;\n" +
        "            const text = el.textContent.trim();\n" +
        "            if (!text || text.length > 80) continue;\n" +
        "            const r = el.getBoundingClientRect();\n" +
        "            const overlaps = r.left < imgRect.right && r.right > imgRect.left &&\n" +
        "                             r.top  < imgRect.bottom && r.bottom > imgRect.top;\n" +
        "            if (overlaps) el.style.setProperty('display','none','important');\n" +
        "          }\n" +
        "        }\n" +
        "      } catch(_) {}\n" +
        "    }).catch(()=>{});\n" +
        "    await page.waitForTimeout(400);\n" +
        "    const buf = await page.screenshot({ fullPage: false });\n" +
        "    let prodUrl = null;\n" +
        "    try {\n" +
        // 1. Amazon-specific: authoritative product image selector
        "      prodUrl = await page.evaluate(() => {\n" +
        "        const el = document.querySelector('#landingImage,#imgTagWrapperId img,[data-old-hires],[data-a-hires]');\n" +
        "        if (!el) return null;\n" +
        "        return el.getAttribute('data-old-hires') || el.getAttribute('data-a-hires') || el.src || null;\n" +
        "      });\n" +
        // 2. Shopify / WooCommerce product image selectors
        "      if (!prodUrl) {\n" +
        "        prodUrl = await page.evaluate(() => {\n" +
        "          const sel = [\n" +
        "            '.product__media img', '.product-single__photo img', '.product-featured-img',\n" +
        "            '.woocommerce-product-gallery__image img', '.wp-post-image',\n" +
        "            '[data-product-featured-image]', '.product-image-main img',\n" +
        "            'img.product__image', '.featured-image img'\n" +
        "          ];\n" +
        "          for (const s of sel) {\n" +
        "            const el = document.querySelector(s);\n" +
        "            if (el) return el.getAttribute('data-src') || el.currentSrc || el.src || null;\n" +
        "          }\n" +
        "          return null;\n" +
        "        });\n" +
        "      }\n" +
        // 3. Best DOM image — skip logos, pick largest product-looking image
        "      if (!prodUrl) {\n" +
        "        const skipPat = /logo|sprite|icon|banner|no_img|placeholder|avatar|pixel|tracking|qr|wechat|barcode|scan|coupon|badge|related|similar|also|bought|header|footer|nav/i;\n" +
        "        const preferPat = /product|item|variant|main|hero|pdp|full|photo|img|cdn|media|shop|listing/i;\n" +
        "        const candidates = await page.evaluate(() =>\n" +
        "          Array.from(document.querySelectorAll('img'))\n" +
        "            .map(img => ({ src: img.currentSrc || img.src || img.getAttribute('data-src') || '', natW: img.naturalWidth, natH: img.naturalHeight }))\n" +
        "            .filter(i => i.src && i.natW >= 300 && i.natH >= 300)\n" +
        "            .sort((a,b) => (b.natW*b.natH) - (a.natW*a.natH))\n" +
        "        );\n" +
        "        const filtered = candidates.filter(c => !skipPat.test(c.src));\n" +
        "        const pool = filtered.length ? filtered : candidates;\n" +
        "        const best = pool.find(c => { const r=c.natW/c.natH; return r>0.5 && r<2.0 && preferPat.test(c.src); })\n" +
        "                  || pool.find(c => { const r=c.natW/c.natH; return r>0.5 && r<2.0; }) || pool[0];\n" +
        "        if (best) prodUrl = best.src;\n" +
        "      }\n" +
        // 4. og:image as last resort — only if it looks product-specific (not a logo)
        "      if (!prodUrl) {\n" +
        "        const ogUrl = await page.evaluate(() => { const og = document.querySelector('meta[property=\"og:image\"],meta[name=\"og:image\"]'); return og ? og.getAttribute('content') : null; });\n" +
        "        const logoLike = /logo|brand|icon|favicon|banner|header|default|placeholder/i;\n" +
        "        if (ogUrl && !logoLike.test(ogUrl)) prodUrl = ogUrl;\n" +
        "      }\n" +
        "    } catch(_) {}\n" +
        "    process.stdout.write(JSON.stringify({ ss: buf.toString('base64'), prodUrl }));\n" +
        "  } catch(e) { process.stderr.write(e.message); process.exit(1); }\n" +
        "  finally { await browser.close(); }\n" +
        "})();\n";

    var scriptFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pwshot_{Guid.NewGuid():N}.cjs");
    await System.IO.File.WriteAllTextAsync(scriptFile, script);

    try
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "node",
            ArgumentList           = { scriptFile },
            WorkingDirectory       = playwrightDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc = System.Diagnostics.Process.Start(psi)!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        // Hard 35-second timeout — kills the node process if it hangs
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(35));
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already dead */ }
            log.Add("Warning", "Headless screenshot timed out — process killed", "35s limit");
            return (null!, null);
        }
        finally { try { System.IO.File.Delete(scriptFile); } catch { /* non-fatal */ } }
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (proc.ExitCode != 0 || string.IsNullOrEmpty(stdout))
        {
            log.Add("Warning", "Headless screenshot failed", (stderr + stdout)[..Math.Min(300, (stderr + stdout).Length)]);
            return (null!, null);
        }

        // Output is JSON: { "ss": "<base64>", "prodUrl": "<url> | null" }
        string screenshotB64;
        string? productImageUrl = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(stdout.Trim());
            screenshotB64   = doc.RootElement.GetProperty("ss").GetString() ?? "";
            productImageUrl = doc.RootElement.TryGetProperty("prodUrl", out var p) && p.ValueKind == System.Text.Json.JsonValueKind.String
                ? p.GetString() : null;
        }
        catch
        {
            screenshotB64 = stdout.Trim();
        }

        log.Add("Info", "Headless screenshot taken",
            $"screenshot: {screenshotB64.Length} chars; product URL: {productImageUrl ?? "none"}");
        return (screenshotB64, productImageUrl);
    }
    finally
    {
        try { System.IO.File.Delete(scriptFile); } catch { }
    }
}

static string? ExtractEbayItemId(string url)
{
    // Matches: ebay.com/itm/123456789 or ebay.com/itm/title-123456789
    var m = System.Text.RegularExpressions.Regex.Match(url,
        @"ebay\.com/itm/(?:[^/]+/)?(\d{10,13})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return m.Success ? m.Groups[1].Value : null;
}

static string ExtractPrimaryImageUrl(string html)
{
    // Try OG image first
    var m = System.Text.RegularExpressions.Regex.Match(html,
        @"<meta[^>]+(?:property|name)=""og:image""[^>]+content=""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (m.Success) return m.Groups[1].Value;
    // Try Twitter image
    m = System.Text.RegularExpressions.Regex.Match(html,
        @"<meta[^>]+name=""twitter:image""[^>]+content=""([^""]+)""",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (m.Success) return m.Groups[1].Value;
    return "";
}

// Scrapes Bing's image search results page for direct ("murl") image URLs.
// Bing renders these into the static HTML (unlike Google Images, which needs a
// headless browser), so a plain HttpClient GET is enough — no Playwright needed.
static async Task<List<string>> SearchProductImagesAsync(string query, int maxResults, IHttpClientFactory httpFactory)
{
    var urls = new List<string>();
    try
    {
        var http = httpFactory.CreateClient();
        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
        http.Timeout = TimeSpan.FromSeconds(10);

        var searchUrl = $"https://www.bing.com/images/search?q={Uri.EscapeDataString(query)}&form=HDRSC2";
        var html = await http.GetStringAsync(searchUrl);

        var skipPat = new System.Text.RegularExpressions.Regex("logo|icon|sprite|placeholder|avatar|favicon",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(html, "murl&quot;:&quot;(.*?)&quot;"))
        {
            var url = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            if (skipPat.IsMatch(url)) continue;
            if (!url.Contains(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains(".png", StringComparison.OrdinalIgnoreCase) &&
                !url.Contains(".webp", StringComparison.OrdinalIgnoreCase))
                continue;
            if (urls.Contains(url)) continue;

            urls.Add(url);
            if (urls.Count >= maxResults) break;
        }
    }
    catch { /* return whatever was found, possibly empty — caller falls back gracefully */ }
    return urls;
}

// Identifies an image's real format from its magic bytes rather than trusting the
// source URL's extension (scraped image URLs frequently have a misleading extension).
// Returns null if the bytes don't match a format Claude's vision API accepts.
static string? DetectImageMime(byte[] bytes)
{
    if (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
        return "image/png";
    if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        return "image/jpeg";
    if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
        return "image/gif";
    if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
        bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
        return "image/webp";
    return null;
}

app.MapPost("/api/improve-seo", async (ImproveSeoRequest req, ClaudeService claude, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.Title) && string.IsNullOrWhiteSpace(req.Description))
        return BadInputJson("Nothing to improve yet",
            "The SEO pass rewrites an existing title and description, and both are empty.",
            "Add a title — even a rough one — then run it again.");

    return await Guarded(FailureDomain.Ai, "AI SEO rewrite", log, async () =>
    {
        var improved = await claude.ImproveSeoAsync(req);
        log.Add("Info", "SEO improvement complete", improved.Title);
        return improved;
    });
});

app.MapPost("/api/ai-modify", async (ModifyListingRequest req, ClaudeService claude, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.Instruction))
        return BadInputJson("No instruction given",
            "This needs a sentence describing the change you want.",
            "Type what to change — for example \"make the title shorter and mention it's tested\".");

    return await Guarded(FailureDomain.Ai, "AI listing edit", log, async () =>
    {
        var modified = await claude.ModifyListingAsync(req);
        log.Add("Info", "AI modification applied", req.Instruction);
        return modified;
    });
});

app.MapPost("/api/quick-fill", async (QuickFillRequest req, ClaudeService claude, IHttpClientFactory httpFactory, IWebHostEnvironment env, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.ItemName))
        return BadInputJson("No item name typed",
            "Quick-fill works from the product's name, and the box was empty.",
            "Type what the item is — brand and model is plenty — then run it again.");

    return await Guarded(FailureDomain.Ai, "AI quick-fill", log, async () =>
    {
        log.Add("Info", "Quick-fill from item name", req.ItemName);

        // Search for product photos online and download up to 3 good ones
        var candidateUrls = await SearchProductImagesAsync(req.ItemName, 8, httpFactory);
        if (candidateUrls.Count == 0)
        {
            // Retry once with punctuation stripped — colons/dashes in the typed item
            // name occasionally return zero results from the image search.
            var simplified = System.Text.RegularExpressions.Regex.Replace(req.ItemName, @"[^\w\s]", " ");
            simplified = System.Text.RegularExpressions.Regex.Replace(simplified, @"\s+", " ").Trim();
            if (!string.IsNullOrWhiteSpace(simplified) && simplified != req.ItemName)
                candidateUrls = await SearchProductImagesAsync(simplified, 8, httpFactory);
        }

        var photosDir = System.IO.Path.Combine(env.ContentRootPath, "generated-photos");
        System.IO.Directory.CreateDirectory(photosDir);

        var savedUrls = new List<string>();
        string? firstImageBase64 = null;
        string? firstImageMime = null;

        foreach (var candidateUrl in candidateUrls)
        {
            if (savedUrls.Count >= 3) break;
            try
            {
                var http = httpFactory.CreateClient();
                http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 Chrome/120.0");
                http.Timeout = TimeSpan.FromSeconds(8);
                var imgBytes = await http.GetByteArrayAsync(candidateUrl);
                if (imgBytes.Length < 2000) continue; // skip tiny icons/tracking pixels

                // Sniff the real format from the file's magic bytes — the URL's extension
                // often lies (e.g. a ".jpg" URL that actually serves WebP), and Claude's
                // vision API rejects images whose declared media type doesn't match the bytes.
                var mime = DetectImageMime(imgBytes);
                if (mime is null) continue; // not a recognizable image format — skip it
                var ext  = mime switch { "image/png" => "png", "image/webp" => "webp", "image/gif" => "gif", _ => "jpg" };
                var file = $"search_{Guid.NewGuid():N}.{ext}";
                await System.IO.File.WriteAllBytesAsync(System.IO.Path.Combine(photosDir, file), imgBytes);
                savedUrls.Add($"/generated-photos/{file}");

                if (firstImageBase64 is null)
                {
                    firstImageBase64 = Convert.ToBase64String(imgBytes);
                    firstImageMime   = mime;
                }
            }
            catch { /* try the next candidate */ }
        }

        if (savedUrls.Count == 0)
            log.Add("Warning", "No product photos found online", req.ItemName);

        var listing = await claude.AnalyzeProductNameAsync(req.ItemName, firstImageBase64, firstImageMime);
        listing.ImageUrls = savedUrls;

        return listing;
    });
});

// ── Fees, take-home and the floor ─────────────────────────────────
// The seller's real cost of doing business, and the one calculation every price in the app is run
// through. These two endpoints are what turn the fee assumptions from a hardcoded constant into
// something the seller owns: what they save here re-prices the listing editor, market research,
// local arbitrage, lot analysis, inventory health and watcher offers at the same moment, because
// they all hold the same FeeProfile singleton.
app.MapGet("/api/fees/profile", (FeeProfile fees) => Results.Ok(FeeProfileStore.ToView(fees)));

app.MapPost("/api/fees/profile", (FeeProfileView body, FeeProfileStore store, FeeProfile fees, ActionLog log) =>
{
    if (body is null) return Results.BadRequest(new { error = "No fee settings supplied." });

    var stored = store.SaveAndApply(FeeProfileStore.FromView(body), fees);
    log.Add("Info", "Fee settings saved",
        $"{stored.RevenueFeeFraction * 100m:0.##}% of each sale, ${stored.DefaultShippingCost:0.00} shipping, "
      + $"${stored.DefaultPackagingCost:0.00} packaging, ${stored.DefaultLaborCost:0.00} handling; "
      + $"floor ${stored.MinimumNetProfit:0.00} / {stored.MinimumMarginPercent:0.##}%.");

    // The sanitized profile, not the submitted one — a rate the form let through but the math
    // could not is corrected here, and the form needs to show the corrected number.
    return Results.Ok(FeeProfileStore.ToView(stored));
});

// Net proceeds at one or more candidate prices, plus the two floors. Takes a list because a
// pricing panel shows several prices at once — the seller's ask, the comps median, a suggestion —
// and each one needs the same treatment.
app.MapPost("/api/pricing/net-quote", (NetQuoteRequest req, NetProceedsCalculator net, FeeProfile fees) =>
{
    if (req is null) return Results.BadRequest(new { error = "No pricing request supplied." });

    // Capped so a malformed client cannot turn one request into an unbounded amount of work.
    var prices = (req.Prices ?? []).Where(p => p >= 0m).Distinct().Take(12).ToList();

    var quotes = prices
        .Select(p => net.Quote(p, req.UnitCost, fees, req.BuyerPaidShipping, req.Quantity,
                               req.ShippingCost, req.OtherCosts))
        .ToList();

    // The floors do not depend on the asking price, so they are computed once from a reference
    // quote rather than read off whichever price happened to be first in the list.
    var reference = net.Quote(0m, req.UnitCost, fees, req.BuyerPaidShipping, req.Quantity,
                              req.ShippingCost, req.OtherCosts);
    var hasCost = req.UnitCost is > 0m;

    return Results.Ok(new NetQuoteResponse
    {
        Quotes = quotes,
        BreakEvenPrice = hasCost ? reference.BreakEvenPrice : null,
        MinimumOfferPrice = hasCost ? reference.MinimumOfferPrice : null,
        MinimumOfferBasis = reference.MinimumOfferBasis,
        HasCostBasis = hasCost,
        Fees = FeeProfileStore.ToView(fees),
    });
});

app.MapGet("/api/sold-comps", async (string q, decimal? cost, decimal? ask, decimal? buyerShipping,
    EbayService ebay, TerapeakService terapeak, IMarketplaceRepository marketplace,
    NetProceedsCalculator net, FeeProfile fees, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Query is required." });

    // Always hand back links to eBay's own research tools as a fallback — Marketplace Insights
    // (the real sold-comps API) requires a special eBay approval most developer accounts don't
    // have, and eBay's search page blocks scraping outright. These deep links always work because
    // they just open in the seller's own already-logged-in browser — no API access needed.
    var terapeakUrl = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60" +
                       "&keywords=" + Uri.EscapeDataString(q);
    var fallbackUrl = "https://www.ebay.com/sch/i.html?_nkw=" + Uri.EscapeDataString(q) + "&LH_Sold=1&LH_Complete=1&_sop=13";

    // What the comps are actually worth after fees. A median is a gross number, and a seller who
    // prices at the median and subtracts nothing is the exact seller this feature exists for, so
    // every sold-comps answer now carries the take-home at the median, at the average and at
    // whatever price the caller is currently considering — plus the two floors.
    object? PricingFor(decimal median, decimal average)
    {
        var shipping = Math.Max(0m, buyerShipping ?? 0m);
        var candidates = new List<(string Key, decimal Price)>();
        if (ask is > 0m) candidates.Add(("ask", ask.Value));
        if (median > 0m) candidates.Add(("median", median));
        if (average > 0m) candidates.Add(("average", average));
        if (candidates.Count == 0) return null;

        var quotes = candidates.ToDictionary(
            c => c.Key,
            c => net.Quote(c.Price, cost, fees, shipping, quantity: 1));

        var reference = quotes.Values.First();
        return new
        {
            quotes,
            hasCostBasis = cost is > 0m,
            breakEvenPrice = cost is > 0m ? reference.BreakEvenPrice : (decimal?)null,
            minimumOfferPrice = cost is > 0m ? reference.MinimumOfferPrice : (decimal?)null,
            minimumOfferBasis = reference.MinimumOfferBasis,
            fees = FeeProfileStore.ToView(fees),
        };
    }

    // Blend the local Marketplace.db sold history into the reported average at 40% weight
    // (Terapeak/Insights carries the other 60%). The local comps are NOT surfaced in the
    // response — the bar's items/count/median/min/max stay exactly as the primary source
    // returned them; only the Average value reflects the blend. If there is no local data,
    // or no primary average, the average falls back to whichever single source has data.
    async Task<decimal> BlendLocalAverageAsync(decimal primaryAverage)
    {
        try
        {
            var local  = await marketplace.SearchByKeywordAsync(q, limit: 24);
            var prices = local.Where(c => c.SoldPrice > 0m).Select(c => c.SoldPrice).ToList();
            if (prices.Count == 0) return primaryAverage;
            var localAverage = prices.Average();
            if (primaryAverage <= 0m) return Math.Round(localAverage, 2);
            return Math.Round(primaryAverage * 0.6m + localAverage * 0.4m, 2);
        }
        catch { return primaryAverage; }
    }

    // Why there is no data, when there is none. "No sold comps" and "the lookup broke" look identical
    // in an empty results panel, and they are opposite situations: one means the item genuinely has no
    // recent sales history — real, useful information for a pricing decision — and the other means the
    // app failed and the seller should not conclude anything at all from the blank panel.
    var dataNote = "";

    // 1) Real Terapeak data, if the seller has connected their session (Settings > Terapeak)
    if (terapeak.IsConnected)
    {
        try
        {
            var scrape = await terapeak.ScrapeAsync(q);
            if (scrape.Status == "ok")
            {
                var parsed = TerapeakMarketService.ParseTerapeakBodyText(scrape.BodyText, q);
                if (parsed is not null)
                {
                    var average = await BlendLocalAverageAsync(parsed.Average);
                    return Results.Ok(new { parsed.Query, parsed.Items, parsed.Count, Average = average, parsed.Median, parsed.Min, parsed.Max, terapeakUrl, fallbackUrl, source = "terapeak", pricing = PricingFor(parsed.Median, average) });
                }
            }
            else if (scrape.Status == "session_expired")
            {
                log.Add("Warning", "Terapeak session expired", "Reconnect in Settings.");
                dataNote = "Your Terapeak session has expired, so its sold data was not available. "
                         + "Reconnect it in Settings.";
            }
        }
        catch (Exception ex)
        {
            // Was unguarded, and this is a browser-driven scrape: a missing Node runtime or a crashed
            // headless browser threw straight past the endpoint, so pressing Research Sold Prices
            // answered with a 500 HTML page.
            var failure = FailureTranslator.Translate(ex, FailureDomain.Research);
            log.Add("Warning", "Terapeak lookup failed", $"{failure.Kind} — {failure.Technical}");
            dataNote = "Terapeak could not be reached for this search, so its sold data is missing here.";
        }
    }

    // 2) Marketplace Insights API (works automatically if eBay ever approves the scope)
    try
    {
        var result = await ebay.SearchSoldCompsAsync(q);
        if (result.Count > 0)
        {
            var average = await BlendLocalAverageAsync(result.Average);
            return Results.Ok(new { result.Query, result.Items, result.Count, Average = average, result.Median, result.Min, result.Max, terapeakUrl, fallbackUrl, source = "marketplace_insights", pricing = PricingFor(result.Median, average) });
        }
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Research);
        log.Add("Warning", "Sold comps lookup failed", $"{failure.Kind} — {failure.Technical}");
        if (dataNote.Length == 0) dataNote = failure.WhatHappened + " " + failure.WhatToDo;
    }

    // 3) Links only. Still a 200 with a usable body: the eBay research links work in the seller's own
    // logged-in browser and are a real answer, not a consolation prize.
    return Results.Ok(new
    {
        query = q, items = Array.Empty<object>(), count = 0, average = 0, median = 0, min = 0, max = 0,
        terapeakUrl, fallbackUrl, source = "none", dataNote,
        // No comps, but the seller's own price still has fees on it. The take-home panel stays
        // useful on the one path where the market data is missing entirely.
        pricing = PricingFor(0m, 0m),
    });
});

// Opportunity Finder — live auctions ending soon for a keyword, ranked by estimated profit
// against recent sold comps (the local sold-history database first — free, instant, no rate
// limit — then Terapeak for anything the database doesn't cover, then Marketplace Insights as a
// last resort), and filtered by a minimum seller feedback score. A Seller username can be
// supplied instead of (or alongside) a keyword to analyze one specific seller's own listings
// rather than the open market.
app.MapGet("/api/opportunities/search", async (string? q, string? seller, string? category, string? condition,
    decimal? minPrice, decimal? maxPrice, string? listingType, bool? includeIlliquid, EbayService ebay,
    TerapeakMarketService terapeakMarket, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, LiquidityScoringConfig liquidityConfig, ActionLog log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q) && string.IsNullOrWhiteSpace(seller))
        return Results.BadRequest(new { error = "A keyword or a seller username is required." });

    OpportunitySearchResult result;
    try
    {
        result = await FindOpportunitiesAsync(q ?? "", category, condition, minPrice, maxPrice, listingType ?? "AUCTION",
            ebay, terapeakMarket, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
            feeProfile, opportunityScorer, confidenceScorer, log, seller: seller, ct: ct);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Opportunity search failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }

    var opportunities = result.Items;

    // Minimum liquidity gate — Stale/Illiquid items are excluded by default (configurable via
    // LiquidityScoringConfig.RejectStaleIlliquidByDefault), overridable per-request with
    // includeIlliquid=true. Items with no liquidity data at all (not priced from the local
    // database) are never gated here — "unknown" isn't the same as "known to be illiquid".
    var excludedIlliquidCount = 0;
    if (liquidityConfig.RejectStaleIlliquidByDefault && includeIlliquid != true)
    {
        var beforeGate = opportunities.Count;
        opportunities = opportunities.Where(x => x.LiquidityLevel != "Stale/Illiquid").ToList();
        excludedIlliquidCount = beforeGate - opportunities.Count;
        if (excludedIlliquidCount > 0)
            log.Add("Info", "Minimum liquidity gate applied", $"Excluded {excludedIlliquidCount} Stale/Illiquid result(s). Pass includeIlliquid=true to override.");
    }
    var lowestPrice = opportunities.Count > 0 ? opportunities.Min(x => x.TotalCost) : (decimal?)null;
    var pricedItems  = opportunities.Where(x => x.ProfitPercent.HasValue).ToList();
    var avgProfitPercent = pricedItems.Count > 0 ? Math.Round(pricedItems.Average(x => x.ProfitPercent!.Value), 1) : (decimal?)null;
    var best = pricedItems.OrderByDescending(x => x.ProfitPercent!.Value).FirstOrDefault();
    var bestOpportunity = best is null ? null : new { best.Title, best.Url, ProfitPercent = best.ProfitPercent, best.IsVerified, best.SellThroughUnverified };

    return Results.Ok(new
    {
        query = result.Query, marketValue = result.MarketValue, averagePrice = result.AveragePrice,
        soldSource = result.SoldSource, listingType = result.ListingType,
        count = opportunities.Count, lowestPrice, sellThroughPercent = result.SellThroughPercent, avgProfitPercent, bestOpportunity,
        excludedIlliquidCount,
        items = opportunities
    });
});

// Supplier File Analyzer — the user pastes/drops a supplier price list (or a single product
// photo) into the Opportunity Finder page. Claude vision extracts every product + wholesale
// cost. Each one goes through the same AnalyzeProductAsync pipeline the Opportunity Finder search
// uses: local market research first (real historical sold listings, no live eBay traffic, no rate
// limits), only falling through to Terapeak (rationed, cache-first) when the local database
// doesn't find anything reliable — one shared implementation, not a second copy of this logic.
app.MapPost("/api/opportunities/analyze-supplier-file", async (AnalyzeSupplierFileRequest req, ClaudeService claude,
    EbayService ebay, IMarketplaceRepository marketplace, ProductNormalizer normalizer, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    ActionLog log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest(new { error = "Image is required." });

    List<SupplierProduct> products;
    try
    {
        products = await claude.AnalyzeSupplierFileAsync(req.ImageBase64, req.MimeType);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Supplier file analysis failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }

    const int MaxProducts = 12;
    const int TerapeakRecheckLimit = 6;

    var items = new List<DropshipAnalysisItem>();
    var realScrapesUsed = 0;

    foreach (var p in products.Take(MaxProducts))
    {
        var query = string.IsNullOrWhiteSpace(p.SearchQuery) ? p.ProductName : p.SearchQuery;
        var item = new DropshipAnalysisItem
        {
            ProductName      = p.ProductName,
            SearchQuery      = query,
            WholesaleCostUsd = p.WholesaleCostUsd,
            Notes            = p.Notes,
            TerapeakUrl      = "https://www.ebay.com/sh/research?marketplace=EBAY-US&tabName=SOLD&dayRange=60&keywords=" + Uri.EscapeDataString(query)
        };

        if (string.IsNullOrWhiteSpace(query))
        {
            items.Add(item);
            continue;
        }

        var target = normalizer.Normalize(query);
        // Claude's vision-read fields fill in wherever the regex extractor (which ran inside
        // AnalyzeProductAsync's normalizer, on the same query text) didn't find anything.
        if (string.IsNullOrWhiteSpace(target.PartNumber) && !string.IsNullOrWhiteSpace(p.PartNumber)) target.PartNumber = p.PartNumber;
        if (string.IsNullOrWhiteSpace(target.Model) && !string.IsNullOrWhiteSpace(p.Model)) target.Model = p.Model;
        if (string.IsNullOrWhiteSpace(target.Brand) && !string.IsNullOrWhiteSpace(p.Brand)) target.Brand = p.Brand;

        var isCached = await priceEstimator.EstimateAsync(target, [], query, "FIXED_PRICE", allowRealTerapeakScrape: false, ct: ct)
            is { TerapeakComparableCount: > 0 };
        // (EstimateAsync's own Terapeak call is cache-only here; if it happened to warm the
        // cache it's a free hit, not a spent scrape — see TerapeakMarketService.GetAsync.)
        var allowScrape = isCached || realScrapesUsed < TerapeakRecheckLimit;
        if (allowScrape && !isCached) realScrapesUsed++;

        MarketAnalysisResult analysis;
        try
        {
            analysis = await AnalyzeProductAsync(
                query, p.WholesaleCostUsd > 0 ? p.WholesaleCostUsd : null, quantity: 1, "FIXED_PRICE",
                activeListingsAlreadyFetched: null, ebayForCompetitionFallback: ebay, allowRealTerapeakScrape: allowScrape,
                normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
                opportunityScorer, confidenceScorer, log, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Dropship pricing lookup failed", $"{query}: {ex.Message}");
            items.Add(item);
            continue;
        }

        ApplyAnalysisToDropshipItem(item, analysis);
        items.Add(item);
    }

    items = items.OrderByDescending(i => i.EstimatedProfitPercent ?? -999m).ToList();

    return Results.Ok(new DropshipAnalysisResult
    {
        Items            = items,
        ProductsExtracted = products.Count,
        ProductsPriced    = items.Count(i => i.IsVerified)
    });
});

// ── Opportunity Finder insight cards ────────────────────────────────────────
// These read from data the app has already mined (Terapeak cache, the user's own live listings)
// rather than spending a fresh scrape just because someone opened the page — see each endpoint
// for exactly what it draws on and why.

app.MapGet("/api/insights/high-sell-through", (TerapeakPriceCache cache) =>
{
    var top = cache.GetTopSellThrough(5, TimeSpan.FromDays(14));
    return Results.Ok(new
    {
        items = top.Select(t => new { category = t.Query, sellThroughPercent = t.SellThroughPercent, scrapedAtUtc = t.ScrapedAtUtc })
    });
});

// "Low competition" needs a real demand signal alongside low supply — a category nobody's
// searching for isn't an opportunity, it's just quiet. Only considers categories the app has
// already mined real Terapeak sell-through data for, then checks current active-listing counts
// via a cheap Browse API call (not Terapeak — free against the scrape budget).
app.MapGet("/api/insights/low-competition", async (EbayService ebay, TerapeakPriceCache cache) =>
{
    var candidates = cache.GetTopSellThrough(15, TimeSpan.FromDays(14));
    // Each of these is an independent Browse API round trip against a different category, so
    // they're fetched concurrently instead of one at a time — this endpoint is on a user-facing
    // request path (Opportunity Finder insight card) and serial awaits made it take up to 15x
    // as long as a single call.
    var counts = await Task.WhenAll(candidates.Select(async c => (c.Query, c.SellThroughPercent, Count: await ebay.GetActiveListingCountAsync(c.Query))));
    var scored = counts.Where(x => x.Count > 0)
        .Select(x => (Category: x.Query, x.SellThroughPercent, ActiveListings: x.Count))
        .ToList();

    var ranked = scored
        .OrderByDescending(s => s.SellThroughPercent / Math.Max(1, s.ActiveListings))
        .Take(5)
        .Select(s => new { category = s.Category, sellThroughPercent = s.SellThroughPercent, activeListings = s.ActiveListings });

    return Results.Ok(new { items = ranked });
});

// Cross-references the user's OWN active listings against cached Terapeak pricing — cache-only,
// deliberately never triggers a live scrape just from opening this page. A listing whose keyword
// hasn't been priced yet simply doesn't show a recommendation rather than guessing.
app.MapGet("/api/insights/pricing-recommendations", async (EbayService ebay, TerapeakPriceCache cache, ActionLog log) =>
{
    List<EbayListingSummary> listings;
    try { listings = await ebay.GetListingsAsync(); }
    catch (Exception ex)
    {
        log.Add("Warning", "Pricing recommendations: listing fetch failed", ex.Message);
        return Results.Ok(new { items = Array.Empty<object>(), checkedListings = 0 });
    }

    var recs = new List<(string Title, string Url, decimal CurrentPrice, decimal SuggestedPrice, decimal DeltaPercent)>();
    foreach (var listing in listings.Take(30))
    {
        if (listing.Price <= 0) continue;
        var keywords = ExtractKeywords(listing.Title, maxWords: 5);
        if (string.IsNullOrWhiteSpace(keywords)) continue;

        var cached = cache.TryGet(keywords, TimeSpan.FromHours(48));
        if (cached is null) continue;

        var soldPrice = cached.Median > 0 ? cached.Median : cached.Average;
        var netResale = soldPrice - cached.AvgShipping;
        if (netResale <= 0) continue;

        var deltaPct = Math.Round((netResale - listing.Price) / listing.Price * 100m, 1);
        if (Math.Abs(deltaPct) < 10) continue; // not worth flagging a small gap

        recs.Add((listing.Title, listing.ListingUrl, listing.Price, Math.Round(netResale, 2), deltaPct));
    }

    return Results.Ok(new
    {
        items = recs.OrderByDescending(r => Math.Abs(r.DeltaPercent)).Take(5)
            .Select(r => new { title = r.Title, listingUrl = r.Url, currentPrice = r.CurrentPrice, suggestedPrice = r.SuggestedPrice, deltaPercent = r.DeltaPercent }),
        checkedListings = listings.Count
    });
});

// General retail-seasonality knowledge, not live trend data — there's no time-series history
// yet to detect a real trend from.
// Clearly labeled as a heuristic calendar on both ends so it's never confused with the rest of
// this page's live-scraped numbers.
app.MapGet("/api/insights/seasonal-demand", () =>
{
    (int Month, string[] Categories)[] calendar =
    [
        (1,  ["fitness equipment", "planners & organizers", "snow gear clearance"]),
        (2,  ["Valentine's gifts", "jewelry", "small kitchen appliances"]),
        (3,  ["spring cleaning supplies", "gardening tools", "patio furniture"]),
        (4,  ["gardening supplies", "Easter items", "bicycles"]),
        (5,  ["graduation gifts", "outdoor furniture", "Mother's Day jewelry"]),
        (6,  ["swimwear & pool gear", "camping equipment", "Father's Day tools"]),
        (7,  ["patio & pool accessories", "camping gear", "back-to-school (early)"]),
        (8,  ["back-to-school supplies", "dorm electronics", "backpacks"]),
        (9,  ["fall decor", "hunting gear", "costumes (early)"]),
        (10, ["Halloween costumes & decor", "fall/winter clothing", "space heaters"]),
        (11, ["holiday decor", "electronics", "toys"]),
        (12, ["holiday gifts", "toys", "winter apparel"]),
    ];
    var month = DateTime.UtcNow.Month;
    var current = calendar.First(c => c.Month == month);
    var next = calendar.First(c => c.Month == (month % 12) + 1);
    return Results.Ok(new
    {
        basis = "General retail seasonality patterns, not live trend data.",
        current = new { monthName = new DateTime(2000, month, 1).ToString("MMMM"), categories = current.Categories },
        upcoming = new { monthName = new DateTime(2000, next.Month, 1).ToString("MMMM"), categories = next.Categories }
    });
});

app.MapPost("/api/terapeak/connect", (TerapeakService terapeak) =>
{
    var (started, message) = terapeak.StartLogin();
    return Results.Ok(new { started, message });
});

app.MapGet("/api/terapeak/status", (TerapeakService terapeak) =>
    Results.Ok(new { connected = terapeak.IsConnected, loginInProgress = terapeak.IsLoginInProgress, lastError = terapeak.LastLoginError }));

app.MapPost("/api/terapeak/disconnect", (TerapeakService terapeak) =>
{
    terapeak.Disconnect();
    return Results.Ok(new { connected = terapeak.IsConnected });
});

// Lets me (the assistant) inspect the real rendered page + selectors once a real session
// is connected, so ParseTerapeakBodyText can be tuned against the actual DOM/text.
app.MapGet("/api/terapeak/debug-scrape", async (string q, TerapeakService terapeak) =>
{
    var scrape = await terapeak.ScrapeAsync(q);
    return Results.Ok(scrape);
});

// ── Facebook Marketplace (local sourcing) ─────────────────────────────────────
// Same shape as the Terapeak endpoints above, because it's the same mechanism: a saved
// logged-in browser session for a site with no public search API. Search is only ever
// reached by an explicit click — nothing here is scheduled or triggered by another feature.

app.MapPost("/api/facebook/connect", (FacebookMarketplaceService facebook) =>
{
    var (started, message) = facebook.StartLogin();
    return Results.Ok(new { started, message });
});

app.MapGet("/api/facebook/status", (FacebookMarketplaceService facebook) =>
    Results.Ok(new { connected = facebook.IsConnected, loginInProgress = facebook.IsLoginInProgress, lastError = facebook.LastLoginError }));

app.MapPost("/api/facebook/disconnect", (FacebookMarketplaceService facebook) =>
{
    facebook.Disconnect();
    return Results.Ok(new { connected = facebook.IsConnected });
});

// Radius comes back snapped to one of Facebook's own dropdown values, so the UI can report
// what was actually searched rather than what was asked for.
app.MapGet("/api/facebook/search", async (string q, string? zip, int? radius, FacebookMarketplaceService facebook, CancellationToken ct) =>
{
    // Through the guard like every other local search: a disconnected account is answered without
    // launching a browser, and a browser that hangs or throws still leaves this endpoint returning
    // a result the page can render. See LocalSupplyGuard.
    var radiusMiles = radius ?? 40;
    var result = await LocalSupplyGuard.RunAsync(
        facebook, token => facebook.SearchAsync(q ?? "", zip ?? "", radiusMiles, token),
        q ?? "", zip ?? "", radiusMiles, ct: ct);

    return Results.Ok(new
    {
        result.Status, result.Query, result.ZipCode, result.RadiusMiles, result.SearchUrl,
        result.Items, result.Count, result.Min, result.Median, result.Max, result.Error, result.Retryable,
        supportedRadii = FacebookMarketplaceSelectors.SupportedRadiiMiles,
    });
});

// ── Craigslist (local sourcing, no login) ─────────────────────────────────────
// The easy source, and deliberately nothing like the Facebook one above: craigslist search
// results are public, so this is a plain HTTPS GET for an RSS feed. No account, no saved session,
// no browser, nothing to connect. See CraigslistService.

// Craigslist is organised by metro, so a zip picks a regional site (CraigslistSites). The site
// actually searched comes back in scopeLabel, and `site` overrides it — a seller on a metro
// boundary knows their own board better than a zip-prefix table does.
app.MapGet("/api/craigslist/search", async (string q, string? zip, int? radius, string? site, CraigslistService craigslist, CancellationToken ct) =>
{
    var radiusMiles = radius ?? 40;
    var result = await LocalSupplyGuard.RunAsync(
        craigslist, token => craigslist.SearchAsync(q ?? "", zip ?? "", radiusMiles, site, token),
        q ?? "", zip ?? "", radiusMiles, ct: ct);

    return Results.Ok(new
    {
        result.Status, result.Query, result.ZipCode, result.RadiusMiles, result.SearchUrl,
        result.ScopeLabel, result.Items, result.Count, result.Min, result.Median, result.Max, result.Error,
        result.Retryable,
        resolvedSite = CraigslistSites.Resolve(zip, site)?.Id ?? "",
    });
});

app.MapGet("/api/craigslist/sites", () => Results.Ok(
    CraigslistSites.All.Select(s => new { s.Id, s.Label, s.State }).OrderBy(s => s.State).ThenBy(s => s.Label)));

// ── Pluggable local supply ────────────────────────────────────────────────────
// Which sites can be searched right now, for the source picker. A source that needs connecting
// is still listed, with the reason — hiding it would just make the feature look absent.
app.MapGet("/api/local/sources", (LocalSupplySources sources, ActionLog log) =>
{
    // The source picker is the panel's front door: if this dead-ends there is nothing to tick and
    // therefore no way to search at all. Describe() reads each source's availability, which for a
    // session-based one is a file probe — cheap, but not something to let take the page down.
    try { return Results.Ok(sources.Describe()); }
    catch (Exception ex)
    {
        log.Add("Error", "Local sources unavailable", ex.Message);
        return Results.Ok(new List<LocalSupplySourceInfo>());
    }
});

// One local search across every selected site, merged into a single list. `sources` is a
// comma-separated list of ids (craigslist,facebook); omitted means everything available now.
//
// This always answers 200 with a valid body — including when every source failed. The frontend
// renders per-source status and whatever results did arrive off that body; a 500 with an HTML
// error page would reach it as a rejected fetch instead, with no results and nothing to say.
app.MapGet("/api/local/search", async (
    string q, string? zip, int? radius, string? sources, string? craigslistSite,
    LocalSupplySources registry, ActionLog log, CancellationToken ct) =>
{
    var radiusMiles = radius ?? 40;
    var results = new List<LocalSupplySearchResult>();

    try
    {
        var picked = registry.Resolve(sources);

        // Sequential, not parallel: one of these sites is searched by driving a real browser, and
        // running that alongside anything else is how a slow search becomes a stuck one.
        foreach (var source in picked)
            results.Add(await SearchLocalSourceAsync(source, q ?? "", zip ?? "", radiusMiles, craigslistSite, ct));

        return Results.Ok(LocalSupplyMerger.Merge(results, q ?? "", zip ?? "", radiusMiles));
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;   // the browser hung up; there is no one left to answer
    }
    catch (Exception ex)
    {
        // Everything per-source is already guarded (LocalSupplyGuard), so reaching here means the
        // registry or the merge itself broke. Even then the sources that did answer are returned.
        log.Add("Error", "Local search failed", ex.Message);
        return Results.Ok(FailedLocalSearch(results, q ?? "", zip ?? "", radiusMiles,
            $"The local search couldn't be completed: {ex.Message}"));
    }
});

// Whatever the search managed before it broke, in the shape the UI already renders — partial
// results beat a bare error, and a site that answered has already done its work.
static LocalSupplyMultiResult FailedLocalSearch(
    List<LocalSupplySearchResult> partial, string q, string zip, int radius, string error)
{
    var merged = LocalSupplyMerger.Merge(partial, q, zip, radius);
    merged.Error = merged.Error ?? error;
    if (merged.Status != "ok") { merged.Status = "error"; merged.Error = error; }
    return merged;
}

// The one site-specific knob in the local-sourcing feature, kept at the HTTP edge rather than
// pushed into ILocalSupplySource: craigslist is organised by metro, so a seller on a boundary
// sometimes has to name their own board (see CraigslistSites). Every other source ignores it, and
// nothing below this line knows the parameter exists.
//
// LocalSupplyGuard is what makes the loop above safe to write as a plain foreach: it bounds each
// source in time and turns any failure into a result, so one site can never fail the search.
static Task<LocalSupplySearchResult> SearchLocalSourceAsync(
    ILocalSupplySource source, string q, string zip, int radius, string? craigslistSite, CancellationToken ct) =>
    LocalSupplyGuard.RunAsync(
        source,
        token => source is CraigslistService craigslist && !string.IsNullOrWhiteSpace(craigslistSite)
            ? craigslist.SearchAsync(q, zip, radius, craigslistSite, token)
            : source.SearchAsync(q, zip, radius, token),
        q, zip, radius, ct: ct);

// The local-arbitrage ranking: the same zip/radius/keyword search as above, across every selected
// site, but every result is priced against real eBay sold data and ranked by what's left after
// fees. Deliberately a separate endpoint from the plain searches rather than a flag on them —
// this one costs a comp lookup per distinct product and can spend Terapeak scrapes, so it only
// ever runs when someone clicks the button that says so.
app.MapGet("/api/local/arbitrage", async (
    string q, string? zip, int? radius, int? maxItems, int? terapeakBudget, string? sort,
    string? sources, string? craigslistSite,
    LocalSupplySources registry, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await FindLocalArbitrageAsync(
            q ?? "", zip ?? "", radius ?? 40,
            // Bounded on both axes: the comp lookups are per-product and the scrapes are per-product
            // too, so an unbounded request would turn one click into hundreds of lookups.
            Math.Clamp(maxItems ?? 30, 1, 60), Math.Clamp(terapeakBudget ?? 5, 0, 10), sort,
            registry.Resolve(sources), craigslistSite, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc,
            profitCalc, feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // The scan is a long pipeline over several external systems, and this is the click a
        // seller waits minutes for. Whatever broke, it comes back as a rendered sentence rather
        // than a rejected fetch after a two-minute wait.
        log.Add("Error", "Local arbitrage scan failed", ex.Message);
        return Results.Ok(FailedArbitrage(q ?? "", zip ?? "", radius ?? 40,
            $"The local scan couldn't be completed: {ex.Message}"));
    }
});

static LocalArbitrageResult FailedArbitrage(string q, string zip, int radius, string error) => new()
{
    Status = "error", Query = q, ZipCode = zip, RadiusMiles = radius, Error = error,
};

// The original Facebook-only route, kept working: it predates the source picker, and silently
// changing what an existing URL searches would be worse than one line of aliasing.
app.MapGet("/api/facebook/arbitrage", async (
    string q, string? zip, int? radius, int? maxItems, int? terapeakBudget, string? sort,
    LocalSupplySources registry, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await FindLocalArbitrageAsync(
            q ?? "", zip ?? "", radius ?? 40,
            Math.Clamp(maxItems ?? 30, 1, 60), Math.Clamp(terapeakBudget ?? 5, 0, 10), sort,
            registry.Resolve(FacebookMarketplaceParser.SourceId), craigslistSite: null, marketplace, normalizer, matcher,
            priceEstimator, sellThroughCalc, profitCalc, feeProfile, opportunityScorer, confidenceScorer, terapeakMarket,
            terapeak, analyzer, log, ct);

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.Add("Error", "Local arbitrage scan failed", ex.Message);
        return Results.Ok(FailedArbitrage(q ?? "", zip ?? "", radius ?? 40,
            $"The local scan couldn't be completed: {ex.Message}"));
    }
});

// ── Roll the Dice ─────────────────────────────────────────────────────────────────────────────
// The one money feature that needs nothing from the seller — no keyword, no supplier file, no idea
// what to look for. It sweeps several CATEGORIES of the sold-comps database at once, keeps the
// products that carry a real margin and real demand, then goes to find where each can be bought
// today. `seed` is the roll: leaving it out rolls at random, and the response carries the seed to
// use for "Roll again", which advances the sweep onto categories this roll didn't touch.
app.MapGet("/api/opportunities/roll-the-dice", async (
    int? seed, int? niches, int? probes, int? maxProducts, int? maxSourced, int? terapeakBudget,
    string? zip, int? radius, string? sort, string? sources, string? craigslistSite,
    LocalSupplySources registry, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer arbitrage, JackpotHunter hunter, EbayService ebay, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await RollTheDiceAsync(
            seed ?? CategorySweep.RandomSeed(),
            // Every one of these bounds a fan-out: niches x probes comps queries, then one real
            // pricing lookup per product kept, then a supply search per product sourced. A roll has
            // to stay a click someone waits a minute or two for, not an unbounded crawl.
            Math.Clamp(niches ?? 4, 1, 8), Math.Clamp(probes ?? 2, 1, 3),
            Math.Clamp(maxProducts ?? 10, 1, 20), Math.Clamp(maxSourced ?? 5, 0, 10),
            Math.Clamp(terapeakBudget ?? 3, 0, 10),
            zip ?? "", Math.Clamp(radius ?? 40, 1, 500), sort,
            registry.Resolve(sources), craigslistSite,
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, terapeakMarket, terapeak, arbitrage, hunter, ebay, log, ct);

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // A roll spans the comps database, Terapeak, local classifieds and eBay. Whatever breaks,
        // it comes back as a sentence on the board rather than a rejected fetch after a long wait.
        log.Add("Error", "Roll the Dice failed", ex.Message);
        return Results.Ok(new JackpotResult
        {
            Status = "error", Seed = seed ?? 0, NextSeed = CategorySweep.NextSeed(seed ?? 0),
            Error = $"The scan couldn't be completed: {ex.Message}",
        });
    }
});

// ── Rising-Demand / Price-Trend Radar ─────────────────────────────────────────────────────────
// Every other pricing screen answers "what is this worth?" in the present tense. This one reads the
// sold-comps database as a time series and answers "what is on its way up?" — the products whose
// recent sold prices AND sale velocity are both rising, ranked by what getting in ahead of the move
// is worth per unit. Read-only: it prices nothing live and lists nothing.
app.MapGet("/api/trends/radar", async (
    int? seed, int? niches, int? probes, int? window, int? maxProducts, int? terapeakBudget, string? direction,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    TerapeakMarketService terapeakMarket, TerapeakService terapeak, JackpotHunter hunter, EbayService ebay,
    ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await ScanPriceTrendsAsync(
            seed ?? CategorySweep.RandomSeed(),
            // Each of these bounds a fan-out: niches x probes comps queries, then one real pricing
            // lookup per product kept. A scan has to stay a click someone waits a minute for.
            Math.Clamp(niches ?? 5, 1, 10), Math.Clamp(probes ?? 2, 1, 3),
            PriceTrendAnalyzer.ClampWindow(window ?? PriceTrendAnalyzer.DefaultWindowDays),
            Math.Clamp(maxProducts ?? 12, 1, 25), Math.Clamp(terapeakBudget ?? 3, 0, 10),
            string.Equals(direction, "all", StringComparison.OrdinalIgnoreCase) ? "all" : "rising",
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, terapeakMarket, terapeak, hunter, ebay, log, ct);

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // A scan spans the comps database, Terapeak and eBay. Whatever breaks, it comes back as a
        // sentence on the board rather than a rejected fetch after a long wait.
        log.Add("Error", "Price-trend radar failed", ex.Message);
        return Results.Ok(new TrendRadarResult
        {
            Status = "error", Seed = seed ?? 0, NextSeed = CategorySweep.NextSeed(seed ?? 0),
            Error = $"The scan couldn't be completed: {ex.Message}",
        });
    }
});

// ── Liquidation lot / manifest analyzer ───────────────────────────────────────────────────────
// A pallet, a wholesale lot or an estate lot is one decision with a lot of money on it: pay the
// ask, or walk. Paste the manifest (or photograph it), give the ask price, and this prices every
// line against real sold comps and answers it — with the max bid, and with the handful of lines
// that actually carry the value called out, because those are the ones to inspect before paying.
//
// Read-only end to end: nothing is listed, published or sent anywhere.
app.MapPost("/api/lots/analyze", async (
    LotAnalysisRequest req, ClaudeService claude, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LotAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.ManifestText) && string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest(new { error = "Paste a manifest or drop a photo of one." });

    try
    {
        var result = await AnalyzeLotAsync(
            req, claude, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
            feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);
        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (Exception ex)
    {
        // A lot analysis spans an extraction, the comps database and possibly Terapeak. Whatever
        // breaks comes back as a sentence on the page, not a rejected fetch after a long wait.
        log.Add("Error", "Lot analysis failed", ex.Message);
        return Results.Ok(new LotAnalysisResult
        {
            Status = "error",
            Error = $"The lot couldn't be analyzed: {ex.Message}",
            Verdict = "no_data",
            VerdictNote = "The analysis did not finish, so there is no verdict.",
        });
    }
});

// The grade table, so the UI renders the picker (and its recovery assumptions) from one source of
// truth rather than a second copy hardcoded in HTML.
app.MapGet("/api/lots/grades", () => Results.Ok(new { grades = LotAnalyzer.Grades }));

// ── Inventory health ──────────────────────────────────────────────────────────────────────────
// Everything else in this app looks forward at inventory the seller has not bought yet. This looks
// backward at what they already own and are already paying to hold.
app.MapGet("/api/inventory/health", async (
    int? maxItems, int? terapeakBudget, int? minDays,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    InventoryHealthAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    var result = await ScanInventoryHealthAsync(
        // Same bounding as the arbitrage scan and for the same reason: one click fans out into a
        // comp lookup per distinct product, so both axes are capped.
        Math.Clamp(maxItems ?? 120, 1, 400), Math.Clamp(terapeakBudget ?? 5, 0, 15), Math.Max(0, minDays ?? 0),
        ebay, costBasis, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
        feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);

    return Results.Ok(result);
});

// Cost basis — the one number in the whole profit calculation that eBay cannot supply.
app.MapGet("/api/inventory/cost-basis", (CostBasisStore store) => Results.Ok(store.GetAll()));

app.MapPost("/api/inventory/cost-basis", (List<CostBasisEntry> entries, CostBasisStore store, ActionLog log) =>
{
    try
    {
        var saved = store.SaveMany(entries ?? []);
        log.Add("Info", $"Cost basis saved for {saved} listing(s)", "");
        return Results.Ok(new { saved });
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});

app.MapDelete("/api/inventory/cost-basis", (string? listingId, string? sku, CostBasisStore store) =>
    Results.Ok(new { deleted = store.Delete(listingId, sku) }));

// Applies recommended prices to LIVE eBay listings.
//
// Three separate brakes, because this is the only endpoint in the app that changes money on
// listings that are already published and visible to buyers:
//   1. It previews by default. `dryRun` has to be explicitly false.
//   2. `confirmed` has to be true on top of that — the same posture /api/listing/update takes with
//      ManualRevisionConfirmed.
//   3. Every price is re-checked against the break-even the server recomputes, not the one the
//      browser sent. A client that asks to sell at a loss is refused unless the seller explicitly
//      opted into that, which is then recorded in the action log.
app.MapPost("/api/inventory/reprice", async (
    RepriceRequest req, EbayService ebay, CostBasisStore costBasis, FeeProfile feeProfile,
    ProfitCalculator profitCalc, ActionLog log) =>
{
    var items = req?.Items ?? [];
    var dryRun = req is null || req.DryRun || !req.Confirmed;
    var result = new RepriceResult { DryRun = dryRun, Requested = items.Count };

    if (items.Count == 0) return Results.Ok(result);
    if (items.Count > 100)
        return Results.BadRequest("Too many listings in one repricing request — 100 at a time is the limit.");

    var allCosts = costBasis.GetAll();

    foreach (var item in items)
    {
        var row = new RepriceItemResult
        {
            ListingId = item.ListingId, Title = item.Title,
            OldPrice = item.CurrentPrice, NewPrice = item.NewPrice,
            ChangePercent = item.CurrentPrice > 0
                ? Math.Round((item.NewPrice - item.CurrentPrice) / item.CurrentPrice * 100m, 1) : 0m,
        };
        result.Items.Add(row);

        if (string.IsNullOrWhiteSpace(item.ListingId))
        {
            row.Status = "skipped";
            row.Message = "No eBay listing ID — this listing can't be revised.";
            result.Skipped++;
            continue;
        }

        if (item.NewPrice <= 0)
        {
            row.Status = "skipped";
            row.Message = "A price of zero or less was requested.";
            result.Skipped++;
            continue;
        }

        // Recomputed here rather than trusted from the request: the floor is the whole safety
        // property of this endpoint, and a value that arrived over HTTP is not one.
        var cost = CostBasisStore.Find(allCosts, item.ListingId, item.Sku);
        if (cost is not null && !(req?.AllowBelowBreakEven ?? false))
        {
            var breakEven = profitCalc.Calculate(
                supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: item.NewPrice,
                quickSalePrice: item.NewPrice, buyerPaidShipping: 0m, fees: feeProfile).BreakEvenSalePrice;

            if (breakEven != decimal.MaxValue && item.NewPrice < breakEven)
            {
                row.Status = "skipped";
                row.Message = $"${item.NewPrice:0.00} is below the ${breakEven:0.00} break-even on a ${cost.TotalUnitCost:0.00} cost basis.";
                result.Skipped++;
                continue;
            }
        }

        if (dryRun)
        {
            row.Status = "preview";
            row.Message = $"Would change ${item.CurrentPrice:0.00} → ${item.NewPrice:0.00}.";
            continue;
        }

        try
        {
            await ebay.ReviseInventoryStatusAsync(item.ListingId, item.NewPrice, Math.Max(1, item.Quantity));
            row.Status = "applied";
            row.Message = $"Repriced ${item.CurrentPrice:0.00} → ${item.NewPrice:0.00}.";
            result.Applied++;
            log.Add("Info", "Listing repriced on eBay",
                $"{item.ListingId} \"{item.Title}\": ${item.CurrentPrice:0.00} → ${item.NewPrice:0.00}" +
                ((req?.AllowBelowBreakEven ?? false) ? " (break-even check overridden by the seller)" : ""));
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Reprice failed", $"{item.ListingId}: {ex.Message}");
        }
    }

    return Results.Ok(result);
});

// ── Offers to watchers ────────────────────────────────────────────────────────────────────────
// The warmest audience a seller ever gets for free: people who found the item, looked at it, and
// told eBay to remember it. This finds them, sizes a private discount that still clears the
// seller's profit floor, and sends it — without moving the public price a cent.
app.MapGet("/api/offers/watchers", async (
    int? maxItems, int? minWatchers, decimal? minProfit, int? terapeakBudget,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    InventoryHealthAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    // With no explicit ask, the floor is the one the seller already set in Fees & Costs rather
    // than zero — a seller who has said "never under $10 profit" should not have to say it again
    // on the screen that is actively handing out discounts.
    var floorProfit = Math.Max(0m, minProfit ?? feeProfile.MinimumNetProfit);
    var watchers = Math.Clamp(minWatchers ?? 1, 0, 500);

    // The board is the inventory-health scan seen from a different angle, so it reuses that scan
    // whole rather than re-deriving market price, break-even and cost basis a second way. The only
    // difference is which listings are worth the lookups: watched ones, biggest audience first.
    var health = await ScanInventoryHealthAsync(
        Math.Clamp(maxItems ?? 120, 1, 400), Math.Clamp(terapeakBudget ?? 3, 0, 15), minDays: 0,
        ebay, costBasis, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
        feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct,
        minWatchers: watchers, watchersFirst: true);

    if (health.Status == "ebay_unavailable")
        return Results.Ok(new WatcherOfferResult { Status = "ebay_unavailable", Error = health.Error });

    // eBay's own answer to "can I send an offer on this right now". Asked once for the whole
    // account, not per listing.
    List<string>? eligibleIds = null;
    var needsReconnect = false;
    var eligibilityNote = "";
    try
    {
        eligibleIds = await ebay.GetOfferEligibleListingIdsAsync();
        if (eligibleIds is null)
            eligibilityNote = "eBay's eligibility list couldn't be read this scan, so the offers below are unverified — a send may still be refused.";
    }
    catch (EbayPermissionException ex)
    {
        // The watcher counts and the suggested offers are all still true and worth showing; only
        // the sending is blocked, and the fix is one click.
        needsReconnect = true;
        eligibilityNote = ex.Message;
        log.Add("Warning", "Offers to watchers: eBay permission missing", ex.Message);
    }
    catch (Exception ex)
    {
        eligibilityNote = "eBay's eligibility list couldn't be read this scan, so the offers below are unverified — a send may still be refused.";
        log.Add("Warning", "find_eligible_items failed (non-fatal)", ex.Message);
    }

    var eligibleSet = eligibleIds is null ? null : new HashSet<string>(eligibleIds, StringComparer.OrdinalIgnoreCase);

    var rows = health.Items.Select(h =>
    {
        bool? eligible = eligibleSet is null ? null : eligibleSet.Contains(h.ListingId);
        var note = eligible == false
            ? "eBay isn't offering this one to watchers right now — offers need a fixed-price listing with stock, and eBay blocks a repeat offer for a while after one goes out."
            : "";
        return WatcherOfferAdvisor.Build(h, eligible, note, floorProfit, feeProfile);
    }).ToList();

    var result = new WatcherOfferResult
    {
        ActiveListings = health.ActiveListings,
        ItemsAnalyzed = health.ItemsAnalyzed,
        ProductsPriced = health.ProductsPriced,
        TerapeakScrapesUsed = health.TerapeakScrapesUsed,
        EligibilityChecked = eligibleSet is not null,
        EligibilityNote = eligibilityNote,
        NeedsReconnect = needsReconnect,
        DataWarning = health.DataWarning,
        MinNetProfit = floorProfit,
        DefaultMessage = WatcherOfferAdvisor.DefaultMessage,
        Items = WatcherOfferAdvisor.Rank(rows),
    };
    result.Summary = WatcherOfferAdvisor.Summarize(result.Items);

    log.Add("Info", "Offers-to-watchers scan",
        $"Watched listings: {result.ItemsAnalyzed} of {result.ActiveListings} active; " +
        $"Watchers: {result.Summary.TotalWatchers}; Ready to send: {result.Summary.ReadyToSend} " +
        $"reaching {result.Summary.WatchersReachable} watcher(s); Blocked by floor: {result.Summary.BlockedByFloor}; " +
        $"Not eligible: {result.Summary.NotEligible}");

    return Results.Ok(result);
});

// Sends the offers. The same three brakes the repricer uses, because this is the other endpoint in
// the app that puts a buyer-visible number on a live listing:
//   1. It previews by default — `dryRun` has to be explicitly false.
//   2. `confirmed` has to be true on top of that.
//   3. Every offer price is re-checked against a floor the SERVER recomputes from the stored cost
//      basis, not the one the browser sent. Selling under it takes a deliberate opt-in, which is
//      then on the record in the action log.
app.MapPost("/api/offers/send", async (
    SendWatcherOffersRequest req, EbayService ebay, CostBasisStore costBasis, FeeProfile feeProfile,
    ProfitCalculator profitCalc, ActionLog log) =>
{
    var items = req?.Items ?? [];
    var dryRun = req is null || req.DryRun || !req.Confirmed;
    var result = new SendWatcherOffersResult { DryRun = dryRun, Requested = items.Count };

    if (items.Count == 0) return Results.Ok(result);
    if (items.Count > 50)
        return Results.BadRequest("Too many listings in one send — 50 at a time is the limit.");

    var message = WatcherOfferAdvisor.CleanMessage(req?.Message);
    // Same default as the board that produced these rows — see /api/offers/watchers.
    var minProfit = Math.Max(0m, req?.MinNetProfit ?? feeProfile.MinimumNetProfit);
    var allCosts = costBasis.GetAll();

    foreach (var item in items)
    {
        var discount = item.DiscountPercent;
        var offerPrice = WatcherOfferAdvisor.OfferPriceFor(item.ListPrice, discount);
        var row = new SendWatcherOfferResultItem
        {
            ListingId = item.ListingId, Title = item.Title, DiscountPercent = discount,
            ListPrice = item.ListPrice, OfferPrice = offerPrice, WatchCount = item.WatchCount,
        };
        result.Items.Add(row);

        if (string.IsNullOrWhiteSpace(item.ListingId))
        {
            row.Status = "skipped";
            row.Message = "No eBay listing ID — no offer can be sent for this one.";
            result.Skipped++;
            continue;
        }

        if (item.ListPrice <= 0m)
        {
            row.Status = "skipped";
            row.Message = "This listing has no price to discount.";
            result.Skipped++;
            continue;
        }

        if (discount < WatcherOfferAdvisor.EbayMinDiscountPercent || discount > WatcherOfferAdvisor.MaxDiscountPercent)
        {
            row.Status = "skipped";
            row.Message = $"{discount}% is outside the {WatcherOfferAdvisor.EbayMinDiscountPercent}–{WatcherOfferAdvisor.MaxDiscountPercent}% range — eBay won't carry a smaller offer, and a deeper one needs a repricing decision.";
            result.Skipped++;
            continue;
        }

        // Recomputed here rather than trusted from the request: the floor is the safety property of
        // this endpoint, and a number that arrived over HTTP is not one.
        var cost = CostBasisStore.Find(allCosts, item.ListingId, item.Sku);
        if (cost is not null && !(req?.AllowBelowFloor ?? false))
        {
            var breakEven = profitCalc.Calculate(
                supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: offerPrice,
                quickSalePrice: offerPrice, buyerPaidShipping: 0m, fees: feeProfile).BreakEvenSalePrice;

            var floor = breakEven == decimal.MaxValue
                ? null
                : WatcherOfferAdvisor.ProfitFloorPrice(breakEven, minProfit, feeProfile);

            if (floor is decimal f && offerPrice < f)
            {
                row.Status = "skipped";
                row.Message = minProfit > 0m
                    ? $"${offerPrice:0.00} leaves less than the ${minProfit:0.00} profit you asked to keep (floor ${f:0.00} on a ${cost.TotalUnitCost:0.00} cost)."
                    : $"${offerPrice:0.00} is below the ${f:0.00} break-even on a ${cost.TotalUnitCost:0.00} cost basis.";
                result.Skipped++;
                continue;
            }
        }

        if (dryRun)
        {
            row.Status = "preview";
            row.Message = $"Would offer {discount}% off — ${item.ListPrice:0.00} → ${offerPrice:0.00}" +
                          (item.WatchCount > 0 ? $" to {item.WatchCount} watcher{(item.WatchCount == 1 ? "" : "s")}." : ".");
            continue;
        }

        try
        {
            var offerId = await ebay.SendOfferToWatchersAsync(
                item.ListingId, discount, message, Math.Max(1, item.Quantity), req?.AllowCounterOffer ?? true);
            row.Status = "sent";
            row.OfferId = offerId;
            row.Message = $"Offered {discount}% off (${offerPrice:0.00})" +
                          (item.WatchCount > 0 ? $" to {item.WatchCount} watcher{(item.WatchCount == 1 ? "" : "s")}." : ".");
            result.Sent++;
            result.WatchersReached += Math.Max(0, item.WatchCount);
            log.Add("Info", "Offer sent to watchers",
                $"{item.ListingId} \"{item.Title}\": {discount}% off, ${item.ListPrice:0.00} → ${offerPrice:0.00}" +
                ((req?.AllowBelowFloor ?? false) ? " (profit floor overridden by the seller)" : ""));
        }
        catch (EbayPermissionException ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Offer send blocked by missing eBay permission", ex.Message);
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Offer send failed", $"{item.ListingId}: {ex.Message}");
        }
    }

    return Results.Ok(result);
});

// ── Promoted Listings ROI ─────────────────────────────────────────────────────────────────────
// eBay's suggested ad rate is computed from what the rest of the category pays. It does not know
// what the seller paid for the item, so it will suggest a rate bigger than the margin and call it a
// recommendation. These two endpoints answer the other half: what each rate costs per sale, how
// much extra volume it has to buy to pay for itself, and the rate that ends the month with the most
// money. Read-only — nothing here changes a campaign on eBay.

// One listing, straight from the editor: no eBay connection, no scan, just the economics on screen.
app.MapPost("/api/promoted/advise", (
    PromotedAdviceRequest req, PromotedListingAdvisor advisor, FeeProfile fees) =>
{
    if (req is null) return Results.BadRequest(new { error = "No listing supplied." });
    if (req.Price <= 0m) return Results.BadRequest(new { error = "A price is required to size an ad rate." });

    var advice = advisor.Build(new PromotedListingAdvisor.Input(
        Title: req.Title ?? "",
        ListPrice: req.Price,
        UnitCost: req.UnitCost,
        BuyerPaidShipping: Math.Max(0m, req.BuyerPaidShipping),
        ShippingCostOverride: req.ShippingCost,
        Category: req.Category ?? "",
        CategoryRateOverride: req.CategoryRatePercent,
        // No explicit rate means "what the app already assumes on every net figure it shows",
        // which is the rate the seller set in Fees & Costs.
        CurrentRatePercent: req.CurrentRatePercent ?? fees.PromotedListingRatePercent,
        SalesPerMonth: req.SalesPerMonth,
        DaysListed: req.DaysListed,
        WatchCount: Math.Max(0, req.WatchCount),
        QuantitySold: Math.Max(0, req.QuantitySold),
        SoldCompCount: Math.Max(0, req.SoldCompCount),
        MarketPrice: req.MarketPrice,
        PriceGapPercent: req.MarketPrice is > 0m
            ? Math.Round((req.Price - req.MarketPrice.Value) / req.MarketPrice.Value * 100m, 1)
            : null), fees);

    return Results.Ok(advice);
});

// The whole board. Reuses the inventory-health scan rather than re-deriving market price, cost
// basis and break-even a second way — the ad rate is a different question asked of the same facts,
// exactly as the offers-to-watchers board is.
app.MapGet("/api/promoted/board", async (
    int? maxItems, int? terapeakBudget, decimal? currentRate,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    InventoryHealthAnalyzer analyzer, PromotedListingAdvisor advisor, ActionLog log, CancellationToken ct) =>
{
    var health = await ScanInventoryHealthAsync(
        Math.Clamp(maxItems ?? 120, 1, 400), Math.Clamp(terapeakBudget ?? 3, 0, 15), minDays: 0,
        ebay, costBasis, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
        feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);

    if (health.Status == "ebay_unavailable")
        return Results.Ok(new PromotedBoardResult { Status = "ebay_unavailable", Error = health.Error });

    // What the seller says they are paying today. eBay exposes no API for a listing's live ad rate,
    // so this is the app-wide assumption from Fees & Costs unless the board overrides it — and the
    // UI says which, rather than presenting a guess as a reading.
    var rate = Math.Clamp(currentRate ?? feeProfile.PromotedListingRatePercent, 0m, 100m);

    var rows = health.Items.Select(h => advisor.Build(new PromotedListingAdvisor.Input(
        Title: h.Title,
        ListPrice: h.ListPrice,
        UnitCost: h.CostBasis,
        Category: h.Category,
        CurrentRatePercent: rate,
        SalesPerMonth: h.SalesPerMonth,
        DaysListed: h.DaysListed,
        WatchCount: h.WatchCount,
        QuantitySold: h.QuantitySold,
        SoldCompCount: h.SoldCompCount + h.TerapeakCompCount,
        LiquidityScore: h.LiquidityScore,
        LiquidityLevel: h.LiquidityLevel,
        MarketPrice: h.MarketPrice,
        PriceGapPercent: h.PriceGapPercent,
        MarketComparable: h.MarketComparable,
        ListingId: h.ListingId,
        Sku: h.Sku,
        Url: h.Url,
        ImageUrl: h.ImageUrl), feeProfile)).ToList();

    var result = new PromotedBoardResult
    {
        ActiveListings = health.ActiveListings,
        ItemsAnalyzed = health.ItemsAnalyzed,
        ProductsPriced = health.ProductsPriced,
        TerapeakScrapesUsed = health.TerapeakScrapesUsed,
        DataWarning = health.DataWarning,
        DefaultRatePercent = feeProfile.PromotedListingRatePercent,
        ComparedRatePercent = rate,
        Items = PromotedListingAdvisor.Rank(rows),
    };
    result.Summary = PromotedListingAdvisor.Summarize(result.Items);

    log.Add("Info", "Promoted Listings ROI scan",
        $"Listings: {result.ItemsAnalyzed} of {result.ActiveListings} active; assumed rate {rate:0.##}%; " +
        $"Under-promoted: {result.Summary.UnderPromoted}; Over-promoted: {result.Summary.OverPromoted}; " +
        $"Shouldn't promote: {result.Summary.ShouldNotPromote}; " +
        $"Blended recommendation: {result.Summary.BlendedRecommendedPercent:0.#}%");

    return Results.Ok(result);
});

// The published category norms, for the picker in the advisor panel.
app.MapGet("/api/promoted/categories", () => Results.Ok(new
{
    categories = PromotedRateNorms.All(),
    minRatePercent = PromotedRateNorms.EbayMinimumRatePercent,
    maxRecommendedPercent = PromotedRateNorms.MaxRecommendedRatePercent,
}));

// Pulls the seller's live eBay listings, prices each against the same sold-comps + Terapeak stack
// every other screen uses, and works out what each should be priced at to actually sell.
//
// Rationed exactly like FindLocalArbitrageAsync, for the same reason — one click over a 300-listing
// inventory would otherwise be 300 comp lookups:
//   * comp lookups are per distinct PRODUCT, not per listing (three copies of the same drill are
//     one lookup), grouped on the normalized signature Terapeak also caches on, and
//   * pass 1 is cache-only, so anything Terapeak already knows is free; pass 2 spends the scrape
//     budget on the listings where the most money hangs on getting the number right.
static async Task<InventoryHealthResult> ScanInventoryHealthAsync(
    int maxItems, int terapeakBudget, int minDays,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    InventoryHealthAnalyzer analyzer, ActionLog log, CancellationToken ct,
    // Set by the offers-to-watchers board, which cares about a different slice of the same
    // inventory: only listings with an audience, biggest audience first.
    int minWatchers = 0, bool watchersFirst = false)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nowUtc = DateTime.UtcNow;
    var result = new InventoryHealthResult { TerapeakConnected = terapeak.IsConnected };

    List<EbayListingSummary> listings;
    try
    {
        listings = await ebay.GetListingsAsync();
    }
    catch (Exception ex)
    {
        // eBay not connected, or the token expired. Reported, never silently retried — reconnecting
        // is the seller's decision, made in Settings.
        log.Add("Warning", "Inventory health scan could not read eBay listings", ex.Message);
        return new InventoryHealthResult { Status = "ebay_unavailable", Error = ex.Message };
    }

    var active = listings
        .Where(l => l.Status is "ACTIVE" or "PUBLISHED" || string.IsNullOrWhiteSpace(l.Status))
        .Where(l => l.Price > 0 && !string.IsNullOrWhiteSpace(l.Title))
        .ToList();
    result.ActiveListings = active.Count;

    if (minDays > 0)
        active = active.Where(l => InventoryHealthAnalyzer.DaysListed(l.StartTimeUtc, nowUtc) >= minDays).ToList();

    if (minWatchers > 0)
        active = active.Where(l => l.WatchCount >= minWatchers).ToList();

    // Highest asking price first when the cap bites: if only part of the inventory can be scanned
    // in one pass, the part holding the most money is the part worth scanning. For the offers
    // board the same logic points at watchers instead — a listing nobody is watching cannot be
    // offered to anyone, however expensive it is.
    var scanned = (watchersFirst
            ? active.OrderByDescending(l => l.WatchCount).ThenByDescending(l => l.Price * Math.Max(1, l.Quantity))
            : active.OrderByDescending(l => l.Price * Math.Max(1, l.Quantity)))
        .Take(maxItems).ToList();
    result.ItemsAnalyzed = scanned.Count;
    if (scanned.Count == 0) return result;

    // One lookup per product signature, shared by every listing that resolves to it — and it is
    // Terapeak's own cache key, so two differently-worded listings for the same item share both
    // the group and any cached scrape.
    var groups = new Dictionary<string, (string LookupTitle, List<EbayListingSummary> Listings)>(StringComparer.OrdinalIgnoreCase);
    var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
    // Units per sale, read off the title by the same normalizer the pricing stack uses. Sold comps
    // are per-unit, so a "Lot of 20" listing has no like-for-like market price and the analyzer
    // refuses to recommend one rather than comparing $3,000 against a single miner.
    var lotQtyOf = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var listing in scanned)
    {
        var identity = normalizer.Normalize(listing.Title);
        lotQtyOf[listing.ListingId] = Math.Max(1, identity.Quantity);
        var key = TerapeakMarketService.BuildCacheKey(identity);
        if (string.IsNullOrWhiteSpace(key)) key = listing.Title.Trim().ToLowerInvariant();
        keyOf[listing.ListingId] = key;

        if (!groups.TryGetValue(key, out var group)) group = (listing.Title, []);
        group.Listings.Add(listing);
        // The fullest title in the group does the lookup — the comp matcher can only work with the
        // words it is given, and eBay titles for the same item vary wildly in detail.
        if (listing.Title.Length > group.LookupTitle.Length) group = (listing.Title, group.Listings);
        groups[key] = group;
    }
    result.ProductsPriced = groups.Count;

    async Task<ResalePricing> PriceAsync(string lookupTitle, bool allowScrape)
    {
        var analysis = await AnalyzeProductAsync(
            lookupTitle, supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: null, ebayForCompetitionFallback: null,
            allowRealTerapeakScrape: allowScrape,
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);
        return ResalePricing.From(analysis, lookupTitle);
    }

    // ── Pass 1: sold-comps database plus whatever Terapeak already has cached. No scrapes. ──────
    var pricing = new Dictionary<string, ResalePricing>(StringComparer.OrdinalIgnoreCase);
    var cached = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, group) in groups)
    {
        cached[key] = await terapeakMarket.GetAsync(
            normalizer.Normalize(group.LookupTitle), group.LookupTitle, allowRealScrape: false, ct: ct) is not null;
        pricing[key] = await PriceAsync(group.LookupTitle, allowScrape: false);
    }

    // ── Pass 2: spend the scrape budget where the most money hangs on the answer ────────────────
    if (terapeak.IsConnected && terapeakBudget > 0)
    {
        var targets = InventoryHealthAnalyzer.SelectScrapeTargets(
            groups.Select(g =>
            {
                var priced = pricing[g.Key];
                var listedValue = g.Value.Listings.Sum(l => l.Price * Math.Max(1, l.Quantity));
                // Dollars at stake, not percent wrong: a 40% gap on a $12 item is not worth a page
                // load and a 16% gap on a $1,400 one is.
                var atStake = priced.HasPrice
                    ? g.Value.Listings.Sum(l => Math.Abs(l.Price - (priced.ExpectedSale ?? priced.Median)!.Value) * Math.Max(1, l.Quantity))
                    : listedValue;
                return (g.Key, DollarsAtStake: (decimal?)atStake, HasTerapeak: cached[g.Key], Unpriced: !priced.HasPrice);
            }), terapeakBudget);

        foreach (var key in targets)
        {
            pricing[key] = await PriceAsync(groups[key].LookupTitle, allowScrape: true);
            result.TerapeakScrapesUsed++;
        }
    }

    // ── Judge ───────────────────────────────────────────────────────────────────────────────────
    var costs = costBasis.GetAll();
    var rows = scanned
        .Select(l => analyzer.Build(
            l, pricing[keyOf[l.ListingId]], CostBasisStore.Find(costs, l.ListingId, l.Sku),
            feeProfile, nowUtc, lotQtyOf[l.ListingId]))
        .ToList();

    result.Items = InventoryHealthAnalyzer.Rank(rows);
    result.Summary = InventoryHealthAnalyzer.Summarize(result.Items);

    if (result.Items.All(r => r.MarketPrice is null))
    {
        result.SoldCompsConfigured = await SoldCompsReachableAsync(marketplace, ct);
        result.DataWarning = (result.SoldCompsConfigured, terapeak.IsConnected) switch
        {
            (false, false) => "No eBay sold-price source is available — connect Terapeak in Settings, or configure the sold-comps database, to price your inventory.",
            (true, _) => "The sold-comps database had no history for any of these listings. Connecting Terapeak would add a second source.",
            (false, true) => "Terapeak is connected but returned no sold history for these listings.",
        };
    }
    else
    {
        result.SoldCompsConfigured = result.Items.Any(r => r.SoldCompCount > 0);
    }

    sw.Stop();
    log.Add("Info", "Inventory health scan",
        $"Active: {result.ActiveListings}; Analyzed: {result.ItemsAnalyzed} across {result.ProductsPriced} product(s); " +
        $"Terapeak scrapes: {result.TerapeakScrapesUsed}; Stale (90d+): {result.Summary.StaleCount}; " +
        $"Overpriced: {result.Summary.OverpricedCount}; Reprice candidates: {result.Summary.RepriceCandidates}; " +
        $"Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// Local arbitrage: search local supply on every selected site, price every result against real
// eBay sold data, rank by net profit after fees. Reuses AnalyzeProductAsync (and therefore the
// hosted sold-comps database, ComparableMatcher, MarketPriceEstimator and Terapeak) rather than
// pricing items a second way — a local flip is worth exactly what a dropship of the same item is
// worth, and a Craigslist flip is worth exactly what a Facebook one is.
//
// Source-pluggable: everything below the search loop is written against ILocalSupplySource and
// LocalSupplyListing, so Craigslist, Facebook and whatever comes next go through one pipeline and
// land in one ranked table. Products are grouped ACROSS sources, which is where that pays off —
// the same drill listed on both sites costs one comp lookup, not two.
//
// Two things are rationed on purpose, because one click here fans out into many lookups:
//   * comp lookups are per distinct PRODUCT, not per listing (five listings of the same drill are
//     one lookup), and
//   * real Terapeak scrapes only ever happen in the second pass, only for the products that pass
//     LocalArbitrageAnalyzer.SelectScrapeTargets, and only up to terapeakBudget. Pass 1 is
//     cache-only, so a product Terapeak already knows about costs nothing.
static async Task<LocalArbitrageResult> FindLocalArbitrageAsync(
    string q, string zip, int radius, int maxItems, int terapeakBudget, string? sort,
    IReadOnlyList<ILocalSupplySource> sources, string? craigslistSite,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // Sequential: one of these sources drives a real browser, and running that concurrently with
    // anything else turns a slow search into a stuck one.
    var searches = new List<LocalSupplySearchResult>();
    foreach (var source in sources)
        searches.Add(await SearchLocalSourceAsync(source, q, zip, radius, craigslistSite, ct));

    var search = LocalSupplyMerger.Merge(searches, q, zip, radius);

    var result = new LocalArbitrageResult
    {
        Status = search.Status, Query = search.Query, ZipCode = search.ZipCode,
        // Echoed from the response rather than the request: Facebook snaps a radius to its own
        // dropdown values, so this reports what was actually searched.
        RadiusMiles = searches.FirstOrDefault()?.RadiusMiles ?? radius,
        SearchUrl = searches.FirstOrDefault(s => s.Status == "ok")?.SearchUrl ?? searches.FirstOrDefault()?.SearchUrl ?? "",
        Error = search.Error,
        Sources = search.Sources,
        LocalListingsFound = search.Count, TerapeakConnected = terapeak.IsConnected,
    };
    // not_connected / session_expired / error pass straight through so the UI can show the same
    // connect prompt the plain local search shows, instead of an empty ranking table. With
    // several sources this only happens when NONE of them answered — see RollUpStatus.
    if (search.Status != "ok" || search.Count == 0) return result;

    // The search is done and its results are already on the result object. Everything below is the
    // pricing half — sold-comps lookups, Terapeak, the profit maths — and it reaches across two
    // more external systems. If any of that breaks, the seller still gets the local listings that
    // were found and a sentence saying pricing is what failed, rather than a dead scan.
    try
    {
        // A listing with no parseable price has no cost basis, so there is no profit to compute for
        // it. "Free" is kept — it's the best possible cost basis, not a missing one. The cap is shared
        // out across sources rather than applied to one flat cheapest-first list, which would spend
        // the whole budget on whichever site returned the most rows.
        var priceable = LocalSupplyMerger.TakeBalanced(
            search.Items.Where(i => i.Price is > 0 || i.IsFree), maxItems);
        result.ItemsAnalyzed = priceable.Count;

        // The normalized brand/model/spec signature, which is also Terapeak's cache key — so two
        // differently-worded tiles for the same product share both the group and the cached scrape.
        var groups = LocalArbitrageAnalyzer.GroupByProduct(
            priceable, l => TerapeakMarketService.BuildCacheKey(normalizer.Normalize(l.Title)));
        result.ProductsPriced = groups.Count;

        async Task<ResalePricing> PriceAsync(LocalArbitrageGroup group, bool allowScrape)
        {
            var analysis = await AnalyzeProductAsync(
                group.LookupTitle, supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
                activeListingsAlreadyFetched: null, ebayForCompetitionFallback: null,
                allowRealTerapeakScrape: allowScrape,
                normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
                opportunityScorer, confidenceScorer, log, ct);
            return ResalePricing.From(analysis, group.LookupTitle);
        }

        // ── Pass 1: sold-comps database + whatever Terapeak already has cached. No scrapes. ─────────
        var pricing = new Dictionary<string, ResalePricing>();
        var cached = new Dictionary<string, bool>();
        foreach (var group in groups)
        {
            // Same cache-only pre-check the Opportunity Finder uses: a product Terapeak already knows
            // must not consume the scrape budget, and this costs one SQLite read, never a page load.
            cached[group.Key] = await terapeakMarket.GetAsync(
                normalizer.Normalize(group.LookupTitle), group.LookupTitle, allowRealScrape: false, ct: ct) is not null;
            pricing[group.Key] = await PriceAsync(group, allowScrape: false);
        }

        // ── Pass 2: spend the scrape budget on the products where it changes a decision ─────────────
        if (terapeak.IsConnected && terapeakBudget > 0)
        {
            var byKey = groups.ToDictionary(g => g.Key);
            var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
                groups.Select(g =>
                {
                    // The group's best buy is what decides whether the group is worth a scrape;
                    // a free listing is the cheapest there is, not a missing price.
                    var cheapest = g.Listings.OrderBy(l => l.IsFree ? 0m : l.Price ?? decimal.MaxValue).First();
                    var preliminary = pricing[g.Key].HasPrice
                        ? analyzer.Build(cheapest, pricing[g.Key], feeProfile).NetProfit
                        : null;
                    return (g.Key, PreliminaryProfit: preliminary, HasTerapeak: cached[g.Key], LocalAsk: g.LowestAsk);
                }), terapeakBudget);

            foreach (var key in targets)
            {
                pricing[key] = await PriceAsync(byKey[key], allowScrape: true);
                result.TerapeakScrapesUsed++;
            }
        }

        // ── Rank ────────────────────────────────────────────────────────────────────────────────────
        var rows = groups
            .SelectMany(g => g.Listings.Select(l => analyzer.Build(l, pricing[g.Key], feeProfile)))
            .ToList();

        result.Items = LocalArbitrageAnalyzer.Rank(rows, sort);
        result.GoldmineCount = result.Items.Count(r => r.Verdict == "goldmine");
        // The rows a seller working off a fixed pot of cash can actually run: profitable AND back in
        // the bank inside three weeks.
        result.FastCashCount = result.Items.Count(r => r.NetProfit is > 0 && r.SpeedTier == "fast");
        // What the whole board is worth if every profitable listing on it were bought and flipped —
        // an upper bound on the search, not a forecast.
        result.TotalPotentialProfit = Math.Round(result.Items.Where(r => r.NetProfit is > 0).Sum(r => r.NetProfit!.Value), 2);

        if (result.Items.All(r => r.EbayExpectedSale is null))
        {
            result.SoldCompsConfigured = await SoldCompsReachableAsync(marketplace, ct);
            result.DataWarning = (result.SoldCompsConfigured, terapeak.IsConnected) switch
            {
                (false, false) => "No eBay sold-price source is available — connect Terapeak in Settings, or configure the sold-comps database, to price these locally.",
                (true, _) => "The sold-comps database had no history for any of these titles. Connecting Terapeak would add a second source.",
                (false, true) => "Terapeak is connected but returned no sold history for these titles.",
            };
        }
        else
        {
            result.SoldCompsConfigured = result.Items.Any(r => r.SoldCompCount > 0);
        }
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.Add("Error", "Local arbitrage pricing failed",
            $"\"{q}\": found {result.LocalListingsFound} local listing(s) but couldn't price them — {ex.Message}");
        // Status stays "ok": the sites answered, and their per-source counts are real. What failed
        // was the resale half, and saying that is more useful than reporting the whole scan dead.
        result.DataWarning =
            $"Found {result.LocalListingsFound} local listing(s), but pricing them against eBay sold data failed: {ex.Message}";
        return result;
    }

    sw.Stop();
    log.Add("Info", "Local arbitrage scan",
        $"\"{q}\" within {result.RadiusMiles} mi{(string.IsNullOrWhiteSpace(zip) ? "" : $" of {zip}")} " +
        $"on {string.Join(" + ", sources.Select(s => s.Id))}; " +
        $"Local listings: {result.LocalListingsFound}; Analyzed: {result.ItemsAnalyzed} across " +
        $"{result.ProductsPriced} product(s); Terapeak scrapes: {result.TerapeakScrapesUsed}; " +
        $"Goldmines: {result.GoldmineCount}; Fast cash (<={DaysToCashEstimator.FastCashDays}d): {result.FastCashCount}; " +
        $"Sorted by: {LocalArbitrageAnalyzer.NormalizeSort(sort)}; Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// Roll the Dice: the cross-category sweep. Every other money feature starts from something the
// seller supplies — a keyword, a supplier file, their own listings. This one starts from nothing and
// answers "what should I be selling, and where do I buy it?".
//
// Four phases, each one bounded, cheapest first:
//   1. SWEEP — a window of categories (see CategorySweep) is mined out of the sold-comps database
//      with a couple of keyword probes each. Cheap: the comps lookup is one HTTP call (hosted) or
//      one indexed read (local) per probe, and it costs no eBay quota and no Terapeak scrape.
//   2. SCREEN — those sold LISTINGS are clustered into PRODUCTS and screened on evidence alone
//      (JackpotHunter.Screen): accessories, lots, broken-item comps, sub-fee prices, dead demand
//      and clusters too vague to be one product are dropped, with the reason kept.
//   3. PRICE — only what survives gets a real per-product lookup through AnalyzeProductAsync, the
//      same pipeline the Opportunity Finder and Local Deals use, so the identity guard, the
//      local/Terapeak blend, sell-through and the confidence score are the ones already trusted
//      elsewhere. Terapeak scrapes are rationed exactly as they are for a local scan.
//   4. SOURCE — for the products with the most headroom, go and find actual supply: eBay Buy It Now
//      capped at the break-even price, plus local classifieds when a zip is given. Every buy is
//      costed by LocalArbitrageAnalyzer, so a jackpot is a goldmine by the same definition.
static async Task<JackpotResult> RollTheDiceAsync(
    int seed, int nicheCount, int probesPerNiche, int maxProducts, int maxSourced, int terapeakBudget,
    string zip, int radius, string? sort, IReadOnlyList<ILocalSupplySource> localSources, string? craigslistSite,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    TerapeakMarketService terapeakMarket, TerapeakService terapeak, LocalArbitrageAnalyzer arbitrage,
    JackpotHunter hunter, EbayService ebay, ActionLog log, CancellationToken ct)
{
    // Rows kept per probe before clustering. Set to the candidate ceiling both repositories fetch
    // anyway (500 — see HostedMarketplaceClient.CandidateLimit / MarketplaceRepository's
    // SqlCandidateLimit), so this costs nothing extra on the wire and stops the clustering being
    // starved: a product only earns a place on the board with several sold comps behind it, and a
    // narrower haul just splits the same history across more one-comp clusters.
    const int compsPerProbe = 500;
    // Buy-side listings looked at per product, and how many of the cheapest are kept as options.
    const int supplyLookLimit = 20;
    const int supplyKeepPerSource = 3;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nowUtc = DateTime.UtcNow;

    var result = new JackpotResult
    {
        Seed = seed, NextSeed = CategorySweep.NextSeed(seed),
        RollsToCoverEverything = CategorySweep.RollsToCoverEverything(nicheCount),
        NichesInUniverse = CategorySweep.Universe.Count,
        ZipCode = zip, RadiusMiles = radius, TerapeakConnected = terapeak.IsConnected,
    };

    // ── 1 + 2: sweep the categories, cluster and screen ─────────────────────────────────────────
    // Keyed by product signature so the same product surfacing under two probes is one candidate,
    // not two — the whole sweep costs one pricing lookup for it either way.
    var candidates = new Dictionary<string, JackpotCandidate>(StringComparer.OrdinalIgnoreCase);

    foreach (var niche in CategorySweep.Select(seed, nicheCount))
    {
        var probes = CategorySweep.ProbesFor(niche, seed, probesPerNiche);
        var outcome = new JackpotNicheOutcome { Id = niche.Id, Label = niche.Label, Probes = probes };

        foreach (var probe in probes)
        {
            IReadOnlyList<MarketplaceComparableResult> rows;
            try
            {
                rows = await marketplace.SearchByKeywordAsync(probe, filters: null, limit: compsPerProbe, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One category failing to read is not the roll failing — the others still answer.
                outcome.Note = $"Sold-comps lookup failed: {ex.Message}";
                log.Add("Warning", "Roll the Dice comps lookup failed", $"\"{probe}\": {ex.Message}");
                continue;
            }

            outcome.CompsScanned += rows.Count;
            result.CompsScanned += rows.Count;

            foreach (var candidate in JackpotHunter.Cluster(rows, niche.Id, niche.Label, probe, nowUtc))
            {
                var (keep, reason) = JackpotHunter.Screen(candidate, normalizer.Normalize(candidate.LookupTitle));
                if (!keep)
                {
                    candidate.RejectReason = reason;
                    outcome.ProductsScreenedOut++;
                    continue;
                }

                outcome.ProductsFound++;
                if (!candidates.TryGetValue(candidate.Key, out var existing) || candidate.Score > existing.Score)
                    candidates[candidate.Key] = candidate;
            }
        }

        outcome.Note ??= outcome.CompsScanned == 0
            ? "No sold history for this category in the comps database."
            : outcome.ProductsFound == 0
                ? $"{outcome.ProductsScreenedOut} product(s) seen, none with enough evidence to price."
                : null;
        result.Niches.Add(outcome);
    }

    result.ProductsConsidered = candidates.Count;

    // Most money on the table first, so a cap that bites cuts the least promising products.
    var shortlist = candidates.Values.OrderByDescending(c => c.Score).Take(maxProducts).ToList();

    // ── 3: price what survived ──────────────────────────────────────────────────────────────────
    // Priced on the product's short keyword, not on the raw comp title it came from. A sold title
    // carries a seller's punctuation, condition notes and shouting, and asking a comps lookup for
    // "Bitmain Antminer S19j Pro 104TH | Tested & Verified + PSU" narrows it to nothing — the same
    // five words that make a usable classifieds search make a usable comp query.
    async Task<ResalePricing> PriceAsync(JackpotCandidate candidate, bool allowScrape)
    {
        var analysis = await AnalyzeProductAsync(
            JackpotHunter.ShoppingQuery(candidate.LookupTitle),
            supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: null,
            // One cheap Browse count per product (limit=1, no scrape budget) — without an active
            // count there is no sell-through denominator, and "days to cash" is half the pitch here.
            ebayForCompetitionFallback: ebay,
            allowRealTerapeakScrape: allowScrape,
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);
        return ResalePricing.From(analysis, candidate.LookupTitle);
    }

    var pricing = new Dictionary<string, ResalePricing>(StringComparer.OrdinalIgnoreCase);
    var terapeakCached = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var candidate in shortlist)
    {
        // Cache-only pre-check, same as every other scan: a product Terapeak already knows about
        // must not consume the scrape budget, and this costs one SQLite read, never a page load.
        terapeakCached[candidate.Key] = await terapeakMarket.GetAsync(
            normalizer.Normalize(candidate.LookupTitle), candidate.LookupTitle, allowRealScrape: false, ct: ct) is not null;
        pricing[candidate.Key] = await PriceAsync(candidate, allowScrape: false);
    }
    result.ProductsPriced = shortlist.Count;

    if (terapeak.IsConnected && terapeakBudget > 0)
    {
        var byKey = shortlist.ToDictionary(c => c.Key, StringComparer.OrdinalIgnoreCase);
        // Break-even buy price stands in for "how much hangs on this answer" — it IS the headroom
        // the whole play is made of, so the products with the most of it get the live re-check.
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            shortlist.Select(c => (
                c.Key,
                PreliminaryProfit: pricing[c.Key].HasPrice ? hunter.BreakEvenBuyPrice(pricing[c.Key], feeProfile) : (decimal?)null,
                HasTerapeak: terapeakCached[c.Key],
                LocalAsk: c.MedianSold)), terapeakBudget);

        foreach (var key in targets)
        {
            pricing[key] = await PriceAsync(byKey[key], allowScrape: true);
            result.TerapeakScrapesUsed++;
        }
    }

    // ── 4: go and find where each one can actually be bought ────────────────────────────────────
    var sourceOptions = new Dictionary<string, List<JackpotSourceOption>>(StringComparer.OrdinalIgnoreCase);
    var localOutcomes = new Dictionary<string, LocalSupplySourceOutcome>(StringComparer.OrdinalIgnoreCase);

    // Only products with real history behind them, and whose two independent reads of that history
    // agree, are worth searching supply for — see the same two checks below, which drop everything
    // else off the board rather than showing it with a hedge.
    var believable = shortlist
        .Where(c => JackpotHunter.HasEnoughHistoryToShow(pricing[c.Key])
                 && JackpotHunter.EstimateAgreesWithSweep(c.MedianSold, pricing[c.Key]))
        .ToList();

    var sourcingTargets = believable
        .OrderByDescending(c => hunter.BreakEvenBuyPrice(pricing[c.Key], feeProfile))
        .Take(maxSourced)
        .ToList();

    foreach (var candidate in sourcingTargets)
    {
        var resale = pricing[candidate.Key];
        var breakEven = hunter.BreakEvenBuyPrice(resale, feeProfile);
        if (breakEven <= 0) continue;   // nothing to buy it for — no point searching for supply

        var query = JackpotHunter.ShoppingQuery(candidate.LookupTitle);
        var options = new List<JackpotSourceOption>();

        // The buy-side identity guard: a keyword search for a $140 robot vacuum under $118 returns
        // filters, mop pads and roller brushes, all of which would price as spectacular flips. Every
        // listing below has to be credibly the product itself before a cent of profit is booked
        // against it (see JackpotHunter.IsPlausibleSupply).
        var priceFloor = JackpotHunter.SupplyPriceFloor(resale);

        JackpotSourceOption? Consider(LocalSupplyListing listing)
        {
            var buyPrice = listing.Price ?? 0m;
            var (plausible, reason) = JackpotHunter.IsPlausibleSupply(
                listing.Title, buyPrice, normalizer.Normalize(listing.Title), candidate.LookupTitle, priceFloor);
            if (plausible) return JackpotSourceOption.From(arbitrage.Build(listing, resale, feeProfile));

            result.SupplyRejected++;
            log.Add("Info", "Roll the Dice supply listing rejected",
                $"\"{listing.Title}\" ({listing.Source}, {buyPrice:C}) — {reason}.");
            return null;
        }

        // eBay Buy It Now, capped at the break-even price: what comes back is supply that could
        // clear its own fees, rather than the whole market for the product.
        try
        {
            var items = await ebay.SearchEndingSoonAsync(
                query, minFeedback: 0, limit: supplyLookLimit, category: null, condition: null,
                minPrice: null, maxPrice: Math.Round(breakEven, 2), listingType: "FIXED_PRICE");
            result.EbaySupplySearched = true;

            // Guarded first, THEN the cheapest kept: taking the three cheapest first would fill the
            // list with accessories and leave the real unit unlooked-at.
            foreach (var option in items.Where(i => i.Price > 0)
                         .OrderBy(i => i.Price + i.ShippingCost)
                         .Select(i => Consider(JackpotHunter.AsSupplyListing(i)))
                         .Where(o => o is not null)
                         .Take(supplyKeepPerSource))
            {
                options.Add(option!);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Roll the Dice eBay supply search failed", $"\"{query}\": {ex.Message}");
        }

        // Local classifieds. Only with a zip — every source resolves its search area from one, and
        // a nationwide classifieds search isn't a thing you can drive to.
        if (!string.IsNullOrWhiteSpace(zip))
        {
            foreach (var source in localSources)
            {
                var search = await SearchLocalSourceAsync(source, query, zip, radius, craigslistSite, ct);

                // Rolled up across every product searched, so the board can say which sites were
                // actually reachable — an empty option list has to be distinguishable from a site
                // that never answered.
                if (!localOutcomes.TryGetValue(source.Id, out var rollup))
                {
                    localOutcomes[source.Id] = LocalSupplySourceOutcome.From(search);
                }
                else
                {
                    rollup.Count += search.Count;
                    if (search.Status == "ok") { rollup.Status = "ok"; rollup.Error = null; }
                }

                foreach (var option in search.Items
                             .Where(i => i.Price is > 0)
                             .OrderBy(i => i.Price!.Value)
                             .Select(Consider)
                             .Where(o => o is not null)
                             .Take(supplyKeepPerSource))
                {
                    options.Add(option!);
                }
            }
        }

        sourceOptions[candidate.Key] = options;
        result.ProductsSourced++;
    }

    result.Sources = localOutcomes.Values.ToList();

    // ── The board ───────────────────────────────────────────────────────────────────────────────
    var plays = new List<JackpotPlay>();
    foreach (var candidate in shortlist)
    {
        var resale = pricing[candidate.Key];

        // Two gates, and both of them drop the product entirely rather than showing it with a hedge:
        //   * too little sold history to price it at all (a two-comp estimate makes the resale price,
        //     the break-even AND the supply price floor meaningless), and
        //   * the sweep's own cluster median disagreeing with the per-product estimate, which means
        //     one of them matched a different product — this is what stops a $150 robot vacuum being
        //     advertised as a $500 one because a keyword tier matched the whole category.
        if (!JackpotHunter.HasEnoughHistoryToShow(resale))
        {
            result.ProductsDropped++;
            log.Add("Info", "Roll the Dice product dropped",
                $"\"{candidate.LookupTitle}\": {resale.SoldCompCount + resale.TerapeakCompCount} sold comp(s) — " +
                "not enough history to price it.");
            continue;
        }

        if (!JackpotHunter.EstimateAgreesWithSweep(candidate.MedianSold, resale))
        {
            result.ProductsDropped++;
            log.Add("Info", "Roll the Dice product dropped",
                $"\"{candidate.LookupTitle}\": sweep median {candidate.MedianSold:C} vs per-product " +
                $"estimate {(resale.ExpectedSale ?? resale.Median):C} — too far apart to trust either.");
            continue;
        }

        plays.Add(hunter.BuildPlay(
            candidate, resale,
            sourceOptions.TryGetValue(candidate.Key, out var options) ? options : [], feeProfile));
    }

    result.Plays = JackpotHunter.Rank(plays, sort);
    result.JackpotCount = result.Plays.Count(p => p.Tier == "jackpot");
    result.FastCashCount = result.Plays.Count(p => p.SpeedTier == "fast" && p.Tier is not ("pass" or "no_data"));
    // Every profitable buy currently on the board added up — an upper bound if you bought them all,
    // not a forecast, and it only ever counts supply that really exists right now.
    result.TotalPotentialProfit = Math.Round(
        result.Plays.SelectMany(p => p.Sources).Where(o => o.NetProfit is > 0).Sum(o => o.NetProfit!.Value), 2);
    foreach (var outcome in result.Niches)
        outcome.PlaysFound = result.Plays.Count(p => p.NicheId == outcome.Id);

    result.SoldCompsConfigured = result.Plays.Any(p => p.SoldCompCount > 0);

    if (result.Plays.Count == 0)
    {
        result.Status = result.CompsScanned == 0 ? "no_comps" : "ok";
        var reachable = await SoldCompsReachableAsync(marketplace, ct);
        result.SoldCompsConfigured = reachable;
        result.DataWarning = result.CompsScanned == 0
            ? reachable
                ? "The sold-comps database answered but has no history for the categories in this roll — roll again to sweep different ones."
                : "No eBay sold-price source is available — configure the sold-comps database, or connect Terapeak in Settings, so a roll has real sold prices to mine."
            : "This roll found sold history but nothing that cleared the evidence bar. Roll again — the sweep moves on to different categories each time.";
    }
    else if (!result.Plays.Any(p => p.HasLiveSupply) && string.IsNullOrWhiteSpace(zip))
    {
        result.DataWarning = "No local classifieds were searched because no zip code was given — add one and roll again " +
                             "to price real local supply instead of eBay alone.";
    }

    sw.Stop();
    log.Add("Info", "Roll the Dice",
        $"Seed {seed}; Categories: {string.Join(" + ", result.Niches.Select(n => n.Id))}; " +
        $"Comps scanned: {result.CompsScanned}; Products considered: {result.ProductsConsidered}; " +
        $"Priced: {result.ProductsPriced}; Dropped: {result.ProductsDropped}; " +
        $"Sourced: {result.ProductsSourced}; Supply rejected: {result.SupplyRejected}; " +
        $"Terapeak scrapes: {result.TerapeakScrapesUsed}; Plays: {result.Plays.Count} " +
        $"(jackpots: {result.JackpotCount}, fast cash: {result.FastCashCount}); " +
        $"Profit on the board: {result.TotalPotentialProfit:C}; Sorted by: {LocalArbitrageAnalyzer.NormalizeSort(sort)}; " +
        $"Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// The Rising-Demand / Price-Trend Radar. Same sweep Roll the Dice uses, read along a different
// axis: instead of "which of these carries a margin right now", it asks "which of these is worth
// MORE than it was, and selling more often than it was".
//
// Four phases, cheapest first, and the order matters — the whole point is that the trend read is
// free (it's arithmetic over comps already fetched) and the expensive pricing lookup is only spent
// on products that already showed a rise:
//   1. SWEEP    — a window of categories mined out of the sold-comps database. One query per probe.
//   2. BASELINE — the scan reads its own data first: how fresh it is, and how its own volume moved
//                 between the two windows. Both are refusals waiting to happen (see
//                 PriceTrendAnalyzer.BuildCorpus), and both are the difference between a trend
//                 radar and a random-number generator.
//   3. MEASURE  — every screened product's dated comps split into two windows and compared. Free.
//   4. PRICE    — only what is actually rising gets a real per-product lookup through
//                 AnalyzeProductAsync, so the resale price, the confidence score and the fee model
//                 are the ones the rest of the app already stands behind.
static async Task<TrendRadarResult> ScanPriceTrendsAsync(
    int seed, int nicheCount, int probesPerNiche, int windowDays, int maxProducts, int terapeakBudget,
    string direction,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    TerapeakMarketService terapeakMarket, TerapeakService terapeak, JackpotHunter hunter, EbayService ebay,
    ActionLog log, CancellationToken ct)
{
    // Same ceiling both repositories fetch anyway, so this costs nothing extra on the wire. A trend
    // needs MORE history than a price does — two windows of it — so starving the haul here would
    // turn every product into "not enough dated sales to compare".
    const int compsPerProbe = 500;

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nowUtc = DateTime.UtcNow;

    var result = new TrendRadarResult
    {
        Seed = seed, NextSeed = CategorySweep.NextSeed(seed),
        RollsToCoverEverything = CategorySweep.RollsToCoverEverything(nicheCount),
        NichesInUniverse = CategorySweep.Universe.Count,
        WindowDays = windowDays, Direction = direction,
        TerapeakConnected = terapeak.IsConnected,
    };

    // ── 1: sweep, keeping the ROWS this time, not just the summary ──────────────────────────────
    // Deduped globally by item id: the same sale coming back under two probes would otherwise count
    // twice in that product's velocity AND twice in the scan-wide baseline it's measured against.
    var seenComps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var compsByKey = new Dictionary<string, List<MarketplaceComparableResult>>(StringComparer.OrdinalIgnoreCase);
    var allComps = new List<MarketplaceComparableResult>();
    var candidates = new Dictionary<string, JackpotCandidate>(StringComparer.OrdinalIgnoreCase);

    foreach (var niche in CategorySweep.Select(seed, nicheCount))
    {
        var probes = CategorySweep.ProbesFor(niche, seed, probesPerNiche);
        var outcome = new TrendNicheOutcome { Id = niche.Id, Label = niche.Label, Probes = probes };

        foreach (var probe in probes)
        {
            IReadOnlyList<MarketplaceComparableResult> rows;
            try
            {
                rows = await marketplace.SearchByKeywordAsync(probe, filters: null, limit: compsPerProbe, ct: ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // One category failing to read is not the scan failing — the others still answer.
                outcome.Note = $"Sold-comps lookup failed: {ex.Message}";
                log.Add("Warning", "Price-trend comps lookup failed", $"\"{probe}\": {ex.Message}");
                continue;
            }

            var fresh = new List<MarketplaceComparableResult>(rows.Count);
            foreach (var row in rows)
            {
                var id = string.IsNullOrWhiteSpace(row.ItemId)
                    ? $"{row.Title}|{row.SoldPrice}|{row.SoldDate:yyyy-MM-dd}"
                    : row.ItemId;
                if (!seenComps.Add(id)) continue;
                fresh.Add(row);
            }

            outcome.CompsScanned += fresh.Count;
            result.CompsScanned += fresh.Count;
            allComps.AddRange(fresh);

            // Grouped on JackpotHunter's own product signature, so a product is the same product
            // here as it is on the Roll the Dice board — one definition of "a product", not two.
            foreach (var comp in fresh)
            {
                if (string.IsNullOrWhiteSpace(comp.Title) || comp.SoldPrice <= 0) continue;
                var (key, _) = JackpotHunter.ProductSignature(comp.Title);
                if (string.IsNullOrWhiteSpace(key)) continue;
                if (!compsByKey.TryGetValue(key, out var bucket)) compsByKey[key] = bucket = [];
                bucket.Add(comp);
            }

            foreach (var candidate in JackpotHunter.Cluster(fresh, niche.Id, niche.Label, probe, nowUtc))
            {
                // The same screen the sweep board uses: accessories, multi-unit lots, broken-item
                // comps, sub-fee prices and clusters too wide to be one product are dropped here.
                // A "price rise" measured across a cluster that isn't one product is a mix shift.
                var (keep, _) = JackpotHunter.Screen(candidate, normalizer.Normalize(candidate.LookupTitle));
                if (!keep) { outcome.ProductsScreenedOut++; continue; }

                outcome.ProductsFound++;
                if (!candidates.TryGetValue(candidate.Key, out var existing) || candidate.Score > existing.Score)
                    candidates[candidate.Key] = candidate;
            }
        }

        outcome.Note ??= outcome.CompsScanned == 0
            ? "No sold history for this category in the comps database."
            : outcome.ProductsFound == 0
                ? $"{outcome.ProductsScreenedOut} product(s) seen, none with enough evidence to measure."
                : null;
        result.Niches.Add(outcome);
    }

    result.ProductsConsidered = candidates.Count;

    // ── 2: the baseline, before a single product is judged ──────────────────────────────────────
    result.Corpus = PriceTrendAnalyzer.BuildCorpus(allComps, nowUtc, windowDays);

    if (!result.Corpus.IsReadable)
    {
        // Refusing the whole scan is the honest answer here. A comps database that stopped being
        // updated makes every product on earth look like demand collapsed, and a database with no
        // dates makes every product look flat — neither is a fact about any market.
        result.Status = result.CompsScanned == 0 ? "no_comps" : "stale_data";
        result.SoldCompsConfigured = result.CompsScanned > 0 || await SoldCompsReachableAsync(marketplace, ct);
        result.DataWarning = result.Corpus.Note ?? (result.SoldCompsConfigured
            ? "The sold-comps database answered but has no dated history for these categories — scan again to sweep different ones."
            : "No eBay sold-price source is available — configure the sold-comps database in Settings so a scan has real sold dates to read.");
        sw.Stop();
        log.Add("Warning", "Price-trend radar refused",
            $"Seed {seed}; Comps: {result.CompsScanned}; Dated: {result.Corpus.DatedComps}; " +
            $"Newest: {result.Corpus.NewestCompAgeDays?.ToString() ?? "n/a"} days; {result.Corpus.Note}");
        return result;
    }

    // ── 3: measure every screened product — free, so everything gets measured ───────────────────
    var measured = new List<(JackpotCandidate Candidate, PriceTrendReading Trend)>();
    foreach (var candidate in candidates.Values)
    {
        if (!compsByKey.TryGetValue(candidate.Key, out var comps)) continue;
        var trend = PriceTrendAnalyzer.Measure(comps, nowUtc, windowDays, result.Corpus);
        result.ProductsMeasured++;
        if (trend.IsRising) result.ProductsRising++;
        if (direction == "rising" && !trend.IsRising) continue;
        measured.Add((candidate, trend));
    }

    // Biggest dollar move first, confirmed readings ahead of tentative ones — a cap that bites has
    // to cut the weakest evidence, not the alphabetically unlucky.
    var shortlist = measured
        .OrderBy(m => m.Trend.Reliability == "confirmed" ? 0 : 1)
        .ThenByDescending(m => Math.Abs(m.Trend.PriceChangeAmount ?? 0m) * m.Trend.Recent.SoldCount)
        .ThenByDescending(m => m.Trend.PriceChangePercent ?? 0m)
        .Take(maxProducts)
        .ToList();

    // ── 4: price only what moved ────────────────────────────────────────────────────────────────
    // Priced on the product's short keyword, not the raw comp title it came from — a sold title
    // carries a seller's punctuation and shouting, and narrows a comp lookup to nothing.
    async Task<ResalePricing> PriceAsync(JackpotCandidate candidate, bool allowScrape)
    {
        var analysis = await AnalyzeProductAsync(
            JackpotHunter.ShoppingQuery(candidate.LookupTitle),
            supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: null,
            ebayForCompetitionFallback: ebay,
            allowRealTerapeakScrape: allowScrape,
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);
        return ResalePricing.From(analysis, candidate.LookupTitle);
    }

    var pricing = new Dictionary<string, ResalePricing>(StringComparer.OrdinalIgnoreCase);
    var terapeakCached = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var (candidate, _) in shortlist)
    {
        // Cache-only pre-check, same as every other scan: a product Terapeak already knows about
        // must not consume the scrape budget, and this costs one SQLite read, never a page load.
        terapeakCached[candidate.Key] = await terapeakMarket.GetAsync(
            normalizer.Normalize(candidate.LookupTitle), candidate.LookupTitle, allowRealScrape: false, ct: ct) is not null;
        pricing[candidate.Key] = await PriceAsync(candidate, allowScrape: false);
    }
    result.ProductsPriced = shortlist.Count;

    if (terapeak.IsConnected && terapeakBudget > 0)
    {
        var byKey = shortlist.ToDictionary(m => m.Candidate.Key, m => m.Candidate, StringComparer.OrdinalIgnoreCase);
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            shortlist.Select(m => (
                m.Candidate.Key,
                PreliminaryProfit: pricing[m.Candidate.Key].HasPrice
                    ? hunter.BreakEvenBuyPrice(pricing[m.Candidate.Key], feeProfile) : (decimal?)null,
                HasTerapeak: terapeakCached[m.Candidate.Key],
                LocalAsk: m.Candidate.MedianSold)), terapeakBudget);

        foreach (var key in targets)
        {
            pricing[key] = await PriceAsync(byKey[key], allowScrape: true);
            result.TerapeakScrapesUsed++;
        }
    }

    // ── The board ───────────────────────────────────────────────────────────────────────────────
    var board = new List<TrendRadarRow>();
    foreach (var (candidate, trend) in shortlist)
    {
        var resale = pricing[candidate.Key];

        // The same two gates the sweep board uses, and both drop the product rather than hedging:
        // too little sold history to price at all, or the sweep's own cluster median disagreeing
        // with the per-product estimate — which means one of them matched a different product.
        if (!JackpotHunter.HasEnoughHistoryToShow(resale) ||
            !JackpotHunter.EstimateAgreesWithSweep(candidate.MedianSold, resale))
        {
            result.ProductsDropped++;
            log.Add("Info", "Price-trend product dropped",
                $"\"{candidate.LookupTitle}\": {resale.SoldCompCount + resale.TerapeakCompCount} sold comp(s), " +
                $"sweep median {candidate.MedianSold:C} vs estimate {(resale.ExpectedSale ?? resale.Median):C}.");
            continue;
        }

        board.Add(BuildTrendRow(candidate, trend, resale, hunter, feeProfile));
    }

    result.Rows = PriceTrendAnalyzer.Rank(board);
    result.BuyNowCount = result.Rows.Count(r => r.Verdict == "buy_now");
    // What getting ahead of the move is worth per unit, added across the rows worth buying. An
    // upper bound if every one of them were bought and the climb held — not a forecast, and it
    // deliberately ignores tentative readings.
    result.TotalTrendHeadroom = Math.Round(
        result.Rows.Where(r => r.Verdict is "buy_now" or "get_in_early" && r.Trend.Reliability == "confirmed")
            .Sum(r => r.TrendHeadroom), 2);
    result.SoldCompsConfigured = result.Rows.Any(r => r.SoldCompCount > 0) || result.CompsScanned > 0;

    foreach (var outcome in result.Niches)
        outcome.RisingFound = result.Rows.Count(r => r.NicheId == outcome.Id);

    if (result.Rows.Count == 0)
    {
        result.DataWarning = result.ProductsMeasured == 0
            ? "Nothing in these categories had enough dated sold history to read a trend from. Scan again — the sweep moves on to different categories each time."
            : result.ProductsRising == 0
                ? $"{result.ProductsMeasured} product(s) measured and none of them are climbing. That's a real answer — nothing here is worth buying ahead of. Scan again to sweep different categories."
                : "Products were rising, but none of them cleared the evidence bar once priced. Scan again to sweep different categories.";
    }

    sw.Stop();
    log.Add("Info", "Price-trend radar",
        $"Seed {seed}; Window: {windowDays}d; Categories: {string.Join(" + ", result.Niches.Select(n => n.Id))}; " +
        $"Comps: {result.CompsScanned} ({result.Corpus.DatedComps} dated, newest {result.Corpus.NewestCompAgeDays?.ToString() ?? "n/a"}d); " +
        $"Considered: {result.ProductsConsidered}; Measured: {result.ProductsMeasured}; Rising: {result.ProductsRising}; " +
        $"Priced: {result.ProductsPriced}; Dropped: {result.ProductsDropped}; Rows: {result.Rows.Count} " +
        $"(buy now: {result.BuyNowCount}); Trend headroom: {result.TotalTrendHeadroom:C}; " +
        $"Terapeak scrapes: {result.TerapeakScrapesUsed}; Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// One radar row: the product, what its price is doing, and what that is worth in money.
//
// The load-bearing rule is which price each number is computed from. MaxBuyToday, TargetBuyPrice
// and ProfitAtTarget all come from TODAY's sold price — the trend never makes a buy affordable.
// Only MaxBuyIfTrendHolds uses the projection, and the gap between the two is reported as upside
// on a buy that already works on its own.
static TrendRadarRow BuildTrendRow(
    JackpotCandidate candidate, PriceTrendReading trend, ResalePricing resale,
    JackpotHunter hunter, FeeProfile fees)
{
    var row = new TrendRadarRow
    {
        Product = candidate.LookupTitle,
        PricedAs = resale.LookupTitle,
        NicheId = candidate.NicheId, NicheLabel = candidate.NicheLabel, Probe = candidate.Probe,
        ImageUrl = candidate.ImageUrl,
        SearchQuery = JackpotHunter.ShoppingQuery(candidate.LookupTitle),
        Trend = trend,
        EbayExpectedSale = resale.ExpectedSale,
        EbayMedian = resale.Median,
        EbayQuickSale = resale.QuickSale,
        ResaleSource = LocalArbitrageAnalyzer.SourceLabel(resale.SoldCompCount, resale.TerapeakCompCount),
        SoldCompCount = resale.SoldCompCount,
        TerapeakCompCount = resale.TerapeakCompCount,
        ConfidenceScore = resale.ConfidenceScore,
        ConfidenceLevel = resale.ConfidenceLevel,
        LiquidityScore = resale.LiquidityScore,
        LiquidityLevel = resale.LiquidityLevel,
        EstimatedMonthlySales = resale.EstimatedMonthlySales,
        DisagreementMessage = resale.DisagreementMessage,
    };

    var breakEvenToday = hunter.BreakEvenBuyPrice(resale, fees);
    row.MaxBuyToday = Math.Round(breakEvenToday, 2);
    row.TargetBuyPrice = JackpotHunter.TargetBuyPrice(breakEvenToday);
    row.ProfitAtTarget = Math.Round(Math.Max(0m, breakEvenToday - row.TargetBuyPrice), 2);

    var expectedToday = resale.ExpectedSale is > 0 ? resale.ExpectedSale!.Value : resale.Median ?? 0m;
    row.MarginAtTargetPercent = expectedToday > 0
        ? Math.Round(row.ProfitAtTarget / expectedToday * 100m, 1)
        : 0m;

    // The wait, priced on the target buy — a climbing product that takes five months to sell is a
    // worse use of the money than a flat one that clears in three weeks, and the row has to say so.
    var speed = DaysToCashEstimator.Estimate(
        resale.EstimatedDaysToSell, resale.EstimatedMonthlySales,
        row.ProfitAtTarget > 0 ? row.ProfitAtTarget : null,
        row.TargetBuyPrice > 0 ? Math.Round(row.ProfitAtTarget / row.TargetBuyPrice * 100m, 1) : null);
    row.DaysToSell = speed.DaysToSell;
    row.DaysToCash = speed.DaysToCash;
    row.ProfitPerDay = speed.ProfitPerDay;
    row.AnnualizedRoiPercent = speed.AnnualizedRoiPercent;
    row.SpeedTier = speed.SpeedTier;
    row.SpeedLabel = speed.SpeedLabel;
    row.SpeedNote = speed.Note;

    // The same break-even, recomputed against the estimator's price scaled by the trend. Same fee
    // model, same shipping — the only thing that changed is the sale price it clears.
    var multiplier = PriceTrendAnalyzer.TrendMultiplier(trend);
    if (multiplier > 1m && expectedToday > 0)
    {
        var atTrend = new ResalePricing
        {
            LookupTitle = resale.LookupTitle,
            ExpectedSale = Math.Round(expectedToday * multiplier, 2),
            Median = resale.Median is > 0 ? Math.Round(resale.Median!.Value * multiplier, 2) : resale.Median,
            QuickSale = resale.QuickSale is > 0 ? Math.Round(resale.QuickSale!.Value * multiplier, 2) : resale.QuickSale,
            AvgCompShipping = resale.AvgCompShipping,
        };
        row.MaxBuyIfTrendHolds = Math.Round(hunter.BreakEvenBuyPrice(atTrend, fees), 2);
    }
    else
    {
        row.MaxBuyIfTrendHolds = row.MaxBuyToday;
    }

    row.TrendHeadroom = Math.Round(Math.Max(0m, row.MaxBuyIfTrendHolds - row.MaxBuyToday), 2);

    var (verdict, note) = PriceTrendAnalyzer.JudgeRow(
        trend, resale.SoldCompCount + resale.TerapeakCompCount, resale.ConfidenceScore, resale.ConfidenceLevel,
        row.MaxBuyToday, row.TargetBuyPrice, row.TrendHeadroom);
    row.Verdict = verdict;
    row.VerdictNote = note;
    return row;
}

// Only called when nothing could be priced — the hosted path's probe is a real HTTP request, so
// it isn't worth spending on a run that already has its answer.
static async Task<bool> SoldCompsReachableAsync(IMarketplaceRepository marketplace, CancellationToken ct)
{
    try { return await marketplace.IsAvailableAsync(ct); }
    catch (OperationCanceledException) { throw; }
    catch { return false; }
}

// The whole opportunity-search-and-score pipeline behind the interactive /api/opportunities/search
// endpoint. When a seller is given with no keyword, this skips the broad market-value estimate
// (there's no single keyword to price against a whole seller's inventory) and lets the per-item
// recheck below price each listing off its own title instead.
static async Task<OpportunitySearchResult> FindOpportunitiesAsync(
    string q, string? category, string? condition, decimal? minPrice, decimal? maxPrice, string listingType,
    EbayService ebay, TerapeakMarketService terapeakMarket, IMarketplaceRepository marketplace,
    ProductNormalizer normalizer, ComparableMatcher matcher, MarketPriceEstimator priceEstimator,
    SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc, FeeProfile feeProfile,
    OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer, ActionLog log,
    int terapeakRecheckLimit = 5, string? seller = null, CancellationToken ct = default)
{
    // Price the same combined keyword+category search that's actually being run below.
    var priceQuery = string.IsNullOrWhiteSpace(category) ? q : $"{q} {category}";

    // Estimate current broad market value from sold comps. Checked in cost order: the local
    // sold-history database first (free, instant, no rate limit against eBay), then Terapeak,
    // then Marketplace Insights as a last resort. This is a rough, blended estimate across
    // everything matching the search term — good enough to rank 50 results, not precise enough
    // to trust for any one specific item (see the per-item recheck below, which replaces it for
    // every candidate it can find better data for). Terapeak goes through the shared cache first
    // (see TerapeakMarketService) so a query any other search already paid for doesn't cost a
    // second real scrape.
    decimal marketValue = 0;
    decimal averagePrice = 0;
    decimal avgShipping = 0;
    decimal? sellThroughPercent = null;
    var soldSource = "none";

    if (!string.IsNullOrWhiteSpace(q))
    {
        // Normalize before hitting the local database, so the search tries the strongest
        // identifier actually present in the query instead of always falling straight to a
        // broad keyword match.
        var broadTarget = normalizer.Normalize(priceQuery);
        if (!string.IsNullOrWhiteSpace(condition)) broadTarget.Condition = condition;

        MarketplacePricingSummary? localBroad = null;
        try
        {
            localBroad = await marketplace.FindComparablesAsync(new MarketplaceLookupRequest
            {
                PartNumber     = broadTarget.PartNumber,
                Model          = broadTarget.Model,
                Brand          = broadTarget.Brand,
                Category       = broadTarget.Category,
                Keywords       = priceQuery,
                Condition      = condition,
                MaxComparables = 20
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Opportunity local market lookup failed", ex.Message);
        }

        // Require a few matches before trusting the local database over a live scrape — one or
        // two comps aren't enough to call it a reliable market estimate.
        if (localBroad is { MatchCount: >= 3 })
        {
            marketValue   = localBroad.MedianPrice ?? localBroad.AveragePrice ?? 0;
            averagePrice  = localBroad.AveragePrice ?? 0;
            avgShipping   = localBroad.AverageShipping ?? 0;
            soldSource    = "local_market_data";
            // No live active-listing count is fetched on this path, so a true sold/(sold+active)
            // sell-through ratio (what Terapeak reports) isn't computable here. LiquidityScore is
            // already a comparable 0-100 measure of how fast this sells (see
            // LiquidityScoringService), so it doubles as the closest honest proxy rather than
            // leaving the sell-through stat silently blank for the now-preferred data source.
            sellThroughPercent = localBroad.LiquidityScore;
        }
        else
        {
            var broadPricing = await terapeakMarket.GetAsync(broadTarget, priceQuery, allowRealScrape: true, ct: ct);
            if (broadPricing is not null)
            {
                marketValue = broadPricing.Data.Median > 0 ? broadPricing.Data.Median : broadPricing.Data.Average;
                averagePrice = broadPricing.Data.Average;
                avgShipping = broadPricing.Data.AvgShipping;
                sellThroughPercent = broadPricing.Data.SellThroughPercent;
                soldSource = "terapeak";
            }
            if (marketValue == 0)
            {
                try
                {
                    var soldResult = await ebay.SearchSoldCompsAsync(priceQuery);
                    if (soldResult.Count > 0)
                    {
                        marketValue = soldResult.Median > 0 ? soldResult.Median : soldResult.Average;
                        averagePrice = soldResult.Average;
                        soldSource = "marketplace_insights";
                    }
                }
                catch (Exception ex)
                {
                    log.Add("Warning", "Opportunity sold-comp lookup failed", ex.Message);
                }
            }
        }
    }
    // Net resale value = what the item actually sells for, minus what it costs YOU to ship it to
    // your buyer. avgShipping is a real cash cost of reselling, not extra revenue — a listing
    // that's "underpriced" before shipping can easily be a loser once a heavy/bulky item's
    // shipping cost is subtracted, and hiding that was inflating every profit estimate. Allowed
    // to go negative (shipping costing more than the item is worth) rather than being clamped to
    // "no data" — that's a real, useful signal, not an absence of one.
    var netResaleValue = marketValue > 0 ? marketValue - avgShipping : 0;

    var listings = string.IsNullOrWhiteSpace(seller)
        ? await ebay.SearchEndingSoonAsync(q, 0, 50, category, condition, minPrice, maxPrice, listingType)
        : await ebay.SearchBySellerAsync(seller, 50, condition, minPrice, maxPrice, listingType);

    // "Newly listed" has no real listing-start timestamp available from the Browse API's
    // item_summary — rank within the API's own newlyListed-sorted order is the only proxy,
    // so it's only meaningful when that's actually the sort in effect (i.e. not AUCTION mode).
    var sortedByRecency = listingType != "AUCTION";

    var opportunities = listings
        .Select((item, idx) =>
        {
            var totalCost = item.Price + item.ShippingCost;
            decimal? pct = marketValue > 0 && totalCost > 0
                ? Math.Round((netResaleValue - totalCost) / totalCost * 100m, 1)
                : (decimal?)null;
            return new OpportunityListItem
            {
                Title                = item.Title,
                Price                = item.Price,
                ShippingCost         = item.ShippingCost,
                TotalCost            = totalCost,
                Url                  = item.Url,
                ImageUrl             = item.ImageUrl,
                EndDate              = item.EndDate,
                SellerUsername       = item.SellerUsername,
                SellerFeedbackScore  = item.SellerFeedbackScore,
                BuyingOption         = item.BuyingOption,
                BidCount             = item.BidCount,
                // Broad, search-wide estimate — same for every item until the per-item Terapeak
                // re-check below replaces it for the top few candidates with a real per-item price.
                MarketAverage           = averagePrice > 0 ? averagePrice : (decimal?)null,
                EstimatedResaleShipping = marketValue > 0 ? avgShipping : (decimal?)null,
                EstimatedResalePrice    = marketValue > 0 ? netResaleValue : (decimal?)null,
                EstimatedProfit         = marketValue > 0 && totalCost > 0 ? Math.Round(netResaleValue - totalCost, 2) : (decimal?)null,
                ProfitPercent        = pct,
                // Cheap heuristics, not AI/vision analysis — catch obvious cases only.
                IsUnderpriced      = pct is > 15,
                IsHighProfitMargin = pct is > 50,
                IsEndingSoon       = item.BuyingOption == "AUCTION" && item.EndDate.HasValue && item.EndDate.Value <= DateTime.UtcNow.AddHours(6),
                IsHighDemand       = item.BuyingOption == "AUCTION" && item.BidCount >= 5,
                IsNewlyListed      = sortedByRecency && idx < 10,
                HasPoorTitle       = HasPoorTitle(item.Title),
                HasMisspelledTitle = HasMisspelledTitle(item.Title),
                HasPoorPhoto       = string.IsNullOrWhiteSpace(item.ImageUrl)
            };
        })
        .ToList();

    // Re-check candidates against comps for THAT specific item (its own title, not the broad
    // search-wide estimate) — a single search term like "postcard" or "graphics card" can span a
    // $0.01-$8,000 range, so the blended market value above is too noisy to trust for any one
    // listing. AnalyzeProductAsync runs the full matching/pricing/scoring engine per candidate:
    // local sold-history database first (free, no budget spent), real Terapeak scrapes only for
    // whatever the database doesn't cover and only up to terapeakRecheckLimit per search (a cache
    // hit is free and doesn't touch that budget).
    if (terapeakRecheckLimit > 0)
    {
        var realScrapesUsed = 0;
        // With no broad keyword (seller-only search) nothing has a ProfitPercent yet to rank
        // candidates by — recheck every listing in whatever order the API returned instead of
        // filtering down to an empty set.
        var candidates = marketValue > 0
            ? opportunities.Where(x => x.ProfitPercent.HasValue).OrderByDescending(x => x.ProfitPercent!.Value)
            : opportunities.AsEnumerable();

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Title)) continue;

            // Cache-only pre-check (allowRealTerapeakScrape: false — never scrapes) so a cache
            // hit doesn't consume the real-scrape budget; AnalyzeProductAsync below re-checks the
            // same cache key, so this costs an extra SQLite read, never an extra scrape.
            var isCached = await terapeakMarket.GetAsync(normalizer.Normalize(candidate.Title), candidate.Title,
                allowRealScrape: false, ct: ct) is not null;
            var allowScrape = isCached || realScrapesUsed < terapeakRecheckLimit;
            if (allowScrape && !isCached) realScrapesUsed++;

            // "Supplier cost" in this flow is the cost to acquire the live listing itself (price +
            // shipping) — what a reseller flipping this item would have paid.
            var analysis = await AnalyzeProductAsync(
                candidate.Title, supplierUnitCost: candidate.TotalCost > 0 ? candidate.TotalCost : null, quantity: 1, listingType,
                activeListingsAlreadyFetched: listings, ebayForCompetitionFallback: null,
                allowRealTerapeakScrape: allowScrape,
                normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
                opportunityScorer, confidenceScorer, log, ct);

            ApplyAnalysisToOpportunityItem(candidate, analysis);
        }
    }

    opportunities = opportunities.OrderByDescending(x => x.ProfitPercent ?? -999m).ToList();

    return new OpportunitySearchResult
    {
        Query = string.IsNullOrWhiteSpace(q) ? $"seller:{seller}" : q,
        MarketValue = marketValue, AveragePrice = averagePrice, SoldSource = soldSource,
        ListingType = listingType, SellThroughPercent = sellThroughPercent, Items = opportunities
    };
}

// Liquidation lot analysis: read the manifest, price every line against real sold comps, then let
// LotAnalyzer turn that plus the ask price into a buy/skip call.
//
// Reuses AnalyzeProductAsync for the pricing, so a unit out of a pallet is valued by exactly the
// stack that values a dropship, a local flip or a live listing being repriced — there is no
// second, friendlier pricing engine for the feature with the biggest number on it.
//
// Two things are rationed, because one click here fans out over a whole manifest:
//   * comp lookups are per distinct PRODUCT, not per line — a manifest listing the same drill on
//     six rows is one lookup, and it is keyed on Terapeak's own cache key so a cached scrape is
//     shared too;
//   * real Terapeak scrapes only happen in a second pass, only for the lines where the most money
//     hangs on the answer, and only up to a hard budget. Pass 1 is cache-only.
static async Task<LotAnalysisResult> AnalyzeLotAsync(
    LotAnalysisRequest req, ClaudeService claude, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LotAnalyzer analyzer, ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    const int TerapeakBudget = 5;

    var result = new LotAnalysisResult
    {
        TargetRoiPercent = Math.Clamp(req.TargetRoiPercent, 0m, 500m),
        TerapeakConnected = terapeak.IsConnected,
    };

    // ── Read the manifest ───────────────────────────────────────────────────────────────────────
    // The deterministic parser goes first and wins whenever it can: a CSV has columns, columns can
    // be read exactly, it costs nothing, and it cannot invent a line that was never on the pallet.
    // Claude is the fallback for what that genuinely cannot do — a photo, or prose.
    var parsed = ManifestParser.Parse(req.ManifestText);
    var lines = parsed.Lines;
    result.SourceFormat = parsed.Format;
    result.SourceNote = parsed.Note;

    var needsAi = !string.IsNullOrWhiteSpace(req.ImageBase64) || lines.Count < 2;
    if (needsAi && req.UseAi)
    {
        try
        {
            var extracted = await claude.AnalyzeManifestAsync(req.ManifestText, req.ImageBase64, req.MimeType, ct);
            if (extracted.Count > lines.Count)
            {
                lines = extracted;
                result.SourceFormat = string.IsNullOrWhiteSpace(req.ImageBase64) ? "ai_text" : "ai_image";
                result.SourceNote = $"Read {extracted.Count} line{(extracted.Count == 1 ? "" : "s")} from " +
                    (string.IsNullOrWhiteSpace(req.ImageBase64) ? "the pasted description" : "the manifest image") + " with AI.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // A failed extraction must not throw away a manifest the plain parser already read.
            log.Add("Warning", "Manifest AI extraction failed", ex.Message);
            if (lines.Count == 0)
            {
                result.Status = "no_lines";
                result.Error = $"The manifest couldn't be read: {ex.Message}";
                result.Verdict = "no_data";
                result.VerdictNote = "Nothing could be read from what was pasted.";
                return result;
            }
            result.Warnings.Add("AI extraction failed, so only the plain-text reading of the manifest was used.");
        }
    }

    result.LinesExtracted = lines.Count;
    if (lines.Count == 0)
    {
        result.Status = "no_lines";
        result.Verdict = "no_data";
        result.VerdictNote = "No product lines could be read. Paste the manifest as a table (description, quantity, retail), or drop a photo of it.";
        return result;
    }

    // Highest claimed value first, so when the cap bites it takes the rows that matter least.
    var maxLines = Math.Clamp(req.MaxLines <= 0 ? 60 : req.MaxLines, 1, 150);
    var analyzing = lines
        .OrderByDescending(l => (l.UnitRetail ?? 0m) * Math.Max(1, l.Quantity))
        .ThenByDescending(l => l.Quantity)
        .Take(maxLines).ToList();
    result.LinesAnalyzed = analyzing.Count;
    if (analyzing.Count < lines.Count)
        result.Warnings.Add($"{lines.Count - analyzing.Count} of {lines.Count} manifest lines were left out of this analysis — the {analyzing.Count} highest-value lines were priced.");

    // ── Group by product, so one drill on six rows is one comp lookup ───────────────────────────
    var groups = new Dictionary<string, (string LookupTitle, List<ManifestLine> Lines)>(StringComparer.OrdinalIgnoreCase);
    var keyOf = new Dictionary<ManifestLine, string>();
    var packQtyOf = new Dictionary<ManifestLine, int>();

    foreach (var line in analyzing)
    {
        var query = string.IsNullOrWhiteSpace(line.SearchQuery) ? line.Description : line.SearchQuery;
        if (string.IsNullOrWhiteSpace(query)) continue;

        var identity = normalizer.Normalize(query);
        // Units the line's OWN wording implies per sale ("case of 12"). Sold comps are per single
        // item, so a pack line has no like-for-like price and LotAnalyzer refuses to invent one.
        packQtyOf[line] = Math.Max(1, identity.Quantity);

        var key = TerapeakMarketService.BuildCacheKey(identity);
        if (string.IsNullOrWhiteSpace(key)) key = query.Trim().ToLowerInvariant();
        keyOf[line] = key;

        if (!groups.TryGetValue(key, out var group)) group = (query, []);
        group.Lines.Add(line);
        // The fullest wording in the group does the lookup — the comp matcher can only work with
        // the words it is given, and manifest rows for the same item vary wildly in detail.
        if (query.Length > group.LookupTitle.Length) group = (query, group.Lines);
        groups[key] = group;
    }
    result.ProductsPriced = groups.Count;

    async Task<ResalePricing> PriceAsync(string lookupTitle, bool allowScrape)
    {
        var analysis = await AnalyzeProductAsync(
            lookupTitle, supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: null, ebayForCompetitionFallback: null,
            allowRealTerapeakScrape: allowScrape,
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);
        return ResalePricing.From(analysis, lookupTitle);
    }

    // ── Pass 1: sold-comps database plus whatever Terapeak already has cached. No scrapes. ──────
    var pricing = new Dictionary<string, ResalePricing>(StringComparer.OrdinalIgnoreCase);
    var cached = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    foreach (var (key, group) in groups)
    {
        cached[key] = await terapeakMarket.GetAsync(
            normalizer.Normalize(group.LookupTitle), group.LookupTitle, allowRealScrape: false, ct: ct) is not null;
        pricing[key] = await PriceAsync(group.LookupTitle, allowScrape: false);
    }

    // ── Pass 2: spend the scrape budget where the most money hangs on the answer ────────────────
    if (terapeak.IsConnected)
    {
        var targets = LocalArbitrageAnalyzer.SelectScrapeTargets(
            groups.Select(g =>
            {
                var priced = pricing[g.Key];
                var units = g.Value.Lines.Sum(l => Math.Max(1, l.Quantity));
                var claimedValue = g.Value.Lines.Sum(l => (l.UnitRetail ?? 0m) * Math.Max(1, l.Quantity));
                // Dollars in play on this product, so corroboration goes to the lines that can
                // move the verdict — not to a $6 accessory that cannot.
                decimal? atStake = priced.HasPrice
                    ? (priced.QuickSale ?? priced.ExpectedSale ?? priced.Median)!.Value * units
                    : null;
                return (g.Key, PreliminaryProfit: atStake, HasTerapeak: cached[g.Key],
                        LocalAsk: claimedValue > 0m ? claimedValue : units);
            }), TerapeakBudget);

        foreach (var key in targets)
        {
            pricing[key] = await PriceAsync(groups[key].LookupTitle, allowScrape: true);
            result.TerapeakScrapesUsed++;
        }
    }

    // ── The money ───────────────────────────────────────────────────────────────────────────────
    var grade = LotAnalyzer.Assumptions(req.ConditionGrade, req.SellableRatePercent, req.ConditionPriceFactorPercent);
    result.Assumptions = grade;

    var handling = Math.Max(0m, req.PerUnitHandlingCost);
    var rows = analyzing
        .Where(l => keyOf.ContainsKey(l))
        .Select(l => analyzer.BuildLine(l, pricing[keyOf[l]], grade, feeProfile, handling, packQtyOf[l]))
        .ToList();

    // Lines with no readable query at all still belong on the table — a manifest row the app
    // could not even form a search for is one the buyer has to eyeball themselves.
    foreach (var orphan in analyzing.Where(l => !keyOf.ContainsKey(l)))
        rows.Add(new LotLineAnalysis
        {
            Description = orphan.Description, Quantity = Math.Max(1, orphan.Quantity),
            UnitRetail = orphan.UnitRetail, RetailTotal = (orphan.UnitRetail ?? 0m) * Math.Max(1, orphan.Quantity),
            Status = "no_data", StatusNote = "Nothing searchable in this line.",
        });

    var costs = LotAnalyzer.CostOf(req.AskPrice, req.BuyerPremiumPercent, req.SalesTaxPercent, req.FreightCost);
    LotAnalyzer.AllocateCost(rows, costs.TotalCost);

    var totals = LotAnalyzer.Summarize(rows, costs,
        manifestUnits: analyzing.Sum(l => Math.Max(1, l.Quantity)),
        manifestRetailTotal: analyzing.Sum(l => (l.UnitRetail ?? 0m) * Math.Max(1, l.Quantity)));

    result.Concentration = LotAnalyzer.Concentrate(rows);
    result.Items = LotAnalyzer.Rank(rows);
    result.Totals = totals;
    result.LinesPriced = rows.Count(r => r.Status is "priced" or "thin");
    result.LinesExcluded = rows.Count(r => r.Status == "excluded");
    result.CoveragePercent = LotAnalyzer.Coverage(rows);
    result.BreakEvenAsk = LotAnalyzer.MaxAsk(totals.NetRecovery, req.BuyerPremiumPercent, req.SalesTaxPercent, req.FreightCost, 0m);
    result.MaxBid = LotAnalyzer.MaxAsk(totals.NetRecovery, req.BuyerPremiumPercent, req.SalesTaxPercent, req.FreightCost, result.TargetRoiPercent);

    var (verdict, note) = LotAnalyzer.Judge(totals, result.CoveragePercent, result.LinesPriced,
        result.BreakEvenAsk, result.MaxBid, result.TargetRoiPercent);
    result.Verdict = verdict;
    result.VerdictNote = note;

    if (result.LinesPriced == 0)
    {
        result.Status = "no_pricing";
        var reachable = await SoldCompsReachableAsync(marketplace, ct);
        result.DataWarning = (reachable, terapeak.IsConnected) switch
        {
            (false, false) => "No eBay sold-price source is available — connect Terapeak in Settings, or configure the sold-comps database, to price a manifest.",
            (true, _) => "The sold-comps database had no history for any line on this manifest. Connecting Terapeak would add a second source.",
            (false, true) => "Terapeak is connected but returned no sold history for these lines.",
        };
    }

    sw.Stop();
    log.Add("Info", "Lot analysis",
        $"Source: {result.SourceFormat}; Lines: {result.LinesExtracted} extracted / {result.LinesAnalyzed} analyzed / " +
        $"{result.LinesPriced} priced / {result.LinesExcluded} excluded across {result.ProductsPriced} product(s); " +
        $"Terapeak scrapes: {result.TerapeakScrapesUsed}; Ask: {totals.TotalCost:C0} all-in; " +
        $"Net recovery: {totals.NetRecovery:C0}; Net profit: {totals.NetProfit:C0}; Coverage: {result.CoveragePercent:0.#}%; " +
        $"Verdict: {result.Verdict}; Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// The single shared entry point for "analyze this one product" — used by both the Opportunity
// Finder's per-item recheck and the Supplier File Analyzer, so there's exactly one implementation
// of product normalization -> local-DB matching -> price estimation -> sell-through -> profit ->
// scoring, not two. See the "Opportunity Finder — Real Product-Matching & Scoring Engine" plan
// this was built from for the full component breakdown.
//
// activeListingsAlreadyFetched: when the caller already has a batch of live listings for the same
// search (the Opportunity Finder keyword/seller search), the OTHER items in that batch are used as
// the competition set — no extra eBay call. ebayForCompetitionFallback is used only when that list
// isn't available (Supplier File Analyzer has no batch of live listings to draw from).
//
// allowRealTerapeakScrape: the caller's own rationing decision (see terapeakRecheckLimit) — this
// function never decides on its own to spend a real scrape; see TerapeakMarketService.
static async Task<MarketAnalysisResult> AnalyzeProductAsync(
    string titleText, decimal? supplierUnitCost, int quantity, string? listingType,
    List<EbayOpportunityItem>? activeListingsAlreadyFetched, EbayService? ebayForCompetitionFallback,
    bool allowRealTerapeakScrape,
    ProductNormalizer normalizer, IMarketplaceRepository marketplace, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var target = normalizer.Normalize(titleText);
    if (quantity > 0) target.Quantity = quantity;

    MarketplacePricingSummary localSummary;
    try
    {
        localSummary = await marketplace.FindComparablesAsync(new MarketplaceLookupRequest
        {
            PartNumber = target.PartNumber, Model = target.Model, Brand = target.Brand,
            Category = target.Category, Keywords = titleText, Condition = target.Condition, MaxComparables = 20,
        }, ct);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        log.Add("Warning", "Market analysis local lookup failed", $"{titleText}: {ex.Message}");
        localSummary = new MarketplacePricingSummary { Query = titleText };
    }
    var localComparables = localSummary.ComparableListings;

    // Re-derives Tier/exclusion info for the (already-accepted) comparables MarketplaceRepository
    // returned — a cheap, pure, in-memory re-score of a small already-fetched set (<=20 rows), not
    // a new DB or API call — so this orchestration doesn't need IMarketplaceRepository's public
    // contract to expose ComparableMatch internals.
    var matches = localComparables.Select(c => matcher.Match(target, c)).Where(m => !m.Excluded).ToList();
    var strongComparableCount = matches.Count(m => m.MatchConfidence >= 50);
    var exactIdentifierMatches = matches.Count(m => m.Tier == MatchTier.ExactIdentifier);
    var modelNumberMatches = matches.Count(m => m.Tier == MatchTier.ExactModel);
    var mostRecentAgeDays = localComparables.Where(c => c.SoldDate.HasValue)
        .Select(c => (int)Math.Max(0, (DateTime.UtcNow - c.SoldDate!.Value).TotalDays))
        .DefaultIfEmpty(-1).Min();
    int? mostRecentComparableAgeDays = mostRecentAgeDays >= 0 ? mostRecentAgeDays : null;

    // ── Active competition — reuse an already-fetched batch when available, never a new per-item
    // eBay search just to analyze one candidate ─────────────────────────────────────────────────
    var competition = new CompetitionAnalysis();
    if (activeListingsAlreadyFetched is { Count: > 0 })
    {
        var closeMatches = activeListingsAlreadyFetched
            .Where(a => !string.Equals(a.Title, titleText, StringComparison.Ordinal))
            .Select(a => (Item: a, Score: MarketplaceMatcher.Score(a.Title, titleText).Score))
            .Where(x => x.Score >= 40)
            .ToList();
        competition.CloseActiveComparableCount = closeMatches.Count;
        if (closeMatches.Count > 0)
        {
            var activePrices = closeMatches.Select(x => x.Item.Price).OrderBy(p => p).ToList();
            competition.MedianActivePrice = MarketplacePricingCalculator.Median(activePrices);
            competition.LowestRealisticActivePrice = activePrices.Min();
        }
    }
    else if (ebayForCompetitionFallback is not null)
    {
        try { competition.CloseActiveComparableCount = await ebayForCompetitionFallback.GetActiveListingCountAsync(titleText); }
        catch (Exception ex) { log.Add("Warning", "Competition lookup failed", ex.Message); }
    }
    competition.CompetitionLevel = competition.CloseActiveComparableCount switch { 0 => "Low", <= 10 => "Moderate", _ => "High" };

    // ── Price estimate — local comps + Terapeak (lazy, rationed by the caller), adaptive blend ──
    var priceEstimate = await priceEstimator.EstimateAsync(
        target, localComparables, titleText, listingType, allowRealTerapeakScrape,
        competition.CloseActiveComparableCount, ct);

    // ── Sell-through ─────────────────────────────────────────────────────────────────────────
    var sellThrough = sellThroughCalc.Calculate(
        titleText, localComparables, competition.CloseActiveComparableCount,
        priceEstimate.ExpectedSalePrice, competition.MedianActivePrice);
    sellThrough.LiquidityScore = localSummary.LiquidityScore;
    sellThrough.LiquidityLevel = localSummary.LiquidityLevel;

    var stability = ComputeStability(localComparables, priceEstimate);

    ProfitBreakdown? profit = null;
    if (supplierUnitCost is decimal cost && priceEstimate.ExpectedSalePrice is decimal expected)
    {
        // What buyers typically paid for shipping on the matched comps — the closest available
        // estimate for "estimated shipping cost" without a new lookup, reusing data already fetched.
        var avgBuyerShipping = localComparables.Count > 0 ? Math.Round(localComparables.Average(c => c.Shipping), 2) : 0m;
        profit = profitCalc.Calculate(cost, target.Quantity, expected, priceEstimate.QuickSalePrice ?? expected, avgBuyerShipping, feeProfile);
    }

    var result = new MarketAnalysisResult
    {
        Identity = target, PriceEstimate = priceEstimate, SellThrough = sellThrough, Competition = competition,
        Profit = profit, Stability = stability,
        TopSoldComparables = localComparables.OrderByDescending(c => c.MatchScore).Take(5).ToList(),
        Sources = new SourceBreakdown
        {
            LocalComparableCount = localComparables.Count,
            TerapeakComparableCount = priceEstimate.TerapeakComparableCount,
            LocalWeightPercent = priceEstimate.LocalWeight * 100,
            TerapeakWeightPercent = priceEstimate.TerapeakWeight * 100,
        },
    };

    result.Confidence = confidenceScorer.Score(result, strongComparableCount, exactIdentifierMatches,
        modelNumberMatches, mostRecentComparableAgeDays, conditionConsistent: true, quantityConsistent: true, categoryConsistent: true);
    result.Score = opportunityScorer.Score(result, strongComparableCount, mostRecentComparableAgeDays);

    sw.Stop();
    log.Add("Info", "Market analysis computed",
        $"\"{titleText}\"; Local comps: {localComparables.Count} (strong: {strongComparableCount}, " +
        $"exact-id: {exactIdentifierMatches}, model: {modelNumberMatches}); Source weighting: " +
        $"local {result.Sources.LocalWeightPercent:0}%/Terapeak {result.Sources.TerapeakWeightPercent:0}%; " +
        $"Opportunity score: {result.Score.Score}; Confidence: {result.Confidence.Score} ({result.Confidence.Level}); " +
        $"Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// Price stability from the dispersion of the strong comparables MarketPriceEstimator already
// selected (narrow IQR relative to the median = high stability) and a simple recent-vs-older
// median comparison for trend direction — a small scoring adjustment, not the main determinant.
static PriceStability ComputeStability(IReadOnlyList<MarketplaceComparableResult> comparables, PriceEstimate estimate)
{
    var stability = new PriceStability();

    if (estimate.Percentile25 is decimal p25 && estimate.Percentile75 is decimal p75 && estimate.MedianPrice is > 0 and decimal median)
    {
        var iqrRatio = (double)((p75 - p25) / median);
        stability.StabilityScore = (int)Math.Round(Math.Clamp(1.0 - iqrRatio, 0, 1) * 100);
    }
    else
    {
        stability.StabilityScore = comparables.Count > 0 ? 50 : 0; // not enough data to judge — neutral, not confidently stable
    }

    var dated = comparables.Where(c => c.SoldDate.HasValue).OrderBy(c => c.SoldDate).ToList();
    if (dated.Count >= 4)
    {
        var half = dated.Count / 2;
        var older = MarketplacePricingCalculator.Median(dated.Take(half).Select(c => c.SoldPrice).ToList());
        var recent = MarketplacePricingCalculator.Median(dated.Skip(half).Select(c => c.SoldPrice).ToList());
        if (older > 0)
        {
            var change = (recent - older) / older;
            stability.Trend = change >= 0.10m ? "Rising" : change <= -0.10m ? "Falling" : "Stable";
        }
    }
    return stability;
}

// Flattens a MarketAnalysisResult onto an OpportunityListItem — populates the pre-existing
// fields (so nothing already reading MarketAverage/ProfitPercent/LiquidityLevel/etc. breaks) and
// the additive new fields the fuller RESULT DISPLAY needs.
static void ApplyAnalysisToOpportunityItem(OpportunityListItem candidate, MarketAnalysisResult analysis)
{
    var resale = analysis.PriceEstimate.ExpectedSalePrice;
    if (resale is decimal expected && candidate.TotalCost > 0)
    {
        candidate.MarketAverage = analysis.PriceEstimate.MedianPrice ?? candidate.MarketAverage;
        candidate.EstimatedResaleShipping = 0m;
        candidate.EstimatedResalePrice = expected;
        candidate.EstimatedProfit = Math.Round(expected - candidate.TotalCost, 2);
        candidate.ProfitPercent = Math.Round((expected - candidate.TotalCost) / candidate.TotalCost * 100m, 1);
        candidate.IsVerified = true;
        candidate.IsUnderpriced = candidate.ProfitPercent is > 15;
        candidate.IsHighProfitMargin = candidate.ProfitPercent is > 50;
    }

    candidate.LiquidityScore = analysis.SellThrough.LiquidityScore;
    candidate.LiquidityLevel = analysis.SellThrough.LiquidityLevel;
    candidate.EstimatedDaysToSell = analysis.SellThrough.EstimatedDaysToSell;
    // No active comps to divide by => we can't honestly report a rate. Leave it null so the UI
    // shows "—" (low confidence), instead of the old fake "100%" that made zero-competition noise
    // look like a guaranteed flip.
    candidate.SellThroughPercent = analysis.SellThrough.RateIsUnbounded
        ? (decimal?)null
        : (analysis.SellThrough.SellThroughRate ?? candidate.SellThroughPercent);
    // Any leftover percentage here came from the broad first pass, not from a rate we could verify
    // for this item — flag it so the row wears "low confidence — thin data" instead of the green
    // Terapeak-matched badge.
    candidate.SellThroughUnverified = SellThroughCalculator.IsUnverified(analysis.SellThrough);
    candidate.IsHighThroughput = !candidate.SellThroughUnverified && candidate.SellThroughPercent is > 50;

    candidate.QuickSalePrice = analysis.PriceEstimate.QuickSalePrice;
    candidate.RecommendedListingPrice = analysis.PriceEstimate.RecommendedListingPrice;
    candidate.HighPriceTarget = analysis.PriceEstimate.HighPriceTarget;
    candidate.LocalComparableCount = analysis.Sources.LocalComparableCount;
    candidate.TerapeakComparableCount = analysis.Sources.TerapeakComparableCount;
    candidate.LocalWeightPercent = analysis.Sources.LocalWeightPercent;
    candidate.TerapeakWeightPercent = analysis.Sources.TerapeakWeightPercent;
    candidate.ConfidenceScore = analysis.Confidence.Score;
    candidate.ConfidenceLevel = analysis.Confidence.Level;
    candidate.PriceStabilityScore = analysis.Stability.StabilityScore;
    candidate.PriceTrend = analysis.Stability.Trend;
    candidate.MarketDataDisagreement = analysis.PriceEstimate.MarketDataDisagreement;
    candidate.DisagreementMessage = analysis.PriceEstimate.DisagreementMessage;
    candidate.Warnings = analysis.Score.Warnings;
    candidate.ScoreReasons = analysis.Score.Reasons;
    candidate.ScoreComponents = analysis.Score.ComponentScores;
    candidate.TopComparables = analysis.TopSoldComparables;
    candidate.CompetitionLevel = analysis.Competition.CompetitionLevel;
    candidate.CloseActiveComparableCount = analysis.Competition.CloseActiveComparableCount;
    candidate.OpportunityScore = analysis.Score.HardRejected ? 0 : analysis.Score.Score;

    if (analysis.Profit is ProfitBreakdown profit)
    {
        candidate.RoiPercent = profit.RoiPercent;
        candidate.MarginPercent = profit.MarginPercent;
        candidate.BreakEvenSalePrice = profit.BreakEvenSalePrice;
    }
    candidate.EstimatedMonthlySales = analysis.SellThrough.EstimatedMonthlySales;
}

// Flattens a MarketAnalysisResult onto a DropshipAnalysisItem (Supplier File Analyzer) — same
// idea as ApplyAnalysisToOpportunityItem, kept as a separate function since the two response
// shapes have diverged field names for the pre-existing fields.
static void ApplyAnalysisToDropshipItem(DropshipAnalysisItem item, MarketAnalysisResult analysis)
{
    item.LocalDataAvailable = analysis.Sources.LocalComparableCount > 0;
    if (!item.LocalDataAvailable)
        item.LocalDataMessage = "No reliable local sold-history matches found.";

    item.EbaySoldAverage = analysis.PriceEstimate.TrimmedMeanPrice;
    item.EbaySoldMedian = analysis.PriceEstimate.MedianPrice;
    item.AvgShipping = 0m;
    item.EstimatedResalePrice = analysis.PriceEstimate.ExpectedSalePrice;
    item.ComparableCount = analysis.Sources.LocalComparableCount + analysis.Sources.TerapeakComparableCount;
    item.ConfidenceScore = analysis.Confidence.Score;
    item.ConfidenceLevel = analysis.Confidence.Level;
    item.ComparableListings = analysis.TopSoldComparables;
    item.IsVerified = analysis.PriceEstimate.ExpectedSalePrice is > 0;

    item.EstimatedDaysToSell = analysis.SellThrough.EstimatedDaysToSell;
    item.LiquidityLevel = analysis.SellThrough.LiquidityLevel;
    item.SellThroughPercent = analysis.SellThrough.SellThroughRate;
    item.EstimatedMonthlySales = analysis.SellThrough.EstimatedMonthlySales;

    item.QuickSalePrice = analysis.PriceEstimate.QuickSalePrice;
    item.RecommendedListingPrice = analysis.PriceEstimate.RecommendedListingPrice;
    item.HighPriceTarget = analysis.PriceEstimate.HighPriceTarget;
    item.TerapeakComparableCount = analysis.Sources.TerapeakComparableCount;
    item.PriceStabilityScore = analysis.Stability.StabilityScore;
    item.PriceTrend = analysis.Stability.Trend;
    item.MarketDataDisagreement = analysis.PriceEstimate.MarketDataDisagreement;
    item.DisagreementMessage = analysis.PriceEstimate.DisagreementMessage;
    item.Warnings = analysis.Score.Warnings;
    item.ScoreReasons = analysis.Score.Reasons;
    item.OpportunityScore = analysis.Score.HardRejected ? 0 : analysis.Score.Score;

    if (analysis.Profit is ProfitBreakdown profit)
    {
        item.EstimatedFees = profit.EbayFees + profit.PromotedListingFees;
        item.EstimatedProfit = profit.NetProfitPerUnit;
        item.EstimatedProfitPercent = profit.RoiPercent;
        item.RoiPercent = profit.RoiPercent;
        item.MarginPercent = profit.MarginPercent;
        item.BreakEvenSalePrice = profit.BreakEvenSalePrice;
    }
}

// Strips generic filler words from a listing title so it can be used as a Terapeak search term
// specific to that one item, instead of the broad (often single-word) original search query.
// Cheap heuristic, not NLP — keeps title word order (brand/model usually comes first) and just
// drops connectors/marketing fluff, capped to a handful of words so the query isn't over-narrow.
static string ExtractKeywords(string title, int maxWords = 3)
{
    string[] stopwords =
    [
        "and", "or", "for", "with", "of", "in", "on", "to", "from", "by", "the", "a", "an",
        "free", "shipping", "fast", "genuine", "authentic", "official", "brand", "nib", "nwt", "oem"
    ];
    // Keep hyphenated alphanumeric codes as one token (e.g. "A06B-6077-K147" stays whole instead
    // of fragmenting into "A06B", "6077", "K147") — splitting a single part number across
    // multiple word-slots was eating into the budget meant for the rest of the title.
    var words = System.Text.RegularExpressions.Regex.Matches(title, @"[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*")
        .Select(m => m.Value)
        .Where(w => w.Length > 1 && !stopwords.Contains(w.ToLowerInvariant()))
        .ToList();

    // A model/part number (e.g. "660" in "EVGA GeForce GTX 660") is the strongest signal for
    // narrowing a Terapeak search to the right item. Truncating at a fixed word count can cut
    // it off, silently turning "EVGA GeForce GTX 660" into "EVGA GeForce" — which prices
    // against every EVGA GeForce card ever sold instead of this specific low-end model.
    // Some titles carry more than one part number — e.g. "Fanuc A06B-6077-K147 Surge Protector
    // A74L-0001-0105" names the parent equipment (A06B-...) it's compatible with BEFORE the
    // part number of the actual item being sold (A74L-...). Stopping at the first digit-bearing
    // token there would price a cheap accessory against comps for expensive parent hardware.
    // Extend through the LAST digit-bearing token instead of the first, so a title with several
    // part numbers still reaches the one that actually identifies the product — capped so a
    // title that's mostly numbers (serials, sizes) can't blow the query out unboundedly.
    var take = maxWords;
    var lastDigitIdx = words.FindLastIndex(w => w.Any(char.IsDigit));
    if (lastDigitIdx >= 0)
        take = Math.Min(Math.Max(take, lastDigitIdx + 1), 8);

    return string.Join(' ', words.Take(Math.Min(take, words.Count)));
}

// Cheap heuristics for the Opportunity Finder's "Poor titles" filter — not AI/NLP, just the
// obvious cases: too short, too few words, or shouty all-caps spam.
static bool HasPoorTitle(string title)
{
    var t = title.Trim();
    if (t.Length < 20) return true;
    if (t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length < 4) return true;
    if (t.Length > 10 && t == t.ToUpperInvariant() && t.Any(char.IsLetter)) return true;
    return false;
}

// Cheap heuristic for the "Misspelled titles" filter — a small common-typo word list plus a
// repeated-letter-run check ("Xboxxx"), not a real dictionary/spellcheck.
static bool HasMisspelledTitle(string title)
{
    string[] commonMisspellings =
    [
        "recieve", "seperate", "definately", "occured", "untill", "wich", "beleive",
        "acessories", "accesories", "orignal", "genuion", "authentc", "excelent",
        "perfet", "conditon", "brandnew", "guarenteed", "warrenty", "shiping",
        "wireles", "controler", "consol", "protable", "compatable"
    ];
    var words = System.Text.RegularExpressions.Regex.Split(title.ToLowerInvariant(), @"[^a-z]+");
    if (words.Any(w => commonMisspellings.Contains(w))) return true;
    // Repeated-letter run (e.g. "Xboxxx") rarely happens in real words — but exclude i/v/x/l,
    // since "III", "XXL", "XXXL" etc. (Roman numerals, size labels) are common and legitimate.
    return System.Text.RegularExpressions.Regex.IsMatch(title, @"([^ivxlIVXL\s])\1{2,}");
}

app.MapPost("/api/generate-photos", async (GeneratePhotosRequest req, ImageGenerationService imgGen, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.Title))
        return Results.BadRequest(new { error = "Title is required to generate product photos." });
    try
    {
        // Use the product photo as img2img reference only when Claude confirms it's a clean product shot
        var refImage = req.ImageType == "product_photo" && !string.IsNullOrWhiteSpace(req.ImageBase64)
            ? req.ImageBase64 : null;
        var urls = await imgGen.GenerateProductPhotosAsync(req.Title, req.Description,
            req.VisualDescription, refImage, string.IsNullOrEmpty(refImage) ? null : req.MimeType);
        return Results.Ok(new { urls });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Image generation failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/image-gen/test", async (ImageGenerationService imgGen) =>
{
    var (online, message) = await imgGen.TestLocalServerAsync();
    return Results.Ok(new { online, message });
});

app.MapGet("/api/image-gen/test-endpoint", async (string endpoint, string backend, ImageGenerationService imgGen) =>
{
    var (online, message) = await imgGen.TestEndpointAsync(endpoint, backend);
    return Results.Ok(new { online, message });
});

app.MapGet("/api/image-gen/detect", async (ImageGenerationService imgGen) =>
{
    var result = await imgGen.DetectLocalServersAsync();
    return Results.Ok(result);
});

app.MapGet("/api/image-gen/comfyui-models", async (string endpoint, ImageGenerationService imgGen) =>
{
    var models = await imgGen.GetComfyUiModelsAsync(endpoint);
    return Results.Ok(new { models });
});

app.MapGet("/api/image-gen/mode", (CredentialsStore store) =>
{
    var c = store.Get();
    return Results.Ok(new { mode = c.ImageGenMode ?? "disabled" });
});

// ── eBay OAuth ────────────────────────────────────────────────────
app.MapGet("/api/ebay/auth-url", (EbayService ebay) =>
    Results.Ok(new { url = ebay.GetAuthorizationUrl() }));

// Legacy direct callback (sandbox / local dev only)
app.MapGet("/api/ebay/callback", async (string code, EbayService ebay, CredentialsStore store, HttpContext ctx) =>
{
    var token = await ebay.ExchangeCodeForTokenResultAsync(code);
    store.SaveOAuthTokensFull(token.AccessToken, token.RefreshToken, token.ExpiresIn, token.RefreshTokenExpiresIn, token.TokenType);
    ctx.Response.Redirect("/");
});

// Server-relay finish: eBay → inglisting.com/api/ebay/callback (PHP) → here
// PHP already exchanged the code; this endpoint fetches the tokens from the server pickup endpoint.
app.MapGet("/api/ebay/finish", async (string session, string pickup, EbayService ebay, CredentialsStore store, ActionLog log, IHttpClientFactory httpFactory, HttpContext ctx) =>
{
    // Validate session matches what we generated (CSRF check)
    if (ebay.PendingOAuthSession != session)
    {
        log.Add("Warning", "OAuth finish: state mismatch", $"Expected {ebay.PendingOAuthSession}, got {session}");
        ctx.Response.Redirect("/?ebay_error=state_mismatch");
        return;
    }

    try
    {
        var client = httpFactory.CreateClient();
        var pickupUrl = $"https://inglisting.com/api/ebay/pickup/?session={Uri.EscapeDataString(session)}&pickup={Uri.EscapeDataString(pickup)}";
        var res = await client.GetAsync(pickupUrl);
        var body = await res.Content.ReadAsStringAsync();

        if (!res.IsSuccessStatusCode)
        {
            log.Add("Warning", "OAuth pickup failed", body);
            ctx.Response.Redirect("/?ebay_error=pickup_failed");
            return;
        }

        var tokens = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(body)!;
        var accessToken   = tokens.GetValueOrDefault("access_token").GetString()   ?? "";
        var refreshToken  = tokens.GetValueOrDefault("refresh_token").GetString()  ?? "";
        var expiresIn     = tokens.GetValueOrDefault("expires_in").TryGetInt32(out var ei) ? ei : 7200;
        var refreshExpiry = tokens.GetValueOrDefault("refresh_token_expires_in").TryGetInt32(out var ri) ? ri : 47304000;
        var tokenType     = tokens.GetValueOrDefault("token_type").GetString()     ?? "User Access Token";

        store.SaveOAuthTokensFull(accessToken, refreshToken, expiresIn, refreshExpiry, tokenType);
        log.Add("Info", "eBay OAuth connected via server relay", "Tokens saved successfully.");
        ctx.Response.Redirect("/?ebay_connected=1");
    }
    catch (Exception ex)
    {
        log.Add("Error", "OAuth finish exception", ex.Message);
        ctx.Response.Redirect("/?ebay_error=exception");
    }
});

app.MapPost("/api/ebay/token", async (EbayAuthRequest req, EbayService ebay, CredentialsStore store) =>
{
    var token = await ebay.ExchangeCodeForTokenResultAsync(req.Code);
    store.SaveOAuthTokensFull(token.AccessToken, token.RefreshToken, token.ExpiresIn, token.RefreshTokenExpiresIn, token.TokenType);
    return Results.Ok(new { hasToken = !string.IsNullOrWhiteSpace(token.AccessToken), hasRefreshToken = !string.IsNullOrWhiteSpace(token.RefreshToken) });
});

app.MapPost("/api/ebay/exchange-redirect-url", async (EbayOAuthRedirectRequest req, EbayService ebay, CredentialsStore store, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.RedirectUrl))
        return Results.BadRequest("Paste the full eBay OAuth redirect URL.");

    try
    {
        var result = await ebay.ExchangeProductionRedirectUrlAsync(req.RedirectUrl);
        store.SaveOAuthTokensFull(result.Token, result.RefreshToken, result.ExpiresIn, result.RefreshTokenExpiresIn, result.TokenType);
        log.Add("Info", "Production eBay OAuth connected", $"Accepted URL: {result.AcceptedUrl}; Redirect URI: {result.RedirectUri}; State: {result.State}");

        return Results.Ok(new
        {
            hasToken = true,
            hasRefreshToken = !string.IsNullOrWhiteSpace(result.RefreshToken),
            acceptedUrl = result.AcceptedUrl,
            redirectUri = result.RedirectUri,
            state = result.State,
            message = "Production eBay OAuth token saved locally."
        });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Production OAuth exchange failed", ex.Message);
        return Results.BadRequest(ex.Message);
    }
});

app.MapGet("/api/ebay/policies", async (EbayService ebay, ActionLog log) =>
{
    try
    {
        var result = await ebay.GetBusinessPoliciesAsync();
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Business policy load failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/ebay/token-status", (CredentialsStore store) =>
    Results.Ok(new
    {
        hasToken = !string.IsNullOrWhiteSpace(store.GetUserToken()),
        hasRefreshToken = !string.IsNullOrWhiteSpace(store.GetRefreshToken())
    }));

app.MapPost("/api/ebay/disconnect", (CredentialsStore store) =>
{
    store.ClearEbayTokens();
    return Results.Ok();
});

// ── Listings ──────────────────────────────────────────────────────
app.MapGet("/api/ebay/listings", async (EbayService ebay, ActionLog log) =>
{
    try
    {
        var listings = await ebay.GetListingsAsync();
        log.Add("Info", $"Import complete: {listings.Count} listing(s)", listings.Count == 0
            ? "Zero active listings found via Inventory API. See earlier log entries for details."
            : $"First: {listings.FirstOrDefault()?.Title ?? "(no title)"}");
        return Results.Ok(listings);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Import listings endpoint failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/local-db/status", (ListingDatabase db) => Results.Ok(db.GetStatus()));

app.MapGet("/api/local-listings/placeholder", () => Results.Ok(PlaceholderListings.Get()));

app.MapGet("/api/photos/default-folders", (PhotoLibrary photos) => Results.Ok(photos.GetDefaultFolders()));

// ── Representative-photo library (USED items) ───────────────────────────────────────────────
// Every model folder + its photo URLs, so the UI can show/manage the seller's real stock photos.
app.MapGet("/api/photos/library", (PhotoLibrary photos) =>
    Results.Ok(photos.GetAllFolders().Select(f => new { f.ModelKey, f.ImageCount, photos = photos.ListPhotoUrls(f.ModelKey) })));

// Create an empty model folder (e.g. when the seller starts a new model's photo set).
app.MapPost("/api/photos/library/create", (LibraryCreateRequest req, PhotoLibrary photos) =>
{
    if (string.IsNullOrWhiteSpace(req.ModelKey)) return Results.BadRequest(new { error = "ModelKey is required" });
    try { return Results.Ok(new { modelKey = photos.CreateFolder(req.ModelKey) }); }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Save one of the seller's REAL photos into a model's library. Send an already-cleaned image
// (e.g. the /api/photos/remove-bg output re-encoded) or the raw upload — both are stored as-is.
app.MapPost("/api/photos/library/upload", async (LibraryUploadRequest req, PhotoLibrary photos, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ModelKey) || string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest(new { error = "ModelKey and ImageBase64 are required" });
    try
    {
        var ext = (req.MimeType ?? "").Contains("png") ? "png" : "jpg";
        var url = await photos.SavePhotoAsync(req.ModelKey, Convert.FromBase64String(req.ImageBase64), ext);
        log.Add("Info", "Representative photo saved", $"{req.ModelKey} -> {url}");
        return Results.Ok(new { url });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

// Drop a single photo from a model's library — the seller culling a bad shot from the set that
// every future unit of this model reuses. Already-published listings keep their uploaded copies.
app.MapPost("/api/photos/library/delete", (LibraryDeleteRequest req, PhotoLibrary photos, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ModelKey) || string.IsNullOrWhiteSpace(req.FileName))
        return Results.BadRequest(new { error = "ModelKey and FileName are required" });
    if (!photos.DeletePhoto(req.ModelKey, req.FileName))
        return Results.NotFound(new { error = "Photo not found" });
    log.Add("Info", "Representative photo deleted", $"{req.ModelKey}/{req.FileName}");
    return Results.Ok(new { deleted = true });
});

// For a used listing: return the matching model's representative photos + the disclosure line to
// append to the description. matched=false means no library set yet for this model (prompt to add).
app.MapGet("/api/photos/library/for-listing", (string? model, string? title, PhotoLibrary photos) =>
{
    var m = photos.ResolveForListing(model, title);
    return m is null
        ? Results.Ok(new { matched = false })
        : Results.Ok(new { matched = true, m.ModelKey, photos = m.PhotoUrls, disclosure = m.Disclosure });
});

app.MapPost("/api/photos/fetch-url", async (FetchPhotoUrlRequest req, IHttpClientFactory http, IWebHostEnvironment env, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.Url))
        return BadInputJson("No image link given",
            "There was no URL to fetch a picture from.",
            "Paste the image's address, or drop the picture straight into the photo box.");

    return await Guarded(FailureDomain.Photos, "Fetch photo by URL", log, async () =>
    {
        using var client = http.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        var bytes = await client.GetByteArrayAsync(req.Url);
        var ext = req.Url.Contains(".png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpg";
        var photosDir = Path.Combine(env.ContentRootPath, "generated-photos");
        Directory.CreateDirectory(photosDir);
        var filename = $"fetched_{Guid.NewGuid():N}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(photosDir, filename), bytes);
        var url = $"/generated-photos/{filename}";
        log.Add("Info", "Product photo fetched", req.Url[..Math.Min(80, req.Url.Length)]);
        return new { url };
    });
});

// A photo that fails to save here used to be the app's quietest and most expensive failure. Both
// `Convert.FromBase64String` (truncated paste) and `WriteAllBytesAsync` (full disk, antivirus lock)
// threw straight out to a 500, and every caller in app.js swallowed it as `catch { /* non-fatal */ }`
// — so the listing carried on to publish with no photograph and nobody was told. A photoless eBay
// listing does not sell, which makes a silent skip here worse than a loud refusal.
app.MapPost("/api/photos/save-uploaded", async (SaveUploadedPhotoRequest req, IWebHostEnvironment env, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ImageBase64))
        return BadInputJson("No image data arrived",
            "The photo did not reach the app, so there was nothing to save.",
            "Add the photo again.");

    return await Guarded(FailureDomain.Photos, "Save uploaded photo", log, async () =>
    {
        var photosDir = Path.Combine(env.ContentRootPath, "generated-photos");
        Directory.CreateDirectory(photosDir);

        var ext = (req.MimeType ?? "").Contains("png") ? "png" : "jpg";
        var filename = $"upload_{Guid.NewGuid():N}.{ext}";
        await File.WriteAllBytesAsync(Path.Combine(photosDir, filename), Convert.FromBase64String(req.ImageBase64));

        var url = $"/generated-photos/{filename}";
        log.Add("Info", "Uploaded product photo saved", filename);
        return new { url };
    });
});

app.MapPost("/api/photos/remove-bg", async (RemoveBgRequest req, IWebHostEnvironment env, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest("ImageBase64 is required");

    var photosDir = Path.Combine(env.ContentRootPath, "generated-photos");
    Directory.CreateDirectory(photosDir);

    var ext        = (req.MimeType ?? "").Contains("png") ? "png" : "jpg";
    var inputFile  = Path.Combine(Path.GetTempPath(), $"rembg_in_{Guid.NewGuid():N}.{ext}");
    var outputFile = Path.Combine(photosDir, $"rembg_{Guid.NewGuid():N}.png");
    var scriptFile = Path.Combine(Path.GetTempPath(), $"rembg_script_{Guid.NewGuid():N}.py");

    try
    {
        await File.WriteAllBytesAsync(inputFile, Convert.FromBase64String(req.ImageBase64));
        await File.WriteAllTextAsync(scriptFile, """
import sys
from rembg import remove
from PIL import Image
import numpy as np
from scipy import ndimage

img = Image.open(sys.argv[1]).convert('RGBA')
cutout = remove(img).convert('RGBA')

# Drop only tiny stray artifacts — keep all components above 0.5% of total pixels
arr = np.array(cutout)
alpha = arr[:, :, 3]
mask = alpha > 10
total_px = mask.size
min_keep = total_px * 0.005  # anything smaller than 0.5% of image is an artifact

labeled, num_features = ndimage.label(mask)
if num_features > 1:
    sizes = np.array([(labeled == i + 1).sum() for i in range(num_features)])
    keep_mask = np.zeros_like(mask)
    for i, sz in enumerate(sizes):
        if sz >= min_keep:
            keep_mask |= (labeled == i + 1)
    rows_idx, cols_idx = np.where(keep_mask)
else:
    rows_idx, cols_idx = np.where(mask)

if len(rows_idx) > 0:
    pad = 5
    ih, iw = mask.shape
    rmin = max(0, int(rows_idx.min()) - pad)
    rmax = min(ih - 1, int(rows_idx.max()) + pad)
    cmin = max(0, int(cols_idx.min()) - pad)
    cmax = min(iw - 1, int(cols_idx.max()) + pad)
    cutout = cutout.crop((cmin, rmin, cmax + 1, rmax + 1))
else:
    bbox = cutout.getbbox()
    if bbox:
        cutout = cutout.crop(bbox)

# Scale product to fill 88% of a 1000x1000 white canvas
canvas = 1000
product_size = int(canvas * 0.88)
w, h = cutout.size
scale = product_size / max(w, h)
new_w = max(1, int(w * scale))
new_h = max(1, int(h * scale))
cutout = cutout.resize((new_w, new_h), Image.LANCZOS)

# Paste centered on white background
result = Image.new('RGB', (canvas, canvas), (255, 255, 255))
x = (canvas - new_w) // 2
y = (canvas - new_h) // 2
result.paste(cutout, (x, y), cutout)
result.save(sys.argv[2], 'PNG')
""");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName               = "python",
            ArgumentList           = { scriptFile, inputFile, outputFile },
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true
        };

        using var proc   = System.Diagnostics.Process.Start(psi)!;
        var stderr       = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        if (proc.ExitCode != 0 || !File.Exists(outputFile))
            throw new Exception($"rembg failed (exit {proc.ExitCode}): {stderr[..Math.Min(300, stderr.Length)]}");

        var url = $"/generated-photos/{Path.GetFileName(outputFile)}";
        log.Add("Info", "Background removed", Path.GetFileName(outputFile));
        return Results.Ok(new { url });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        // Background removal is a nicety, not a requirement, and the message says so: a seller whose
        // machine has no Python should be told their photo is fine as it is, not left thinking the
        // listing is broken.
        var failure = FailureTranslator.Translate(ex, FailureDomain.Photos);
        log.Add("Warning", "Background removal failed", $"{failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }
    finally
    {
        if (File.Exists(inputFile))  File.Delete(inputFile);
        if (File.Exists(scriptFile)) File.Delete(scriptFile);
    }
});

app.MapPost("/api/bulk-import/extract-links", async (AnalyzeUrlRequest req, IHttpClientFactory http) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest("URL required");
    try
    {
        var client = http.CreateClient();
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36");
        var html = await client.GetStringAsync(req.Url);

        // Extract product links — works for Shopify (/products/), eBay, and generic /product/ patterns
        var matches = System.Text.RegularExpressions.Regex.Matches(
            html, @"href=""(/(?:products|product|listing|item|p)/[^""?#]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var baseUri = new Uri(req.Url);
        var links = matches
            .Select(m => new Uri(baseUri, m.Groups[1].Value).ToString())
            .Distinct()
            .Where(u => !u.Contains("/collections/") && !u.Contains("/categories/"))
            .Take(50)
            .ToList();

        return Results.Ok(new { links });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/ebay/category-children", async (string? id, EbayService ebay) =>
{
    var children = await ebay.GetCategoryChildrenAsync(id ?? "0");
    return Results.Ok(children);
});

app.MapGet("/api/ebay/category-suggestions", async (string q, EbayService ebay, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
        return Results.Ok(Array.Empty<object>());
    try
    {
        var suggestions = await ebay.GetCategorySuggestionsAsync(q);
        return Results.Ok(suggestions);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Category suggestions failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ── Listing readiness: what eBay requires, before the seller presses Publish ──────────────────
//
// eBay does not tell a seller what a category requires until the publish fails. The Taxonomy API
// knows up front, and these two endpoints are the app finally asking it.
//
// Both always answer 200 with a renderable body. A readiness check that fails is a readiness
// check that gets in the way of listing, and the point of it is the opposite.

// The raw aspect list for a category — used by the UI to render the Item Specifics fields.
app.MapGet("/api/ebay/category-aspects", async (string? categoryId, EbayService ebay, CredentialsStore store, ActionLog log) =>
{
    var result = new CategoryAspectsResult { CategoryId = categoryId ?? "" };

    if (string.IsNullOrWhiteSpace(categoryId))
    {
        result.Status  = "no_category";
        result.Message = "Pick a category first — required Item Specifics are set per category.";
        return Results.Ok(result);
    }

    if (string.IsNullOrWhiteSpace(store.GetUserToken()))
    {
        result.Status  = "not_connected";
        result.Message = "Connect eBay to see which Item Specifics this category requires.";
        return Results.Ok(result);
    }

    try
    {
        result.Aspects = await ebay.GetCategoryAspectsAsync(categoryId);
        if (result.Aspects.Count == 0)
            result.Message = "eBay lists no required Item Specifics for this category.";
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Category aspects lookup failed", ex.Message);
        result.Status  = "error";
        result.Message = "Couldn't reach eBay for this category's Item Specifics: " + ex.Message;
        return Results.Ok(result);
    }
});

// Score the whole draft. The aspect lookup is one part of it, so a listing is still scored on
// title, photos, identifiers and description when eBay can't be reached — and says which half
// it couldn't check rather than implying everything passed.
app.MapPost("/api/listing/readiness", async (
    ReadinessRequest req, EbayService ebay, CredentialsStore store,
    ProductIdentityExtractor identity, ActionLog log) =>
{
    var aspectStatus  = "ok";
    var aspectMessage = "";
    List<CategoryAspect> aspects = [];

    if (req.SkipAspects)
    {
        aspectStatus  = "skipped";
        aspectMessage = "Item Specifics not checked on this pass.";
    }
    else if (string.IsNullOrWhiteSpace(req.CategoryId))
    {
        aspectStatus  = "no_category";
        aspectMessage = "Pick a category to check what eBay requires here.";
    }
    else if (string.IsNullOrWhiteSpace(store.GetUserToken()))
    {
        aspectStatus  = "not_connected";
        aspectMessage = "Connect eBay to check this category's required Item Specifics.";
    }
    else
    {
        try
        {
            aspects = await ebay.GetCategoryAspectsAsync(req.CategoryId);
        }
        catch (Exception ex)
        {
            log.Add("Warning", "Readiness: aspect lookup failed", ex.Message);
            aspectStatus  = "error";
            aspectMessage = "eBay didn't answer the Item Specifics check: " + ex.Message;
        }
    }

    // The identity extractor is the same parser the sold-comps pipeline reads titles with, so a
    // listing and its comparables are interpreted by one set of rules rather than two.
    ProductIdentity? parsed = null;
    try { parsed = identity.Extract(req.Title); }
    catch (Exception ex) { log.Add("Warning", "Readiness: title parse failed", ex.Message); }

    var facts = new AspectMatcher.ListingFacts(
        Title:           req.Title ?? "",
        DescriptionText: AspectMatcher.StripHtml(req.Description),
        Brand:           req.Brand ?? "",
        Mpn:             req.Mpn ?? "",
        Upc:             req.Upc ?? "",
        Ean:             req.Ean ?? "",
        Isbn:            req.Isbn ?? "",
        Condition:       req.Condition ?? "",
        Identity:        parsed);

    var (fields, custom) = AspectMatcher.Evaluate(
        aspects, req.ItemSpecifics ?? [], facts);

    var result = ListingReadinessAnalyzer.Analyze(req, fields, aspectStatus, aspectMessage, custom);
    return Results.Ok(result);
});

app.MapPost("/api/ebay/upload-picture", async (RemoveBgRequest req, EbayService ebay, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest(new { error = "ImageBase64 is required" });
    try
    {
        var url = await ebay.UploadPictureToEpsAsync(req.ImageBase64, req.MimeType ?? "image/jpeg");
        log.Add("Info", "Picture uploaded to eBay EPS", url[..Math.Min(80, url.Length)]);
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "eBay picture upload failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/logs/recent", (ActionLog log) => Results.Ok(log.Recent()));

// ── Local Drafts ──────────────────────────────────────────────────
app.MapGet("/api/local-drafts/ensure-folder", (DraftStore drafts) =>
    Results.Ok(new { path = drafts.EnsureFolder() }));

app.MapGet("/api/local-drafts/list", (DraftStore drafts) => Results.Ok(drafts.ListDrafts()));

app.MapPost("/api/local-drafts/save", (DraftFile draft, DraftStore drafts, ActionLog log) =>
{
    var filename = drafts.SaveDraft(draft);
    log.Add("Info", "Draft saved locally", $"{filename}");
    return Results.Ok(new { filename });
});

app.MapGet("/api/local-drafts/load/{filename}", (string filename, DraftStore drafts) =>
{
    var draft = drafts.LoadDraft(filename);
    return draft != null ? Results.Ok(draft) : Results.NotFound();
});

app.MapDelete("/api/local-drafts/delete/{filename}", (string filename, DraftStore drafts, ActionLog log) =>
{
    drafts.DeleteDraft(filename);
    log.Add("Info", "Draft deleted", filename);
    return Results.Ok();
});

// ── Cross-listing exporter ────────────────────────────────────────
// Reformats an existing draft for Facebook Marketplace / Mercari / Amazon. Purely local text and
// CSV generation — it never contacts those sites and never touches the eBay listing.
app.MapPost("/api/crosslist/export", (CrossListRequest req, CrossListingExporter exporter, ActionLog log) =>
{
    try
    {
        var result = exporter.Export(req);
        var titlePreview = (req.Title ?? "").Trim();
        if (titlePreview.Length > 60) titlePreview = titlePreview[..60] + "…";
        log.Add("Info", "Cross-list export generated",
            $"{result.Listings.Count} marketplace(s) for: {titlePreview}");
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Cross-list export failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/listing/seller-hub-draft", async (PostListingRequest req, EbayService ebay, ActionLog log) =>
{
    var titlePreview = (req.Title ?? "").Trim();
    if (titlePreview.Length > 60) titlePreview = titlePreview[..60] + "…";
    log.Add("Info", "Seller Hub Draft: endpoint called", $"Title: {titlePreview}");

    try
    {
        var result = await ebay.CreateSellerHubDraftAsync(req);
        log.Add("Info", "Seller Hub Draft: succeeded", $"DraftId: {result.DraftId}; URL: {result.SellerHubUrl}");
        return Results.Ok(new { ok = true, draftId = result.DraftId, sellerHubUrl = result.SellerHubUrl });
    }
    catch (Exception ex)
    {
        var shortError = ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message;
        log.Add("Warning", "Seller Hub Draft: failed", shortError);
        return Results.Json(new { ok = false, error = shortError, details = ex.Message }, statusCode: 400);
    }
});

app.MapPost("/api/listing/post", async (PostListingRequest req, EbayService ebay, ActionLog log) =>
{
    var titlePreview = (req.Title ?? "").Trim();
    if (titlePreview.Length > 60) titlePreview = titlePreview[..60] + "…";
    log.Add("Info", "Create Draft: endpoint called", $"Title: {titlePreview}; CategoryId: {req.CategoryId}; Price: {req.Price:F2}");

    try
    {
        var offerId = await ebay.CreateListingAsync(req, req.EbayToken);
        log.Add("Info", "Create Draft: succeeded", $"OfferId: {offerId}");
        return Results.Ok(new
        {
            ok = true,
            offerId,
            listingId = "",
            status = "Draft",
            message = "Draft offer created. It has not been published."
        });
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Ebay);
        log.Add("Warning", "Create Draft: failed", $"{failure.Kind} — {failure.Technical}");
        // `where` is kept: nlHighlightPolicyIssues reads it to point at the field that needs fixing.
        return Results.Json(new
        {
            ok = false,
            error = failure.Headline,
            details = string.IsNullOrWhiteSpace(failure.Technical) ? failure.WhatHappened : failure.Technical,
            where = "CreateDraft",
            failure = new
            {
                kind = failure.Kind.ToString(),
                domain = failure.Domain.ToString(),
                headline = failure.Headline,
                whatHappened = failure.WhatHappened,
                whatToDo = failure.WhatToDo,
                retryable = failure.Retryable,
                retryAfterSeconds = failure.RetryAfterSeconds,
                fixAction = failure.FixAction,
                attempts = failure.Attempts,
                workPreserved = true,
                technical = failure.Technical,
            },
        }, statusCode: 400);
    }
});

// The one endpoint that creates something buyers can see and pay for, and therefore the one whose
// failure handling has to assume its own error report might be wrong.
//
// A publish that fails on the way back — a timeout, a dropped connection, an eBay 500 — does not mean
// eBay declined to create the listing. It means the app never heard the answer. Reporting that as a
// plain failure invites the seller to press Publish again, and the outcome is two live listings for
// one physical item: two insertion fees, two audiences, and an oversell the moment one sells. So on
// exactly those failures the app looks at the account before it says anything.
app.MapPost("/api/listing/publish", async (PostListingRequest req, EbayService ebay, ActionLog log,
    CredentialsStore store, LicenseService license, PublishGuard guard) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;

    if (string.IsNullOrWhiteSpace(req.Title))
        return BadInputJson("This listing has no title",
            "eBay will not accept a listing without one, so nothing was sent.",
            "Add a title, then publish.");
    if (req.Price <= 0)
        return BadInputJson("This listing has no price",
            "eBay will not accept a price of zero, so nothing was sent.",
            "Set a price above zero, then publish.");

    var fingerprint = PublishGuard.Fingerprint(req);
    var verdict = guard.Begin(fingerprint, req.WorkKey);

    if (verdict.Decision == PublishDecision.AlreadyPublished)
        return Results.Ok(new
        {
            offerId = "",
            listingId = verdict.ListingId,
            sku = "",
            listingUrl = string.IsNullOrEmpty(verdict.ListingId) ? "" : $"https://www.ebay.com/itm/{verdict.ListingId}",
            alreadyPublished = true,
            message = "This listing is already live — it was published moments ago, so a second copy was not "
                    + "created. Open it below to check.",
        });

    if (verdict.Decision == PublishDecision.AlreadyRunning)
        return FailureJson(new FailureInfo
        {
            Kind = FailureKind.BadInput,
            Domain = FailureDomain.Ebay,
            Headline = "This listing is already being published",
            WhatHappened = "An identical publish is still running. Sending a second would risk two live "
                         + "listings for one item.",
            WhatToDo = "Wait for the one in progress to finish. Refresh your listings in a moment to see it.",
            Retryable = false,
        });

    try
    {
        var result = await ebay.PublishListingAsync(req);
        guard.Succeeded(fingerprint, req.WorkKey, result.ListingId);

        var listingUrl = !string.IsNullOrEmpty(result.ListingId)
            ? $"https://www.ebay.com/itm/{result.ListingId}"
            : "";
        log.Add("Info", "eBay listing published live", $"Listing ID: {result.ListingId}; Offer ID: {result.OfferId}; SKU: {result.Sku}");
        return Results.Ok(new { result.OfferId, result.ListingId, result.Sku, listingUrl });
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Ebay);
        guard.Failed(fingerprint, req.WorkKey, failure.Technical);
        log.Add("Warning", "eBay publish failed", $"{failure.Kind} — {failure.Technical}");

        // Only these three can lie about the outcome. An eBay rejection is a definite no: nothing was
        // created, so looking for it would only risk matching some earlier listing with the same title.
        if (failure.Kind is FailureKind.Timeout or FailureKind.Network or FailureKind.UpstreamServerError)
        {
            var found = await TryFindJustPublishedAsync(ebay, log, req.Title);
            if (found is not null)
            {
                guard.Succeeded(fingerprint, req.WorkKey, found.ListingId);
                log.Add("Info", "Publish reconciled after a failed response",
                    $"eBay had created listing {found.ListingId} despite the error — no duplicate was made.");
                return Results.Ok(new
                {
                    offerId = found.OfferId,
                    listingId = found.ListingId,
                    sku = found.Sku,
                    listingUrl = string.IsNullOrEmpty(found.ListingUrl)
                        ? $"https://www.ebay.com/itm/{found.ListingId}"
                        : found.ListingUrl,
                    reconciled = true,
                    message = "The connection to eBay dropped before it confirmed — but the listing did go "
                            + "live. It is shown below. Nothing was published twice.",
                });
            }

            return FailureJson(failure with
            {
                WhatHappened = failure.WhatHappened + " The app then checked your active listings and did not "
                             + "find it, so it does not appear to have gone live.",
                WhatToDo = "Publishing again is safe — the check above found no listing for it. Your listing is "
                         + "saved and still on screen.",
                Retryable = true,
            });
        }

        return FailureJson(failure);
    }
});

// Answers "is it actually live?" — used both after a failed publish and by the seller's own
// Check eBay button.
//
// Never throws. It is called on a path that has already failed once, and a reconciliation that
// itself blows up would replace a recoverable situation with a confusing one.
static async Task<EbayListingSummary?> TryFindJustPublishedAsync(EbayService ebay, ActionLog log, string? title)
{
    try
    {
        var active = await ebay.GetListingsAsync();
        return PublishGuard.MatchPublished(active, title, DateTimeOffset.UtcNow, PublishGuard.DuplicateWindow);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Could not check whether the listing went live", ex.Message);
        return null;
    }
}

// The Check eBay button. Deliberately read-only: it looks, reports, and changes nothing — the
// decision to publish again stays the seller's.
app.MapPost("/api/listing/check-published", async (PostListingRequest req, EbayService ebay, ActionLog log,
    PublishGuard guard) =>
{
    if (string.IsNullOrWhiteSpace(req.Title))
        return BadInputJson("Nothing to look for",
            "Checking eBay matches on the listing's title, and there isn't one.",
            "Add the title back, then check again.");

    var found = await TryFindJustPublishedAsync(ebay, log, req.Title);
    if (found is null)
        return Results.Ok(new
        {
            found = false,
            message = "No live listing with this title was found on your account, so it did not go through. "
                    + "Publishing again is safe.",
        });

    // Recorded so the duplicate guard knows about a listing it never saw created — a publish that
    // succeeded during an app restart, for instance.
    guard.Succeeded(PublishGuard.Fingerprint(req), req.WorkKey, found.ListingId);

    return Results.Ok(new
    {
        found = true,
        listingId = found.ListingId,
        listingUrl = string.IsNullOrEmpty(found.ListingUrl)
            ? $"https://www.ebay.com/itm/{found.ListingId}"
            : found.ListingUrl,
        price = found.Price,
        message = "It is live on eBay — the earlier error was only the reply getting lost. Do not publish "
                + "again or you will have two listings for one item.",
    });
});

// The local save is the seller's safety net — it is what "your work is kept" means on every failure
// message elsewhere — so it gets the same treatment as the paths that talk to eBay. A locked database
// used to surface here as a 500 with an HTML body on the one action whose whole job is not losing work.
app.MapPost("/api/local-listings/save-edit", (UpdateListingRequest req, ListingDatabase db, ActionLog log) =>
{
    try
    {
        var result = db.SaveEdit(req);
        log.Add("Info", "Local edit saved", string.IsNullOrWhiteSpace(req.Sku) ? req.Title : req.Sku);
        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Storage);
        log.Add("Warning", "Local edit save failed", $"{failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }
});

app.MapPost("/api/listing/update", async (UpdateListingRequest req, EbayService ebay, ActionLog log) =>
{
    if (!req.ManualRevisionConfirmed)
    {
        log.Add("Warning", "eBay revise blocked", "Manual revision confirmation was missing.");
        return BadInputJson("This change was not confirmed",
            "Revising a live listing needs an explicit confirmation, and none was sent — so nothing on eBay "
          + "was touched.",
            "Confirm the change and try again.");
    }

    // Was an unguarded `await`: any eBay refusal on a live revision — an expired token most of all —
    // reached the browser as a 500 HTML page, on an action the seller had just explicitly confirmed.
    return await Guarded(FailureDomain.Ebay, "Revise live eBay listing", log, async () =>
    {
        await ebay.UpdateListingAsync(req);
        log.Add("Info", "eBay listing revised", string.IsNullOrWhiteSpace(req.Sku) ? req.OfferId : req.Sku);
        return new { ok = true };
    });
});

// ── Crash recovery: keeping a listing in progress alive outside the browser tab ────
//
// Before this, a listing being written existed only in the DOM. A Claude-written title, description
// and item specifics cost real API spend and a minute or two of waiting, and every one was a single
// accidental tab close, refresh, or crash away from being gone with no trace and no way back.
app.MapPost("/api/work/autosave", (WorkAutosaveRequest req, WorkRecoveryStore work, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.Key))
        return BadInputJson("Autosave had no key",
            "The app could not tell which draft to save against.",
            "Reload the page — your current work is still on screen.");

    try
    {
        var saved = work.Save(new WorkSnapshot
        {
            Key = req.Key,
            Label = req.Label ?? "",
            Stage = string.IsNullOrWhiteSpace(req.Stage) ? WorkStage.Editing : req.Stage!,
            Payload = req.Payload ?? "",
        });

        // Reported rather than thrown. Autosave runs while the seller types; interrupting them to
        // announce that a background save was too big would be worse than the problem.
        if (!saved)
        {
            log.Add("Warning", "Autosave skipped", $"Payload for {req.Key} exceeded {WorkRecoveryStore.MaxPayloadBytes} bytes.");
            return Results.Ok(new { saved = false, reason = "too_large" });
        }

        return Results.Ok(new { saved = true });
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Storage);
        log.Add("Warning", "Autosave failed", $"{failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }
});

app.MapGet("/api/work/recoverable", (WorkRecoveryStore work, ActionLog log) =>
{
    try
    {
        var rows = work.Recoverable();
        return Results.Ok(new
        {
            items = rows.Select(r => new
            {
                key = r.Key,
                label = r.Label,
                stage = r.Stage,
                payload = r.Payload,
                lastError = r.LastError,
                listingId = r.ListingId,
                attemptCount = r.AttemptCount,
                updatedUtc = r.UpdatedUtc,
                // A row still marked publishing means the app went down between sending the listing
                // and hearing back. The seller needs to be told the outcome is unknown rather than
                // have it hidden on the assumption it worked.
                outcomeUnknown = r.Stage == WorkStage.Publishing,
            }),
        });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Could not read recoverable work", ex.Message);
        return Results.Ok(new { items = Array.Empty<object>() });
    }
});

app.MapPost("/api/work/discard", (WorkDiscardRequest req, WorkRecoveryStore work, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.Key)) return Results.Ok(new { discarded = false });
    try
    {
        var discarded = work.Discard(req.Key);
        if (discarded) log.Add("Info", "Recovered draft discarded", req.Key);
        return Results.Ok(new { discarded });
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Storage);
        return FailureJson(failure);
    }
});

// ── Owner dashboard ───────────────────────────────────────────────
app.MapGet("/api/owner/stats", (string? k, CredentialsStore store, AnalyticsStore analytics, ActionLog log, StripeService stripe) =>
{
    var adminKey = store.EnsureAdminKey();
    if (string.IsNullOrWhiteSpace(k) || k != adminKey)
        return Results.Unauthorized();
    var snap = analytics.GetSnapshot();
    return Results.Ok(new
    {
        analytics       = snap,
        recentLogs      = log.Recent(),
        stripeConfigured = stripe.IsConfigured,
        dashboardUrl    = $"{baseUrl}/owner?k={adminKey}"
    });
});

app.MapGet("/owner", (string? k, CredentialsStore store, StripeService stripe) =>
{
    var adminKey = store.EnsureAdminKey();
    if (string.IsNullOrWhiteSpace(k) || k != adminKey)
        return Results.Content("<html><body><h2>401 Unauthorized</h2></body></html>", "text/html", statusCode: 401);
    var stripeConfigured = stripe.IsConfigured;
    var stripePubKey     = stripe.PublishableKey ?? "";

    var html = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8"/>
<meta name="viewport" content="width=device-width,initial-scale=1"/>
<title>Owner Dashboard — ING Listing Engine</title>
<style>
  *{box-sizing:border-box;margin:0;padding:0}
  body{font-family:system-ui,sans-serif;background:#0f1117;color:#e2e8f0;min-height:100vh;padding:2rem}
  h1{font-size:1.5rem;font-weight:700;margin-bottom:1.5rem;color:#f8fafc}
  h2{font-size:1rem;font-weight:600;margin-bottom:.75rem;color:#94a3b8;text-transform:uppercase;letter-spacing:.05em}
  .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(160px,1fr));gap:1rem;margin-bottom:2rem}
  .card{background:#1e2330;border:1px solid #2d3748;border-radius:10px;padding:1.25rem}
  .card-val{font-size:2rem;font-weight:700;color:#60a5fa}
  .card-lbl{font-size:.75rem;color:#64748b;margin-top:.25rem}
  table{width:100%;border-collapse:collapse;background:#1e2330;border-radius:10px;overflow:hidden;margin-bottom:2rem}
  th{background:#2d3748;padding:.6rem 1rem;text-align:left;font-size:.75rem;color:#94a3b8;text-transform:uppercase}
  td{padding:.6rem 1rem;border-top:1px solid #2d3748;font-size:.85rem}
  .log-warn{color:#f59e0b}.log-info{color:#94a3b8}
  .badge{display:inline-block;padding:.2rem .5rem;border-radius:4px;font-size:.7rem;font-weight:600}
  .badge-warn{background:#78350f;color:#fcd34d}.badge-info{background:#1e3a5f;color:#93c5fd}
  #status{color:#94a3b8;font-size:.85rem;margin-bottom:1rem}
  .refresh-btn{background:#2563eb;color:#fff;border:none;border-radius:6px;padding:.5rem 1rem;cursor:pointer;font-size:.85rem}
  .refresh-btn:hover{background:#1d4ed8}
</style>
</head>
<body>
<h1>ING Listing Engine™ — Owner Dashboard</h1>
<div id="status">Loading…</div>
<button class="refresh-btn" onclick="load()">Refresh</button>

<div style="background:#1e2330;border:1px solid #2d3748;border-radius:10px;padding:1.25rem;margin-bottom:2rem">
  <h2 style="margin-bottom:.75rem">Stripe / Monetization</h2>
  <div style="display:flex;gap:2rem;flex-wrap:wrap;font-size:.9rem">
    <div><span style="color:#64748b">Status:</span> <strong style="color:{{(stripeConfigured ? "#4ade80" : "#f87171")}}">{{(stripeConfigured ? "✓ Configured" : "✗ Not configured")}}</strong></div>
    <div><span style="color:#64748b">Trial:</span> <strong style="color:#4ade80">7 days free</strong></div>
    <div><span style="color:#64748b">Monthly:</span> <strong style="color:#60a5fa">$29.99/mo</strong></div>
    <div><span style="color:#64748b">Annual:</span> <strong style="color:#60a5fa">$249.99/yr</strong></div>
    <div><span style="color:#64748b">Publishable key:</span> <code style="font-size:.75rem;color:#94a3b8">{{(stripePubKey.Length > 16 ? stripePubKey[..16] + "…" : "(none)")}}</code></div>
  </div>
  <div style="margin-top:.75rem;font-size:.8rem;color:#64748b">
    Checkout endpoints: <code style="color:#93c5fd">POST /api/stripe/checkout</code> (monthly) &nbsp;|&nbsp; <code style="color:#93c5fd">POST /api/stripe/checkout/annual</code>
  </div>
  <div style="margin-top:1rem">
    <a href="https://dashboard.stripe.com/" target="_blank" rel="noopener"
       style="display:inline-block;background:#635bff;color:#fff;text-decoration:none;padding:.6rem 1.1rem;border-radius:8px;font-weight:700;font-size:.85rem">
      {{(stripeConfigured ? "Manage Payments in Stripe →" : "Activate Payments — Set up Stripe →")}}
    </a>
  </div>
</div>

<div id="root"></div>
<script>
const KEY = new URLSearchParams(location.search).get('k');
async function load() {
  document.getElementById('status').textContent = 'Loading…';
  const res = await fetch('/api/owner/stats?k=' + encodeURIComponent(KEY));
  if (!res.ok) { document.getElementById('status').textContent = 'Error ' + res.status; return; }
  const d = await res.json();
  const a = d.analytics;
  document.getElementById('status').textContent = 'Last updated: ' + new Date().toLocaleTimeString();
  const stats = [
    { val: (a.uniqueIps||[]).length, lbl: 'Users' },
    { val: a.totalPageLoads, lbl: 'Page Loads' },
    { val: a.aiAnalyses, lbl: 'AI Analyses' },
    { val: a.bulkImports, lbl: 'Bulk Imports' },
    { val: a.listingsPublished, lbl: 'Published' },
    { val: a.draftsSaved, lbl: 'Drafts Saved' },
  ];
  let html = '<div class="grid">' + stats.map(s =>
    `<div class="card"><div class="card-val">${s.val??0}</div><div class="card-lbl">${s.lbl}</div></div>`
  ).join('') + '</div>';
  if (a.firstSeen) html += `<p style="color:#64748b;font-size:.8rem;margin-bottom:2rem">First seen: ${new Date(a.firstSeen).toLocaleString()} &nbsp;|&nbsp; Last seen: ${new Date(a.lastSeen).toLocaleString()}</p>`;
  if ((a.daily||[]).length) {
    html += '<h2>Daily (last 30 days)</h2><table><thead><tr><th>Date</th><th>Page Loads</th><th>Unique IPs</th><th>AI Analyses</th><th>Bulk Imports</th><th>Published</th></tr></thead><tbody>';
    [...a.daily].reverse().forEach(r => {
      html += `<tr><td>${r.date}</td><td>${r.pageLoads}</td><td>${r.uniqueIps}</td><td>${r.aiAnalyses}</td><td>${r.bulkImports}</td><td>${r.listingsPublished}</td></tr>`;
    });
    html += '</tbody></table>';
  }
  if ((d.recentLogs||[]).length) {
    html += '<h2>Recent Logs</h2><table><thead><tr><th>Time</th><th>Level</th><th>Action</th><th>Detail</th></tr></thead><tbody>';
    d.recentLogs.forEach(l => {
      const cls = l.level==='Warning'?'badge-warn':'badge-info';
      html += `<tr><td>${new Date(l.timestamp).toLocaleTimeString()}</td><td><span class="badge ${cls}">${l.level}</span></td><td>${esc(l.title)}</td><td style="color:#64748b">${esc(l.detail)}</td></tr>`;
    });
    html += '</tbody></table>';
  }
  document.getElementById('root').innerHTML = html;
}
function esc(s){const d=document.createElement('div');d.textContent=s??'';return d.innerHTML;}
load();
</script>
</body>
</html>
""";
    return Results.Content(html, "text/html");
});

// ── eBay Sniper ───────────────────────────────────────────────────────────────
app.MapGet("/api/sniper/lookup", async (string itemId, EbayService ebay, ActionLog log) =>
{
    try
    {
        var item = await ebay.GetItemAsync(itemId);
        return Results.Ok(new
        {
            itemId,
            title      = item.Title ?? "",
            endsAt     = (string?)null,   // Trading API GetItem can return EndTime — wired below
            currentBid = item.Price,
        });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Sniper lookup failed", ex.Message);
        return Results.Ok(new { itemId, title = "", endsAt = (string?)null, currentBid = (decimal?)null });
    }
});

app.MapPost("/api/sniper/bid", async (SniperBidRequest req, EbayService ebay, ActionLog log) =>
{
    try
    {
        await ebay.PlaceMaxBidAsync(req.ItemId, req.MaxBid);
        log.Add("Info", "Sniper bid placed", $"Item {req.ItemId} @ ${req.MaxBid:F2}");
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Sniper bid failed", ex.Message);
        return Results.Ok(new { ok = false, error = ex.Message });
    }
});

// ── Service mode: headless web server, lifecycle managed by Windows SCM ──────
if (isWindowsService)
{
    await app.RunAsync(baseUrl);
    return;
}

// ── Interactive mode: background web server + system tray icon ───────────────
var webTask = app.RunAsync(baseUrl);

// Open the browser automatically once Kestrel has bound the port
_ = Task.Run(async () =>
{
    await Task.Delay(1200);
    OpenBrowser();
});

// NOTE: the app is reached at http://localhost:9332 — no hosts-file/local-DNS
// entry is written. Modifying C:\Windows\System32\drivers\etc\hosts is a classic
// malware technique (hosts hijacking) that AV/EDR flags, and it forced a UAC
// prompt for a purely cosmetic hostname alias. Dropping it removes both the
// antivirus risk and the elevation, with no loss of function.

System.Windows.Forms.Application.EnableVisualStyles();
System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);

using var trayIcon = new System.Windows.Forms.NotifyIcon
{
    Icon    = CreateAppIcon(),
    Text    = $"ING AutoLister  •  localhost:{port}",
    Visible = true,
};
trayIcon.ShowBalloonTip(
    3000, "ING AutoLister",
    "Running in background. Right-click this icon to open or quit.",
    System.Windows.Forms.ToolTipIcon.Info);

var ctxMenu = new System.Windows.Forms.ContextMenuStrip();
ctxMenu.Items.Add("Open ING AutoLister", null, (_, _) => OpenBrowser());
ctxMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
ctxMenu.Items.Add("Quit ING AutoLister", null, (_, _) =>
{
    trayIcon.Visible = false;
    System.Windows.Forms.Application.ExitThread();
});
trayIcon.ContextMenuStrip  = ctxMenu;
trayIcon.DoubleClick      += (_, _) => OpenBrowser();

System.Windows.Forms.Application.Run(); // blocks until ExitThread()
await app.StopAsync(TimeSpan.FromSeconds(3));
_mutex?.Dispose();

static System.Drawing.Icon CreateAppIcon()
{
    var bmp = new System.Drawing.Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
    using var g = System.Drawing.Graphics.FromImage(bmp);
    g.SmoothingMode     = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
    g.Clear(System.Drawing.Color.Transparent);

    // Dark teal rounded background
    var teal = System.Drawing.Color.FromArgb(8, 37, 41);   // #082529
    var gold = System.Drawing.Color.FromArgb(199, 154, 54); // #c79a36

    using var bgBrush = new System.Drawing.SolidBrush(teal);
    using var path = new System.Drawing.Drawing2D.GraphicsPath();
    int r = 5;
    path.AddArc(0, 0, r * 2, r * 2, 180, 90);
    path.AddArc(32 - r * 2, 0, r * 2, r * 2, 270, 90);
    path.AddArc(32 - r * 2, 32 - r * 2, r * 2, r * 2, 0, 90);
    path.AddArc(0, 32 - r * 2, r * 2, r * 2, 90, 90);
    path.CloseFigure();
    g.FillPath(bgBrush, path);

    // Gold price-tag arrow: H5 10  H19 L27 16 L19 22 H5 Z  (scaled from 32px viewBox)
    var tagPts = new System.Drawing.PointF[]
    {
        new(4,  9), new(18, 9), new(26, 16),
        new(18, 23), new(4, 23)
    };
    using var tagBrush = new System.Drawing.SolidBrush(gold);
    g.FillPolygon(tagBrush, tagPts);

    // Punch hole
    using var holeBrush = new System.Drawing.SolidBrush(teal);
    g.FillEllipse(holeBrush, 5.5f, 14f, 4f, 4f);

    var handle = bmp.GetHicon();
    return System.Drawing.Icon.FromHandle(handle);
}

void OpenBrowser() =>
    System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(baseUrl) { UseShellExecute = true });

// EnsureLocalDns removed: no hosts-file write means nothing for antivirus/EDR to
// flag as hosts hijacking. The app is reached at http://localhost:9332.
