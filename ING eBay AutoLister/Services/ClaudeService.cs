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

public class ClaudeService(CredentialsStore creds, ActionLog log)
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
        CancellationToken cancellationToken = default)
    {
        return ResilientCall.RunAsync(async () =>
        {
            var client = BuildClient();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ModelCallTimeout);

            var response = await client.Messages.GetClaudeMessageAsync(buildRequest(), timeout.Token);
            return parse(response);
        }, FailureDomain.Ai, operation, log, ResilientCall.DefaultAttempts, cancellationToken);
    }

    private static string TextOf(MessageResponse response, string fallback) =>
        response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? fallback;

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
                Model      = "claude-opus-4-8",
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
                Model    = "claude-opus-4-8",
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
                Model     = "claude-opus-4-8",
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
                Model = "claude-opus-4-8",
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
                Model     = "claude-opus-4-8",
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
    public Task<SnapIdentity> IdentifyItemAsync(string base64Image, string mimeType,
        CancellationToken cancellationToken = default)
    {
        var prompt = """
            You are a reseller standing in front of this item at a yard sale, deciding in seconds
            whether to buy it. Identify the product in the photo. Be fast and be specific.

            Return ONLY a valid JSON object — no markdown, no code fences, no commentary:

            {
              "Title": "the eBay search you would type to find what this sold for. Brand + model + the
                        one or two specs that distinguish it. Example: 'Bitmain Antminer S19j Pro 104TH'
                        or 'DeWalt DCD771 20V Cordless Drill'. NOT a marketing sentence, NOT a listing
                        title, max 12 words. If you cannot tell the brand or model, describe the item
                        as specifically as the photo allows — a searchable description beats a guess
                        at a model number.",
              "Brand": "brand name, or empty string if you cannot read one",
              "Model": "model or part number if legible, else empty string",
              "Condition": "one of exactly: NEW, LIKE_NEW, USED_EXCELLENT, USED_VERY_GOOD, USED_GOOD,
                            USED_ACCEPTABLE, FOR_PARTS_OR_NOT_WORKING — judged from visible wear only",
              "ConditionNote": "the visible wear in under 12 words, or empty string if it looks clean",
              "Certainty": "one of exactly: high, medium, low — how sure you are you named the RIGHT
                            product. 'high' only when you can read a brand and model, or the item is
                            unmistakable. If you are inferring the model from the shape, that is 'low'.",
              "CheckThis": "the ONE thing to physically check before paying that a photo cannot answer,
                            in under 12 words. Examples: 'Power it on before you pay', 'Ask if the
                            charger is included', 'Check the heels for cracks'. Empty string if there
                            is genuinely nothing."
            }

            Be honest in Certainty. A confident name for the wrong product costs the seller real money;
            'low' costs them ten seconds of reading.
            """;

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
                Model     = "claude-opus-4-8",
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
            "Identify item from photo",
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

    public async Task<ListingData> ImproveSeoAsync(ImproveSeoRequest req)
    {
        var prompt = $"""
            You are an expert eBay seller and SEO copywriter.
            Below is a draft listing. Improve it for maximum search ranking, buyer trust, and conversion.

            CURRENT DRAFT:
            Title: {req.Title}
            Subtitle: {req.Subtitle}
            Category: {req.Category}
            Condition: {req.Condition}
            Brand: {req.Brand}
            Price: {req.Price}
            Current description (may be poor quality — rewrite it completely):
            {req.Description}

            Current item specifics:
            {System.Text.Json.JsonSerializer.Serialize(req.ItemSpecifics)}

            YOUR TASKS:
            1. Rewrite the title — max 80 characters, keyword-first, no fluff, count the characters
            2. Rewrite the subtitle — max 55 characters, trust/benefit focused, empty string if not worth it
            3. Rewrite the description using the premium HTML template — this must look like a real eBay listing
            4. Fill any missing item specifics; correct any that look wrong
            5. Keep all other fields (price, condition, category, shipping) as-is unless clearly wrong

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
                Model    = "claude-opus-4-8",
                MaxTokens = 8192,
                Messages  = messages,
                Thinking  = new ThinkingParameters { Type = ThinkingType.adaptive, Effort = ThinkingEffort.high }
            },
            response => DeserializeListing(TextOf(response, "{}")),
            "AI SEO rewrite");

        // Preserve fields that the improve pass shouldn't override
        improved.Price                    = req.Price > 0 ? req.Price : improved.Price;
        improved.Quantity                 = req.Quantity > 0 ? req.Quantity : improved.Quantity;
        improved.WeightLbs                = req.WeightLbs > 0 ? req.WeightLbs : improved.WeightLbs;
        improved.WeightOz                 = req.WeightOz > 0 ? req.WeightOz : improved.WeightOz;
        improved.PackageLengthIn          = req.PackageLengthIn > 0 ? req.PackageLengthIn : improved.PackageLengthIn;
        improved.PackageWidthIn           = req.PackageWidthIn > 0 ? req.PackageWidthIn : improved.PackageWidthIn;
        improved.PackageHeightIn          = req.PackageHeightIn > 0 ? req.PackageHeightIn : improved.PackageHeightIn;
        improved.HandlingTimeBusinessDays = req.HandlingTimeBusinessDays > 0 ? req.HandlingTimeBusinessDays : improved.HandlingTimeBusinessDays;
        improved.ItemLocationPostalCode   = !string.IsNullOrWhiteSpace(req.ItemLocationPostalCode) ? req.ItemLocationPostalCode : improved.ItemLocationPostalCode;
        improved.ImageUrls                = req.ImageUrls.Count > 0 ? req.ImageUrls : improved.ImageUrls;

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
            A seller searched eBay for this and got zero results. It is probably a typo.

            SEARCH: {q}

            Reply with ONLY the corrected search terms, nothing else - no quotes, no explanation.
            If the spelling is already correct, reply with exactly: OK

            These are product searches, so brand and model names matter more than dictionary words
            (e.g. "antmnier s19" -> "antminer s19", "crytpocurrency" -> "cryptocurrency",
            "dewlat" -> "dewalt"). Keep every word the seller meant; only fix spelling. Do not add
            words, do not remove words, do not make it more specific.
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
            ct);

        if (string.IsNullOrWhiteSpace(answer)) return null;
        if (answer.Equals("OK", StringComparison.OrdinalIgnoreCase)) return null;

        // A model that answered with a sentence, or rewrote the search into something else, is not
        // a spelling correction — drop it rather than send the seller somewhere they did not ask for.
        if (answer.Length > q.Length + 25) return null;
        if (answer.Equals(q, StringComparison.OrdinalIgnoreCase)) return null;

        return answer;
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
                Model     = "claude-opus-4-8",
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
