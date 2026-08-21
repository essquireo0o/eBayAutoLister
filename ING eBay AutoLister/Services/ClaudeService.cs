using System.Net;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using ING_eBay_AutoLister.Models;
using System.Text.Json;
using System.Text.Json.Nodes;
// Alias to avoid ambiguity with System.Windows.Forms.Message (pulled in by UseWindowsForms)
using Message = Anthropic.SDK.Messaging.Message;

namespace ING_eBay_AutoLister.Services;

/// <param name="quota">
/// What every call here has to get past first. On the hosted trial the owner's own Anthropic key
/// pays for everybody, so an unmetered path to this class is an unbounded bill; the gate is a no-op
/// in the desktop build, where the seller is spending their own key. See <see cref="AiQuotaGate"/>.
/// </param>
public class ClaudeService(CredentialsStore creds, ActionLog log, AiQuotaGate quota)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Wall-clock ceiling on one model call, including reading the reply.
    /// </summary>
    /// <remarks>
    /// Generous, because writing a full listing at high thinking effort legitimately takes a while and
    /// cutting off a call that was about to succeed wastes the spend and the seller's wait. But it is
    /// a real bound: without one, a connection that stops producing bytes leaves the seller watching a
    /// spinner with no end and no explanation, which is the failure mode that reads as "the app is
    /// broken" even though nothing crashed.
    /// </remarks>
    private static readonly TimeSpan ModelCallTimeout = TimeSpan.FromMinutes(4);

    private static readonly HashSet<string> AllowedConditions = new(StringComparer.OrdinalIgnoreCase)
    {
        "NEW", "LIKE_NEW", "USED_EXCELLENT", "USED_VERY_GOOD",
        "USED_GOOD", "USED_ACCEPTABLE", "FOR_PARTS_OR_NOT_WORKING"
    };

    // Shared JSON schema comment used in both prompts
    private const string ListingSchema = """
        Return ONLY a valid JSON object — no markdown, no code fences, no extra text.

        {
          "Title": "CRITICAL: max 80 chars — lead with brand+model+type, pack in search keywords. Example: 'Sony WH-1000XM5 Wireless Noise Canceling Headphones Black — Excellent Used'",
          "Subtitle": "",
          "Category": "best matching eBay leaf category name",
          "CategoryId": "eBay numeric LEAF category ID — must be a leaf (no subcategories). CATEGORY EXAMPLES: 9355=Cell Phones & Smartphones; 175672=Laptops & Netbooks; 3676=Digital Cameras; 293=Consumer Electronics; 64800=Electronic Components; 11450=Men's Clothing; 15724=Women's Clothing; 11554=Shoes; 267=Books; 220=Toys & Hobbies; 1249=Video Games; 139971=Auto Parts; 11700=Home & Garden; 1281=Tools; 180959=Vitamins & Supplements; 179171=Cryptocurrency Mining Equipment; 42017=Computer Power Supplies; 1244=Motherboards. Choose the most specific matching leaf category for the product.",
          "SecondaryCategoryId": "",
          "Condition": "one of exactly: NEW, LIKE_NEW, USED_EXCELLENT, USED_VERY_GOOD, USED_GOOD, USED_ACCEPTABLE, FOR_PARTS_OR_NOT_WORKING",
          "ConditionDescription": "honest visible-wear notes — what a buyer would notice in person. Empty string if new.",
          "Brand": "brand name from product, empty string if unknown",
          "Mpn": "manufacturer part number if visible, else empty string",
          "Upc": "12-digit UPC if visible, else empty string",
          "Ean": "EAN-13 if visible, else empty string",
          "Isbn": "ISBN if book, else empty string",
          "Description": "SEO-optimized HTML — keyword-rich, professional eBay listing — see HTML template instructions below",
          "Price": suggested Buy It Now price as a number (current eBay sold comps for this condition and market),
          "BestOfferEnabled": true for used/collectible items where negotiation is common — false for new/fixed items,
          "AutoAcceptPrice": number or null — 90% of Price if BestOfferEnabled, else null,
          "AutoDeclinePrice": number or null — 70% of Price if BestOfferEnabled, else null,
          "Quantity": 1,
          "QuantityLimitPerBuyer": null,
          "PackageType": "one of: LETTER, LARGE_ENVELOPE_OR_FLAT_PACK, PACKAGE_THICK_ENVELOPE, MAILING_BOX, BULKY_GOODS, VERY_LARGE_PACKAGE",
          "WeightLbs": estimated package weight whole pounds,
          "WeightOz": additional ounces 0-15,
          "PackageLengthIn": estimated length inches,
          "PackageWidthIn": estimated width inches,
          "PackageHeightIn": estimated height inches,
          "HandlingTimeBusinessDays": 1,
          "ItemLocationCountry": "2-letter ISO country code where the item physically is — US, CN, HK, GB, DE, JP, CA, AU, etc. CRITICAL: set CN for China, HK for Hong Kong — eBay rejects listings with wrong item location",
          "ItemLocationPostalCode": "postal/zip code of item location if known, else empty string",
          "PrivateListing": false,
          "CharityDonationPercentage": 0,
          "CharityId": "",
          "ItemSpecifics": {
            "Brand": "1-4 word value",
            "Model": "1-4 word value",
            "IMPORTANT — keep every value to 4 words or fewer. No sentences, no comma-separated lists. Examples: 'Black', '480W', 'SHA-256', 'United States'. Include all relevant fields for the category: Brand, Model, Color, Size, Material, Type, Features, Algorithm, Hashrate, Power Consumption, Connectivity, Storage, RAM, OS, MPN, Year, Country of Manufacture, etc."
          },
          "VisualDescription": "concise visual description of the product FOR AI IMAGE GENERATION — describe what it physically looks like: color, shape, size, materials, screen/ports/buttons, form factor. Example: 'compact black rectangular device with green LCD screen, multiple port connectors on front panel, dark metal enclosure, USB Type-C charging port'. Be specific and visual — this drives photo generation.",
          "ImageType": "classify the uploaded image: 'product_photo' if it is a clean photo of just the product on a plain/simple background (suitable as an AI generation reference image), or 'webpage_screenshot' if it is a screenshot of a website, listing page, browser, or contains UI elements, text overlays, or multiple products",
          "ImageUrls": ["CRITICAL — find ALL product photo URLs visible in this image. If this is a webpage screenshot: read every image src URL you can see in the page HTML/content, look for CDN image paths, product gallery URLs, thumbnail URLs — return each as a complete https:// URL. Look carefully for URLs in the page source code or visible link text. If this is a clean product photo with no URLs visible: return an empty array. Include up to 8 product image URLs."]
        }
        """;

    private const string HtmlTemplateInstructions = """
        DESCRIPTION - MUST be rich SEO-optimized HTML. Plain text is NOT acceptable.

        CRITICAL RULES - READ FIRST:
        - The Description field MUST contain real HTML tags: <div>, <table>, <h2>, <p>, <ul>, <li>, <strong>
        - NEVER return plain text - always wrap everything in proper HTML structure
        - Use only inline CSS - no <style> blocks, no class attributes
        - No JavaScript, no iframes, no external images or URLs in the HTML
        - No "guarantee", "guaranteed", "warranty", "best price", "lowest price", "click here"
        - No contact info (email, phone, WhatsApp, Telegram) - eBay suppresses listings with these
        - Max 9000 characters total - count before returning. The structure below lands around
          6-7k with real content; 4k was the old plain-text-ish limit and cannot hold this layout,
          so it only taught the model to ignore the instruction. eBay's own cap is far higher.

        WHY TABLES, NOT FLEXBOX: most eBay traffic is the mobile app, and it strips much of the CSS
        that flex and grid layouts depend on. Tables with inline styles render identically everywhere.
        Do not replace the tables below with divs.

        SEO RULES (search ranking depends on this):
        - First paragraph MUST contain: exact Brand name + Model number/name + product type + primary use case
        - Use the exact search terms buyers type: model numbers, compatibility specs with units (e.g. "128GB", "Bluetooth 5.2")
        - Repeat the main product name and top keyword 2-3x across the description naturally
        - Every bullet and every table row MUST carry a real fact with a number or specific detail - zero vague phrases
        - h2 headings act as SEO anchors - make them keyword-rich, not generic

        PRODUCE THIS EXACT HTML STRUCTURE - replace all bracketed placeholders with real product content:

        <div style="font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Arial,sans-serif;max-width:800px;margin:0 auto;color:#1a1a1a;font-size:15px;line-height:1.7;background:#ffffff">

          <div style="background:linear-gradient(135deg,#0f766e 0%,#134e4a 100%);padding:26px 24px;border-radius:12px 12px 0 0">
            <div style="font-size:11px;letter-spacing:.18em;text-transform:uppercase;color:#7dd3d8;font-weight:700;margin-bottom:8px">[Brand]</div>
            <h2 style="margin:0;font-size:23px;line-height:1.3;font-weight:800;color:#ffffff">[Model] - [Product Type]</h2>
            <div style="margin-top:12px;font-size:14px;color:#c7f0f2">[One-line hook: the single best reason to buy this exact unit]</div>
          </div>

          <div style="padding:22px 24px;border:1px solid #e5e7eb;border-top:none;border-radius:0 0 12px 12px">

            <p style="margin:0 0 20px;font-size:15px;color:#374151">[KEYWORD-RICH opening. Brand + Model + what it is + who it is for + top 2-3 specs. Every word must be a search term. Example: "Sony WH-1000XM5 wireless noise-canceling headphones deliver industry-leading ANC with 30-hour battery life, Bluetooth 5.2 multipoint connection, and LDAC hi-res audio - built for commuters, remote workers, and audiophiles."]</p>

            <table role="presentation" style="width:100%;border-collapse:collapse;margin:0 0 22px">
              <tr><td style="padding:0 0 10px"><span style="display:inline-block;background:#ecfdf5;color:#065f46;border:1px solid #a7f3d0;border-radius:999px;padding:5px 13px;font-size:12px;font-weight:700;margin:0 6px 6px 0">[Key spec badge - e.g. "30-Hour Battery"]</span><span style="display:inline-block;background:#ecfdf5;color:#065f46;border:1px solid #a7f3d0;border-radius:999px;padding:5px 13px;font-size:12px;font-weight:700;margin:0 6px 6px 0">[Second badge]</span><span style="display:inline-block;background:#ecfdf5;color:#065f46;border:1px solid #a7f3d0;border-radius:999px;padding:5px 13px;font-size:12px;font-weight:700;margin:0 6px 6px 0">[Third badge]</span></td></tr>
            </table>

            <h2 style="margin:0 0 12px;font-size:15px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#0f766e">Key Specifications</h2>
            <table role="presentation" style="width:100%;border-collapse:collapse;margin:0 0 24px;font-size:14px">
              <tr style="background:#f9fafb"><td style="padding:10px 12px;border:1px solid #e5e7eb;font-weight:700;width:38%;color:#374151">[Spec label]</td><td style="padding:10px 12px;border:1px solid #e5e7eb;color:#1a1a1a">[exact value with unit - e.g. "30 hours"]</td></tr>
              <tr><td style="padding:10px 12px;border:1px solid #e5e7eb;font-weight:700;color:#374151">[Spec label]</td><td style="padding:10px 12px;border:1px solid #e5e7eb">[exact value - e.g. "Bluetooth 5.2, 3.5mm jack"]</td></tr>
              <tr style="background:#f9fafb"><td style="padding:10px 12px;border:1px solid #e5e7eb;font-weight:700;color:#374151">[Spec label]</td><td style="padding:10px 12px;border:1px solid #e5e7eb">[exact value]</td></tr>
              <tr><td style="padding:10px 12px;border:1px solid #e5e7eb;font-weight:700;color:#374151">[Spec label]</td><td style="padding:10px 12px;border:1px solid #e5e7eb">[exact value]</td></tr>
              <tr style="background:#f9fafb"><td style="padding:10px 12px;border:1px solid #e5e7eb;font-weight:700;color:#374151">[Add every relevant spec - be exhaustive, alternate the row shading]</td><td style="padding:10px 12px;border:1px solid #e5e7eb">[exact value]</td></tr>
            </table>

            <h2 style="margin:0 0 12px;font-size:15px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#0f766e">Features &amp; Benefits</h2>
            <ul style="margin:0 0 24px;padding:0 0 0 20px;font-size:14px;color:#374151">
              <li style="margin-bottom:9px">[Specific feature buyers care about - include the model name for SEO]</li>
              <li style="margin-bottom:9px">[Another concrete benefit with a number or detail]</li>
              <li style="margin-bottom:9px">[Third feature - tie it to a use case buyers search for]</li>
              <li style="margin-bottom:9px">[Fourth feature]</li>
            </ul>

            <h2 style="margin:0 0 12px;font-size:15px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#0f766e">Condition &amp; What's Included</h2>
            <table role="presentation" style="width:100%;border-collapse:collapse;margin:0 0 22px">
              <tr><td style="padding:16px 18px;background:#fffbeb;border-left:4px solid #f59e0b;border-radius:6px;font-size:14px;color:#78350f">[Honest condition description - new/used/refurbished and exact cosmetic state. Do not guess, do not flatter.]</td></tr>
            </table>
            <ul style="margin:0 0 24px;padding:0 0 0 20px;font-size:14px;color:#374151">
              <li style="margin-bottom:9px">[Exactly what ships - e.g. "1x [Model] unit"]</li>
              <li style="margin-bottom:9px">[Cables and accessories included or not]</li>
              <li style="margin-bottom:9px">[Original box/packaging if included]</li>
            </ul>

            <h2 style="margin:0 0 12px;font-size:15px;font-weight:800;text-transform:uppercase;letter-spacing:.08em;color:#0f766e">Compatibility</h2>
            <p style="margin:0 0 22px;font-size:14px;color:#374151">[What this works with - compatible devices, OS, software, firmware versions. Include model numbers buyers search for.]</p>

            <p style="margin:0;padding-top:16px;border-top:1px solid #e5e7eb;font-size:12px;color:#9ca3af">Ships securely packaged with tracking. See all photos for the exact item condition.</p>
          </div>
        </div>
        """;

    private AnthropicClient BuildClient()
    {
        var key = creds.Get().AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Anthropic API key is not configured. Open Settings to add it.");
        return new AnthropicClient(key);
    }

    /// <summary>
    /// A key check has to answer while the seller is still looking at the field they typed into.
    /// Four minutes is right for writing a listing and wrong for one token.
    /// </summary>
    private static readonly TimeSpan KeyCheckTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Asks Anthropic whether a key actually works, and turns the answer into something to do.
    /// </summary>
    /// <param name="candidateKey">
    /// A key to test without saving it, or null to test the one already saved.
    /// </param>
    /// <remarks>
    /// <para>
    /// The smallest real request there is: Haiku, one token of output, the word "hi". It costs a
    /// small fraction of a cent, and it is a genuine end-to-end proof — the key authenticated, the
    /// account could be billed, and a model answered. Nothing short of a real call proves that; a
    /// key that merely <em>looks</em> like <c>sk-ant-…</c> is exactly the key that fails later.
    /// </para>
    /// <para>
    /// Deliberately outside <see cref="ResilientCall"/>. Everywhere else a retry saves the seller's
    /// work from a blip; here a retry means a person watching a spinner for three attempts to be
    /// told what the first attempt already knew — and the two failures worth retrying (rate limit,
    /// overload) are the two this returns as "ask again later" anyway.
    /// </para>
    /// <para>
    /// It checks Haiku, and the listing paths run Fable and Opus. In practice a key that Anthropic
    /// authenticates and bills works for all of them, so the gap this leaves is narrow — an account
    /// with per-model restrictions would pass here and fail at the first analysis, where the
    /// failure translator explains it.
    /// </para>
    /// </remarks>
    public async Task<AiKeyCheck.Verdict> CheckKeyAsync(string? candidateKey = null, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(candidateKey) ? creds.Get().AnthropicApiKey : candidateKey.Trim();
        if (string.IsNullOrWhiteSpace(key))
            return AiKeyCheck.Describe(AiKeyCheck.Missing, DateTimeOffset.UtcNow);

        // A real request to Anthropic, so it cannot be the one unmetered road to the owner's key.
        // It does not take a generation — one Haiku token is orders of magnitude cheaper than a
        // listing, and a diagnostic that costs a tenth of somebody's day is one they stop pressing
        // — but an account with nothing left gets no calls at all. See AiQuotaGate.
        quota.EnsureNotExhausted("Claude key check");

        try
        {
            var client = new AnthropicClient(key);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(KeyCheckTimeout);

            await client.Messages.GetClaudeMessageAsync(new MessageParameters
            {
                Model     = "claude-haiku-4-5-20251001",
                MaxTokens = 1,
                Messages  = [new() { Role = RoleType.User, Content = [new TextContent { Text = "hi" }] }]
            }, timeout.Token);

            log.Add("Info", "Claude key checked", "Anthropic accepted the key and answered.");
            return AiKeyCheck.Working();
        }
        catch (Exception ex)
        {
            var verdict = AiKeyCheck.FromFailure(FailureTranslator.Translate(ex, FailureDomain.Ai));
            log.Add(verdict.Definitive ? "Error" : "Warning", "Claude key check failed", verdict.Headline);
            return verdict;
        }
    }

    /// <summary>
    /// Sends one request to the model and parses the reply, retrying transient failures.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every AI call in the app goes through here, so the retry behaviour is decided once. It used to
    /// be decided nowhere: a single "overloaded" from Anthropic — the most common failure on this path
    /// and the one that clears fastest — discarded a listing analysis the seller had waited minutes
    /// for, and told them only <c>529</c>.
    /// </para>
    /// <para>
    /// Being the one road out to Anthropic is also what makes it the place the hosted build's
    /// quota is enforced. <paramref name="metered"/> defaults to true, so an AI call added here
    /// next year costs its user a generation by virtue of having been written rather than by
    /// somebody remembering to say so. The exception is the incidental Haiku work the app starts
    /// on its own — the spelling check after an empty search — which nobody asked for and which
    /// costs a fraction of a cent: those are refused to an account with nothing left, but they do
    /// not take a generation from one that has some. See <see cref="AiQuotaGate"/>.
    /// </para>
    /// <para>
    /// The parse runs inside the retried block deliberately. A truncated or prose-wrapped reply is a
    /// transient fault of the same kind as a network blip: the request was fine, this particular
    /// sample was not, and a fresh sample almost always parses. Leaving the parse outside would mean
    /// paying for a retry-worthy failure and reporting it as fatal.
    /// </para>
    /// </remarks>
    private Task<T> CallModelAsync<T>(
        Func<MessageParameters> buildRequest,
        Func<MessageResponse, T> parse,
        string operation,
        CancellationToken cancellationToken = default,
        bool metered = true)
    {
        // Before anything else, and outside the retry block on purpose. Outside, because the
        // retries are the app's own decision to spend rather than another generation the user
        // asked for — one reservation covers all three attempts. Before, because the reservation
        // has to happen ahead of the request that costs the money, not after it has been paid for.
        if (metered) quota.Reserve(operation);
        else quota.EnsureNotExhausted(operation);

        return ResilientCall.RunAsync(async () =>
        {
            var client = BuildClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelCallTimeout);

            var response = await client.Messages.GetClaudeMessageAsync(buildRequest(), timeout.Token);

            // A safety classifier can decline a request outright. That arrives as a perfectly normal
            // HTTP 200 with stop_reason "refusal" and an empty content array — not an error, not an
            // exception. Without this check TextOf falls through to its "{}" fallback and the seller
            // gets a blank listing with no explanation, which reads as "the AI is broken".
            //
            // Deliberately not a transient failure: the classifier is deterministic, so retrying the
            // same prompt burns latency to be refused again. FailureTranslator maps anything outside
            // its transient list to no-retry, which is what we want here.
            if (string.Equals(response.StopReason, "refusal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Claude declined this request ({operation}). Try rewording the item details.");

            return parse(response);
        }, FailureDomain.Ai, operation, log, ResilientCall.DefaultAttempts, cancellationToken);
    }

    private static string TextOf(MessageResponse response, string fallback) =>
        response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? fallback;

    /// <summary>
    /// The LAST thing the model said, for replies that ran a server tool first. A web search
    /// answers in several blocks — what it looked for, what it found, then the conclusion — and
    /// <see cref="TextOf"/> would hand back the narration instead of the answer.
    /// </summary>
    private static string LastTextOf(MessageResponse response, string fallback) =>
        response.Content.OfType<TextContent>().LastOrDefault(t => !string.IsNullOrWhiteSpace(t.Text))?.Text ?? fallback;

    // ── URL / web-page analysis → full listing ───────────────────────────────

    public Task<ListingData> AnalyzeUrlAsync(string pageText, string? imageBase64, string? imageMimeType)
    {
        var contentBlocks = new List<ContentBase>();

        if (!string.IsNullOrWhiteSpace(imageBase64))
        {
            contentBlocks.Add(new ImageContent
            {
                Source = new ImageSource
                {
                    MediaType = imageMimeType ?? "image/jpeg",
                    Data      = imageBase64
                }
            });
        }

        contentBlocks.Add(new TextContent
        {
            Text = $"""
                You are an expert eBay seller and SEO specialist with 10+ years of experience.
                Analyze the product web page below and generate a complete, professional, SEO-optimized eBay listing.

                PAGE CONTENT:
                {pageText}

                {(!string.IsNullOrWhiteSpace(imageBase64) ? "An image from the page is also attached above. Use it to confirm visual details." : "")}

                TITLE RULES (critical — search ranking depends on this):
                - Max 80 characters exactly — eBay truncates longer titles
                - Lead with the most searchable terms: Brand + Model + Item Type
                - Include key attributes buyers search: compatibility, specs, accessories included
                - No ALL CAPS, no spammy punctuation (!!! or ***), no filler words like "Nice" or "Look"
                - Do NOT keyword stuff — every word must add buyer value
                - Count the characters before returning — stay under 80

                SEO SUBTITLE (optional, 55 chars max):
                - Use for a second sales hook or trust signal: "US Seller | Tested | Fast Ship"
                - Leave empty string if nothing meaningful to add

                {HtmlTemplateInstructions}

                ITEM SPECIFICS:
                - Include every applicable specific for the category — buyers filter by these
                - For electronics: Brand, Model, MPN, Color, Connectivity, Compatible Devices, Storage, RAM, OS
                - For clothing: Brand, Size, Size Type (e.g. Regular, Big & Tall, Plus, Petite — required by eBay separately from Size), Color, Material, Department, Style, Type
                - For collectibles: Brand, Year, Theme, Character, Set
                - Keep every value to 4 words or fewer — no sentences, no comma-separated lists

                PRICING:
                - Use the listed/retail price as a reference but set Price to a realistic eBay resale price
                - If the page shows a new item, set Condition to NEW and price competitively
                - Enable Best Offer for used/collectible items

                {ListingSchema}

                IMPORTANT NOTES FOR URL ANALYSIS:
                - Extract the exact product name, model number, brand from the page
                - Populate ImageUrls with all product image URLs found in the page content
                - ImageType should always be "webpage_screenshot" for URL analysis
                """
        });

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = contentBlocks }
        };

        return CallModelAsync(
            () => new MessageParameters
            {
                Model      = "claude-fable-5",
                MaxTokens  = 8192,
                Messages   = messages,
                Thinking   = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.high }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI listing from URL");
    }

    // ── Image analysis → full listing ────────────────────────────────────────

    public Task<ListingData> AnalyzeImageAsync(string base64Image, string mimeType,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            You are an expert eBay seller and SEO specialist with 10+ years of experience.
            Analyze this product image and generate a complete, professional, SEO-optimized eBay listing.

            TITLE RULES (critical — search ranking depends on this):
            - Max 80 characters exactly — eBay truncates longer titles
            - Lead with the most searchable terms: Brand + Model + Item Type
            - Include key attributes buyers search: compatibility, specs, accessories included
            - No ALL CAPS, no spammy punctuation (!!! or ***), no filler words like "Nice" or "Look"
            - Do NOT keyword stuff — every word must add buyer value
            - Count the characters before returning — stay under 80

            SEO SUBTITLE (optional, 55 chars max):
            - Use for a second sales hook or trust signal: "US Seller | Tested | Fast Ship"
            - Leave empty string if nothing meaningful to add

            {HtmlTemplateInstructions}

            ITEM SPECIFICS:
            - Include every applicable specific for the category — buyers filter by these
            - For electronics/ASIC: Brand, Model, MPN, Hashrate, Algorithm, Power, Condition, Compatible Currency
            - For clothing: Brand, Size, Size Type (e.g. Regular, Big & Tall, Plus, Petite — required by eBay separately from Size), Color, Material, Department, Style, Type
            - For collectibles: Brand, Year, Theme, Character, Set

            PRICING:
            - Research current eBay sold listings for this exact condition
            - Be realistic — overpriced listings don't sell
            - Enable Best Offer for used/collectible items

            {ListingSchema}
            """;

        var messages = new List<Message>
        {
            new()
            {
                Role = RoleType.User,
                Content =
                [
                    new ImageContent
                    {
                        Source = new ImageSource { MediaType = mimeType, Data = base64Image }
                    },
                    new TextContent { Text = prompt }
                ]
            }
        };

        return CallModelAsync(
            () => new MessageParameters
            {
                Model    = "claude-fable-5",
                MaxTokens = 8192,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.high }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI listing from photo",
            cancellationToken);
    }

    // ── Supplier file analysis → extracted products (dropship profit calculator) ────────────

    public async Task<List<SupplierProduct>> AnalyzeSupplierFileAsync(string base64Image, string mimeType)
    {
        var prompt = """
            You are an expert sourcing agent reading a supplier document. The attached image is either:
            (a) a wholesale/OEM price list or quote sheet, possibly with multiple products, models, and
                quantity-tier pricing (e.g. "1-100 units" vs "above 100"), or
            (b) a single product photo with no pricing info.

            Extract EVERY distinct product or model visible in the image. For each one:
            - ProductName: the full product/model name as shown
            - Brand: manufacturer/brand name if shown, else empty string
            - Model: model number/variant if shown, else empty string
            - PartNumber: manufacturer part number / SKU / MPN if shown (distinct from Model —
              e.g. a specific board or PSU revision code), else empty string
            - SearchQuery: a SHORT eBay-search-friendly keyword string for this exact product —
              brand + model + the one or two specs that distinguish it (e.g. "Bitaxe Gamma 601 1.2TH"),
              NOT the full marketing name. This is what will be used to search eBay sold listings.
            - WholesaleCostUsd: the unit cost in US dollars for the LOWEST quantity tier shown
              (e.g. "1-200" or "Sample 1-50", not the bulk/"above X" discount tier). If the sheet shows
              both a RMB/CNY column and a USD column, ALWAYS use the USD column. If only RMB is shown
              and no USD price exists anywhere for that row, convert using approximately 7.2 RMB = 1 USD.
              If truly no price is visible for a product, use 0.
            - Notes: 1-2 short factual specs worth showing next to the price (e.g. hashrate, power draw,
              key differentiator) — empty string if nothing notable.

            Return ONLY a valid JSON array — no markdown, no code fences, no extra text, no wrapping object:
            [
              {
                "ProductName": "",
                "Brand": "",
                "Model": "",
                "PartNumber": "",
                "SearchQuery": "",
                "WholesaleCostUsd": 0,
                "Notes": ""
              }
            ]

            Extract up to 15 distinct products. If the image contains only one product with no pricing
            table, return a single-item array with WholesaleCostUsd of 0.
            """;

        var messages = new List<Message>
        {
            new()
            {
                Role = RoleType.User,
                Content =
                [
                    new ImageContent
                    {
                        Source = new ImageSource { MediaType = mimeType, Data = base64Image }
                    },
                    new TextContent { Text = prompt }
                ]
            }
        };

        var products = await CallModelAsync(
            () => new MessageParameters
            {
                Model     = "claude-fable-5",
                MaxTokens = 4096,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.medium }
            },
            response =>
            {
                var json = ExtractJsonArray(TextOf(response, "[]"));
                return JsonSerializer.Deserialize<List<SupplierProduct>>(json, JsonOptions) ?? [];
            },
            "AI supplier-file extraction");

        foreach (var p in products)
        {
            p.ProductName      ??= "";
            p.Brand             ??= "";
            p.Model             ??= "";
            p.PartNumber        ??= "";
            p.SearchQuery       ??= "";
            p.Notes             ??= "";
            if (string.IsNullOrWhiteSpace(p.SearchQuery))
                p.SearchQuery = string.IsNullOrWhiteSpace(p.ProductName) ? $"{p.Brand} {p.Model}".Trim() : p.ProductName;
        }

        return products.Where(p => !string.IsNullOrWhiteSpace(p.ProductName) || !string.IsNullOrWhiteSpace(p.SearchQuery)).ToList();
    }

    // ── Liquidation manifest / lot description → itemised lines ─────────────

    /// <summary>
    /// Reads a liquidation manifest that <see cref="ManifestParser"/> could not read on its own —
    /// a photographed or screenshotted manifest, or a prose lot description of the kind auctions
    /// and estate sales actually publish ("pallet of assorted power tools, includes two Dewalt
    /// drills, a Milwaukee packout…").
    ///
    /// A clean CSV never reaches here: columns are read deterministically, which is both free and
    /// impossible to hallucinate a quantity into. This is the fallback, and it is told plainly not
    /// to invent lines, because an imagined $400 item is a $400 error in a buy decision.
    /// </summary>
    public async Task<List<ManifestLine>> AnalyzeManifestAsync(
        string? manifestText, string? base64Image, string? mimeType, CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            You are reading a LIQUIDATION MANIFEST for a reseller who is deciding whether to buy the
            lot. The source is {{(string.IsNullOrWhiteSpace(base64Image) ? "the text below" : "the attached image" + (string.IsNullOrWhiteSpace(manifestText) ? "" : ", plus the text below"))}}.
            It may be a pallet manifest, a wholesale lot sheet, an auction lot description, an estate
            lot listing, or a photo of a printed manifest.

            Extract EVERY distinct product line. For each one:
            - Description: the item as written, cleaned up. Keep brand, model and the specs that
              identify it. Do NOT merge different products into one line.
            - Brand: brand/manufacturer if stated or obvious from the model, else empty string
            - Model: model number / MPN / style code if stated, else empty string
            - Upc: UPC/EAN/GTIN if stated, else empty string
            - Quantity: units of THIS line in the lot. If it does not say, use 1. Read "2 pcs",
              "x3", "QTY 4" and case counts. If the line is a case/pack of N of the same item and
              the lot has M cases, Quantity is the number of SELLABLE units the buyer ends up with.
            - UnitRetail: the manifest's stated retail/MSRP PER UNIT in US dollars, or null if none
              is stated. If only an extended/total retail is shown, divide it by the quantity. Never
              invent a retail price and never use your own estimate of what it sells for — this field
              is only ever what the document itself claims.
            - Condition: the condition/grade as stated for this line (e.g. "New", "Open box",
              "Customer return", "Salvage"), else empty string
            - SearchQuery: a SHORT eBay-search keyword string for this exact product — brand + model
              + the one or two specs that distinguish it. This is what will be searched against real
              sold listings, so no marketing words, no condition words, no quantities.

            CRITICAL RULES:
            - Extract only what is actually there. If the source is vague ("assorted housewares"),
              return that as ONE line with the quantity it states — do not imagine what is in it.
            - Never add a line that is not in the source. A made-up item becomes a made-up dollar
              figure in a real purchase decision.
            - Skip total/subtotal rows, page numbers, headers, terms and shipping notes.
            - Extract up to 60 lines. If there are more, take the highest-value ones.

            Return ONLY a valid JSON array — no markdown, no code fences, no extra text:
            [
              {
                "Description": "",
                "Brand": "",
                "Model": "",
                "Upc": "",
                "Quantity": 1,
                "UnitRetail": null,
                "Condition": "",
                "SearchQuery": ""
              }
            ]
            {{(string.IsNullOrWhiteSpace(manifestText) ? "" : "\n\nMANIFEST TEXT:\n" + Truncate(manifestText, 20000))}}
            """;

        var content = new List<ContentBase>();
        if (!string.IsNullOrWhiteSpace(base64Image))
            content.Add(new ImageContent { Source = new ImageSource { MediaType = mimeType ?? "image/jpeg", Data = base64Image } });
        content.Add(new TextContent { Text = prompt });

        var messages = new List<Message> { new() { Role = RoleType.User, Content = content } };

        var lines = await CallModelAsync(
            () => new MessageParameters
            {
                Model = "claude-fable-5",
                MaxTokens = 8192,
                Messages = messages,
                Thinking = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.medium }
            },
            response =>
            {
                var json = ExtractJsonArray(TextOf(response, "[]"));
                return JsonSerializer.Deserialize<List<ManifestLine>>(json, JsonOptions) ?? [];
            },
            "AI manifest extraction",
            cancellationToken);

        foreach (var line in lines)
        {
            line.Description ??= "";
            line.Brand ??= "";
            line.Model ??= "";
            line.Upc ??= "";
            line.Condition ??= "";
            line.Quantity = Math.Max(1, line.Quantity);
            if (line.UnitRetail is <= 0m) line.UnitRetail = null;
            if (string.IsNullOrWhiteSpace(line.SearchQuery)) line.SearchQuery = ManifestParser.BuildQuery(line);
        }

        return lines.Where(l => !string.IsNullOrWhiteSpace(l.Description) || !string.IsNullOrWhiteSpace(l.SearchQuery)).ToList();
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "\n[…manifest truncated…]";

    // ── Item name only → full listing (quick-fill) ──────────────────────────

    public Task<ListingData> AnalyzeProductNameAsync(string itemName, string? imageBase64, string? imageMimeType,
        CancellationToken cancellationToken = default)
    {
        var contentBlocks = new List<ContentBase>();

        if (!string.IsNullOrWhiteSpace(imageBase64))
        {
            contentBlocks.Add(new ImageContent
            {
                Source = new ImageSource
                {
                    MediaType = imageMimeType ?? "image/jpeg",
                    Data      = imageBase64
                }
            });
        }

        contentBlocks.Add(new TextContent
        {
            Text = $"""
                You are an expert eBay seller and SEO specialist with 10+ years of experience.
                The seller only typed a product name — there is no source page to read. Use your own
                knowledge of this product to generate a complete, professional, SEO-optimized eBay listing.

                PRODUCT NAME (as typed by the seller):
                "{itemName}"

                {(!string.IsNullOrWhiteSpace(imageBase64)
                    ? "A reference photo found online for this product is also attached above — use it to confirm what the product actually looks like and to write VisualDescription."
                    : "No reference photo is available — rely on your own knowledge of this product.")}

                TITLE RULES (critical — search ranking depends on this):
                - Max 80 characters exactly — eBay truncates longer titles
                - Lead with the most searchable terms: Brand + Model + Item Type
                - Include key attributes buyers search: compatibility, specs, accessories included
                - No ALL CAPS, no spammy punctuation (!!! or ***), no filler words like "Nice" or "Look"
                - Count the characters before returning — stay under 80

                SEO SUBTITLE (optional, 55 chars max):
                - Use for a second sales hook or trust signal: "US Seller | Tested | Fast Ship"
                - Leave empty string if nothing meaningful to add

                {HtmlTemplateInstructions}

                ITEM SPECIFICS:
                - Include every applicable specific for the category — buyers filter by these
                - For electronics: Brand, Model, MPN, Color, Connectivity, Compatible Devices, Storage, RAM, OS
                - For clothing: Brand, Size, Size Type (e.g. Regular, Big & Tall, Plus, Petite — required by eBay separately from Size), Color, Material, Department, Style, Type
                - For collectibles: Brand, Year, Theme, Character, Set
                - Keep every value to 4 words or fewer — no sentences, no comma-separated lists

                PRICING:
                - Set Price to a realistic current eBay resale price for this exact product
                - Enable Best Offer for used/collectible items

                {ListingSchema}

                IMPORTANT — leave "ImageUrls" as an empty array. Photos are supplied separately by the app.
                """
        });

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = contentBlocks }
        };

        return CallModelAsync(
            () => new MessageParameters
            {
                Model     = "claude-fable-5",
                MaxTokens = 8192,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.high }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI listing from product name",
            cancellationToken);
    }

    // ── Photo → what is it, in one breath (Snap & Source) ───────────────────

    /// <summary>
    /// Names the product in a photo, and nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately not <see cref="AnalyzeImageAsync"/>. That one writes a complete SEO listing —
    /// eighty-character title, four thousand characters of HTML, item specifics, package dimensions
    /// — at high thinking effort with an eight-thousand-token ceiling and a four-minute timeout,
    /// which is right for the job it does and wrong for every part of this one. Snap &amp; Source is
    /// used standing in front of a stranger's table with somebody waiting; the seller is not going
    /// to publish anything, and the only field that matters is the search query the comp lookup runs.
    /// </para>
    /// <para>
    /// So this asks for six short fields at low effort with a small ceiling, which is where the
    /// latency actually comes from — the model is the same one every other call in this file uses,
    /// because "identify this object" is not the part worth economising on. The two extra fields it
    /// does ask for are the two a number cannot supply: how sure it is that it named the right
    /// product, and the one thing to physically check before handing over money.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The same read as <see cref="IdentifyItemAsync"/>, but for a frame of a LIVE SELLING SHOW
    /// rather than a photograph of an object — and, when the seller's computer could transcribe it,
    /// what the host was saying while that frame was on screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two sources of truth arrive with this frame that a yard-sale photo does not have, and both
    /// beat naming the object from its pixels:
    /// </para>
    /// <para>
    /// <b>The overlay.</b> A live-selling app prints the lot the seller typed — title, condition,
    /// current bid, shipping — over the video. That text is the seller's own description of the
    /// thing being auctioned. Reading it is not image recognition, it is reading, and it is right
    /// far more often than a guess from a hand holding a box at arm's length.
    /// </para>
    /// <para>
    /// <b>The voice.</b> The host says what the overlay cannot fit: the retail price, whether it is
    /// sealed, the flaw on the corner. Claude cannot hear — the Messages API takes text, images and
    /// PDFs, and no audio — so the audio is transcribed elsewhere and arrives here as text.
    /// </para>
    /// </remarks>
    public Task<SnapIdentity> IdentifyLiveLotAsync(string base64Image, string mimeType,
        string? spokenTranscript, CancellationToken cancellationToken = default)
    {
        var heard = string.IsNullOrWhiteSpace(spokenTranscript)
            ? "Nothing was transcribed for this moment — go on the screen alone."
            : $"What the host was SAYING while this frame was on screen:\n\"\"\"\n{spokenTranscript.Trim()}\n\"\"\"";

        var prompt = $$"""
            This is a frame of a LIVE SELLING SHOW, not a photograph of an object. Somebody is holding
            an item up to a camera and auctioning it right now, and a reseller is deciding in seconds
            whether to bid.

            READ THE SCREEN BEFORE YOU JUDGE THE OBJECT. These apps print the seller's own lot
            information over the video — a title like "5. APPLE IPAD #23", a condition like
            "Refurbished - Excellent", a current bid, a shipping line. That text is the seller's
            description of the exact thing being sold. When it is legible it OUTRANKS anything you
            infer from the picture: a blurry hand-held box that the overlay calls an iPad is an iPad.
            Ignore the chat messages, the giveaway banners, the viewer counts and the other lots
            listed down the side — only the lot being sold right now matters.

            {{heard}}

            The voice carries what the overlay cannot fit — "brand new in box", "retails for four
            hundred", "there's a scratch on the back". Use it for the condition and for the specs.
            Where the spoken word and the overlay disagree about WHAT the item is, trust the overlay;
            where they disagree about its CONDITION, trust the more specific of the two and say so
            in ConditionNote.

            Be fast and be specific.

            {{IdentitySchema}}
            """;

        return RunIdentifyAsync(prompt, base64Image, mimeType, "Identify live lot", cancellationToken);
    }

    public Task<SnapIdentity> IdentifyItemAsync(string base64Image, string mimeType,
        CancellationToken cancellationToken = default)
    {
        var prompt = $$"""
            You are a reseller standing in front of this item at a yard sale, deciding in seconds
            whether to buy it. Identify the product in the photo. Be fast and be specific.

            {{IdentitySchema}}
            """;

        return RunIdentifyAsync(prompt, base64Image, mimeType, "Identify item from photo", cancellationToken);
    }

    /// <summary>The JSON contract every identify prompt ends with — one shape, so callers can share a parser.</summary>
    private const string IdentitySchema = """
        Return ONLY a valid JSON object — no markdown, no code fences, no commentary:

        {
          "Title": "the eBay search you would type to find what this sold for. Brand + model + the
                    one or two specs that distinguish it. Example: 'Bitmain Antminer S19j Pro 104TH'
                    or 'DeWalt DCD771 20V Cordless Drill'. NOT a marketing sentence, NOT a listing
                    title, max 12 words. If you cannot tell the brand or model, describe the item
                    as specifically as the source allows — a searchable description beats a guess
                    at a model number.",
          "Brand": "brand name, or empty string if you cannot read one",
          "Model": "model or part number if legible, else empty string",
          "Condition": "one of exactly: NEW, LIKE_NEW, USED_EXCELLENT, USED_VERY_GOOD, USED_GOOD,
                        USED_ACCEPTABLE, FOR_PARTS_OR_NOT_WORKING",
          "ConditionNote": "the wear or the stated condition in under 12 words, or empty string if clean",
          "Certainty": "one of exactly: high, medium, low — how sure you are you named the RIGHT
                        product. 'high' only when you can read a brand and model, or the item is
                        unmistakable. If you are inferring the model from the shape, that is 'low'.",
          "CheckThis": "the ONE thing to check before paying that the source cannot answer, in under
                        12 words. Examples: 'Power it on before you pay', 'Ask if the charger is
                        included', 'Check the heels for cracks'. Empty string if genuinely nothing."
        }

        Be honest in Certainty. A confident name for the wrong product costs the seller real money;
        'low' costs them ten seconds of reading.
        """;

    private Task<SnapIdentity> RunIdentifyAsync(string prompt, string base64Image, string mimeType,
        string label, CancellationToken cancellationToken)
    {
        var messages = new List<Message>
        {
            new()
            {
                Role = RoleType.User,
                Content =
                [
                    new ImageContent { Source = new ImageSource { MediaType = mimeType, Data = base64Image } },
                    new TextContent { Text = prompt }
                ]
            }
        };

        return CallModelAsync(
            () => new MessageParameters
            {
                Model     = "claude-fable-5",
                MaxTokens = 1024,
                Messages  = messages,
                // The whole point of this call is that it comes back before the seller has to answer
                // the person waiting on them. Naming a familiar object does not need deliberation,
                // and the field it feeds is a search query.
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.low }
            },
            response => NormalizeIdentity(
                JsonSerializer.Deserialize<SnapIdentity>(
                    ExtractJsonObject(TextOf(response, "{}")), JsonOptions) ?? new SnapIdentity()),
            label,
            cancellationToken);
    }

    private static readonly HashSet<string> IdentityCertainties =
        new(StringComparer.OrdinalIgnoreCase) { "high", "medium", "low" };

    // Everything downstream reads Title as a search query and Certainty as a gate on how loudly the
    // answer is stated, so both are pinned to a known shape here rather than trusted. An empty title
    // is the caller's signal that the photo could not be read at all.
    private static SnapIdentity NormalizeIdentity(SnapIdentity identity)
    {
        identity.Title = TrimTo((identity.Title ?? "").Trim(), 120);
        identity.Brand = TrimTo((identity.Brand ?? "").Trim(), 60);
        identity.Model = TrimTo((identity.Model ?? "").Trim(), 60);
        identity.ConditionNote = TrimTo((identity.ConditionNote ?? "").Trim(), 120);
        identity.CheckThis = TrimTo((identity.CheckThis ?? "").Trim(), 120);

        var condition = (identity.Condition ?? "").Trim();
        identity.Condition = AllowedConditions.Contains(condition) ? condition.ToUpperInvariant() : "USED_GOOD";

        var certainty = (identity.Certainty ?? "").Trim();
        // Unrecognised means the model said something this code has never seen, and the safe reading
        // of an unknown confidence claim is the cautious one — it only ever adds a caveat.
        identity.Certainty = IdentityCertainties.Contains(certainty) ? certainty.ToLowerInvariant() : "low";

        return identity;
    }

    // ── SEO improvement pass ─────────────────────────────────────────────────

    /// <summary>
    /// Rewrites every part of a listing a buyer can search on, and leaves the commercial terms alone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What decides whether a listing is ever seen is text: the title, the subtitle, the description
    /// and the item specifics. The item specifics matter most and are the ones sellers leave
    /// half-empty — eBay builds its left-hand filters out of them, so a blank <em>Model</em> or
    /// <em>Compatible Brand</em> drops the listing out of every refined search that would have found
    /// it. The pass is told to FILL the missing ones, not only polish the ones already there.
    /// </para>
    /// <para>
    /// Against that pull: a wrong item specific is worse than a blank one. It brings buyers who
    /// wanted something else, and an item-not-as-described return costs the seller the item, the
    /// postage and a defect on the account. So the prompt forbids inventing anything the listing
    /// does not already support and says plainly that leaving a field out is the right answer when
    /// the listing cannot establish it.
    /// </para>
    /// <para>
    /// Price, quantity, packaging and location are put back from the request afterwards rather than
    /// trusted from the answer — they are not SEO, and silently moving a price is the worst thing a
    /// rewrite could do. The photos are not sent at all: a list the model never sees is a list it
    /// cannot rewrite, and the seller's own URLs go back on unconditionally below.
    /// </para>
    /// </remarks>
    public async Task<ListingData> ImproveSeoAsync(ImproveSeoRequest req)
    {
        var prompt = $"""
            You are an expert eBay seller and SEO copywriter.
            Below is a seller's listing. Rewrite every part of it that a buyer searches on.

            CURRENT LISTING:
            Title: {req.Title}
            Subtitle: {req.Subtitle}
            Category: {req.Category}
            Condition: {req.Condition}
            Condition description: {req.ConditionDescription}
            Brand: {req.Brand}
            MPN: {req.Mpn}
            UPC: {req.Upc}
            EAN: {req.Ean}
            ISBN: {req.Isbn}
            Price: {req.Price} (FIXED — not yours to change)
            Current description (may be poor quality — rewrite it completely):
            {req.Description}

            Current item specifics:
            {System.Text.Json.JsonSerializer.Serialize(req.ItemSpecifics)}

            REWRITE ALL OF THESE:
            1. Title — max 80 characters, keyword-first, no fluff, count the characters
            2. Subtitle — max 55 characters, trust/benefit focused, empty string if not worth it
            3. Description — the full HTML template below; this must look like a real eBay listing
            4. ItemSpecifics — return every specific above, corrected where it is wrong, PLUS every
               one that is MISSING and that the listing above establishes. This is the field that
               decides which buyer filters the listing shows up in, and it is the one most sellers
               leave half-empty, so filling it is the single most valuable thing in this pass.
               Include the ones the category expects: Brand, Model, MPN, Type, Colour, Material,
               Size, Country/Region of Manufacture, and whatever else this product's category filters
               on. Keep every value to 4 words or fewer.
            5. ConditionDescription — what a buyer would notice in person, from what the listing
               already says about its condition. Empty string if the item is new or the listing says
               nothing about wear.
            6. Brand and Mpn — fill them in if the listing above states them anywhere, including in
               the title or the old description.

            NEVER INVENT. Every word you write must be supported by the listing above:
            - no measurements, capacities, speeds, wattages or compatibility you were not given
            - no model numbers, part numbers or years you were not given
            - no "rare", "hard to find", "last one", "discontinued" or any other scarcity claim
            - no condition claim more flattering than what the listing already says
            - if a specific cannot be established from the listing, LEAVE IT OUT rather than guess.
              A missing item specific only costs a search filter. A wrong one gets the seller an
              item-not-as-described return, the item back, and a defect on their account.

            DO NOT CHANGE — echo these back exactly as they were given:
            - Price, Quantity, BestOffer settings
            - PackageType, weight, dimensions, HandlingTimeBusinessDays
            - ItemLocationPostalCode, ItemLocationCountry
            - ImageUrls — return an empty array. The seller's own photos are attached afterwards and
              are not yours to change. The schema below describes reading photo URLs out of an
              uploaded image; that task is not this one, and does not apply here.

            {HtmlTemplateInstructions}

            TITLE RULES:
            - Lead with Brand + Model + Item Type
            - Include key specs buyers filter by
            - Max 80 characters — count carefully
            - No ALL CAPS, no spammy punctuation

            {ListingSchema}
            """;

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = [ new TextContent { Text = prompt } ] }
        };

        var improved = await CallModelAsync(
            () => new MessageParameters
            {
                Model    = "claude-fable-5",
                MaxTokens = 8192,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.high }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI SEO rewrite");

        // Preserve fields that the improve pass shouldn't override. Anything the seller has already
        // decided wins; the model's value is only used where the seller has left a blank, which is
        // what makes this same call useful on a half-written draft in the editor.
        improved.Price                    = req.Price > 0 ? req.Price : improved.Price;
        improved.Quantity                 = req.Quantity > 0 ? req.Quantity : improved.Quantity;
        improved.WeightLbs                = req.WeightLbs > 0 ? req.WeightLbs : improved.WeightLbs;
        improved.WeightOz                 = req.WeightOz > 0 ? req.WeightOz : improved.WeightOz;
        improved.PackageLengthIn          = req.PackageLengthIn > 0 ? req.PackageLengthIn : improved.PackageLengthIn;
        improved.PackageWidthIn           = req.PackageWidthIn > 0 ? req.PackageWidthIn : improved.PackageWidthIn;
        improved.PackageHeightIn          = req.PackageHeightIn > 0 ? req.PackageHeightIn : improved.PackageHeightIn;
        improved.HandlingTimeBusinessDays = req.HandlingTimeBusinessDays > 0 ? req.HandlingTimeBusinessDays : improved.HandlingTimeBusinessDays;
        improved.ItemLocationPostalCode   = !string.IsNullOrWhiteSpace(req.ItemLocationPostalCode) ? req.ItemLocationPostalCode : improved.ItemLocationPostalCode;

        // The photos come from the request, always — not "unless the model returned some". A model
        // that answered with a URL it made up would otherwise replace what the buyer sees with a
        // photo of something else, and the old fallback let exactly that through whenever the
        // request happened to arrive with no photos on it.
        improved.ImageUrls                = [.. req.ImageUrls ?? []];

        return improved;
    }

    /// <summary>
    /// Corrects a misspelled search keyword, or returns null when it is already fine.
    /// </summary>
    /// <remarks>
    /// <para>
    /// eBay does not do this. Measured against the live Browse API: <c>miners crytpocurrency</c> and
    /// <c>antmnier s19</c> both return <c>total: 0</c> with no <c>autoCorrections</c> field and no
    /// suggestion of any kind — a typo just looks like "there is nothing out there". That is the
    /// worst possible answer for a sourcing tool, because it reads as a verdict on the market rather
    /// than a slip of the keyboard.
    /// </para>
    /// <para>
    /// Deliberately a small, cheap model: this is one short line of text, and it runs only after a
    /// search has already come back empty. Product spelling is exactly where a dictionary would fail
    /// and a language model does well — "antmnier" is not in any dictionary, and neither is the
    /// correct spelling.
    /// </para>
    /// </remarks>
    public async Task<string?> SuggestSearchSpellingAsync(string query, CancellationToken ct = default)
    {
        var q = (query ?? "").Trim();
        if (q.Length < 3 || q.Length > 120) return null;

        var prompt = $"""
            A reseller searched eBay for this and got nothing worth buying — no results, or only
            results too weak to price. It is probably a typo in a product name.

            SEARCH: {q}

            Reply with ONLY the corrected search terms, nothing else - no quotes, no explanation.
            If the spelling is already correct, reply with exactly: OK

            Rules, in order of priority:

            1. Prefer the SMALLEST change. The correct answer is usually one or two characters
               different from what was typed. A candidate that changes more letters is usually wrong
               even if it reads more naturally.
            2. Prefer a real brand, manufacturer or model name over an ordinary English word. These
               are product searches, and a seller typing a near-miss of a brand is far more common
               than one misspelling a common noun. "facuc" is "fanuc" (one letter, a CNC brand), not
               "facet" (two letters, a dictionary word). Likewise "antmnier s19" -> "antminer s19",
               "dewlat" -> "dewalt", "allen bradly" -> "allen bradley".
            3. Keep every word the seller meant. Do not add words, do not remove words, do not make
               the search more specific, and do not expand an abbreviation.

            If nothing plausible is within a small edit, reply exactly: OK
            """;

        var answer = await CallModelAsync(
            () => new MessageParameters
            {
                Model = "claude-haiku-4-5-20251001",
                MaxTokens = 100,
                Messages = [new() { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }]
            },
            response => TextOf(response, "").Trim(),
            "search spelling check",
            ct,
            // Not a generation. The seller did not ask for this — it fires by itself when a search
            // comes back empty — and charging a day's allowance for a typo would be the app fining
            // people for typing. Still refused once an account is out, so it is never a way round
            // the limit. See AiQuotaGate.EnsureNotExhausted.
            metered: false);

        if (string.IsNullOrWhiteSpace(answer)) return null;
        if (answer.Equals("OK", StringComparison.OrdinalIgnoreCase)) return null;

        // A model that answered with a sentence, or rewrote the search into something else, is not
        // a spelling correction — drop it rather than send the seller somewhere they did not ask for.
        if (answer.Length > q.Length + 25) return null;
        if (answer.Equals(q, StringComparison.OrdinalIgnoreCase)) return null;

        return answer;
    }

    // ── What is it worth, when nothing has sold under that name ─────────────

    /// <summary>
    /// A resale estimate for items no sold comp could price, made in ONE call for the whole board.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this exists.</b> Most of a Marketplace feed has no eBay sold history under the name
    /// the seller typed — "LOTS of keys!!", "Vintage prison Radio", a go-kart nobody listed twice.
    /// The board answered those with "No sold data", which is true and useless: the seller still
    /// has to decide whether to drive across town, and a rough number they can argue with beats no
    /// number at all (owner, 2026-08-21: "AI has to give an estimated cost — guess — it does not
    /// have to be perfect").
    /// </para>
    /// <para>
    /// <b>What it is allowed to claim.</b> The model has no live web access here, so this is
    /// knowledge of the second-hand market — eBay sold, Mercari, OfferUp, auction results, what
    /// the thing cost new and how those hold value — not a lookup. Everything it produces is
    /// labelled an AI estimate on the card and never counted as evidence: it cannot lift a row's
    /// grade, and a comp-backed price always wins over it. That is the whole safety of it.
    /// </para>
    /// <para>
    /// <b>One call for forty items.</b> Forty calls would be forty times the money and a minute of
    /// waiting; a single prompt carrying the whole board costs one generation and comes back in
    /// seconds. A model that returns fewer rows than it was given is fine — the missing ones simply
    /// stay unpriced.
    /// </para>
    /// </remarks>
    public async Task<List<AiResaleEstimate>> EstimateResaleAsync(
        IReadOnlyList<AiEstimateItem> items, CancellationToken ct = default)
    {
        var asked = (items ?? []).Where(i => !string.IsNullOrWhiteSpace(i.Title)).Take(60).ToList();
        if (asked.Count == 0) return [];

        var lines = string.Join("\n", asked.Select(i =>
            $"- id={i.ItemId} | {TrimTo(i.Title!.Trim(), 160)}"
            + (i.AskingPrice is > 0 ? $" | asking ${i.AskingPrice:0.##}" : "")
            + (string.IsNullOrWhiteSpace(i.Condition) ? "" : $" | {i.Condition}")));

        // $$ so a single brace is literal JSON and {{ }} is the interpolation — the reply format
        // below is full of braces the model must copy exactly.
        var prompt = $$"""
            You are pricing second-hand goods for a reseller. For each item below, estimate what it
            realistically SELLS for used, today, on the general resale market — eBay sold listings,
            Mercari, OfferUp, Facebook Marketplace, and auction results taken together. Not the
            asking price, not retail: what a buyer actually pays.

            ITEMS:
            {{lines}}

            Reply with ONLY a JSON array, one object per item, no prose and no code fences:
            [{"itemId":"<the id>","low":<number>,"high":<number>,"basis":"<8 words or fewer>"}]

            You may use the web search tool up to five times. Spend those searches on the items you
            cannot price confidently from memory — an unusual model number, a collectible whose
            value has moved, anything where a real sold listing would change your answer. Search
            for what the thing SOLD for, not what someone is asking. Price the rest from your own
            knowledge of the second-hand market; do not burn a search on an ordinary used tool.

            Whatever you search, your final message must be the JSON array and nothing else.

            Rules:
            - low and high are US dollars for the WHOLE item as described, already used/second-hand.
              Keep the range honest: wide when the name is vague, tight when the model is exact.
            - "basis" says what you priced it as, in a few words: "used 2019 model, common" or
              "scrap metal weight" or "collectible, condition-dependent".
            - Price what is actually described. A lot of assorted keys is scrap brass, not keys.
              A t-shirt is a t-shirt even if the band is famous. Read quantities: "6 acres" is land.
            - OMIT the item entirely — do not guess — when it is not a resellable good (real estate,
              vehicles requiring title transfer, services, adult content) or when the name is too
              vague to price at all. A missing row is a correct answer; an invented one is not.
            - Never return 0. If it is worth almost nothing, say so with a low range like 5 to 15.
            """;

        // The web, for the handful it cannot price from memory. Bounded on purpose: a board of
        // forty is mostly ordinary goods the model already knows, and an unbounded search budget
        // would turn a two-second answer into a minute of lookups and a real bill. Five searches
        // buys the hard ones — an obscure model number, a collectible whose value moved — and the
        // prompt tells it to spend them there. Legacy tool version: the newer one needs the code
        // execution tool alongside it, which this call has no use for.
        List<AiResaleEstimate> Parse(MessageResponse response)
        {
            var json = ExtractJsonArray(LastTextOf(response, "[]"));
            return JsonSerializer.Deserialize<List<AiResaleEstimate>>(json, JsonOptions) ?? [];
        }

        MessageParameters Request(bool withWeb) => new()
        {
            Model     = "claude-fable-5",
            MaxTokens = 4096,
            Messages  = [new() { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }],
            Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.low },
            Tools     = withWeb
                ? [ServerTools.GetWebSearchTool(5, null, null, null, ServerTools.WebSearchVersionLegacy)]
                : null,
        };

        List<AiResaleEstimate> estimates;
        try
        {
            estimates = await CallModelAsync(() => Request(true), Parse, "AI resale estimate", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A deployment whose key cannot use server tools must still get its numbers. The
            // seller has already been charged for the generation, so the retry is unmetered.
            log.Add("Warning", "AI resale estimate — web search unavailable",
                $"Falling back to the model's own knowledge of the resale market. {ex.Message}");
            estimates = await CallModelAsync(() => Request(false), Parse, "AI resale estimate (no web)",
                                             ct, metered: false);
        }

        // A range that arrived backwards, negative or zero is a model slip, not a price. Drop it
        // rather than put it on a card the seller might act on.
        return estimates
            .Where(e => !string.IsNullOrWhiteSpace(e.ItemId) && e.Low > 0 && e.High > 0)
            .Select(e => e with
            {
                Low   = Math.Min(e.Low, e.High),
                High  = Math.Max(e.Low, e.High),
                Basis = TrimTo((e.Basis ?? "").Trim(), 80),
            })
            .ToList();
    }

    // ── Natural language modification of current listing ────────────────────

    public async Task<ListingData> ModifyListingAsync(ModifyListingRequest req)
    {
        var currentJson = System.Text.Json.JsonSerializer.Serialize(req, new System.Text.Json.JsonSerializerOptions { WriteIndented = false });

        var prompt = $"""
            You are an expert eBay seller. The user has a listing draft and wants to modify it with a natural language instruction.

            CURRENT LISTING (JSON):
            {currentJson}

            USER INSTRUCTION:
            "{req.Instruction}"

            Apply the instruction to produce an updated listing. Rules:
            - Only change the fields the instruction targets — preserve everything else exactly as-is
            - Title must remain ≤ 80 characters
            - Subtitle must remain ≤ 55 characters (leave as empty string if not meaningful)
            - Condition must be one of: NEW, LIKE_NEW, USED_EXCELLENT, USED_VERY_GOOD, USED_GOOD, USED_ACCEPTABLE, FOR_PARTS_OR_NOT_WORKING
            - Description must remain valid HTML (preserve the HTML structure — do not convert to plain text)
            - Price must be a positive number
            - Keep ItemSpecifics values ≤ 4 words each
            - If the instruction is ambiguous, make the most reasonable interpretation

            {ListingSchema}
            """;

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = [ new TextContent { Text = prompt } ] }
        };

        var modified = await CallModelAsync(
            () => new MessageParameters
            {
                Model     = "claude-fable-5",
                MaxTokens = 8192,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.low }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI listing edit");

        // Always preserve photos — the instruction never touches those
        modified.ImageUrls = req.ImageUrls?.Count > 0 ? req.ImageUrls : modified.ImageUrls;

        return modified;
    }

    // ── Reality-check on an estimate deal ────────────────────────────────────

    /// <summary>
    /// Reads a deal the board could only estimate and says whether its price is credible for THIS
    /// exact item.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The eBay-scanner board prices rows with a mechanical comp-matcher that cannot tell a component
    /// from a whole unit. A "Bitmain Antminer S19 XP Hashboard" carries every token the matcher keys
    /// on, so it gets valued off whole-S19-XP-miner sold comps and lands at the TOP of the board on a
    /// fantasy ROI — and always as an ESTIMATE row, because so few comps actually matched. A language
    /// model reads the title the way a reseller does: "hashboard" is a part, "10-pack" is a lot, "for
    /// Antminer" is an accessory. That is the whole job here — not a new price, a sanity check on the
    /// old one.
    /// </para>
    /// <para>
    /// The image is optional on purpose. Title-only is the core, because the words usually name the
    /// kind of thing on their own; a photo, when one is handed in, only sharpens the call. Low effort
    /// and a small ceiling, like <see cref="IdentifyItemAsync"/> — this runs on the top rows after a
    /// scan and per row on demand, so it has to come back before the seller has moved on.
    /// </para>
    /// </remarks>
    public Task<DealRealityCheck> CheckDealAsync(DealCheckInput input, CancellationToken ct = default)
    {
        var contentBlocks = new List<ContentBase>();

        if (!string.IsNullOrWhiteSpace(input.ImageBase64))
        {
            contentBlocks.Add(new ImageContent
            {
                Source = new ImageSource
                {
                    MediaType = input.ImageMimeType ?? "image/jpeg",
                    Data      = input.ImageBase64
                }
            });
        }

        var estimate = input.EstimatedResale is { } resale
            ? $"${resale:0.##}"
            : "none (the scanner could not settle on one)";

        contentBlocks.Add(new TextContent
        {
            Text = $$"""
                You are a veteran eBay reseller and hardware/product analyst. An automated deal-scanner
                found a listing and priced it by matching it against recent SOLD listings — but its
                matcher is mechanical and cannot tell a COMPONENT from a whole unit, a SINGLE item from
                a multi-pack, or an ACCESSORY from the product it fits. It flagged this row as an
                ESTIMATE because very few sold comps matched, and those thin estimates are exactly where
                it goes wrong: a "Bitmain Antminer S19 XP Hashboard" gets priced off whole-S19-XP-miner
                sales and shows a fantasy profit, when a hashboard alone resells for a small fraction of
                a whole miner. A single vitamin bottle matched to a 12-pack is the same failure.

                Judge whether the automated estimate is credible for THIS EXACT item.

                LISTING TITLE: "{{input.Title}}"
                CATEGORY: {{(string.IsNullOrWhiteSpace(input.Category) ? "unspecified" : input.Category)}}
                BUY PRICE (delivered): ${{input.BuyPrice:0.##}}
                AUTOMATED RESALE ESTIMATE: {{estimate}}
                SOLD COMPS BEHIND THAT ESTIMATE: {{input.CompCount}} (few — treat the estimate as thin evidence)

                {{(!string.IsNullOrWhiteSpace(input.ImageBase64)
                    ? "A photo of the item is attached above — use it to confirm what this actually is."
                    : "No photo is attached — judge from the title and category, which usually name the kind of thing on their own.")}}

                Use your own knowledge of what this item is and what it actually resells for on eBay.
                Decide whether whole-unit sold comps plausibly describe THIS listing, and give a
                realistic resale range for THIS exact item.

                Return ONLY a valid JSON object — no markdown, no code fences, no commentary:

                {
                  "whatItIs": "short phrase naming the item and, if it is not a whole unit, saying so — e.g. 'a single S19 XP hashboard (a mining component, not a whole miner)'",
                  "itemType": "one of exactly: whole_unit, component_or_part, lot_or_bundle, accessory, consumable, other",
                  "compsMatchItem": true or false — do whole-unit sold comps plausibly match THIS listing?,
                  "realisticResaleLow": number — realistic eBay resale for THIS exact item in USD (low end),
                  "realisticResaleHigh": number — realistic eBay resale for THIS exact item in USD (high end),
                  "verdict": "one of exactly: real_deal, false_positive, uncertain",
                  "reason": "one sentence, e.g. 'The estimate used whole-miner comps; a hashboard alone resells for ~$150-300.'"
                }

                Be decisive but honest: 'false_positive' when the comps clearly describe a different
                thing than this listing, 'real_deal' when they plausibly match and the estimate looks
                sound, 'uncertain' only when you genuinely cannot tell.
                """
        });

        var messages = new List<Message>
        {
            new() { Role = RoleType.User, Content = contentBlocks }
        };

        return CallModelAsync(
            () => new MessageParameters
            {
                Model     = "claude-fable-5",
                MaxTokens = 1024,
                Messages  = messages,
                // A sanity check on a name, not a listing to write — the seller is looking at the row
                // it belongs to, so it has to answer before they scroll past.
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.low }
            },
            response => ParseDealRealityCheck(TextOf(response, "{}")),
            "AI deal reality check",
            ct);
    }

    private static readonly HashSet<string> DealItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "whole_unit", "component_or_part", "lot_or_bundle", "accessory", "consumable", "other"
    };

    private static readonly HashSet<string> DealVerdicts = new(StringComparer.OrdinalIgnoreCase)
    {
        "real_deal", "false_positive", "uncertain"
    };

    /// <summary>
    /// Turns one Claude reply into a <see cref="DealRealityCheck"/>, pinning the two fields the UI
    /// gates on — <c>itemType</c> and <c>verdict</c> — to values it recognises. Throws on anything it
    /// cannot read as a JSON object, so a garbled reply is a caught failure rather than a blank badge.
    /// </summary>
    public static DealRealityCheck ParseDealRealityCheck(string text)
    {
        var json  = ExtractJsonObject(text);
        var check = JsonSerializer.Deserialize<DealRealityCheck>(json, JsonOptions)
                    ?? throw new JsonException("AI deal-check response could not be parsed.");

        check.WhatItIs = (check.WhatItIs ?? "").Trim();
        check.Reason   = (check.Reason ?? "").Trim();

        // An unrecognised type or verdict is read the cautious way rather than trusted: "other" says
        // nothing false, and "uncertain" never flags a row the model didn't mean to reject.
        var itemType   = (check.ItemType ?? "").Trim();
        check.ItemType = DealItemTypes.Contains(itemType) ? itemType.ToLowerInvariant() : "other";

        var verdict   = (check.Verdict ?? "").Trim();
        check.Verdict = DealVerdicts.Contains(verdict) ? verdict.ToLowerInvariant() : "uncertain";

        // A resale range that is negative or inverted is a model slip, not a price — square it up so
        // the row never draws "$300–$150" or a negative floor.
        if (check.RealisticResaleLow < 0) check.RealisticResaleLow = 0;
        if (check.RealisticResaleHigh < check.RealisticResaleLow)
            check.RealisticResaleHigh = check.RealisticResaleLow;

        return check;
    }

    // ── Deserialization helpers ──────────────────────────────────────────────

    private static ListingData DeserializeListing(string text)
    {
        var json = ExtractJsonObject(text);
        var node = JsonNode.Parse(json)?.AsObject() ?? new JsonObject();
        ReplaceNullScalars(node);

        var listing = node.Deserialize<ListingData>(JsonOptions) ?? new ListingData();
        return NormalizeListing(listing);
    }

    private static string ExtractJsonObject(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
            trimmed = trimmed[(trimmed.IndexOf('\n') + 1)..];
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..trimmed.LastIndexOf("```", StringComparison.Ordinal)];

        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        if (start < 0 || end <= start)
            throw new JsonException("AI response did not contain a JSON object.");

        return trimmed[start..(end + 1)];
    }

    private static string ExtractJsonArray(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```"))
            trimmed = trimmed[(trimmed.IndexOf('\n') + 1)..];
        if (trimmed.EndsWith("```"))
            trimmed = trimmed[..trimmed.LastIndexOf("```", StringComparison.Ordinal)];

        var start = trimmed.IndexOf('[');
        var end = trimmed.LastIndexOf(']');
        if (start < 0 || end <= start)
            throw new JsonException("AI response did not contain a JSON array.");

        return trimmed[start..(end + 1)];
    }

    private static void ReplaceNullScalars(JsonObject node)
    {
        foreach (var name in new[]
        {
            "Title", "Subtitle", "Category", "CategoryId", "SecondaryCategoryId", "Condition",
            "ConditionDescription", "Brand", "Mpn", "Upc", "Ean", "Isbn", "Description",
            "PackageType", "ItemLocationPostalCode", "ItemLocationCountry", "CharityId", "VisualDescription", "ImageType"
        })
            ReplaceNull(node, name, () => JsonValue.Create(""));

        foreach (var name in new[]
        {
            "Price", "AutoAcceptPrice", "AutoDeclinePrice", "WeightLbs", "WeightOz",
            "PackageLengthIn", "PackageWidthIn", "PackageHeightIn"
        })
            ReplaceNull(node, name, () => JsonValue.Create(0));

        foreach (var name in new[] { "Quantity", "QuantityLimitPerBuyer", "HandlingTimeBusinessDays", "CharityDonationPercentage" })
            ReplaceNull(node, name, () => JsonValue.Create(0));

        foreach (var name in new[] { "BestOfferEnabled", "PrivateListing" })
            ReplaceNull(node, name, () => JsonValue.Create(false));

        ReplaceNull(node, "ImageUrls",     () => new JsonArray());
        ReplaceNull(node, "ItemSpecifics", () => new JsonObject());
    }

    private static void ReplaceNull(JsonObject node, string name, Func<JsonNode?> replacement)
    {
        var key = node.Select(kvp => kvp.Key).FirstOrDefault(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)) ?? name;
        if (!node.ContainsKey(key) || node[key] is null)
            node[key] = replacement();
    }

    private static ListingData NormalizeListing(ListingData listing)
    {
        listing.Title    = TrimTo((listing.Title ?? "").Trim(), 80);
        listing.Subtitle = "";
        listing.Category           ??= "";
        listing.CategoryId         ??= "";
        listing.SecondaryCategoryId ??= "";
        var condition = listing.Condition ?? "";
        listing.Condition            = AllowedConditions.Contains(condition) ? condition : "USED_EXCELLENT";
        listing.ConditionDescription ??= "";
        listing.Brand  ??= "";
        listing.Mpn    ??= "";
        listing.Upc    ??= "";
        listing.Ean    ??= "";
        listing.Isbn   ??= "";
        listing.Description = SanitizeDescription(listing.Description ?? "");
        listing.PackageType = string.IsNullOrWhiteSpace(listing.PackageType) ? "PACKAGE_THICK_ENVELOPE" : listing.PackageType;
        listing.ItemLocationCountry  = string.IsNullOrWhiteSpace(listing.ItemLocationCountry) ? "US" : listing.ItemLocationCountry;
        listing.ItemLocationPostalCode ??= "";
        listing.CharityId ??= "";
        listing.Quantity  = Math.Max(1, listing.Quantity);
        listing.HandlingTimeBusinessDays = Math.Max(1, listing.HandlingTimeBusinessDays);
        listing.CharityDonationPercentage = Math.Clamp(listing.CharityDonationPercentage, 0, 100);
        listing.ImageUrls     ??= [];
        listing.ItemSpecifics ??= [];

        // eBay caps item specific values at 65 chars — keep them short (≤4 words / ≤65 chars)
        listing.ItemSpecifics = listing.ItemSpecifics
            .ToDictionary(
                kvp => kvp.Key,
                kvp =>
                {
                    var v = kvp.Value ?? "";
                    // Trim to first 4 words
                    var words = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    v = words.Length > 4 ? string.Join(" ", words[..4]) : v;
                    // Hard cap at 65 chars as eBay requires
                    return v.Length > 65 ? v[..65] : v;
                });

        if (!listing.BestOfferEnabled)
        {
            listing.AutoAcceptPrice  = null;
            listing.AutoDeclinePrice = null;
        }

        return AsicMinerPresets.Apply(listing);
    }

    private static string TrimTo(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength].Trim();

    private static readonly System.Text.RegularExpressions.Regex _contactPat =
        new(@"(contact.*?(whatsapp|telegram|wechat|email|phone|call|text)|whatsapp\s*[:+]?\s*\+?\d|telegram\s*[:@]|wechat\s*[:@]|[\w.+-]+@[\w-]+\.[a-z]{2,}|\+?1?[\s.-]?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string SanitizeDescription(string desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return desc;
        // Remove contact/social info that leaks in from source pages
        desc = _contactPat.Replace(desc, "");
        // Collapse blank lines left behind
        desc = System.Text.RegularExpressions.Regex.Replace(desc, @"\n{3,}", "\n\n");
        desc = desc.Trim();

        // If the AI returned plain text instead of HTML, wrap it in the standard template
        if (!desc.Contains('<'))
        {
            var paragraphs = desc.Split(["\n\n"], StringSplitOptions.RemoveEmptyEntries);
            var sb = new System.Text.StringBuilder();
            sb.Append("<div style=\"font-family:Arial,sans-serif;max-width:760px;margin:0 auto;color:#222;font-size:15px;line-height:1.75\">");
            foreach (var para in paragraphs)
            {
                var lines = para.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 1)
                    sb.Append($"<p style=\"margin:0 0 14px\">{System.Net.WebUtility.HtmlEncode(lines[0])}</p>");
                else
                {
                    sb.Append("<ul style=\"margin:0 0 16px 18px;padding:0;font-size:14px\">");
                    foreach (var line in lines)
                        sb.Append($"<li style=\"margin-bottom:5px\">{System.Net.WebUtility.HtmlEncode(line.TrimStart('-', '*', '•', ' '))}</li>");
                    sb.Append("</ul>");
                }
            }
            sb.Append("</div>");
            desc = sb.ToString();
        }

        return desc;
    }
}
