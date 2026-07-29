using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;
using Microsoft.Extensions.Hosting.WindowsServices;

// ── Crash logging ────────────────────────────────────────────────────────────
// Writes crash.log into the fixed data home before the process dies so the cause
// is visible even when there is no console window to read — and so the log is in
// the same place every build, rather than beside whichever exe happened to run
// (which may sit in a read-only Program Files folder).
AppDomain.CurrentDomain.UnhandledException += (_, e) =>
{
    try
    {
        Directory.CreateDirectory(AppPaths.DataHome);
        File.AppendAllText(Path.Combine(AppPaths.DataHome, "crash.log"),
            $"{DateTime.Now:u}: {e.ExceptionObject}\n---\n");
    }
    catch { }
};

// ── Service mode detection ────────────────────────────────────────────────────
// When launched by the Windows SCM, run headless (no tray icon, no browser).
// Interactive launches (double-click, startup shortcut) get the full tray UI.
bool isWindowsService = WindowsServiceHelpers.IsWindowsService();

// ── The port ──────────────────────────────────────────────────────────────────
// Fixed, always, with no environment override: the eBay OAuth relay redirects to
// localhost:9332 and nowhere else, so an instance on any other port is one whose
// eBay sign-in cannot complete. If 9332 is taken, this app focuses the copy that
// has it or stops and says so — see AppInstance. It never moves.
var port    = AppPaths.Port;
var baseUrl = AppPaths.BaseUrl;

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

// ── Who owns port 9332? ───────────────────────────────────────────────────────
// Asked before anything binds, and answered the same way in both modes: the app
// gets the port, or it doesn't run. There is no third outcome where it comes up
// on a different URL — that URL is where eBay sends the seller back to.
async Task<PortOwner> DetectPortOwnerAsync()
{
    using var probeHttp = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    return await AppInstance.DetectAsync(
        AppInstance.IsPortListening(port),
        async path =>
        {
            try
            {
                var res = await probeHttp.GetAsync(baseUrl + path);
                return res.IsSuccessStatusCode ? await res.Content.ReadAsStringAsync() : null;
            }
            catch { return null; }
        });
}

// ── Single-instance / already-running guard ───────────────────────────────────
// Service mode: SCM guarantees a single instance — skip the mutex entirely, but
// still refuse to start if something else already holds the port (a service that
// crash-loops on a bind failure is worse than one that stops and logs why).
// Interactive mode:
//   1. Acquire mutex so only one tray instance runs at a time.
//   2. If a copy of this app is already serving on 9332, focus it — open the
//      browser at it and show a tray icon, without starting a second server.
//   3. If something that is not this app holds 9332, say so and exit cleanly.
//   4. Otherwise start the server ourselves, then show the tray icon.
System.Threading.Mutex? _mutex = null;
if (isWindowsService)
{
    if (await DetectPortOwnerAsync() != PortOwner.Free)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataHome);
            File.AppendAllText(Path.Combine(AppPaths.DataHome, "crash.log"),
                $"{DateTime.Now:u}: port {port} already in use — service not started\n---\n");
        }
        catch { }
        return;
    }
}
else
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

    var portOwner = await DetectPortOwnerAsync();

    if (portOwner == PortOwner.Foreign)
    {
        // Somebody else's server is on 9332. Moving to a free port would "work" right up until the
        // seller tries to connect eBay, so this stops instead and names the problem.
        System.Windows.Forms.MessageBox.Show(
            AppInstance.ForeignPortMessage(port),
            "ING AutoLister is not able to start",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Warning);
        _mutex.Dispose();
        return;
    }

    if (portOwner == PortOwner.ThisApp)
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
// One fixed home, whatever folder this exe was launched from. It used to be the
// exe's own directory for anything outside Program Files, which made bin\Debug,
// a copied build and an installed app three separate sets of credentials — the
// reason a different build looked like it had "lost" the API key and every
// marketplace connection. See AppPaths.
var exeDir  = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
var dataDir = AppPaths.DataHome;
Directory.CreateDirectory(dataDir);

// Pull anything the old per-build locations still hold into the fixed home. Copies only what the
// home does not already have, so it is a no-op from the second run onwards and can never overwrite
// the live data with a stale build's copy. The exe directory covers both the old portable/dev
// layout and the Program Files template that installs ship a pre-configured credentials.json in;
// the service home covers a seller moving from the installed background service to running it
// themselves.
var migrated = AppPaths.Migrate(dataDir, [exeDir, AppPaths.Resolve(isWindowsService: true)]);

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
// Facebook sold/pending sightings. Deliberately NOT registered anywhere near IMarketplaceRepository:
// what it holds are asking prices, and the comp path must only ever see real eBay sale prices.
builder.Services.AddSingleton<FacebookSoldStore>();

// Singleton because the run outlives the request that started it: a whole-account rewrite takes
// minutes, and the browser polls it rather than holding the connection open.
builder.Services.AddSingleton<CopilotSeoJob>();
// Local sourcing — Facebook Marketplace has no public search API, so this uses the same
// saved-browser-session pattern as Terapeak (one visible login to the seller's own account,
// then headless reads). User-driven only: never scheduled, never a side effect of anything
// else. See FacebookMarketplaceService.
builder.Services.AddSingleton<FacebookMarketplaceService>();
// Craigslist is the same job with none of that machinery: public search, no login, an RSS feed
// and craigslist's own postal+distance filter. See CraigslistService.
builder.Services.AddSingleton<CraigslistService>();
// Retail supply: the public deal feeds (Slickdeals, DealNews, TechBargains). Not local at all —
// buy it new on clearance and sell it used-market — but the same LocalSupplyListing, so it ranks
// in the same table. The one sourcing source that still works when the seller's own city is
// empty. See DealFeedService / DealFeedCatalog.
builder.Services.AddSingleton<DealFeedService>();
// Liquidation supply: what a business sells when it is emptying itself — store closings, overstock,
// customer returns, municipal surplus. The cheapest stock this app can see, because the seller's
// goal is an empty building rather than a good price. Priced through the Liquidation Lot Analyzer's
// own grade and max-bid arithmetic. See LiquidationSourceService / LiquidationLotPricer.
builder.Services.AddSingleton<LiquidationSourceService>();
builder.Services.AddSingleton<LiquidationLotPricer>();
// Free supply: craigslist's free-stuff board near the seller, plus free-after-coupon and
// free-after-rebate deals nationwide. The one board where the cost basis is zero, so the margin is
// the whole sale price — and the one that expires fastest, which every row says out loud. Reads
// craigslist through CraigslistService rather than a second copy of it. See FreebieSourceService.
builder.Services.AddSingleton<FreebieSourceService>();
// Every source behind one interface, so the arbitrage pipeline (grouping → comp lookup → profit →
// ranking) never learns which site a listing came from and a fifth source is a registration.
// Order matters: no-login sources first, so a seller who has connected nothing still gets
// results from a default search. See ILocalSupplySource / LocalSupplySources.
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<CraigslistService>());
// Ahead of the paid sources on purpose: a free item cannot lose money, so it is the first thing
// worth looking at and the first thing the analysis cap should be spent on.
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<FreebieSourceService>());
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<DealFeedService>());
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<LiquidationSourceService>());
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<FacebookMarketplaceService>());
// eBay itself, as a place to BUY. It was the one marketplace missing from the "where can I buy
// this" picker even though every price on that screen is measured against eBay sold data — an
// underpriced Buy It Now or a no-bid auction is an ordinary eBay-to-eBay flip. Nationwide rather
// than local, and it says so: see EbaySupplySource.IsLocationBased.
builder.Services.AddSingleton<EbaySupplySource>();
builder.Services.AddSingleton<ILocalSupplySource>(sp => sp.GetRequiredService<EbaySupplySource>());
builder.Services.AddSingleton<LocalSupplySources>();
// The buy side of the retail rows above: the public promo codes and cashback offers published for
// whichever stores the board is buying from, so the cost basis can be cut before the profit is
// computed against it. A dollar off the buy is worth more than a dollar on the sale — eBay takes
// none of it. Read off the same public lists the deal feeds come from, cached per store, and never
// folded into a row's own profit figure: a public code is a claim, not a price. See CouponService /
// CouponStacker.
builder.Services.AddSingleton<CouponService>();
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
// Answers "is each connection actually up, and if not why" with real probes — see
// ConnectionDoctor. Served by /api/diagnostics/connections.
builder.Services.AddSingleton<ConnectionDoctor>();
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
// The shipping profit engine. Until this existed, FeeProfile.DefaultShippingCost — ONE number —
// was the assumed cost of putting a label on a phone case and on a 40 lb miner alike, underneath
// every board in the app at once. PackageEstimator infers a box from a title so the sourcing
// screens can cost shipping on items nobody has weighed; ShippingRateBook prices it across
// carriers and zones; ShippingAdvisor turns that into the label to buy and the way to charge for
// it; ShippingLeakScanner runs the same maths over everything already live.
builder.Services.AddSingleton<PackageEstimator>();
builder.Services.AddSingleton<ShippingAdvisor>();
builder.Services.AddSingleton<ShippingLeakScanner>();
builder.Services.AddSingleton<OpportunityScoringService>();
builder.Services.AddSingleton<ConfidenceScoringService>();
builder.Services.AddSingleton<CrossListingFeeProfile>();
builder.Services.AddSingleton<CrossListingExporter>();
// Where to sell highest — the exporter above answers "how do I post this elsewhere"; this answers
// the question that comes first, which is whether posting it elsewhere pays more. Pure comparison
// logic over the seller's own fee profile, the published off-eBay rates, and whatever live supply
// the local sources can see; the searching is orchestrated in WhereToSellAsync below.
builder.Services.AddSingleton<WhereToSellAnalyzer>();
// What is allowed to put a resale price on a row, per category — and, just as importantly, what
// says "I can't value this" instead of inventing one. The sold-comps database this app reads is
// electronics-heavy: ask it what a 2011 Tundra sells for and it will answer with the median of four
// tow hitches, confidently. Registered as a set so a real eBay Motors feed or a book-value service
// drops in as a fourth provider and nothing else changes. See Services/ResaleValuation.cs.
foreach (var provider in ResaleValuationRegistry.BuildDefaults())
    builder.Services.AddSingleton(provider);
builder.Services.AddSingleton<ResaleValuationRegistry>();
// Local arbitrage — prices each Facebook Marketplace result against the sold-comps database and
// Terapeak, then ranks by net profit after fees. Pure ranking/verdict logic plus ProfitCalculator;
// the pricing lookups themselves are orchestrated in FindLocalArbitrageAsync below.
builder.Services.AddSingleton<LocalArbitrageAnalyzer>();
// Roll the Dice — the cross-category sweep. Mines the sold-comps database for products that carry
// a margin AND actually sell, then reuses LocalArbitrageAnalyzer above to cost buying each of them
// wherever supply exists (local classifieds, or eBay Buy It Now). Pure clustering/screening/verdict
// logic; the sweep itself is orchestrated in RollTheDiceAsync below.
builder.Services.AddSingleton<JackpotHunter>();
// The sourcing budget optimizer — the only screen that spends money rather than ranking it. Takes
// the deals the seller is already looking at (priced by the services above) plus whatever is
// tracked at Sourced, and solves an exact knapsack for the basket that makes the most out of a
// fixed amount of cash. Pure: it re-prices nothing, because a basket whose numbers disagreed with
// the board they came from would be worse than no basket.
builder.Services.AddSingleton<SourcingBudgetOptimizer>();
// Inventory health — the same pricing stack pointed at listings the seller ALREADY owns, to find
// the ones whose price the market has drifted out from under. CostBasisStore holds the one number
// eBay can never supply (what the seller paid), which is what turns a markdown suggestion into a
// break-even-checked one.
builder.Services.AddSingleton<CostBasisStore>();
builder.Services.AddSingleton<InventoryHealthAnalyzer>();
// Aging-inventory rescue — the step after a repricing suggestion. InventoryHealthAnalyzer caps a
// markdown at one revision and tells the seller to come back; the whole failure mode of dead stock
// is that nobody comes back. This turns that one step into a dated ladder decided in advance, and
// finds bundles that let a slow mover ride out with something that already sells.
builder.Services.AddSingleton<AgingInventoryRescuer>();
// Recover lost sales — the same pricing stack pointed at listings that ENDED without selling, the
// one slice of a seller's inventory no other screen in the app (or in eBay Seller Hub) puts a
// number on. Decides what to put back up and at what price, and finds the bidders who lost an
// ended auction and can still be sold to. Pure; the eBay reads are orchestrated in
// ScanRelistRecoveryAsync below.
builder.Services.AddSingleton<RelistAnalyzer>();
// Liquidation lots — the same pricing stack pointed at a whole pallet at once. LotAnalyzer owns
// only the part that is specific to buying in bulk: recovery by grade, per-unit fees across every
// unit on the manifest, the ask allocated across the lines, the max bid solved exactly, and which
// few lines actually carry the value. Orchestrated in AnalyzeLotAsync below.
builder.Services.AddSingleton<LotAnalyzer>();
// Promoted Listings ROI — the ad rate that maximises take-home rather than the one eBay suggests
// from what the rest of the category is paying. Shares ProfitCalculator/FeeProfile with every other
// money screen, so the margin an ad rate is measured against is the same margin the editor shows.
builder.Services.AddSingleton<PromotedListingAdvisor>();
// Money Made — the only screen in the app that reports what already happened. EarningsStore keeps
// the flips, EarningsCalculator does the money, and both lean on CostBasisStore for the one figure
// eBay can never supply, so a cost typed once in Inventory Health counts the profit here too.
builder.Services.AddSingleton<EarningsStore>();
builder.Services.AddSingleton<EarningsCalculator>();
// One import implementation, shared by the button and the automatic refresh — see
// EarningsImportRunner for why a second copy would be the thing that silently drifts.
builder.Services.AddSingleton<EarningsImportRunner>();
// Imports sold orders as soon as eBay is connected, then refreshes every few hours. Unlike the
// Facebook session this is a plain Sell API call with a token the app already refreshes, so there
// is no browser in the path and nothing that can be blocked. See EarningsAutoImport.
builder.Services.AddSingleton<EarningsAutoImport>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EarningsAutoImport>());
// The Deal Pipeline — the thread that joins all of the above. Every other money service answers
// one question about one moment; this one carries a single flip from the sourcing forecast that
// justified the buy, through the cash that left the bank, to the sale in EarningsStore that
// settled it. It computes no profit of its own: the projection is frozen at tracking time and the
// realized figure comes from Money Made, so the two can be compared without either being fudged.
builder.Services.AddSingleton<DealStore>();
builder.Services.AddSingleton<DealPipelineCalculator>();
// The Auction Sniper — the one sourcing service that buys on the same marketplace it sells on.
// Prices live eBay auctions and Buy It Nows against the sold comps, and answers the only question a
// bidder has: the most to bid. Shares JackpotHunter's break-even and LocalArbitrageAnalyzer's
// "worth doing" bars, so a flip won at auction is judged by the same arithmetic as one bought off
// Craigslist. The sweep itself is orchestrated in ScanSnipesAsync below.
builder.Services.AddSingleton<AuctionSniperAnalyzer>();

// ── Deal Radar: the board that reads itself ───────────────────────────────────
// Every sourcing screen above is a button. This is the same local-arbitrage scan, saved with a
// profit bar on it and run on a human cadence, so the $400 miner three miles away doesn't need
// somebody to be looking at the right tab at the right minute.
//
// The delegate below is the whole seam: FindLocalArbitrageAsync is a static local function wired to
// fourteen singletons at the HTTP edge, and the radar needs exactly it — not a second pricing path,
// which is how a notification ends up quoting a profit the board it links to doesn't show. Every
// service it closes over is a singleton, so there is no scope to create and nothing to dispose.
builder.Services.AddSingleton<DealRadarStore>();
builder.Services.AddSingleton<DesktopNotifier>();
builder.Services.AddSingleton<LocalArbitrageScan>(sp => (request, token) => FindLocalArbitrageAsync(
    request.Query, request.Zip, request.RadiusMiles, request.MaxItems, request.TerapeakBudget,
    sort: null, sp.GetRequiredService<LocalSupplySources>().Resolve(request.Sources), request.CraigslistSite,
    // The rate only touches retail rows, and a radar watch reads classifieds by default. The board's
    // own default is used rather than a per-watch field nobody would fill in.
    RetailBuyCosts.DefaultSalesTaxPercent,
    sp.GetRequiredService<IMarketplaceRepository>(), sp.GetRequiredService<ProductNormalizer>(),
    sp.GetRequiredService<ComparableMatcher>(), sp.GetRequiredService<MarketPriceEstimator>(),
    sp.GetRequiredService<SellThroughCalculator>(), sp.GetRequiredService<ProfitCalculator>(),
    sp.GetRequiredService<FeeProfile>(), sp.GetRequiredService<OpportunityScoringService>(),
    sp.GetRequiredService<ConfidenceScoringService>(), sp.GetRequiredService<TerapeakMarketService>(),
    sp.GetRequiredService<TerapeakService>(), sp.GetRequiredService<LocalArbitrageAnalyzer>(),
    sp.GetRequiredService<ActionLog>(), token,
    request.Coupons ? sp.GetRequiredService<CouponService>() : null,
    ResaleCategoryCatalog.Resolve(request.CategoryId)));
builder.Services.AddSingleton<DealRadarService>();
// Registered as the same instance twice: the hosted service that runs the loop, and the object the
// endpoints ask "is a scan running" and "run this one now".
builder.Services.AddHostedService(sp => sp.GetRequiredService<DealRadarService>());

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

// Serve generated-photos from the fixed data home
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

// Where everything lives, said once at startup. Worth a line in the log on every run: when a
// seller reports that a setting "disappeared", the first thing worth knowing is which folder the
// build they are looking at is reading.
app.Services.GetRequiredService<ActionLog>()
    .Add("Info", "Data folder", $"All saved data is in {dataDir}");
if (migrated.Count > 0)
{
    app.Services.GetRequiredService<ActionLog>()
        .Add("Info", "Saved data moved to the fixed folder",
             $"Brought forward from an earlier build's folder: {string.Join(", ", migrated)}");
}

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

            // 2. Generated-photos cleanup — keep newest 300, delete the rest.
            // ContentRootPath, not WebRootPath: the photos are served out of the data home, and
            // WebRootPath points at a wwwroot subfolder that does not exist there — so this loop
            // was quietly cleaning nothing and the folder grew without limit.
            var photosDir = Path.Combine(
                app.Services.GetRequiredService<IWebHostEnvironment>().ContentRootPath,
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
// ── Instance identity ─────────────────────────────────────────────
// How a second launch tells "ING AutoLister is already running on 9332" apart from "something else
// took 9332". A listening socket alone cannot say which, and the difference decides whether the new
// process focuses the running app or stops with an explanation — so this answers with a marker, no
// auth and no setup required, on a fresh install and a configured one alike. See AppInstance.
// Also the honest answer to "which folder is this build reading?", which is the first question when
// a saved key looks missing.
app.MapGet(AppInstance.IdentityPath, (IWebHostEnvironment env) => Results.Ok(new
{
    app      = AppInstance.IdentityMarker,
    port     = AppPaths.Port,
    url      = AppPaths.BaseUrl,
    pid      = Environment.ProcessId,
    dataHome = env.ContentRootPath,
    version  = typeof(Program).Assembly.GetName().Version?.ToString() ?? "",
}));

app.MapGet("/api/setup/status", (CredentialsStore store) => Results.Ok(store.GetStatus()));
app.MapGet("/api/setup/fields", (CredentialsStore store) => Results.Ok(store.GetPublicFields()));

// A partial save: the body carries only the fields the screen that posted it owns, and anything
// absent is left as it was (see CredentialsPatch).
app.MapPost("/api/setup/save", (CredentialsPatch body, CredentialsStore store) =>
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
app.MapPost("/api/pricing/net-quote", (NetQuoteRequest req, NetProceedsCalculator net, FeeProfile fees,
    ShippingAdvisor shipping, CredentialsStore creds) =>
{
    if (req is null) return Results.BadRequest(new { error = "No pricing request supplied." });

    // Capped so a malformed client cannot turn one request into an unbounded amount of work.
    var prices = (req.Prices ?? []).Where(p => p >= 0m).Distinct().Take(12).ToList();

    // The shipping cost these numbers are built on, in order of how much it can be trusted: what
    // the caller measured, then a real label priced off the package, then the flat profile default.
    // The default is a last resort rather than the norm now — it is the same number for every item
    // the seller owns, which is exactly why the profit figures used to drift.
    var shippingCost = req.ShippingCost;
    ShippingEstimateSummary? estimate = null;

    if (shippingCost is null && req.EstimateShipping)
    {
        var advice = shipping.Advise(new ShippingQuoteRequest
        {
            Title = req.Title,
            Category = req.Category,
            Price = prices.Count > 0 ? prices.Max() : 0m,
            WeightLbs = req.WeightLbs,
            WeightOz = req.WeightOz,
            PackageLengthIn = req.PackageLengthIn,
            PackageWidthIn = req.PackageWidthIn,
            PackageHeightIn = req.PackageHeightIn,
            OriginZip = creds.GetPublicFields().DefaultPostalCode,
        }, fees);

        // A fallback package is the estimator admitting it has no idea what the item is. Costing
        // against that would be swapping one arbitrary number for another, so the profile default
        // — which the seller at least chose — keeps the job.
        if (advice.Best is not null && advice.Package.Source != "fallback")
        {
            shippingCost = advice.Best.ExpectedCost;
            estimate = new ShippingEstimateSummary
            {
                LabelCost = advice.Best.ExpectedCost,
                ServiceName = advice.Best.Name,
                WeightLb = advice.Package.WeightLb,
                PackageSource = advice.Package.Source,
                Basis = advice.Package.Basis,
                ZoneSpread = advice.Best.ZoneSpread,
            };
        }
    }

    var quotes = prices
        .Select(p => net.Quote(p, req.UnitCost, fees, req.BuyerPaidShipping, req.Quantity,
                               shippingCost, req.OtherCosts))
        .ToList();

    // The floors do not depend on the asking price, so they are computed once from a reference
    // quote rather than read off whichever price happened to be first in the list.
    var reference = net.Quote(0m, req.UnitCost, fees, req.BuyerPaidShipping, req.Quantity,
                              shippingCost, req.OtherCosts);
    var hasCost = req.UnitCost is > 0m;

    return Results.Ok(new NetQuoteResponse
    {
        Quotes = quotes,
        BreakEvenPrice = hasCost ? reference.BreakEvenPrice : null,
        MinimumOfferPrice = hasCost ? reference.MinimumOfferPrice : null,
        MinimumOfferBasis = reference.MinimumOfferBasis,
        HasCostBasis = hasCost,
        Fees = FeeProfileStore.ToView(fees),
        Shipping = estimate,
    });
});

// ── Shipping Profit Engine ───────────────────────────────────────────────────────────────────────
// One item: the box, every service that will carry it, and the four ways to charge for it.
app.MapPost("/api/shipping/quote", (ShippingQuoteRequest req, ShippingAdvisor advisor, FeeProfile fees,
    CredentialsStore creds, ActionLog log) =>
{
    if (req is null) return Results.BadRequest(new { error = "No shipping request supplied." });

    // The seller's own listing ZIP is the right default — zones only exist relative to an origin,
    // and asking for it again on a screen that could already know it is how a field gets left blank.
    if (string.IsNullOrWhiteSpace(req.OriginZip))
        req.OriginZip = creds.GetPublicFields().DefaultPostalCode;

    try
    {
        return Results.Ok(advisor.Advise(req, fees));
    }
    catch (Exception ex)
    {
        log.Add("Error", "Shipping quote failed", ex.Message);
        return Results.Ok(new ShippingRecommendation
        {
            Status = "error",
            Headline = "Could not price this package.",
            Note = ex.Message,
        });
    }
});

// The rate card itself, so the numbers on every other screen are checkable rather than magic.
app.MapGet("/api/shipping/services", () => Results.Ok(new
{
    services = ShippingRateBook.Describe(),
    note = "Calibrated estimates of eBay/commercial label pricing, not live carrier rates. "
         + "Expect them to be within about a dollar; weigh and measure to be exact.",
}));

// The bulk view: what shipping is quietly costing across every listing already live. Read-only —
// no eBay writes, and no comp lookups, so unlike the pricing scans this one has no budget to ration.
app.MapGet("/api/shipping/leaks", async (int? maxItems, EbayService ebay, ShippingLeakScanner scanner,
    FeeProfile fees, CredentialsStore creds, ActionLog log) =>
{
    List<EbayListingSummary> listings;
    try
    {
        listings = await ebay.GetListingsAsync();
    }
    catch (Exception ex)
    {
        // Same contract as every other inventory-wide board: reported, never silently retried.
        // Reconnecting eBay is the seller's decision, made in Settings.
        log.Add("Warning", "Shipping leak scan could not read eBay listings", ex.Message);
        return Results.Ok(new ShippingScanResult { Status = "ebay_unavailable", Error = ex.Message });
    }

    var active = listings
        .Where(l => l.Status is "ACTIVE" or "PUBLISHED" || string.IsNullOrWhiteSpace(l.Status))
        .Where(l => l.Price > 0m && !string.IsNullOrWhiteSpace(l.Title))
        .ToList();

    var result = scanner.Scan(active, creds.GetPublicFields().DefaultPostalCode, fees,
                              Math.Clamp(maxItems ?? 500, 1, 1000));

    log.Add("Info", "Shipping leak scan finished",
        $"{result.Summary.ListingsScanned} listings checked, {result.Summary.LeaksFound} leaks, "
      + $"${result.Summary.TotalPerSaleImpact:0.00} per sale on the table ({result.ElapsedMs} ms).");

    return Results.Ok(result);
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

// ── Connection diagnostics ────────────────────────────────────────────────────
// The four /status endpoints below and beside this one each answer "is a file/token present",
// which is what made a dead session, a revoked grant and an unreachable host all look identical.
// This one goes and asks: a live eBay token refresh plus an authenticated API call, both saved
// browser sessions replayed headlessly, and a real query against the comps API. That costs two
// Chrome launches and several seconds, so it is a "why is this broken" button, not a poll.
app.MapGet("/api/diagnostics/connections", async (ConnectionDoctor doctor, CancellationToken ct) =>
    Results.Ok(new { connections = await doctor.CheckAllAsync(ct) }));

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

// Marketplace's own front page for this account — "Today's picks". Not a search: it is whatever
// Facebook decided to show the seller near them, which is where local supply they'd never have
// thought to type turns up. One page load, and only when asked for.
app.MapGet("/api/facebook/picks", async (FacebookMarketplaceService facebook, CancellationToken ct) =>
    Results.Ok(await facebook.BrowsePicksAsync(ct)));

// Today's Picks, priced.
//
// The unpriced feed answers "what is near me" and stops there — a photo, an ask and a town. The
// question a reseller actually has is "is $600 a buy", and answering it meant leaving the app. This
// runs the same feed down the same pipeline the scan boards use, so every card can carry what it
// sells for, how many sold, the profit after fees and the ROI.
app.MapPost("/api/local/price-these", async (
    LocalSupplySearchResult picks, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, CouponService couponService, ActionLog log,
    int? maxItems, int? terapeakBudget, CancellationToken ct) =>
{
    try
    {
        // The listings come from the caller because the browser already has them: Today's Picks is a
        // ~30 second real page load against Marketplace, and fetching it a second time purely to
        // attach prices would double that wait and double the traffic against the seller's own
        // logged-in account for no new information.
        if (picks?.Items is null || picks.Items.Count == 0)
            return Results.Ok(new LocalArbitrageResult { Status = picks?.Status ?? "ok", Query = "", Error = picks?.Error });

        var result = await FindLocalArbitrageAsync(
            "", "", 40,
            Math.Clamp(maxItems ?? 25, 1, 60), Math.Clamp(terapeakBudget ?? 3, 0, 10), sort: null,
            [new PrefetchedSupplySource(picks)], craigslistSite: null,
            retailSalesTaxPercent: 0m,
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc,
            profitCalc, feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct,
            couponService, category: null);

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Pricing supplied listings failed", ex.Message);
        return Results.BadRequest(new { error = ex.Message });
    }
});

// Marketplace with its own filters applied. Facebook cannot be embedded — every Marketplace URL
// is served X-Frame-Options: DENY, so no browser will render their page inside this one — but all
// of their filters are query-string parameters, so the same controls reach the same results here.
app.MapGet("/api/facebook/browse", async (
    string? q, string? category, decimal? minPrice, decimal? maxPrice, string? condition,
    string? daysListed, string? delivery, string? sortBy, int? radius,
    FacebookMarketplaceService facebook, CancellationToken ct) =>
{
    // Whitelisted against Facebook's own option lists: these go into a URL Facebook parses, and an
    // invented value returns an empty board that looks exactly like "nothing for sale near you".
    static string Pick((string Value, string Label)[] options, string? wanted) =>
        options.Any(o => o.Value == wanted) ? wanted! : "";

    var filters = new FacebookMarketplaceSelectors.BrowseFilters(
        Query:        (q ?? "").Trim(),
        CategorySlug: Pick(FacebookMarketplaceSelectors.CategoryOptions.Select(c => (c.Slug, c.Label)).ToArray(), category),
        MinPrice:     minPrice is > 0 ? minPrice : null,
        MaxPrice:     maxPrice is > 0 ? maxPrice : null,
        Condition:    Pick(FacebookMarketplaceSelectors.ConditionOptions, condition),
        DaysListed:   Pick(FacebookMarketplaceSelectors.DateListedOptions, daysListed),
        Delivery:     Pick(FacebookMarketplaceSelectors.DeliveryOptions, delivery),
        SortBy:       Pick(FacebookMarketplaceSelectors.SortOptions, sortBy),
        RadiusMiles:  Math.Clamp(radius ?? 40, 1, 500));

    return Results.Ok(await facebook.BrowseAsync(filters, ct));
});

// The filter lists themselves, so the browser renders Facebook's own options rather than a second
// copy of them that drifts the day Marketplace adds one.
app.MapGet("/api/facebook/browse-options", () => Results.Ok(new
{
    categories = FacebookMarketplaceSelectors.CategoryOptions.Select(c => new { value = c.Slug, label = c.Label }),
    conditions = FacebookMarketplaceSelectors.ConditionOptions.Select(c => new { value = c.Value, label = c.Label }),
    dates      = FacebookMarketplaceSelectors.DateListedOptions.Select(c => new { value = c.Value, label = c.Label }),
    delivery   = FacebookMarketplaceSelectors.DeliveryOptions.Select(c => new { value = c.Value, label = c.Label }),
    sorts      = FacebookMarketplaceSelectors.SortOptions.Select(c => new { value = c.Value, label = c.Label }),
    radii      = FacebookMarketplaceSelectors.SupportedRadiiMiles,
}));

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

// What kinds of thing the scanner can be pointed at, for the category picker.
//
// A category is not a filter on the results — it changes what is searched (a craigslist board
// rather than the whole for-sale section), what is allowed to value each row, and what selling it
// costs. Which is why the picker states, per category, whether this app can price it at all: a
// seller who picks Boats should learn that here rather than from a column of dashes.
app.MapGet("/api/local/categories", () => Results.Ok(ResaleCategoryCatalog.Describe()));

// One local search across every selected site, merged into a single list. `sources` is a
// comma-separated list of ids (craigslist,facebook); omitted means everything available now.
//
// This always answers 200 with a valid body — including when every source failed. The frontend
// renders per-source status and whatever results did arrive off that body; a 500 with an HTML
// error page would reach it as a rejected fetch instead, with no results and nothing to say.
app.MapGet("/api/local/search", async (
    string q, string? zip, int? radius, string? sources, string? craigslistSite, string? category,
    LocalSupplySources registry, ActionLog log, CancellationToken ct) =>
{
    var radiusMiles = radius ?? 40;
    var results = new List<LocalSupplySearchResult>();
    var wanted = ResaleCategoryCatalog.Resolve(category);

    try
    {
        var picked = registry.Resolve(sources);

        // Sequential, not parallel: one of these sites is searched by driving a real browser, and
        // running that alongside anything else is how a slow search becomes a stuck one.
        foreach (var source in picked)
            results.Add(await SearchLocalSourceAsync(source, q ?? "", zip ?? "", radiusMiles, craigslistSite, wanted, ct));

        var merged = LocalSupplyMerger.Merge(results, q ?? "", zip ?? "", radiusMiles);
        // What each row actually IS, worked out once for the whole search. The plain list only uses
        // it to show the vehicle it read off a title — but classifying here rather than in the
        // arbitrage pipeline means both boards agree about what a row is.
        ResaleCategoryCatalog.ClassifyAll(merged.Items, wanted);
        return Results.Ok(merged);
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
//
// The category is the second knob, and craigslist is the only source that can do anything with it:
// it files cars, boats, RVs, trailers, appliances and furniture on separate boards, so a category
// there is a different URL rather than a different word in the search box. Every other source gets
// the category anyway (the interface default ignores it) and its results are classified per listing
// afterwards — see ResaleCategoryCatalog.ClassifyAll.
static Task<LocalSupplySearchResult> SearchLocalSourceAsync(
    ILocalSupplySource source, string q, string zip, int radius, string? craigslistSite,
    ResaleCategory category, CancellationToken ct) =>
    LocalSupplyGuard.RunAsync(
        source,
        token => source is CraigslistService craigslist
            ? craigslist.SearchCategoryAsync(
                category.CraigslistBoard, q, zip, radius, craigslistSite, token,
                // "Anything" is the absence of a classification, not one — stamping it would stop
                // the per-listing classifier ever running on craigslist rows.
                categoryId: category.IsDefault ? "" : category.Id,
                // A named board IS the search: "everything on the cars board within 40 miles" is
                // exactly what picking a category means, where a blank for-sale search is the whole
                // classifieds section.
                allowBlankQuery: !category.IsDefault)
            : source.SearchAsync(q, zip, radius, category, token),
        q, zip, radius, ct: ct);

// The local-arbitrage ranking: the same zip/radius/keyword search as above, across every selected
// site, but every result is priced against real eBay sold data and ranked by what's left after
// fees. Deliberately a separate endpoint from the plain searches rather than a flag on them —
// this one costs a comp lookup per distinct product and can spend Terapeak scrapes, so it only
// ever runs when someone clicks the button that says so.
// ── eBay scanner ──────────────────────────────────────────────────────────────
// eBay as a place to BUY, with the filters that only mean something on eBay: condition, auction
// vs Buy It Now, a price band, a seller-quality floor. It runs the SAME pipeline as the local
// board — same grouping, same sold-comp lookup, same ProfitCalculator, same ranking — and returns
// the same shape, so the browser renders it with the existing table and nothing was forked.
//
// It exists as its own route rather than as more parameters on /api/local/arbitrage because these
// filters are meaningless to every other source: a condition filter on a Craigslist RSS feed or a
// buying-option filter on a freebie board is a control that silently does nothing.
app.MapGet("/api/ebay/scan", async (
    string q, string? condition, string? listingType, decimal? minPrice, decimal? maxPrice,
    int? minFeedback, string? sort, int? maxItems, int? terapeakBudget, string? category,
    EbaySupplySource ebaySource, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, CouponService couponService, ClaudeService claude,
    ActionLog log, CancellationToken ct) =>
{
    // Whitelisted rather than passed through: these reach eBay's own filter syntax, and an
    // unrecognised value there is an error on the whole search rather than an ignored parameter.
    var wantedCondition = condition?.ToUpperInvariant() switch
    {
        "NEW" or "USED" or "REFURBISHED" or "FOR_PARTS" => condition!.ToUpperInvariant(),
        _ => null,
    };
    var wantedType = listingType?.ToUpperInvariant() switch
    {
        "AUCTION" => "AUCTION",
        "FIXED_PRICE" => "FIXED_PRICE",
        _ => "BOTH",
    };

    var filters = new EbayScanFilters(
        wantedCondition, wantedType,
        minPrice is > 0 ? minPrice : null,
        maxPrice is > 0 ? maxPrice : null,
        Math.Clamp(minFeedback ?? 0, 0, 100000));

    try
    {
        Task<LocalArbitrageResult> Scan(string keyword) => FindLocalArbitrageAsync(
            keyword, "", 40,
            Math.Clamp(maxItems ?? 30, 1, 60), Math.Clamp(terapeakBudget ?? 5, 0, 10), sort,
            [ebaySource.WithFilters(filters)], craigslistSite: null,
            // eBay charges sales tax, but it is already inside the delivered price the source
            // reports — adding a rate on top would double-count it.
            retailSalesTaxPercent: 0m,
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc,
            profitCalc, feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct,
            couponService,
            ResaleCategoryCatalog.Resolve(category));

        var result = await Scan(q ?? "");

        // Only on an empty scan, and only ever once. eBay answers a misspelling with a bare
        // "total: 0" — no correction, no suggestion — so a typo is indistinguishable from a dead
        // market. Correcting a search that already returned rows would be the other failure:
        // quietly answering a question the seller did not ask.
        if (result.Items.Count == 0 && !string.IsNullOrWhiteSpace(q))
        {
            var corrected = await claude.SuggestSearchSpellingAsync(q, ct);
            if (!string.IsNullOrWhiteSpace(corrected))
            {
                var retry = await Scan(corrected);

                // Swap only when the corrected search actually found something to buy. Otherwise
                // keep the seller's own search and carry the suggestion alongside it, so the screen
                // can ask "did you mean" instead of silently answering a question nobody typed.
                //
                // These have to be separate cases because a scan returns zero rows for two very
                // different reasons: eBay had nothing, or eBay had plenty and none of it could be
                // priced against sold comps. Measured: "miners cryptocurrency" is 1,202 listings on
                // eBay and still zero rows here. Gating the correction on priced rows meant a
                // correct suggestion was thrown away.
                if (retry.Items.Count > 0)
                {
                    retry.CorrectedFrom = q;
                    retry.CorrectedTo = corrected;
                    log.Add("Info", "Search spelling corrected", $"\"{q}\" -> \"{corrected}\" ({retry.Items.Count} results)");
                    return Results.Ok(retry);
                }

                result.CorrectedTo = corrected;      // a suggestion, not a substitution
                log.Add("Info", "Search spelling suggestion", $"\"{q}\" -> \"{corrected}\" (neither found priced rows)");
            }
        }

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.Add("Error", "eBay scan failed", ex.Message);
        return Results.Ok(FailedArbitrage(q ?? "", "", 40,
            $"The eBay scan couldn't be completed: {ex.Message}"));
    }
});

app.MapGet("/api/local/arbitrage", async (
    string q, string? zip, int? radius, int? maxItems, int? terapeakBudget, string? sort,
    string? sources, string? craigslistSite, decimal? salesTax, bool? coupons, string? category,
    LocalSupplySources registry, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, CouponService couponService, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await FindLocalArbitrageAsync(
            q ?? "", zip ?? "", radius ?? 40,
            // Bounded on both axes: the comp lookups are per-product and the scrapes are per-product
            // too, so an unbounded request would turn one click into hundreds of lookups.
            Math.Clamp(maxItems ?? 30, 1, 60), Math.Clamp(terapeakBudget ?? 5, 0, 10), sort,
            registry.Resolve(sources), craigslistSite,
            // The seller's own sales-tax rate, which applies to the retail rows and to nothing
            // else. Clamped rather than trusted — see RetailBuyCosts.
            RetailBuyCosts.Sanitize(salesTax),
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc,
            profitCalc, feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct,
            // On by default: the codes are free to look up, cached per store, and every dollar one
            // takes off the buy is a dollar eBay never sees. Off is offered because a seller who
            // wants the scan back a few seconds sooner is entitled to it.
            coupons == false ? null : couponService,
            // What kind of thing to look for. Changes the craigslist board that is searched, what is
            // allowed to value each row, and what selling it costs — see ResaleCategoryCatalog.
            ResaleCategoryCatalog.Resolve(category));

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

// ── Coupons, promo codes and cashback ─────────────────────────────────────────
// The buy side, on its own: every public code this app can find for one store, and — when a price
// is given — what the best legal stack of them leaves that item costing. The scan above does this
// automatically for the stores on the board; this is the same lookup for the item a seller is
// looking at right now, in a tab that isn't this app.
//
// Always answers 200 with a body the UI can render, including when every list refused: the manual
// links (RetailMeNot and the cashback portals, which block automated reads) are part of the answer
// and are worth returning even when nothing machine-readable could be.
app.MapGet("/api/coupons", async (
    string? store, decimal? price, decimal? salesTax, CouponService coupons, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await coupons.LookupAsync(store, ct);

        // Only priced when the caller said what the thing costs. A stack against an unstated price
        // would have to invent a subtotal, and every figure in it would be about that invention.
        if (price is > 0)
        {
            result.Stack = CouponStacker.Best(
                result.Offers, price.Value, RetailBuyCosts.Sanitize(salesTax));
        }

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        log.Add("Error", "Coupon lookup failed", ex.Message);
        return Results.Ok(new CouponLookupResult
        {
            Status = "error", Query = store ?? "", CheckedUtc = DateTime.UtcNow,
            Error = $"The coupon lookup couldn't be completed: {ex.Message}",
            // Still the useful half of the answer: the lists a person can open in a browser.
            ManualSites = CouponCatalog.Resolve(store) is { } merchant
                ? CouponCatalog.ManualSitesFor(merchant) : [],
        });
    }
});

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
            registry.Resolve(FacebookMarketplaceParser.SourceId), craigslistSite: null,
            // Facebook supply is never retail, so the rate is moot here — passed for the signature.
            RetailBuyCosts.DefaultSalesTaxPercent,
            marketplace, normalizer, matcher,
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

// ── Buy-side negotiation ──────────────────────────────────────────────────────────────────────
// Every row on the local board already carries its own negotiation plan. This is the same advice
// for a deal the seller found somewhere this app doesn't scan — paste the ask and what it sells
// for, and get the opening offer, the ceiling and the drafted message back.
//
// Pure arithmetic and string building: no network, no scraping, nothing sent anywhere. The message
// is drafted for the seller to read, edit and send themselves.
app.MapPost("/api/local/negotiate", (
    NegotiationRequest req, JackpotHunter hunter, FeeProfile feeProfile, ActionLog log) =>
{
    // Prefer a break-even the caller already costed; derive it from the resale price otherwise, via
    // exactly the profit calculator every other screen uses, so both routes end at one number.
    var breakEven = req.BreakEvenBuyPrice ?? (req.ResalePrice is > 0m
        ? hunter.BreakEvenBuyPrice(new ResalePricing
        {
            ExpectedSale = req.ResalePrice, Median = req.ResalePrice, QuickSale = req.ResalePrice,
            SoldCompCount = req.SoldCompCount,
        }, feeProfile)
        : 0m);

    var plan = NegotiationAdvisor.Build(
        askPrice: req.AskPrice, breakEvenBuyPrice: breakEven, resalePrice: req.ResalePrice,
        // A hand-entered deal has no comp count to check, so a stated resale price is taken at its
        // word — the seller typed it, and the draft says "similar ones sell for" either way.
        compCount: req.SoldCompCount > 0 ? req.SoldCompCount : (req.ResalePrice is > 0m ? NegotiationAdvisor.MinCompsToCite : 0),
        daysListed: req.DaysListed, daysToCash: req.DaysToCash,
        originalPrice: req.OriginalPrice, distanceMiles: req.DistanceMiles);

    log.Add("Negotiation", "Drafted a buy-side offer",
        $"\"{req.Title}\": asking {req.AskPrice:C}, open at {plan.OpeningOffer:C} ({plan.Verdict})");

    return Results.Ok(plan);
});

// ── Snap & Source ─────────────────────────────────────────────────────────────────────────────
// One item, one answer, from a pasted link or a photo. Every board in this app answers "what should
// I buy?"; this answers "should I buy THIS?", which is the only version of the question that gets
// asked standing up with somebody waiting.
//
// The pricing is not new and deliberately so: it is AnalyzeProductAsync → ResalePricing →
// LocalArbitrageAnalyzer.Build, the identical path Local Deals, Deal Radar and Roll the Dice take.
// A snap that disagreed with the board about the same item at the same price would mean the app has
// two opinions and the seller has none. What is new is the two ways in and the one way out.

app.MapPost("/api/snap", async (
    SnapRequest req, ClaudeService claude, IHttpClientFactory httpFactory,
    LocalArbitrageAnalyzer arbitrage, FeeProfile feeProfile,
    ProductNormalizer normalizer, IMarketplaceRepository marketplace, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    CredentialsStore store, LicenseService license, ActionLog log, CancellationToken ct) =>
{
    if (TrialGuard(store, license) is { } blocked) return blocked;

    var sw = System.Diagnostics.Stopwatch.StartNew();

    var typedTitle = (req.Title ?? "").Trim();
    var url = (req.Url ?? "").Trim();
    var photo = (req.ImageBase64 ?? "").Trim();

    // A URL pasted into the "what is it" box is still a URL. Sellers paste links into whichever box
    // is in front of them, and refusing on which field it arrived in would be the app being pedantic
    // about its own form layout.
    if (url.Length == 0 && SnapPageParser.LooksLikeUrl(typedTitle))
    {
        url = typedTitle;
        typedTitle = "";
    }

    if (url.Length == 0 && photo.Length == 0 && typedTitle.Length == 0)
    {
        return BadInputJson("Nothing to price",
            "No link, no photo and no item name arrived with the request.",
            "Paste a listing link, drop a photo of the item, or type what it is.");
    }

    string title = typedTitle;
    string imageUrl = "";
    string sourceLabel = typedTitle.Length > 0 ? "Typed" : "";
    string inputMode = typedTitle.Length > 0 ? "text" : "";
    decimal? pageAsk = null;
    SnapIdentity? identity = null;
    var warnings = new List<string>();

    // ── The link ─────────────────────────────────────────────────────────────
    // Read for its metadata, not screenshotted. The headless-browser route behind /api/analyze-url
    // takes tens of seconds, which is fine at a desk and useless in a driveway — see SnapPageParser.
    if (url.Length > 0)
    {
        inputMode = "url";
        sourceLabel = SnapPageParser.SiteLabel(url);

        var (html, fetchError) = await SnapFetchPageAsync(httpFactory, url, ct);
        if (fetchError is not null)
        {
            return BadInputJson("That page wouldn't open", fetchError,
                "Take a photo of the item instead, or type what it is — either one prices it the same way.");
        }

        var facts = SnapPageParser.Parse(html, url);
        if (facts.SiteLabel.Length > 0) sourceLabel = facts.SiteLabel;
        imageUrl = facts.ImageUrl;
        if (title.Length == 0) title = facts.Title;
        pageAsk = facts.Price;

        if (title.Length == 0)
        {
            return BadInputJson("That page didn't say what it is",
                "The listing loaded but published no title the app could read — some sites render " +
                "everything in script, and a few deliberately hide it from link previews.",
                "Take a photo of the item instead, or type what it is.");
        }

        if (facts.Price is null && !facts.IsFree)
        {
            warnings.Add("That page didn't publish a price the app could read — type what they want " +
                         "for it and snap again, or use the pay-up-to figure below.");
        }
    }
    // ── The photo ────────────────────────────────────────────────────────────
    // Only when the seller has not already told us what it is. Re-pricing a corrected name must not
    // pay for a second look at the same picture.
    else if (photo.Length > 0 && title.Length == 0)
    {
        inputMode = "photo";
        sourceLabel = "Photo";

        try
        {
            identity = await claude.IdentifyItemAsync(photo, req.MimeType ?? "image/jpeg", ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var failure = FailureTranslator.Translate(ex, FailureDomain.Ai);
            log.Add("Warning", "Snap identification failed", $"{failure.Kind} — {failure.Technical}");
            return FailureJson(failure);
        }

        title = identity.Title;
        if (title.Length == 0)
        {
            return BadInputJson("Couldn't tell what that is",
                "The photo came through, but nothing in it identified a product well enough to price.",
                "Get the label, the model plate or the whole item in frame and try again — or type what it is.");
        }
    }
    else if (photo.Length > 0)
    {
        // A photo carried alongside a corrected name: kept as the picture on the card, never re-read.
        inputMode = "photo";
        sourceLabel = "Photo";
    }

    // ── The price ────────────────────────────────────────────────────────────
    // What the seller typed wins over what the page said: a listing at $80 that the seller has
    // already talked down to $55 is a different deal, and they are the one standing there.
    var ask = req.AskPrice ?? pageAsk;
    var askWasKnown = ask is > 0m;

    var listing = new LocalSupplyListing
    {
        Source = "snap",
        SourceLabel = sourceLabel.Length > 0 ? sourceLabel : "Snap",
        ItemId = "snap",
        Title = title,
        Url = url,
        ImageUrl = imageUrl,
        // Zero, never null, when nobody has named a price. The row is then costed against a cost of
        // nothing, which is exactly what makes MaxBuyPrice come back as the break-even sticker —
        // and SnapJudge drops the profit and ROI that figure implies, because they are arithmetic
        // about a price the seller has not been offered. See SnapJudge.Build.
        Price = ask ?? 0m,
        PriceText = askWasKnown ? $"{ask:C}" : "",
        Location = sourceLabel,
    };

    // What KIND of thing this is, decided before anything prices it — the same classification the
    // local scan runs, so a snapped truck is refused by the same rule that refuses one on the board
    // rather than priced off tow-hitch comps.
    var category = ResaleCategoryCatalog.Classify(
        listing, req.CategoryId is { Length: > 0 } ? ResaleCategoryCatalog.Resolve(req.CategoryId) : null);
    listing.CategoryId = category.Id;
    listing.CategoryLabel = category.Label;

    ResalePricing? resale;
    try
    {
        var analysis = await AnalyzeProductAsync(
            title, supplierUnitCost: askWasKnown ? ask : null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: null, ebayForCompetitionFallback: null,
            // Never. A real Terapeak scrape is a browser page load against a logged-in session, and
            // this screen exists to answer before the person selling the thing gets bored. The
            // hosted comps database answers in milliseconds and is what the verdict rests on.
            allowRealTerapeakScrape: false,
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);

        resale = ResalePricing.From(analysis, title);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Research);
        log.Add("Warning", "Snap pricing failed", $"\"{title}\": {failure.Kind} — {failure.Technical}");
        return FailureJson(failure);
    }

    var row = arbitrage.Build(listing, resale, feeProfile);
    var result = SnapJudge.Build(row, askWasKnown);

    result.InputMode = inputMode;
    result.SourceLabel = listing.SourceLabel;
    result.Item = title;
    if (result.ImageUrl.Length == 0) result.ImageUrl = imageUrl;
    result.SoldSearchUrl = ResaleValuationLinks.SoldSearchUrl(category, row.PricedAs.Length > 0 ? row.PricedAs : title);
    result.Identity = identity;
    // The page's caveats first, then the photo's, then the comps' — the order the seller meets them
    // in. SnapJudge has already added its own, so these go in front of them.
    result.Warnings.InsertRange(0, warnings);
    if (identity is not null) SnapJudge.AddIdentityWarnings(result, identity);
    result.ElapsedMs = sw.ElapsedMilliseconds;

    log.Add("Research", "Snap & Source verdict",
        $"\"{title}\" ({inputMode}); ask {(askWasKnown ? $"{ask:C}" : "unknown")}; " +
        $"resale {(result.ResalePrice is { } r ? $"{r:C}" : "none")} on {result.CompCount} comp(s); " +
        $"{result.Call} — {result.CallLabel}; {sw.ElapsedMilliseconds}ms");

    return Results.Ok(result);
});

// One GET for one page the seller pasted. Shaped like a browser asking for a document, because the
// CDNs in front of these sites refuse a bare client first — the same headers every feed read in this
// app uses. Short deadline on purpose: this screen's whole promise is an answer before the person
// selling the thing gets bored, and a page that needs longer than this is a page to photograph
// instead.
static async Task<(string? Html, string? Error)> SnapFetchPageAsync(
    IHttpClientFactory httpFactory, string url, CancellationToken ct)
{
    const int maxBytes = 3_000_000;

    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
        || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
    {
        return (null, "That doesn't look like a web address.");
    }

    try
    {
        var http = httpFactory.CreateClient();
        PublicFeedHttp.ApplyBrowserHeaders(http);
        http.Timeout = TimeSpan.FromSeconds(12);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(12));

        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode)
        {
            return (null, (int)response.StatusCode switch
            {
                403 or 429 => "The site turned the request away — several of these listing sites " +
                              "refuse anything that isn't a signed-in browser.",
                404 or 410 => "That listing is gone — it 404s, which usually means it sold or was deleted.",
                >= 500 => "The site is having trouble right now.",
                _ => $"The site answered HTTP {(int)response.StatusCode}.",
            });
        }

        if (response.Content.Headers.ContentLength > maxBytes)
            return (null, "That page came back far larger than a listing page should be.");

        var html = await response.Content.ReadAsStringAsync(deadline.Token);
        return html.Length == 0 ? (null, "That page came back empty.") : (html, null);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
    catch (OperationCanceledException) { return (null, "That page took too long to load."); }
    catch (Exception ex) { return (null, $"That page couldn't be loaded: {ex.Message}"); }
}

// ── Deal Radar ────────────────────────────────────────────────────────────────────────────────
// Saved searches that run themselves. Everything below is bookkeeping over DealRadarStore plus one
// call into DealRadarService — the scanning, the cadence and the bar all live there, because the
// timer and the "Scan now" button have to take exactly the same path or the two will drift.
//
// Nothing here scans on a GET. The status endpoint is polled by the open tab every half-minute.

app.MapGet("/api/radar/status", (
    DealRadarStore store, DealRadarService radar, DesktopNotifier notifier) =>
    Results.Ok(BuildRadarStatus(store, radar, notifier)));

// The feed. Dismissed alerts are excluded unless asked for — dismissing is "I've dealt with this",
// not "delete the record", and the seen-memory keeps it from being re-found either way.
app.MapGet("/api/radar/alerts", (int? limit, bool? includeDismissed, DealRadarStore store) =>
    Results.Ok(store.ListAlerts(limit ?? 60, includeDismissed == true)));

// Create or edit one watch. A partial body edits only what it names (see DealWatchRequest), so the
// pause toggle can post two fields without blanking the seller's thresholds.
app.MapPost("/api/radar/watches", (
    DealWatchRequest req, DealRadarStore store, DealRadarService radar, DesktopNotifier notifier, ActionLog log) =>
{
    try
    {
        var watch = store.SaveWatch(req);
        log.Add("Deal Radar", req.Id is > 0 ? "Watch updated" : "Watch saved",
            $"\"{watch.Name}\" — {DealRadarService.EffectiveSources(watch)}, every {watch.IntervalMinutes} min, " +
            $"at least {watch.MinNetProfit:C0} and {watch.MinRoiPercent:0}%");
        return Results.Ok(new { ok = true, watch, status = BuildRadarStatus(store, radar, notifier) });
    }
    catch (InvalidOperationException ex)
    {
        // The validation messages are sentences a seller can act on, so they're returned as-is
        // rather than as a generic 400 body the UI would have to translate.
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

app.MapDelete("/api/radar/watches/{id:long}", (
    long id, DealRadarStore store, DealRadarService radar, DesktopNotifier notifier, ActionLog log) =>
{
    var deleted = store.DeleteWatch(id);
    if (deleted) log.Add("Deal Radar", "Watch deleted", $"Watch #{id} and its alerts were removed.");
    return Results.Ok(new { ok = deleted, status = BuildRadarStatus(store, radar, notifier) });
});

// Scan one watch right now. Takes the same one-at-a-time gate the timer does, so pressing this
// during a background sweep is answered immediately with what's happening rather than queued.
app.MapPost("/api/radar/watches/{id:long}/run", async (
    long id, DealRadarStore store, DealRadarService radar, DesktopNotifier notifier, CancellationToken ct) =>
{
    var run = await radar.RunWatchAsync(id, manual: true, ct);
    return Results.Ok(new
    {
        ok = run.Status != RadarRunStatuses.Error,
        // `runStatus`, not `status`: the camelCase policy would collide it with the radar status
        // below, and System.Text.Json answers a duplicate property name with a 500 — which reads,
        // after a two-minute scan, as "the scan failed" rather than "the reply couldn't be written".
        runStatus = run.Status,
        run.Note, run.Alerts, run.Scanned,
        status = BuildRadarStatus(store, radar, notifier),
    });
});

app.MapPost("/api/radar/settings", (
    DealRadarSettings req, DealRadarStore store, DealRadarService radar, DesktopNotifier notifier, ActionLog log) =>
{
    var saved = store.SaveSettings(req);
    log.Add("Deal Radar", saved.Enabled ? "Radar switched on" : "Radar switched off",
        saved.Enabled
            ? $"Watches will run on their own schedule. Quiet hours: {(DealRadarClock.DescribeQuietHours(saved) is { Length: > 0 } q ? q : "off")}."
            : "Nothing will scan in the background. Scan now still works.");
    return Results.Ok(new { ok = true, settings = saved, status = BuildRadarStatus(store, radar, notifier) });
});

app.MapPost("/api/radar/alerts/{id:long}/read", (long id, bool? value, DealRadarStore store) =>
    Results.Ok(new { ok = store.SetAlertFlag(id, "read", value ?? true) }));

app.MapPost("/api/radar/alerts/{id:long}/dismiss", (long id, DealRadarStore store) =>
    // Dismissing marks it read too: an alert taken off the feed unread would keep the badge lit for
    // something the seller has already dealt with.
    Results.Ok(new { ok = store.SetAlertFlag(id, "dismissed", true) && store.SetAlertFlag(id, "read", true) }));

app.MapPost("/api/radar/alerts/read-all", (DealRadarStore store) =>
    Results.Ok(new { ok = true, marked = store.MarkAllRead() }));

app.MapPost("/api/radar/alerts/clear", (DealRadarStore store, ActionLog log) =>
{
    var cleared = store.ClearAlerts();
    log.Add("Deal Radar", "Feed cleared", $"{cleared} alert(s) removed. Nothing already found will be re-alerted.");
    return Results.Ok(new { ok = true, cleared });
});

// One read for the whole screen: the settings, every watch with where the scanner got to, the
// counts behind the sidebar badge, and — stated rather than assumed — whether a notification can
// physically reach this desktop from this process. See DesktopNotifier.
static DealRadarStatus BuildRadarStatus(DealRadarStore store, DealRadarService radar, DesktopNotifier notifier)
{
    var settings = store.GetSettings();
    var watches = store.ListWatches();
    var counts = store.AlertCounts();
    var now = DateTimeOffset.UtcNow;

    return new DealRadarStatus
    {
        Settings = settings,
        Watches = watches,
        Scanning = radar.Scanning,
        ScanningWatchId = radar.ScanningWatchId,
        LastScanUtc = radar.LastScanUtc,
        NextScanUtc = settings.Enabled ? DealRadarClock.NextScanDue(watches, now) : null,
        UnreadAlertCount = counts.Unread,
        TotalAlertCount = counts.Total,
        UnreadProfit = counts.UnreadProfit,
        DesktopChannel = notifier.Channel,
        InQuietHours = DealRadarClock.IsQuiet(settings, now.ToLocalTime()),
        MaxWatches = DealRadarClock.MaxWatches,
        MinIntervalMinutes = DealRadarClock.MinIntervalMinutes,
    };
}

// ── Spend the budget: the sourcing basket ─────────────────────────────────────────────────────
// Every board above ranks deals one at a time. This is the only endpoint that answers the question
// the seller actually has standing at a cash machine: given $500 and these deals, WHICH ONES.
//
// It prices nothing. The candidates arrive already costed — from the local board the seller is
// looking at, and from the deals they tracked at Sourced — and the allocation is an exact knapsack
// over those numbers, so the basket can never quote a profit the table beside it doesn't.
//
// Read-only in every sense: nothing is bought, tracked, listed or sent anywhere. It answers with a
// shopping list.
app.MapPost("/api/sourcing/budget", (
    BudgetPlanRequest req, SourcingBudgetOptimizer optimizer, DealStore deals, ActionLog log) =>
{
    try
    {
        var request = new BudgetPlanRequest
        {
            Budget = req.Budget,
            Reserve = req.Reserve,
            MaxDaysToCash = req.MaxDaysToCash,
            MinCompCount = req.MinCompCount,
            IncludeThin = req.IncludeThin,
            IncludeTrackedDeals = req.IncludeTrackedDeals,
            Objective = req.Objective,
            // The scan rows go in first so that when the same post is both scanned and tracked, the
            // live price is the one the basket is built on (SourcingBudgetOptimizer.Dedupe keeps the
            // scan either way — this just keeps the order readable).
            Candidates = [.. req.Candidates ?? []],
        };

        if (req.IncludeTrackedDeals)
            request.Candidates.AddRange(TrackedDealCandidates(deals));

        var result = optimizer.Plan(request);

        log.Add("Sourcing", "Budget basket planned",
            $"{result.Budget:C0} across {result.EligibleCount} eligible deal(s) → " +
            $"{result.Plan.Picks.Count} to buy for {result.Plan.CapitalDeployed:C0}, " +
            $"{result.Plan.TotalNetProfit:C0} projected net" +
            (result.Comparison.ExtraProfit > 0
                ? $" ({result.Comparison.ExtraProfit:C0} more than buying down the list)" : ""));

        return Results.Ok(result);
    }
    catch (Exception ex)
    {
        log.Add("Error", "Budget planning failed", ex.Message);
        return Results.Ok(new BudgetPlanResult
        {
            Status = "error",
            Message = $"The budget couldn't be planned: {ex.Message}",
            Budget = req.Budget,
        });
    }
});

// Deals already on the pipeline board at Sourced — money the seller is weighing up but hasn't spent
// yet. Their numbers are the forecast FROZEN when the deal was tracked, never a fresh lookup, which
// is exactly what makes the pipeline's accuracy grading worth anything; the basket carries that
// provenance through to the pick so a stale forecast is never passed off as a live one.
static List<BudgetCandidate> TrackedDealCandidates(DealStore deals) => deals.GetAll()
    .Where(d => d.Stage == DealStages.Sourced && d.AskPrice is > 0 && d.ProjectedNetProfit is > 0)
    .Select(d => new BudgetCandidate
    {
        Id = string.IsNullOrWhiteSpace(d.SourceItemId) ? $"deal-{d.Id}" : d.SourceItemId,
        Title = d.Title,
        Source = string.IsNullOrWhiteSpace(d.Source) ? "manual" : d.Source,
        SourceLabel = string.IsNullOrWhiteSpace(d.SourceLabel) ? "Tracked deal" : d.SourceLabel,
        Url = d.SourceUrl,
        BuyPrice = d.AskPrice!.Value,
        Quantity = Math.Max(1, d.Quantity),
        NetProfit = d.ProjectedNetProfit!.Value,
        MaxBuyPrice = d.MaxBuyPrice,
        DaysToCash = d.ProjectedDaysToCash,
        // The sold-comp count is carried in the frozen basis line ("14 sold comps · High
        // confidence"), because that is the form the pipeline stores its evidence in. Unparseable
        // means unknown, never zero-and-therefore-rejected — see the evidence gate in the optimizer.
        CompCount = CompCountFromBasis(d.ProjectedBasis),
        Origin = BudgetOrigins.Tracked,
        ForecastUtc = d.ProjectedUtc,
    })
    .ToList();

static int CompCountFromBasis(string? basis)
{
    if (string.IsNullOrWhiteSpace(basis)) return 0;
    var match = System.Text.RegularExpressions.Regex.Match(basis, @"(\d+)\s+sold comp");
    return match.Success && int.TryParse(match.Groups[1].Value, out var count) ? count : 0;
}

// ── Where to sell highest ─────────────────────────────────────────────────────────────────────
// Every other pricing screen in this app assumes the answer to "where does this sell" is eBay,
// because eBay is where it lists. This one checks. It prices the item on eBay from sold history,
// prices it off eBay from live local supply, costs each venue with the seller's own fee profile,
// and reports which one actually hands over the most money.
//
// Read-only and user-initiated: it searches, it compares, and it posts nothing anywhere.
app.MapGet("/api/where-to-sell", async (
    string q, string? zip, int? radius, decimal? cost, string? sources, string? craigslistSite,
    bool? terapeak,
    LocalSupplySources registry, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakService terapeakSession,
    WhereToSellAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(q))
        return Results.BadRequest(new { error = "Tell me what the item is and I'll tell you where it pays most." });

    try
    {
        var report = await WhereToSellAsync(
            q.Trim(), zip ?? "", Math.Clamp(radius ?? 40, 1, 500), cost,
            registry.Resolve(sources), craigslistSite,
            // A Terapeak scrape drives a real browser, so it stays opt-in per request like it is
            // everywhere else — and it only helps at all when a session is actually saved.
            (terapeak ?? false) && terapeakSession.IsConnected,
            marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, analyzer, log, ct);

        log.Add("Research", "Compared where to sell",
            $"\"{q.Trim()}\": {report.Headline}");

        return Results.Ok(report);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // The comparison reaches across the comps database and up to two scraped sites. Whatever
        // broke comes back as a sentence the seller can read rather than a rejected fetch.
        log.Add("Error", "Where-to-sell comparison failed", ex.Message);
        return Results.Ok(new WhereToSellReport
        {
            Status = "error", Query = q.Trim(), ZipCode = zip ?? "", RadiusMiles = radius ?? 40,
            Error = $"The comparison couldn't be completed: {ex.Message}",
            Headline = "The comparison couldn't be completed",
            Subhead = "Nothing was changed or posted. Try it again in a moment.",
        });
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

// ── The Auction Sniper ────────────────────────────────────────────────────────────────────────
// Every other sourcing screen sends the seller somewhere else to buy. This one buys on eBay and
// sells on eBay: live auctions and Buy It Nows priced BELOW what the same item's sold comps settle
// at, with the most to bid and what winning at that price is worth after fees.
//
// With no keyword it hunts the seller's OWN completed sales — the products they have already
// proven they can move — which is the difference between a search box and a board that is worth
// opening on a Tuesday morning.
//
// Read-only against eBay: this searches, and nothing else. No bid is placed, ever. The max bid is a
// number for the seller to type into eBay's own bid box.
app.MapGet("/api/snipes", async (
    string? q, string? mode, string? sort, int? terms, int? perTerm, int? recheck, int? terapeakBudget,
    EarningsStore earnings, EbayService ebay, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket,
    AuctionSniperAnalyzer sniper, ActionLog log, CancellationToken ct) =>
{
    try
    {
        var result = await ScanSnipesAsync(
            q, mode, sort,
            // Each bound caps a fan-out: one eBay search per term per format, one comp lookup per
            // term, then one more lookup per row re-priced off its own title.
            Math.Clamp(terms ?? 5, 1, 8), Math.Clamp(perTerm ?? 25, 5, 50),
            Math.Clamp(recheck ?? 6, 0, 15), Math.Clamp(terapeakBudget ?? 3, 0, 10),
            earnings, ebay, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
            feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, sniper, log, ct);

        return Results.Ok(result);
    }
    catch (OperationCanceledException) when (ct.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception ex)
    {
        // A scan spans eBay's Browse API, the comps database and Terapeak. Whatever breaks comes
        // back as a sentence on the board rather than a rejected fetch after a long wait.
        log.Add("Error", "Auction sniper scan failed", ex.Message);
        return Results.Ok(new SnipeScanResult
        {
            Status = "error",
            PriceIsRealHours = AuctionSniperAnalyzer.PriceIsRealHours,
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

// ── Aging-inventory rescue ────────────────────────────────────────────────────────────────────
// Inventory Health says what a listing should cost today. This says what to do about the ones that
// have been ignoring that answer for months: a dated ladder of drops per listing, decided in
// advance, plus bundles that pair stuck stock with something that already sells.
//
// Read-only. The first step of a plan is applied through the existing /api/inventory/reprice, which
// keeps every brake on changing a live price in exactly one place.
app.MapGet("/api/inventory/rescue", async (
    int? maxItems, int? terapeakBudget, int? staleAfterDays, int? maxBundles,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    InventoryHealthAnalyzer analyzer, AgingInventoryRescuer rescuer, ActionLog log, CancellationToken ct) =>
{
    var staleDays = Math.Clamp(staleAfterDays ?? AgingInventoryRescuer.DefaultStaleAfterDays, 14, 730);

    // Deliberately NOT filtered to old listings, even though only old ones get a plan: the bundle
    // half of this board needs the fast movers too, and a fast mover is by definition not stale.
    // Filtering here would leave every slow item with nothing to pair it with.
    var health = await ScanInventoryHealthAsync(
        Math.Clamp(maxItems ?? 120, 1, 400), Math.Clamp(terapeakBudget ?? 5, 0, 15), minDays: 0,
        ebay, costBasis, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
        feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);

    if (health.Status == "ebay_unavailable")
        return Results.Ok(new RescueResult { Status = "ebay_unavailable", Error = health.Error });

    var result = rescuer.Build(
        health.Items, feeProfile, DateTime.UtcNow, staleDays,
        Math.Clamp(maxBundles ?? 12, 0, 50));

    result.ActiveListings = health.ActiveListings;
    result.ItemsAnalyzed = health.ItemsAnalyzed;
    result.ProductsPriced = health.ProductsPriced;
    result.TerapeakScrapesUsed = health.TerapeakScrapesUsed;
    result.DataWarning = health.DataWarning;

    var s = result.Summary;
    log.Add("Info", "Aging-inventory rescue scan",
        $"Analyzed: {result.ItemsAnalyzed} of {result.ActiveListings} active; Stale ({staleDays}d+): {s.StaleListings} " +
        $"holding ${s.TrappedCapital:0.00}; Plans: {s.PlansReady} ({s.StepsDueNow} due now); " +
        $"No plan: {s.NoPlanCount}; Bundles: {s.BundlesFound} freeing ${s.CapitalFreedByBundles:0.00}");

    return Results.Ok(result);
});

// ── Money Made — the earnings tracker ─────────────────────────────────────────────────────────
// Every other money endpoint in this file answers "what would this make?". These four answer
// "what did it make?" — buy cost, sale price, the fee eBay actually charged, net profit, and a
// running total the seller can check against their own bank statement.
//
// Read-only against eBay throughout. Importing orders reads /sell/fulfillment; nothing here lists,
// relists, reprices or messages anybody.

app.MapGet("/api/earnings", (
    EarningsStore store, CostBasisStore costBasis, EarningsCalculator calculator, FeeProfile feeProfile) =>
    Results.Ok(BuildEarnings(store, costBasis, calculator, feeProfile)));

// Pulls completed orders from eBay. Idempotent by (orderId, lineItemId) — running it twice over
// the same window updates rows rather than doubling the seller's profit, which matters because the
// natural way to use this button is to press it again.
app.MapPost("/api/earnings/import", async (
    int? days, EarningsImportRunner runner, EarningsStore store, CostBasisStore costBasis,
    EarningsCalculator calculator, FeeProfile feeProfile, ActionLog log, CancellationToken ct) =>
{
    // Same gate the automatic refresh takes, so a hand-pressed import and a scheduled one can
    // never be writing the same rows at once. The manual one waits its turn rather than bailing:
    // somebody is watching this one.
    await EarningsAutoImport.ImportGate.WaitAsync(ct);
    EarningsImportResult import;
    try
    {
        import = await runner.RunAsync(days ?? 90, ct);
    }
    finally
    {
        EarningsAutoImport.ImportGate.Release();
    }

    if (!string.Equals(import.Status, "ok", StringComparison.OrdinalIgnoreCase))
        return Results.Ok(new { import });

    log.Add("Info", $"Earnings import: {import.LinesImported} sold line(s) from {import.OrdersRead} order(s)",
        $"{import.LinesAdded} new, {import.LinesUpdated} updated, {import.MatchedToCostBasis} already have a cost basis");

    var earnings = BuildEarnings(store, costBasis, calculator, feeProfile);
    earnings.Summary.LastImportUtc = DateTimeOffset.UtcNow;
    return Results.Ok(new { import, earnings });
});

// What the automatic importer has done lately, so the earnings screen can say "updated 20 minutes
// ago" instead of leaving the seller wondering whether it is running at all.
app.MapGet("/api/earnings/auto-status", (EarningsAutoImport auto, CredentialsStore credentials) =>
    Results.Ok(new
    {
        ebayConnected  = !string.IsNullOrWhiteSpace(credentials.GetRefreshToken()),
        lastRunUtc     = auto.LastRunUtc,
        lastSuccessUtc = auto.LastSuccessUtc,
        lastStatus     = auto.LastStatus,
        lastMessage    = auto.LastMessage,
        linesImported  = auto.LastLinesImported,
    }));

// Logging a flip the app never listed — a garage-sale find sold on Facebook, a local cash deal.
// The seller's earnings are their earnings; restricting the tracker to eBay would make the total
// smaller than the truth, which is the one direction this feature must never be wrong in.
app.MapPost("/api/earnings/flips", (
    FlipUpsertRequest req, EarningsStore store, CostBasisStore costBasis,
    EarningsCalculator calculator, FeeProfile feeProfile, ActionLog log) =>
{
    try
    {
        if (req.Id is > 0)
        {
            var updated = store.ApplyEdit(req.Id.Value, req);
            if (updated is null) return Results.NotFound("That sale is no longer on record.");
            log.Add("Info", "Sale updated", $"#{updated.Id} \"{updated.Title}\"");
        }
        else
        {
            var flip = EarningsStore.FromRequest(req);
            store.Upsert(flip);
            log.Add("Info", "Sale logged", $"\"{flip.Title}\" — {flip.SalePrice:C0} on {flip.SoldUtc:yyyy-MM-dd}");
        }

        return Results.Ok(BuildEarnings(store, costBasis, calculator, feeProfile));
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});

app.MapDelete("/api/earnings/flips/{id:long}", (
    long id, EarningsStore store, CostBasisStore costBasis, EarningsCalculator calculator,
    FeeProfile feeProfile, ActionLog log) =>
{
    if (!store.Delete(id)) return Results.NotFound("That sale is no longer on record.");
    log.Add("Info", "Sale removed from earnings", $"#{id}");
    return Results.Ok(BuildEarnings(store, costBasis, calculator, feeProfile));
});

// Recording what a sold item cost. Writes to the SHARED cost-basis table whenever the sale has a
// listing ID or SKU, so a cost entered here also gives Inventory Health a real break-even floor
// for the next unit of the same item — one number, typed once, used everywhere.
app.MapPost("/api/earnings/cost", (
    FlipUpsertRequest req, EarningsStore store, CostBasisStore costBasis,
    EarningsCalculator calculator, FeeProfile feeProfile, ActionLog log) =>
{
    if (req.Id is not > 0) return Results.BadRequest("Which sale is this cost for?");
    if (req.UnitCost is null or < 0) return Results.BadRequest("Enter what you paid for it — zero or more.");

    var flip = store.Get(req.Id.Value);
    if (flip is null) return Results.NotFound("That sale is no longer on record.");

    var shared = !string.IsNullOrWhiteSpace(flip.ListingId) || !string.IsNullOrWhiteSpace(flip.Sku);

    // How many OTHER sales this one cost is about to price. A listing that sold fourteen times is
    // fourteen sales sharing one cost basis, and applying it to all of them is the correct answer —
    // but it is a surprising amount of money to move without saying so, and the seller has to be
    // told which number they just changed.
    var alsoAffected = shared
        ? store.GetAll().Count(f => f.Id != flip.Id && f.UnitCost is null
            && ((!string.IsNullOrWhiteSpace(flip.ListingId) && string.Equals(f.ListingId, flip.ListingId, StringComparison.OrdinalIgnoreCase))
             || (!string.IsNullOrWhiteSpace(flip.Sku) && string.Equals(f.Sku, flip.Sku, StringComparison.OrdinalIgnoreCase))))
        : 0;

    if (shared)
    {
        costBasis.Save(new CostBasisEntry
        {
            ListingId = flip.ListingId, Sku = flip.Sku,
            UnitCost = req.UnitCost.Value, InboundShipping = 0m,
            Note = $"From a sale logged in Money Made — {flip.Title}",
            AcquiredUtc = null,
        });
        // Cleared rather than mirrored: two copies of the same cost drift apart the moment one is
        // edited, and the per-flip value would silently win.
        flip.UnitCost = null;
    }
    else
    {
        flip.UnitCost = req.UnitCost.Value;
    }

    if (req.ShippingCost.HasValue) flip.ShippingCost = req.ShippingCost.Value;
    if (req.OtherCosts.HasValue) flip.OtherCosts = req.OtherCosts.Value;

    try { store.Upsert(flip); }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }

    log.Add("Info", "Cost recorded for a completed sale",
        $"\"{flip.Title}\" — {req.UnitCost.Value:C2}{(shared ? $" (shared cost basis; also prices {alsoAffected} other sale(s) of this item)" : "")}");

    return Results.Ok(new
    {
        earnings = BuildEarnings(store, costBasis, calculator, feeProfile),
        alsoAffected,
        sharedBasis = shared,
    });
});

// One place that assembles the page, so every endpoint that changes a flip answers with the same
// recomputed totals rather than leaving the browser to re-derive them.
static EarningsResult BuildEarnings(
    EarningsStore store, CostBasisStore costBasis, EarningsCalculator calculator, FeeProfile feeProfile)
{
    // One read of the cost table for the whole set, not one lookup per sale.
    var costs = costBasis.GetAll();
    var computed = store.GetAll()
        .Select(flip => calculator.Compute(flip, CostBasisStore.Find(costs, flip.ListingId, flip.Sku), feeProfile))
        .ToList();

    // Local time, not UTC: "this month" has to mean the month the seller is living in.
    return calculator.Summarize(computed, DateTimeOffset.Now);
}

// ── The Deal Pipeline — Sourced → Bought → Listed → Sold ──────────────────────────────────────
// Everything above answers one question about one moment. This carries a single flip end to end:
// the forecast that justified buying it, the cash that actually left, the listing it went into,
// and the sale that settled it — so the seller can see what money is in motion and what to do next.
//
// It reaches into two existing stores rather than keeping its own copies:
//   * EarningsStore supplies realized profit, which uses the fee eBay actually charged.
//   * CostBasisStore is written when a deal is listed, so the price paid — typed once, at the
//     moment it was paid — becomes the break-even floor in Inventory Health AND the cost of goods
//     in Money Made, with nobody typing it a second time.
//
// Nothing here touches eBay. The whole feature is the app's own database.

app.MapGet("/api/deals", (
    DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile) =>
    Results.Ok(BuildPipeline(deals, earnings, costBasis, earningsCalc, pipeline, feeProfile)));

// Tracking a deal. Called by "Track" in the goldmine table — which hands over the projection it
// just computed — and by the Add-a-deal form. Tracking the same local post twice updates the card
// rather than duplicating its capital and its projected profit into every total on the board.
app.MapPost("/api/deals", (
    DealUpsertRequest req, DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile, ActionLog log) =>
{
    try
    {
        if (req.Id is > 0)
        {
            var updated = deals.ApplyEdit(req.Id.Value, req);
            if (updated is null) return Results.NotFound("That deal is no longer on the board.");
            log.Add("Info", "Deal updated", $"#{updated.Id} \"{updated.Title}\" — {DealStages.Label(updated.Stage)}");
        }
        else
        {
            var deal = DealStore.FromRequest(req);
            var inserted = deals.Upsert(deal);
            log.Add("Info", inserted ? "Deal tracked" : "Deal re-tracked",
                $"\"{deal.Title}\"{(deal.ProjectedNetProfit is > 0 ? $" — {deal.ProjectedNetProfit * deal.Quantity:C0} projected" : "")}");
        }

        return Results.Ok(BuildPipeline(deals, earnings, costBasis, earningsCalc, pipeline, feeProfile));
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }
});

// Moving a card. This is where the pipeline pays for itself: reaching Listed with a listing ID and
// a purchase price writes the shared cost basis, which is the number Money Made needs before it
// will count a sale as profit at all.
app.MapPost("/api/deals/{id:long}/stage", (
    long id, DealUpsertRequest req, DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile, ActionLog log) =>
{
    var before = deals.Get(id);
    if (before is null) return Results.NotFound("That deal is no longer on the board.");

    DealRecord deal;
    try
    {
        var updated = deals.ApplyEdit(id, req);
        if (updated is null) return Results.NotFound("That deal is no longer on the board.");
        deal = updated;
    }
    catch (InvalidOperationException ex) { return Results.BadRequest(ex.Message); }

    var result = ApplyDealCostBasis(deal, earnings, costBasis);
    log.Add("Info", $"Deal moved to {DealStages.Label(deal.Stage)}",
        $"\"{deal.Title}\"{(result.CostBasisSaved ? $" — cost basis {result.CostBasisUnitCost:C2} recorded" : "")}");

    result.Pipeline = BuildPipeline(deals, earnings, costBasis, earningsCalc, pipeline, feeProfile);
    return Results.Ok(result);
});

// "Apply what you paid" — the one-click fix for a sold deal whose completed sales are sitting in
// Money Made uncounted. The pipeline already knows the purchase price; this pushes it to the shared
// cost table so the profit the seller has already earned starts being reported.
app.MapPost("/api/deals/{id:long}/apply-cost", (
    long id, DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile, ActionLog log) =>
{
    var deal = deals.Get(id);
    if (deal is null) return Results.NotFound("That deal is no longer on the board.");
    if (deal.PurchasePrice is null)
        return Results.BadRequest("This deal has no purchase price recorded yet, so there's nothing to apply.");
    if (string.IsNullOrWhiteSpace(deal.ListingId) && string.IsNullOrWhiteSpace(deal.Sku))
        return Results.BadRequest("Add the eBay listing ID or SKU first — that's what joins the cost to the sale.");

    var result = ApplyDealCostBasis(deal, earnings, costBasis);
    log.Add("Info", "Cost basis applied from the deal pipeline",
        $"\"{deal.Title}\" — {result.CostBasisUnitCost:C2}, pricing {result.SalesPriced} completed sale(s)");

    result.Pipeline = BuildPipeline(deals, earnings, costBasis, earningsCalc, pipeline, feeProfile);
    return Results.Ok(result);
});

app.MapDelete("/api/deals/{id:long}", (
    long id, DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile, ActionLog log) =>
{
    if (!deals.Delete(id)) return Results.NotFound("That deal is no longer on the board.");
    // The cost basis is deliberately left behind: it belongs to the listing, other sales may
    // already be priced by it, and deleting a card off a board is not a statement about what the
    // item cost.
    log.Add("Info", "Deal removed from the pipeline", $"#{id}");
    return Results.Ok(BuildPipeline(deals, earnings, costBasis, earningsCalc, pipeline, feeProfile));
});

// Writes what the seller paid into the SHARED cost-basis table, and reports how many already-
// completed sales that just gave a real profit figure to. Inbound extras are divided across the
// units, because CostBasisEntry is per unit and a $60 pickup run on a lot of six is $10 a unit.
static DealStageChangeResult ApplyDealCostBasis(DealRecord deal, EarningsStore earnings, CostBasisStore costBasis)
{
    var result = new DealStageChangeResult();

    var hasKey = !string.IsNullOrWhiteSpace(deal.ListingId) || !string.IsNullOrWhiteSpace(deal.Sku);
    if (!hasKey || deal.PurchasePrice is null) return result;

    var perUnitExtra = deal.Quantity > 0 ? Math.Round(deal.PurchaseExtraCost / deal.Quantity, 2) : deal.PurchaseExtraCost;

    // Counted BEFORE the write: these are the sales whose profit was unknown and is about to be
    // known. Counting after would report zero every time.
    result.SalesPriced = earnings.GetAll().Count(f =>
        f.Status == "paid" && f.UnitCost is null
        && ((!string.IsNullOrWhiteSpace(deal.ListingId) && string.Equals(f.ListingId, deal.ListingId, StringComparison.OrdinalIgnoreCase))
         || (!string.IsNullOrWhiteSpace(deal.Sku) && string.Equals(f.Sku, deal.Sku, StringComparison.OrdinalIgnoreCase)))
        && CostBasisStore.Find(costBasis.GetAll(), f.ListingId, f.Sku) is null);

    costBasis.Save(new CostBasisEntry
    {
        ListingId = deal.ListingId,
        Sku = deal.Sku,
        UnitCost = deal.PurchasePrice.Value,
        InboundShipping = perUnitExtra,
        Note = $"From the deal pipeline — {deal.Title}",
        AcquiredUtc = deal.BoughtUtc,
    });

    result.CostBasisSaved = true;
    result.CostBasisUnitCost = Math.Round(deal.PurchasePrice.Value + perUnitExtra, 2);
    result.Message = result.SalesPriced > 0
        ? $"Recorded {result.CostBasisUnitCost:C2} as what this cost you — that also prices {result.SalesPriced} completed sale{(result.SalesPriced == 1 ? "" : "s")} in Money Made."
        : $"Recorded {result.CostBasisUnitCost:C2} as what this cost you. Inventory Health now has a real break-even floor for it, and any sale will count as real profit.";
    return result;
}

// One place that assembles the board, so every mutation answers with the recomputed pipeline and
// the browser never has to re-derive a total.
static DealPipelineResult BuildPipeline(
    DealStore deals, EarningsStore earnings, CostBasisStore costBasis,
    EarningsCalculator earningsCalc, DealPipelineCalculator pipeline, FeeProfile feeProfile)
{
    // One read of the cost table for the whole set, exactly as BuildEarnings does — a board with
    // 200 deals must not become 200 scans of the same table.
    var costs = costBasis.GetAll();
    var computed = earnings.GetAll()
        .Select(flip => earningsCalc.Compute(flip, CostBasisStore.Find(costs, flip.ListingId, flip.Sku), feeProfile))
        .ToList();

    return pipeline.Build(deals.GetAll(), computed, DateTimeOffset.UtcNow);
}

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

// ── Recover lost sales: relist + Second Chance Offers ─────────────────────────────────────────
// The one pile of inventory nothing else in this app can see. An ended, unsold listing is not in
// the active-listing import, never reached Money Made, and on eBay's own Unsold page comes with a
// Relist button and nothing else — no market read, no cost basis, no reason it failed. So the
// platform's default is to put the same failed price back up and fail again.
//
// Two different kinds of money come back here, and the board leads with the bigger one: a relist
// is a second run at a maybe, while a Second Chance Offer goes to somebody who publicly bid a
// specific dollar amount on this exact item and lost.
app.MapGet("/api/relist/recover", async (
    int? days, int? maxItems, int? terapeakBudget, decimal? minProfit, int? bidderBudget,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    RelistAnalyzer analyzer, ActionLog log, CancellationToken ct) =>
{
    var result = await ScanRelistRecoveryAsync(
        Math.Clamp(days ?? RelistAnalyzer.DefaultLookbackDays, 1, RelistAnalyzer.MaxLookbackDays),
        Math.Clamp(maxItems ?? 120, 1, 400),
        Math.Clamp(terapeakBudget ?? 3, 0, 15),
        Math.Clamp(bidderBudget ?? 15, 0, 60),
        // With no explicit ask, the floor is the one the seller already set in Fees & Costs — the
        // same default the offers board uses, so the two screens cannot disagree about the floor.
        Math.Max(0m, minProfit ?? feeProfile.MinimumNetProfit),
        ebay, costBasis, marketplace, normalizer, matcher, priceEstimator, sellThroughCalc, profitCalc,
        feeProfile, opportunityScorer, confidenceScorer, terapeakMarket, terapeak, analyzer, log, ct);

    return Results.Ok(result);
});

// Puts listings back on eBay. The same three brakes as the repricer and the offers board, because
// this is the third endpoint in the app that creates something buyer-visible:
//   1. It previews by default — `dryRun` has to be explicitly false.
//   2. `confirmed` has to be true on top of that.
//   3. Every relist price is re-checked against a floor the SERVER recomputes from the stored cost
//      basis, not the one the browser sent. Going under it takes a deliberate opt-in, on the
//      record in the action log.
app.MapPost("/api/relist/run", async (
    RelistRequest req, EbayService ebay, CostBasisStore costBasis, FeeProfile feeProfile,
    ProfitCalculator profitCalc, ActionLog log) =>
{
    var items = req?.Items ?? [];
    var dryRun = req is null || req.DryRun || !req.Confirmed;
    var result = new RelistResult { DryRun = dryRun, Requested = items.Count };

    if (items.Count == 0) return Results.Ok(result);
    if (items.Count > 50)
        return Results.BadRequest("Too many listings in one relist — 50 at a time is the limit.");

    var minProfit = Math.Max(0m, req?.MinNetProfit ?? feeProfile.MinimumNetProfit);
    var allCosts = costBasis.GetAll();

    foreach (var item in items)
    {
        var quantity = Math.Max(1, item.Quantity);
        var row = new RelistItemResult
        {
            ListingId = item.ListingId, Title = item.Title,
            OldPrice = item.EndPrice, NewPrice = item.NewPrice,
            ChangePercent = item.EndPrice > 0m
                ? Math.Round((item.NewPrice - item.EndPrice) / item.EndPrice * 100m, 1) : 0m,
        };
        result.Items.Add(row);

        if (string.IsNullOrWhiteSpace(item.ListingId))
        {
            row.Status = "skipped";
            row.Message = "No eBay item ID — there is nothing to relist.";
            result.Skipped++;
            continue;
        }

        if (item.NewPrice <= 0m)
        {
            row.Status = "skipped";
            row.Message = "A relist needs a price above zero.";
            result.Skipped++;
            continue;
        }

        // Recomputed here rather than trusted from the request: the floor is the safety property of
        // this endpoint, and a number that arrived over HTTP is not one.
        var cost = CostBasisStore.Find(allCosts, item.ListingId, item.Sku);
        if (cost is not null && !(req?.AllowBelowFloor ?? false))
        {
            var breakEven = profitCalc.Calculate(
                supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: item.NewPrice,
                quickSalePrice: item.NewPrice, buyerPaidShipping: 0m, fees: feeProfile).BreakEvenSalePrice;

            var (floor, _) = breakEven == decimal.MaxValue
                ? ((decimal?)null, "")
                : NetProceedsCalculator.MinimumOffer(breakEven, feeProfile, 0m, minProfit);

            if (floor is decimal f && item.NewPrice < f)
            {
                row.Status = "skipped";
                row.Message = minProfit > 0m
                    ? $"${item.NewPrice:0.00} leaves less than the ${minProfit:0.00} profit you asked to keep (floor ${f:0.00} on a ${cost.TotalUnitCost:0.00} cost). Relisting it would book the loss again."
                    : $"${item.NewPrice:0.00} is below the ${f:0.00} break-even on a ${cost.TotalUnitCost:0.00} cost basis.";
                result.Skipped++;
                continue;
            }
        }

        if (dryRun)
        {
            row.Status = "preview";
            row.Message = Math.Abs(item.NewPrice - item.EndPrice) < 0.01m
                ? $"Would relist at the same ${item.NewPrice:0.00}."
                : $"Would relist at ${item.NewPrice:0.00} (was ${item.EndPrice:0.00}).";
            result.ListedValue += item.NewPrice * quantity;
            continue;
        }

        try
        {
            var (newItemId, insertionFee) = await ebay.RelistListingAsync(
                item.ListingId, item.NewPrice, quantity, item.IsAuction);

            row.Status = "relisted";
            row.NewListingId = newItemId;
            row.InsertionFee = insertionFee;
            row.Message = $"Back on eBay as item {newItemId} at ${item.NewPrice:0.00}"
                        + (insertionFee is decimal fee ? $" (${fee:0.00} insertion fee)." : ".");
            result.Relisted++;
            result.ListedValue += item.NewPrice * quantity;
            if (insertionFee is decimal charged) result.TotalFees += charged;

            log.Add("Info", "Listing relisted",
                $"{item.ListingId} \"{item.Title}\" → {newItemId}: ${item.EndPrice:0.00} → ${item.NewPrice:0.00}"
                + ((req?.AllowBelowFloor ?? false) ? " (profit floor overridden by the seller)" : ""));
        }
        catch (EbayPermissionException ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Relist blocked by missing eBay permission", ex.Message);
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Relist failed", $"{item.ListingId}: {ex.Message}");
        }
    }

    result.ListedValue = Math.Round(result.ListedValue, 2);
    result.TotalFees = Math.Round(result.TotalFees, 2);
    return Results.Ok(result);
});

// Sends Second Chance Offers to bidders who lost an ended auction. Same three brakes again, with
// one extra rule that is specific to this call: eBay will not carry an offer above what the
// recipient already bid, so the offer price is THEIR number — the only question this endpoint can
// answer is whether that number still clears the seller's floor.
app.MapPost("/api/relist/second-chance", async (
    SecondChanceRequest req, EbayService ebay, CostBasisStore costBasis, FeeProfile feeProfile,
    ProfitCalculator profitCalc, ActionLog log) =>
{
    var items = req?.Items ?? [];
    var dryRun = req is null || req.DryRun || !req.Confirmed;
    var result = new SecondChanceResult { DryRun = dryRun, Requested = items.Count };

    if (items.Count == 0) return Results.Ok(result);
    if (items.Count > 50)
        return Results.BadRequest("Too many offers in one send — 50 at a time is the limit.");

    var duration = RelistAnalyzer.NormalizeDuration(req?.DurationDays ?? RelistAnalyzer.DefaultOfferDays);
    var message = RelistAnalyzer.CleanMessage(req?.Message);
    var minProfit = Math.Max(0m, req?.MinNetProfit ?? feeProfile.MinimumNetProfit);
    var allCosts = costBasis.GetAll();

    foreach (var item in items)
    {
        var row = new SecondChanceResultItem
        {
            ListingId = item.ListingId, Title = item.Title,
            BidderUserId = item.BidderUserId, OfferPrice = item.OfferPrice,
        };
        result.Items.Add(row);

        if (string.IsNullOrWhiteSpace(item.ListingId) || string.IsNullOrWhiteSpace(item.BidderUserId))
        {
            row.Status = "skipped";
            row.Message = "An offer needs both the ended item and the bidder it goes to.";
            result.Skipped++;
            continue;
        }

        // A masked bidder ID cannot receive an offer. Caught here as well as in the analyzer,
        // because this endpoint accepts whatever the browser sends it.
        if (item.BidderUserId.Contains('*'))
        {
            row.Status = "skipped";
            row.Message = "eBay masked this bidder's user ID, so no offer can be addressed to them.";
            result.Skipped++;
            continue;
        }

        if (item.OfferPrice <= 0m)
        {
            row.Status = "skipped";
            row.Message = "eBay didn't disclose what this bidder bid, so there is no price to offer.";
            result.Skipped++;
            continue;
        }

        var cost = CostBasisStore.Find(allCosts, item.ListingId, item.Sku);
        if (cost is not null && !(req?.AllowBelowFloor ?? false))
        {
            var breakEven = profitCalc.Calculate(
                supplierUnitCost: cost.TotalUnitCost, quantity: 1, expectedSalePrice: item.OfferPrice,
                quickSalePrice: item.OfferPrice, buyerPaidShipping: 0m, fees: feeProfile).BreakEvenSalePrice;

            var (floor, _) = breakEven == decimal.MaxValue
                ? ((decimal?)null, "")
                : NetProceedsCalculator.MinimumOffer(breakEven, feeProfile, 0m, minProfit);

            if (floor is decimal f && item.OfferPrice < f)
            {
                row.Status = "skipped";
                row.Message = $"They bid ${item.OfferPrice:0.00} and you need ${f:0.00} to clear costs on a ${cost.TotalUnitCost:0.00} basis. eBay won't carry an offer above their bid, so there is no price that works for both of you.";
                result.Skipped++;
                continue;
            }
        }

        if (dryRun)
        {
            row.Status = "preview";
            row.Message = $"Would offer this to {item.BidderUserId} at ${item.OfferPrice:0.00} for {duration} day{(duration == 1 ? "" : "s")}.";
            result.OfferedValue += item.OfferPrice;
            continue;
        }

        try
        {
            var offerItemId = await ebay.SendSecondChanceOfferAsync(
                item.ListingId, item.BidderUserId, item.OfferPrice, duration, message);

            row.Status = "sent";
            row.OfferItemId = offerItemId;
            row.Message = $"Offered to {item.BidderUserId} at ${item.OfferPrice:0.00}, open for {duration} day{(duration == 1 ? "" : "s")}.";
            result.Sent++;
            result.OfferedValue += item.OfferPrice;

            log.Add("Info", "Second Chance Offer sent",
                $"{item.ListingId} \"{item.Title}\" to a losing bidder at ${item.OfferPrice:0.00}"
                + ((req?.AllowBelowFloor ?? false) ? " (profit floor overridden by the seller)" : ""));
        }
        catch (EbayPermissionException ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Second Chance Offer blocked by missing eBay permission", ex.Message);
        }
        catch (Exception ex)
        {
            row.Status = "failed";
            row.Message = ex.Message;
            result.Failed++;
            log.Add("Warning", "Second Chance Offer failed", $"{item.ListingId}: {ex.Message}");
        }
    }

    result.OfferedValue = Math.Round(result.OfferedValue, 2);
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

// The lost-sales scan. Same shape as ScanInventoryHealthAsync above and rationed the same way —
// one comp lookup per distinct PRODUCT rather than per listing, cache-only first, then the scrape
// budget spent where the most money hangs on the answer — because the two screens ask the same
// pricing question about different halves of the seller's inventory.
//
// One thing is rationed that the health scan has no equivalent for: bidder lookups. Every ended
// auction with bids costs one more eBay call to find out who lost it, so that budget goes to the
// auctions whose losing bidders are worth the most.
static async Task<RelistRecoveryResult> ScanRelistRecoveryAsync(
    int lookbackDays, int maxItems, int terapeakBudget, int bidderBudget, decimal minNetProfit,
    EbayService ebay, CostBasisStore costBasis, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    RelistAnalyzer analyzer, ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nowUtc = DateTime.UtcNow;
    var result = new RelistRecoveryResult
    {
        LookbackDays = lookbackDays,
        MinNetProfit = minNetProfit,
        DefaultSellerMessage = RelistAnalyzer.DefaultSellerMessage,
    };

    List<EbayEndedListing> ended;
    try
    {
        ended = await ebay.GetUnsoldListingsAsync(lookbackDays);
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Lost-sales scan could not read eBay's unsold list", ex.Message);
        return new RelistRecoveryResult { Status = "ebay_unavailable", Error = ex.Message, LookbackDays = lookbackDays };
    }

    result.Summary.EndedListings = ended.Count;

    // Biggest money first when the cap bites, so a truncated scan is still the useful part of it.
    var scanned = ended
        .Where(e => !string.IsNullOrWhiteSpace(e.Title) && !string.IsNullOrWhiteSpace(e.ListingId))
        .OrderByDescending(e => e.Price * Math.Max(1, e.QuantityUnsold))
        .Take(maxItems)
        .ToList();

    if (scanned.Count == 0)
    {
        sw.Stop();
        log.Add("Info", "Lost-sales scan", $"No unsold listings in the last {lookbackDays} days.");
        return result;
    }

    // One lookup per product signature, on Terapeak's own cache key — so two differently-worded
    // listings for the same item share both the group and any cached scrape.
    var groups = new Dictionary<string, (string LookupTitle, List<EbayEndedListing> Listings)>(StringComparer.OrdinalIgnoreCase);
    var keyOf = new Dictionary<string, string>(StringComparer.Ordinal);
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
                var askedValue = g.Value.Listings.Sum(l => l.Price * Math.Max(1, l.QuantityUnsold));
                var atStake = priced.HasPrice
                    ? g.Value.Listings.Sum(l => Math.Abs(l.Price - (priced.ExpectedSale ?? priced.Median)!.Value) * Math.Max(1, l.QuantityUnsold))
                    : askedValue;
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
    var rows = scanned.Select(e => analyzer.Build(
            e, pricing[keyOf[e.ListingId]], CostBasisStore.Find(costs, e.ListingId, e.Sku),
            feeProfile, nowUtc, lotQtyOf[e.ListingId], minNetProfit))
        .ToList();
    var rowById = rows.ToDictionary(r => r.ListingId, StringComparer.OrdinalIgnoreCase);

    // ── Who lost the auctions ──────────────────────────────────────────────────────────────────
    // One eBay call per auction, so only the ones worth it, and a failure on any single auction
    // never takes the board down — the relist half of the answer is still true without it.
    foreach (var listingId in RelistAnalyzer.SelectBidderLookups(scanned, bidderBudget))
    {
        if (!rowById.TryGetValue(listingId, out var row)) continue;
        try
        {
            var bidders = await ebay.GetSecondChanceBiddersAsync(listingId);
            RelistAnalyzer.ApplyBidders(row, bidders.Select(b => RelistAnalyzer.BuildBidder(
                b.UserId, b.MaxBid, b.Quantity, row.FloorPrice, row.BreakEvenPrice, feeProfile)));
            result.BidderLookups++;
        }
        catch (EbayPermissionException ex)
        {
            row.BidderNote = ex.Message;
            log.Add("Warning", "Second-chance bidder lookup blocked by missing eBay permission", ex.Message);
        }
        catch (Exception ex)
        {
            row.BidderNote = "eBay wouldn't say who bid on this one, so any Second Chance Offers here are unknown rather than absent.";
            log.Add("Warning", "Second-chance bidder lookup failed (non-fatal)", $"{listingId}: {ex.Message}");
        }
    }

    result.Items = RelistAnalyzer.Rank(rows);
    result.Summary = RelistAnalyzer.Summarize(result.Items);
    result.Summary.EndedListings = ended.Count;
    result.Summary.Analyzed = rows.Count;

    if (result.Items.All(r => r.MarketPrice is null))
    {
        var compsReachable = await SoldCompsReachableAsync(marketplace, ct);
        result.DataWarning = (compsReachable, terapeak.IsConnected) switch
        {
            (false, false) => "No eBay sold-price source is available — connect Terapeak in Settings, or configure the sold-comps database, to price these before you relist them.",
            (true, _) => "The sold-comps database had no history for any of these listings, so every relist price below is your old price rather than a market-checked one.",
            (false, true) => "Terapeak is connected but returned no sold history for these listings.",
        };
    }

    sw.Stop();
    log.Add("Info", "Lost-sales scan",
        $"Ended unsold ({lookbackDays}d): {ended.Count}; Analyzed: {rows.Count} across {result.ProductsPriced} product(s); " +
        $"Terapeak scrapes: {result.TerapeakScrapesUsed}; Bidder lookups: {result.BidderLookups}; " +
        $"Ready to relist: {result.Summary.ReadyToRelist}; Second-chance bidders: {result.Summary.SecondChanceBidders}; " +
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
    IReadOnlyList<ILocalSupplySource> sources, string? craigslistSite, decimal retailSalesTaxPercent,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket, TerapeakService terapeak,
    LocalArbitrageAnalyzer analyzer, ActionLog log, CancellationToken ct,
    CouponService? couponService = null, ResaleCategory? category = null)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var wanted = category ?? ResaleCategoryCatalog.Anything;

    // Sequential: one of these sources drives a real browser, and running that concurrently with
    // anything else turns a slow search into a stuck one.
    var searches = new List<LocalSupplySearchResult>();
    foreach (var source in sources)
        searches.Add(await SearchLocalSourceAsync(source, q, zip, radius, craigslistSite, wanted, ct));

    var search = LocalSupplyMerger.Merge(searches, q, zip, radius);

    // What each row IS, decided once and before anything is priced — because the category chooses
    // what may value the row and what selling it costs, and both of those are wrong by default for
    // anything that isn't a small parcel. See ResaleCategoryCatalog.
    ResaleCategoryCatalog.ClassifyAll(search.Items, wanted);

    var result = new LocalArbitrageResult
    {
        Status = search.Status, Query = search.Query, ZipCode = search.ZipCode,
        CategoryId = wanted.Id, CategoryLabel = wanted.Label,
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
        // Listings that aren't the product — a repair service, a manual, a core charge — are dropped
        // before anything prices them. They carry the product's own part number, so the comps matcher
        // scores them as exact hits and a $1 service ad against an $899 board ranks first on the
        // whole scan at five-figure ROI. Screened here rather than per-source: it is the same junk
        // whichever site it came from. See NonItemListingDetector for why this is phrases, not words.
        var screened = search.Items.Where(i => i.Price is > 0 || i.IsFree).ToList();
        var goods = screened.Where(i => !NonItemListingDetector.IsNotTheItem(i.Title)).ToList();
        result.NotTheItemCount = screened.Count - goods.Count;

        // A listing with no parseable price has no cost basis, so there is no profit to compute for
        // it. "Free" is kept — it's the best possible cost basis, not a missing one. The cap is shared
        // out across sources rather than applied to one flat cheapest-first list, which would spend
        // the whole budget on whichever site returned the most rows.
        var priceable = LocalSupplyMerger.TakeBalanced(goods, maxItems);
        result.ItemsAnalyzed = priceable.Count;

        // The normalized brand/model/spec signature, which is also Terapeak's cache key — so two
        // differently-worded tiles for the same product share both the group and the cached scrape.
        // On a vehicle the identity is the year, make and model, and the generic product signature
        // cannot see any of them: it keys a classifieds title on the brand, which would put a 2011
        // Tundra and a 2003 Camry in one group and price them off one lookup. Everything else keys
        // exactly as it always has. See VehicleTitleParser.GroupKey.
        var groups = LocalArbitrageAnalyzer.GroupByProduct(
            priceable, l => VehicleTitleParser.GroupKey(l.Vehicle) is { Length: > 0 } key
                ? key
                : TerapeakMarketService.BuildCacheKey(normalizer.Normalize(l.Title)));
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
                        ? analyzer.Build(cheapest, pricing[g.Key], feeProfile, retailSalesTaxPercent).NetProfit
                        : null;
                    return (g.Key, PreliminaryProfit: preliminary, HasTerapeak: cached[g.Key], LocalAsk: g.LowestAsk);
                }), terapeakBudget);

            foreach (var key in targets)
            {
                pricing[key] = await PriceAsync(byKey[key], allowScrape: true);
                result.TerapeakScrapesUsed++;
            }
        }

        // ── Coupons: cut the buy price before anything is judged against it ─────────────────────────
        // The buy side of the retail rows. Bounded the same way everything else in this scan is:
        // per STORE rather than per row (thirty Amazon deals are one lookup), and only for the
        // handful of stores carrying the most money on this board — see CollectCouponsAsync.
        var (couponsByStore, couponStores) = await CollectCouponsAsync(couponService, priceable, log, ct);
        result.CouponStores = couponStores;

        // ── Rank ────────────────────────────────────────────────────────────────────────────────────
        var rows = groups
            .SelectMany(g => g.Listings.Select(l =>
                analyzer.Build(l, pricing[g.Key], feeProfile, retailSalesTaxPercent, CouponsForListing(couponsByStore, l))))
            .ToList();

        result.Items = LocalArbitrageAnalyzer.Rank(rows, sort);
        // What kinds of thing this board is made of, for the category filter above the table. A scan
        // for "anything" routinely comes back as six categories in one ranking, and until it says so
        // a seller filtering for the truck has to read every row.
        result.Categories = ResaleCategoryCatalog.Tally(result.Items);
        // And the rows the app deliberately refused to price. Surfaced rather than buried: on a scan
        // of the cars board this can be most of the board, and a column of dashes with no
        // explanation reads as a broken feature instead of as the honest answer it is.
        result.ManualValuationCount = result.Items.Count(r => r.Valuation is { Status: ValuationStatuses.Manual });
        result.GoldmineCount = result.Items.Count(r => r.Verdict == "goldmine");
        // The rows a seller working off a fixed pot of cash can actually run: profitable AND back in
        // the bank inside three weeks.
        result.FastCashCount = result.Items.Count(r => r.NetProfit is > 0 && r.SpeedTier == "fast");
        // What the whole board is worth if every profitable listing on it were bought and flipped —
        // an upper bound on the search, not a forecast.
        result.TotalPotentialProfit = Math.Round(result.Items.Where(r => r.NetProfit is > 0).Sum(r => r.NetProfit!.Value), 2);

        // What is on the table on the BUY side. Counted only on rows with an offer actually worth
        // sending — a "walk" row has no upside, and a long shot isn't money anyone should bank on.
        var negotiable = result.Items
            .Where(r => r.Negotiation is { Verdict: "buy_now" or "negotiate" or "must_negotiate" } n && n.Upside > 0)
            .ToList();
        result.NegotiableCount = negotiable.Count;
        result.NegotiationUpside = Math.Round(negotiable.Sum(r => r.Negotiation!.Upside), 2);

        // The liquidation half of the board, counted separately because it expires. A closeout row
        // is not a listing that will still be there tomorrow — it is a bid that closes, and a
        // profitable one the seller reads on Thursday for an auction that ended Wednesday is a
        // deal they never had.
        var now = DateTime.UtcNow;
        result.LiquidationCount = result.Items.Count(r => r.Liquidation is not null);
        result.ClosingSoonCount = result.Items.Count(r =>
            r.NetProfit is > 0 && r.Liquidation is { } lot && LiquidationLotPricer.ClosingSoon(lot, now));

        // The free half of the board, counted separately because it is the only money on it that
        // risks nothing: nothing was spent, so a row that doesn't sell costs the seller an afternoon
        // rather than a cost basis. A seller with no cash at all can work this column and no other.
        var freebies = result.Items.Where(r => r.Freebie is not null).ToList();
        result.FreebieCount = freebies.Count;
        result.FreeMoneyOnTheTable = Math.Round(freebies.Where(r => r.NetProfit is > 0).Sum(r => r.NetProfit!.Value), 2);
        // And the half of THAT which is gone by tomorrow. A free post is claimed the same day and a
        // stated one-day coupon is exactly what it says, so this is the number that decides whether
        // the list is worth reading now or at the weekend.
        result.ExpiringTodayCount = freebies.Count(r =>
            r.NetProfit is > 0 && r.Freebie!.Urgency is FreebieUrgency.Today or FreebieUrgency.FirstCome);

        // The covered half of the board. A used item with warranty left is not a better-priced row,
        // it is a different KIND of row: the same flip with the downside cut off. A seller who can't
        // absorb one bad buy shops this column before the profit ranking, so it gets its own count.
        var covered = result.Items.Where(r => r.Warranty is { Kind: not WarrantyKinds.None }).ToList();
        result.WarrantyCount = covered.Count;
        result.TransferableWarrantyCount = covered.Count(r => r.NetProfit is > 0 && r.Warranty!.TransfersToBuyer);
        // Already inside TotalPotentialProfit, unlike the coupon and negotiation figures — a stated
        // warranty is a claim about the goods that the seller repeats in their own listing, not a
        // promo code that may be dead. Reported anyway so the bare-comps number stays recoverable.
        result.WarrantyUpliftOnTheTable = Math.Round(
            covered.Where(r => r.NetProfit is > 0).Sum(r => r.Warranty!.ResaleUplift), 2);
        // And the rows where the listing says the opposite. Counted only where the money is big
        // enough for "no returns" to be the loss — see WarrantyPricer.RiskNote.
        result.AsIsRiskCount = result.Items.Count(r => r.Warranty?.RiskNote is not null);

        // The buy-side half of the retail rows, counted apart from the board's own totals and never
        // added into them: a public code is a claim, and TotalPotentialProfit is a figure the app
        // stands behind. Only rows that make money WITH the code are counted — extra profit on a
        // flip that still loses money is not money.
        var couponed = result.Items.Where(r => r.Coupons is { ExtraProfit: > 0, NetProfitWithCoupons: > 0 }).ToList();
        result.CouponedCount = couponed.Count;
        result.CouponSavingsOnTheTable = Math.Round(couponed.Sum(r => r.Coupons!.ExtraProfit!.Value), 2);
        // The rows that only exist because of a code — a deal the board would otherwise have told
        // the seller to walk away from.
        result.CouponRescuedCount = couponed.Count(r => r.Coupons!.RescuesTheDeal);

        // How many rows each checked store actually accounts for, so a store with one $12 row isn't
        // presented with the same weight as the one behind half the board.
        foreach (var store in result.CouponStores)
        {
            store.RowCount = result.Items.Count(r =>
                r.IsRetail && CouponCatalog.Resolve(r.Retailer)?.Id == store.MerchantId);
        }

        if (result.Items.All(r => r.EbayExpectedSale is null))
        {
            result.SoldCompsConfigured = await SoldCompsReachableAsync(marketplace, ct);

            // A board of unpriced rows has two completely different explanations, and telling a
            // seller the wrong one sends them to fix something that isn't broken. If the app REFUSED
            // to price these — because nothing it reads values cars — then connecting Terapeak and
            // configuring a comps database would change nothing at all, and saying otherwise would
            // be the app blaming its own setup for a limit it knows about.
            var allRefused = result.Items.Count > 0 && result.ManualValuationCount == result.Items.Count;

            result.DataWarning = allRefused
                ? $"None of these could be valued from sold data — {wanted.Label.ToLowerInvariant()} isn't something " +
                  "this app's sold-comps database can price. Every row still shows what it costs and links to the " +
                  "eBay sold listings for it, so you can put a number on the ones worth chasing."
                : (result.SoldCompsConfigured, terapeak.IsConnected) switch
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
        $"\"{q}\" in {wanted.Label} within {result.RadiusMiles} mi{(string.IsNullOrWhiteSpace(zip) ? "" : $" of {zip}")} " +
        $"on {string.Join(" + ", sources.Select(s => s.Id))}; " +
        $"Valued by hand (no comp source for the category): {result.ManualValuationCount}; " +
        $"Local listings: {result.LocalListingsFound}; Analyzed: {result.ItemsAnalyzed} across " +
        $"{result.ProductsPriced} product(s); Terapeak scrapes: {result.TerapeakScrapesUsed}; " +
        $"Goldmines: {result.GoldmineCount}; Fast cash (<={DaysToCashEstimator.FastCashDays}d): {result.FastCashCount}; " +
        $"Still under warranty: {result.WarrantyCount} ({result.TransferableWarrantyCount} transferable, " +
        $"+{result.WarrantyUpliftOnTheTable:C} resale); Sold as-is at risk: {result.AsIsRiskCount}; " +
        $"Sorted by: {LocalArbitrageAnalyzer.NormalizeSort(sort)}; Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// The public promo codes for the stores this board is buying from.
//
// Bounded on the only axis that matters: one lookup per STORE, not per row — thirty Amazon deals
// are one read of a coupon list, and a board of thirty retail rows across seven stores costs at
// most CouponService.MaxStoresPerScan lookups. Stores are checked biggest-money-first, so when the
// cap bites it drops the store with $30 on it rather than the one with $900.
//
// Never fails the scan. A blocked list, a moved feed or a slow one arrives as a per-store status
// beside real results, exactly like a source that couldn't be searched.
static async Task<(Dictionary<string, List<CouponOffer>> ByStore, List<CouponStoreOutcome> Stores)>
    CollectCouponsAsync(
        CouponService? coupons, IReadOnlyList<LocalSupplyListing> listings, ActionLog log, CancellationToken ct)
{
    var byStore = new Dictionary<string, List<CouponOffer>>(StringComparer.OrdinalIgnoreCase);
    var outcomes = new List<CouponStoreOutcome>();
    if (coupons is null) return (byStore, outcomes);

    // Only a till takes a promo code, and only a row with a price has anything to take it off.
    var stores = listings
        .Where(l => l.IsRetail && l.Price is > 0 && !string.IsNullOrWhiteSpace(l.Retailer))
        .Select(l => (Merchant: CouponCatalog.Resolve(l.Retailer), l.Price))
        .Where(x => x.Merchant is not null)
        .GroupBy(x => x.Merchant!.Id, StringComparer.OrdinalIgnoreCase)
        .Select(g => (Merchant: CouponCatalog.Resolve(g.First().Merchant!.Label)!, Money: g.Sum(x => x.Price ?? 0m)))
        .OrderByDescending(x => x.Money)
        .Take(CouponService.MaxStoresPerScan)
        .ToList();

    foreach (var (merchant, _) in stores)
    {
        var result = await coupons.LookupAsync(merchant.Label, ct);

        if (result.Offers.Count > 0) byStore[merchant.Id] = result.Offers;

        outcomes.Add(new CouponStoreOutcome
        {
            MerchantId = merchant.Id,
            MerchantLabel = merchant.Label,
            Status = result.Status,
            OfferCount = result.Offers.Count,
            // The stores where a code list is the wrong place to look say so, rather than coming
            // back empty and reading as "no discount available".
            Note = result.MerchantNote,
            Error = result.Error,
            ManualSites = result.ManualSites,
        });
    }

    if (outcomes.Count > 0)
    {
        log.Add("Info", "Coupon check",
            $"{outcomes.Count} store(s) checked — " +
            string.Join(" · ", outcomes.Select(o => $"{o.MerchantLabel}: {o.OfferCount}")));
    }

    return (byStore, outcomes);
}

// The codes that apply to one row: its own store's, and nobody else's. A Home Depot code on a
// Newegg row would be a discount the seller cannot get, printed under a profit figure.
static IReadOnlyList<CouponOffer>? CouponsForListing(
    Dictionary<string, List<CouponOffer>> byStore, LocalSupplyListing listing)
{
    if (!listing.IsRetail || byStore.Count == 0) return null;

    var merchant = CouponCatalog.Resolve(listing.Retailer);
    return merchant is not null && byStore.TryGetValue(merchant.Id, out var offers) ? offers : null;
}

// Where to sell highest: one item, priced on every venue this app can see, ranked by what the
// seller actually banks. Two lookups, both already built:
//
//   * eBay is priced by AnalyzeProductAsync — the same sold-comps + Terapeak pipeline the
//     Opportunity Finder and Local Deals use, so the eBay column here is the eBay number the rest
//     of the app would give.
//   * Everywhere else is priced from live local supply through the same ILocalSupplySource
//     registry that Local Deals searches, then filtered through ComparableMatcher so a search for
//     "Antminer S19j Pro" is not priced off a mining-rig-shaped shelf someone is selling nearby.
//
// The money comparison itself is WhereToSellAnalyzer and is pure — everything here is I/O.
static async Task<WhereToSellReport> WhereToSellAsync(
    string q, string zip, int radius, decimal? cost,
    IReadOnlyList<ILocalSupplySource> sources, string? craigslistSite, bool allowTerapeakScrape,
    IMarketplaceRepository marketplace, ProductNormalizer normalizer, ComparableMatcher matcher,
    MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc, ProfitCalculator profitCalc,
    FeeProfile feeProfile, OpportunityScoringService opportunityScorer, ConfidenceScoringService confidenceScorer,
    WhereToSellAnalyzer analyzer, ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();

    // ── The eBay side: what buyers actually paid ─────────────────────────────────────────────
    var analysis = await AnalyzeProductAsync(
        q, supplierUnitCost: cost, quantity: 1, listingType: "FIXED_PRICE",
        activeListingsAlreadyFetched: null, ebayForCompetitionFallback: null,
        allowRealTerapeakScrape: allowTerapeakScrape,
        normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
        opportunityScorer, confidenceScorer, log, ct);
    var resale = ResalePricing.From(analysis, q);

    // ── Everywhere else: what the item is going for nearby, right now ────────────────────────
    // Sequential for the same reason every other local scan is: one of these sources is a real
    // browser, and running it alongside anything else turns a slow search into a stuck one.
    var target = normalizer.Normalize(q);
    var evidence = new List<LocalVenueEvidence>();
    var outcomes = new List<LocalSupplySourceOutcome>();

    foreach (var source in sources)
    {
        // No category: this screen is comparing one named product's price across venues, so there is
        // no board to narrow to and nothing here reads a listing's category.
        var search = await SearchLocalSourceAsync(source, q, zip, radius, craigslistSite, ResaleCategoryCatalog.Anything, ct);
        outcomes.Add(LocalSupplySourceOutcome.From(search));
        evidence.Add(new LocalVenueEvidence
        {
            Venue = search.SourceId, Label = search.SourceLabel,
            Status = search.Status, Error = search.Error, SearchUrl = search.SearchUrl,
            RawResultCount = search.Count,
            Prices = RelevantLocalPrices(target, search.Items, matcher),
        });
    }

    var report = analyzer.Build(q, resale, evidence, feeProfile, cost, zip, radius);
    report.Sources = outcomes;

    log.Add("Research", "Where-to-sell comparison",
        $"\"{q}\" within {radius} mi{(string.IsNullOrWhiteSpace(zip) ? "" : $" of {zip}")}; " +
        $"eBay comps: {resale.SoldCompCount}+{resale.TerapeakCompCount}; " +
        $"local matched: {string.Join(", ", evidence.Select(e => $"{e.Venue} {e.Prices.Count}/{e.RawResultCount}"))}; " +
        $"verdict: {report.Verdict}; Duration: {sw.ElapsedMilliseconds}ms");

    return report;
}

// Local asking prices for listings that are actually THIS item, per unit.
//
// A keyword search on a classifieds site returns whatever shares a word with the query, and pricing
// a venue off those is how "sell it locally for $40" gets printed under an $800 item. Every
// candidate goes through the same ComparableMatcher that guards the sold-comps set — including its
// hard exclusions for parts/broken/accessory listings — and a lot of three is divided down to one,
// exactly as MarketPriceEstimator normalizes a lot comp.
static List<decimal> RelevantLocalPrices(
    NormalizedProduct target, IEnumerable<LocalSupplyListing> items, ComparableMatcher matcher)
{
    // The same floor ComparableMatcher itself treats as broad-keyword tier. Below it a "match" is
    // a shared word, not a shared product.
    const int MinLocalMatchConfidence = 40;

    var prices = new List<decimal>();
    foreach (var item in items)
    {
        if (item.Price is not decimal price || price <= 0m || string.IsNullOrWhiteSpace(item.Title)) continue;

        var candidate = new MarketplaceComparableResult { Title = item.Title, SoldPrice = price, TotalPrice = price };
        var match = matcher.Match(target, candidate);
        if (match.Excluded || match.MatchConfidence < MinLocalMatchConfidence) continue;

        // Match() sets Quantity from the candidate's own title ("lot of 3", "2x") — a $300 pair is
        // a $150 comparable for one unit, not a $300 one.
        var quantity = Math.Max(1, candidate.Quantity);
        prices.Add(Math.Round(price / quantity, 2));
    }
    return prices;
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
                // The sweep picks its own products, so there is no category to search by — but what
                // comes back still has to be classified, because these rows are costed by the same
                // LocalArbitrageAnalyzer as the local board and a truck must not be costed as a parcel.
                var search = await SearchLocalSourceAsync(
                    source, query, zip, radius, craigslistSite, ResaleCategoryCatalog.Anything, ct);
                ResaleCategoryCatalog.ClassifyAll(search.Items);

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

// The Auction Sniper's sweep: live eBay listings priced against real sold comps, ranked by what is
// still winnable. Four phases, ordered so the expensive work is only ever spent on rows that are
// already worth spending it on:
//
//   1. TERMS   — what to hunt. Typed keywords, or (the default, and the point of the feature) the
//                seller's own completed sales, grouped by product.
//   2. PRICE   — one comp lookup per TERM, not per listing. Every result of a keyword search is
//                nominally the same product, and 25 lookups for one answer is 24 wasted.
//   3. SWEEP   — eBay's Browse API per term: auctions soonest-ending, Buy It Nows cheapest-first.
//                Every listing passes the same identity guard the rest of the app uses before a
//                cent of profit is booked against it, and every rejection is logged with its reason.
//   4. RECHECK — the handful of rows carrying real money are re-priced against their OWN title,
//                because the number the seller is about to bid should come from the item they are
//                bidding on rather than the keyword that found it.
//
// Read-only against eBay: item_summary/search and nothing else. Nothing bids, lists or spends.
static async Task<SnipeScanResult> ScanSnipesAsync(
    string? q, string? mode, string? sort, int maxTerms, int perTerm, int recheckBudget, int terapeakBudget,
    EarningsStore earnings, EbayService ebay, IMarketplaceRepository marketplace, ProductNormalizer normalizer,
    ComparableMatcher matcher, MarketPriceEstimator priceEstimator, SellThroughCalculator sellThroughCalc,
    ProfitCalculator profitCalc, FeeProfile feeProfile, OpportunityScoringService opportunityScorer,
    ConfidenceScoringService confidenceScorer, TerapeakMarketService terapeakMarket,
    AuctionSniperAnalyzer sniper, ActionLog log, CancellationToken ct)
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var nowUtc = DateTime.UtcNow;

    var scanMode = (mode ?? "").Trim().ToLowerInvariant() switch
    {
        "bins" or "fixed" or "fixed_price" => "bins",
        "both" or "all" => "both",
        _ => "auctions",
    };

    var result = new SnipeScanResult
    {
        Mode = scanMode,
        Sort = AuctionSniperAnalyzer.NormalizeSort(sort),
        PriceIsRealHours = AuctionSniperAnalyzer.PriceIsRealHours,
        TermsWereTyped = !string.IsNullOrWhiteSpace(q),
        Honesty = SnipeHonesty(),
    };

    // ── 1: what to hunt ─────────────────────────────────────────────────────────────────────────
    result.Terms = result.TermsWereTyped
        ? AuctionSniperAnalyzer.ParseTypedTerms(q, maxTerms)
        : AuctionSniperAnalyzer.WatchTermsFromSales(earnings.GetAll(), maxTerms);

    if (result.Terms.Count == 0)
    {
        // Not an error, and not an empty board either — there is nothing to look for yet, and the
        // fix is one sentence long.
        result.Status = "no_terms";
        result.DataWarning = "Type what you're hunting for, or import your eBay sales in Money Made — " +
            "this hunts the products you've already sold, because those are the ones you know you can move.";
        result.Summary.ScannedUtc = nowUtc;
        return result;
    }

    var terapeakUsed = 0;

    // The scrape budget, spent the same way the Opportunity Finder spends it: a cache hit is free
    // and never consumes it, and this function never decides on its own to pay for a scrape.
    async Task<bool> AllowScrapeAsync(string title)
    {
        var cached = await terapeakMarket.GetAsync(
            normalizer.Normalize(title), title, allowRealScrape: false, ct: ct) is not null;
        if (cached) return true;
        if (terapeakUsed >= terapeakBudget) return false;
        terapeakUsed++;
        return true;
    }

    // Resale is valued as a FIXED_PRICE listing on purpose: the seller buys at auction and relists
    // at a Buy It Now price, so the comps that should carry the most weight are the fixed-price ones.
    async Task<ResalePricing?> PriceAsync(string title, List<EbayOpportunityItem>? competition)
    {
        var analysis = await AnalyzeProductAsync(
            title, supplierUnitCost: null, quantity: 1, listingType: "FIXED_PRICE",
            activeListingsAlreadyFetched: competition, ebayForCompetitionFallback: null,
            allowRealTerapeakScrape: await AllowScrapeAsync(title),
            normalizer, marketplace, matcher, priceEstimator, sellThroughCalc, profitCalc, feeProfile,
            opportunityScorer, confidenceScorer, log, ct);

        var resale = ResalePricing.From(analysis, title);
        return resale.HasPrice ? resale : null;
    }

    // Every surviving row, with the listing and the term that found it, so the recheck below can
    // rebuild a row from a better price without re-running the search.
    var built = new List<(EbayOpportunityItem Item, SnipeWatchTerm Term, ResalePricing Resale, SnipeCandidate Row)>();
    var seenItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var term in result.Terms)
    {
        ct.ThrowIfCancellationRequested();

        var listings = new List<EbayOpportunityItem>();
        try
        {
            if (scanMode is "auctions" or "both")
                listings.AddRange(await ebay.SearchEndingSoonAsync(
                    term.Term, minFeedback: 0, limit: perTerm, listingType: "AUCTION"));

            // Cheapest first, shipping included: an underpriced Buy It Now is by definition at the
            // bottom of that order, while "newly listed" would return the most recent 50 whatever
            // they cost.
            if (scanMode is "bins" or "both")
                listings.AddRange(await ebay.SearchEndingSoonAsync(
                    term.Term, minFeedback: 0, limit: perTerm, listingType: "FIXED_PRICE",
                    sortOverride: "price"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            term.Error = ex.Message;
            log.Add("Warning", "Auction sniper search failed", $"\"{term.Term}\": {ex.Message}");
            continue;
        }

        term.ListingsFound = listings.Count;
        if (listings.Count == 0) continue;

        // ── 2: one comp lookup for the whole term ───────────────────────────────────────────────
        ResalePricing? resale;
        try
        {
            resale = await PriceAsync(term.LookupTitle, listings);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            term.Error = ex.Message;
            log.Add("Warning", "Auction sniper pricing failed", $"\"{term.LookupTitle}\": {ex.Message}");
            continue;
        }

        if (resale is null)
        {
            log.Add("Info", "Auction sniper term dropped",
                $"\"{term.LookupTitle}\" — no sold history matched, so nothing found under it can be priced.");
            continue;
        }
        term.Priced = true;

        // ── 3: the identity guard, then the money ───────────────────────────────────────────────
        var priceFloor = JackpotHunter.SupplyPriceFloor(resale);

        foreach (var item in listings)
        {
            if (string.IsNullOrWhiteSpace(item.Title) || item.Price <= 0) continue;

            // The same listing can come back under two terms, and under both formats when a seller
            // runs an auction with a Buy It Now on it. Counting it twice doubles it in every total.
            var key = string.IsNullOrWhiteSpace(item.ItemId) ? item.Url : item.ItemId;
            if (!string.IsNullOrWhiteSpace(key) && !seenItems.Add(key)) continue;

            var (plausible, reason) = AuctionSniperAnalyzer.IsPlausibleSnipe(
                item, normalizer.Normalize(item.Title), term.LookupTitle, priceFloor);
            if (!plausible)
            {
                term.ListingsRejected++;
                log.Add("Info", "Auction sniper listing rejected",
                    $"\"{item.Title}\" ({item.Price:C}) — {reason}.");
                continue;
            }

            term.Kept++;
            built.Add((item, term, resale, sniper.Build(item, resale, feeProfile, nowUtc, term)));
        }
    }

    // ── 4: re-price what money actually hangs on ────────────────────────────────────────────────
    // Ranked by profit at the ceiling rather than by discount: the deepest discount on the board is
    // routinely a $9 item, and a recheck spent there is a recheck not spent on the $200 one.
    var recheckTargets = built
        .Where(b => b.Row.Verdict is AuctionSniperAnalyzer.VerdictSnipe
                        or AuctionSniperAnalyzer.VerdictTooEarly
                        or AuctionSniperAnalyzer.VerdictThin
                        or AuctionSniperAnalyzer.VerdictWatch)
        .OrderByDescending(b => b.Row.ProfitAtMaxBid ?? 0m)
        .Take(recheckBudget)
        .ToList();

    foreach (var target in recheckTargets)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var itemResale = await PriceAsync(target.Item.Title, null);
            if (itemResale is null) continue;

            var rebuilt = sniper.Build(target.Item, itemResale, feeProfile, nowUtc, target.Term);
            rebuilt.PricedPerItem = true;

            var index = built.IndexOf(target);
            if (index >= 0) built[index] = (target.Item, target.Term, itemResale, rebuilt);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            log.Add("Warning", "Auction sniper recheck failed", $"\"{target.Item.Title}\": {ex.Message}");
        }
    }

    // ── The board ───────────────────────────────────────────────────────────────────────────────
    var rows = built.Select(b => b.Row).ToList();
    result.Candidates = AuctionSniperAnalyzer.Rank(rows, result.Sort);
    result.Summary = AuctionSniperAnalyzer.Summarize(result.Candidates, nowUtc);
    result.Summary.TermsScanned = result.Terms.Count(t => t.Priced);
    result.Summary.ListingsScanned = result.Terms.Sum(t => t.ListingsFound);
    result.Summary.ListingsRejected = result.Terms.Sum(t => t.ListingsRejected);

    if (result.Candidates.Count == 0)
    {
        // Three different failures wearing one empty board, and the seller can only fix the one
        // they're actually looking at. Naming the wrong one — "no sold history" over a search that
        // simply returned nothing live — sends them to mend a database that was never broken.
        var searched = result.Terms.Count(t => t.ListingsFound > 0);

        result.DataWarning = searched == 0
            ? "eBay has nothing live for these searches right now. Auctions come and go by the hour — scan again later."
            : result.Summary.TermsScanned == 0
                ? $"eBay returned listings, but none of the {searched} search{(searched == 1 ? "" : "es")} that found " +
                  "any could be priced — the sold-comps database has no history for them yet."
                : "Nothing live right now is priced under what these sell for. That is the normal answer most of the " +
                  "time, and it is the answer that keeps the board worth reading when it isn't.";
    }
    else if (result.Summary.SnipeCount == 0 && result.Summary.TooEarlyCount > 0)
    {
        result.DataWarning = $"Nothing is closing soon enough to bid on yet. {result.Summary.TooEarlyCount} " +
            $"auction{(result.Summary.TooEarlyCount == 1 ? " is" : "s are")} worth coming back to — their prices " +
            "aren't real until they're near the end.";
    }

    sw.Stop();
    log.Add("Info", "Auction sniper scan",
        $"Terms: {result.Terms.Count} ({(result.TermsWereTyped ? "typed" : "from your own sales")}); " +
        $"Mode: {scanMode}; Listings: {result.Summary.ListingsScanned}; " +
        $"Rejected: {result.Summary.ListingsRejected}; Rows: {result.Candidates.Count}; " +
        $"Snipes: {result.Summary.SnipeCount}; Too early: {result.Summary.TooEarlyCount}; " +
        $"Profit at ceilings: {result.Summary.ProfitAtCeilings:C}; Rechecks: {recheckTargets.Count}; " +
        $"Terapeak scrapes: {terapeakUsed}; Duration: {sw.ElapsedMilliseconds}ms");

    return result;
}

// What the numbers on the snipe board do and don't mean. Returned with every scan, including the
// empty ones — the caveats are part of the feature, not a footnote for the good days.
static List<string> SnipeHonesty() =>
[
    "Nothing here places a bid. The max bid is a number for you to type into eBay yourself — the app " +
        "never spends your money.",
    $"An auction's current price is not its closing price. Anything more than {AuctionSniperAnalyzer.PriceIsRealHours} " +
        "hours out is listed as too early to price, however cheap it looks right now.",
    "Profit is what's left at YOUR ceiling after eBay's fees, shipping both ways, packaging and the " +
        "return/testing reserves in your fee profile — not the gap between the bid and the median.",
    "The board total assumes you win every row at your ceiling. You won't. It's an upper bound on what " +
        "is on the board right now, and it falls every time somebody bids.",
];

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
            // The count that decides whether a percentage is believable, carried beside the count
            // that decides whether the lookup found anything. They are routinely very different.
            PricedOnCompCount = priceEstimate.PricedOnCompCount,
            IdentityVerified = priceEstimate.IdentityVerified,
        },
    };

    result.Confidence = confidenceScorer.Score(result, strongComparableCount, exactIdentifierMatches,
        modelNumberMatches, mostRecentComparableAgeDays, conditionConsistent: true, quantityConsistent: true, categoryConsistent: true);
    result.Score = opportunityScorer.Score(result, strongComparableCount, mostRecentComparableAgeDays);

    sw.Stop();
    log.Add("Info", "Market analysis computed",
        $"\"{titleText}\"; Local comps: {localComparables.Count} (priced on: {priceEstimate.PricedOnCompCount}, " +
        $"strong: {strongComparableCount}, exact-id: {exactIdentifierMatches}, model: {modelNumberMatches}" +
        $"{(priceEstimate.IdentityVerified ? "" : ", IDENTITY UNVERIFIED — no comp carries the model/part token")}" +
        $"); Source weighting: " +
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

// Apply the policy renames.
//
// The browser sends ids, never names: the plan is recomputed here from the policies as they stand
// right now, and a rename only happens where the freshly computed plan still agrees. That means a
// stale page cannot write a name derived from data that has since changed, and no caller can put
// an arbitrary string on a policy through this endpoint.
app.MapPost("/api/copilot/rename-policies", async (
    CopilotRenameRequest req, EbayService ebay, ActionLog log) =>
{
    var wanted = (req.PolicyIds ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet();
    if (wanted.Count == 0) return Results.BadRequest(new { error = "No policies were selected." });

    var policies = await ebay.GetBusinessPoliciesAsync();

    var groups = new (string Kind, List<PolicyInfo> List)[]
    {
        ("fulfillment_policy", policies.FulfillmentPolicies),
        ("payment_policy",     policies.PaymentPolicies),
        ("return_policy",      policies.ReturnPolicies),
    };

    var results = new List<object>();
    var renamed = 0;

    foreach (var (kind, list) in groups)
    {
        // Planned per group, because eBay's uniqueness rule is per policy type.
        foreach (var r in ListingCopilot.PlanPolicyRenames(list))
        {
            if (!wanted.Contains(r.PolicyId)) continue;

            var error = await ebay.RenamePolicyAsync(kind, r.PolicyId, r.ProposedName);
            if (error is null) renamed++;

            results.Add(new
            {
                policyId = r.PolicyId,
                kind,
                before = r.CurrentName,
                after = r.ProposedName,
                ok = error is null,
                reason = error,
            });
        }
    }

    log.Add("Info", "Copilot policy renames finished",
        $"{renamed} of {results.Count} renamed.");

    return Results.Ok(new
    {
        requested = wanted.Count,
        renamed,
        failed = results.Count - renamed,
        skipped = wanted.Count - results.Count,
        note = results.Count < wanted.Count
            ? "Some selected policies no longer needed renaming and were left alone."
            : null,
        results,
    });
});

// The AI half of the Copilot: rewrite listings for search, as DRAFTS.
//
// Three deliberate limits, because this is the one action here that spends money and touches the
// eBay account:
//   • It takes explicit listing ids. There is no "do everything" path, so a bulk rewrite can only
//     ever happen because the seller picked the listings and pressed the button.
//   • It writes Seller Hub drafts and never revises a live listing. An SEO rewrite is a judgement
//     call about someone else's inventory; the seller publishes, or doesn't.
//   • One listing failing does not abandon the rest, and every outcome is reported per listing —
//     a bulk job that half-worked and said "done" would be worse than one that failed outright.
// Rewrite the seller's listings for search - titles, subtitles, the full HTML description and the
// item specifics - as eBay DRAFTS.
//
// Started once and polled, because a whole account runs for minutes and a request that long dies in
// the browser, taking the completed work with it: the seller would pay for every rewrite and receive
// none of them. Sending no ids means every live listing.
app.MapPost("/api/copilot/improve-seo/start", (CopilotSeoRequest req, CopilotSeoJob job) =>
{
    var run = job.Start(req.ListingIds);
    return Results.Ok(new
    {
        started = true,
        alreadyRunning = run.Done > 0 && !run.Finished,
        stage = run.Stage,
        total = run.Total,
        note = "Drafts only - no live listing is revised. Publish them from eBay Seller Hub.",
    });
});

app.MapGet("/api/copilot/improve-seo/status", (CopilotSeoJob job) =>
{
    var run = job.Current;
    if (run is null) return Results.Ok(new { running = false, everRun = false });

    return Results.Ok(new
    {
        running = !run.Finished,
        everRun = true,
        stage = run.Stage,
        total = run.Total,
        done = run.Done,
        drafted = run.Drafted,
        skipped = run.Skipped,
        failed = run.Failed,
        error = run.Error,
        startedAt = run.StartedAt,
        finishedAt = run.FinishedAt,
        results = run.Results.Reverse().Take(60),
    });
});

app.MapPost("/api/copilot/improve-seo/cancel", (CopilotSeoJob job) =>
{
    job.Cancel();
    return Results.Ok(new { ok = true, note = "Stopping after the listing in progress. Drafts already made are kept." });
});

// Listing Copilot: what is wrong across the whole account, and exactly what would change.
// Read-only by design — this endpoint renames nothing and revises nothing. The seller reads the
// plan and applies it deliberately, because a bulk edit across a live store that ran on page load
// would be indistinguishable from an accident.
app.MapGet("/api/copilot/scan", async (EbayService ebay, ActionLog log) =>
{
    try
    {
        var policies = await ebay.GetBusinessPoliciesAsync();
        var listings = await ebay.GetListingsAsync();

        var shippingRenames = ListingCopilot.PlanPolicyRenames(policies.FulfillmentPolicies);
        var paymentRenames  = ListingCopilot.PlanPolicyRenames(policies.PaymentPolicies);
        var returnRenames   = ListingCopilot.PlanPolicyRenames(policies.ReturnPolicies);

        var reviewed = listings.Select(l =>
        {
            var issues = ListingCopilot.AuditTitle(l.Title)
                .Concat(ListingCopilot.AuditCategory(l.CategoryId, l.Category))
                .ToList();
            return new
            {
                l.ListingId,
                l.OfferId,
                l.Sku,
                l.Title,
                l.Category,
                l.CategoryId,
                l.Price,
                tidiedTitle = ListingCopilot.TidyTitle(l.Title),
                issues,
            };
        })
        .Where(x => x.issues.Count > 0)
        .ToList();

        // "Category not loaded" is a gap in what we fetched, not a fault in the seller's account.
        // It is counted and named separately so the categories card can say the honest thing
        // instead of reporting every listing as broken.
        var categoryUnknown = reviewed.Count(x => x.issues.Any(i => i.Code == "category_unknown"));

        return Results.Ok(new
        {
            scannedListings = listings.Count,
            policies = new
            {
                shipping = shippingRenames,
                payment  = paymentRenames,
                returns  = returnRenames,
                total    = shippingRenames.Count + paymentRenames.Count + returnRenames.Count,
            },
            listings = reviewed,
            listingsNeedingWork = reviewed.Count(x => x.issues.Any(i => i.Code != "category_unknown")),
            categoryUnknown,

            // Every listing, flagged or not, so the seller can tick individual ones to rewrite.
            // The SEO pass rewrites the description and item specifics as well as the title, and a
            // listing with a perfectly good title is exactly the one whose description most often
            // needs the work — so the picker must not be limited to what the title audit flagged.
            allListings = listings.Select(l => new
            {
                l.ListingId,
                l.Title,
                l.Price,
                needsWork = ListingCopilot.AuditTitle(l.Title).Count > 0,
            }).ToList(),
            // Said in the payload rather than left to the UI to remember: eBay's own category tree
            // belongs to eBay. A seller can move a listing between categories; they cannot rename
            // or reorder the categories themselves.
            categoryNote = "eBay's category tree cannot be renamed or reordered by a seller. "
                         + "What this checks is whether each listing sits in a category that earns it views.",
            policyError = policies.Error,
        });
    }
    catch (Exception ex)
    {
        log.Add("Warning", "Listing Copilot scan failed", ex.Message);
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

// Facebook items seen marked Sold or Pending, gathered as a by-product of searches already run.
// The response says what these prices are in the payload itself, not just in a comment, because
// the one way this feature does damage is by being read as sold comps.
app.MapGet("/api/facebook/sold", (FacebookSoldStore store, string? q, int? limit) => Results.Ok(new
{
    priceMeaning = "last asking price when seen marked sold — Facebook publishes no sale prices",
    usableAsComps = false,
    total = store.Count(),
    items = store.Recent(q, limit ?? 100),
}));

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
        var revised = await ebay.UpdateListingAsync(req);
        log.Add("Info", "eBay listing revised", string.IsNullOrWhiteSpace(req.Sku) ? req.OfferId : req.Sku);
        // Hand back what actually reached eBay. A bare ok:true was true even when the call carried
        // price and quantity and dropped every other edit on the floor.
        return new { ok = true, changed = revised.Changed, warnings = revised.Warnings, listingId = revised.ListingId };
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

    // A blank tab that was opened and closed is not work. Refused before the write, and quietly —
    // this is the normal answer for an untouched form, not an anomaly worth a line in the log.
    if (!WorkRecoveryStore.IsWorthRecovering(req.Stage, req.Label, req.Payload))
        return Results.Ok(new { saved = false, reason = "empty" });

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

// Clearing the banner in one action. Without this, a seller facing eight finished-with drafts has to
// confirm eight discards — so they do it once and leave the rest there for good.
app.MapPost("/api/work/discard-all", (WorkRecoveryStore work, ActionLog log) =>
{
    try
    {
        // Drafts only. The published rows in this table are the publish journal PublishGuard reads to
        // refuse a duplicate, and tidying the banner must not cost the seller that protection.
        var discarded = work.DiscardAll();
        if (discarded > 0) log.Add("Info", "All recovered drafts discarded", $"{discarded} draft(s) cleared.");
        return Results.Ok(new { discarded });
    }
    catch (Exception ex)
    {
        var failure = FailureTranslator.Translate(ex, FailureDomain.Storage);
        log.Add("Warning", "Could not clear recovered drafts", $"{failure.Kind} — {failure.Technical}");
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

// The full detail of one live listing, for the edit drawer.
//
// The listings grid is filled from GetMyeBaySelling, which is a SUMMARY call: it returns the title,
// price, quantity and a gallery thumbnail and nothing else. Description, condition, category, brand
// and every item specific come back empty. Opening the editor on one of those rows therefore showed
// a blank description and no specifics for a listing that has both on eBay — the seller was being
// asked to edit a listing they could not see.
//
// GetItem is the call that has those fields, and it is per-item, which is why the grid can't use it
// (87 listings would be 87 calls). One item, opened deliberately, is exactly when it is affordable.
app.MapGet("/api/ebay/listing-detail", async (string itemId, EbayService ebay, ActionLog log) =>
{
    if (string.IsNullOrWhiteSpace(itemId))
        return BadInputJson("No listing was named",
            "The editor asked eBay for a listing without saying which one.",
            "Reopen the listing from the grid.");

    return await Guarded(FailureDomain.Ebay, "Read live eBay listing", log, async () =>
        new { ok = true, data = await ebay.GetItemAsync(itemId) });
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

// ── Bind the one port ────────────────────────────────────────────────────────
// Started explicitly rather than through RunAsync(url) so a failed bind is caught here. The
// already-running check above is a snapshot, and something can still take 9332 in the moment
// between that check and this line — the answer is the same either way: say so and stop, never
// fall back to a port the eBay OAuth relay does not redirect to.
app.Urls.Clear();
app.Urls.Add(baseUrl);
try
{
    await app.StartAsync();
}
catch (Exception ex)
{
    app.Services.GetRequiredService<ActionLog>()
        .Add("Error", $"Could not start on port {port}", ex.Message);
    if (!isWindowsService)
    {
        System.Windows.Forms.MessageBox.Show(
            AppInstance.ForeignPortMessage(port),
            "ING AutoLister is not able to start",
            System.Windows.Forms.MessageBoxButtons.OK,
            System.Windows.Forms.MessageBoxIcon.Warning);
    }
    _mutex?.Dispose();
    return;
}

// ── Service mode: headless web server, lifecycle managed by Windows SCM ──────
if (isWindowsService)
{
    await app.WaitForShutdownAsync();
    return;
}

// ── Interactive mode: background web server + system tray icon ───────────────
// The port is already bound by the time this runs, so the browser can be opened straight away —
// no guess-a-delay wait for a bind that either succeeded above or ended the process.
OpenBrowser();

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
ctxMenu.Items.Add("Open Deal Radar", null, (_, _) => OpenBrowserAt("#radar"));
ctxMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
ctxMenu.Items.Add("Quit ING AutoLister", null, (_, _) =>
{
    trayIcon.Visible = false;
    System.Windows.Forms.Application.ExitThread();
});
trayIcon.ContextMenuStrip  = ctxMenu;
trayIcon.DoubleClick      += (_, _) => OpenBrowser();

// ── Deal Radar → the Windows notification tray ───────────────────────────────
// The radar runs inside this process and knows nothing about WinForms — a background service
// touching a NotifyIcon is how the headless service install turns into a 3am crash with nobody
// watching (see Services/DesktopNotifier.cs). This is the entire connection between the two.
//
// The hidden control exists only for its handle: ShowBalloonTip has to be called on the thread that
// created the tray icon, and the radar fires from a timer thread.
var uiMarshal = new System.Windows.Forms.Control();
_ = uiMarshal.Handle;

var desktopNotifier = app.Services.GetRequiredService<DesktopNotifier>();
desktopNotifier.Notified += notification =>
{
    try
    {
        uiMarshal.BeginInvoke(() => trayIcon.ShowBalloonTip(
            10000, notification.Title, notification.Message, System.Windows.Forms.ToolTipIcon.Info));
    }
    catch
    {
        // The tray is being torn down, or the handle is gone. The find is already saved and sitting
        // in the feed — a notification is the least important thing happening at this moment.
    }
};
// Only now can /api/radar/status promise a desktop notification. Without this it reports "browser"
// and the UI says the tab has to stay open, rather than promising a balloon nothing can draw.
desktopNotifier.AttachDesktopChannel();
trayIcon.BalloonTipClicked += (_, _) => OpenBrowserAt("#radar");

System.Windows.Forms.Application.Run(); // blocks until ExitThread()
desktopNotifier.DetachDesktopChannel();
uiMarshal.Dispose();
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

// The same, landing on one screen. Used by the Deal Radar balloon and its tray entry: a
// notification that opens the dashboard makes the seller hunt for the thing it just told them about.
void OpenBrowserAt(string hash) =>
    System.Diagnostics.Process.Start(
        new System.Diagnostics.ProcessStartInfo(baseUrl + hash) { UseShellExecute = true });

// EnsureLocalDns removed: no hosts-file write means nothing for antivirus/EDR to
// flag as hosts hijacking. The app is reached at http://localhost:9332.
