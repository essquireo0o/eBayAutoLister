using System.Net;
using Anthropic.SDK;
using Anthropic.SDK.Constants;
using Anthropic.SDK.Messaging;
using ING_eBay_AutoLister.Models;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ING_eBay_AutoLister.Services;

public class ClaudeService(CredentialsStore creds)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        DESCRIPTION — MUST be rich SEO-optimized HTML. Plain text is NOT acceptable.

        CRITICAL RULES — READ FIRST:
        - The Description field MUST contain real HTML tags: <div>, <h2>, <p>, <ul>, <li>, <strong>
        - NEVER return plain text — always wrap everything in proper HTML structure
        - Use only inline CSS — no <style> blocks, no class attributes
        - No JavaScript, no iframes, no external images or URLs in the HTML
        - No "guarantee", "guaranteed", "warranty", "best price", "lowest price", "click here"
        - No contact info (email, phone, WhatsApp, Telegram) — eBay suppresses listings with these
        - Max 4000 characters total — count before returning

        SEO RULES (search ranking depends on this):
        - First paragraph MUST contain: exact Brand name + Model number/name + product type + primary use case
        - Use the exact search terms buyers type: model numbers, compatibility specs with units (e.g. "128GB", "Bluetooth 5.2")
        - Repeat the main product name and top keyword 2-3x across the description naturally
        - Every bullet point MUST contain a real fact with a number or specific detail — zero vague phrases
        - h2 headings act as SEO anchors — make them keyword-rich, not generic

        PRODUCE THIS EXACT HTML STRUCTURE — replace all bracketed placeholders with real product content:

        <div style="font-family:Arial,sans-serif;max-width:760px;margin:0 auto;color:#222;font-size:15px;line-height:1.75">

          <h2 style="margin:0 0 10px;font-size:20px;font-weight:700;color:#0d5c63">[Brand] [Model] — [Product Type] | [Top Keyword or Benefit]</h2>

          <p style="margin:0 0 16px;font-size:15px">[KEYWORD-RICH opening. Brand + Model + what it is + who it is for + top 2-3 specs. Every word must be a search term. Example: "Sony WH-1000XM5 wireless noise-canceling headphones deliver industry-leading ANC with 30-hour battery life, Bluetooth 5.2 multipoint connection, and LDAC hi-res audio — built for commuters, remote workers, and audiophiles."]</p>

          <h2 style="margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63">Key Specifications</h2>
          <ul style="margin:0 0 18px 18px;padding:0;font-size:14px">
            <li style="margin-bottom:5px"><strong>[Spec label]:</strong> [exact value with unit — e.g. "Battery Life: 30 hours"]</li>
            <li style="margin-bottom:5px"><strong>[Spec label]:</strong> [exact value — e.g. "Connectivity: Bluetooth 5.2, 3.5mm jack"]</li>
            <li style="margin-bottom:5px"><strong>[Spec label]:</strong> [exact value]</li>
            <li style="margin-bottom:5px"><strong>[Spec label]:</strong> [exact value]</li>
            <li style="margin-bottom:5px"><strong>[Add every relevant spec for this product — be exhaustive]</strong></li>
          </ul>

          <h2 style="margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63">Features &amp; Benefits</h2>
          <ul style="margin:0 0 18px 18px;padding:0;font-size:14px">
            <li style="margin-bottom:5px">[Specific feature buyers care about — include model name for SEO]</li>
            <li style="margin-bottom:5px">[Another concrete benefit with a number or detail]</li>
            <li style="margin-bottom:5px">[Third feature — tie it to a use case buyers search for]</li>
            <li style="margin-bottom:5px">[Fourth feature]</li>
          </ul>

          <h2 style="margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63">Condition &amp; What's Included</h2>
          <p style="margin:0 0 8px;font-size:14px">[Honest condition description — new/used/refurbished and exact cosmetic state. Do not guess.]</p>
          <ul style="margin:0 0 18px 18px;padding:0;font-size:14px">
            <li style="margin-bottom:5px">[Exactly what ships — e.g. "1x [Model] unit"]</li>
            <li style="margin-bottom:5px">[Cables and accessories included or not]</li>
            <li style="margin-bottom:5px">[Original box/packaging if included]</li>
          </ul>

          <h2 style="margin:0 0 10px;font-size:16px;font-weight:700;text-transform:uppercase;letter-spacing:.06em;border-bottom:2px solid #0d5c63;padding-bottom:5px;color:#0d5c63">Compatibility</h2>
          <p style="margin:0 0 18px;font-size:14px">[What this works with — compatible devices, OS, software, firmware versions. Include model numbers buyers search for.]</p>

          <p style="margin:0;font-size:12px;color:#888;border-top:1px solid #eee;padding-top:10px">Ships securely packaged with tracking. See all photos for exact item condition.</p>
        </div>
        """;

    private AnthropicClient BuildClient()
    {
        var key = creds.Get().AnthropicApiKey;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("Anthropic API key is not configured. Open Settings to add it.");
        return new AnthropicClient(key);
    }

    // ── URL / web-page analysis → full listing ───────────────────────────────

    public async Task<ListingData> AnalyzeUrlAsync(string pageText, string? imageBase64, string? imageMimeType)
    {
        var client = BuildClient();

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
                - For clothing: Brand, Size, Color, Material, Department, Style
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

        var response = await client.Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model      = AnthropicModels.Claude46Sonnet,
            MaxTokens  = 4096,
            Messages   = messages
        });

        var raw = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
        return DeserializeListing(raw);
    }

    // ── Image analysis → full listing ────────────────────────────────────────

    public async Task<ListingData> AnalyzeImageAsync(string base64Image, string mimeType)
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
            - For clothing: Brand, Size, Color, Material, Department, Style
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

        var response = await BuildClient().Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 4096,
            Messages = messages
        });

        var text = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
        return DeserializeListing(text);
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

        var response = await BuildClient().Messages.GetClaudeMessageAsync(new MessageParameters
        {
            Model = AnthropicModels.Claude46Sonnet,
            MaxTokens = 4096,
            Messages = messages
        });

        var text = response.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
        var improved = DeserializeListing(text);

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
