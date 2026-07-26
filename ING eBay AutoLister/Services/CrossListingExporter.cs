using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ING_eBay_AutoLister.Models;

namespace ING_eBay_AutoLister.Services;

// Turns one finished eBay draft into ready-to-post listings for Facebook Marketplace, Mercari and
// Amazon. The same item in three places is three times the buyer pool for work already done — but
// only if it survives the trip, which is where the real work here is:
//
//   * Titles are rewritten to each site's limit (Mercari's is 80 characters vs eBay's 80-character
//     keyword-stuffed style) and stripped of cross-site references, which every other marketplace
//     bans in listing text.
//   * eBay's structured Item Specifics have no equivalent field on Facebook or Mercari, so they get
//     folded into the description instead of being silently dropped.
//   * Prices are re-derived at NET PARITY: the price that leaves the seller the same take-home
//     after that site's fees. Copying the eBay price across is how sellers quietly lose margin on
//     Amazon (15%) — and it's also how they miss that Mercari's 0% seller fee lets them undercut
//     their own eBay price and still net the same.
//
// Nothing here contacts an external site. It produces text and CSV the seller pastes or imports.
public sealed partial class CrossListingExporter(CrossListingFeeProfile crossFees, FeeProfile ebayFees)
{
    public const string Facebook = "facebook";
    public const string Mercari  = "mercari";
    public const string Amazon   = "amazon";

    public static readonly string[] AllMarketplaces = [Facebook, Mercari, Amazon];

    // Phrases that are pointless or disallowed off eBay. Cross-site references ("eBay") are banned
    // in listing text on Facebook, Mercari and Amazon alike; the shipping/urgency noise is eBay
    // title-padding habit that just burns characters against a tighter limit elsewhere.
    [GeneratedRegex(@"\b(?:e[\s\-]?bay|buy\s+it\s+now|best\s+offer|no\s+reserve|free\s+(?:shipping|ship|s/h)|fast\s+shipping|ships\s+free|l@@k|must\s+see|wow)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex TitleNoiseRegex();

    [GeneratedRegex(@"\be[\s\-]?bay\b", RegexOptions.IgnoreCase)]
    private static partial Regex EbayMentionRegex();

    [GeneratedRegex(@"<br\s*/?>|</p>|</div>|</li>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemTagRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex AnyTagRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex RunOfSpacesRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex RunOfBlankLinesRegex();

    [GeneratedRegex(@"[^a-zA-Z0-9]+")]
    private static partial Regex NonAlphanumericRegex();

    private sealed record MarketplaceProfile(
        string Key,
        string DisplayName,
        int TitleLimit,
        int DescriptionLimit,
        decimal FeePercent,
        decimal FeeFixed,
        string FeeNote,
        string CreateUrl,
        string ImportSupport,
        string ImportNote);

    private MarketplaceProfile ProfileFor(string key) => key switch
    {
        Facebook => new MarketplaceProfile(
            Facebook, "Facebook Marketplace", 100, 5000,
            crossFees.FacebookFeePercent, crossFees.FacebookFeeFixed,
            "Estimate: 5% per shipped order (minimum $0.40 on orders of $8 or less). Local pickup sales pay no fee at all — sell locally and the whole price is yours.",
            "https://www.facebook.com/marketplace/create/item",
            "catalog_csv",
            "Facebook's Commerce Manager catalog importer reads this format directly (Catalog → Data Sources → Add Items → Data Feed)."),

        Mercari => new MarketplaceProfile(
            Mercari, "Mercari", 80, 1000,
            crossFees.MercariFeePercent, crossFees.MercariFeeFixed,
            "Estimate: no seller selling fee since Mercari's 2024 change — the service fee is charged to the buyer at checkout. Confirm against your current Mercari seller terms.",
            "https://www.mercari.com/sell/",
            "manual",
            "Mercari has no public bulk importer, so this CSV is a worksheet for your own records — use the copy buttons to fill the Mercari form."),

        Amazon => new MarketplaceProfile(
            Amazon, "Amazon", 200, 2000,
            crossFees.AmazonFeePercent, crossFees.AmazonFeeFixed,
            "Estimate: 15% referral fee (category rates run 8–15%). Individual-plan sellers also pay $0.99 per item sold.",
            "https://sellercentral.amazon.com/productsearch",
            "flat_file",
            "Column names match Amazon's Inventory Loader flat file (Seller Central → Inventory → Add Products via Upload). Paste these columns into the category template Amazon gives you."),

        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown marketplace"),
    };

    public CrossListingExport Export(CrossListRequest req)
    {
        var targets = (req.Targets ?? [])
            .Select(t => (t ?? "").Trim().ToLowerInvariant())
            .Where(t => AllMarketplaces.Contains(t))
            .Distinct()
            .ToList();
        if (targets.Count == 0) targets = [.. AllMarketplaces];

        var price = Math.Max(0m, req.Price);
        var ebayFee = Math.Round(price * (ebayFees.EbayFinalValueFeePercent / 100m) + ebayFees.EbayFinalValueFeeFixed, 2);
        if (ebayFee > price) ebayFee = price;        // a $0.50 item can't owe more in fees than it earns
        var ebayNet = Math.Round(price - ebayFee, 2);

        var sku = BuildSku(req);

        var export = new CrossListingExport
        {
            Sku = sku,
            EbayPrice = price,
            EbayFees = ebayFee,
            EbayNet = ebayNet,
        };

        if (string.IsNullOrWhiteSpace(req.Title))
            export.Warnings.Add("This draft has no title — every marketplace requires one.");
        if (price <= 0)
            export.Warnings.Add("This draft has no price, so the fee and net-parity figures below are all zero.");

        var photos = (req.ImageUrls ?? []).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        if (photos.Count == 0)
            export.Warnings.Add("This draft has no photos. Nothing sells on Facebook or Mercari without them.");
        else if (!photos.Any(IsPubliclyReachable))
            export.Warnings.Add("The photos on this draft are local app URLs, not public web addresses. Copy the image files over by hand when you post — a CSV import can't fetch them.");

        foreach (var target in targets)
            export.Listings.Add(BuildListing(ProfileFor(target), req, sku, ebayNet, photos));

        return export;
    }

    private CrossListingResult BuildListing(
        MarketplaceProfile profile, CrossListRequest req, string sku, decimal ebayNet, List<string> photos)
    {
        var result = new CrossListingResult
        {
            Marketplace = profile.Key,
            DisplayName = profile.DisplayName,
            CreateUrl = BuildCreateUrl(profile, req),
            ImportSupport = profile.ImportSupport,
            ImportNote = profile.ImportNote,
            TitleLimit = profile.TitleLimit,
            DescriptionLimit = profile.DescriptionLimit,
            FeePercent = profile.FeePercent,
            FeeFixed = profile.FeeFixed,
            FeeNote = profile.FeeNote,
            Condition = MapCondition(profile.Key, req.Condition),
        };

        // ── Title ────────────────────────────────────────────────────────────
        var cleanedTitle = CleanTitle(req.Title);
        result.Title = Truncate(cleanedTitle, profile.TitleLimit, out var titleCut);
        result.TitleTruncated = titleCut;
        if (titleCut)
            result.Warnings.Add($"Title shortened to fit {profile.DisplayName}'s {profile.TitleLimit}-character limit (yours was {cleanedTitle.Length}). Check the most important keywords survived.");

        // ── Description ──────────────────────────────────────────────────────
        var (body, removedEbayLines) = BuildDescription(req, profile);
        result.Description = Truncate(body, profile.DescriptionLimit, out var descCut);
        result.DescriptionTruncated = descCut;
        if (descCut)
            result.Warnings.Add($"Description shortened to {profile.DisplayName}'s {profile.DescriptionLimit}-character limit.");
        if (removedEbayLines > 0)
            result.Warnings.Add($"Removed {removedEbayLines} description line{(removedEbayLines == 1 ? "" : "s")} that mentioned eBay — other marketplaces don't allow pointing buyers to another site.");

        // ── Pricing ──────────────────────────────────────────────────────────
        var price = Math.Max(0m, req.Price);
        result.SamePrice = price;
        result.EstimatedFees = FeesOn(price, profile);
        result.NetAtSamePrice = Math.Round(price - result.EstimatedFees, 2);
        result.NetParityPrice = NetParityPrice(ebayNet, profile);

        // ── Fields, warnings, CSV ────────────────────────────────────────────
        result.Fields = BuildFields(profile, req, result, photos);
        result.Warnings.AddRange(MarketplaceWarnings(profile, req, photos));
        result.Csv = BuildCsv(profile, req, result, sku, photos);
        result.CsvFilename = $"{profile.Key}-{(string.IsNullOrWhiteSpace(sku) ? "listing" : sku.ToLowerInvariant())}.csv";

        return result;
    }

    // ── Pricing ───────────────────────────────────────────────────────────────

    private static decimal FeesOn(decimal price, MarketplaceProfile profile) =>
        price <= 0 ? 0m : Math.Min(price, Math.Round(price * (profile.FeePercent / 100m) + profile.FeeFixed, 2));

    // The price on this marketplace that leaves the seller the same take-home as the eBay listing:
    //   p - (p * pct + fixed) = ebayNet   =>   p = (ebayNet + fixed) / (1 - pct)
    // Rounded UP to the cent so rounding never costs the seller money. On a cheaper marketplace
    // this comes back BELOW the eBay price, which is the useful direction — it's the most the
    // seller can undercut their own eBay listing without earning less.
    private static decimal NetParityPrice(decimal ebayNet, MarketplaceProfile profile)
    {
        if (ebayNet <= 0) return 0m;
        var rate = profile.FeePercent / 100m;
        if (rate >= 1m) return ebayNet;              // degenerate config — don't emit a nonsense price
        var raw = (ebayNet + profile.FeeFixed) / (1m - rate);
        return raw <= 0 ? ebayNet : Math.Ceiling(raw * 100m) / 100m;
    }

    // ── Title ─────────────────────────────────────────────────────────────────

    private static string CleanTitle(string? raw)
    {
        var title = TitleNoiseRegex().Replace(raw ?? "", " ");
        title = title.Replace("!!!", "!").Replace("!!", "!");
        title = RunOfSpacesRegex().Replace(title.Replace('\n', ' ').Replace('\r', ' '), " ");
        // Removing a phrase can leave a dangling separator behind ("Widget - - Blue |").
        title = Regex.Replace(title, @"\s*([\-\|,/])\s*(?=[\-\|,/]|$)", "$1");
        return title.Trim().Trim('-', '|', ',', '/', ' ');
    }

    // Cuts on a word boundary rather than mid-word — a title ending in "Wirel" reads as a typo and
    // costs clicks. Falls back to a hard cut if there's no space to break on.
    private static string Truncate(string text, int limit, out bool truncated)
    {
        text = (text ?? "").Trim();
        truncated = false;
        if (limit <= 0 || text.Length <= limit) return text;

        truncated = true;
        var slice = text[..limit];
        var lastBreak = slice.LastIndexOfAny([' ', '\n', '\t']);
        if (lastBreak > limit / 2) slice = slice[..lastBreak];
        return slice.TrimEnd().TrimEnd('-', '|', ',', '/', ' ');
    }

    // ── Description ───────────────────────────────────────────────────────────

    // Returns the marketplace-safe description plus how many lines were dropped for mentioning eBay.
    private (string Body, int RemovedEbayLines) BuildDescription(CrossListRequest req, MarketplaceProfile profile)
    {
        var text = HtmlToText(req.Description);

        var kept = new List<string>();
        var removed = 0;
        foreach (var line in text.Split('\n'))
        {
            if (EbayMentionRegex().IsMatch(line))
            {
                if (!string.IsNullOrWhiteSpace(line)) removed++;
                continue;
            }
            kept.Add(line);
        }

        var sb = new StringBuilder(string.Join("\n", kept).Trim());

        // eBay's Item Specifics are structured fields with no equivalent on Facebook or Mercari.
        // Amazon has real attribute columns, so it doesn't need the prose version.
        if (profile.Key != Amazon)
        {
            var details = DetailLines(req);
            if (details.Count > 0)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append("Details:\n");
                sb.Append(string.Join("\n", details.Select(d => "• " + d)));
            }
        }

        if (!string.IsNullOrWhiteSpace(req.ConditionDescription))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("Condition: ").Append(req.ConditionDescription.Trim());
        }

        return (RunOfBlankLinesRegex().Replace(sb.ToString(), "\n\n").Trim(), removed);
    }

    private static List<string> DetailLines(CrossListRequest req)
    {
        var lines = new List<string>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add($"{label}: {value!.Trim()}");
        }

        Add("Brand", req.Brand);
        Add("Model / Part #", req.Mpn);
        Add("UPC", req.Upc);

        foreach (var (key, value) in req.ItemSpecifics ?? [])
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            if (lines.Any(l => l.StartsWith(key.Trim() + ":", StringComparison.OrdinalIgnoreCase))) continue;
            lines.Add($"{key.Trim()}: {value.Trim()}");
        }

        return lines;
    }

    // eBay descriptions are HTML. Facebook, Mercari and Amazon's description fields are all plain
    // text — pasting raw markup in shows the tags to buyers.
    public static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";

        var text = html.Replace("\r\n", "\n");
        text = ListItemTagRegex().Replace(text, "\n• ");
        text = BlockTagRegex().Replace(text, "\n");
        text = AnyTagRegex().Replace(text, "");

        text = text
            .Replace("&nbsp;", " ").Replace("&amp;", "&").Replace("&lt;", "<")
            .Replace("&gt;", ">").Replace("&quot;", "\"").Replace("&#39;", "'")
            .Replace("&apos;", "'");

        text = RunOfSpacesRegex().Replace(text, " ");
        text = string.Join("\n", text.Split('\n').Select(l => l.Trim()));
        return RunOfBlankLinesRegex().Replace(text, "\n\n").Trim();
    }

    // ── Condition ─────────────────────────────────────────────────────────────

    // eBay's condition enum has no shared vocabulary with the other three. Getting this wrong is a
    // returns/defect risk, so each mapping is deliberately conservative — where a target has fewer
    // grades than eBay, the item lands on the LOWER one rather than overselling it.
    public static string MapCondition(string marketplace, string? ebayCondition)
    {
        var c = (ebayCondition ?? "").Trim().ToUpperInvariant();

        return marketplace switch
        {
            Facebook => c switch
            {
                "NEW" => "New",
                "LIKE_NEW" or "USED_EXCELLENT" => "Used - Like New",
                "USED_VERY_GOOD" or "USED_GOOD" => "Used - Good",
                _ => "Used - Fair",
            },
            Mercari => c switch
            {
                "NEW" => "New",
                "LIKE_NEW" or "USED_EXCELLENT" => "Like new",
                "USED_VERY_GOOD" or "USED_GOOD" => "Good",
                "USED_ACCEPTABLE" => "Fair",
                "FOR_PARTS_OR_NOT_WORKING" => "Poor",
                _ => "Good",
            },
            Amazon => c switch
            {
                "NEW" => "New",
                "LIKE_NEW" => "UsedLikeNew",
                "USED_EXCELLENT" => "UsedLikeNew",
                "USED_VERY_GOOD" => "UsedVeryGood",
                "USED_GOOD" => "UsedGood",
                _ => "UsedAcceptable",
            },
            _ => ebayCondition ?? "",
        };
    }

    // Facebook's catalog feed uses its own lowercase condition tokens, not the UI labels.
    private static string FacebookCatalogCondition(string? ebayCondition) =>
        MapCondition(Facebook, ebayCondition) switch
        {
            "New" => "new",
            "Used - Like New" => "used_like_new",
            "Used - Good" => "used_good",
            _ => "used_fair",
        };

    // ── Fields / warnings ─────────────────────────────────────────────────────

    private static List<CrossListingField> BuildFields(
        MarketplaceProfile profile, CrossListRequest req, CrossListingResult result, List<string> photos)
    {
        var totalOunces = req.WeightLbs * 16m + req.WeightOz;

        var fields = new List<CrossListingField>
        {
            new() { Name = "Condition", Value = result.Condition, Required = true },
            new() { Name = "Category",  Value = string.IsNullOrWhiteSpace(req.Category) ? "" : req.Category, Required = true },
            new() { Name = "Brand",     Value = req.Brand, Required = profile.Key == Amazon },
            new() { Name = "Quantity",  Value = Math.Max(1, req.Quantity).ToString(CultureInfo.InvariantCulture) },
            new() { Name = "Photos",    Value = $"{photos.Count} attached", Required = true },
        };

        if (profile.Key == Amazon)
        {
            var (idValue, idType) = GtinFor(req);
            fields.Insert(1, new CrossListingField { Name = "Product ID (GTIN)", Value = idValue, Required = true });
            fields.Insert(2, new CrossListingField { Name = "Product ID type", Value = idType, Required = true });
            fields.Add(new CrossListingField { Name = "Manufacturer", Value = string.IsNullOrWhiteSpace(req.Brand) ? "" : req.Brand });
        }
        else
        {
            fields.Add(new CrossListingField
            {
                Name = "Shipping weight",
                Value = totalOunces > 0 ? $"{totalOunces:0.##} oz" : "",
                Required = profile.Key == Mercari,
            });
        }

        if (profile.Key == Facebook)
            fields.Add(new CrossListingField { Name = "Availability", Value = "in stock", Required = true });

        return fields;
    }

    private static IEnumerable<string> MarketplaceWarnings(
        MarketplaceProfile profile, CrossListRequest req, List<string> photos)
    {
        var warnings = new List<string>();
        var condition = (req.Condition ?? "").Trim().ToUpperInvariant();

        switch (profile.Key)
        {
            case Amazon:
                var (gtin, _) = GtinFor(req);
                if (string.IsNullOrWhiteSpace(gtin))
                    warnings.Add("Amazon needs a UPC, EAN or ISBN to create a listing (or a Brand Registry GTIN exemption). Add one before uploading — this is the single most common flat-file rejection.");
                if (string.IsNullOrWhiteSpace(req.Brand))
                    warnings.Add("Amazon requires a brand name. Use \"Generic\" if the item genuinely has no brand.");
                if (condition == "FOR_PARTS_OR_NOT_WORKING")
                    warnings.Add("Amazon has no for-parts condition and most categories forbid selling non-working items. This exported as Used-Acceptable — sell it on eBay or Facebook instead.");
                if (condition != "NEW")
                    warnings.Add("Used listings need Amazon's used-condition approval in many categories. Check your account before uploading.");
                break;

            case Facebook:
                if (photos.Count == 0)
                    warnings.Add("Facebook Marketplace requires at least one photo.");
                if (string.IsNullOrWhiteSpace(req.ItemLocationPostalCode))
                    warnings.Add("No item ZIP code on this draft. Facebook Marketplace listings are location-first — buyers filter by distance, so set your location when you post.");
                break;

            case Mercari:
                if (req.WeightLbs * 16m + req.WeightOz <= 0)
                    warnings.Add("No package weight on this draft. Mercari prices the shipping label from weight — without it you'll under-charge shipping and eat the difference.");
                if (photos.Count > 12)
                    warnings.Add("Mercari accepts 12 photos; only the first 12 will be used.");
                break;
        }

        return warnings;
    }

    // Amazon's product-id-type codes: 1 ASIN, 2 ISBN, 3 UPC, 4 EAN.
    private static (string Value, string Type) GtinFor(CrossListRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Upc))  return (req.Upc.Trim(),  "UPC");
        if (!string.IsNullOrWhiteSpace(req.Ean))  return (req.Ean.Trim(),  "EAN");
        if (!string.IsNullOrWhiteSpace(req.Isbn)) return (req.Isbn.Trim(), "ISBN");
        return ("", "");
    }

    private static string AmazonIdTypeCode(string type) => type switch
    {
        "ISBN" => "2",
        "UPC"  => "3",
        "EAN"  => "4",
        _      => "",
    };

    // ── Deep links ────────────────────────────────────────────────────────────

    private static string BuildCreateUrl(MarketplaceProfile profile, CrossListRequest req)
    {
        // Amazon wants you to match an existing catalog entry first, so send the seller straight to
        // the product search pre-filled with the strongest identifier available.
        if (profile.Key == Amazon)
        {
            var (gtin, _) = GtinFor(req);
            var query = !string.IsNullOrWhiteSpace(gtin) ? gtin : (req.Title ?? "").Trim();
            return string.IsNullOrWhiteSpace(query)
                ? profile.CreateUrl
                : $"{profile.CreateUrl}?q={Uri.EscapeDataString(query)}";
        }
        return profile.CreateUrl;
    }

    // ── SKU ───────────────────────────────────────────────────────────────────

    // Deterministic and human-readable so the same draft exported twice keeps one SKU across all
    // three marketplaces — that's what makes the seller's inventory reconcilable later.
    private static string BuildSku(CrossListRequest req)
    {
        var basis = !string.IsNullOrWhiteSpace(req.Mpn)
            ? $"{req.Brand} {req.Mpn}"
            : !string.IsNullOrWhiteSpace(req.Upc) ? req.Upc : req.Title;

        var slug = NonAlphanumericRegex().Replace(basis ?? "", "-").Trim('-').ToUpperInvariant();
        if (slug.Length > 28) slug = slug[..28].TrimEnd('-');
        return string.IsNullOrEmpty(slug) ? "ING-LISTING" : "ING-" + slug;
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    private static string BuildCsv(
        MarketplaceProfile profile, CrossListRequest req, CrossListingResult result, string sku, List<string> photos)
    {
        var price = result.NetParityPrice > 0 ? result.NetParityPrice : result.SamePrice;
        var qty = Math.Max(1, req.Quantity).ToString(CultureInfo.InvariantCulture);
        var money = price.ToString("0.00", CultureInfo.InvariantCulture);

        return profile.Key switch
        {
            Facebook => CsvDocument(
                ["id", "title", "description", "availability", "condition", "price", "link", "image_link",
                 "additional_image_link", "brand", "quantity_to_sell_on_facebook"],
                [sku, result.Title, result.Description, "in stock", FacebookCatalogCondition(req.Condition),
                 $"{money} USD", photos.FirstOrDefault() ?? "", photos.FirstOrDefault() ?? "",
                 string.Join(",", photos.Skip(1).Take(9)), req.Brand ?? "", qty]),

            // Mercari has no importer, so this is the seller's own worksheet — sku leads it so the
            // same item stays reconcilable against the Facebook and Amazon rows.
            Mercari => CsvDocument(
                ["sku", "title", "description", "category", "brand", "condition", "price",
                 "shipping_weight_oz", "quantity", "photo_urls"],
                [sku, result.Title, result.Description, req.Category ?? "", req.Brand ?? "", result.Condition,
                 money, (req.WeightLbs * 16m + req.WeightOz).ToString("0.##", CultureInfo.InvariantCulture),
                 qty, string.Join(" | ", photos.Take(12))]),

            Amazon => BuildAmazonCsv(req, result, sku, photos, money, qty),

            _ => "",
        };
    }

    private static string BuildAmazonCsv(
        CrossListRequest req, CrossListingResult result, string sku, List<string> photos, string money, string qty)
    {
        var (gtin, gtinType) = GtinFor(req);
        var bullets = AmazonBullets(req, result);
        var otherImages = photos.Skip(1).Take(3).ToList();
        while (otherImages.Count < 3) otherImages.Add("");

        var headers = new List<string>
        {
            "sku", "product-id", "product-id-type", "item-name", "brand-name", "manufacturer",
            "item-description", "bullet_point1", "bullet_point2", "bullet_point3", "bullet_point4",
            "bullet_point5", "standard-price", "quantity", "condition-type", "condition-note",
            "main-image-url", "other-image-url1", "other-image-url2", "other-image-url3",
            "item-weight", "item-weight-unit-of-measure",
        };

        var totalOunces = req.WeightLbs * 16m + req.WeightOz;
        var values = new List<string>
        {
            sku, gtin, AmazonIdTypeCode(gtinType), result.Title, req.Brand ?? "", req.Brand ?? "",
            result.Description,
            bullets.ElementAtOrDefault(0) ?? "", bullets.ElementAtOrDefault(1) ?? "",
            bullets.ElementAtOrDefault(2) ?? "", bullets.ElementAtOrDefault(3) ?? "",
            bullets.ElementAtOrDefault(4) ?? "",
            money, qty, result.Condition,
            string.IsNullOrWhiteSpace(req.ConditionDescription) ? "" : req.ConditionDescription.Trim(),
            photos.FirstOrDefault() ?? "", otherImages[0], otherImages[1], otherImages[2],
            totalOunces > 0 ? totalOunces.ToString("0.##", CultureInfo.InvariantCulture) : "",
            totalOunces > 0 ? "OZ" : "",
        };

        return CsvDocument(headers, values);
    }

    // Amazon shows bullets, not prose. Item Specifics are already the right shape for them; the
    // description's opening sentences fill any remaining slots.
    private static List<string> AmazonBullets(CrossListRequest req, CrossListingResult result)
    {
        var bullets = new List<string>();

        foreach (var (key, value) in req.ItemSpecifics ?? [])
        {
            if (bullets.Count >= 5) break;
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value)) continue;
            bullets.Add($"{key.Trim()}: {value.Trim()}");
        }

        if (bullets.Count < 5)
        {
            var sentences = result.Description
                .Split(['\n', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim(' ', '•', '-', '\t'))
                .Where(s => s.Length >= 15);

            foreach (var sentence in sentences)
            {
                if (bullets.Count >= 5) break;
                var text = sentence.Length > 500 ? sentence[..500] : sentence;
                if (!bullets.Contains(text)) bullets.Add(text);
            }
        }

        return bullets;
    }

    private static string CsvDocument(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(",", headers.Select(CsvCell))).Append("\r\n");
        sb.Append(string.Join(",", values.Select(CsvCell))).Append("\r\n");
        return sb.ToString();
    }

    // RFC 4180 quoting, plus a leading apostrophe on anything a spreadsheet would treat as a
    // formula. A listing description is attacker-influenced text (it can arrive from a scraped
    // supplier page), and these files are opened in Excel and Google Sheets by definition.
    private static string CsvCell(string? value)
    {
        var text = (value ?? "").Replace("\r\n", "\n");

        if (text.Length > 0 && "=+-@\t\r".Contains(text[0])
            && !decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
        {
            text = "'" + text;
        }

        return text.IndexOfAny([',', '"', '\n', '\r']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    private static bool IsPubliclyReachable(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !uri.IsLoopback;
}
