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
// touching the service's port 9331).
var port    = Environment.GetEnvironmentVariable("AUTOLISTER_DEV_PORT") ?? "9331";
var baseUrl = $"http://localhost:{port}";
var isDevPort = port != "9331";

// ── Elevated helper: add inglist.com → 127.0.0.1 to hosts ──────────
// The installer re-launches with this flag as admin. After adding the entry the
// process exits immediately — it is not the long-running server instance.
// (removed) --add-local-dns hosts-file writer — see note near app startup: the
// hosts write is gone entirely to avoid antivirus/EDR flagging. App runs on
// http://localhost:9331.

// ── Post-install helper: just open the web UI, then exit ──────────────────────
// The MSI runs the exe with this flag when the install finishes. It does NOT bind
// a port or start the tray/server (the installed Windows service already owns
// port 9331), so there's no conflict — it only pops the browser to the running
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
//   2. If the Windows service is already serving on 9331, show a tray icon
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
builder.Services.AddSingleton<LicenseService>();
builder.Services.AddSingleton<StripeService>();
builder.Services.AddSingleton<AnalyticsStore>();
builder.Services.AddSingleton<TerapeakService>();
builder.Services.AddSingleton<TerapeakPriceCache>();
// Local sold-history lookup — read-only against the externally-maintained Marketplace.db at
// C:\INGListing\Data\Marketplace.db (populated by a separate collector process). Feeds the
// Opportunity Finder's Supplier File Analyzer with real local comps before falling back to
// Terapeak. See ExternalMarketplaceDb / MarketplaceRepository for the read-only guarantees.
builder.Services.AddSingleton<ExternalMarketplaceDb>();
builder.Services.AddSingleton<IMarketplaceRepository, MarketplaceRepository>();
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
builder.Services.AddSingleton<OpportunityScoringService>();
builder.Services.AddSingleton<ConfidenceScoringService>();

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

// Stripe keys are configured via the Settings page and stored in credentials.json

// Record install date on first run
app.Services.GetRequiredService<CredentialsStore>().EnsureInstallDate();

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

// ── AI analysis ───────────────────────────────────────────────────
app.MapPost("/api/analyze", async (AnalyzeRequest req, ClaudeService claude, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrEmpty(req.ImageBase64))
        return Results.BadRequest("ImageBase64 is required");
    var listing = await claude.AnalyzeImageAsync(req.ImageBase64, req.MimeType);
    return Results.Ok(listing);
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
    catch (Exception ex)
    {
        log.Add("Warning", "Analyze URL failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
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
        return Results.BadRequest(new { error = "Title or description is required." });
    try
    {
        var improved = await claude.ImproveSeoAsync(req);
        log.Add("Info", "SEO improvement complete", improved.Title);
        return Results.Ok(improved);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "SEO improvement failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/ai-modify", async (ModifyListingRequest req, ClaudeService claude, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.Instruction))
        return Results.BadRequest(new { error = "Instruction is required." });
    try
    {
        var modified = await claude.ModifyListingAsync(req);
        log.Add("Info", "AI modification applied", req.Instruction);
        return Results.Ok(modified);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "AI modification failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/quick-fill", async (QuickFillRequest req, ClaudeService claude, IHttpClientFactory httpFactory, IWebHostEnvironment env, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    if (string.IsNullOrWhiteSpace(req.ItemName))
        return Results.BadRequest(new { error = "Item name is required." });

    try
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

        return Results.Ok(listing);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Quick-fill failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/sold-comps", async (string q, EbayService ebay, TerapeakService terapeak, IMarketplaceRepository marketplace, ActionLog log) =>
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

    // 1) Real Terapeak data, if the seller has connected their session (Settings > Terapeak)
    if (terapeak.IsConnected)
    {
        var scrape = await terapeak.ScrapeAsync(q);
        if (scrape.Status == "ok")
        {
            var parsed = TerapeakMarketService.ParseTerapeakBodyText(scrape.BodyText, q);
            if (parsed is not null)
            {
                var average = await BlendLocalAverageAsync(parsed.Average);
                return Results.Ok(new { parsed.Query, parsed.Items, parsed.Count, Average = average, parsed.Median, parsed.Min, parsed.Max, terapeakUrl, fallbackUrl, source = "terapeak" });
            }
        }
        else if (scrape.Status == "session_expired")
        {
            log.Add("Warning", "Terapeak session expired", "Reconnect in Settings.");
        }
    }

    // 2) Marketplace Insights API (works automatically if eBay ever approves the scope)
    try
    {
        var result = await ebay.SearchSoldCompsAsync(q);
        if (result.Count > 0)
        {
            var average = await BlendLocalAverageAsync(result.Average);
            return Results.Ok(new { result.Query, result.Items, result.Count, Average = average, result.Median, result.Min, result.Max, terapeakUrl, fallbackUrl, source = "marketplace_insights" });
        }
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Sold comps lookup failed", ex.Message);
    }

    // 3) Links only
    return Results.Ok(new { query = q, items = Array.Empty<object>(), count = 0, average = 0, median = 0, min = 0, max = 0, terapeakUrl, fallbackUrl, source = "none" });
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
    var bestOpportunity = best is null ? null : new { best.Title, best.Url, ProfitPercent = best.ProfitPercent, best.IsVerified };

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
    candidate.SellThroughPercent = analysis.SellThrough.SellThroughRate ?? (analysis.SellThrough.RateIsUnbounded ? 100m : candidate.SellThroughPercent);
    candidate.IsHighThroughput = candidate.SellThroughPercent is > 50;

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

app.MapPost("/api/photos/fetch-url", async (FetchPhotoUrlRequest req, IHttpClientFactory http, IWebHostEnvironment env, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.Url)) return Results.BadRequest("URL required");
    try
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
        return Results.Ok(new { url });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/photos/save-uploaded", async (SaveUploadedPhotoRequest req, IWebHostEnvironment env, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(req.ImageBase64))
        return Results.BadRequest("ImageBase64 is required");

    var photosDir = Path.Combine(env.ContentRootPath, "generated-photos");
    Directory.CreateDirectory(photosDir);

    var ext = (req.MimeType ?? "").Contains("png") ? "png" : "jpg";
    var filename = $"upload_{Guid.NewGuid():N}.{ext}";
    await File.WriteAllBytesAsync(Path.Combine(photosDir, filename), Convert.FromBase64String(req.ImageBase64));

    var url = $"/generated-photos/{filename}";
    log.Add("Info", "Uploaded product photo saved", filename);
    return Results.Ok(new { url });
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
    catch (Exception ex)
    {
        log.Add("Warning", "Background removal failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
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
    catch (Exception ex)
    {
        var shortError = ex.Message.Length > 300 ? ex.Message[..300] + "…" : ex.Message;
        log.Add("Warning", "Create Draft: failed", shortError);
        return Results.Json(
            new { ok = false, error = shortError, details = ex.Message, where = "CreateDraft" },
            statusCode: 400);
    }
});

app.MapPost("/api/listing/publish", async (PostListingRequest req, EbayService ebay, ActionLog log, CredentialsStore store, LicenseService license) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;
    try
    {
        var result = await ebay.PublishListingAsync(req);
        var listingUrl = !string.IsNullOrEmpty(result.ListingId)
            ? $"https://www.ebay.com/itm/{result.ListingId}"
            : "";
        log.Add("Info", "eBay listing published live", $"Listing ID: {result.ListingId}; Offer ID: {result.OfferId}; SKU: {result.Sku}");
        return Results.Ok(new { result.OfferId, result.ListingId, result.Sku, listingUrl });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "eBay publish failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/local-listings/save-edit", (UpdateListingRequest req, ListingDatabase db, ActionLog log) =>
{
    var result = db.SaveEdit(req);
    log.Add("Info", "Local edit saved", string.IsNullOrWhiteSpace(req.Sku) ? req.Title : req.Sku);
    return Results.Ok(result);
});

app.MapPost("/api/listing/update", async (UpdateListingRequest req, EbayService ebay, ActionLog log) =>
{
    if (!req.ManualRevisionConfirmed)
    {
        log.Add("Warning", "eBay revise blocked", "Manual revision confirmation was missing.");
        return Results.BadRequest("Manual revision confirmation is required before revising eBay.");
    }

    await ebay.UpdateListingAsync(req);
    log.Add("Info", "eBay listing revised", string.IsNullOrWhiteSpace(req.Sku) ? req.OfferId : req.Sku);
    return Results.Ok();
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

// NOTE: the app is reached at http://localhost:9331 — no hosts-file/local-DNS
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
// flag as hosts hijacking. The app is reached at http://localhost:9331.
