using ING_eBay_AutoLister.Models;
using ING_eBay_AutoLister.Services;

// Pin content root to the exe's own directory so credentials/photos survive
// single-file extraction (AppContext.BaseDirectory points to the temp folder).
var exeDir = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = exeDir
});
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

var app = builder.Build();

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

// ── Trial ─────────────────────────────────────────────────────────
app.MapGet("/api/trial/status", (CredentialsStore store, LicenseService license) => Results.Ok(new
{
    daysRemaining = 9999,
    expired       = false,
    licensed      = true,
    tier          = "freeware"
}));

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

static string ExtractPageText(string url, string html)
{
    // Strip scripts, styles, nav
    var clean = System.Text.RegularExpressions.Regex.Replace(html,
        @"<(script|style|nav|footer|header)[^>]*>[\s\S]*?</\1>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // Strip all remaining tags
    var text = System.Text.RegularExpressions.Regex.Replace(clean, "<[^>]+>", " ");
    // Collapse whitespace
    text = System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
    // Truncate
    if (text.Length > 4000) text = text[..4000];

    // Extract meta tags separately
    var og = ExtractOgTags(html);
    var jsonLd = ExtractJsonLd(html);

    return $"URL: {url}\n\nMETA/OG DATA:\n{og}\n\nPRODUCT STRUCTURED DATA:\n{jsonLd}\n\nPAGE TEXT:\n{text}";
}

static string ExtractOgTags(string html)
{
    var sb = new System.Text.StringBuilder();
    var tags = new[] { "og:title","og:description","og:image","og:price:amount","og:brand",
                       "product:price:amount","twitter:title","twitter:description" };
    foreach (var tag in tags)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html,
            $@"<meta[^>]+(?:property|name)=""{System.Text.RegularExpressions.Regex.Escape(tag)}""[^>]+content=""([^""]+)""",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) sb.AppendLine($"{tag}: {m.Groups[1].Value}");
    }
    // Also grab <title>
    var titleM = System.Text.RegularExpressions.Regex.Match(html, @"<title[^>]*>([^<]+)</title>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (titleM.Success) sb.AppendLine($"title: {titleM.Groups[1].Value.Trim()}");
    return sb.ToString();
}

static string ExtractJsonLd(string html)
{
    var m = System.Text.RegularExpressions.Regex.Match(html,
        @"<script[^>]+type=""application/ld\+json""[^>]*>([\s\S]*?)</script>",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    if (!m.Success) return "";
    var raw = m.Groups[1].Value.Trim();
    return raw.Length > 1500 ? raw[..1500] : raw;
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

static string ExtractLocalUrl(string pageText)
{
    var m = System.Text.RegularExpressions.Regex.Match(pageText, @"PRIMARY_IMAGE_LOCAL_URL: (/generated-photos/[^\s]+)");
    return m.Success ? m.Groups[1].Value : "";
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

app.MapGet("/api/ebay/callback", async (string code, EbayService ebay, CredentialsStore store, HttpContext ctx) =>
{
    var token = await ebay.ExchangeCodeForTokenResultAsync(code);
    store.SaveOAuthTokensFull(token.AccessToken, token.RefreshToken, token.ExpiresIn, token.RefreshTokenExpiresIn, token.TokenType);
    ctx.Response.Redirect("/");
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
app.MapGet("/api/owner/stats", (string? k, CredentialsStore store, AnalyticsStore analytics, ActionLog log) =>
{
    var adminKey = store.EnsureAdminKey();
    if (string.IsNullOrWhiteSpace(k) || k != adminKey)
        return Results.Unauthorized();
    var snap = analytics.GetSnapshot();
    return Results.Ok(new
    {
        analytics = snap,
        recentLogs = log.Recent()
    });
});

app.MapGet("/owner", (string? k, CredentialsStore store) =>
{
    var adminKey = store.EnsureAdminKey();
    if (string.IsNullOrWhiteSpace(k) || k != adminKey)
        return Results.Content("<html><body><h2>401 Unauthorized</h2></body></html>", "text/html", statusCode: 401);

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
<h1>Owner Dashboard</h1>
<div id="status">Loading…</div>
<button class="refresh-btn" onclick="load()">Refresh</button>
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
    { val: a.totalPageLoads, lbl: 'Page Loads' },
    { val: (a.uniqueIps||[]).length, lbl: 'Unique IPs' },
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

app.Run("http://localhost:9330");

